using System.Globalization;
using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// La charte des MENUS d'EmulationStation, lue dans le theme actif.
///
/// Le panneau de classement se pose a cote du menu de jeu : s'il n'a pas exactement la meme
/// police, les memes tailles et les memes couleurs, il se voit comme une piece rapportee. Or le
/// theme les declare, et la source d'ES dit comment il les lit
/// (<c>ThemeData::ThemeMenu</c>, elements <c>menubg</c>, <c>menutitle</c>, <c>menutext</c>,
/// <c>menugroup</c>, <c>menufooter</c>). On lit les MEMES elements, avec les MEMES valeurs de
/// repli que la source quand le theme se tait.
///
/// Deux details qu'on ne devine pas et qui sautent aux yeux quand ils manquent :
///   - une taille de police d'un theme est une FRACTION DE LA HAUTEUR D'ECRAN, pas des pixels ;
///   - ES met ses entrees de menu en MAJUSCULES lui-meme (<c>Utils::String::toUpper</c>).
///
/// Les valeurs peuvent porter des variables du colorset (<c>${baseColor}</c>) : on les resout
/// avec les variables du theme, exactement comme ES.
/// </summary>
public sealed record EsMenuStyle
{
    public string ThemeSet { get; init; } = "";

    /// <summary>Le fond du menu (carbon : 242424). Les couleurs sont « RRGGBB » ou « RRGGBBAA ».</summary>
    public string BackgroundColor { get; init; } = "242424";

    public string TitleFontPath { get; init; } = "";
    public double TitleFontSize { get; init; } = 0.034;
    public string TitleColor { get; init; } = "FAFAFA";

    public string TextFontPath { get; init; } = "";
    public double TextFontSize { get; init; } = 0.026;
    public string TextColor { get; init; } = "969696";

    /// <summary>La barre de selection : un degrade horizontal, comme dans les menus.</summary>
    public string SelectorColor { get; init; } = "3675CA";
    public string SelectorColorEnd { get; init; } = "00205b";
    public string SelectedTextColor { get; init; } = "FFFFFF";

    /// <summary>Les separateurs entre onglets (menutext) et les bordures de grille (menugrid).</summary>
    public string SeparatorColor { get; init; } = "C6C7C6FF";
    public string GridSeparatorColor { get; init; } = "C6C7C6FF";

    /// <summary>Les en-tetes de section (« GAME MEDIA », « OPTIONS »…) : nos onglets s'y calquent.</summary>
    public string GroupFontPath { get; init; } = "";
    public double GroupFontSize { get; init; } = 0.018;
    public string GroupColor { get; init; } = "5178C3";

    /// <summary>Le petit texte des menus (menutextsmall) : etiquettes, origines.</summary>
    public string SmallFontPath { get; init; } = "";
    public double SmallFontSize { get; init; } = 0.016;

    public string FooterFontPath { get; init; } = "";
    public double FooterFontSize { get; init; } = 0.02;
    public string FooterColor { get; init; } = "777777";

    /// <summary>Le bouton des menus (menubutton) : le rayon de ses coins, en pixels.</summary>
    public double ButtonCornerSize { get; init; } = 16;

    /// <summary>
    /// Les pictogrammes de la barre d'aide, par identite de bouton, tels que le sous-ensemble
    /// d'aide ACTIF du theme les choisit (carbon « buttons » : A est, B sud, X nord, Y ouest),
    /// et leur couleur (iconColor, souvent ${baseColor}). C'est ce que le joueur voit deja en
    /// bas de l'ecran : nos touches doivent porter les memes.
    /// </summary>
    public IReadOnlyDictionary<string, string> HelpIcons { get; init; } = new Dictionary<string, string>();
    public string HelpIconColor { get; init; } = "7d7d7d";
    public string HelpFontPath { get; init; } = "";

