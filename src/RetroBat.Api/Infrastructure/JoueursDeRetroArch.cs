using System.Globalization;
using System.Text.RegularExpressions;
using RetroBat.Api.Netplay;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// QUI EST LE JOUEUR 1 DANS RETROARCH, pour le lancement en cours (13/11, borne du user).
///
/// ES (attribution automatique) passe au lanceur ses manettes (`-pNindex/-pNguid/-pNname`), et le lanceur
/// en fait la configuration de RetroArch : `input_joypad_driver` et `input_playerN_joypad_index`. C'est
/// CETTE derniere qui decide, pas l'ordre d'ES : le 13/11, avec un panel « Generic USB Joystick » en p1
/// (index SDL 1) et la manette Xbox en p2 (index SDL 0), le lanceur a ecrit `input_player1_joypad_index = 0`
/// pour le panel (doublon avec la Xbox, « Value 0 is duplicated for keys: 1, 2 », corrige en gardant le
/// premier joueur) : dans RetroArch, le port 1 etait la Xbox, avec les numeros de boutons du panel. L'API,
/// elle, suivait le panel en joueur 1 (<see cref="PlayerNumbering"/>) : Player Deck sur le panel, START de la
/// Xbox lu « joueur 2 », et une partie seule classee « a plusieurs des le depart ».
///
/// Pour le pilote sdl2 (celui du lanceur par defaut), l'index de RetroArch est l'index SDL que le lanceur a
/// recu d'ES (`-pNindex`, la meme enumeration SDL que RetroArch : le 13/11, Xbox 0 et panel 1 chez ES comme
/// chez RetroArch, « Xbox One S Controller port 1 », « G-Shark GS-GP702 port 2 »). Le joueur N de RetroArch
/// est donc la manette du lancement dont l'index d'ES vaut `input_playerN_joypad_index`. Pour xinput et
/// dinput, les enumerations different : on ne devine pas, la regle de <see cref="PlayerNumbering"/> reste.
/// </summary>
public static class JoueursDeRetroArch
{
    /// <summary>Une manette passee au lanceur : son joueur chez ES, son index SDL chez ES, son GUID et son nom.</summary>
    public sealed record Manette(int JoueurEs, int IndexEs, string Guid, string Nom);

    /// <summary>Le joueur de RetroArch -> la manette qu'il a recue, et le pilote d'entree.</summary>
    public sealed record Attribution(string Pilote, IReadOnlyDictionary<int, Manette> ParJoueur)
    {
        /// <summary>« joueur de RetroArch » -> GUID, pour <see cref="PlayerNumbering.Assign(IReadOnlyList{PlayerNumbering.Device}, IReadOnlyDictionary{int, string}, IReadOnlyDictionary{int, string}?)"/>.</summary>
        public IReadOnlyDictionary<int, string> Guids => ParJoueur.ToDictionary(p => p.Key, p => p.Value.Guid);

        /// <summary>« 1 = Xbox One S Controller (index 0, p2 chez ES), 2 = Generic USB Joystick (index 1, p1 chez ES) ».</summary>
        public string Decrire()
            => string.Join(", ", ParJoueur.OrderBy(p => p.Key)
                .Select(p => $"{p.Key} = {p.Value.Nom} (index {p.Value.IndexEs}, p{p.Value.JoueurEs} chez ES)"));

        /// <summary>Vrai si RetroArch n'a pas donne les joueurs dans l'ordre d'ES.</summary>
        public bool DiffereDEs => ParJoueur.Any(p => p.Key != p.Value.JoueurEs);
    }

