using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// La charte d'EmulationStation, LUE sur le disque et jamais imitee.
///
/// Le panneau de classement s'affiche a cote du menu natif d'ES : s'il ne porte pas la meme
/// police, les memes couleurs et les memes glyphes de boutons, il se voit comme une piece
/// rapportee. Or tout est deja la : le theme actif dit sa police et ses couleurs, et ES livre
/// ses 38 glyphes de boutons dans <c>resources/help/</c>. On lit ces fichiers, donc le panneau
/// suit le theme que le joueur a choisi, sans redeploiement et sans qu'on ait rien a copier.
///
/// Ordre de lecture, du plus precis au plus general :
///   1. le sous-ensemble « help » du theme (police, couleurs, taille) : c'est lui qui habille la
///      barre d'aide du bas, donc la reference exacte pour un texte d'aide ;
///   2. le colorset du theme (fond, couleur de marque) ;
///   3. des valeurs de repli, celles de carbon, quand un theme ne dit rien.
///
/// Rien ici ne dessine : on rend des chemins et des couleurs, et ca se teste sans ecran.
/// </summary>
public sealed record EsThemeStyle
{
    /// <summary>Le theme lu, pour le journal et le diagnostic.</summary>
    public string ThemeSet { get; init; } = "";

    /// <summary>La police du theme, chemin absolu. Vide si le theme n'en fournit pas.</summary>
    public string FontPath { get; init; } = "";

    /// <summary>La variante grasse, quand le theme en a une a cote de la reguliere.</summary>
    public string FontBoldPath { get; init; } = "";

    /// <summary>Taille de la barre d'aide, en FRACTION de la hauteur d'ecran (ES raisonne ainsi).</summary>
    public double HelpFontSize { get; init; } = 0.032;

    public string TextColor { get; init; } = "7d7d7d";
    public string IconColor { get; init; } = "7d7d7d";
    public string BackgroundColor { get; init; } = "051222";
    public string BaseColor { get; init; } = "3675CA";
    public string GroupColor { get; init; } = "5178C3";

    /// <summary>Le dossier des glyphes de boutons d'ES (<c>resources/help</c>).</summary>
    public string HelpIconsRoot { get; init; } = "";

    /// <summary>Le fichier d'un glyphe, ou une chaine vide s'il manque.</summary>
    public string Icon(string nom)
    {
        if (HelpIconsRoot.Length == 0 || nom.Length == 0) return "";
        var chemin = Path.Combine(HelpIconsRoot, nom + ".svg");
        return File.Exists(chemin) ? chemin : "";
    }

