using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE .SMEM, LE SENS DES SIGNAUX (decision du user du 08/11, annexe CDC-NelfePlay-PlayerDeck-Annexe-SMEM.md).
/// Le .MEM est l'ecouteur : toute adresse surveillee y est declaree. Le .SMEM, compagnon pose a cote du .MEM, dit ce que
/// veulent dire ses signaux pour l'affichage (nom, unite, echelle, bornes, etiquettes, regroupement en instruments). Il
/// ne declare AUCUNE adresse a surveiller : chaque signal cite des lignes du .MEM par adresse, action et joueur. Il
/// n'entre ni dans le score ni dans l'empreinte certifiee ; seuls l'API et le PlayerDeck le lisent.
///
/// Syntaxe : une table declarative de forme Lua (<c>return { ... }</c>), comme le .MEM, lue par un lecteur strict qui
/// n'execute rien : chaines entre guillemets, entiers (decimaux ou 0x), true/false, tables. Pas de nil, pas de nombre
/// decimal (les echelles sont rationnelles), pas de fonction, pas d'expression. Toute cle inconnue est une erreur : le
/// format evolue par son numero de schema.
/// </summary>
public static class Smem
{
    public const int Schema = 1;
    public const int TailleMax = 256 * 1024;
    public const int ProfondeurMax = 8;
    public const int ChampsMax = 4096;
    public const int ChaineMax = 512;

