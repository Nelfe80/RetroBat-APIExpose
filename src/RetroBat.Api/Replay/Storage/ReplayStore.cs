using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Api.Replay.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Storage;

public sealed record ReplayIndexDoc(string Schema, DateTime GeneratedAt, IReadOnlyList<ReplayIndexEntry> Entries);

/// <summary>
/// Stockage local Replay, SANS base de données (CDC §8). Source de vérité = le fichier
/// .replay (par son hash) + le manifeste JSON immuable. Les index sont des vues dérivées
/// reconstructibles. Toutes les écritures mutables sont atomiques (tmp -> rename).
/// R1 = implémentation locale ; l'IReplayObjectStore/NelfeShare (R7) se branchera derrière.
/// </summary>
public sealed class ReplayStore : IReplayManifestStore, IReplayObjectStore, IReplayMetadataStore, IReplayIndex
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Compact (une ligne) pour le journal JSONL des réactions.
    private static readonly JsonSerializerOptions JsonLine = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ReplayStore> _logger;
    private readonly string _root, _manifests, _meta, _objects, _index, _temp, _reactions, _social;
    private readonly object _reactLock = new();

    public string ActiveRecordingPath { get; }
    public string TempRoot => _temp;
    public string SocialRoot => _social;

    public ReplayStore(ILogger<ReplayStore> logger)
        : this(logger, Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay"))
    {
    }

    /// <summary>Racine explicite : pour les tests, qui ne doivent jamais toucher au vrai magasin.</summary>
    internal ReplayStore(ILogger<ReplayStore> logger, string root)
    {
        _logger = logger;
        _root = root;
        _manifests = Path.Combine(_root, "manifests");
        _meta = Path.Combine(_root, "meta");
        _objects = Path.Combine(_root, "objects", "sha256");
        _index = Path.Combine(_root, "index");
        _temp = Path.Combine(_root, "temp");
        _reactions = Path.Combine(_root, "reactions");
        // Evenements sociaux SIGNES venus du reseau (R9). Separes du journal local des
        // reactions : celui-ci est ce QUE CETTE BORNE a produit et peut remonter, celui-la est ce
        // qu'elle a recu d'ailleurs et verifie. Les melanger ferait remonter les reactions d'autrui.
        _social = Path.Combine(_root, "social");
        ActiveRecordingPath = Path.Combine(_root, "active-recording.json");
        foreach (var d in new[] { _manifests, _meta, _objects, _index, _temp, _reactions, _social })
            Directory.CreateDirectory(d);
    }

    // ── écritures JSON atomiques ────────────────────────────────────────────
    public void WriteJsonAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public T? ReadJson<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) : default; }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : JSON illisible {Path}", path); return default; }
    }

    public void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // ── objets adressés par contenu ─────────────────────────────────────────
    public async Task<ReplayObjectRef> ImportObjectAsync(string sourcePath, CancellationToken ct)
    {
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var (sha, size) = await HashFileAsync(sourcePath, ct).ConfigureAwait(false);
        var dir = Path.Combine(_objects, sha[..2]);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, sha + ".replay");
        if (!File.Exists(dest) || new FileInfo(dest).Length != size)
            File.Copy(sourcePath, dest, overwrite: true); // dedup : si déjà présent et bonne taille, on garde
        // Un import lit l'objet DEUX fois (hash puis copie) : sur dix-sept mega-octets, ca se sent
        // pendant une partie. On le dit, avec qui l'a demande.
        RetroBat.Api.Infrastructure.IoTrace.Balayage(_logger, sourcePath, size * 2, chrono.ElapsedMilliseconds);
        return new ReplayObjectRef(sha, size);
    }

    public string ObjectPath(string sha256) => Path.Combine(_objects, sha256[..2], sha256 + ".replay");

    // ── compression AU REPOS (2026-09-25) ───────────────────────────────────
    //
    // Les joueurs se plaignaient de la place des replays : le magasin grossit a chaque START et
    // rien ne l'elaguait. Un replay de RetroArch 1.22.2 tient en 1 a 2 % de sa taille une fois
    // compresse (mesure sur tout le magasin de la borne de dev : 27 replays, 89 Mo). L'objet dort
    // donc en <sha>.replay.gz ; le brut n'est MATERIALISE que pour etre lu, puis recompresse par le
    // compacteur quand RetroArch ne tourne plus. ObjectPath reste le chemin du brut.

    public string ObjectPathGz(string sha256) => ObjectPath(sha256) + ReplayCompression.Suffixe;

    public bool HasObject(string sha256) => File.Exists(ObjectPath(sha256)) || File.Exists(ObjectPathGz(sha256));

    private readonly SemaphoreSlim _materialisation = new(1, 1);

    /// <summary>Plafond d'un brut materialise : aucun replay n'en approche, une archive qui le
    /// depasse n'est pas un replay.</summary>
    private const long PlafondBrut = 2L * 1024 * 1024 * 1024;

    public async Task<string?> EnsureRawAsync(string sha256, CancellationToken ct)
    {
        var brut = ObjectPath(sha256);
        if (File.Exists(brut)) return brut;
        var gz = ObjectPathGz(sha256);
        if (!File.Exists(gz)) return null;

        await _materialisation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(brut)) return brut;   // un autre appel vient de le faire
            Directory.CreateDirectory(_temp);
            var tmp = Path.Combine(_temp, $"materialise-{sha256}.part");
            var ecrits = await ReplayCompression.DecompresserPlafonneAsync(gz, tmp, PlafondBrut, ct).ConfigureAwait(false);
            var (sha, _) = ecrits is null ? ("", 0L) : await HashFileAsync(tmp, ct).ConfigureAwait(false);
            if (!string.Equals(sha, sha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuiet(tmp);
                _logger.LogWarning("Replay : la forme compressee de {Sha} ne redonne pas son empreinte, objet ignore.", sha256[..8]);
                return null;
            }
            File.Move(tmp, brut, overwrite: true);
            return brut;
        }
        finally
        {
            _materialisation.Release();
        }
    }

    public void DeleteObject(string sha256)
    {
        DeleteQuiet(ObjectPath(sha256));
        DeleteQuiet(ObjectPathGz(sha256));
    }

    /// <summary>
    /// Recompresse les objets bruts du magasin, au plus <paramref name="max"/> par passage. Un brut
    /// n'est SUPPRIME qu'apres avoir verifie que sa forme compressee redonne exactement son
    /// empreinte ; un brut deja accompagne de sa forme compressee (materialise pour une lecture)
    /// est simplement retire. Un fichier en cours d'utilisation ne se supprime pas sous Windows :
    /// il reste, et le passage suivant le reprendra. A n'appeler que RetroArch ferme.
    /// </summary>
    public async Task<(int Compresses, int Retires, long Avant, long Apres)> CompacterAsync(int max, CancellationToken ct)
    {
        int compresses = 0, retires = 0;
        long avant = 0, apres = 0;
        if (!Directory.Exists(_objects)) return (0, 0, 0, 0);

        foreach (var brut in Directory.EnumerateFiles(_objects, "*.replay", SearchOption.AllDirectories))
        {
            if (compresses >= max) break;
            ct.ThrowIfCancellationRequested();
            var sha = Path.GetFileNameWithoutExtension(brut);
            if (sha.Length != 64) continue;
            var gz = brut + ReplayCompression.Suffixe;
            try
            {
                if (File.Exists(gz))
                {
                    File.Delete(brut);   // brut materialise pour une lecture : la forme compressee suffit
                    retires++;
                    continue;
                }

                var taille = new FileInfo(brut).Length;
                var tmp = gz + ".part";
                await ReplayCompression.CompresserAsync(brut, tmp, ct).ConfigureAwait(false);

                // L'aller-retour AVANT de supprimer quoi que ce soit : la forme compressee doit
                // redonner l'empreinte qui nomme le fichier.
                var verif = Path.Combine(_temp, $"compact-{sha}.part");
                Directory.CreateDirectory(_temp);
                var ecrits = await ReplayCompression.DecompresserPlafonneAsync(tmp, verif, taille, ct).ConfigureAwait(false);
                var (relu, _) = ecrits == taille ? await HashFileAsync(verif, ct).ConfigureAwait(false) : ("", 0L);
                DeleteQuiet(verif);
                if (!string.Equals(relu, sha, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteQuiet(tmp);
                    _logger.LogWarning("Replay : {Sha} ne se relit pas a l'identique une fois compresse, il reste brut.", sha[..8]);
                    continue;
                }

                File.Move(tmp, gz, overwrite: true);
                File.Delete(brut);
                compresses++;
                avant += taille;
                apres += new FileInfo(gz).Length;
            }
            catch (IOException)
            {
                // En cours de lecture (un pair le telecharge) : on reessaiera au prochain passage.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return (compresses, retires, avant, apres);
    }

    public static async Task<(string sha, long size)> HashFileAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return (Convert.ToHexString(hash).ToLowerInvariant(), fs.Length);
    }

    /// <summary>R6 : intégrité de l'objet .replay avant lecture — TAILLE + SHA-256 doivent
    /// correspondre au manifeste. Indispensable dès qu'un objet peut venir d'un peer (NelfeNet) :
    /// détecte une corruption (bit rot) ou une altération. False si absent / taille≠ / hash≠.</summary>
    public async Task<bool> VerifyObjectAsync(ReplayObjectRef obj, CancellationToken ct)
    {
        try
        {
            // Compresse au repos : on materialise le brut, c'est lui qui va etre lu.
            var path = await EnsureRawAsync(obj.Sha256, ct).ConfigureAwait(false);
            if (path is null) return false;
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != obj.Size) return false;
            var (sha, _) = await HashFileAsync(path, ct).ConfigureAwait(false);
            return string.Equals(sha, obj.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── manifests (immuables) ───────────────────────────────────────────────
    public string ManifestPath(string replayId) => Path.Combine(_manifests, replayId + ".json");

    public void SaveManifest(ReplayManifest m)
    {
        var path = ManifestPath(m.ReplayId);
        if (File.Exists(path)) return; // immuable : jamais réécrire un manifeste finalisé
        WriteJsonAtomic(path, m);
    }

    public ReplayManifest? GetManifest(string replayId) => ReadJson<ReplayManifest>(ManifestPath(replayId));

    public IReadOnlyList<ReplayManifest> ListManifests()
    {
        var list = new List<ReplayManifest>();
        if (!Directory.Exists(_manifests)) return list;
        foreach (var f in Directory.EnumerateFiles(_manifests, "*.json"))
        {
            var m = ReadJson<ReplayManifest>(f);
            if (m is not null) list.Add(m);
        }
        return list;
    }

    // ── métadonnées locales (mutables) ──────────────────────────────────────
    public string MetaPath(string replayId) => Path.Combine(_meta, replayId + ".json");
    public void SaveMeta(ReplayLocalMetadata meta) => WriteJsonAtomic(MetaPath(meta.ReplayId), meta);
    public ReplayLocalMetadata? GetMeta(string replayId) => ReadJson<ReplayLocalMetadata>(MetaPath(replayId));

    // ── index (vue dérivée, reconstructible depuis les manifests) ───────────
    public string IndexPath => Path.Combine(_index, "replays.json");

    public IReadOnlyList<ReplayIndexEntry> RebuildIndex()
    {
        var entries = ListManifests()
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new ReplayIndexEntry(m.ReplayId, m.Game.GameId, m.CreatedAt, m.Object.Sha256))
            .ToList();
        WriteJsonAtomic(IndexPath, new ReplayIndexDoc("nelfe.replay.index.v1", DateTime.UtcNow, entries));
        return entries;
    }

    public IReadOnlyList<ReplayIndexEntry> ReadIndex()
    {
        var doc = ReadJson<ReplayIndexDoc>(IndexPath);
        return doc?.Entries ?? (IReadOnlyList<ReplayIndexEntry>)Array.Empty<ReplayIndexEntry>();
    }

    // ── réactions (journal JSONL append-only par replay, rejouable) ─────────
    public string ReactionsPath(string replayId) => Path.Combine(_reactions, replayId + ".jsonl");

    public void AppendReaction(ReplayReaction r)
    {
        var line = JsonSerializer.Serialize(r, JsonLine);
        lock (_reactLock) File.AppendAllText(ReactionsPath(r.ReplayId), line + "\n");
    }

    /// <summary>Les replays qui ont un journal de reactions ici, remontees ou non.</summary>
    public IReadOnlyList<string> ReplaysWithReactions()
    {
        if (!Directory.Exists(_reactions)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_reactions, "*.jsonl")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
    }

    // Les groupes (jeton de spectateur, seance) deja remontes a la plateforme : une ligne par
    // groupe, a cote du journal. Le journal, lui, reste append-only et rejouable.
    private string ReactionsSentPath(string replayId) => Path.Combine(_reactions, replayId + ".sent");

    public IReadOnlySet<string> ReadReactionsSent(string replayId)
    {
        var path = ReactionsSentPath(replayId);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        try
        {
            foreach (var l in File.ReadAllLines(path))
            {
                if (!string.IsNullOrWhiteSpace(l)) set.Add(l.Trim());
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : marques de remontee illisibles {Path}", path); }
        return set;
    }

    public void MarkReactionsSent(string replayId, string groupKey)
    {
        lock (_reactLock) File.AppendAllText(ReactionsSentPath(replayId), groupKey + "\n");
    }

    public static string ReactionGroupKey(string viewerToken, long sessionSeq) => viewerToken + "|" + sessionSeq;

    public IReadOnlyList<ReplayReaction> ReadReactions(string replayId)
    {
        var path = ReactionsPath(replayId);
        var list = new List<ReplayReaction>();
        if (!File.Exists(path)) return list;
        try
        {
            foreach (var l in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                var r = JsonSerializer.Deserialize<ReplayReaction>(l, JsonLine);
                if (r is not null) list.Add(r);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : réactions illisibles {Path}", path); }
        return list;
    }
}
