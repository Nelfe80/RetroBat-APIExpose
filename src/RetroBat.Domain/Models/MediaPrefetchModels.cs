namespace RetroBat.Domain.Models;

public static class MediaKinds
{
    public const string LegacyScreenMarquee = "screenmarquee";
    public const string LegacyBoxBack = "boxback";
    public const string Image = "image";
    public const string Thumbnail = "thumbnail";
    public const string Logo = "logo";
    public const string Wheel = "wheel";
    public const string WheelCarbon = "wheel-carbon";
    public const string WheelSteel = "wheel-steel";
    public const string Marquee = "marquee";
    public const string ScreenMarquee = "screen-marquee";
    public const string ScreenMarqueeSmall = "screen-marquee-small";
    public const string SteamGrid = "steamgrid";
    public const string MixRbv1 = "mixrbv1";
    public const string MixRbv2 = "mixrbv2";
    public const string BoxFront = "box-front";
    public const string BoxSide = "box-side";
    public const string BoxTexture = "box-texture";
    public const string Box3d = "box-3d";
    public const string Cartridge = "cartridge";
    public const string Label = "label";
    public const string Fanart = "fanart";
    public const string Flyer = "flyer";
    public const string Figurine = "figurine";
    public const string Bezel = "bezel";
    public const string BoxBack = "box-back";
    public const string Map = "map";
    public const string Manual = "manual";
    public const string Magazine = "magazine";
    public const string Video = "video";
    public const string VideoNormalized = "video-normalized";
    public const string ThemeHb = "themehb";

