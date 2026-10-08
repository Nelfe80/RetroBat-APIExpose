using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// L'annuaire des pairs : réunit ce que TOUTES les portes d'entrée rapportent, sans en privilégier
/// aucune par principe (CDC §47). Une borne installée chez un particulier n'a ni hub ni
/// administrateur : il faut donc que le LAN, la plateforme et le fichier manuel marchent en même
/// temps, et que l'absence de l'un n'empêche pas les autres.
///
/// Deux règles de fusion. D'abord, deux entrées désignant la même borne sont fondues, et celle qui
/// porte une CLÉ gagne : découvrir une borne sur le LAN ne doit pas effacer l'identifiant qu'on
/// avait pour elle. Ensuite, tout pair chez qui une récupération a RÉUSSI est mémorisé sur disque
/// et resservi aux démarrages suivants : ce sont les « pairs récents » du §53, et c'est ce qui
/// permet de retrouver ses voisins quand l'annuaire en ligne est injoignable.
/// </summary>
public sealed class ReplayPeerDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly IEnumerable<IReplayPeerSource> _sources;
    private readonly ILogger<ReplayPeerDirectory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ReplayPeer> _cache = new();
    private DateTime _cachedAt;

    public ReplayPeerDirectory(IEnumerable<IReplayPeerSource> sources, ILogger<ReplayPeerDirectory> logger)
    {
        _sources = sources; _logger = logger;
    }

    private static string KnownPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "known-peers.json");

    public async Task<IReadOnlyList<ReplayPeer>> PeersAsync(CancellationToken ct, bool refresh = false)
    {
        if (!refresh && DateTime.UtcNow - _cachedAt < CacheFor && _cache.Count > 0) return _cache;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && DateTime.UtcNow - _cachedAt < CacheFor && _cache.Count > 0) return _cache;

            var merged = new Dictionary<string, ReplayPeer>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ReadKnown()) Merge(merged, p);

            // Les sources sont interrogées EN PARALLÈLE : une porte lente (annuaire en ligne
            // injoignable) ne doit pas retarder celles qui répondent tout de suite.
            var harvest = await Task.WhenAll(_sources.Select(s => SafeDiscover(s, ct))).ConfigureAwait(false);
            foreach (var batch in harvest)
                foreach (var p in batch) Merge(merged, p);

            // Ordre de résolution du CDC §13 : un voisin d'abord, le miroir en dernier. Il est
            // toujours joignable, donc il répondrait toujours en premier si on le laissait faire,
            // et on paierait un aller-retour central là où un pair du LAN suffisait.
            _cache = merged.Values
                .OrderBy(p => p.Source.Contains(MirrorPeerSource.SourceTag, StringComparison.Ordinal) ? 1 : 0)
                .ToList();
            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Mémorise un pair qui a réellement livré un objet. C'est le seul signal de qualité
    /// dont on dispose aujourd'hui, et il survit au redémarrage.</summary>
    public void RememberWorking(ReplayPeer peer)
    {
        // Un MIROIR est une source CONFIGURÉE, pas un voisin découvert. Le mémoriser ferait
        // survivre une URL périmée à son propre changement de configuration : constaté en test,
        // où le miroir de recette restait listé après avoir été repointé sur la plateforme.
        if (peer.Source.Contains(MirrorPeerSource.SourceTag, StringComparison.Ordinal)) return;
        // Un NOEUD vient de la carte signee du reseau : s'il en sort, il ne doit pas survivre ici.
        if (peer.Source.Contains(NodePeerSource.SourceTag, StringComparison.Ordinal)) return;
        try
        {
            var known = ReadKnown().ToDictionary(p => Normalize(p.BaseUrl), p => p, StringComparer.OrdinalIgnoreCase);
            known[Normalize(peer.BaseUrl)] = peer;
            var doc = new ReplayPeersDoc("nelfe.replay.known-peers.v1", known.Values.ToList());
            var path = KnownPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(doc, PeerJson.Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : pair récent non mémorisé."); }
    }

    private IEnumerable<ReplayPeer> ReadKnown()
    {
        try
        {
            if (!File.Exists(KnownPath)) return Array.Empty<ReplayPeer>();
            var doc = JsonSerializer.Deserialize<ReplayPeersDoc>(File.ReadAllBytes(KnownPath), PeerJson.Options);
            return (doc?.Peers ?? new List<ReplayPeer>()).Where(p => !string.IsNullOrWhiteSpace(p.BaseUrl));
        }
        catch { return Array.Empty<ReplayPeer>(); }
    }

    private async Task<IReadOnlyList<ReplayPeer>> SafeDiscover(IReplayPeerSource source, CancellationToken ct)
    {
        try { return await source.DiscoverAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : porte d'entrée « {Source} » sans résultat.", source.Name);
            return Array.Empty<ReplayPeer>();
        }
    }

    private static void Merge(Dictionary<string, ReplayPeer> into, ReplayPeer peer)
    {
        var key = Normalize(peer.BaseUrl);
        if (key.Length == 0) return;
        if (!into.TryGetValue(key, out var existing)) { into[key] = peer; return; }

        // Ne jamais perdre une clé ni une identité déjà connue en refusionnant la même borne.
        into[key] = existing with
        {
            ApiKey = existing.ApiKey ?? peer.ApiKey,
            DeviceId = existing.DeviceId ?? peer.DeviceId,
            Name = string.IsNullOrWhiteSpace(existing.Name) ? peer.Name : existing.Name,
            Source = existing.Source == peer.Source ? existing.Source : existing.Source + "+" + peer.Source,
        };
    }

    private static string Normalize(string? baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
}
