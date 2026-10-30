using System.Net;
using System.Net.Http.Headers;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// LA SYNCHRONISATION DES DEFIS (lot P3 du CDC du panneau) : l'index du site et ses reglements, gardes sur disque. Sans
/// reseau, la borne repart de la derniere copie valide. Un 304 ne coute rien, chaque document se reprend une fois (son
/// empreinte le nomme) et rien ne part pendant une partie.
/// </summary>
public sealed class IndexDesDefisService : BackgroundService
{
    public const string HttpClientName = "nelfeplay-challenges";

    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _http;
    private readonly CatalogueDesDefis _catalogue;
    private readonly ApiContext? _contexte;
    private readonly ILogger<IndexDesDefisService>? _journal;
    private readonly string _dossier;
    private string? _etag;

    public IndexDesDefisService(IHttpClientFactory http, CatalogueDesDefis catalogue, ApiContext? contexte = null,
        ILogger<IndexDesDefisService>? journal = null, string? dossier = null)
    {
        _http = http;
        _catalogue = catalogue;
        _contexte = contexte;
        _journal = journal;
        _dossier = dossier ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "defis");
    }

    private string CheminDeLIndex => Path.Combine(_dossier, "index.json");
    private string CheminDeLEtag => Path.Combine(_dossier, "index.etag");
    private string CheminDuDocument(string empreinte) => Path.Combine(_dossier, empreinte + ".json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChargerDuDisque();
        try
        {
            await Task.Delay(PremierDelai, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SynchroniserAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _journal?.LogWarning(ex, "Defis : synchronisation impossible.");
                }

                await Task.Delay(Periode, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>La derniere copie valide, relue et reverifiee : un fichier abime ecarte son defi, pas les autres.</summary>
    internal void ChargerDuDisque()
    {
        try
        {
            if (!File.Exists(CheminDeLIndex)) return;
            var (lecture, erreur) = IndexDesDefis.Lire(File.ReadAllText(CheminDeLIndex));
            if (lecture is null)
            {
                _journal?.LogWarning("Defis : copie locale de l'index ecartee ({Erreur}).", erreur);
                return;
            }

            var reglements = new List<Reglement>();
            foreach (var defi in lecture.Defis)
            {
                var chemin = CheminDuDocument(defi.DocumentSha256);
                if (!File.Exists(chemin)) continue;
                if (IndexDesDefis.Controler(defi, File.ReadAllBytes(chemin)).Reglement is { } r) reglements.Add(r);
            }

            _catalogue.Remplacer(reglements);
            _etag = File.Exists(CheminDeLEtag) ? File.ReadAllText(CheminDeLEtag).Trim() : null;
            _journal?.LogInformation("Defis : {Nombre} defi(s) repris de la copie locale.", reglements.Count);
        }
        catch (IOException ex)
        {
            _journal?.LogWarning(ex, "Defis : copie locale illisible.");
        }
    }

    internal async Task SynchroniserAsync(CancellationToken ct)
    {
        if (_contexte?.Ui.Running is not null) return;

        using var client = _http.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));

        using var requete = new HttpRequestMessage(HttpMethod.Get, "/api/v1/challenges/open");
        requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(_etag)) requete.Headers.TryAddWithoutValidation("If-None-Match", _etag);

        using var reponse = await client.SendAsync(requete, ct).ConfigureAwait(false);
        if (reponse.StatusCode == HttpStatusCode.NotModified) return;
        if (!reponse.IsSuccessStatusCode)
        {
            _journal?.LogInformation("Defis : index indisponible (HTTP {Code}), le catalogue ne change pas.", (int) reponse.StatusCode);
            return;
        }

        var corps = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (lecture, erreur) = IndexDesDefis.Lire(corps);
        if (lecture is null)
        {
            _journal?.LogWarning("Defis : index ecarte ({Erreur}), le catalogue ne change pas.", erreur);
            return;
        }

        foreach (var e in lecture.Ecartes) _journal?.LogWarning("Defis : {Ecart}.", e);

        Directory.CreateDirectory(_dossier);
        var reglements = new List<Reglement>();
        var gardes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var defi in lecture.Defis)
        {
            var octets = await OctetsDuDocumentAsync(client, defi, ct).ConfigureAwait(false);
            // Un document injoignable : tout reprendre au prochain passage plutot que de perdre un defi ouvert.
            if (octets is null) return;

            var (reglement, ecart) = IndexDesDefis.Controler(defi, octets);
            if (reglement is null)
            {
                _journal?.LogWarning("Defis : {Defi} v{Version} ecarte ({Ecart}).", defi.ChallengeId, defi.Version, ecart);
                continue;
            }

            await EcrireAsync(CheminDuDocument(defi.DocumentSha256), octets, ct).ConfigureAwait(false);
            gardes.Add(defi.DocumentSha256);
            reglements.Add(reglement);
        }

        await EcrireAsync(CheminDeLIndex, System.Text.Encoding.UTF8.GetBytes(corps), ct).ConfigureAwait(false);
        _etag = reponse.Headers.ETag?.ToString();
        await EcrireAsync(CheminDeLEtag, System.Text.Encoding.UTF8.GetBytes(_etag ?? ""), ct).ConfigureAwait(false);
        foreach (var ancien in Directory.GetFiles(_dossier, "*.json"))
        {
            var nom = Path.GetFileNameWithoutExtension(ancien);
            if (nom.Length == 64 && !gardes.Contains(nom)) File.Delete(ancien);
        }

        _catalogue.Remplacer(reglements);
        _journal?.LogInformation("Defis : {Nombre} defi(s) ouvert(s), revision {Revision}.", reglements.Count, lecture.Revision);
    }

    /// <summary>Le document deja garde s'il a la bonne empreinte, sinon celui du site ; null si le site ne le donne pas.</summary>
    private async Task<byte[]?> OctetsDuDocumentAsync(HttpClient client, DefiDeLIndex defi, CancellationToken ct)
    {
        var chemin = CheminDuDocument(defi.DocumentSha256);
        if (File.Exists(chemin))
        {
            var garde = await File.ReadAllBytesAsync(chemin, ct).ConfigureAwait(false);
            if (string.Equals(RetroBat.Api.Scoring.Crypto.Sha256Hex(garde), defi.DocumentSha256, StringComparison.OrdinalIgnoreCase)) return garde;
        }

        using var reponse = await client.GetAsync(defi.DocumentUrl, ct).ConfigureAwait(false);
        if (reponse.IsSuccessStatusCode) return await reponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        _journal?.LogInformation("Defis : reglement de {Defi} indisponible (HTTP {Code}).", defi.ChallengeId, (int) reponse.StatusCode);
        return null;
    }

    private static async Task EcrireAsync(string chemin, byte[] octets, CancellationToken ct)
    {
        var provisoire = chemin + ".tmp";
        await File.WriteAllBytesAsync(provisoire, octets, ct).ConfigureAwait(false);
        File.Move(provisoire, chemin, overwrite: true);
    }
}
