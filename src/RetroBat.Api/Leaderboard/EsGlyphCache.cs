using System.Diagnostics;
using System.Runtime.Versioning;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Les glyphes de boutons d'EmulationStation, rasterises une fois et gardes.
///
/// ES livre ses 38 glyphes en SVG (<c>resources/help/</c>) et GDI+ ne sait pas les lire. Plutot
/// que de redessiner des croix directionnelles a la main - qui se verraient tout de suite a cote
/// des vraies - on convertit les FICHIERS D'ES avec l'ImageMagick deja livre dans APIExpose, et
/// on garde le resultat dans <c>.cache/</c>. Le panneau affiche donc les icones que le joueur
/// voit deja dans la barre d'aide, et suit son theme sans qu'on recopie quoi que ce soit.
///
/// La conversion coute quelques dizaines de millisecondes par glyphe, une seule fois par taille
/// et par couleur : elle est faite en amont de l'affichage, jamais pendant un rendu.
///
/// Sans ImageMagick, on rend null : l'appelant dessine alors son texte sans icone. Une aide sans
/// pictogramme reste lisible ; une aide qui fait tomber le panneau, non.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EsGlyphCache : IDisposable
{
    private readonly EsThemeStyle _style;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, Image?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public EsGlyphCache(EsThemeStyle style, ILogger? logger = null)
    {
        _style = style;
        _logger = logger;
    }

    private static string Racine => Path.Combine(RetroBatPaths.PluginRoot, ".cache", "leaderboard-glyphs");

    /// <summary>L'outil de conversion livre avec APIExpose, ou une chaine vide s'il manque.</summary>
    public static string Outil()
    {
        foreach (var nom in new[] { "magick.exe", "convert.exe" })
        {
            var chemin = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", nom);
            if (File.Exists(chemin)) return chemin;
        }
        return "";
    }

    /// <summary>
    /// Le glyphe demande, a cette hauteur en pixels, teinte de cette couleur. Null s'il n'existe
    /// pas ou si la conversion echoue.
    /// </summary>
    public Image? Glyphe(string nom, int hauteur, Color teinte)
    {
        var cle = $"{nom}-{hauteur}-{teinte.R:X2}{teinte.G:X2}{teinte.B:X2}";
        lock (_gate)
        {
            if (_images.TryGetValue(cle, out var deja)) return deja;
            var image = Charger(nom, hauteur, teinte, cle);
            _images[cle] = image;
            return image;
        }
    }

    private Image? Charger(string nom, int hauteur, Color teinte, string cle)
    {
        var source = _style.Icon(nom);
        if (source.Length == 0) return null;

        var cible = Path.Combine(Racine, cle + ".png");
        try
        {
            if (!File.Exists(cible) || File.GetLastWriteTimeUtc(cible) < File.GetLastWriteTimeUtc(source))
            {
                if (!Convertir(source, cible, hauteur, teinte)) return null;
            }
            // On lit les octets et on referme : garder le fichier ouvert empecherait de le
            // regenerer, et un cache qu'on ne peut pas refaire est un cache qu'on subit.
            using var flux = new MemoryStream(File.ReadAllBytes(cible));
            return Image.FromStream(flux);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Classement : glyphe {Nom} indisponible.", nom);
            return null;
        }
    }

    private bool Convertir(string source, string cible, int hauteur, Color teinte)
    {
        var outil = Outil();
        if (outil.Length == 0)
        {
            _logger?.LogDebug("Classement : ImageMagick absent, le panneau se passera d'icones.");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(cible)!);

        // La densite dicte la finesse du trait : on rasterise plus grand que la cible, puis on
        // reduit, sinon les arrondis de la croix directionnelle bavent.
        var densite = Math.Clamp(hauteur * 6, 96, 1200);
        var couleur = $"#{teinte.R:X2}{teinte.G:X2}{teinte.B:X2}";
        var arguments = $"-background none -density {densite} \"{source}\" -resize x{hauteur} "
            + $"-fill \"{couleur}\" -colorize 100 \"{cible}\"";

        try
        {
            using var p = Process.Start(new ProcessStartInfo(outil, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return File.Exists(cible);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Classement : conversion de {Source} impossible.", Path.GetFileName(source));
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var image in _images.Values) image?.Dispose();
            _images.Clear();
        }
    }
}