    /// <summary>Les manettes d'une ligne de commande du lanceur (`-pNindex`, `-pNguid`, `-pNname`), par joueur d'ES.</summary>
    public static IReadOnlyList<Manette> ManettesDuLancement(string commande)
    {
        var parJoueur = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (Match m in ArgumentDeManette.Matches(commande ?? string.Empty))
        {
            var joueur = int.Parse(m.Groups["joueur"].Value, CultureInfo.InvariantCulture);
            if (!parJoueur.TryGetValue(joueur, out var cles))
            {
                parJoueur[joueur] = cles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            cles[m.Groups["cle"].Value] = m.Groups["valeur"].Value.Trim('"');
        }

        var manettes = new List<Manette>();
        foreach (var (joueur, cles) in parJoueur)
        {
            if (!cles.TryGetValue("index", out var texte) || !int.TryParse(texte, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) continue;
            manettes.Add(new Manette(joueur, index, cles.GetValueOrDefault("guid", string.Empty), cles.GetValueOrDefault("name", string.Empty)));
        }
        return manettes;
    }

    /// <summary>Le pilote d'entree et les `input_playerN_joypad_index` d'un retroarch.cfg.</summary>
    public static (string? Pilote, IReadOnlyDictionary<int, int> Index) LireConfig(IEnumerable<string> lignes)
    {
        string? pilote = null;
        var index = new Dictionary<int, int>();
        foreach (var brute in lignes)
        {
            var m = LigneDeConfig.Match(brute);
            if (!m.Success) continue;
            var cle = m.Groups["cle"].Value;
            var valeur = m.Groups["valeur"].Value;
            if (string.Equals(cle, "input_joypad_driver", StringComparison.Ordinal))
            {
                pilote = valeur;
                continue;
            }
            var j = IndexDeJoueur.Match(cle);
            if (j.Success && int.TryParse(valeur, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                index[int.Parse(j.Groups[1].Value, CultureInfo.InvariantCulture)] = n;
            }
        }
        return (pilote, index);
    }

    /// <summary>
    /// Le joueur de RetroArch de chaque manette du lancement, ou null quand on ne peut pas le savoir (pilote autre que
    /// sdl2, aucune manette, ou aucun index de la configuration ne designe une manette du lancement).
    /// </summary>
    public static Attribution? Resoudre(IReadOnlyList<Manette> manettes, string? pilote, IReadOnlyDictionary<int, int> index)
    {
        if (manettes.Count == 0 || !string.Equals(pilote, "sdl2", StringComparison.OrdinalIgnoreCase)) return null;

        // Seuls les joueurs que le lanceur a configures (un par manette recue) ont un index a jour ; les autres
        // gardent celui de la configuration d'avant.
        var parJoueur = new SortedDictionary<int, Manette>();
        var prises = new HashSet<Manette>();
        foreach (var joueur in manettes.Select(m => m.JoueurEs).Distinct().OrderBy(j => j))
        {
            if (!index.TryGetValue(joueur, out var k) || k < 0) continue;
            var recue = manettes.FirstOrDefault(m => m.IndexEs == k && !prises.Contains(m));
            if (recue is null) continue;
            parJoueur[joueur] = recue;
            prises.Add(recue);
        }
        return parJoueur.Count == 0 ? null : new Attribution(pilote!, parJoueur);
    }

    /// <summary>L'heure (locale) en tete d'une ligne du journal du lanceur, « 2026-11-13 21:34:12.657 [INFO] ... ».</summary>
    public static DateTime? HeureDeLaLigne(string ligne)
        => ligne is { Length: >= 23 }
           && DateTime.TryParseExact(ligne[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var heure)
            ? heure
            : null;

    /// <summary>
    /// Le joueur de RetroArch de chaque manette pour le lancement en cours : la derniere ligne du journal du lanceur, si elle
    /// est posterieure a <paramref name="depuis"/> (heure locale, un peu avant le depart annonce), et retroarch.cfg, s'il a
    /// ete ecrit apres elle. Null tant que ce n'est pas le cas, ou si on ne peut pas savoir ; <paramref name="raison"/> le dit.
    /// </summary>
    public static Attribution? LireDuLancement(DateTime depuis, out string raison)
    {
        var journal = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", "emulatorLauncher.log");
        var ligne = EsLaunchArguments.LancementsDepuisLaFin(journal).FirstOrDefault();
        if (ligne is null)
        {
            raison = "aucun lancement dans le journal du lanceur";
            return null;
        }
        var heure = HeureDeLaLigne(ligne);
        if (heure is not { } h || h < depuis)
        {
            raison = "le lanceur n'a pas encore ecrit ce lancement";
            return null;
        }
        var config = RetroBatPaths.RetroArchConfigPath;
        try
        {
            if (!File.Exists(config) || File.GetLastWriteTime(config) < h)
            {
                raison = "retroarch.cfg pas encore ecrit pour ce lancement";
                return null;
            }
            var manettes = ManettesDuLancement(ligne);
            var (pilote, index) = LireConfig(LireLignesPartagees(config));
            var attribution = Resoudre(manettes, pilote, index);
            raison = attribution is null
                ? manettes.Count == 0 ? "aucune manette passee au lanceur" : $"pilote {pilote ?? "?"} : ordre de RetroArch inconnu"
                : "ok";
            return attribution;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            raison = "retroarch.cfg illisible (" + ex.Message + ")";
            return null;
        }
    }

    /// <summary>Une raison qui ne changera pas en attendant : la configuration est ecrite, on ne peut juste pas conclure.</summary>
    public static bool RaisonDefinitive(string raison)
        => raison.StartsWith("pilote", StringComparison.Ordinal) || raison.StartsWith("aucune manette", StringComparison.Ordinal);

    private static IEnumerable<string> LireLignesPartagees(string chemin)
    {
        // Le lanceur peut encore l'ecrire : lecture partagee, comme le journal.
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lecteur = new StreamReader(flux);
        var lignes = new List<string>();
        string? l;
        while ((l = lecteur.ReadLine()) is not null) lignes.Add(l);
        return lignes;
    }

    private static readonly Regex ArgumentDeManette = new(
        "-p(?<joueur>\\d+)(?<cle>[a-zA-Z]+)\\s+(?<valeur>\"[^\"]*\"|[^\\s\"]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LigneDeConfig = new(
        "^\\s*(?<cle>[A-Za-z0-9_]+)\\s*=\\s*\"(?<valeur>[^\"]*)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IndexDeJoueur = new(
        "^input_player(\\d+)_joypad_index$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
