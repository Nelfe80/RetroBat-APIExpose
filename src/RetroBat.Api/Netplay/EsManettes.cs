using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// LES MANETTES D'UN LANCEMENT DIRECT, COMME EMULATIONSTATION LES DONNERAIT (2026-10-26, rapport de falken : Blazing
/// Star en MULTIPLAYER, manette muette, clavier seul).
///
/// Le lanceur de RetroBat ne cherche aucune manette lui-meme : il prend les arguments `-pN…` qu'ES lui passe, et sans
/// eux il efface les touches de manette de retroarch.cfg. L'API les relisait dans la ligne de lancement qu'ES ecrit
/// dans es_log.txt. Mais ES n'ecrit cette ligne qu'en `LogLevel` « information » ou « debug » ; sans reglage (une
/// installation normale), il n'ecrit que ses erreurs (`Log::init`). Chez les joueurs, le MULTIPLAYER partait donc sans
/// aucune manette ; la borne du developpement, en debug, ne le voyait pas.
///
/// Trois sources, dans cet ordre :
/// 1. Le CALCUL D'ES refait au moment du lancement (<see cref="EsManettesSdl"/>, dans un processus a part) : les
///    manettes branchees, retenues et rangees par joueur comme ES le fait (InputManager::computePlayersConfigs).
/// 2. Le DERNIER LANCEMENT D'ES lu dans les journaux (emulatorLauncher.log, toujours ecrit, puis es_log), sans les
///    manettes qui ne sont plus branchees.
/// 3. Rien : la partie part au clavier, et le joueur en est prevenu dans RetroArch.
/// </summary>
public static class EsManettes
{
    /// <summary>L'argument du processus enfant qui enumere les manettes avec le SDL d'ES.</summary>
    public const string Argument = "--manettes-es";

    /// <summary>MAX_PLAYERS d'ES (InputConfig.h).</summary>
    public const int JoueursMax = 8;

    private static readonly TimeSpan PatienceDuCalcul = TimeSpan.FromSeconds(10);

    /// <summary>Une manette vue par le SDL d'ES, avec les valeurs qu'ES passerait au lanceur.</summary>
    public sealed record Appareil(int Index, string Guid, string Nom, string Chemin, int Boutons, int Chapeaux, int Axes, bool MappageSdl);

    /// <summary>Les arguments `-pN…` retenus, et d'ou ils viennent (pour le journal).</summary>
    public sealed record Choix(string Arguments, string Source);

