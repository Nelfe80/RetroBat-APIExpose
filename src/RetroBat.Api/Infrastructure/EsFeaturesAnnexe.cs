using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES OPTIONS D'APIEXPOSE DANS LEUR PROPRE FICHIER (2026-10-04, suggestion d'un concepteur de
/// RetroBat). EmulationStation lit, a cote de son es_features.cfg, les fichiers es_features_*.cfg
/// du dossier .emulationstation, et les FUSIONNE : options partagees, places dans le menu global,
/// options ajoutees a un coeur ou a un systeme deja decrits (verifie sur la borne). APIExpose
/// patchait es_features.cfg et les traductions de RetroBat a chaque demarrage, et les defaisait a
/// la fermeture d'ES ; une mise a jour de RetroBat ou un retrait rate laissait des fichiers abimes.
/// Desormais tout tient dans es_features_apiexpose.cfg, et on ne touche plus aux fichiers de RetroBat.
///
/// LES LIBELLES SONT DEJA TRADUITS dans la langue d'ES : nos traductions vivaient dans les .po de
/// RetroBat. Un libelle traduit n'est pas trouve dans le .po, ES l'affiche tel quel. Seuls les noms
/// de GROUPE de RetroBat (« ADVANCED SETTINGS »...) restent en anglais : ES les traduit lui-meme, et
/// un groupe traduit par nous en formerait un second a cote du sien.
/// </summary>
public static class EsFeaturesAnnexe
{
    public const string NomDuFichier = "es_features_apiexpose.cfg";