    private static readonly Regex Identifiant = new(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$");
    private static readonly Regex ActionValide = new(@"^[A-Z0-9_]+$");
    private static readonly string[] Genres = { "counter", "level", "enum", "flag", "gauge" };
    private static readonly string[] Affichages = { "counter", "level", "gauge", "enum", "state", "text" };
    private static readonly string[] Qualites = { "qualified", "partial", "unproven" };

    /// <summary>
    /// Lit et valide un .SMEM contre le .MEM qu'il accompagne. <paramref name="memTexte"/> null : .MEM introuvable,
    /// chaque reference est alors une erreur. Le resultat porte ses erreurs ; avec une seule erreur il est invalide et
    /// ne publie aucun signal (rien d'affiche plutot qu'un sens faux).
    /// </summary>
    public static SmemResultat Lire(string texte, string? memTexte, string? nomDuMem = null)
    {
        var erreurs = new List<string>();
        var avertissements = new List<string>();
        SmemTable? racine = null;
        if (Encoding.UTF8.GetByteCount(texte) > TailleMax)
        {
            erreurs.Add($"fichier trop gros (plus de {TailleMax} octets)");
        }
        else
        {
            try
            {
                racine = new LecteurSmem(texte).LireFichier();
            }
            catch (SmemSyntaxe ex)
            {
                erreurs.Add(ex.Message);
            }
        }

        if (racine is null)
        {
            return new SmemResultat(null, null, Array.Empty<DeckSemanticSignal>(), Array.Empty<DeckSemanticInstrument>(), erreurs, avertissements);
        }

        var lignes = memTexte is null ? null : LignesDuMem.Lire(memTexte);
        var v = new Validation(erreurs);

        v.Cles(racine, "racine", "schema", "kind", "version", "game", "mem", "signals", "instruments", "spatial");
        var schema = v.Entier(racine, "schema", "racine", obligatoire: true);
        if (schema is not null && schema != Schema) v.Erreur($"racine : schema {schema} inconnu (ce lecteur connait le schema {Schema})");
        var genre = v.Texte(racine, "kind", "racine", obligatoire: true);
        if (genre is not null && genre != "semantics") v.Erreur($"racine : kind \"{genre}\" inconnu (attendu \"semantics\")");
        var version = v.Entier(racine, "version", "racine", obligatoire: true);
        if (version is < 1) v.Erreur("racine : version doit valoir 1 ou plus");
        if (racine.Trouver("spatial") is not null) avertissements.Add("racine : spatial est reserve (partie spatiale a venir), ignore par le schema 1");

        DeckSemanticGame? jeu = null;
        if (v.Table(racine, "game", "racine", obligatoire: true) is { } g)
        {
            v.Cles(g, "game", "system", "slug", "title");
            jeu = new DeckSemanticGame(
                v.Texte(g, "system", "game", obligatoire: true) ?? "",
                v.Texte(g, "slug", "game", obligatoire: true) ?? "",
                v.Texte(g, "title", "game"));
        }

        if (v.Table(racine, "mem", "racine", obligatoire: true) is { } m)
        {
            v.Cles(m, "mem", "file");
            var fichier = v.Texte(m, "file", "mem", obligatoire: true);
            if (fichier is not null && nomDuMem is not null && !string.Equals(fichier, nomDuMem, StringComparison.OrdinalIgnoreCase))
            {
                v.Erreur($"mem : file \"{fichier}\" ne nomme pas le .MEM qu'il accompagne ({nomDuMem})");
            }
        }

        var signaux = new List<DeckSemanticSignal>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (v.Table(racine, "signals", "racine", obligatoire: true) is { } ts)
        {
            v.Liste(ts, "signals");
            var n = 0;
            foreach (var item in ts.Elements())
            {
                n++;
                if (item is not SmemTable s) { v.Erreur($"signals[{n}] : une table est attendue"); continue; }
                var signal = LireSignal(s, n, v, lignes);
                if (signal is null) continue;
                if (!ids.Add(signal.Id)) v.Erreur($"signals[{n}] : id \"{signal.Id}\" en double");
                signaux.Add(signal);
            }
        }

        var instruments = new List<DeckSemanticInstrument>();
        var idsInstr = new HashSet<string>(StringComparer.Ordinal);
        if (v.Table(racine, "instruments", "racine") is { } ti)
        {
            v.Liste(ti, "instruments");
            var n = 0;
            foreach (var item in ti.Elements())
            {
                n++;
                var ou = $"instruments[{n}]";
                if (item is not SmemTable t) { v.Erreur($"{ou} : une table est attendue"); continue; }
                v.Cles(t, ou, "id", "label", "player", "show", "signals");
                var id = v.Texte(t, "id", ou, obligatoire: true);
                if (id is not null && !Identifiant.IsMatch(id)) v.Erreur($"{ou} : id \"{id}\" invalide");
                if (id is not null && !idsInstr.Add(id)) v.Erreur($"{ou} : id \"{id}\" en double");
                var montrer = v.Texte(t, "show", ou, obligatoire: true);
                if (montrer is not null && !Affichages.Contains(montrer)) v.Erreur($"{ou} : show \"{montrer}\" inconnu");
                var joueur = v.Joueur(t, ou);
                var cites = new List<string>();
                if (v.Table(t, "signals", ou, obligatoire: true) is { } liste)
                {
                    v.Liste(liste, ou + ".signals");
                    foreach (var e in liste.Elements())
                    {
                        if (e is not string sid) { v.Erreur($"{ou}.signals : un id de signal (chaine) est attendu"); continue; }
                        if (!ids.Contains(sid)) v.Erreur($"{ou}.signals : signal \"{sid}\" non declare");
                        cites.Add(sid);
                    }
                    if (cites.Count == 0) v.Erreur($"{ou}.signals : au moins un signal");
                }
                instruments.Add(new DeckSemanticInstrument(id ?? "", v.Texte(t, "label", ou), joueur, montrer ?? "", cites));
            }
        }

        if (erreurs.Count > 0)
        {
            return new SmemResultat(jeu, (int?)version, Array.Empty<DeckSemanticSignal>(), Array.Empty<DeckSemanticInstrument>(), erreurs, avertissements);
        }
        return new SmemResultat(jeu, (int?)version, signaux, instruments, erreurs, avertissements);
    }

    private static DeckSemanticSignal? LireSignal(SmemTable s, int n, Validation v, LignesDuMem? lignes)
    {
        var ou = $"signals[{n}]";
        v.Cles(s, ou, "id", "player", "kind", "unit", "label", "short", "from", "values", "scale", "min", "max", "initial", "quality", "proof");
        var id = v.Texte(s, "id", ou, obligatoire: true);
        if (id is null) return null;
        if (!Identifiant.IsMatch(id)) v.Erreur($"{ou} : id \"{id}\" invalide");
        ou = $"signals[{n}] ({id})";
        var genre = v.Texte(s, "kind", ou, obligatoire: true);
        if (genre is not null && !Genres.Contains(genre)) v.Erreur($"{ou} : kind \"{genre}\" inconnu");
        var qualite = v.Texte(s, "quality", ou, obligatoire: true);
        if (qualite is not null && !Qualites.Contains(qualite)) v.Erreur($"{ou} : quality \"{qualite}\" inconnue");
        var joueur = v.Joueur(s, ou);
        var min = v.Entier(s, "min", ou);
        var max = v.Entier(s, "max", ou);
        if (min is not null && max is not null && min > max) v.Erreur($"{ou} : min {min} plus grand que max {max}");
        if (genre == "gauge" && max is null) v.Erreur($"{ou} : une jauge exige un max");
        var initial = v.Entier(s, "initial", ou);

        var echelle = new DeckSemanticScale(1, 1, 0);
        if (v.Table(s, "scale", ou) is { } e)
        {
            v.Cles(e, ou + ".scale", "num", "den", "add");
            var num = v.Entier(e, "num", ou + ".scale") ?? 1;
            var den = v.Entier(e, "den", ou + ".scale") ?? 1;
            var add = v.Entier(e, "add", ou + ".scale") ?? 0;
            if (den == 0) v.Erreur($"{ou}.scale : den ne peut pas valoir 0");
            echelle = new DeckSemanticScale(num, den, add);
        }

        var valeurs = new List<DeckSemanticValue>();
        if (v.Table(s, "values", ou) is { } tv)
        {
            v.Liste(tv, ou + ".values");
            var k = 0;
            foreach (var item in tv.Elements())
            {
                k++;
                var ouv = $"{ou}.values[{k}]";
                if (item is not SmemTable t) { v.Erreur($"{ouv} : une table est attendue"); continue; }
                v.Cles(t, ouv, "raw", "min", "max", "value", "label");
                var brute = v.Entier(t, "raw", ouv);
                var bmin = v.Entier(t, "min", ouv);
                var bmax = v.Entier(t, "max", ouv);
                if (brute is null == (bmin is null || bmax is null)) v.Erreur($"{ouv} : raw, ou bien min et max, exactement");
                if (brute is not null && (bmin is not null || bmax is not null)) v.Erreur($"{ouv} : raw et min/max ensemble");
                if (bmin is not null && bmax is not null && bmin > bmax) v.Erreur($"{ouv} : min plus grand que max");
                var etiquette = v.Texte(t, "label", ouv, obligatoire: true);
                valeurs.Add(new DeckSemanticValue(brute, bmin, bmax, v.Entier(t, "value", ouv), etiquette ?? ""));
            }
            for (var a = 0; a < valeurs.Count; a++)
            {
                for (var b = a + 1; b < valeurs.Count; b++)
                {
                    if (Recouvre(valeurs[a], valeurs[b])) v.Erreur($"{ou}.values : les entrees {a + 1} et {b + 1} se recouvrent");
                }
            }
        }

        var sources = new List<DeckSemanticRef>();
        var disponible = qualite != "unproven";
        if (v.Table(s, "from", ou, obligatoire: true) is { } tf)
        {
            v.Liste(tf, ou + ".from");
            var k = 0;
            foreach (var item in tf.Elements())
            {
                k++;
                var our = $"{ou}.from[{k}]";
                if (item is not SmemTable t) { v.Erreur($"{our} : une table est attendue"); continue; }
                v.Cles(t, our, "address", "action", "player");
                var adresse = v.Entier(t, "address", our, obligatoire: true);
                var action = v.Texte(t, "action", our, obligatoire: true);
                if (action is not null && !ActionValide.IsMatch(action)) v.Erreur($"{our} : action \"{action}\" invalide");
                var jr = v.Joueur(t, our);
                if (adresse is null || action is null) continue;
                sources.Add(new DeckSemanticRef(Hex.Ecrire(adresse.Value), action, jr));
                if (lignes is null)
                {
                    v.Erreur($"{our} : .MEM introuvable, la ligne {Hex.Ecrire(adresse.Value)} {action} ne peut pas etre verifiee");
                    continue;
                }
                var ligne = lignes.Lignes.FirstOrDefault(l => l.Adresse == adresse && l.Action == action && l.Joueur == jr);
                if (ligne is null)
                {
                    var joueurTexte = jr is null ? "sans joueur" : $"joueur {jr}";
                    v.Erreur($"{our} : aucune ligne du .MEM {Hex.Ecrire(adresse.Value)} {action} {joueurTexte}");
                }
                else if (ligne.Muette)
                {
                    v.Erreur($"{our} : la ligne {Hex.Ecrire(adresse.Value)} {action} est muette (no_log ou no_survey), elle ne parlera jamais");
                }
            }
            if (k == 0) v.Erreur($"{ou}.from : au moins une ligne du .MEM");
        }

        return new DeckSemanticSignal(
            Id: id,
            Player: joueur,
            Kind: genre ?? "",
            Unit: v.Texte(s, "unit", ou),
            Label: v.Texte(s, "label", ou),
            Short: v.Texte(s, "short", ou),
            From: sources,
            Values: valeurs,
            Scale: echelle,
            Min: min,
            Max: max,
            Initial: initial,
            Quality: qualite ?? "",
            Available: disponible);
    }

    private static bool Recouvre(DeckSemanticValue a, DeckSemanticValue b)
    {
        var (a1, a2) = a.Raw is { } ra ? (ra, ra) : (a.RawMin ?? 0, a.RawMax ?? 0);
        var (b1, b2) = b.Raw is { } rb ? (rb, rb) : (b.RawMin ?? 0, b.RawMax ?? 0);
        return a1 <= b2 && b1 <= a2;
    }

    /// <summary>
    /// Le message deck.semantics d'un lancement : trouve le .SMEM du .MEM en vigueur, le lit et le valide contre ce
    /// .MEM. Ne leve jamais : un fichier illisible donne <c>Status: "invalid"</c> avec la raison.
    /// </summary>
    public static DeckSemantics Semantique(string launchId, string systemId, string rom, string? memRom,
        string? definitionFile, string ramRoot, long revision)
    {
        var chemin = Trouver(definitionFile, ramRoot);
        if (chemin is null)
        {
            return new DeckSemantics(SchemaDuMessage, launchId, systemId, rom, memRom, definitionFile, null, "absent", null, null,
                null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<DeckSemanticSignal>(),
                Array.Empty<DeckSemanticInstrument>(), revision);
        }

        try
        {
            var octets = File.ReadAllBytes(chemin);
            byte[]? mem = null;
            if (!string.IsNullOrWhiteSpace(definitionFile) && File.Exists(definitionFile)) mem = File.ReadAllBytes(definitionFile);
            var lu = Lire(Encoding.UTF8.GetString(octets), mem is null ? null : Encoding.UTF8.GetString(mem),
                definitionFile is null ? null : Path.GetFileName(definitionFile));
            return new DeckSemantics(SchemaDuMessage, launchId, systemId, rom, memRom, definitionFile, chemin,
                lu.Valide ? "loaded" : "invalid", Empreinte(octets), mem is null ? null : Empreinte(mem), lu.Version, lu.Game,
                lu.Errors, lu.Warnings, lu.Signals, lu.Instruments, revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeckSemantics(SchemaDuMessage, launchId, systemId, rom, memRom, definitionFile, chemin, "invalid", null, null,
                null, null, new[] { "lecture impossible : " + ex.Message }, Array.Empty<string>(),
                Array.Empty<DeckSemanticSignal>(), Array.Empty<DeckSemanticInstrument>(), revision);
        }
    }

    public const string SchemaDuMessage = "nelfeplay.deck.semantics/1";

    /// <summary>L'empreinte des octets lus, pour dire au Deck quel fichier il affiche (n'entre dans aucun certificat).</summary>
    public static string Empreinte(byte[] octets) => Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

    /// <summary>
    /// Le .SMEM d'un .MEM en vigueur : a cote de lui (meme couche : officielle, .user, .contest), sinon celui de la
    /// couche officielle (<c>ram\&lt;systeme&gt;\&lt;jeu&gt;.SMEM</c>). Null sans fichier.
    /// </summary>
    public static string? Trouver(string? definitionFile, string ramRoot)
    {
        if (string.IsNullOrWhiteSpace(definitionFile)) return null;
        try
        {
            var voisin = Path.ChangeExtension(definitionFile, ".SMEM");
            if (File.Exists(voisin)) return voisin;
            var systeme = Path.GetFileName(Path.GetDirectoryName(definitionFile));
            if (string.IsNullOrEmpty(systeme)) return null;
            var officiel = Path.Combine(ramRoot, systeme, Path.GetFileNameWithoutExtension(definitionFile) + ".SMEM");
            return File.Exists(officiel) ? officiel : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class Validation(List<string> erreurs)
    {
        public void Erreur(string message) => erreurs.Add(message);

        public void Cles(SmemTable t, string ou, params string[] permises)
        {
            foreach (var (cle, _, ligne) in t.Champs)
            {
                if (cle is not null && !permises.Contains(cle)) erreurs.Add($"{ou} : cle \"{cle}\" inconnue (ligne {ligne})");
            }
        }

        public void Liste(SmemTable t, string ou)
        {
            if (t.Champs.Any(c => c.Cle is not null)) erreurs.Add($"{ou} : une liste est attendue, sans cle");
        }

        public long? Entier(SmemTable t, string cle, string ou, bool obligatoire = false)
        {
            var v = t.Trouver(cle);
            if (v is null) { if (obligatoire) erreurs.Add($"{ou} : {cle} manquant"); return null; }
            if (v is long n) return n;
            erreurs.Add($"{ou} : {cle} doit etre un entier");
            return null;
        }

        public string? Texte(SmemTable t, string cle, string ou, bool obligatoire = false)
        {
            var v = t.Trouver(cle);
            if (v is null) { if (obligatoire) erreurs.Add($"{ou} : {cle} manquant"); return null; }
            if (v is string s) return s;
            erreurs.Add($"{ou} : {cle} doit etre une chaine");
            return null;
        }

        public SmemTable? Table(SmemTable t, string cle, string ou, bool obligatoire = false)
        {
            var v = t.Trouver(cle);
            if (v is null) { if (obligatoire) erreurs.Add($"{ou} : {cle} manquant"); return null; }
            if (v is SmemTable st) return st;
            erreurs.Add($"{ou} : {cle} doit etre une table");
            return null;
        }

        public int? Joueur(SmemTable t, string ou)
        {
            var j = Entier(t, "player", ou);
            if (j is null) return null;
            if (j is < 1 or > 4) { erreurs.Add($"{ou} : player {j} hors de 1 a 4"); return null; }
            return (int)j;
        }
    }
}

/// <summary>Une table lue : ses champs dans l'ordre du fichier (cle null = element de liste).</summary>
public sealed class SmemTable
{
    public List<(string? Cle, object Valeur, int Ligne)> Champs { get; } = new();

    public object? Trouver(string cle) => Champs.FirstOrDefault(c => c.Cle == cle).Valeur;

    public IEnumerable<object> Elements() => Champs.Where(c => c.Cle is null).Select(c => c.Valeur);
}

public sealed class SmemSyntaxe(string message) : Exception(message);

/// <summary>Le lecteur strict : jetons, tables, rien d'execute.</summary>
internal sealed class LecteurSmem(string texte)
{
    private int _i;
    private int _ligne = 1;
    private int _champs;

    public SmemTable LireFichier()
    {
        Blancs();
        if (!Mot("return")) throw Faute("le fichier doit commencer par « return { »");
        Blancs();
        var t = LireTable(1);
        Blancs();
        if (_i < texte.Length) throw Faute("texte apres la table finale");
        return t;
    }

    private SmemTable LireTable(int profondeur)
    {
        if (profondeur > Smem.ProfondeurMax) throw Faute($"tables imbriquees trop profondes (plus de {Smem.ProfondeurMax})");
        Attendre('{');
        var t = new SmemTable();
        var vues = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Blancs();
            if (Courant == '}') { _i++; return t; }
            if (++_champs > Smem.ChampsMax) throw Faute($"plus de {Smem.ChampsMax} champs");
            var ligne = _ligne;
            string? cle = null;
            if (char.IsLetter(Courant) || Courant == '_')
            {
                var debut = _i;
                var nom = Nom();
                Blancs();
                if (Courant == '=')
                {
                    _i++;
                    cle = nom;
                    if (!vues.Add(nom)) throw Faute($"cle \"{nom}\" en double dans la meme table");
                    Blancs();
                }
                else
                {
                    _i = debut; // true / false en element de liste
                }
            }
            var valeur = LireValeur(profondeur);
            t.Champs.Add((cle, valeur, ligne));
            Blancs();
            if (Courant == ',') { _i++; continue; }
            if (Courant == '}') continue;
            throw Faute("« , » ou « } » attendu");
        }
    }

    private object LireValeur(int profondeur)
    {
        var c = Courant;
        if (c == '{') return LireTable(profondeur + 1);
        if (c == '"') return Chaine();
        if (c == '-' || char.IsDigit(c)) return Nombre();
        if (char.IsLetter(c))
        {
            var mot = Nom();
            return mot switch
            {
                "true" => true,
                "false" => false,
                _ => throw Faute($"valeur « {mot} » refusee (ni nil, ni fonction, ni nom : chaine, entier, true, false ou table)")
            };
        }
        throw Faute($"valeur attendue, « {c} » trouve");
    }

    private string Chaine()
    {
        _i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= texte.Length || texte[_i] == '\n') throw Faute("chaine non fermee");
            var c = texte[_i++];
            if (c == '"') break;
            if (c == '\\')
            {
                if (_i >= texte.Length) throw Faute("chaine non fermee");
                var e = texte[_i++];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => throw Faute($"echappement \\{e} refuse") });
            }
            else sb.Append(c);
            if (sb.Length > Smem.ChaineMax) throw Faute($"chaine de plus de {Smem.ChaineMax} caracteres");
        }
        return sb.ToString();
    }

    private object Nombre()
    {
        var debut = _i;
        var negatif = Courant == '-';
        if (negatif) _i++;
        if (Courant == '0' && _i + 1 < texte.Length && (texte[_i + 1] == 'x' || texte[_i + 1] == 'X'))
        {
            _i += 2;
            var d = _i;
            while (_i < texte.Length && Uri.IsHexDigit(texte[_i])) _i++;
            if (_i == d) throw Faute("nombre hexadecimal vide");
            var n = long.Parse(texte[d.._i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return negatif ? -n : n;
        }
        var dd = _i;
        while (_i < texte.Length && char.IsDigit(texte[_i])) _i++;
        if (_i == dd) throw Faute("nombre attendu");
        if (_i < texte.Length && (texte[_i] == '.' || texte[_i] == 'e' || texte[_i] == 'E'))
            throw Faute("nombre decimal refuse : les echelles sont rationnelles (scale = { num, den, add })");
        if (!long.TryParse(texte[debut.._i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
            throw Faute("entier hors bornes");
        return v;
    }

    private string Nom()
    {
        var d = _i;
        while (_i < texte.Length && (char.IsLetterOrDigit(texte[_i]) || texte[_i] == '_')) _i++;
        return texte[d.._i];
    }

    private bool Mot(string mot)
    {
        if (string.CompareOrdinal(texte, _i, mot, 0, mot.Length) != 0) return false;
        var fin = _i + mot.Length;
        if (fin < texte.Length && (char.IsLetterOrDigit(texte[fin]) || texte[fin] == '_')) return false;
        _i = fin;
        return true;
    }

    private void Attendre(char c)
    {
        if (Courant != c) throw Faute($"« {c} » attendu");
        _i++;
    }

    /// <summary>Espaces et commentaires « -- » jusqu'a la fin de la ligne (pas de commentaire long).</summary>
    private void Blancs()
    {
        while (_i < texte.Length)
        {
            var c = texte[_i];
            if (c == '\n') { _ligne++; _i++; }
            else if (char.IsWhiteSpace(c) || c == '﻿') _i++;
            else if (c == '-' && _i + 1 < texte.Length && texte[_i + 1] == '-')
            {
                while (_i < texte.Length && texte[_i] != '\n') _i++;
            }
            else break;
        }
    }

    private char Courant => _i < texte.Length ? texte[_i] : '\0';

    private SmemSyntaxe Faute(string message) => new($"ligne {_ligne} : {message}");
}

/// <summary>Le resultat de la lecture : le sens a publier, ou les erreurs qui l'empechent.</summary>
public sealed record SmemResultat(
    DeckSemanticGame? Game,
    int? Version,
    IReadOnlyList<DeckSemanticSignal> Signals,
    IReadOnlyList<DeckSemanticInstrument> Instruments,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool Valide => Errors.Count == 0;
}

// ─────────────────────────────── message deck.semantics (contrat additif) ───────────────────────────────

/// <summary>
/// deck.semantics : le sens des signaux du jeu lance, lu dans son .SMEM. <c>Status</c> = loaded, absent (pas de
/// .SMEM : le Deck garde ses liaisons par adresse) ou invalid (<c>Errors</c> dit pourquoi ; aucun signal publie).
/// </summary>
public sealed record DeckSemantics(
    string Schema,
    string LaunchId,
    string SystemId,
    string Rom,
    string? MemRom,
    string? DefinitionFile,
    string? SmemFile,
    string Status,
    string? SmemSha256,
    string? MemSha256,
    int? Version,
    DeckSemanticGame? Game,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DeckSemanticSignal> Signals,
    IReadOnlyList<DeckSemanticInstrument> Instruments,
    long Revision);

public sealed record DeckSemanticGame(string System, string Slug, string? Title);

/// <summary>Une ligne du .MEM citee : adresse (« 0xFF8305 »), action, joueur (null : la ligne n'a pas de champ player).</summary>
public sealed record DeckSemanticRef(string Address, string Action, int? Player);

/// <summary>Une entree d'enumeration : valeur brute exacte (<c>Raw</c>) ou plage (<c>RawMin</c>..<c>RawMax</c>).</summary>
public sealed record DeckSemanticValue(long? Raw, long? RawMin, long? RawMax, long? Value, string Label);

/// <summary>Echelle rationnelle : affiche = brute * Num / Den + Add.</summary>
public sealed record DeckSemanticScale(long Num, long Den, long Add);

public sealed record DeckSemanticSignal(
    string Id,
    int? Player,
    string Kind,
    string? Unit,
    string? Label,
    string? Short,
    IReadOnlyList<DeckSemanticRef> From,
    IReadOnlyList<DeckSemanticValue> Values,
    DeckSemanticScale Scale,
    long? Min,
    long? Max,
    long? Initial,
    string Quality,
    bool Available);

public sealed record DeckSemanticInstrument(string Id, string? Label, int? Player, string Show, IReadOnlyList<string> Signals);
