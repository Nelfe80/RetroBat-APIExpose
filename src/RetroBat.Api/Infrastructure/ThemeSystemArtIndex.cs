namespace RetroBat.Api.Infrastructure;

/// <summary>
/// A once-built, in-memory index of the SYSTEM logo and fanart shipped by the
/// EmulationStation themes - the active theme first, then <c>es-theme-carbon</c> as a
/// guaranteed fallback. It replaces a per-system-selected glob over the themes (dozens of
/// <c>Directory.Exists</c> / <c>Directory.EnumerateFiles</c> calls against a fixed, shipped
/// set of files) with an O(1) dictionary lookup, built lazily on first use and cached for
/// the process.
///
/// Carbon's confirmed layout is <c>art/logos/&lt;name&gt;.(svg|png)</c> and
/// <c>art/background/&lt;name&gt;.jpg</c>, named by the ES system id. A small alias table
/// bridges the few RetroBat system ids that differ from carbon's file names; logos prefer
/// the <c>-w</c> (white) variant; collections (auto-verticalarcade, auto-favorites,
/// lightgun, custom-collections…) carry a UI-language suffix (<c>-fr</c>, <c>-de</c>…) with
/// the base name as the English fallback. Fanart has neither white nor language variants.
///
/// The COLLECTIONS sub-folders (<c>art/logos/collections</c>, <c>art/background/collections</c>) come after the system
/// folders of the same theme (brief du 13/11, point 1.5) : that is where EsCollectionThemeAssets puts the logo and the
/// background of the NelfePlay collections (nelfeplay-scoring, nelfeplay-challenges, nelfeplay-multiplayer). Without them
/// the World Scoring logo was never found in the theme, and the API cache of the logo became its own source.
/// </summary>
internal sealed class ThemeSystemArtIndex
{
    /// <summary>RetroBat system id → carbon file base name, for the few that differ.</summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["jaguar"] = "atarijaguar",
            ["jaguarcd"] = "atarijaguarcd",
            ["lynx"] = "atarilynx",
            ["wswan"] = "wonderswan",
            ["wswanc"] = "wonderswancolor",
            ["gamecube"] = "gc",
            ["oricatmos"] = "oric",
            ["sg1000"] = "sg-1000",
            ["gw"] = "gameandwatch",
            ["dos"] = "exodos",
        };

    private readonly IReadOnlyList<ThemeArt> _themes; // priority order: active theme first, carbon last

    private ThemeSystemArtIndex(IReadOnlyList<ThemeArt> themes) => _themes = themes;

    /// <summary>Builds an index over the given theme roots, in priority order (each root is a
    /// <c>themes/&lt;name&gt;</c> directory - the active theme, then es-theme-carbon). The
    /// caller (the projection) provides the roots, so this type never reads es_settings.</summary>
    internal static ThemeSystemArtIndex Build(IEnumerable<string> themeRootsInPriorityOrder)
    {
        var themes = new List<ThemeArt>();
        foreach (var themeRoot in themeRootsInPriorityOrder)
        {
            themes.Add(new ThemeArt(
                IndexDirectory(Path.Combine(themeRoot, "art", "logos")),
                IndexDirectory(Path.Combine(themeRoot, "art", "background")),
                IndexDirectory(Path.Combine(themeRoot, "art", "logos", "collections")),
                IndexDirectory(Path.Combine(themeRoot, "art", "background", "collections"))));
        }

        return new ThemeSystemArtIndex(themes);
    }

    /// <summary>Logo path for a system/collection. The caller supplies candidate names in
    /// priority order (e.g. the specific frontend id, the es_systems theme, the normalised
    /// id, the display name); each is alias-mapped, then tried with the <c>-w</c> white
    /// preference and the collection language suffix. Null when no theme carries one.</summary>
    public string? ResolveLogoPath(IReadOnlyList<string?> names, string? language)
    {
        foreach (var theme in _themes)
        {
            // The system folder first, for every name ; the collections folder only after.
            foreach (var logos in new[] { theme.Logos, theme.CollectionLogos })
            {
                foreach (var name in AliasedDistinct(names))
                {
                    foreach (var key in LogoKeys(name, language))
                    {
                        if (logos.TryGetValue(key, out var path))
                        {
                            return path;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Fanart (background) path - candidate names in priority order, no white
    /// variant, no language. Null when no theme carries one.</summary>
    public string? ResolveFanartPath(IReadOnlyList<string?> names)
    {
        foreach (var theme in _themes)
        {
            foreach (var fanart in new[] { theme.Fanart, theme.CollectionFanart })
            {
                foreach (var name in AliasedDistinct(names))
                {
                    if (fanart.TryGetValue(name, out var path))
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The caller's candidate names, alias-mapped and de-duplicated, order preserved.</summary>
    private static IEnumerable<string> AliasedDistinct(IReadOnlyList<string?> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var id = (raw ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                continue;
            }

            var name = Aliases.TryGetValue(id, out var alias) ? alias : id;
            if (seen.Add(name))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> LogoKeys(string name, string? language)
    {
        var lang = (language ?? string.Empty).Trim();
        if (lang.Length > 0)
        {
            yield return name + "-" + lang; // localized collection (auto-verticalarcade-fr)
        }

        yield return name + "-w"; // white system variant (mame-w)
        yield return name;        // base
    }

    private static IReadOnlyDictionary<string, string> IndexDirectory(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return map;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                // First file wins for a given stem; the consumer converts svg to png anyway,
                // so a png/svg pair collapses to whichever the directory yields first.
                if (!string.IsNullOrEmpty(stem) && !map.ContainsKey(stem))
                {
                    map[stem] = path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable theme directory: behave as absent.
        }

        return map;
    }

    private sealed record ThemeArt(
        IReadOnlyDictionary<string, string> Logos,
        IReadOnlyDictionary<string, string> Fanart,
        IReadOnlyDictionary<string, string> CollectionLogos,
        IReadOnlyDictionary<string, string> CollectionFanart);
}
