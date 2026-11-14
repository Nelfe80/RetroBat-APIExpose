using RetroBat.Domain.Models;

namespace RetroBat.Api.Media;

/// <summary>
/// LOT 2 - the shared media QUALIFICATION, lifted out of RomsMediaCanonicalMigrationHostedService
/// so a file can be typed WITHOUT moving it or writing a gamelist. It owns the filename-suffix
/// table and the folder conventions that used to live inside the migration; the migration now
/// delegates here, so there is ONE source of truth for "what kind is this file?".
///
/// Order of certainty (§7.2): an explicit gamelist tag or provider type wins over any of this;
/// what this service covers is the file-based tail - filename convention, then folder convention.
/// A generic gamelist slot (&lt;marquee&gt;…&lt;/marquee&gt;) is NEVER enough on its own to make a
/// Kind: the Kind only comes from qualifying the file the slot points at.
/// </summary>
public sealed class MediaQualificationService
{
    // Suffix -> Kind, in PRIORITY order: the most specific suffix wins, so "-video-normalized" is
    // matched before "-video", "-screenmarqueesmall" before "-screenmarquee" before "-marquee",
    // and every specific box face before "-box". Moved verbatim from the migration; the ORDER is
    // the "suffix precedence" rule and must not be sorted.
    private static readonly (string Suffix, string Kind)[] SuffixKinds =
    [
        ("-video-normalized", MediaKinds.VideoNormalized),
        ("-screenmarqueesmall", MediaKinds.ScreenMarqueeSmall),
        ("-screenmarquee", MediaKinds.ScreenMarquee),
        ("-wheelcarbon", MediaKinds.WheelCarbon),
        ("-wheelsteel", MediaKinds.WheelSteel),
        ("-boxtexture", MediaKinds.BoxTexture),
        ("-steamgrid", MediaKinds.SteamGrid),
        ("-mixrbv1", MediaKinds.MixRbv1),
        ("-mixrbv2", MediaKinds.MixRbv2),
        ("-thumbnail", MediaKinds.Thumbnail),
        ("-screenshot", MediaKinds.Thumbnail),
        ("-titleshot", MediaKinds.Image),
        ("-boxside", MediaKinds.BoxSide),
        ("-figurine", MediaKinds.Figurine),
        ("-cartridge", MediaKinds.Cartridge),
        ("-support2d", MediaKinds.Cartridge),
        ("-supporttexture", MediaKinds.Label),
        ("-support-texture", MediaKinds.Label),
        ("-label", MediaKinds.Label),
        ("-themehb", MediaKinds.ThemeHb),
        ("-marquee", MediaKinds.Marquee),
        ("-boxback", MediaKinds.BoxBack),
        ("-boxfront", MediaKinds.BoxFront),
        ("-box2d", MediaKinds.BoxFront),
        ("-box3d", MediaKinds.Box3d),
        ("-box", MediaKinds.BoxFront),
        ("-fanart", MediaKinds.Fanart),
        ("-bezel", MediaKinds.Bezel),
        ("-image", MediaKinds.Image),
        ("-thumb", MediaKinds.Thumbnail),
        ("-logo", MediaKinds.Logo),
        ("-wheel", MediaKinds.Wheel),
        ("-flyer", MediaKinds.Flyer),
        ("-manual", MediaKinds.Manual),
        ("-magazine", MediaKinds.Magazine),
        ("-video", MediaKinds.Video),
        ("-map", MediaKinds.Map),
        ("-mix", MediaKinds.MixRbv2)
    ];

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".webm"];

    /// <summary>
    /// Type <paramref name="fileStem"/> sitting in a legacy media folder
    /// (<paramref name="legacyFolder"/> = images / videos / manuals / themehb / themes), and
    /// return the base name it projects to. Filename suffix first (filename-convention), then the
    /// folder's default (folder-convention). False when nothing is determinable, or when a suffix
    /// matched a kind the folder does not allow (e.g. a "-video" file under images/). The result is
    /// identical to the migration's former private logic; <paramref name="qualification"/> reports
    /// which source decided it.
    /// </summary>
    public bool TryQualify(
        string legacyFolder,
        string fileStem,
        string extension,
        out string projectionBaseName,
        out string kind,
        out string qualification)
    {
        projectionBaseName = string.Empty;
        kind = string.Empty;
        qualification = string.Empty;

        foreach (var (suffix, candidateKind) in SuffixKinds)
        {
            if (!fileStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsKindAllowedForFolder(candidateKind, legacyFolder))
            {
                return false;
            }

            projectionBaseName = fileStem[..^suffix.Length];
            kind = candidateKind;
            qualification = MediaQualifications.FilenameConvention;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        if (legacyFolder.Equals("themehb", StringComparison.OrdinalIgnoreCase) ||
            legacyFolder.Equals("themes", StringComparison.OrdinalIgnoreCase))
        {
            projectionBaseName = fileStem;
            kind = MediaKinds.ThemeHb;
            qualification = MediaQualifications.FolderConvention;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        if (TryResolveDefaultKind(legacyFolder, extension, out kind))
        {
            projectionBaseName = fileStem;
            qualification = MediaQualifications.FolderConvention;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        return false;
    }

    private static bool TryResolveDefaultKind(string legacyFolder, string extension, out string kind)
    {
        kind = string.Empty;
        var normalizedFolder = legacyFolder.ToLowerInvariant();
        if (normalizedFolder is "videos" && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Video;
            return true;
        }

        if (normalizedFolder is "manuals" && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Manual;
            return true;
        }

        if (normalizedFolder is "images" && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Thumbnail;
            return true;
        }

        return false;
    }

    private static bool IsKindAllowedForFolder(string kind, string legacyFolder)
    {
        return legacyFolder.ToLowerInvariant() switch
        {
            "images" => kind is not MediaKinds.Video and not MediaKinds.VideoNormalized and not MediaKinds.Manual and not MediaKinds.ThemeHb,
            "videos" => kind is MediaKinds.Video or MediaKinds.VideoNormalized,
            "manuals" => kind is MediaKinds.Manual,
            "themehb" or "themes" => kind is MediaKinds.ThemeHb,
            _ => false
        };
    }

    // Durable gamelist tags whose NAME already names a kind (§7.2 source 1: explicit-gamelist).
    // The generic slots image / marquee / thumbnail are deliberately ABSENT (§7.3): they never
    // imply a kind on their own - the kind comes from qualifying the file they point at.
    private static readonly IReadOnlyDictionary<string, string> GamelistTagKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fanart"] = MediaKinds.Fanart,
            ["video"] = MediaKinds.Video,
            ["manual"] = MediaKinds.Manual,
            ["magazine"] = MediaKinds.Magazine,
            ["map"] = MediaKinds.Map,
            ["bezel"] = MediaKinds.Bezel,
            ["cartridge"] = MediaKinds.Cartridge,
            ["boxart"] = MediaKinds.BoxFront,
            ["box"] = MediaKinds.BoxFront,
            ["titleshot"] = MediaKinds.Image,
            ["mix"] = MediaKinds.MixRbv2
        };

    /// <summary>The KIND a DURABLE gamelist tag names on its own (explicit-gamelist). False for the
    /// generic slots image / marquee / thumbnail, which never imply a kind (§7.3).</summary>
    public bool TryQualifyByGamelistTag(string tag, out string kind)
    {
        if (!string.IsNullOrWhiteSpace(tag) && GamelistTagKinds.TryGetValue(tag, out var found))
        {
            kind = found;
            return true;
        }

        kind = string.Empty;
        return false;
    }

    // API-7 (14/11) - the folders a scraper (Skraper, RetroBat) files ONE kind of media in, one file per
    // game named after the rom: roms/<system>/media/fanart/<rom>.png, media/box3d/<rom>.png... The
    // folder names the kind; the generic gamelist slot (<image>, <thumbnail>) that points at the file
    // does not. Mixed legacy folders (images, downloaded_images, thumbnails) are deliberately ABSENT:
    // they hold every kind and only the file name can tell.
    private static readonly IReadOnlyDictionary<string, string> TypeFolderKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["box3d"] = MediaKinds.Box3d, ["box-3d"] = MediaKinds.Box3d, ["3dboxes"] = MediaKinds.Box3d,
            ["box2d"] = MediaKinds.BoxFront, ["box-2d"] = MediaKinds.BoxFront, ["boxfront"] = MediaKinds.BoxFront,
            ["box-front"] = MediaKinds.BoxFront, ["covers"] = MediaKinds.BoxFront, ["boxart"] = MediaKinds.BoxFront,
            ["boxback"] = MediaKinds.BoxBack, ["box-back"] = MediaKinds.BoxBack, ["backcovers"] = MediaKinds.BoxBack,
            ["boxside"] = MediaKinds.BoxSide, ["box-side"] = MediaKinds.BoxSide,
            ["boxtexture"] = MediaKinds.BoxTexture, ["box-texture"] = MediaKinds.BoxTexture,
            ["fanart"] = MediaKinds.Fanart, ["fanarts"] = MediaKinds.Fanart,
            ["screenshot"] = MediaKinds.Thumbnail, ["screenshots"] = MediaKinds.Thumbnail, ["ss"] = MediaKinds.Thumbnail,
            ["snap"] = MediaKinds.Thumbnail, ["snaps"] = MediaKinds.Thumbnail,
            ["titlescreen"] = MediaKinds.Image, ["titlescreens"] = MediaKinds.Image, ["sstitle"] = MediaKinds.Image,
            ["screenshottitle"] = MediaKinds.Image, ["titleshot"] = MediaKinds.Image, ["titles"] = MediaKinds.Image,
            ["wheel"] = MediaKinds.Wheel, ["wheels"] = MediaKinds.Wheel, ["logo"] = MediaKinds.Wheel, ["logos"] = MediaKinds.Wheel,
            ["wheelcarbon"] = MediaKinds.WheelCarbon, ["wheel-carbon"] = MediaKinds.WheelCarbon,
            ["wheelsteel"] = MediaKinds.WheelSteel, ["wheel-steel"] = MediaKinds.WheelSteel,
            ["marquee"] = MediaKinds.Marquee, ["marquees"] = MediaKinds.Marquee,
            ["screenmarquee"] = MediaKinds.ScreenMarquee, ["screenmarqueesmall"] = MediaKinds.ScreenMarqueeSmall,
            ["steamgrid"] = MediaKinds.SteamGrid,
            ["mix"] = MediaKinds.MixRbv2, ["mixrbv2"] = MediaKinds.MixRbv2, ["miximage"] = MediaKinds.MixRbv2,
            ["miximages"] = MediaKinds.MixRbv2, ["mixrbv1"] = MediaKinds.MixRbv1,
            ["cartridge"] = MediaKinds.Cartridge, ["cartridges"] = MediaKinds.Cartridge, ["support"] = MediaKinds.Cartridge,
            ["support2d"] = MediaKinds.Cartridge, ["label"] = MediaKinds.Label, ["supporttexture"] = MediaKinds.Label,
            ["flyer"] = MediaKinds.Flyer, ["flyers"] = MediaKinds.Flyer,
            ["figurine"] = MediaKinds.Figurine, ["figurines"] = MediaKinds.Figurine,
            ["bezel"] = MediaKinds.Bezel, ["bezels"] = MediaKinds.Bezel,
            ["map"] = MediaKinds.Map, ["maps"] = MediaKinds.Map,
            ["manual"] = MediaKinds.Manual, ["manuals"] = MediaKinds.Manual,
            ["magazine"] = MediaKinds.Magazine, ["magazines"] = MediaKinds.Magazine,
            ["video"] = MediaKinds.Video, ["videos"] = MediaKinds.Video,
            ["videonormalized"] = MediaKinds.VideoNormalized, ["video-normalized"] = MediaKinds.VideoNormalized,
        };

    /// <summary>The type folders (API-7), for a caller that looks for a game's files in them.</summary>
    public static IEnumerable<string> TypeFolderNames => TypeFolderKinds.Keys;

    /// <summary>
    /// The KIND a type folder names (API-7): <c>media/box3d/&lt;rom&gt;.png</c> is a 3D box whatever gamelist slot
    /// points at it. The extension must suit the kind (a video kind wants a video file, a manual a PDF, every other kind
    /// an image). False for a mixed folder (images, downloaded_images) or an unknown one.
    /// </summary>
    public static bool TryQualifyByTypeFolder(string? folderName, string? extension, out string kind)
    {
        kind = string.Empty;
        if (string.IsNullOrWhiteSpace(folderName) || !TypeFolderKinds.TryGetValue(folderName.Trim(), out var found)) return false;
        var ext = extension ?? string.Empty;
        var suits = found switch
        {
            MediaKinds.Video or MediaKinds.VideoNormalized => VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase),
            MediaKinds.Manual or MediaKinds.Magazine => ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                || ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase),
            _ => ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase),
        };
        if (!suits) return false;
        kind = found;
        return true;
    }

    /// <summary>The KIND a file name suffix implies, IGNORING the folder - for a gamelist medium
    /// that can live in any folder (downloaded_images, media, …) where the tag is the authority and
    /// the file name is only a secondary signal. Shares the same suffix table (and its precedence)
    /// as <see cref="TryQualify"/>. False when no known suffix matches.</summary>
    public bool TryQualifyByFilename(string fileStem, out string kind)
    {
        foreach (var (suffix, candidateKind) in SuffixKinds)
        {
            if (fileStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidateKind;
                return true;
            }
        }

        kind = string.Empty;
        return false;
    }
}
