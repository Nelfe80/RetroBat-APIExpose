using System.Text.RegularExpressions;
using RetroBat.Api.Replay.Runtime;

namespace RetroBat.Api.Deck;

/// <summary>
/// CE QUE RETROARCH A DIT AU LANCEMENT, lu dans son journal (<c>es_launch_stdout.log</c>, ecrit par RetroArch lui-meme
/// avec <c>--log-file</c>, ligne a ligne, pendant la partie). Le Deck en tire l'identite technique du jeu avant le
/// premier signal du .MEM : coeur charge, geometrie et forme de l'image annoncees par le coeur
/// (<c>retro_get_system_av_info</c>), rotation demandee (jeu vertical), et le fichier de remappage que RetroArch a
/// reellement applique.
///
/// Pur : la lecture du fichier est faite par l'appelant, ce qui permet de tester sur des journaux reels de la borne.
/// La geometrie et la rotation reprennent les lecteurs du replay (<see cref="ReplayCoreTimingProbe"/>), deja eprouves.
/// </summary>
public static class DeckLaunchLog
{
    /// <summary>
    /// « [Remap] Game-specific remap found at "...\FinalBurn Neo\19xx.rmp". » ; RetroArch 1.22 dit aussi
    /// « Content-dir-specific » et « Core-specific ». La portee dit lequel des trois fichiers a gagne.
    /// </summary>
    private static readonly Regex RemapLine = new(
        "\\[Remap\\]\\s+(?<portee>Game|Content-dir|Core)-specific remap found at\\s+\"(?<chemin>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>« [Video] Set video size to: 1920x1200. » : la taille de la fenetre (ou du plein ecran) demandee.</summary>
    private static readonly Regex VideoSizeLine = new(
        @"\[Video\]\s+Set video size to:\s*(?<w>\d+)x(?<h>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>« [Core] Loading dynamic libretro core from: "...\core_proxy\fbneo_libretro.dll". »</summary>
    private static readonly Regex CoreLine = new(
        @"Loading dynamic libretro core from:\s*""?(?:[^""]*[\\/])?(?<c>[^""\\/]+?)_libretro\.dll",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>« [Content] CRC32: 0xa3ddb605. » : FBNeo le journalise APRES la geometrie (il charge le contenu lui-meme).</summary>
    private static readonly Regex CrcLine = new(
        @"\[Content\]\s+CRC32:\s*(?:0x)?(?<crc>[0-9a-fA-F]{1,8})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>« [Config] Appending config "...". » : un fichier ajoute par <c>--appendconfig</c>, applique apres la configuration.</summary>
    private static readonly Regex AppendLine = new(
        "Appending config\\s+\"(?<p>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Ce que le journal donne, champ par champ ; null pour ce qu'il ne dit pas (encore).</summary>
    public sealed record Lecture(
        string? Core,
        int? BaseWidth,
        int? BaseHeight,
        double? Aspect,
        double? Fps,
        int? Rotation,
        string? Crc32,
        string? RemapPath,
        string? RemapScope,
        int? VideoWidth,
        int? VideoHeight,
        IReadOnlyList<string>? AppendedConfigs = null)
    {
        /// <summary>La geometrie est connue : le coeur a charge le contenu.</summary>
        public bool GeometrieConnue => BaseWidth is > 0 && BaseHeight is > 0;
    }

    /// <summary>Lit un journal complet (un lancement : RetroArch le reecrit a chaque demarrage).</summary>
    public static Lecture Lire(IReadOnlyList<string> lignes)
    {
        string? core = null;
        string? remapPath = null;
        string? remapScope = null;
        int? videoW = null, videoH = null;
        var ajouts = new List<string>();
        string? crc = null;
        foreach (var ligne in lignes)
        {
            var k = CrcLine.Match(ligne);
            if (k.Success)
            {
                crc = k.Groups["crc"].Value.ToLowerInvariant().PadLeft(8, '0');
                continue;
            }

            var a = AppendLine.Match(ligne);
            if (a.Success)
            {
                ajouts.Add(a.Groups["p"].Value.Replace("\\\\", "\\"));
                continue;
            }

            var c = CoreLine.Match(ligne);
            if (c.Success)
            {
                core = c.Groups["c"].Value.ToLowerInvariant();
                continue;
            }

            var r = RemapLine.Match(ligne);
            if (r.Success)
            {
                // le dernier dit vrai : RetroArch peut recharger un remappage (changement de coeur dans le menu)
                remapPath = r.Groups["chemin"].Value.Replace("\\\\", "\\");
                remapScope = r.Groups["portee"].Value.ToLowerInvariant() switch
                {
                    "game" => "game",
                    "content-dir" => "content-dir",
                    _ => "core"
                };
                continue;
            }

            var v = VideoSizeLine.Match(ligne);
            if (v.Success && int.TryParse(v.Groups["w"].Value, out var w) && int.TryParse(v.Groups["h"].Value, out var h))
            {
                videoW = w;
                videoH = h;
            }
        }

        var cadence = ReplayCoreTimingProbe.LireLaCadence(lignes);
        var rotation = ReplayCoreTimingProbe.LireRotation(lignes, null);
        return new Lecture(
            Core: cadence?.Coeur ?? core,
            BaseWidth: cadence?.Width is > 0 ? cadence.Width : null,
            BaseHeight: cadence?.Height is > 0 ? cadence.Height : null,
            Aspect: cadence?.Aspect,
            Fps: cadence?.Fps,
            Rotation: rotation,
            Crc32: cadence?.Crc32 ?? crc,
            RemapPath: remapPath,
            RemapScope: remapScope,
            VideoWidth: videoW,
            VideoHeight: videoH,
            AppendedConfigs: ajouts);
    }

    /// <summary>
    /// La forme de l'image telle que RetroArch la montre : la largeur et la hauteur du jeu une fois tourne (un quart de
    /// tour impair echange les deux), et son orientation. Le coeur annonce le tampon NON tourne (19XX sous FBNeo :
    /// 384x224, rotation 1) ; l'aspect qu'il donne est deja celui de l'image affichee (0,750 pour 19XX).
    /// </summary>
    public static (int Width, int Height, string Orientation) Affichage(int baseWidth, int baseHeight, int? rotation, double? aspect)
    {
        var tourne = rotation is 1 or 3;
        var w = tourne ? baseHeight : baseWidth;
        var h = tourne ? baseWidth : baseHeight;
        var vertical = tourne || (aspect is > 0 and < 1.0) || (aspect is null && h > w);
        return (w, h, vertical ? "vertical" : "horizontal");
    }
}