    public static string Normalize(string? kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "sstitle" => Image,
            "screentitle" => Image,
            "screen-title" => Image,
            "title" => Image,
            "ss" => Thumbnail,
            "screenshot" => Thumbnail,
            "thumb" => Thumbnail,
            LegacyScreenMarquee => ScreenMarquee,
            "screen_marquee" => ScreenMarquee,
            "screenmarqueesmall" => ScreenMarqueeSmall,
            "screen-marquee-small" => ScreenMarqueeSmall,
            "screen_marquee_small" => ScreenMarqueeSmall,
            "mix" => MixRbv2,
            "mix-rbv1" => MixRbv1,
            "mix_rbv1" => MixRbv1,
            "mix-rbv2" => MixRbv2,
            "mix_rbv2" => MixRbv2,
            "cart" => Cartridge,
            "support-2d" => Cartridge,
            "support2d" => Cartridge,
            "support-texture" => Label,
            "supporttexture" => Label,
            "video_normalized" => VideoNormalized,
            "theme-hb" => ThemeHb,
            "theme_hb" => ThemeHb,
            LegacyBoxBack => BoxBack,
            "box_back" => BoxBack,
            _ => normalized
        };
    }

    // Kinds carried by the physical media store that predate this vocabulary. They
    // were named by the WebSocket snapshot's own fields; naming them here lets an
    // asset table speak one language instead of two.
    public const string GeneratedMarquee = "generated-marquee";
    public const string GeneratedDmd = "generated-dmd";
    public const string Dmd = "dmd";
    public const string DmdAnimation = "dmd-animation";
    public const string Topper = "topper";
    public const string InstructionCard = "instruction-card";

    /// <summary>
    /// The directories <see cref="FromRelativePath"/> can qualify a file in, RELATIVE to
    /// a game or system media root (<c>""</c> is the root itself, where video lives).
    ///
    /// This is the single source of truth for a TARGETED media inventory: instead of
    /// walking a whole subtree (and everything under <c>games/</c>) then discarding what
    /// does not qualify, the inventory lists exactly these directories. It therefore MUST
    /// move in lock-step with the switch in <see cref="FromRelativePath"/> - a qualifier
    /// case with no directory here would make its media silently vanish from snapshots.
    /// A guard test asserts every entry qualifies, and the FromRelativePath
    /// characterisation tests prove nothing qualifies OUTSIDE this set.
    /// </summary>
    public static readonly IReadOnlySet<string> RecognisedMediaDirectories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "",
            "artwork",
            "artwork/box",
            "artwork/mix",
            "artwork/bezels",
            "artwork/marquee",
            "artwork/ic",
            "ui",
            "ui/wheels",
            "ui/logos",
            "documents",
            "documents/maps",
            "themes",
        };

    /// <summary>
    /// The kind a file stands for, from its path RELATIVE to a game or system media
    /// root ("artwork/box/3d.png" → box-3d). Naming by file stem alone cannot do this:
    /// "3d.png" and "front.png" only mean something under "artwork/box/", and
    /// "marquee.png" under "artwork/marquee/" is not the same thing as a folder called
    /// marquee. Null when the file is not a recognised medium - the caller publishes
    /// nothing rather than inventing a name.
    /// </summary>
    public static string? FromRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var path = relativePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..slash];
        var file = slash < 0 ? path : path[(slash + 1)..];
        var dot = file.LastIndexOf('.');
        var stem = dot < 0 ? file : file[..dot];

        return directory switch
        {
            "artwork" => stem switch
            {
                "fanart" => Fanart,
                "flyer" => Flyer,
                "screenshot" => Thumbnail,
                "screentitle" => Image,
                "steamgrid" => SteamGrid,
                "cartridge" => Cartridge,
                "label" => Label,
                "figurine" => Figurine,
                _ => VariantOf(stem, "screentitle", Image) ?? VariantOf(stem, "fanart", Fanart)
            },
            "artwork/box" => stem switch
            {
                "3d" => Box3d,
                "front" => BoxFront,
                "back" => BoxBack,
                "side" => BoxSide,
                "texture" => BoxTexture,
                _ => VariantOf(stem, "front", BoxFront)
            },
            "artwork/mix" => stem switch
            {
                "mixrbv1" => MixRbv1,
                "mixrbv2" => MixRbv2,
                _ => null
            },
            "artwork/bezels" => stem == "bezel" ? Bezel : null,
            "artwork/marquee" => stem switch
            {
                "marquee" => Marquee,
                "screenmarquee" => ScreenMarquee,
                "screenmarquee-small" => ScreenMarqueeSmall,
                "topper" => Topper,
                "dmd" => Dmd,
                "generated-marquee" or "generated-system-marquee" => GeneratedMarquee,
                "generated-dmd" or "generated-system-dmd" => GeneratedDmd,
                _ => stem.StartsWith("dmd", StringComparison.Ordinal) ? DmdAnimation : null
            },
            "artwork/ic" => stem == "ic" || stem.StartsWith("ic-", StringComparison.Ordinal)
                ? InstructionCard
                : null,
            "ui" => stem == "steamgrid" ? SteamGrid : null,
            "ui/wheels" => stem switch
            {
                "wheel" => Wheel,
                "wheel-carbon" => WheelCarbon,
                "wheel-steel" => WheelSteel,
                _ => VariantOf(stem, "wheel", Wheel)
            },
            "ui/logos" => stem == "logo" ? Logo : null,
            "documents" => stem == "manual" ? Manual : null,
            "documents/maps" => stem == "map" ? Map : null,
            "themes" => stem == "themehb" ? ThemeHb : null,
            "" => stem switch
            {
                "video" => Video,
                "video-normalized" => VideoNormalized,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>
    /// A regional or styled variant keeps its suffix as its own kind ("front-us" →
    /// "box-front-us"): merging it into the base would let a consumer show a US box for
    /// a European game without ever being told. Consumers that only know the base kind
    /// simply do not see it.
    /// </summary>
    private static string? VariantOf(string stem, string baseStem, string baseKind)
        => stem.Length > baseStem.Length + 1
           && stem.StartsWith(baseStem + "-", StringComparison.Ordinal)
            ? baseKind + stem[baseStem.Length..]
            : null;
}

public class MediaPrefetchRequest
{
    public string SystemId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public GameDetails? Details { get; set; }
}

public class MediaNeed
{
    public string Kind { get; set; } = string.Empty;
    public bool IsMissing { get; set; }
    public string InitialExistingPath { get; set; } = string.Empty;
    public string ExistingPath { get; set; } = string.Empty;
    public string TargetRelativePath { get; set; } = string.Empty;
    public string ImportedPath { get; set; } = string.Empty;
    public string ProjectedPath { get; set; } = string.Empty;
    public bool WasImported { get; set; }
    public bool WasProjected { get; set; }
    public bool WasContentChanged { get; set; }

    /// <summary>
    /// Le store d'un AUTRE systeme d'arcade qui fournit ce media (« arcade » quand un jeu
    /// neogeo reprend ce que mame et fbneo ont deja). Renseigne, il dit que le fichier ne doit
    /// pas etre recopie dans le store du systeme courant : la fiche le designe la ou il est.
    /// Vide dans tous les autres cas.
    /// </summary>
    public string SharedFromSystemId { get; set; } = string.Empty;
}

public class MediaProjectionPlan
{
    public string SystemId { get; set; } = string.Empty;
    public string FrontendSystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public string TextSourceGameSlug { get; set; } = string.Empty;

    /// <summary>
    /// Le store d'un AUTRE systeme d'arcade qui fournit les textes de cette fiche, quand le
    /// sien n'en a pas encore (un jeu de mame dont la description vit sous cps1). Vide sinon,
    /// et c'est alors <see cref="SystemId"/> qui sert.
    ///
    /// Ce repli ne remplace pas le scrap : il fait voir tout de suite ce qui est deja la, le
    /// scrap continue en fond pour le texte propre au systeme, et ce dernier reprend la main
    /// des la visite suivante.
    /// </summary>
    public string TextSourceSystemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string ProjectionBaseName { get; set; } = string.Empty;
    public string PreferredImageSource { get; set; } = string.Empty;
    public string PreferredLogoSource { get; set; } = string.Empty;
    public string PreferredThumbnailSource { get; set; } = string.Empty;
    public bool IsArcadeLike { get; set; }
    public bool IsFolderBasedSystem { get; set; }
    public bool SkipCrcComputation { get; set; }
    public bool IsFilteredArcadeBiosCandidate { get; set; }
    public bool NeedsDescriptionScrape { get; set; }
    public bool IgnoreRemoteScrapeCooldown { get; set; }
    public bool GamePathExists { get; set; }
    public string GamelistMd5 { get; set; } = string.Empty;
    public string GamelistCrc32 { get; set; } = string.Empty;
    public string GamelistPath { get; set; } = string.Empty;
    public string EsGameId { get; set; } = string.Empty;
    public string ScreenScraperGameId { get; set; } = string.Empty;
    public List<string> RomRegions { get; set; } = new();
    public List<string> RomLanguages { get; set; } = new();
    public bool SuppressImmediateGamelistUpdates { get; set; }
    public List<MediaNeed> Needs { get; set; } = new();
}

public class MediaPrefetchResult
{
    public string SystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public bool QueuedRemoteScrape { get; set; }
    public bool IsArcadeLike { get; set; }
    public bool IsFolderBasedSystem { get; set; }
    public bool SkipCrcComputation { get; set; }
    public bool IsFilteredArcadeBiosCandidate { get; set; }
    public bool GamePathExists { get; set; }
    public string GamelistMd5 { get; set; } = string.Empty;
    public string GamelistCrc32 { get; set; } = string.Empty;
    public List<MediaNeed> Needs { get; set; } = new();
}