    /// <summary>Le pictogramme d'une identite de bouton (a, b, x, y, l, r, l2, r2, start, select).</summary>
    public string HelpIcon(string identite)
    {
        var id = (identite ?? "").Trim().ToLowerInvariant();
        if (HelpIcons.TryGetValue(id, out var chemin) && chemin.Length > 0) return chemin;
        var nom = id switch
        {
            "a" or "b" or "x" or "y" or "l" or "r" or "start" or "select" => "button_" + id,
            "l2" => "button_lt",
            "r2" => "button_rt",
            "l3" or "r3" => "analog_thumb",
            _ => "",
        };
        return nom.Length == 0 ? "" : Icon(nom);
    }

    /// <summary>Le dossier des 38 glyphes de boutons d'ES, et celui de ses autres icones.</summary>
    public string HelpIconsRoot { get; init; } = "";
    public string ResourcesRoot { get; init; } = "";

    /// <summary>
    /// Une icone par son nom : d'abord un glyphe de bouton d'ES (help/), puis une icone de ses
    /// ressources (star_filled…), puis une image du Data Pack (theme/images/). Un chemin
    /// complet est rendu tel quel s'il existe.
    /// </summary>
    public string Icon(string nom)
    {
        if (nom.Length == 0) return "";
        if (Path.IsPathRooted(nom)) return File.Exists(nom) ? nom : "";
        foreach (var racine in new[] { HelpIconsRoot, ResourcesRoot, Path.Combine(RetroBatPaths.PluginRoot, "resources", "theme", "images") })
        {
            if (racine.Length == 0) continue;
            foreach (var ext in new[] { ".svg", ".png" })
            {
                var chemin = Path.Combine(racine, nom + ext);
                if (File.Exists(chemin)) return chemin;
            }
        }
        return "";
    }

