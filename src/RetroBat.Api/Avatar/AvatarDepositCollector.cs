using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Sharing;

namespace RetroBat.Api.Avatar;

/// <summary>
/// Va chercher les planches d'avatar que la plateforme garde pour CE compte.
///
/// C'est l'autre moitie d'un renversement. Un navigateur ne peut pas parler au loopback d'une
/// borne (le navigateur le bloque), et passer par une fenetre demandait au joueur un geste, sa
/// borne allumee, et le meme appareil. Le joueur DEPOSE donc sa planche sur la plateforme sans
/// rien faire de plus, et c'est la borne qui vient la prendre quand elle tourne : ca marche
/// depuis un telephone, borne eteinte, et pour plusieurs bornes du meme compte.
///
/// La borne ne dit jamais de quel compte il s'agit : elle s'authentifie comme MACHINE, et le lien
/// machine vers compte designe les depots. Elle ne peut donc prendre que les planches du compte
/// auquel elle est appairee.
///
/// Rien n'est cru sur parole a l'arrivee : le magasin recalcule l'empreinte et refuse des octets
/// qui ne tombent pas sur celle demandee. Une fois rangee, la borne le DIT, la plateforme efface
/// les octets (elle fait transiter, elle ne conserve pas), et le recensement la declare pour que
/// les autres bornes puissent la demander par le relais.
/// </summary>
public sealed class AvatarDepositCollector : BackgroundService
{
    private const string CheminAttente = "/api/v1/agent/avatar/pending";
    private const string CheminObjet = "/api/v1/agent/avatar/object";
    private const string CheminPrise = "/api/v1/agent/avatar/taken";

    /// <summary>Un depot n'est pas urgent : le joueur ne regarde pas sa borne quand il le pose.</summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(70);

    private readonly AvatarSheetStore _magasin;
    private readonly NelfePlayDeviceStore _machine;
    private readonly ReplayHoldingsReporter _recensement;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AvatarDepositCollector> _logger;
    private readonly SuiviDuSite? _suivi;
    /// <summary>Le minuteur et le releve de l'agent ne prennent pas la meme planche en meme temps.</summary>
    private readonly SemaphoreSlim _prise = new(1, 1);

