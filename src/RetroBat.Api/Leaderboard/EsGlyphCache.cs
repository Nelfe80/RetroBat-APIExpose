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
    private readonly EsMenuStyle _style;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, Image?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public EsGlyphCache(EsMenuStyle style, ILogger? logger = null)
    {
        _style = style;
        _logger = logger;
    }

    /// <summary>
    /// Ou vivent les pictogrammes convertis. C'est un MEDIA du produit, pas un cache jetable :
    /// la conversion (un processus ImageMagick par image) se fait une fois, les fichiers
    /// restent, et le panneau ne fait plus que les lire.
    /// </summary>
    private static string Racine => Path.Combine(RetroBatPaths.PluginRoot, "media", "leaderboard");

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
    /// <summary>Appele quand un glyphe demande plus tot est devenu disponible.</summary>
    public event Action? Pret;

    private readonly HashSet<string> _enCours = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Le glyphe s'il est deja en memoire ou deja rasterise sur le disque ; sinon null TOUT DE
    /// SUITE, et la rasterisation part en arriere-plan (un processus ImageMagick par glyphe,
    /// une a plusieurs secondes chacune). La faire ici, sur le fil de la fenetre, a laisse le
    /// panneau invisible pres d'une minute a la premiere ouverture.
    /// </summary>
    public Image? Glyphe(string nom, int hauteur, Color? teinte = null)
    {
        var nomDeCle = Path.IsPathRooted(nom) ? Path.GetFileNameWithoutExtension(nom) : nom;
        var cle = teinte is { } t ? $"{nomDeCle}-{hauteur}-{t.R:X2}{t.G:X2}{t.B:X2}" : $"{nomDeCle}-{hauteur}-tel-quel";
        lock (_gate)
        {
            if (_images.TryGetValue(cle, out var deja)) return deja;
            var source = _style.Icon(nom);
            if (source.Length == 0) { _images[cle] = null; return null; }

            var cible = Path.Combine(Racine, cle + ".png");
            if (File.Exists(cible) && File.GetLastWriteTimeUtc(cible) >= File.GetLastWriteTimeUtc(source))
            {
                var image = LireImage(cible, nom);
                _images[cle] = image;
                return image;
            }
            if (!_enCours.Add(cle)) return null;
        }

        _ = Task.Run(() =>
        {
            Image? image = null;
            try
            {
                var source = _style.Icon(nom);
                var cible = Path.Combine(Racine, cle + ".png");
                if (source.Length > 0 && Convertir(source, cible, hauteur, teinte)) image = LireImage(cible, nom);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Classement : glyphe {Nom} indisponible.", nom);
            }
            lock (_gate)
            {
                _images[cle] = image;
                _enCours.Remove(cle);
            }
            if (image is not null) Pret?.Invoke();
        });
        return null;
    }

    /// <summary>
    /// Le nombre de pictogrammes PRETS SUR LE DISQUE. C'est ce qui compte : la preparation
    /// ecrit des fichiers sans les charger, et ce sont eux qui evitent toute conversion plus
    /// tard. Compter les images en memoire aurait annonce zero apres un prechauffage reussi.
    /// </summary>
    public int Combien
    {
        get
        {
            try { return Directory.Exists(Racine) ? Directory.EnumerateFiles(Racine, "*.png").Count() : 0; }
            catch (IOException) { return 0; }
        }
    }

    /// <summary>
    /// Ecrit le fichier du pictogramme s'il manque, ICI et maintenant (appele depuis un fil de
    /// fond). Ne charge pas l'image en memoire : le dessin la lira du disque quand il en aura
    /// besoin, sans jamais relancer de conversion.
    /// </summary>
    public void Preparer(string nom, int hauteur, Color? teinte)
    {
        var nomDeCle = Path.IsPathRooted(nom) ? Path.GetFileNameWithoutExtension(nom) : nom;
        var cle = teinte is { } t ? $"{nomDeCle}-{hauteur}-{t.R:X2}{t.G:X2}{t.B:X2}" : $"{nomDeCle}-{hauteur}-tel-quel";
        var source = _style.Icon(nom);
        if (source.Length == 0) return;
        var cible = Path.Combine(Racine, cle + ".png");
        try
        {
            if (File.Exists(cible) && File.GetLastWriteTimeUtc(cible) >= File.GetLastWriteTimeUtc(source)) return;
            Convertir(source, cible, hauteur, teinte);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Classement : preparation de {Nom} impossible.", nom);
        }
    }

    private Image? LireImage(string cible, string nom)
    {
        try
        {
            using var flux = new MemoryStream(File.ReadAllBytes(cible));
            return Image.FromStream(flux);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Classement : glyphe {Nom} illisible.", nom);
            return null;
        }
    }

    private bool Convertir(string source, string cible, int hauteur, Color? teinte)
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
        var teinter = teinte is { } t ? $"-fill \"#{t.R:X2}{t.G:X2}{t.B:X2}\" -colorize 100 " : "";
        var arguments = $"-background none -density {densite} \"{source}\" -resize x{hauteur} "
            + teinter + $"\"{cible}\"";

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
