using System.Net;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Storage;
using RetroBat.Api.Reseau;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// LES NOEUDS « REPLAY » DE LA CARTE (CDC infra §3.6 et §15.7, 2026-10-08) : des serveurs d'hebergeurs approuves
/// qui gardent une part des replays publics des scores publies, et les servent par leur empreinte. La borne les
/// apprend par la carte signee du reseau, pas par un annuaire : chacun expose
/// <c>/replays/v1/objets/{sha}.replay.gz</c>, et la borne verifie taille et SHA-256 a l'arrivee comme pour tout
/// pair. Ils passent avant l'amorce GitHub et GitLab, apres les voisins du reseau local.
///
/// Un replay ne revient qu'a quelques noeuds (hachage de rendez-vous, <see cref="Rendezvous"/>) : la borne ne
/// demande qu'a ceux-la, sans sonder les autres.
/// </summary>
public sealed class NodePeerSource : IReplayPeerSource
{
    /// <summary>Marqueur de la source : l'annuaire ne memorise pas ces pairs, la carte fait foi.</summary>
    public const string SourceTag = "noeud";

    private readonly ServiceDeCarte _cartes;
    private readonly IConfiguration _config;

    public NodePeerSource(ServiceDeCarte cartes, IConfiguration config)
    {
        _cartes = cartes;
        _config = config;
    }

    public string Name => SourceTag;

    /// <summary>Le gabarit d'URL d'un noeud ; la borne essaie la forme compressee (<c>.gz</c>) d'abord.</summary>
    public static string Gabarit(string urlDuNoeud) => urlDuNoeud.TrimEnd('/') + "/replays/v1/objets/{sha}.replay";

    /// <summary>Ou une borne depose un replay chez un noeud.</summary>
    public static string Depot(string urlDuNoeud, string sha) => urlDuNoeud.TrimEnd('/') + "/replays/v1/depot/" + sha + ".replay.gz";

    public bool Actif => _config.GetValue("Replay:Share:NodesEnabled", true);

    /// <summary>Les noeuds « replay » de la carte en vigueur (des noeuds enroles, avec leur cle).</summary>
    public IReadOnlyList<NoeudDuReseau> Noeuds()
        => Actif ? _cartes.Actuelle.AvecLeRole("replay").Where(n => n.Genre == "node" && n.Cle is not null).ToList() : [];

    /// <summary>Les noeuds a qui revient ce replay (rendez-vous), dans l'ordre.</summary>
    public IReadOnlyList<NoeudDuReseau> Proprietaires(string sha)
        => Rendezvous.Proprietaires(sha, Noeuds(), _cartes.Actuelle.Copies);

    public Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReplayPeer>>(Noeuds()
            .Select(n => new ReplayPeer("noeud " + n.Nom, n.Url, ApiKey: null, Source: SourceTag, UrlTemplate: Gabarit(n.Url)))
            .ToList());

    /// <summary>
    /// Parmi ces pairs, ceux a qui demander ce replay : tous les autres pairs, et seulement les noeuds a qui il
    /// revient. Un noeud qui n'en est pas proprietaire ne l'a pas : inutile de le sonder.
    /// </summary>
    public IReadOnlyList<ReplayPeer> Filtrer(IReadOnlyList<ReplayPeer> pairs, string sha)
    {
        if (!pairs.Any(p => p.Source.Contains(SourceTag, StringComparison.Ordinal))) return pairs;
        var proprietaires = Proprietaires(sha).Select(n => n.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pairs.Where(p => !p.Source.Contains(SourceTag, StringComparison.Ordinal) || proprietaires.Contains(p.BaseUrl.TrimEnd('/'))).ToList();
    }
}

/// <summary>
/// LE DEPOT DES REPLAYS CHEZ LES NOEUDS (CDC infra §15.7, 2026-10-08). Un replay public d'un score publie ne doit
/// pas vivre que sur la borne qui l'a enregistre : elle le depose chez les noeuds a qui il revient (deux par
/// defaut). Les replays du top y sont deja (les noeuds les prennent a l'amorce) ; ce service porte les autres.
///
/// Rien de prive ne part : seuls les replays que le joueur a rendus publics, et seulement ceux que le central
/// liste (GET /api/v1/nodes/replays : public, score publie). Le noeud revérifie le contenu avant de le garder.
/// Un passage toutes les six heures, et trois minutes apres un verdict publie ; jamais pendant une partie.
/// </summary>
public sealed class ReplayNodeDepositService : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromHours(6);
    private static readonly TimeSpan ApresUnVerdict = TimeSpan.FromMinutes(3);
    private const int DepotsAuPlus = 40;

