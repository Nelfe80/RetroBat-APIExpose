using System.Drawing;
using System.Drawing.Text;
using System.Runtime.Versioning;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LES POLICES DES ECRANS MULTIPLAYER (CDC Ecrans v1.0, lot E1) : les polices libres (OFL) que le Data Pack livre dans
/// <c>theme/fonts</c>, chargees une seule fois pour tout le processus. Une famille absente rend null : l'ecran retombe sur
/// les polices du theme d'ES, il ne casse pas.
///
/// PIEGE GDI+ : une graisse hors des quatre classiques (regular, gras, italique, gras italique) forme sa PROPRE famille.
/// « Barlow Condensed ExtraBold » est une famille, pas un style de « Barlow Condensed ».
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PolicesDesEcrans
{
    private static readonly object Verrou = new();
    private static PrivateFontCollection? _collection;
    private static Dictionary<string, FontFamily>? _familles;
    private static string _dossier = Path.Combine(RetroBatPaths.ThemeResourcesRoot, "fonts");

    /// <summary>Le dossier des polices. Le changer oublie ce qui a ete charge (tests).</summary>
    internal static string Dossier
    {
        get { lock (Verrou) return _dossier; }
        set
        {
            lock (Verrou)
            {
                _dossier = value;
                _collection?.Dispose();
                _collection = null;
                _familles = null;
            }
        }
    }

    /// <summary>Les familles chargees, par nom.</summary>
    internal static IReadOnlyCollection<string> Noms()
    {
        Charger();
        lock (Verrou) return _familles!.Keys.ToList();
    }

    public static FontFamily? Famille(string nom)
    {
        Charger();
        lock (Verrou) return _familles!.TryGetValue(nom, out var famille) ? famille : null;
    }

    /// <summary>
    /// Une police de <paramref name="taillePx"/> pixels, dans le premier style que la famille connait (celui demande, puis
    /// regular, puis gras). Null si la famille manque.
    /// </summary>
    public static Font? Police(string famille, float taillePx, FontStyle style = FontStyle.Regular)
    {
        if (Famille(famille) is not { } f)
        {
            return null;
        }

        foreach (var essai in new[] { style, FontStyle.Regular, FontStyle.Bold })
        {
            if (f.IsStyleAvailable(essai))
            {
                return new Font(f, Math.Max(1f, taillePx), essai, GraphicsUnit.Pixel);
            }
        }

        return null;
    }

    private static void Charger()
    {
        lock (Verrou)
        {
            if (_familles is not null)
            {
                return;
            }

            var collection = new PrivateFontCollection();
            var familles = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_dossier))
            {
                foreach (var fichier in Directory.EnumerateFiles(_dossier, "*.ttf").Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        collection.AddFontFile(fichier);
                    }
                    catch (Exception)
                    {
                        // Un fichier abime ne prive pas les autres de leur police.
                    }
                }

                foreach (var famille in collection.Families)
                {
                    familles[famille.Name] = famille;
                }
            }

            _collection = collection;
            _familles = familles;
        }
    }
}