    /// <summary>
    /// Le processus enfant : enumere les manettes avec le SDL d'ES et ecrit la liste en JSON sur sa sortie. Il ne
    /// demarre rien d'autre de l'API.
    /// </summary>
    public static int Executer()
    {
        try
        {
            var reglages = LireReglages(RetroBatPaths.EmulationStationSettingsPath);
            var appareils = EsManettesSdl.Enumerer(
                RetroBatPaths.RetroBatRoot,
                Booleen(reglages, "HidJoysticks", true),
                Booleen(reglages, "BackgroundJoystickInput", false));
            var sortie = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            sortie.Write(JsonSerializer.Serialize(appareils));
            sortie.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + " : " + ex.Message);
            return 2;
        }
    }

    /// <summary>Les manettes a passer au lanceur pour une partie lancee sans ES.</summary>
    public static Choix PourLancer(ILogger journal)
    {
        var calcul = Calculer(journal);
        var dernier = SansLesAbsentes(EsLaunchArguments.ManettesDuDernierLancement(), PeripheriquesWindows.EstBranche);
        if (calcul is { Length: > 0 })
        {
            // Le calcul fait foi ; un ecart avec la ligne d'ES se journalise, c'est ce qui le verifie sur le parc.
            if (dernier.Length > 0 && SansIndex(calcul) != SansIndex(dernier))
            {
                journal.LogInformation("Manettes : le calcul d'ES ({Calcul}) differe du dernier lancement d'ES ({Dernier})", calcul, dernier);
            }
            return new Choix(calcul, "calcul d'ES");
        }

        return dernier.Length > 0 ? new Choix(dernier, "dernier lancement d'ES") : new Choix(string.Empty, "aucune source");
    }

    /// <summary>Le calcul d'ES, ou null s'il n'a pas pu se faire (pas de processus enfant, SDL absent).</summary>
    private static string? Calculer(ILogger journal)
    {
        var appareils = EnumererAPart(journal);
        if (appareils is null)
        {
            return null;
        }

        XDocument? esInput = null;
        try
        {
            var chemin = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_input.cfg");
            if (File.Exists(chemin))
            {
                esInput = XDocument.Load(chemin);
            }
        }
        catch (Exception ex)
        {
            journal.LogInformation("Manettes : es_input.cfg illisible ({Erreur}), seules les manettes connues de SDL comptent", ex.Message);
        }

        var reglages = LireReglages(RetroBatPaths.EmulationStationSettingsPath);
        var joueurs = AttribuerLesJoueurs(Configurees(appareils, esInput), cle => reglages.GetValueOrDefault(cle, string.Empty));
        return Composer(joueurs);
    }

    /// <summary>Lance le processus enfant et lit sa liste. Null hors de RetroBat.Api.exe (tests) ou s'il echoue.</summary>
    private static List<Appareil>? EnumererAPart(ILogger journal)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !string.Equals(Path.GetFileNameWithoutExtension(exe), "RetroBat.Api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var processus = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Argument,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (processus is null)
            {
                return null;
            }

            var sortie = processus.StandardOutput.ReadToEndAsync();
            var erreurs = processus.StandardError.ReadToEndAsync();
            if (!processus.WaitForExit(PatienceDuCalcul))
            {
                try { processus.Kill(); } catch (Exception) { /* deja sorti */ }
                journal.LogWarning("Manettes : le calcul d'ES n'a pas repondu en {Secondes} s", PatienceDuCalcul.TotalSeconds);
                return null;
            }
            if (processus.ExitCode != 0)
            {
                journal.LogWarning("Manettes : le calcul d'ES a echoue (code {Code}) : {Erreur}", processus.ExitCode, erreurs.Result.Trim());
                return null;
            }

            return JsonSerializer.Deserialize<List<Appareil>>(sortie.Result);
        }
        catch (Exception ex)
        {
            journal.LogWarning(ex, "Manettes : le calcul d'ES n'a pas pu se faire");
            return null;
        }
    }

    /// <summary>
    /// Les manettes qu'ES retient (InputManager::loadInputConfig, Windows) : une fiche d'es_input.cfg au meme GUID ET
    /// au meme nom, avec au moins une touche. Sans fiche, ES en cree une depuis le mappage de SDL quand SDL en a un.
    /// </summary>
    internal static List<Appareil> Configurees(IEnumerable<Appareil> appareils, XDocument? esInput)
    {
        var fiches = esInput?.Root?.Elements("inputConfig").ToList() ?? [];
        var retenues = new List<Appareil>();
        foreach (var appareil in appareils)
        {
            var fiche = fiches.FirstOrDefault(f =>
                string.Equals((string?)f.Attribute("deviceGUID"), appareil.Guid, StringComparison.Ordinal) &&
                string.Equals((string?)f.Attribute("deviceName"), appareil.Nom, StringComparison.Ordinal));
            if (fiche is not null ? fiche.Elements("input").Any() : appareil.MappageSdl)
            {
                retenues.Add(appareil);
            }
        }
        return retenues;
    }

    /// <summary>
    /// Les joueurs d'ES (InputManager::computePlayersConfigs) : d'abord le chemin choisi pour le joueur dans ES
    /// (`INPUT P1PATH`), puis GUID et nom, puis le nom seul, puis les manettes libres rangees par chemin ; un trou est
    /// comble par le dernier joueur. `reglage` rend la valeur d'es_settings, vide si elle manque.
    /// </summary>
    internal static Appareil?[] AttribuerLesJoueurs(IReadOnlyList<Appareil> configurees, Func<string, string> reglage)
    {
        var libres = configurees
            .OrderBy(a => CheminDeTri(a.Chemin), StringComparer.Ordinal)
            .ThenBy(a => a.Index)
            .ToList();
        var joueurs = new Appareil?[JoueursMax];

        void Prendre(int joueur, Func<Appareil, bool> critere)
        {
            var trouvee = libres.FirstOrDefault(critere);
            if (trouvee is not null)
            {
                libres.Remove(trouvee);
                joueurs[joueur] = trouvee;
            }
        }

        for (var j = 0; j < JoueursMax; j++)
        {
            var chemin = reglage($"INPUT P{j + 1}PATH");
            if (chemin.Length > 0)
            {
                Prendre(j, a => CheminDeTri(a.Chemin) == chemin);
            }
        }
        for (var j = 0; j < JoueursMax; j++)
        {
            if (joueurs[j] is null)
            {
                var nom = reglage($"INPUT P{j + 1}NAME");
                var guid = reglage($"INPUT P{j + 1}GUID");
                Prendre(j, a => a.Nom == nom && a.Guid == guid);
            }
        }
        for (var j = 0; j < JoueursMax; j++)
        {
            if (joueurs[j] is null)
            {
                var nom = reglage($"INPUT P{j + 1}NAME");
                Prendre(j, a => a.Nom == nom);
            }
        }
        for (var j = 0; j < JoueursMax; j++)
        {
            if (joueurs[j] is null)
            {
                Prendre(j, _ => true);
            }
        }
        // Un trou (joueur 1 vide, joueur 4 pris) : ES y descend le DERNIER joueur pris.
        for (var j = 0; j < JoueursMax; j++)
        {
            for (var autre = JoueursMax - 1; autre > j && joueurs[j] is null; autre--)
            {
                if (joueurs[autre] is not null)
                {
                    joueurs[j] = joueurs[autre];
                    joueurs[autre] = null;
                }
            }
        }
        return joueurs;
    }

    /// <summary>
    /// Le chemin de tri d'ES (InputConfig::getSortDevicePath) : pour « USB\… », la fin apres le dernier « &amp; » seulement,
    /// le debut changeant d'un demarrage a l'autre.
    /// </summary>
    internal static string CheminDeTri(string chemin)
    {
        if (chemin.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            var dernierSeparateur = chemin.LastIndexOf('\\');
            var dernierEt = chemin.LastIndexOf('&');
            if (dernierSeparateur >= 0 && dernierEt > dernierSeparateur)
            {
                return chemin[..(dernierSeparateur + 1)] + chemin[(dernierEt + 1)..];
            }
        }
        return chemin;
    }

    /// <summary>Les arguments du lanceur, au format exact d'ES (InputManager::configureEmulators, Windows).</summary>
    internal static string Composer(IReadOnlyList<Appareil?> joueurs)
    {
        var morceaux = new List<string>();
        for (var j = 0; j < joueurs.Count; j++)
        {
            if (joueurs[j] is not { } a)
            {
                continue;
            }
            var p = "-p" + (j + 1);
            morceaux.AddRange(
            [
                p + "index", a.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                p + "guid", a.Guid,
                p + "path", '"' + a.Chemin + '"',
                p + "name", '"' + a.Nom + '"',
                p + "nbbuttons", a.Boutons.ToString(System.Globalization.CultureInfo.InvariantCulture),
                p + "nbhats", a.Chapeaux.ToString(System.Globalization.CultureInfo.InvariantCulture),
                p + "nbaxes", a.Axes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);
        }
        return string.Join(' ', morceaux);
    }

    /// <summary>
    /// Retire d'une ligne d'ES les manettes que Windows dit debranchees (`branche` rend faux), et renumerote les
    /// suivantes : la premiere manette encore la devient le joueur 1. Une manette dont on ne sait rien reste.
    /// </summary>
    internal static string SansLesAbsentes(string manettes, Func<string, bool?> branche)
    {
        var joueurs = new SortedDictionary<int, List<(string Cle, string Valeur)>>();
        foreach (Match m in ArgumentDeManette.Matches(manettes ?? string.Empty))
        {
            var numero = int.Parse(m.Groups["joueur"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (!joueurs.TryGetValue(numero, out var liste))
            {
                joueurs[numero] = liste = [];
            }
            liste.Add((m.Groups["cle"].Value, m.Groups["valeur"].Value));
        }

        var morceaux = new List<string>();
        var nouveau = 0;
        foreach (var liste in joueurs.Values)
        {
            var chemin = liste.FirstOrDefault(x => x.Cle == "path").Valeur?.Trim('"') ?? string.Empty;
            if (chemin.Length > 0 && branche(chemin) == false)
            {
                continue;
            }
            nouveau++;
            foreach (var (cle, valeur) in liste)
            {
                morceaux.Add("-p" + nouveau + cle);
                morceaux.Add(valeur);
            }
        }
        return string.Join(' ', morceaux);
    }

    /// <summary>Deux listes de manettes sans leurs index SDL, qu'ES et le calcul peuvent numeroter autrement.</summary>
    private static string SansIndex(string manettes)
        => Regex.Replace(manettes, "-p\\d+index\\s+\\S+\\s*", string.Empty, RegexOptions.CultureInvariant).Trim();

    /// <summary>Les reglages d'es_settings.cfg (nom → valeur), vides s'il est illisible.</summary>
    internal static Dictionary<string, string> LireReglages(string chemin)
    {
        var reglages = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(chemin))
            {
                return reglages;
            }
            foreach (var element in XDocument.Load(chemin).Root?.Elements() ?? [])
            {
                if ((string?)element.Attribute("name") is { } nom && (string?)element.Attribute("value") is { } valeur)
                {
                    reglages[nom] = valeur;
                }
            }
        }
        catch (Exception)
        {
            // Illisible : les valeurs par defaut d'ES.
        }
        return reglages;
    }

    private static bool Booleen(Dictionary<string, string> reglages, string nom, bool parDefaut)
        => reglages.TryGetValue(nom, out var valeur) ? string.Equals(valeur, "true", StringComparison.OrdinalIgnoreCase) : parDefaut;

    /// <summary>Un argument `-p&lt;joueur&gt;&lt;cle&gt; valeur`, la valeur entre guillemets ou en un mot.</summary>
    private static readonly Regex ArgumentDeManette = new(
        "-p(?<joueur>\\d+)(?<cle>[a-zA-Z]+)\\s+(?<valeur>\"[^\"]*\"|[^\\s\"]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

/// <summary>Ce que Windows sait d'un peripherique, par son identifiant d'instance (cfgmgr32).</summary>
internal static class PeripheriquesWindows
{
    private const int CrSucces = 0x00;
    private const int CrPasDeNoeud = 0x0D;   // CR_NO_SUCH_DEVNODE : le peripherique n'est pas branche
    private const uint LocaliserNormal = 0;  // CM_LOCATE_DEVNODE_NORMAL : les peripheriques presents seulement

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint noeud, string identifiant, uint options);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint noeud, uint options);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint noeud, char[] tampon, int taille, uint options);

    /// <summary>
    /// Vrai si le peripherique d'un chemin d'ES est branche, faux s'il ne l'est pas, null si on ne peut pas le dire
    /// (chemin « 0000@guid » d'un SDL sans chemins, identifiant que Windows ne reconnait pas).
    /// </summary>
    public static bool? EstBranche(string chemin)
    {
        var identifiant = Instance(chemin);
        if (identifiant.Length == 0)
        {
            return null;
        }
        try
        {
            return CM_Locate_DevNodeW(out _, identifiant, LocaliserNormal) switch
            {
                CrSucces => true,
                CrPasDeNoeud => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// L'identifiant d'instance d'un chemin d'ES : sans le « #COLnn » qu'ES ajoute aux boitiers a plusieurs
    /// collections, et pour un chemin d'interface HID (« \\?\HID#…#{classe} »), l'instance qu'il designe. Vide pour un
    /// chemin « 0000@guid ».
    /// </summary>
    internal static string Instance(string chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin) || !chemin.Contains('\\'))
        {
            return string.Empty;
        }
        var identifiant = Regex.Replace(chemin.Trim(), "#COL\\d+$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (identifiant.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            identifiant = identifiant[4..];
            var classe = identifiant.LastIndexOf("#{", StringComparison.Ordinal);
            if (classe > 0)
            {
                identifiant = identifiant[..classe];
            }
            identifiant = identifiant.Replace('#', '\\');
        }
        return identifiant;
    }

    /// <summary>
    /// Le parent d'un chemin HID rendu par SDL, comme ES le calcule (Win32RawInputApi::getInputDeviceParent) : le
    /// peripherique USB d'une manette plutot que sa collection HID, plus « #COLnn » pour un boitier a plusieurs
    /// collections. Le chemin tel quel si Windows ne trouve pas le parent.
    /// </summary>
    public static string ParentSelonEs(string cheminSdl)
    {
        var chemin = cheminSdl;
        var vid = chemin.IndexOf("VID_", StringComparison.Ordinal);
        if (vid < 0)
        {
            vid = chemin.IndexOf("vid_", StringComparison.Ordinal);
        }
        if (vid >= 0)
        {
            var coupe = chemin.IndexOf("#{", vid, StringComparison.Ordinal);
            if (coupe >= 0)
            {
                chemin = chemin[..coupe];
            }
        }
        chemin = chemin.Replace("\\\\?\\", string.Empty, StringComparison.Ordinal).Replace('#', '\\');
        var collection = SuffixeDeCollection(cheminSdl);

        try
        {
            if (CM_Locate_DevNodeW(out var noeud, chemin, LocaliserNormal) == CrSucces &&
                CM_Get_Parent(out var parent, noeud, 0) == CrSucces)
            {
                var tampon = new char[256];
                if (CM_Get_Device_IDW(parent, tampon, tampon.Length, 0) == CrSucces)
                {
                    var longueur = Array.IndexOf(tampon, '\0');
                    return new string(tampon, 0, longueur < 0 ? tampon.Length : longueur) + collection;
                }
            }
        }
        catch (Exception)
        {
            // cfgmgr32 indisponible : le chemin tel quel, comme ES.
        }
        return cheminSdl;
    }

    /// <summary>« #COLnn » si le chemin designe une collection HID (« &amp;COLnn »), vide sinon (getHidCollectionSuffix).</summary>
    internal static string SuffixeDeCollection(string chemin)
    {
        var majuscules = chemin.ToUpperInvariant();
        var debut = majuscules.IndexOf("&COL", StringComparison.Ordinal);
        if (debut < 0)
        {
            return string.Empty;
        }
        debut += 4;
        var fin = debut;
        while (fin < majuscules.Length && char.IsAsciiDigit(majuscules[fin]))
        {
            fin++;
        }
        return fin == debut ? string.Empty : "#COL" + majuscules[debut..fin];
    }
}