    /// <summary>Une couleur de theme (« RRGGBB » ou « RRGGBBAA ») vers ses composantes.</summary>
    public static (int R, int G, int B, int A) Couleur(string hex, int alphaParDefaut = 255)
    {
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length is not (6 or 8)) return (255, 255, 255, alphaParDefaut);
        static int Lire(string s, int i) => Convert.ToInt32(s.Substring(i, 2), 16);
        try
        {
            return (Lire(h, 0), Lire(h, 2), Lire(h, 4), h.Length == 8 ? Lire(h, 6) : alphaParDefaut);
        }
        catch (FormatException)
        {
            return (255, 255, 255, alphaParDefaut);
        }
    }

    // ── Lecture ──────────────────────────────────────────────────────────────

    /// <summary>La charte du theme ACTIF de cette borne.</summary>
    public static EsThemeStyle Lire(ILogger? logger = null)
        => Lire(Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation"), LireThemeSet(), logger);

    /// <summary>
    /// La charte, a partir d'une racine EmulationStation et d'un nom de theme. Separee pour se
    /// tester sur une arborescence quelconque.
    /// </summary>
    public static EsThemeStyle Lire(string racineEs, string themeSet, ILogger? logger = null)
    {
        var aides = Path.Combine(racineEs, "resources", "help");
        var theme = Path.Combine(racineEs, ".emulationstation", "themes", themeSet);
        // `racineEs` est le dossier « emulationstation » de RetroBat : les glyphes sont sous
        // `resources/help`, les themes sous `.emulationstation/themes`.
        var style = new EsThemeStyle
        {
            ThemeSet = themeSet,
            HelpIconsRoot = Directory.Exists(aides) ? aides : "",
        };
        if (!Directory.Exists(theme))
        {
            logger?.LogDebug("Classement : theme {Theme} introuvable, charte de repli.", themeSet);
            return style;
        }

        var (police, taille, texte, icone) = LireAide(theme, logger);
        var (fond, marque, groupe) = LireCouleurs(theme, logger);
        var gras = police.Length > 0 ? police.Replace("-Regular.", "-Bold.") : "";

        return style with
        {
            FontPath = police,
            FontBoldPath = gras.Length > 0 && File.Exists(gras) ? gras : "",
            HelpFontSize = taille ?? style.HelpFontSize,
            TextColor = texte ?? style.TextColor,
            IconColor = icone ?? style.IconColor,
            BackgroundColor = fond ?? style.BackgroundColor,
            BaseColor = marque ?? style.BaseColor,
            GroupColor = groupe ?? style.GroupColor,
        };
    }

    /// <summary>Le theme choisi dans es_settings.cfg (<c>ThemeSet</c>).</summary>
    public static string LireThemeSet()
    {
        try
        {
            var fichier = RetroBatPaths.EmulationStationSettingsPath;
            if (!File.Exists(fichier)) return "es-theme-carbon";
            foreach (var ligne in File.ReadLines(fichier))
            {
                if (!ligne.Contains("\"ThemeSet\"", StringComparison.Ordinal)) continue;
                var i = ligne.IndexOf("value=\"", StringComparison.Ordinal);
                if (i < 0) continue;
                var j = ligne.IndexOf('"', i + 7);
                if (j > i) return ligne[(i + 7)..j];
            }
        }
        catch (IOException) { }
        return "es-theme-carbon";
    }

    /// <summary>
    /// Le sous-ensemble « help » du theme : police, taille, couleurs. On prend le PREMIER
    /// <c>helpsystem</c> rencontre sous <c>subsets/help</c>, puis a defaut n'importe lequel du
    /// theme : tous decrivent la meme barre, et un theme qui n'en declare qu'un le declare la.
    /// </summary>
    private static (string Police, double? Taille, string? Texte, string? Icone) LireAide(string theme, ILogger? logger)
    {
        foreach (var fichier in FichiersDeTheme(theme))
        {
            try
            {
                var doc = XDocument.Load(fichier);
                var aide = doc.Descendants("helpsystem").FirstOrDefault();
                if (aide is null) continue;

                var police = (string?) aide.Element("fontPath") ?? "";
                if (police.Length > 0)
                {
                    police = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fichier)!, police.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(police)) police = "";
                }
                // ES ecrit parfois plusieurs fontSize (dont un pour l'ecran vertical) : on prend
                // le premier, celui de l'ecran normal.
                double? taille = double.TryParse((string?) aide.Element("fontSize"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;
                return (police, taille, (string?) aide.Element("textColor"), (string?) aide.Element("iconColor"));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                logger?.LogDebug("Classement : {Fichier} illisible.", Path.GetFileName(fichier));
            }
        }
        return ("", null, null, null);
    }

    /// <summary>Les variables de couleur du theme : fond, couleur de marque, couleur de groupe.</summary>
    private static (string? Fond, string? Marque, string? Groupe) LireCouleurs(string theme, ILogger? logger)
    {
        string? fond = null, marque = null, groupe = null;
        foreach (var fichier in FichiersDeTheme(theme))
        {
            try
            {
                var doc = XDocument.Load(fichier);
                var variables = doc.Descendants("variables").FirstOrDefault();
                if (variables is null) continue;
                fond ??= (string?) variables.Element("backgroundColor");
                marque ??= (string?) variables.Element("baseColor");
                groupe ??= (string?) variables.Element("groupColor");
                if (fond is not null && marque is not null && groupe is not null) break;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                logger?.LogDebug("Classement : {Fichier} illisible.", Path.GetFileName(fichier));
            }
        }
        return (fond, marque, groupe);
    }

    /// <summary>
    /// Les fichiers du theme a interroger : d'abord les sous-ensembles « help » et les colorsets
    /// (les plus precis), puis le theme.xml, puis le reste. Borne a deux cents fichiers : un
    /// theme n'en a jamais autant d'utiles, et on ne veut pas parcourir un disque.
    /// </summary>
    private static IEnumerable<string> FichiersDeTheme(string theme)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sous in new[] { Path.Combine("subsets", "help"), Path.Combine("subsets", "colorsets") })
        {
            var dossier = Path.Combine(theme, sous);
            if (!Directory.Exists(dossier)) continue;
            foreach (var f in Directory.EnumerateFiles(dossier, "*.xml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (vus.Add(f)) yield return f;
            }
        }
        var racine = Path.Combine(theme, "theme.xml");
        if (File.Exists(racine) && vus.Add(racine)) yield return racine;

        if (!Directory.Exists(theme)) yield break;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(theme, "*.xml", SearchOption.AllDirectories))
        {
            if (++n > 200) yield break;
            if (vus.Add(f)) yield return f;
        }
    }
}