    private readonly NodePeerSource _noeuds;
    private readonly IReplayManifestStore _manifestes;
    private readonly IReplayMetadataStore _metas;
    private readonly IReplayObjectStore _objets;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly IEventBus? _bus;
    private readonly ILogger<ReplayNodeDepositService>? _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Func<bool> _enJeu;

    public ReplayNodeDepositService(NodePeerSource noeuds, IReplayManifestStore manifestes, IReplayMetadataStore metas,
        IReplayObjectStore objets, IHttpClientFactory http, IConfiguration config, IEventBus? bus = null,
        ILogger<ReplayNodeDepositService>? logger = null)
        : this(noeuds, manifestes, metas, objets, http, config, bus, logger, EmulatorForeground.EmulateurTourne)
    {
    }

    internal ReplayNodeDepositService(NodePeerSource noeuds, IReplayManifestStore manifestes, IReplayMetadataStore metas,
        IReplayObjectStore objets, IHttpClientFactory http, IConfiguration config, IEventBus? bus,
        ILogger<ReplayNodeDepositService>? logger, Func<bool> enJeu)
    {
        _noeuds = noeuds;
        _manifestes = manifestes;
        _metas = metas;
        _objets = objets;
        _http = http;
        _config = config;
        _bus = bus;
        _logger = logger;
        _enJeu = enJeu;
    }

    public sealed record Bilan(int Deposes, int DejaLa, int Refuses, int Candidats);

