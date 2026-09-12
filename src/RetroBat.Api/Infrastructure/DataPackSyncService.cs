using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Le Data Pack OFFICIEL, fichier par fichier : APIExpose va chercher dans le depot public
/// RetroBat-DataPack ce que ses resources/ ont de perime, et rien d'autre.
///
/// Pourquoi un depot git et pas une archive : c'est git qui donne la granularite fichier. Un
/// appel pour le commit HEAD (rien a faire s'il n'a pas bouge), un pour l'arbre (chaque
/// fichier y a son sha de blob), puis seulement les fichiers dont le sha differe du local,
/// un par un depuis le CDN de contenu brut de GitHub, qui ne compte pas dans la limite
/// d'appels (60 par heure et par adresse, anonyme). Un .MEM de 2 Ko corrige coute 2 appels
/// et 2 Ko, pas 150 Mo.
///
/// Les bases par systeme (gamelist/systems, jusqu'a 161 Mo) ne tiennent pas dans un
/// historique git : elles viennent de la release « gamelist » du meme depot, un actif gzip
/// par systeme et un manifeste d'empreintes, et seuls les systemes dont le contenu a change
/// sont repris.
///
/// Ce canal ECRASE : c'est l'officiel. Le canal communautaire, lui, n'ajoute que les absents.
/// Il n'efface jamais rien : ce qui existe en local sans exister au depot (la couche perso
/// <c>ram/.user</c>, un .MEM communautaire, les SVG et gameinfos que l'API genere) est laisse
/// tel quel. Chaque ecriture est atomique (fichier temporaire puis renommage) et chaque octet
/// telecharge est verifie contre l'empreinte annoncee avant de prendre sa place.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataPackSyncService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly MameGamelistGroupIndex _gamelistIndex;
    private readonly ILogger<DataPackSyncService> _logger;
    private readonly SemaphoreSlim _unSeul = new(1, 1);

    public DataPackSyncService(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ApiExposeOptions> options,
        MameGamelistGroupIndex gamelistIndex,
        ILogger<DataPackSyncService> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _gamelistIndex = gamelistIndex;
        _logger = logger;
    }

    private static string StatePath => Path.Combine(RetroBatPaths.RuntimeLogRoot, "datapack-sync.json");
    private static string TempRoot => Path.Combine(RetroBatPaths.RuntimeTempRoot, "datapack");

    /// <summary>Le dernier bilan, pour l'endpoint de maintenance.</summary>
    public DataPackSyncResult? Dernier { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delai = Math.Max(5, _options.CurrentValue.DataPack.StartupDelaySeconds);
        try { await Task.Delay(TimeSpan.FromSeconds(delai), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = _options.CurrentValue.DataPack;
            if (opt.Enabled)
            {
                try { await SyncNowAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Data Pack : synchronisation echouee (nouvelle tentative au prochain cycle).");
                }
            }
            try { await Task.Delay(TimeSpan.FromHours(Math.Max(1, opt.IntervalHours)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Une synchronisation maintenant. Une seule a la fois : la seconde attend la premiere.</summary>
    public async Task<DataPackSyncResult> SyncNowAsync(CancellationToken ct)
    {
        await _unSeul.WaitAsync(ct);
        try
        {
            var opt = _options.CurrentValue.DataPack;
            var result = new DataPackSyncResult { StartedUtc = DateTime.UtcNow, Repository = opt.Repository };
            var state = LoadState();
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("APIExpose-DataPackSync");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            try
            {
                await SyncTreeAsync(client, opt, state, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add("arbre : " + ex.Message);
                _logger.LogWarning(ex, "Data Pack : la partie depot a echoue.");
            }
            try
            {
                await SyncGamelistSystemsAsync(client, opt, state, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add("gamelist : " + ex.Message);
                _logger.LogWarning(ex, "Data Pack : la partie gamelist a echoue.");
            }
            try
            {
                await SyncIccardsAsync(client, opt, state, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add("iccards : " + ex.Message);
                _logger.LogWarning(ex, "Data Pack : la partie cartes d'instructions a echoue.");
            }

            state.LastSyncUtc = DateTime.UtcNow;
            SaveState(state);
            result.FinishedUtc = DateTime.UtcNow;
            Dernier = result;
            _logger.LogInformation(
                "Data Pack : {Updated} fichier(s) mis a jour, {Added} ajoute(s), {Unchanged} inchange(s), {Systems} base(s) reprise(s), {Cards} carte(s) posee(s) - {Repo}@{Sha}{Errors}.",
                result.Updated, result.Added, result.Unchanged, result.SystemsUpdated, result.IccardsAdded + result.IccardsReplaced, opt.Repository,
                Short(state.LastTreeSha ?? ""), result.Errors.Count == 0 ? "" : $", {result.Errors.Count} erreur(s)");
            return result;
        }
        finally
        {
            _unSeul.Release();
        }
    }

    // ── Le depot : un blob par fichier ────────────────────────────────────────

    /// <summary>
    /// L'etat retient, par fichier, l'empreinte appliquee ET la taille et la date du fichier
    /// local au moment ou on l'a appliquee : comme l'index de git. Un fichier dont la taille ou
    /// la date a bouge est rehache ; s'il ne dit plus ce que le depot dit, il est repris. Une
    /// modification locale d'un fichier officiel se repare donc au cycle suivant, sans que
    /// personne n'ait a le demander, et sans relire les 25 000 autres.
    /// </summary>
    private async Task SyncTreeAsync(HttpClient client, DataPackOptions opt, SyncState state, DataPackSyncResult result, CancellationToken ct)
    {
        var repo = opt.Repository.Trim();
        var branch = string.IsNullOrWhiteSpace(opt.Branch) ? "main" : opt.Branch.Trim();
        if (repo.Length == 0) return;

        var head = await GetJsonAsync(client, $"https://api.github.com/repos/{repo}/commits/{branch}", ct);
        var sha = head.TryGetProperty("sha", out var shaEl) ? shaEl.GetString() : null;
        if (string.IsNullOrEmpty(sha)) throw new InvalidOperationException("commit HEAD illisible");
        result.TreeSha = sha;

        var dossiers = new HashSet<string>(opt.Folders, StringComparer.OrdinalIgnoreCase);
        // Ce que le depot contient : l'arbre s'il a bouge, sinon ce qu'on en sait deja.
        Dictionary<string, string> attendu;
        if (string.Equals(state.LastTreeSha, sha, StringComparison.Ordinal) && state.Files.Count > 0)
        {
            result.TreeUnchanged = true;
            attendu = state.Files.ToDictionary(kv => kv.Key, kv => kv.Value.Sha, StringComparer.Ordinal);
        }
        else
        {
            var tree = await GetJsonAsync(client, $"https://api.github.com/repos/{repo}/git/trees/{sha}?recursive=1", ct);
            if (tree.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True)
            {
                // Au-dela de 100 000 entrees GitHub coupe l'arbre : on le saurait ici, pas par des fichiers manquants.
                result.Errors.Add("arbre tronque par GitHub : trop d'entrees");
            }
            attendu = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tree.TryGetProperty("tree", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var t) || t.GetString() != "blob") continue;
                    var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    var blob = item.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : "";
                    if (blob.Length == 40 && DataPackPaths.Autorise(path, dossiers)) attendu[path] = blob;
                }
            }
            // Ce qui a quitte le depot quitte l'etat (pas le disque : on n'efface jamais).
            foreach (var k in state.Files.Keys.Where(k => !attendu.ContainsKey(k)).ToList()) state.Files.Remove(k);
        }

        // Les fichiers locaux, taille et date, en une enumeration par dossier : c'est ce qui
        // coute le moins sur une borne, bien moins qu'ouvrir chaque fichier.
        var local = EnumererLocal(dossiers);

        var aVerifier = new List<(string Path, string Sha)>();
        foreach (var (path, blob) in attendu)
        {
            if (state.Files.TryGetValue(path, out var connu)
                && string.Equals(connu.Sha, blob, StringComparison.Ordinal)
                && local.TryGetValue(path, out var stat)
                && stat.Size == connu.Size && stat.Ticks == connu.Ticks)
            {
                result.Unchanged++;
                continue;
            }
            aVerifier.Add((path, blob));
        }

        // Ce qui n'est pas connu tel quel est rehache, DE FRONT : sur une borne, c'est
        // l'ouverture du fichier qui coute (antivirus, disque), pas le hachage ; 25 000
        // fichiers un par un prenaient huit minutes.
        var aFaire = new List<(string Path, string Sha)>();
        var identiques = new List<(string Path, string Sha, Stat Stat)>();
        await Parallel.ForEachAsync(
            aVerifier,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, opt.Parallelism * 4), CancellationToken = ct },
            async (f, token) =>
            {
                var chemin = DataPackPaths.Local(f.Path);
                if (File.Exists(chemin))
                {
                    var localSha = DataPackPaths.GitBlobSha1(await File.ReadAllBytesAsync(chemin, token));
                    if (string.Equals(localSha, f.Sha, StringComparison.Ordinal))
                    {
                        lock (identiques) identiques.Add((f.Path, f.Sha, Stat.De(chemin)));
                        return;
                    }
                }
                lock (aFaire) aFaire.Add(f);
            });
        foreach (var (path, blob, stat) in identiques)
        {
            state.Files[path] = new FileState { Sha = blob, Size = stat.Size, Ticks = stat.Ticks };
            result.Unchanged++;
        }
        aFaire.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));

        if (aFaire.Count > 0)
        {
            _logger.LogInformation("Data Pack : {Count} fichier(s) a reprendre depuis {Repo}@{Sha}.", aFaire.Count, repo, Short(sha));
        }
        Directory.CreateDirectory(TempRoot);
        var touches = new List<string>();
        using var porte = new SemaphoreSlim(Math.Max(1, opt.Parallelism));
        var taches = aFaire.Select(async f =>
        {
            await porte.WaitAsync(ct);
            try
            {
                var escaped = string.Join('/', f.Path.Split('/').Select(Uri.EscapeDataString));
                var octets = await client.GetByteArrayAsync($"https://raw.githubusercontent.com/{repo}/{branch}/{escaped}", ct);
                if (!string.Equals(DataPackPaths.GitBlobSha1(octets), f.Sha, StringComparison.Ordinal))
                {
                    // Ce n'est pas ce que l'arbre annonce (CDN en retard, ou pire) : on ne pose pas.
                    lock (result) result.Errors.Add($"{f.Path} : empreinte differente de l'arbre, ignore");
                    return;
                }
                var chemin = DataPackPaths.Local(f.Path);
                var existait = File.Exists(chemin);
                await EcrireAtomiqueAsync(chemin, octets, ct);
                var stat = Stat.De(chemin);
                lock (result)
                {
                    if (existait) result.Updated++; else result.Added++;
                    state.Files[f.Path] = new FileState { Sha = f.Sha, Size = stat.Size, Ticks = stat.Ticks };
                    touches.Add(f.Path);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lock (result) result.Errors.Add($"{f.Path} : {ex.Message}");
            }
            finally { porte.Release(); }
        });
        await Task.WhenAll(taches);

        // Tout repris sans faute : ce HEAD est acquis, on ne relira plus l'arbre pour lui.
        if (result.Errors.Count == 0) state.LastTreeSha = sha;
        foreach (var t in touches.Where(t => t.StartsWith("gamelist/", StringComparison.OrdinalIgnoreCase)))
        {
            _gamelistIndex.Oublier(DataPackPaths.Local(t));
        }
    }

    /// <summary>Taille et date de chaque fichier local des dossiers du pack, par une enumeration (pas une ouverture).</summary>
    private static Dictionary<string, Stat> EnumererLocal(IEnumerable<string> dossiers)
    {
        var racine = Path.Combine(RetroBatPaths.PluginRoot, "resources");
        var local = new Dictionary<string, Stat>(StringComparer.Ordinal);
        foreach (var d in dossiers)
        {
            var dir = new DirectoryInfo(Path.Combine(racine, d));
            if (!dir.Exists) continue;
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(racine, f.FullName).Replace('\\', '/');
                local[rel] = new Stat(f.Length, f.LastWriteTimeUtc.Ticks);
            }
        }
        return local;
    }

    // ── Les bases par systeme : la release « gamelist » ───────────────────────

    private async Task SyncGamelistSystemsAsync(HttpClient client, DataPackOptions opt, SyncState state, DataPackSyncResult result, CancellationToken ct)
    {
        var repo = opt.Repository.Trim();
        var tag = opt.GamelistReleaseTag.Trim();
        if (repo.Length == 0 || tag.Length == 0) return;
        var baseUrl = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/";

        string manifeste;
        try { manifeste = await client.GetStringAsync(baseUrl + "gamelist-manifest.json", ct); }
        catch (HttpRequestException ex)
        {
            result.Errors.Add("manifeste gamelist : " + ex.Message);
            return;
        }
        using var doc = JsonDocument.Parse(manifeste);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return;

        var seulGamelist = new HashSet<string>(new[] { "gamelist" }, StringComparer.OrdinalIgnoreCase);
        var aFaire = new List<(string Path, string Sha256, string Asset, string AssetSha)>();
        foreach (var prop in files.EnumerateObject())
        {
            var path = "gamelist/" + prop.Name;            // « systems/<sys>_lt.json »
            var sha256 = prop.Value.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "").ToLowerInvariant() : "";
            var asset = prop.Value.TryGetProperty("asset", out var a) ? a.GetString() ?? "" : "";
            var assetSha = prop.Value.TryGetProperty("asset_sha256", out var ah) ? (ah.GetString() ?? "").ToLowerInvariant() : "";
            if (sha256.Length != 64 || asset.Length == 0 || !DataPackPaths.Autorise(path, seulGamelist)) continue;
            if (asset.Contains('/') || asset.Contains('\\') || asset.Contains("..")) continue;

            var chemin = DataPackPaths.Local(path);
            var existe = File.Exists(chemin);
            // Connu, et le fichier n'a pas bouge depuis : rien a lire.
            if (existe && state.Systems.TryGetValue(path, out var connu)
                && string.Equals(connu.Sha, sha256, StringComparison.Ordinal))
            {
                var stat = Stat.De(chemin);
                if (stat.Size == connu.Size && stat.Ticks == connu.Ticks)
                {
                    result.Unchanged++;
                    continue;
                }
            }
            if (existe)
            {
                var localSha = await Sha256Async(chemin, ct);
                if (string.Equals(localSha, sha256, StringComparison.Ordinal))
                {
                    var stat = Stat.De(chemin);
                    state.Systems[path] = new FileState { Sha = sha256, Size = stat.Size, Ticks = stat.Ticks };
                    result.Unchanged++;
                    continue;
                }
            }
            aFaire.Add((path, sha256, asset, assetSha));
        }
        if (aFaire.Count == 0) return;
        _logger.LogInformation("Data Pack : {Count} base(s) par systeme a reprendre.", aFaire.Count);

        Directory.CreateDirectory(TempRoot);
        foreach (var f in aFaire)
        {
            ct.ThrowIfCancellationRequested();
            var archive = Path.Combine(TempRoot, f.Asset);
            var tmp = Path.Combine(TempRoot, f.Asset + ".json.tmp");
            try
            {
                await TelechargerAsync(client, baseUrl + Uri.EscapeDataString(f.Asset), archive, ct);
                if (f.AssetSha.Length == 64 && !string.Equals(await Sha256Async(archive, ct), f.AssetSha, StringComparison.Ordinal))
                {
                    result.Errors.Add($"{f.Asset} : empreinte de l'archive differente du manifeste, ignore");
                    continue;
                }
                await using (var entree = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                await using (var sortie = File.Create(tmp))
                {
                    await entree.CopyToAsync(sortie, ct);
                }
                if (!string.Equals(await Sha256Async(tmp, ct), f.Sha256, StringComparison.Ordinal))
                {
                    result.Errors.Add($"{f.Path} : contenu different du manifeste, ignore");
                    continue;
                }
                var chemin = DataPackPaths.Local(f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
                File.Move(tmp, chemin, overwrite: true);
                var stat = Stat.De(chemin);
                state.Systems[f.Path] = new FileState { Sha = f.Sha256, Size = stat.Size, Ticks = stat.Ticks };
                result.SystemsUpdated++;
                _gamelistIndex.Oublier(chemin);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"{f.Path} : {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    // ── Les cartes d'instructions : la release « iccards » ────────────────────

    /// <summary>
    /// Le pack de cartes d'instructions (un zip de 887 cartes et leurs compagnons de placement)
    /// s'installe dans media/systems/arcade/games/&lt;rom&gt;/artwork/ic/. Repris quand
    /// l'empreinte publiee change, et pose avec la regle de install-iccards.bat, en mieux : une
    /// carte ABSENTE est ajoutee ; une carte que le pack precedent avait posee et que personne
    /// n'a touchee est remplacee ; une carte que le joueur a faite ou modifiee reste la sienne.
    /// On sait laquelle est laquelle par l'empreinte du pack precedent (l'etat, ou a defaut le
    /// zip encore present dans resources/iccards).
    /// </summary>
    private async Task SyncIccardsAsync(HttpClient client, DataPackOptions opt, SyncState state, DataPackSyncResult result, CancellationToken ct)
    {
        var repo = opt.Repository.Trim();
        var tag = opt.IccardsReleaseTag.Trim();
        if (repo.Length == 0 || tag.Length == 0) return;
        var baseUrl = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/";

        string manifeste;
        try { manifeste = await client.GetStringAsync(baseUrl + "iccards-manifest.json", ct); }
        catch (HttpRequestException ex)
        {
            result.Errors.Add("manifeste iccards : " + ex.Message);
            return;
        }
        using var doc = JsonDocument.Parse(manifeste);
        var r = doc.RootElement;
        var sha = r.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "").ToLowerInvariant() : "";
        var asset = r.TryGetProperty("asset", out var a) ? a.GetString() ?? "" : "";
        var version = r.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        if (sha.Length != 64 || asset.Length == 0 || asset.Contains('/') || asset.Contains('\\') || asset.Contains("..")) return;
        if (string.Equals(state.Iccards.Sha, sha, StringComparison.Ordinal))
        {
            result.IccardsUnchanged = true;
            return;
        }

        var dossierPack = Path.Combine(RetroBatPaths.PluginRoot, "resources", "iccards");
        var zipLocal = Path.Combine(dossierPack, "iccards-arcade.zip");
        var racineMedia = Path.Combine(RetroBatPaths.MediaRoot, "systems", "arcade", "games");

        // Ce que le pack PRECEDENT avait pose : l'etat, ou a defaut le zip encore la.
        var ancien = new Dictionary<string, string>(state.Iccards.Files, StringComparer.OrdinalIgnoreCase);
        if (ancien.Count == 0 && File.Exists(zipLocal))
        {
            try
            {
                using var vieux = ZipFile.OpenRead(zipLocal);
                foreach (var e in vieux.Entries)
                {
                    if (e.FullName.EndsWith('/') || e.Length == 0 && e.Name.Length == 0) continue;
                    await using var s = e.Open();
                    ancien[e.FullName.Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Data Pack : l'ancien pack de cartes est illisible, on ne remplacera rien d'existant.");
                ancien.Clear();
            }
        }

        Directory.CreateDirectory(TempRoot);
        var archive = Path.Combine(TempRoot, asset);
        // Le zip publie est peut-etre deja la (pose par l'installeur) : on ne le retelecharge pas,
        // on ne fait que verifier ce qu'il a installe.
        var dejaLa = File.Exists(zipLocal) && string.Equals(await Sha256Async(zipLocal, ct), sha, StringComparison.Ordinal);
        if (dejaLa) archive = zipLocal;
        _logger.LogInformation("Data Pack : pack de cartes d'instructions {Version} a {Action} ({Sha}).", version, dejaLa ? "verifier" : "reprendre", sha[..8]);
        try
        {
            if (!dejaLa)
            {
                await TelechargerAsync(client, baseUrl + Uri.EscapeDataString(asset), archive, ct);
                if (!string.Equals(await Sha256Async(archive, ct), sha, StringComparison.Ordinal))
                {
                    result.Errors.Add("iccards : empreinte du zip differente du manifeste, ignore");
                    return;
                }
            }

            var poses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int ajoutes = 0, remplaces = 0, gardes = 0, inchanges = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = e.FullName.Replace('\\', '/');
                    if (rel.EndsWith('/') || e.Name.Length == 0) continue;
                    if (!DataPackPaths.CheminRelatifSur(rel)) { result.Errors.Add($"iccards : chemin refuse {rel}"); continue; }

                    string empreinte;
                    await using (var s = e.Open())
                    {
                        empreinte = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
                    }
                    poses[rel] = empreinte;

                    var cible = Path.Combine(racineMedia, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(cible))
                    {
                        var locale = await Sha256Async(cible, ct);
                        if (string.Equals(locale, empreinte, StringComparison.Ordinal)) { inchanges++; continue; }
                        // Posee par l'ancien pack et jamais touchee : on remplace. Sinon c'est la sienne.
                        if (!ancien.TryGetValue(rel, out var precedente) || !string.Equals(precedente, locale, StringComparison.Ordinal))
                        {
                            gardes++;
                            continue;
                        }
                        remplaces++;
                    }
                    else
                    {
                        ajoutes++;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
                    var tmp = cible + ".datapack.tmp";
                    await using (var s = e.Open())
                    await using (var f = File.Create(tmp))
                    {
                        await s.CopyToAsync(f, ct);
                    }
                    File.Move(tmp, cible, overwrite: true);
                }
            }

            // Le pack et son manifeste restent dans resources/iccards : install-iccards.bat et
            // l'installeur y comptent, et c'est lui qui dira, la prochaine fois, ce qui etait a lui.
            Directory.CreateDirectory(dossierPack);
            if (!dejaLa) File.Move(archive, zipLocal, overwrite: true);
            try
            {
                var localManifest = new Dictionary<string, object?>
                {
                    ["pack"] = "instruction-cards-arcade",
                    ["version"] = version,
                    ["source"] = r.TryGetProperty("source", out var src) ? src.GetString() : null,
                    ["jeux"] = r.TryGetProperty("jeux", out var j) && j.TryGetInt32(out var nj) ? nj : (int?) null,
                    ["cartes"] = r.TryGetProperty("cartes", out var c) && c.TryGetInt32(out var nc) ? nc : (int?) null,
                    ["cible"] = "media/systems/arcade/games/<rom>/artwork/ic/<role>/",
                };
                await File.WriteAllTextAsync(Path.Combine(dossierPack, "manifest.json"),
                    JsonSerializer.Serialize(localManifest, new JsonSerializerOptions { WriteIndented = true }), ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Data Pack : manifeste local des cartes non ecrit."); }

            state.Iccards = new IccardsState { Sha = sha, Version = version, Files = poses };
            result.IccardsAdded = ajoutes;
            result.IccardsReplaced = remplaces;
            result.IccardsKept = gardes;
            _logger.LogInformation(
                "Data Pack : cartes d'instructions {Version} : {Added} ajoutee(s), {Replaced} remplacee(s), {Kept} gardee(s) (les votres), {Same} deja a jour.",
                version, ajoutes, remplaces, gardes, inchanges);
        }
        finally
        {
            try { if (!dejaLa && File.Exists(archive)) File.Delete(archive); } catch { }
        }
    }

    // ── Outils ────────────────────────────────────────────────────────────────

    private static async Task EcrireAtomiqueAsync(string cible, byte[] octets, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
        var tmp = cible + ".datapack.tmp";
        await File.WriteAllBytesAsync(tmp, octets, ct);
        File.Move(tmp, cible, overwrite: true);
    }

    private static async Task TelechargerAsync(HttpClient client, string url, string cible, CancellationToken ct)
    {
        using var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        await using var entree = await r.Content.ReadAsStreamAsync(ct);
        await using var sortie = File.Create(cible);
        await entree.CopyToAsync(sortie, ct);
    }

    private static async Task<string> Sha256Async(string chemin, CancellationToken ct)
    {
        await using var f = File.OpenRead(chemin);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repond {(int) resp.StatusCode} pour {url}");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private SyncState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath), Json) ?? new SyncState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Data Pack : etat de synchro illisible, on repart des fichiers.");
        }
        return new SyncState();
    }

    private void SaveState(SyncState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Data Pack : etat de synchro non ecrit.");
        }
    }

    /// <summary>
    /// Ce qu'on sait deja : le HEAD acquis, et par fichier l'empreinte appliquee. C'est ce qui
    /// evite de relire 25 000 fichiers a chaque cycle.
    /// </summary>
    private sealed class SyncState
    {
        public string? LastTreeSha { get; set; }
        public DateTime LastSyncUtc { get; set; }
        /// <summary>Les fichiers du depot : chemin -> empreinte de blob appliquee, taille et date locales.</summary>
        public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Les bases par systeme : chemin -> sha256 applique, taille et date locales.</summary>
        public Dictionary<string, FileState> Systems { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Le pack de cartes d'instructions applique, et ce qu'il a pose (pour savoir quoi remplacer la prochaine fois).</summary>
        public IccardsState Iccards { get; set; } = new();
    }

    private sealed class IccardsState
    {
        public string Sha { get; set; } = "";
        public string Version { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FileState
    {
        public string Sha { get; set; } = "";
        public long Size { get; set; }
        public long Ticks { get; set; }
    }

    private readonly record struct Stat(long Size, long Ticks)
    {
        public static Stat De(string chemin)
        {
            var f = new FileInfo(chemin);
            return new Stat(f.Length, f.LastWriteTimeUtc.Ticks);
        }
    }
}

/// <summary>Le bilan d'une synchronisation.</summary>
public sealed class DataPackSyncResult
{
    public string Repository { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string? TreeSha { get; set; }
    public bool TreeUnchanged { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int SystemsUpdated { get; set; }
    public bool IccardsUnchanged { get; set; }
    public int IccardsAdded { get; set; }
    public int IccardsReplaced { get; set; }
    public int IccardsKept { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>Les regles pures du Data Pack : quel chemin est admis, ou il va, comment git l'empreinte.</summary>
public static class DataPackPaths
{
    /// <summary>
    /// Un chemin du depot est admis s'il est relatif, sans remontee, et sous un des dossiers
    /// autorises. Le reste du depot (README, licence, outils) ne s'installe pas.
    /// </summary>
    public static bool Autorise(string path, ISet<string> dossiers)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400) return false;
        if (path.Contains('\\') || path.StartsWith('/') || Path.IsPathRooted(path)) return false;
        var segments = path.Split('/');
        if (segments.Length < 2) return false;
        foreach (var s in segments)
        {
            if (s.Length == 0 || s == "." || s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }
        return dossiers.Contains(segments[0]);
    }

    /// <summary>Un chemin d'archive admis : relatif, sans remontee, sans caractere interdit.</summary>
    public static bool CheminRelatifSur(string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Length > 400 || rel.StartsWith('/') || Path.IsPathRooted(rel)) return false;
        foreach (var s in rel.Split('/'))
        {
            if (s.Length == 0 || s == "." || s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }
        return true;
    }

    public static string Local(string path)
        => Path.Combine(RetroBatPaths.PluginRoot, "resources", path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>L'empreinte d'un blob telle que git la calcule : sha1("blob " + taille + "\0" + octets).</summary>
    public static string GitBlobSha1(ReadOnlySpan<byte> octets)
    {
        var entete = Encoding.ASCII.GetBytes("blob " + octets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(entete);
        sha1.AppendData(octets);
        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }
}
