using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Une façon d'apprendre l'existence d'autres bornes. Le CDC §53 en liste plusieurs ; on les
/// implémente toutes plutôt qu'une seule, parce que la plupart des APIExpose tournent sur un PC
/// personnel : il n'y a ni hub de salle, ni administrateur pour remplir un fichier.
///
/// Une source DÉCOUVRE, elle n'autorise pas. Savoir qu'une borne existe ne donne pas le droit de
/// lui prendre quoi que ce soit : c'est la clé de partage, ou plus tard un jeton signé, qui le fait.
/// </summary>
public interface IReplayPeerSource
{
    /// <summary>Nom court, pour le diagnostic (« manuel », « lan », « ancre », « plateforme »).</summary>
    string Name { get; }

    Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct);
}

/// <summary>Le fichier rempli à la main. Porte de secours, et la seule qui donne une clé.</summary>
public sealed class ManualPeerSource : IReplayPeerSource
{
    private readonly ReplayPeerStore _store;
    public ManualPeerSource(ReplayPeerStore store) => _store = store;
    public string Name => "manuel";
    public Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReplayPeer>>(_store.Peers.Select(p => p with { Source = "manuel" }).ToList());
}

/// <summary>
/// Les ANCRES : les machines qui nous ont appelés en présentant la clé d'administration depuis le
/// réseau. En pratique, un hub de flotte. On n'a pas besoin de configurer son adresse, il se
/// présente lui-même en nous parlant ; la borne ne connaît d'ailleurs pas son hub autrement.
///
/// Le hub est une source prioritaire sur le LAN (CDC §50) et peut détenir une copie des replays
/// de la salle (§49). Tant qu'il n'expose pas l'annuaire attendu, cette source ne rend rien : le
/// contrat est posé côté borne, l'implémentation viendra côté hub.
/// </summary>
public sealed class AnchorPeerSource : IReplayPeerSource
{
    /// <summary>Chemin auquel une ancre est censée publier les autres bornes qu'elle connaît.</summary>
    public const string DirectoryPath = "/api/v1/nelfenet/peers";

    private static readonly TimeSpan Freshness = TimeSpan.FromHours(12);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Seen = new();

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnchorPeerSource> _logger;

    public AnchorPeerSource(IHttpClientFactory httpFactory, ILogger<AnchorPeerSource> logger)
    {
        _httpFactory = httpFactory; _logger = logger;
    }

    public string Name => "ancre";

    /// <summary>Appelé par le pipeline quand une requête administrée arrive du réseau.</summary>
    public static void Remember(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address)) return;
        Seen[address.ToString()] = DateTime.UtcNow;
    }

    public async Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<ReplayPeer>();
        foreach (var (host, seenAt) in Seen.ToArray())
        {
            if (DateTime.UtcNow - seenAt > Freshness) { Seen.TryRemove(host, out _); continue; }
            var baseUrl = $"http://{host}:12345";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var client = _httpFactory.CreateClient();
                client.Timeout = Timeout.InfiniteTimeSpan;
                using var res = await client.GetAsync(baseUrl + DirectoryPath, cts.Token).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) continue; // ancre sans annuaire : normal aujourd'hui
                var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                found.AddRange(ParsePeers(body, "ancre"));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : ancre {Host} sans annuaire exploitable.", host); }
        }
        return found;
    }

    internal static IEnumerable<ReplayPeer> ParsePeers(string json, string source)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ReplayPeersDoc>(json, PeerJson.Options);
            return (doc?.Peers ?? new List<ReplayPeer>())
                .Where(p => !string.IsNullOrWhiteSpace(p.BaseUrl))
                .Select(p => p with { Source = source })
                .ToList();
        }
        catch { return Array.Empty<ReplayPeer>(); }
    }
}

/// <summary>
/// La PLATEFORME. Pour deux bornes chez deux particuliers différents, c'est le seul rendez-vous
/// possible : chacune sort en HTTPS avec son identité d'appareil, aucune n'est joignable en
/// entrant. La borne sait déjà lui parler, elle est appairée.
///
/// ⚠️ Ce que l'annuaire renvoie sont des CANDIDATS, pas des bornes joignables. Deux machines
/// derrière deux routeurs domestiques ne peuvent pas se connecter directement ; c'est le relais
/// prévu au CDC §52 qui rendra ce cas fonctionnel. La joignabilité est donc constatée par sonde,
/// jamais supposée.
/// </summary>
public sealed class PlatformPeerSource : IReplayPeerSource
{
    public const string DirectoryPath = "/api/v1/nelfenet/peers";

    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PlatformPeerSource> _logger;