    /// <summary>Un passage est demande (un verdict vient d'etre publie).</summary>
    public void Signaler()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var abonnement = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type == "scoring.verdict") Signaler();
        });
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var bilan = await PasserAsync(stoppingToken).ConfigureAwait(false);
                    if (bilan.Deposes > 0 || bilan.Refuses > 0)
                        _logger?.LogInformation("Replays : {Deposes} depose(s) chez les noeuds, {DejaLa} deja la, {Refuses} refuse(s).",
                            bilan.Deposes, bilan.DejaLa, bilan.Refuses);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Replays : passage de depot chez les noeuds en echec.");
                }
                // Le prochain passage : dans six heures, ou trois minutes apres un verdict (le temps que le lien
                // du replay soit declare et que le central le liste).
                if (await _signal.WaitAsync(Periode, stoppingToken).ConfigureAwait(false))
                    await Task.Delay(ApresUnVerdict, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Un passage : pour chaque replay public de cette borne que le central liste, le deposer chez ceux de ses
    /// noeuds qui ne l'ont pas.
    /// </summary>
    internal async Task<Bilan> PasserAsync(CancellationToken ct)
    {
        if (!_config.GetValue("Replay:Share:NodeDepositEnabled", true) || _enJeu()) return new Bilan(0, 0, 0, 0);
        if (_noeuds.Noeuds().Count == 0) return new Bilan(0, 0, 0, 0);

        var candidats = _manifestes.ListManifests()
            .Where(m => _metas.GetMeta(m.ReplayId) is { CreatedByThisDevice: true } meta
                        && string.Equals(meta.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                        && _objets.HasObject(m.Object.Sha256))
            .Select(m => m.Object.Sha256.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidats.Count == 0) return new Bilan(0, 0, 0, 0);

        // Ce que les noeuds acceptent : les replays publics des scores publies. Sans la liste, rien ne part.
        var acceptes = await ListeDuCentralAsync(ct).ConfigureAwait(false);
        if (acceptes is null) return new Bilan(0, 0, 0, candidats.Count);

        var client = _http.CreateClient(nameof(ReplayNodeDepositService));
        client.Timeout = TimeSpan.FromMinutes(5);
        int deposes = 0, dejaLa = 0, refuses = 0;
        var muets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in candidats.Where(acceptes.Contains))
        {
            if (deposes >= DepotsAuPlus || _enJeu()) break;
            foreach (var noeud in _noeuds.Proprietaires(sha))
            {
                if (muets.Contains(noeud.Url)) continue;
                try
                {
                    using var sonde = new HttpRequestMessage(HttpMethod.Head, ReplayCompression.UrlCompressee(
                        NodePeerSource.Gabarit(noeud.Url).Replace("{sha}", sha, StringComparison.Ordinal)));
                    using (var presence = await client.SendAsync(sonde, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        if (presence.IsSuccessStatusCode)
                        {
                            dejaLa++;
                            continue;
                        }
                        if (presence.StatusCode != HttpStatusCode.NotFound)
                        {
                            muets.Add(noeud.Url);
                            continue;
                        }
                    }
                    switch (await DeposerAsync(client, noeud, sha, ct).ConfigureAwait(false))
                    {
                        case >= 200 and < 300:
                            deposes++;
                            break;
                        case >= 400 and < 500:
                            refuses++;
                            break;
                        default:
                            muets.Add(noeud.Url);
                            break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
                {
                    _logger?.LogDebug(ex, "Replays : noeud {Noeud} injoignable pour un depot.", noeud.Nom);
                    muets.Add(noeud.Url);
                }
            }
        }
        return new Bilan(deposes, dejaLa, refuses, candidats.Count);
    }

    /// <summary>Depose la forme compressee d'un objet chez un noeud ; rend le statut HTTP (0 si l'objet manque).</summary>
    private async Task<int> DeposerAsync(HttpClient client, NoeudDuReseau noeud, string sha, CancellationToken ct)
    {
        var compresse = _objets.ObjectPath(sha) + ReplayCompression.Suffixe;
        string? temporaire = null;
        try
        {
            if (!File.Exists(compresse))
            {
                var brut = await _objets.EnsureRawAsync(sha, ct).ConfigureAwait(false);
                if (brut is null) return 0;
                temporaire = Path.Combine(_objets.TempRoot, "depot-" + sha[..16] + "-" + Guid.NewGuid().ToString("N") + ".gz");
                Directory.CreateDirectory(_objets.TempRoot);
                await ReplayCompression.CompresserAsync(brut, temporaire, ct).ConfigureAwait(false);
                compresse = temporaire;
            }
            await using var flux = File.OpenRead(compresse);
            using var contenu = new StreamContent(flux);
            contenu.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/gzip");
            contenu.Headers.ContentLength = flux.Length;
            using var reponse = await client.PutAsync(NodePeerSource.Depot(noeud.Url, sha), contenu, ct).ConfigureAwait(false);
            var statut = (int)reponse.StatusCode;
            if (statut is >= 200 and < 300)
                _logger?.LogInformation("Replays : {Sha} depose chez le noeud {Noeud}.", sha[..12], noeud.Nom);
            else
                _logger?.LogInformation("Replays : le noeud {Noeud} n'a pas pris {Sha} (HTTP {Statut}).", noeud.Nom, sha[..12], statut);
            return statut;
        }
        finally
        {
            if (temporaire is not null)
            {
                try
                {
                    File.Delete(temporaire);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Les empreintes des replays que les noeuds gardent (public, score publie), ou null si le central ne repond pas.</summary>
    private async Task<HashSet<string>?> ListeDuCentralAsync(CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient(nameof(ReplayNodeDepositService));
            client.Timeout = TimeSpan.FromSeconds(30);
            using var reponse = await client.GetAsync(NelfePlayAgentService.BaseUrl.TrimEnd('/') + "/api/v1/nodes/replays", ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await reponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("objects", out var liste) || liste.ValueKind != JsonValueKind.Array) return null;
            var empreintes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var objet in liste.EnumerateArray())
            {
                if (objet.ValueKind == JsonValueKind.Object && objet.TryGetProperty("sha256", out var sha)
                    && sha.ValueKind == JsonValueKind.String && sha.GetString() is { Length: 64 } texte)
                    empreintes.Add(texte);
            }
            return empreintes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Replays : liste des noeuds injoignable.");
            return null;
        }
    }
}
