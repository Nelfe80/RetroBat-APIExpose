using System.Globalization;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// LES PROFILS D'AFFICHAGE DECLARATIFS (API 1.9.212) : le sens du bloc du canal (ecran, rotation, conversion des positions, tables
/// d'ennemis, classes, ROMs couvertes) n'est plus code en dur : il vient de <c>ram/&lt;systeme&gt;/&lt;jeu&gt;.VIEW.json</c>, a cote du
/// <c>.VIEW</c> que le wrapper lit (le wrapper ne connait pas ce fichier : il ouvre <c>&lt;jeu&gt;.VIEW</c> exactement). Le Data Pack le
/// distribue comme un .MEM ; un nouveau jeu n'exige donc plus de release de l'API. Format : <c>docs/PROFIL_AFFICHAGE.md</c>.
///
/// Ordre de recherche : <c>ram/.user/&lt;systeme&gt;</c> (essai du labo, comme le .MEM .user), puis <c>ram/&lt;systeme&gt;</c>. Un fichier
/// invalide est ignore (le jeu n'a alors pas de canal : comportement d'avant) et l'erreur est gardee dans <see cref="Erreurs"/>.
/// </summary>
public static class CanalProfils
{
    public const string Schema = "nelfeplay.view-profile/1";
    public const string Extension = ".VIEW.json";

    private static readonly object Verrou = new();
    private static List<CanalProfilAffichage> _liste = [];
    private static List<string> _erreurs = [];
    private static DateTime _lu = DateTime.MinValue;
    private static string? _luDe;
    private static string? _dossier;

    /// <summary>Le dossier <c>ram</c> a lire (essais : un dossier de test). Par defaut <c>resources\ram</c> de la racine de l'API.</summary>
    public static string DossierRam
    {
        get => _dossier ?? RetroBatPaths.RamResourcesRoot;
        set { lock (Verrou) { _dossier = value; _lu = DateTime.MinValue; } }
    }

    /// <summary>Les fichiers refuses a la derniere lecture (chemin : raison), pour le diagnostic.</summary>
    public static IReadOnlyList<string> Erreurs { get { lock (Verrou) { return [.. _erreurs]; } } }

    public static readonly TimeSpan Fraicheur = TimeSpan.FromSeconds(3);

    /// <summary>Tous les profils lus (relus au plus toutes les 3 s : un fichier pose ou corrige se voit sans relancer l'API).</summary>
    public static IReadOnlyList<CanalProfilAffichage> Tous()
    {
        lock (Verrou)
        {
            var dossier = DossierRam;
            if (_luDe == dossier && DateTime.UtcNow - _lu < Fraicheur) return _liste;
            var liste = new List<CanalProfilAffichage>();
            var erreurs = new List<string>();
            foreach (var f in Fichiers(dossier))
            {
                try
                {
                    var p = DepuisJson(File.ReadAllText(f), out var erreur);
                    if (p is null) erreurs.Add($"{f} : {erreur}");
                    else liste.Add(p);
                }
                catch (Exception ex) { erreurs.Add($"{f} : {ex.Message}"); }
            }

            _liste = liste;
            _erreurs = erreurs;
            _lu = DateTime.UtcNow;
            _luDe = dossier;
            return _liste;
        }
    }