    public AvatarDepositCollector(AvatarSheetStore magasin, NelfePlayDeviceStore machine,
        ReplayHoldingsReporter recensement, IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<AvatarDepositCollector> logger, SuiviDuSite? suivi = null)
    {
        _suivi = suivi;
        // Les planches jointes au releve de l'agent (point 5 du 14/11) : meme traitement que le releve du minuteur.
        _suivi?.Traiter(SuiviDuSite.AvatarsEnAttente, (liste, ct) => TraiterLaListeAsync(liste, ct));
        _magasin = magasin;
        _machine = machine;
        _recensement = recensement;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var echecs = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // COUVERT (point 5 du 14/11) : le releve de l'agent porte deja la liste ; le minuteur se tait.
                var r = _suivi?.Couvert(SuiviDuSite.AvatarsEnAttente, DateTime.UtcNow) == true
                    ? new Resultat(false, 0, "covered")
                    : await RelevereAsync(stoppingToken).ConfigureAwait(false);
                echecs = r.Reason is "unreachable" || r.Reason?.StartsWith("http_", StringComparison.Ordinal) == true ? echecs + 1 : 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Avatars : releve des depots en erreur."); echecs++; }

            // Recul apres echec (2 h au plus) et dispersion de 10 % (C-9, 14/11).
            try { await Task.Delay(CadenceDeFond.Prochaine(Cadence, echecs, TimeSpan.FromHours(2)), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public sealed record Resultat(bool Ran, int Recues, string? Reason);

    public async Task<Resultat> RelevereAsync(CancellationToken ct)
    {
        var client = Client();
        if (client is null) return new Resultat(false, 0, "device_not_paired");

        List<Depot> depots;
        try
        {
            using var reponse = await client.GetAsync(CheminAttente, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return new Resultat(false, 0, "http_" + (int) reponse.StatusCode);
            using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            depots = doc.RootElement.TryGetProperty("sheets", out var liste) ? Lire(liste) : [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Avatars : plateforme injoignable pour les depots.");
            return new Resultat(false, 0, "unreachable");
        }

        return await PrendreLesDepotsAsync(client, depots, ct).ConfigureAwait(false);
    }

    /// <summary>La liste <c>avatar_pending</c> jointe au releve de l'agent, meme forme que <c>sheets</c>.</summary>
    internal async Task<Resultat> TraiterLaListeAsync(JsonElement liste, CancellationToken ct)
    {
        var depots = Lire(liste);
        if (depots.Count == 0) return new Resultat(true, 0, null);
        var client = Client();
        if (client is null) return new Resultat(false, 0, "device_not_paired");
        return await PrendreLesDepotsAsync(client, depots, ct).ConfigureAwait(false);
    }

    private HttpClient? Client()
    {
        var credential = _machine.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return null;
        var client = _httpFactory.CreateClient();
        client.BaseAddress = new Uri(BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
        return client;
    }

    private async Task<Resultat> PrendreLesDepotsAsync(HttpClient client, List<Depot> depots, CancellationToken ct)
    {
        if (depots.Count == 0) return new Resultat(true, 0, null);
        await _prise.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PrendreLesDepotsSeulAsync(client, depots, ct).ConfigureAwait(false);
        }
        finally
        {
            _prise.Release();
        }
    }

    private async Task<Resultat> PrendreLesDepotsSeulAsync(HttpClient client, List<Depot> depots, CancellationToken ct)
    {
        var recues = 0;
        foreach (var depot in depots)
        {
            if (ct.IsCancellationRequested) break;

            // Deja la (une autre borne du compte l'a semee, ou un passage precedent) : on le dit
            // quand meme, sinon la plateforme garderait des octets dont plus personne n'a besoin.
            if (_magasin.Has(depot.Sha256))
            {
                _magasin.Cataloguer(depot.Pseudo, depot.Famille, depot.Variation, depot.Generateur, depot.Sha256, verifiee: true);
                await SignalerPriseAsync(client, depot.Sha256, ct).ConfigureAwait(false);
                continue;
            }

            if (await PrendreAsync(client, depot, ct).ConfigureAwait(false)) recues++;
        }

        if (recues > 0)
        {
            _logger.LogInformation("Avatars : {Count} planche(s) recue(s) de la plateforme.", recues);
            // Se declarer detentrice tout de suite : tant que le recensement ne le dit pas, aucune
            // autre borne ne peut demander la planche par le relais.
            try { await _recensement.ReportAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Avatars : recensement apres reception en erreur."); }
        }

        return new Resultat(true, recues, null);
    }

    private async Task<bool> PrendreAsync(HttpClient client, Depot depot, CancellationToken ct)
    {
        var temporaire = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nelfe-avatar-" + depot.Sha256 + ".part");
        try
        {
            using var reponse = await client
                .GetAsync(CheminObjet + "?sha256=" + Uri.EscapeDataString(depot.Sha256), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("Avatars : depot {Sha} refuse ({Code}).", Court(depot.Sha256), (int) reponse.StatusCode);
                return false;
            }

            await using (var sortie = File.Create(temporaire))
            {
                await reponse.Content.CopyToAsync(sortie, ct).ConfigureAwait(false);
            }

            // Le magasin recalcule l'empreinte : des octets qui ne tombent pas dessus n'entrent pas.
            var r = await _magasin.ImporterFichierAsync(temporaire, depot.Sha256, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                _logger.LogWarning("Avatars : planche recue pour {Attendu} ecartee ({Raison}).", Court(depot.Sha256), r.Erreur);
                return false;
            }

            _magasin.Cataloguer(depot.Pseudo, depot.Famille, depot.Variation, depot.Generateur, depot.Sha256, verifiee: true);
            await SignalerPriseAsync(client, depot.Sha256, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Avatars : depot {Sha} non recupere.", Court(depot.Sha256));
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaire)) File.Delete(temporaire); } catch { }
        }
    }

    /// <summary>Dit que la planche est rangee. La plateforme efface alors ses octets.</summary>
    private async Task SignalerPriseAsync(HttpClient client, string sha256, CancellationToken ct)
    {
        try
        {
            using var contenu = new StringContent("{\"sha256\":\"" + sha256 + "\"}", Encoding.UTF8, "application/json");
            using var _ = await client.PostAsync(CheminPrise, contenu, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Un signal manque fait refaire le transfert au prochain passage : sans consequence.
            _logger.LogDebug(ex, "Avatars : prise non signalee pour {Sha}.", Court(sha256));
        }
    }

    private sealed record Depot(string Sha256, string Pseudo, string Famille, string Generateur, int Variation);

    /// <summary>Une liste de depots (<c>sheets</c> d'avatar/pending, <c>avatar_pending</c> du releve). Pur.</summary>
    private static List<Depot> Lire(JsonElement liste)
    {
        var sortie = new List<Depot>();
        if (liste.ValueKind != JsonValueKind.Array) return sortie;
        foreach (var e in liste.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var sha = Texte(e, "sha256");
            var pseudo = Texte(e, "pseudo");
            var famille = Texte(e, "family");
            var generateur = Texte(e, "generator");
            if (!AvatarSheetStore.EstSha(sha) || pseudo.Length == 0 || famille.Length == 0 || generateur.Length == 0) continue;
            var variation = e.TryGetProperty("variation", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
            sortie.Add(new Depot(sha, pseudo, famille, generateur, variation));
        }
        return sortie;
    }

    private static string Texte(JsonElement racine, string nom)
        => racine.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private string BaseUrl
    {
        get
        {
            var url = _config["Replay:Share:TransitUrl"];
            if (string.IsNullOrWhiteSpace(url)) url = NelfePlayAgentService.BaseUrl;
            return (url ?? string.Empty).TrimEnd('/');
        }
    }

    private static string Court(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
