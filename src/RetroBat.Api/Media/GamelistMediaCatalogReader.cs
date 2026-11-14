using System.Collections.Concurrent;
using System.Xml.Linq;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>LOT 3 - what the user gamelist knows about ONE game's media: the raw bindings
/// (slot → file) and the qualified candidates (file → kind + provenance).</summary>
public sealed record GamelistGameMedia(
    IReadOnlyList<MediaBinding> Bindings,
    IReadOnlyList<QualifiedMediaCandidate> Candidates);

/// <summary>
/// LOT 3 - reads a user gamelist as a MEDIA source, so a <c>game-selected</c> can get its media
/// from <c>roms/&lt;system&gt;/gamelist.xml</c> without an <c>/systems/{system}/games</c> call. It is
/// READ-ONLY (never writes the XML), cached per system on the gamelist's path + mtime + length, and
/// indexed by the game's NORMALIZED rom path (not the basename). It reuses <see cref="IGamelistStore"/>
/// to load and <see cref="MediaQualificationService"/> to type the referenced files:
///  - every media tag becomes a <see cref="MediaBinding"/> (slot → resolved, existing file);
///  - a durable tag names a kind on its own (explicit-gamelist);
///  - the file name may add a kind (filename-convention) - a generic slot alone never does (§7.3).
/// Relative media paths resolve against the gamelist folder and carry PathRoot "retrobat".
/// </summary>
public sealed class GamelistMediaCatalogReader
{
    // The media tags an ES gamelist can carry. The generic slots (image/marquee/thumbnail) are here
    // too - they still produce a Binding; they just never imply a Kind on their own.
    private static readonly string[] MediaTags =
    [
        "image", "marquee", "thumbnail", "fanart", "video", "boxart", "box",
        "manual", "magazine", "map", "bezel", "cartridge", "mix", "titleshot"
    ];

    private readonly IGamelistStore _gamelistStore;
    private readonly MediaQualificationService _qualification;
    private readonly ILogger<GamelistMediaCatalogReader>? _logger;

    private sealed record SystemCache(
        DateTime MtimeUtc,
        long Length,
        IReadOnlyDictionary<string, GamelistGameMedia> ByRomPath);

    private readonly ConcurrentDictionary<string, SystemCache> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GamelistMediaCatalogReader(
        IGamelistStore gamelistStore,
        MediaQualificationService qualification,
        ILogger<GamelistMediaCatalogReader>? logger = null)
    {
        _gamelistStore = gamelistStore;
        _qualification = qualification;
        _logger = logger;
    }

    /// <summary>The media the gamelist declares for the game at <paramref name="romPath"/> in
    /// <paramref name="systemId"/>, or null when the system has no gamelist or the game is absent.</summary>
    public GamelistGameMedia? GetGameMedia(string systemId, string romPath)
    {
        var index = GetSystemIndex(systemId);
        var media = index != null && index.TryGetValue(NormalizeRomPath(systemId, romPath), out var found)
            ? found
            : null;
        // API-7 : les fichiers des dossiers de type que la gamelist ne cite pas (fanart de Skraper, souvent sans balise)
        var dansLesDossiers = TypeFolderCandidates(Path.Combine(RetroBatPaths.RomsRoot, systemId), romPath, media);
        if (dansLesDossiers.Count == 0) return media;
        return media is null
            ? new GamelistGameMedia(Array.Empty<MediaBinding>(), dansLesDossiers)
            : new GamelistGameMedia(media.Bindings, media.Candidates.Concat(dansLesDossiers).ToList());
    }