    /// <summary>Une couleur de theme vers ses composantes. « RRGGBB » ou « RRGGBBAA ».</summary>
    public static (int R, int G, int B, int A) Couleur(string hex, int alphaParDefaut = 255)
    {
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length is not (6 or 8)) return (255, 255, 255, alphaParDefaut);
        try
        {
            static int Lire(string s, int i) => Convert.ToInt32(s.Substring(i, 2), 16);
            return (Lire(h, 0), Lire(h, 2), Lire(h, 4), h.Length == 8 ? Lire(h, 6) : alphaParDefaut);
        }
        catch (FormatException)
        {
            return (255, 255, 255, alphaParDefaut);
        }
    }

    // ── Lecture ──────────────────────────────────────────────────────────────

    private static EsMenuStyle? _enMemoire;
    private static string _signatureEnMemoire = "";

    /// <summary>
    /// La charte du theme actif. Elle est gardee en memoire : la lire demande de parcourir des
    /// dizaines de fichiers XML, et le faire a CHAQUE ouverture retardait l'affichage du
    /// panneau. La signature (theme choisi + date des reglages) suffit a savoir qu'il faut
    /// relire, par exemple quand le joueur change de theme ou de jeu d'icones.
    /// </summary>
    public static EsMenuStyle Lire(ILogger? logger = null)
    {
        var themeSet = LireThemeSet();
        // La signature ne retient que ce qui change l'APPARENCE. Elle contenait la date de
        // es_settings.cfg, qu'EmulationStation reecrit en naviguant : le cache tombait presque
        // a chaque ouverture et la charte etait relue (1,5 s de fichiers XML).
        var signature = themeSet + "|" + LireReglage("subset.helpsystem") + "|" + LireReglage("subset.blurfx");

        if (_enMemoire is { } deja && string.Equals(signature, _signatureEnMemoire, StringComparison.Ordinal))
        {
            return deja;
        }
        var style = Lire(Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation"), themeSet, logger);
        _enMemoire = style;
        _signatureEnMemoire = signature;
        return style;
    }

    public static EsMenuStyle Lire(string racineEs, string themeSet, ILogger? logger = null)
    {
        var ressources = Path.Combine(racineEs, "resources");
        var aides = Path.Combine(ressources, "help");
        var style = new EsMenuStyle
        {
            ThemeSet = themeSet,
            HelpIconsRoot = Directory.Exists(aides) ? aides : "",
            ResourcesRoot = Directory.Exists(ressources) ? ressources : "",
        };

        var theme = Path.Combine(racineEs, ".emulationstation", "themes", themeSet);
        if (!Directory.Exists(theme))
        {
            logger?.LogDebug("Classement : theme {Theme} introuvable, charte de repli.", themeSet);
            return style;
        }

        var variables = LireVariables(theme);
        style = style with { HelpIcons = LireIconesDAide(theme, ressources, variables, out var couleurIcones, out var policeAide),
            HelpIconColor = couleurIcones ?? style.HelpIconColor, HelpFontPath = policeAide ?? style.HelpFontPath };
        // ES applique la vue de menu, puis les sous-ensembles inclus par-dessus (carbon :
        // blurfx rend le fond translucide). On lit dans le meme ordre, et chaque fichier
        // surcharge ce qu'il declare.
        var lus = 0;
        foreach (var fichier in FichiersDeMenu(theme))
        {
            XDocument doc;
            try { doc = XDocument.Load(fichier); }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { continue; }

            var dossier = Path.GetDirectoryName(fichier)!;
            var fond = Element(doc, "menuBackground", "menubg");
            var titre = Element(doc, "menuText", "menutitle");
            var texte = Element(doc, "menuText", "menutext");
            var groupe = Element(doc, "menuGroup", "menugroup");
            var pied = Element(doc, "menuText", "menufooter");
            var grille = Element(doc, "menuGrid", "menugrid");
            var petit = Element(doc, "menuTextSmall", "menutextsmall");
            var bouton = Element(doc, "menuButton", "menubutton");
            if (fond is null && titre is null && texte is null && groupe is null && pied is null && bouton is null) continue;

            style = style with
            {
                BackgroundColor = Valeur(fond, "color", variables) ?? style.BackgroundColor,

                TitleFontPath = Police(titre, dossier) ?? style.TitleFontPath,
                TitleFontSize = Taille(titre, variables) ?? style.TitleFontSize,
                TitleColor = Valeur(titre, "color", variables) ?? style.TitleColor,

                TextFontPath = Police(texte, dossier) ?? style.TextFontPath,
                TextFontSize = Taille(texte, variables) ?? style.TextFontSize,
                TextColor = Valeur(texte, "color", variables) ?? style.TextColor,
                SelectorColor = Valeur(texte, "selectorColor", variables) ?? style.SelectorColor,
                SelectorColorEnd = Valeur(texte, "selectorColorEnd", variables) ?? style.SelectorColorEnd,
                SelectedTextColor = Valeur(texte, "selectedColor", variables) ?? style.SelectedTextColor,
                SeparatorColor = Valeur(texte, "separatorColor", variables) ?? style.SeparatorColor,
                GridSeparatorColor = Valeur(grille, "separatorColor", variables)
                    ?? Valeur(texte, "separatorColor", variables) ?? style.GridSeparatorColor,

                GroupFontPath = Police(groupe, dossier) ?? style.GroupFontPath,
                GroupFontSize = Taille(groupe, variables) ?? style.GroupFontSize,
                GroupColor = Valeur(groupe, "color", variables) ?? style.GroupColor,

                SmallFontPath = Police(petit, dossier) ?? Police(texte, dossier) ?? style.SmallFontPath,
                SmallFontSize = Taille(petit, variables) ?? style.SmallFontSize,

                FooterFontPath = Police(pied, dossier) ?? style.FooterFontPath,
                FooterFontSize = Taille(pied, variables) ?? style.FooterFontSize,
                FooterColor = Valeur(pied, "color", variables) ?? style.FooterColor,

                ButtonCornerSize = Coin(bouton) ?? style.ButtonCornerSize,
            };
            lus++;
        }
        logger?.LogInformation("Classement : charte des menus lue ({Fichiers} fichier(s), theme {Theme}, fond {Fond}).",
            lus, themeSet, style.BackgroundColor);
        return style;
    }

    /// <summary>
    /// Les pictogrammes d'aide du sous-ensemble ACTIF : celui que <c>subset.helpsystem</c>
    /// nomme dans es_settings, sinon le premier declare par le theme (c'est la regle d'ES).
    /// Le fichier du sous-ensemble inclut souvent <c>default.xml</c> : on lit l'inclusion
    /// d'abord, puis le fichier, qui surcharge.
    /// </summary>
    private static Dictionary<string, string> LireIconesDAide(string theme, string ressources,
        IReadOnlyDictionary<string, string> variables, out string? couleur, out string? police)
    {
        var icones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        couleur = null;
        police = null;
        var racine = Path.Combine(theme, "theme.xml");
        if (!File.Exists(racine)) return icones;

        string? fichierActif = null;
        try
        {
            var doc = XDocument.Load(racine);
            var subset = doc.Descendants("subset").FirstOrDefault(e => (string?) e.Attribute("name") == "helpsystem");
            var includes = subset?.Elements("include").ToList() ?? new List<XElement>();
            if (includes.Count > 0)
            {
                var voulu = LireReglage("subset.helpsystem");
                var choisi = includes.FirstOrDefault(i => voulu.Length > 0 && string.Equals((string?) i.Attribute("name"), voulu, StringComparison.OrdinalIgnoreCase))
                             ?? includes[0];
                fichierActif = Path.GetFullPath(Path.Combine(theme, choisi.Value.Trim().Replace('/', Path.DirectorySeparatorChar)));
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { return icones; }
        if (fichierActif is null || !File.Exists(fichierActif)) return icones;

        var fichiers = new List<string>();
        try
        {
            var doc = XDocument.Load(fichierActif);
            foreach (var inc in doc.Root?.Elements("include") ?? Enumerable.Empty<XElement>())
            {
                var chemin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fichierActif)!, inc.Value.Trim().Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(chemin)) fichiers.Add(chemin);
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { }
        fichiers.Add(fichierActif);

        foreach (var fichier in fichiers)
        {
            XDocument doc;
            try { doc = XDocument.Load(fichier); }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { continue; }
            foreach (var aide in doc.Descendants("helpsystem"))
            {
                var c = (string?) aide.Element("iconColor");
                if (!string.IsNullOrWhiteSpace(c)) couleur = Resoudre(c.Trim(), variables);
                var p = Police(aide, Path.GetDirectoryName(fichier)!);
                if (p is not null) police = p;
                foreach (var e in aide.Elements())
                {
                    var nom = e.Name.LocalName;
                    if (!nom.StartsWith("icon", StringComparison.Ordinal) || nom == "iconColor") continue;
                    var identite = nom["icon".Length..].ToLowerInvariant();   // iconA -> a, iconLR -> lr
                    var valeur = (e.Value ?? "").Trim();
                    if (valeur.Length == 0) continue;
                    var chemin = valeur.StartsWith(":/", StringComparison.Ordinal)
                        ? Path.Combine(ressources, valeur[2..].Replace('/', Path.DirectorySeparatorChar))
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fichier)!, valeur.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(chemin)) icones[identite] = chemin;
                }
            }
        }
        return icones;
    }

    /// <summary>Une valeur de es_settings.cfg, ou vide.</summary>
    private static string LireReglage(string nom)
    {
        try
        {
            var fichier = RetroBatPaths.EmulationStationSettingsPath;
            if (!File.Exists(fichier)) return "";
            var cle = "\"" + nom + "\"";
            foreach (var ligne in File.ReadLines(fichier))
            {
                if (!ligne.Contains(cle, StringComparison.Ordinal)) continue;
                var i = ligne.IndexOf("value=\"", StringComparison.Ordinal);
                if (i < 0) continue;
                var j = ligne.IndexOf('"', i + 7);
                if (j > i) return ligne[(i + 7)..j];
            }
        }
        catch (IOException) { }
        return "";
    }

    /// <summary>La vue de menu d'abord, puis les sous-ensembles, comme ES les applique.</summary>
    private static IEnumerable<string> FichiersDeMenu(string theme)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sous in new[] { "views", "subsets" })
        {
            var dossier = Path.Combine(theme, sous);
            if (!Directory.Exists(dossier)) continue;
            foreach (var f in Directory.EnumerateFiles(dossier, "*.xml", SearchOption.AllDirectories)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (vus.Add(f)) yield return f;
            }
        }
    }

    /// <summary>« 8 8 » : le rayon des coins d'un bouton, en pixels.</summary>
    private static double? Coin(XElement? element)
    {
        var brut = (string?) element?.Element("cornerSize");
        if (string.IsNullOrWhiteSpace(brut)) return null;
        var premier = brut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(premier, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

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

    // ── Details de lecture ───────────────────────────────────────────────────

    private static XElement? Element(XDocument doc, string balise, string nom)
        => doc.Descendants(balise).FirstOrDefault(e => (string?) e.Attribute("name") == nom);

    /// <summary>Une valeur, variables du colorset resolues (<c>${baseColor}</c>).</summary>
    private static string? Valeur(XElement? element, string nom, IReadOnlyDictionary<string, string> variables)
    {
        var brut = (string?) element?.Element(nom);
        if (string.IsNullOrWhiteSpace(brut)) return null;
        return Resoudre(brut.Trim(), variables);
    }

    private static string Resoudre(string valeur, IReadOnlyDictionary<string, string> variables)
    {
        if (!valeur.Contains("${", StringComparison.Ordinal)) return valeur;
        foreach (var (nom, v) in variables)
        {
            valeur = valeur.Replace("${" + nom + "}", v, StringComparison.Ordinal);
        }
        return valeur;
    }

    private static string? Police(XElement? element, string dossier)
    {
        var brut = (string?) element?.Element("fontPath");
        if (string.IsNullOrWhiteSpace(brut)) return null;
        var chemin = Path.GetFullPath(Path.Combine(dossier, brut.Trim().Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(chemin) ? chemin : null;
    }

    /// <summary>
    /// La taille : une FRACTION de la hauteur d'ecran. On prend la premiere declaration, celle
    /// de l'ecran ordinaire ; les variantes (<c>tinyScreen</c>, <c>verticalScreen</c>) suivent.
    /// </summary>
    private static double? Taille(XElement? element, IReadOnlyDictionary<string, string> variables)
    {
        var brut = element?.Elements("fontSize").FirstOrDefault(e => !e.Attributes().Any());
        var valeur = (string?) brut ?? (string?) element?.Element("fontSize");
        if (string.IsNullOrWhiteSpace(valeur)) return null;
        return double.TryParse(Resoudre(valeur.Trim(), variables), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? t
            : null;
    }

    /// <summary>Les variables du theme (colorset compris) : le thème s'y refere par ${nom}.</summary>
    private static Dictionary<string, string> LireVariables(string theme)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fichier in FichiersDeTheme(theme))
        {
            try
            {
                foreach (var bloc in XDocument.Load(fichier).Descendants("variables"))
                {
                    foreach (var v in bloc.Elements())
                    {
                        var valeur = v.Value.Trim();
                        if (valeur.Length > 0 && !variables.ContainsKey(v.Name.LocalName))
                        {
                            variables[v.Name.LocalName] = valeur;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { }
        }
        return variables;
    }

    /// <summary>
    /// Les fichiers du theme, du plus precis au plus general : les vues (dont <c>menu.xml</c>),
    /// les colorsets, puis le reste. Borne : un theme n'a jamais deux cents fichiers utiles.
    /// </summary>
    private static IEnumerable<string> FichiersDeTheme(string theme)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sous in new[] { "views", Path.Combine("subsets", "colorsets"), "subsets" })
        {
            var dossier = Path.Combine(theme, sous);
            if (!Directory.Exists(dossier)) continue;
            foreach (var f in Directory.EnumerateFiles(dossier, "*.xml", SearchOption.AllDirectories)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (vus.Add(f)) yield return f;
            }
        }
        var racine = Path.Combine(theme, "theme.xml");
        if (File.Exists(racine) && vus.Add(racine)) yield return racine;

        var n = 0;
        foreach (var f in Directory.EnumerateFiles(theme, "*.xml", SearchOption.AllDirectories))
        {
            if (++n > 200) yield break;
            if (vus.Add(f)) yield return f;
        }
    }
}