    private static IEnumerable<string> Fichiers(string ram)
    {
        if (!Directory.Exists(ram)) yield break;
        // .user d'abord (essai du labo : prioritaire), puis l'officiel ; les autres couches (.contest, .defis) ne portent pas de profil
        var dossiers = new List<string>();
        var user = Path.Combine(ram, ".user");
        if (Directory.Exists(user)) dossiers.AddRange(Directory.GetDirectories(user));
        dossiers.AddRange(Directory.GetDirectories(ram).Where(d => !Path.GetFileName(d).StartsWith('.')));
        foreach (var d in dossiers)
            foreach (var f in Directory.EnumerateFiles(d, "*" + Extension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                yield return f;
    }

    /// <summary>Le profil du jeu lance (nom de ROM), ou null. Un profil ne vaut que pour les ROMs qu'il liste (contenu prouve).</summary>
    public static CanalProfilAffichage? Pour(string? rom)
    {
        if (string.IsNullOrWhiteSpace(rom)) return null;
        var r = rom.Trim();
        foreach (var p in Tous())
            foreach (var x in p.Roms)
                if (string.Equals(x, r, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Le profil dont <c>view</c> est le nom du .VIEW charge (avec ou sans extension .VIEW), ou null.</summary>
    public static CanalProfilAffichage? PourLaVue(string? nomVue)
    {
        var n = SansExtension(nomVue);
        if (n.Length == 0) return null;
        foreach (var p in Tous())
            if (string.Equals(p.Vue, n, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    public static string SansExtension(string? nomVue)
    {
        var n = (nomVue ?? "").Trim();
        return n.EndsWith(".VIEW", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
    }

    // ---- lecture d'un fichier ------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> CleRacine = ["schema", "id", "view", "view_sha256", "roms", "screen", "ship", "enemy", "camera_scale", "combo_max", "comment"];
    private static readonly HashSet<string> CleEnnemi = ["column_x", "column_y", "x", "y", "drawn_mask", "life_required", "signed", "classes", "comment"];
    private static readonly string[] NomsClasses = ["light", "heavy", "special", "generic"];

    /// <summary>Lit et valide un profil. Null et le motif de refus si le fichier est faux (mot inconnu, champ manquant, valeur hors limite).</summary>
    public static CanalProfilAffichage? DepuisJson(string json, out string erreur)
    {
        erreur = "";
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Refus(out erreur, "la racine doit etre un objet");
            foreach (var c in r.EnumerateObject())
                if (!CleRacine.Contains(c.Name)) return Refus(out erreur, $"cle inconnue « {c.Name} »");
            if (Texte(r, "schema") != Schema) return Refus(out erreur, $"schema attendu « {Schema} »");

            var id = Texte(r, "id");
            var vue = SansExtension(Texte(r, "view"));
            if (!NomValide(id)) return Refus(out erreur, "id invalide");
            if (!NomValide(vue)) return Refus(out erreur, "view invalide (nom du .VIEW sans extension)");

            var roms = new List<string>();
            if (!r.TryGetProperty("roms", out var jr) || jr.ValueKind != JsonValueKind.Array || jr.GetArrayLength() == 0)
                return Refus(out erreur, "roms : liste non vide des ROMs prouvees obligatoire");
            foreach (var x in jr.EnumerateArray())
            {
                var s = x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim().ToLowerInvariant() : "";
                if (!NomValide(s)) return Refus(out erreur, "roms : nom de ROM invalide");
                roms.Add(s);
            }

            List<string>? shas = null;
            if (r.TryGetProperty("view_sha256", out var js))
            {
                if (js.ValueKind != JsonValueKind.Array) return Refus(out erreur, "view_sha256 : liste attendue");
                shas = [];
                foreach (var x in js.EnumerateArray())
                {
                    var s = x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim().ToLowerInvariant() : "";
                    if (s.Length != 64 || !s.All(Uri.IsHexDigit)) return Refus(out erreur, "view_sha256 : empreinte de 64 chiffres hexadecimaux");
                    shas.Add(s);
                }
            }

            if (!r.TryGetProperty("screen", out var sc) || sc.ValueKind != JsonValueKind.Object) return Refus(out erreur, "screen obligatoire");
            var ecranL = Entier(sc, "w", 1, 4096, ref erreur);
            var ecranH = Entier(sc, "h", 1, 4096, ref erreur);
            if (erreur.Length > 0) { erreur = "screen." + erreur; return null; }

            if (!r.TryGetProperty("ship", out var ship) || ship.ValueKind != JsonValueKind.Object) return Refus(out erreur, "ship obligatoire");
            var vx = Axe(ship, "x", ref erreur);
            var vy = Axe(ship, "y", ref erreur);
            if (erreur.Length > 0) { erreur = "ship." + erreur; return null; }

            if (!r.TryGetProperty("enemy", out var en) || en.ValueKind != JsonValueKind.Object) return Refus(out erreur, "enemy obligatoire");
            foreach (var c in en.EnumerateObject())
                if (!CleEnnemi.Contains(c.Name)) return Refus(out erreur, $"enemy : cle inconnue « {c.Name} »");
            var colX = Texte(en, "column_x");
            var colY = Texte(en, "column_y");
            if (!NomValide(colX) || !NomValide(colY)) return Refus(out erreur, "enemy.column_x et enemy.column_y : noms des colonnes de la table d'affichage du .VIEW");
            var ex = Axe(en, "x", ref erreur);
            var ey = Axe(en, "y", ref erreur);
            if (erreur.Length > 0) { erreur = "enemy." + erreur; return null; }
            var masque = en.TryGetProperty("drawn_mask", out var jm) ? Nombre(jm, ref erreur) : 0;
            if (erreur.Length > 0) { erreur = "enemy.drawn_mask : " + erreur; return null; }
            if (masque < 0 || masque > 0xFFFF) return Refus(out erreur, "enemy.drawn_mask : 0 a 0xFFFF");
            var vieRequise = Booleen(en, "life_required");
            var signe = Booleen(en, "signed");

            var classes = new Dictionary<int, int>();
            if (en.TryGetProperty("classes", out var jc))
            {
                if (jc.ValueKind != JsonValueKind.Object) return Refus(out erreur, "enemy.classes : objet attendu");
                foreach (var c in jc.EnumerateObject())
                {
                    var k = Array.IndexOf(NomsClasses, c.Name);
                    if (k < 0) return Refus(out erreur, $"enemy.classes : classe inconnue « {c.Name} » (light, heavy, special, generic)");
                    if (c.Value.ValueKind != JsonValueKind.Array) return Refus(out erreur, $"enemy.classes.{c.Name} : liste attendue");
                    foreach (var t in c.Value.EnumerateArray())
                    {
                        var type = Nombre(t, ref erreur);
                        if (erreur.Length > 0 || type < 0 || type > 0xFFFF) return Refus(out erreur, $"enemy.classes.{c.Name} : type de 0 a 0xFFFF attendu");
                        if (!classes.TryAdd((int)type, k)) return Refus(out erreur, $"enemy.classes : type 0x{type:X} dans deux classes");
                    }
                }
            }

            var camera = r.TryGetProperty("camera_scale", out var jcs) ? Reel(jcs, ref erreur) : 0.0;
            if (erreur.Length > 0) { erreur = "camera_scale : " + erreur; return null; }
            var comboMax = 0;
            if (r.TryGetProperty("combo_max", out var jcm))
            {
                var n = Nombre(jcm, ref erreur);
                if (erreur.Length > 0 || n < 0 || n > 0xFFFFFF) return Refus(out erreur, "combo_max : entier de 0 a 16777215");
                comboMax = (int)n;
            }

            return new CanalProfilAffichage(
                id, ecranL, ecranH, vx.Axe, vy.Axe, colX, colY, ex.Axe, ey.Axe, (int)masque, vieRequise, signe, comboMax, camera, classes, roms, vue, shas);
        }
        catch (JsonException ex) { return Refus(out erreur, "JSON invalide : " + ex.Message); }
    }

    private static CanalProfilAffichage? Refus(out string erreur, string motif) { erreur = motif; return null; }

    private static bool NomValide(string s) => s.Length is > 0 and <= 64 && s.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-');

    private static string Texte(JsonElement o, string cle) => o.TryGetProperty(cle, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static bool Booleen(JsonElement o, string cle) => o.TryGetProperty(cle, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Entier(JsonElement o, string cle, int min, int max, ref string erreur)
    {
        if (!o.TryGetProperty(cle, out var v)) { erreur = $"{cle} obligatoire"; return 0; }
        var n = Nombre(v, ref erreur);
        if (erreur.Length > 0) { erreur = $"{cle} : {erreur}"; return 0; }
        if (n < min || n > max) { erreur = $"{cle} : {min} a {max}"; return 0; }
        return (int)n;
    }

    /// <summary>Un entier : nombre JSON ou chaine « 0x... » / decimale.</summary>
    private static long Nombre(JsonElement v, ref string erreur)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()!.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n)
                : long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
        }

        erreur = "entier attendu (nombre ou chaine 0x...)";
        return 0;
    }

    /// <summary>Un reel : nombre JSON, ou fraction exacte « a/b » (ex. « -1/64 » : evite l'arrondi d'une ecriture decimale).</summary>
    private static double Reel(JsonElement v, ref string erreur)
    {
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()!.Trim();
            var i = s.IndexOf('/');
            if (i > 0)
            {
                if (double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    && double.TryParse(s.AsSpan(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b;
            }
            else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        }

        erreur = "reel attendu (nombre ou fraction « a/b »)";
        return 0;
    }

    private static (CanalAxe Axe, bool Ok) Axe(JsonElement o, string cle, ref string erreur)
    {
        if (!o.TryGetProperty(cle, out var a) || a.ValueKind != JsonValueKind.Object) { erreur = $"{cle} : objet {{scale, offset}} obligatoire"; return (default, false); }
        foreach (var c in a.EnumerateObject())
            if (c.Name is not ("scale" or "offset")) { erreur = $"{cle} : cle inconnue « {c.Name} »"; return (default, false); }
        if (!a.TryGetProperty("scale", out var s) || !a.TryGetProperty("offset", out var d)) { erreur = $"{cle} : scale et offset obligatoires"; return (default, false); }
        var scale = Reel(s, ref erreur);
        if (erreur.Length > 0) { erreur = $"{cle}.scale : {erreur}"; return (default, false); }
        var offset = Reel(d, ref erreur);
        if (erreur.Length > 0) { erreur = $"{cle}.offset : {erreur}"; return (default, false); }
        if (scale == 0 || double.IsNaN(scale) || double.IsInfinity(scale) || double.IsNaN(offset) || double.IsInfinity(offset)) { erreur = $"{cle} : scale non nul et valeurs finies"; return (default, false); }
        return (new CanalAxe(scale, offset), true);
    }
}