    /// <summary>Les traductions d'un .po : msgid vers msgstr, sans les entrees non traduites.</summary>
    public static Dictionary<string, string> LirePo(string contenu)
    {
        var traductions = new Dictionary<string, string>(StringComparer.Ordinal);
        string? champ = null;
        var msgid = new StringBuilder();
        var msgstr = new StringBuilder();

        void Ranger()
        {
            if (msgid.Length > 0 && msgstr.Length > 0 && !traductions.ContainsKey(msgid.ToString()))
            {
                traductions[msgid.ToString()] = msgstr.ToString();
            }
            msgid.Clear();
            msgstr.Clear();
            champ = null;
        }

        foreach (var brute in (contenu ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var ligne = brute.Trim();
            if (ligne.Length == 0)
            {
                Ranger();
                continue;
            }
            if (ligne.StartsWith('#')) continue;
            if (ligne.StartsWith("msgctxt", StringComparison.Ordinal))
            {
                Ranger();
                champ = "msgctxt";
                continue;
            }
            if (ligne.StartsWith("msgid ", StringComparison.Ordinal))
            {
                if (champ == "msgstr") Ranger();
                champ = "msgid";
                msgid.Append(Chaine(ligne["msgid ".Length..]));
                continue;
            }
            if (ligne.StartsWith("msgstr ", StringComparison.Ordinal))
            {
                champ = "msgstr";
                msgstr.Append(Chaine(ligne["msgstr ".Length..]));
                continue;
            }
            if (ligne.StartsWith('"'))
            {
                if (champ == "msgid") msgid.Append(Chaine(ligne));
                else if (champ == "msgstr") msgstr.Append(Chaine(ligne));
            }
        }
        Ranger();
        return traductions;
    }

    /// <summary>Le texte d'une chaine .po entre guillemets, sequences d'echappement resolues.</summary>
    private static string Chaine(string guillemets)
    {
        var s = guillemets.Trim();
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return string.Empty;
        s = s[1..^1];
        var sortie = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sortie.Append(s[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => s[i] });
            }
            else
            {
                sortie.Append(s[i]);
            }
        }
        return sortie.ToString();
    }

    /// <summary>
    /// Le dossier de traduction d'une langue d'ES parmi les notres : « fr_FR » exact, sinon « fr »,
    /// sinon le premier « fr_* ». Null pour l'anglais : les libelles d'origine sont anglais.
    /// </summary>
    public static string? DossierDeLangue(IEnumerable<string> dossiers, string? langue)
    {
        var code = (langue ?? string.Empty).Trim().Replace('-', '_');
        if (code.Length < 2 || code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return null;
        var liste = dossiers.ToList();
        var deux = code[..2];
        return liste.FirstOrDefault(d => string.Equals(d, code, StringComparison.OrdinalIgnoreCase))
               ?? liste.FirstOrDefault(d => string.Equals(d, deux, StringComparison.OrdinalIgnoreCase))
               ?? liste.FirstOrDefault(d => d.StartsWith(deux + "_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// L'annexe : nos options partagees, leurs places dans le menu global, et les options du panel
    /// par systeme. Celles-ci sont deja placees dans <paramref name="copieRetroBat"/>, une COPIE du
    /// es_features.cfg de RetroBat (meme logique de placement qu'avant) : on en recopie chacune avec
    /// la chaine de ses parents (emulateur, coeurs, coeur, systeme), que ES fusionne aux siens.
    /// </summary>
    public static XDocument Construire(
        IEnumerable<XElement> options,
        IEnumerable<XElement> placesGlobales,
        XElement? copieRetroBat,
        Func<string?, bool> estUneOptionDuPanel,
        IReadOnlyDictionary<string, string> traductions)
    {
        var groupesDeRetroBat = new HashSet<string>(StringComparer.Ordinal);
        if (copieRetroBat is not null)
        {
            foreach (var g in copieRetroBat.Descendants().Where(e => !estUneOptionDuPanel((string?)e.Attribute("value")))
                         .Select(e => (string?)e.Attribute("group")).OfType<string>())
            {
                groupesDeRetroBat.Add(g);
            }
        }

        var racine = new XElement("features");
        racine.Add(new XElement("sharedFeatures", options.Select(o => Traduire(new XElement(o), traductions, groupesDeRetroBat))));
        racine.Add(new XElement("globalFeatures", placesGlobales.Select(p => Traduire(new XElement(p), traductions, groupesDeRetroBat))));

        if (copieRetroBat is not null)
        {
            foreach (var option in copieRetroBat.Descendants("feature").Where(f => estUneOptionDuPanel((string?)f.Attribute("value"))).ToList())
            {
                var parent = racine;
                foreach (var ancetre in option.Ancestors().TakeWhile(a => a != copieRetroBat).Reverse())
                {
                    var nom = (string?)ancetre.Attribute("name");
                    var existant = parent.Elements(ancetre.Name).FirstOrDefault(e => (string?)e.Attribute("name") == nom);
                    if (existant is null)
                    {
                        existant = new XElement(ancetre.Name);
                        if (nom is not null) existant.SetAttributeValue("name", nom);
                        parent.Add(existant);
                    }
                    parent = existant;
                }
                parent.Add(Traduire(new XElement(option), traductions, groupesDeRetroBat));
            }
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XComment(" Options d'APIExpose pour EmulationStation. Ecrit par APIExpose a chaque demarrage : ne pas modifier. "),
            racine);
    }

    /// <summary>Les libelles d'une option et de ses choix dans la langue d'ES ; les groupes de RetroBat restent ceux d'ES.</summary>
    private static XElement Traduire(XElement element, IReadOnlyDictionary<string, string> traductions, IReadOnlySet<string> groupesDeRetroBat)
    {
        foreach (var e in element.DescendantsAndSelf())
        {
            foreach (var nom in new[] { "name", "description", "submenu", "group" })
            {
                if (e.Attribute(nom) is not { } attribut || attribut.Value.Length == 0) continue;
                if (nom == "group" && groupesDeRetroBat.Contains(attribut.Value)) continue;
                if (traductions.TryGetValue(attribut.Value, out var traduit)) attribut.Value = traduit;
            }
        }
        return element;
    }

    /// <summary>Le texte du fichier : indente, UTF-8 sans BOM, fins de ligne Windows.</summary>
    public static string Serialiser(XDocument document)
    {
        var parametres = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            NewLineChars = "\r\n",
            OmitXmlDeclaration = false,
        };
        using var flux = new MemoryStream();
        using (var ecrivain = XmlWriter.Create(flux, parametres))
        {
            document.Save(ecrivain);
        }
        return new UTF8Encoding(false).GetString(flux.ToArray());
    }
}