    public PlatformPeerSource(RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices,
        IHttpClientFactory httpFactory, ILogger<PlatformPeerSource> logger)
    {
        _devices = devices; _httpFactory = httpFactory; _logger = logger;
    }

    public string Name => "plateforme";

    public async Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct)
    {
        var deviceId = _devices.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId)) return Array.Empty<ReplayPeer>(); // borne non appairée
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var url = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl.TrimEnd('/') + DirectoryPath;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Device-Id", deviceId);
            using var res = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Array.Empty<ReplayPeer>(); // annuaire pas encore en ligne
            var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return AnchorPeerSource.ParsePeers(body, "plateforme").ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : annuaire plateforme injoignable.");
            return Array.Empty<ReplayPeer>();
        }
    }
}

/// <summary>
/// L'AMORCE : l'étape 6 de l'ordre de résolution du CDC §13, juste avant le relais.
///
/// Une borne chez un particulier ne peut pas être jointe en entrant. Personne ne peut donc aller
/// chercher un record chez celui qui vient de l'établir, et la diffusion ne démarrerait jamais
/// sans un premier dépôt joignable. C'est le rôle de l'amorce : un endroit statique, public, que
/// toute borne atteint par une simple requête SORTANTE.
///
/// ⚠️ L'amorce n'est PAS la plateforme. La distribution appartient à NelfeNet ; NelfePlay
/// publie et fait transiter, elle ne sert jamais un objet. L'amorce est donc un hébergement
/// statique tiers, désigné par un GABARIT d'URL, ce qui la rend interchangeable.
///
/// Elle n'est jamais une autorité (CDC §56 : accélérateur, jamais prérequis). L'adressage par
/// contenu la neutralise : taille et SHA-256 sont vérifiés à l'arrivée, et elle ne sert que des
/// objets publics. On l'interroge en DERNIER, un voisin du LAN étant plus rapide et gratuit.
/// </summary>
public sealed class MirrorPeerSource : IReplayPeerSource
{
    /// <summary>Marqueur reconnu par l'annuaire pour reléguer ces entrées en fin de liste.</summary>
    public const string SourceTag = "miroir";

    /// <summary>
    /// LA SECONDE SOURCE (2026-10-08) : le serveur NelfePlay pose les mêmes replays sur GitLab que
    /// sur l'amorce GitHub, à plat et nommés par leur empreinte. Une borne y va quand GitHub ne
    /// répond pas. Remplaçable par <c>Replay:Share:MirrorFallbackUrlTemplates</c>, coupé par
    /// <c>Replay:Share:MirrorFallbackEnabled = false</c>.
    /// </summary>
    public const string GitLabTemplate = "https://gitlab.com/Nelfe80/NelfeNet-Replays/-/raw/main/objects/{sha}.replay";

    private readonly IConfiguration _config;
    public MirrorPeerSource(IConfiguration config) => _config = config;

    public string Name => SourceTag;

    public Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct)
    {
        // Lecture sortante d'objets publics : rien n'est exposé, donc actif par défaut.
        if (!_config.GetValue("Replay:Share:MirrorEnabled", true))
            return Task.FromResult<IReadOnlyList<ReplayPeer>>(Array.Empty<ReplayPeer>());

        // AUCUN repli sur la plateforme : elle n'est jamais une source d'objets. Sans gabarit
        // configure, il n'y a pas d'amorce, point. L'ordre est celui d'essai : GitHub, puis GitLab.
        var gabarits = new List<string>();
        var principal = _config["Replay:Share:MirrorUrlTemplate"];
        if (!string.IsNullOrWhiteSpace(principal)) gabarits.Add(principal);
        if (_config.GetValue("Replay:Share:MirrorFallbackEnabled", true))
        {
            var secours = _config.GetSection("Replay:Share:MirrorFallbackUrlTemplates").Get<string[]>();
            gabarits.AddRange(secours is { Length: > 0 } ? secours : [GitLabTemplate]);
        }

        var pairs = new List<ReplayPeer>();
        foreach (var template in gabarits.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(template) || !template.Contains("{sha}", StringComparison.Ordinal)) continue;
            var label = Uri.TryCreate(template, UriKind.Absolute, out var u) ? u.Host : "amorce";
            pairs.Add(new ReplayPeer("amorce " + label, template, ApiKey: null, Source: SourceTag, UrlTemplate: template));
        }
        return Task.FromResult<IReadOnlyList<ReplayPeer>>(pairs);
    }
}

internal static class PeerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