    private static readonly string[] TypeFolderExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".mp4", ".mkv", ".avi", ".webm", ".pdf"];
    private static readonly TimeSpan TypeFolderListingTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (DateTime AtUtc, IReadOnlyList<string> Folders)> _typeFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// API-7 - the files a scraper filed by kind next to the roms, <c>roms/&lt;system&gt;/media/&lt;type&gt;/&lt;rom&gt;.&lt;ext&gt;</c>,
    /// typed by their folder (folder-convention), whether or not the gamelist references them. A file the gamelist
    /// already bound is not repeated. The list of type folders of a system is cached a minute; each game then costs a
    /// handful of existence checks. Read-only.
    /// </summary>
    internal IReadOnlyList<QualifiedMediaCandidate> TypeFolderCandidates(string systemRoot, string? romPath, GamelistGameMedia? already)
    {
        var stem = string.IsNullOrWhiteSpace(romPath) ? string.Empty : Path.GetFileNameWithoutExtension(romPath.Trim().TrimEnd('/', '\\'));
        if (stem.Length == 0) return Array.Empty<QualifiedMediaCandidate>();

        var mediaRoot = Path.Combine(systemRoot, "media");
        var folders = TypeFoldersOf(mediaRoot);
        if (folders.Count == 0) return Array.Empty<QualifiedMediaCandidate>();

        var bound = new HashSet<string>(
            (already?.Candidates ?? []).Select(c => c.Asset.Path)
                .Concat((already?.Bindings ?? []).Select(b => b.Asset.Path)),
            StringComparer.OrdinalIgnoreCase);
        var found = new List<QualifiedMediaCandidate>();
        foreach (var folder in folders)
        {
            foreach (var ext in TypeFolderExtensions)
            {
                var file = Path.Combine(mediaRoot, folder, stem + ext);
                if (!MediaQualificationService.TryQualifyByTypeFolder(folder, ext, out var kind) || !File.Exists(file)) continue;
                var asset = ToAssetRef(file);
                if (bound.Contains(asset.Path)) continue;
                found.Add(new QualifiedMediaCandidate(kind, asset, MediaQualifications.FolderConvention, 70, null, null, null, false));
                break;   // one file per kind and game: the first extension wins
            }
        }

        return found;
    }

    private IReadOnlyList<string> TypeFoldersOf(string mediaRoot)
    {
        if (_typeFolders.TryGetValue(mediaRoot, out var cached) && DateTime.UtcNow - cached.AtUtc < TypeFolderListingTtl)
        {
            return cached.Folders;
        }

        IReadOnlyList<string> folders;
        try
        {
            folders = Directory.Exists(mediaRoot)
                ? Directory.EnumerateDirectories(mediaRoot, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && MediaQualificationService.TypeFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .Select(name => name!)
                    .ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folders = Array.Empty<string>();
        }

        _typeFolders[mediaRoot] = (DateTime.UtcNow, folders);
        return folders;
    }

    private IReadOnlyDictionary<string, GamelistGameMedia>? GetSystemIndex(string systemId)
    {
        var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
        FileInfo info;
        try
        {
            info = new FileInfo(gamelistPath);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_cache.TryGetValue(systemId, out var cached)
            && cached.MtimeUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.ByRomPath;
        }

        var index = BuildIndex(systemId, gamelistPath);
        _cache[systemId] = new SystemCache(info.LastWriteTimeUtc, info.Length, index);
        return index;
    }

    private IReadOnlyDictionary<string, GamelistGameMedia> BuildIndex(string systemId, string gamelistPath)
    {
        var byRomPath = new Dictionary<string, GamelistGameMedia>(StringComparer.OrdinalIgnoreCase);

        XDocument? doc;
        try
        {
            doc = _gamelistStore.Load(gamelistPath, LoadOptions.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Gamelist media catalog: unable to load {GamelistPath}.", gamelistPath);
            return byRomPath;
        }

        if (doc?.Root == null) return byRomPath;

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        foreach (var game in doc.Root.Elements("game"))
        {
            var rawPath = game.Element("path")?.Value;
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            var media = ExtractGameMedia(game, systemRoot, _qualification);
            if (media != null)
            {
                byRomPath[NormalizeAbsolute(ResolveRelative(systemRoot, rawPath))] = media;
            }
        }

        return byRomPath;
    }

    /// <summary>The media a single &lt;game&gt; element declares, resolved against
    /// <paramref name="systemRoot"/> and typed via <paramref name="qualification"/>. Internal +
    /// static so the extraction can be tested without a real gamelist or the roms root. Only files
    /// that exist are bound (read-only); null when the entry declares no present media.</summary>
    internal static GamelistGameMedia? ExtractGameMedia(
        XElement game, string systemRoot, MediaQualificationService qualification)
    {
        var bindings = new List<MediaBinding>();
        var candidates = new List<QualifiedMediaCandidate>();

        foreach (var tag in MediaTags)
        {
            var relative = game.Element(tag)?.Value;
            if (string.IsNullOrWhiteSpace(relative)) continue;

            var absolute = ResolveRelative(systemRoot, relative);
            if (!File.Exists(absolute)) continue; // read-only: only bind media that is really there

            var asset = ToAssetRef(absolute);
            bindings.Add(new MediaBinding(tag, asset, tag, ManagedByApiExpose: false));

            if (qualification.TryQualifyByGamelistTag(tag, out var tagKind))
            {
                candidates.Add(new QualifiedMediaCandidate(
                    tagKind, asset, MediaQualifications.ExplicitGamelist, 100, null, null, null, true));
            }

            // API-7 : un fichier range dans un dossier de type (media/box3d/<rom>.png) prend le type du dossier, quel que soit
            // le creneau qui le designe ; son nom n'y ajoute rien (un « -thumb » dans box3d/ reste une boite 3D).
            if (MediaQualificationService.TryQualifyByTypeFolder(
                    Path.GetFileName(Path.GetDirectoryName(absolute)), Path.GetExtension(absolute), out var folderKind))
            {
                candidates.Add(new QualifiedMediaCandidate(
                    folderKind, asset, MediaQualifications.FolderConvention, 80, null, null, null, true));
            }
            else if (qualification.TryQualifyByFilename(Path.GetFileNameWithoutExtension(absolute), out var fileKind))
            {
                candidates.Add(new QualifiedMediaCandidate(
                    fileKind, asset, MediaQualifications.FilenameConvention, 60, null, null, null, true));
            }
        }

        return bindings.Count > 0 ? new GamelistGameMedia(bindings, candidates) : null;
    }

    private static MediaAssetRef ToAssetRef(string absolute)
    {
        FileInfo info;
        try { info = new FileInfo(absolute); }
        catch { info = null!; }

        // Gamelist media lives under the RetroBat root (roms/…): PathRoot "retrobat", path relative
        // to it - the HP5 contract, so MarqueeManager resolves it against the right root.
        var relative = IsUnderRoot(absolute, RetroBatPaths.RetroBatRoot)
            ? Path.GetRelativePath(RetroBatPaths.RetroBatRoot, absolute).Replace('\\', '/')
            : absolute.Replace('\\', '/');

        return new MediaAssetRef(
            relative,
            "retrobat",
            "user",
            Url: null,
            Length: info is { Exists: true } ? info.Length : null,
            LastWriteTimeUtc: info is { Exists: true } ? info.LastWriteTimeUtc : null);
    }

    private static string ResolveRelative(string systemRoot, string relative)
    {
        var cleaned = relative.Replace('\\', '/').TrimStart('.', '/');
        return Path.GetFullPath(Path.Combine(systemRoot, cleaned));
    }

    private static string NormalizeRomPath(string systemId, string romPath)
    {
        var absolute = Path.IsPathRooted(romPath)
            ? romPath
            : ResolveRelative(Path.Combine(RetroBatPaths.RomsRoot, systemId), romPath);
        return NormalizeAbsolute(absolute);
    }

    private static string NormalizeAbsolute(string path)
    {
        try { return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant(); }
        catch { return path.Replace('\\', '/').ToLowerInvariant(); }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            return Path.GetFullPath(path)
                .StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
