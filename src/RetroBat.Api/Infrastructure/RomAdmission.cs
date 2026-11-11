using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES ROMS ADMISES (11/11, rapport de Gogerad, regle validee par le user). Un jeu est un SYSTEME + un CONTENU ; ce contenu
/// doit en plus etre un dump OFFICIEL et PROPRE pour entrer dans une collection NelfePlay (World Scoring, Challenges &amp;
/// Speedruns) et pour qu'une partie soit soumise.
/// <para>
/// POURQUOI. Le site publie pour chaque jeu console les empreintes de TOUT le groupe du referentiel (`content_hashes`,
/// elargi par `widenFromReference`) : 77 md5 pour Super Mario Bros., dont 3 dumps officiels, le reste en hacks, pirates,
/// traductions, trainers et mauvais dumps. La borne de Gogerad retenait ainsi « Super Mario Bros. (Asia) (En) (Pirate) » et
/// un hack de couleurs de Streets of Rage 2. Le referentiel de la borne (resources/gamelist/systems/&lt;sys&gt;_lt.json) porte
/// les marques de chaque dump : on les lit ici.
/// </para>
/// <list type="number">
/// <item>Seuls les dumps officiels propres entrent : hack, pirate, traduction, trainer, mauvais dump, sans licence, beta,
/// prototype, demo, Virtual Console, reeditions et compilations sont exclus, meme quand le jeu marche.</item>
/// <item>Une version de REFERENCE par jeu : celle des etats de depart et des fantomes pour un speedrun (la seule acceptee),
/// sinon le premier dump propre que le .MEM nomme, USA ou World d'abord.</item>
/// <item>Les autres versions officielles n'entrent au World Scoring que PROUVEES (nommees par le .MEM, ou du meme nom No-Intro
/// qu'un dump nomme) ET dans le profil (`content_hashes`).</item>
/// <item>Une version PAL (50 Hz) prouvee entre, marquee PAL : elle concourra dans un classement PAL a part (a venir).</item>
/// <item>Plusieurs ROMs du meme jeu : la reference d'abord, puis une version admise, jamais une ROM exclue.</item>
/// </list>
/// L'arcade garde sa regle (le set nomme par le .MEM, jamais un bootleg ni un hack) : voir
/// <see cref="NelfePlayScoringCollectionSyncService.ContenuConfirme"/>.
/// </summary>
public static partial class RomAdmission
{
    public enum Statut
    {
        /// <summary>La version de reference du jeu.</summary>
        Reference,
        /// <summary>Une autre version officielle, prouvee et dans le profil.</summary>
        Admise,
        /// <summary>Une version PAL prouvee : admise, marquee PAL.</summary>
        Pal,
        /// <summary>Hors de toute collection, jamais soumise.</summary>
        Exclue,
    }

    /// <summary>Le verdict sur un dump, avec ce qu'il faut dire au joueur.</summary>
    public sealed record Decision(Statut Statut, string Raison, string? NomDuDump, string? RomAttendue)
    {
        public bool Admise => Statut != Statut.Exclue;

        /// <summary>Le texte a montrer au joueur quand le dump est exclu : ce qu'il a, et LA ROM qu'il faut.</summary>
        public string Message => Admise
            ? Raison
            : (NomDuDump is { Length: > 0 } n ? $"« {n} » : {Raison}" : Raison)
              + (RomAttendue is { Length: > 0 } a ? $". ROM attendue : « {a} »" : "");
    }

    /// <summary>Une entree du referentiel : ses noms (No-Intro, GoodTools…), ses marques, ses regions et ses md5.</summary>
    public sealed record Entree(IReadOnlyList<string> Noms, IReadOnlyList<string> Drapeaux, IReadOnlyList<string> Regions,
        IReadOnlyList<string> Md5);

    /// <summary>Ce que l'on sait d'un dump : son nom, ses marques d'exclusion, ses regions.</summary>
    public sealed record Fiche(string? Nom, IReadOnlyList<string> Marques, IReadOnlyList<string> Regions, bool Connue)
    {
        public bool Propre => Connue && Marques.Count == 0;
        public bool Pal => EstPal(Regions);
    }

    /// <summary>La version de reference d'un jeu : son nom No-Intro et les empreintes qui la designent.</summary>
    public sealed record Reference(string Nom, IReadOnlySet<string> Empreintes);

    /// <summary>Le referentiel d'identite, injectable pour les tests.</summary>
    public interface IReferentiel
    {
        IReadOnlyList<Entree> ParEmpreinte(string systeme, string empreinte);
        IReadOnlyList<Entree> ParNom(string systeme, string nom);
    }

    // ── Les marques ─────────────────────────────────────────────────────────────────────────

    /// <summary>Les marques du referentiel qui excluent toujours.</summary>
    private static readonly HashSet<string> MarquesFortes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hack", "pirate", "translation", "fan_translation", "trainer", "bad_dump", "overdump", "cheat", "cheat_device", "cracked",
        "bootleg", "graphics_hack", "custom_hack", "modified_dump", "level_select", "debug_menu", "restoration", "uncensored",
        "accessibility", "bugfix", "update",
        // les reeditions et les compilations
        "virtual_console", "digital_reissue", "nintendo_switch_online", "kiosk", "not_for_resale", "test_cartridge", "diagnostic",
        "input_test", "sound_test", "utility", "promotional",
    };

    /// <summary>
    /// Les marques que le referentiel pose parfois a tort (« Streets of Rage 2 (USA) », la cartouche officielle, y est marquee
    /// homebrew) : elles n'excluent que si un NOM du dump les confirme.
    /// </summary>
    private static readonly Dictionary<string, Regex> MarquesFaibles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["homebrew"] = new(@"\((homebrew|aftermarket)\)", RegexOptions.IgnoreCase),
        ["aftermarket"] = new(@"\(aftermarket\)", RegexOptions.IgnoreCase),
        ["unlicensed"] = new(@"\(unl\)", RegexOptions.IgnoreCase),
        ["demo"] = new(@"\((demo|sample|kiosk)", RegexOptions.IgnoreCase),
        ["beta"] = new(@"\(beta", RegexOptions.IgnoreCase),
        ["prototype"] = new(@"\((proto|prototype)", RegexOptions.IgnoreCase),
        ["alpha"] = new(@"\(alpha", RegexOptions.IgnoreCase),
    };

    /// <summary>Les etiquettes de NOM qui excluent (No-Intro, GoodTools, TOSEC), avec ce qu'on en dit au joueur.</summary>
    private static readonly (Regex Motif, string Libelle)[] EtiquettesExclues =
    [
        (new(@"\[h[^\]]*\]|\(hack\)|\bhack\b", RegexOptions.IgnoreCase), "hack"),
        (new(@"\[p[^\]]*\]|\(pirate\)", RegexOptions.IgnoreCase), "pirate"),
        (new(@"\[tr[^\]]*\]|\[t\+|\(trad|\(translat", RegexOptions.IgnoreCase), "traduction"),
        (new(@"\[t\d*\]|\[t[ \]]", RegexOptions.IgnoreCase), "trainer"),
        (new(@"\[b\d*[^\]]*\]", RegexOptions.IgnoreCase), "mauvais dump"),
        (new(@"\[o\d*[^\]]*\]", RegexOptions.IgnoreCase), "overdump"),
        (new(@"\(unl\)", RegexOptions.IgnoreCase), "sans licence"),
        (new(@"\(beta", RegexOptions.IgnoreCase), "beta"),
        (new(@"\(proto", RegexOptions.IgnoreCase), "prototype"),
        (new(@"\(demo|\(sample", RegexOptions.IgnoreCase), "démo"),
        (new(@"virtual console", RegexOptions.IgnoreCase), "Virtual Console"),
        (new(@"switch online", RegexOptions.IgnoreCase), "Switch Online"),
        (new(@"\(playchoice|\(mega-tech|\(mega play", RegexOptions.IgnoreCase), "version d'arcade d'un jeu de console"),
        (new(@"mega collection|3d classics|classic mini|classics collection|\(collection|collection\)|compilation|arcade archives|"
             + @"\(evercade|\(retro-bit|\(steam\)|\(gog\)|\(ique\)|\(np\)|mega drive mini|genesis mini|sega ages|\(limited run|"
             + @"\(wii\)|\(gamecube\)|\(e-reader|\(animal crossing|\(nes classics\)|\(classic nes series\)|\(famicom mini\)|"
             + @"25th anniversary|\(lodgenet|\(iam8bit|capcom town", RegexOptions.IgnoreCase), "réédition"),
        (new(@"msu-?1", RegexOptions.IgnoreCase), "MSU-1"),
        // Les traductions de fan nommees a la No-Intro : « (USA) (Ru) (Shedevr) », « (USA) (Es) (Wave) ».
        (new(@"\)\s*\((ru|es|pt|it|fr|de|zh|nl|sv|pl|el|hr|da|no|tr|ar|ko|pt-br)\)\s*\(", RegexOptions.IgnoreCase), "traduction"),
    ];

    /// <summary>Les regions qui designent un dump PAL (50 Hz) quand elles sont les seules.</summary>
    private static readonly HashSet<string> RegionsPal = new(StringComparer.OrdinalIgnoreCase)
    {
        "europe", "australia", "germany", "france", "spain", "italy", "uk", "united kingdom", "netherlands", "sweden",
        "scandinavia", "nordic", "denmark", "finland", "norway", "portugal", "greece", "russia", "poland", "new zealand",
    };

    /// <summary>Les etiquettes d'exclusion d'un nom de dump (pas d'un nom de fichier local : celui-ci ne fait pas l'identite).</summary>
    public static IReadOnlyList<string> EtiquettesDuNom(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom)) return [];
        return EtiquettesExclues.Where(e => e.Motif.IsMatch(nom)).Select(e => e.Libelle).Distinct().ToList();
    }

    /// <summary>Les marques d'exclusion d'une entree du referentiel ; vide pour un dump officiel propre.</summary>
    public static IReadOnlyList<string> MarquesDe(Entree e)
    {
        var texte = string.Join(" ", e.Noms);
        var marques = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in e.Drapeaux)
        {
            if (MarquesFortes.Contains(d)) marques.Add(Libelle(d));
            else if (MarquesFaibles.TryGetValue(d, out var confirme) && confirme.IsMatch(texte)) marques.Add(Libelle(d));
        }
        foreach (var nom in e.Noms)
        {
            foreach (var l in EtiquettesDuNom(nom)) marques.Add(l);
        }
        return marques.ToList();
    }

    private static string Libelle(string drapeau) => drapeau.ToLowerInvariant() switch
    {
        "translation" or "fan_translation" => "traduction",
        "bad_dump" => "mauvais dump",
        "virtual_console" => "Virtual Console",
        "digital_reissue" or "nintendo_switch_online" => "réédition",
        "unlicensed" => "sans licence",
        "prototype" => "prototype",
        "homebrew" or "aftermarket" => "homebrew",
        _ => drapeau.ToLowerInvariant().Replace('_', ' '),
    };

    public static bool EstPal(IReadOnlyList<string> regions)
        => regions.Count > 0 && regions.All(r => RegionsPal.Contains(r.Trim()));

    // ── La fiche d'un dump ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ce que le referentiel dit d'un dump, d'apres TOUTES ses empreintes (md5 mesure, md5 sans en-tete, md5 de la gamelist).
    /// Une meme empreinte peut figurer dans plusieurs DAT : elle est propre si UNE de ses entrees l'est. Inconnue du
    /// referentiel, elle peut encore etre nommee par le .MEM (son libelle fait alors la fiche) ; sinon elle n'est pas un dump
    /// No-Intro et n'entre pas.
    /// </summary>
    public static Fiche FicheDe(string systeme, IEnumerable<string?> empreintes, IReadOnlyDictionary<string, string>? dumpsDuMem,
        IReferentiel referentiel)
    {
        var hashes = empreintes.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.Trim().ToLowerInvariant()).Distinct().ToList();
        var entrees = hashes.SelectMany(h => referentiel.ParEmpreinte(systeme, h)).ToList();
        var libelle = hashes.Select(h => dumpsDuMem is not null && dumpsDuMem.TryGetValue(h, out var l) ? l : null)
            .FirstOrDefault(l => l is not null);
        if (entrees.Count == 0)
        {
            if (libelle is null) return new Fiche(null, ["inconnue du référentiel No-Intro"], [], false);
            var nom = Path.GetFileNameWithoutExtension(libelle);
            return new Fiche(nom, EtiquettesDuNom(libelle), RegionsDuNom(nom), true);
        }

        var propres = entrees.Where(e => MarquesDe(e).Count == 0).ToList();
        var choisie = propres.Count > 0 ? propres[0] : entrees[0];
        var marques = propres.Count > 0 ? new List<string>() : MarquesDe(entrees[0]).ToList();
        // Le libelle du .MEM peut dire ce que le referentiel tait (« (Ru) (Shedevr) »).
        if (libelle is not null) marques = marques.Union(EtiquettesDuNom(libelle)).ToList();
        return new Fiche(NomNoIntro(propres.Count > 0 ? propres : entrees), marques, choisie.Regions, true);
    }

    /// <summary>Le nom a la No-Intro (« Super Mario Bros. (World) ») plutot qu'un nom GoodTools (« (Nintendo) (JP-US) »).</summary>
    private static string? NomNoIntro(IReadOnlyList<Entree> entrees)
    {
        var noms = entrees.SelectMany(e => e.Noms).Where(n => n.Length > 0).ToList();
        return noms.FirstOrDefault(n => Regex.IsMatch(n, @"\((USA|World|Europe|Japan|Asia|Korea|Brazil|Australia|France|Germany|Spain|Italy)[,)]")
                                        && !Regex.IsMatch(n, @"\(nintendo\)|\(sega\)|\(konami\)|\(capcom\)|\[|\(-\)", RegexOptions.IgnoreCase))
               ?? noms.FirstOrDefault(n => !Regex.IsMatch(n, @"\(nintendo\)|\(sega\)|\(konami\)|\(capcom\)|\[|\(-\)", RegexOptions.IgnoreCase))
               ?? noms.FirstOrDefault();
    }

    private static IReadOnlyList<string> RegionsDuNom(string nom)
    {
        var m = Regex.Match(nom, @"\(([^()]*)\)");
        return m.Success ? m.Groups[1].Value.Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).ToList() : [];
    }

    // ── La reference ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// La version de reference du World Scoring : le premier dump PROPRE que le .MEM nomme et que le profil publie, USA ou
    /// World d'abord, jamais PAL tant qu'une autre existe. Ses empreintes : celles de toutes les entrees du meme nom No-Intro
    /// (le meme jeu sous un autre en-tete). Null si le .MEM ne nomme aucun dump propre publie.
    /// </summary>
    public static Reference? ReferenceDuScore(string systeme, IReadOnlyDictionary<string, string> dumpsDuMem, ContentHashes? profil,
        IReferentiel referentiel)
    {
        var publies = Publiees(profil);
        var candidats = dumpsDuMem.Keys
            .Where(publies.Contains)
            .Select(h => (Hash: h, Fiche: FicheDe(systeme, [h], dumpsDuMem, referentiel)))
            .Where(c => c.Fiche.Propre && c.Fiche.Nom is { Length: > 0 })
            .OrderBy(c => c.Fiche.Pal ? 1 : 0)
            .ThenBy(c => c.Fiche.Regions.Any(r => r.Equals("USA", StringComparison.OrdinalIgnoreCase) || r.Equals("World", StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ToList();
        if (candidats.Count == 0) return null;
        var choisi = candidats[0];
        var empreintes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { choisi.Hash };
        foreach (var e in referentiel.ParNom(systeme, choisi.Fiche.Nom!)) empreintes.UnionWith(e.Md5);
        return new Reference(choisi.Fiche.Nom!, empreintes);
    }

    /// <summary>
    /// La version de reference d'un SPEEDRUN : la ROM des etats de depart et des fantomes, la seule acceptee. Son nom vient du
    /// coeur hote des fantomes (<see cref="RetroBat.Api.Speedrun.FantomesDuSpeedrun.Liste"/>), ses empreintes du referentiel
    /// (toutes les entrees de ce nom), du md5 sans en-tete du coeur hote et du md5 de la ROM ecrit dans les etats de depart.
    /// Null pour un jeu sans coeur hote ni etat qui la dise.
    /// </summary>
    public static Reference? ReferenceDuSpeedrun(string systeme, string groupe, IReferentiel referentiel, string? racineEtats = null)
    {
        var hote = RetroBat.Api.Speedrun.FantomesDuSpeedrun.Liste.FirstOrDefault(h =>
            string.Equals(h.Systeme, systeme, StringComparison.OrdinalIgnoreCase) && string.Equals(h.Groupe, groupe, StringComparison.OrdinalIgnoreCase));
        var empreintes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nom = hote?.Dump;
        if (hote?.SansEnTete is { Length: > 0 } se) empreintes.Add(se);
        foreach (var (rom, md5) in RomsDesEtats(systeme, groupe, racineEtats))
        {
            empreintes.Add(md5);
            nom ??= Path.GetFileNameWithoutExtension(rom);
        }
        if (nom is null) return null;
        foreach (var e in referentiel.ParNom(systeme, nom)) empreintes.UnionWith(e.Md5);
        return empreintes.Count == 0 ? null : new Reference(nom, empreintes);
    }

    /// <summary>La ROM que les etats de depart disent (champs « rom » et « rom_md5 » de leurs .json).</summary>
    private static IEnumerable<(string Rom, string Md5)> RomsDesEtats(string systeme, string groupe, string? racine)
    {
        var dossier = Path.Combine(racine ?? Path.Combine(RetroBatPaths.PluginRoot, "resources", "challenges", "states"), systeme, groupe);
        if (!Directory.Exists(dossier)) yield break;
        string[] fichiers;
        try { fichiers = Directory.GetFiles(dossier, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in fichiers)
        {
            string? rom = null, md5 = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("rom", out var r)) rom = r.GetString();
                if (doc.RootElement.TryGetProperty("rom_md5", out var m)) md5 = m.GetString();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
            if (rom is { Length: > 0 } && md5 is { Length: 32 }) yield return (rom, md5.ToLowerInvariant());
        }
    }

    // ── Le verdict ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Le verdict sur un dump de CONSOLE. <paramref name="empreintes"/> : tout ce qui a ete mesure ou lu du fichier (md5,
    /// md5 sans en-tete, md5 de la gamelist). <paramref name="speedrun"/> non null : le jeu n'est ouvert qu'aux speedruns
    /// (ou on juge pour une epreuve), seule sa reference passe. Pur.
    /// </summary>
    public static Decision JugerConsole(string systeme, IEnumerable<string?> empreintes, IReadOnlyDictionary<string, string> dumpsDuMem,
        ContentHashes? profil, Reference? reference, Reference? speedrun, IReferentiel referentiel)
    {
        var hashes = empreintes.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.Trim().ToLowerInvariant()).Distinct().ToList();
        var fiche = FicheDe(systeme, hashes, dumpsDuMem, referentiel);
        var attendue = speedrun?.Nom ?? reference?.Nom;
        if (!fiche.Propre)
            return new Decision(Statut.Exclue, "ROM exclue (" + string.Join(", ", fiche.Marques) + ")", fiche.Nom, attendue);

        if (speedrun is not null)
        {
            return hashes.Any(speedrun.Empreintes.Contains)
                ? new Decision(Statut.Reference, "version de référence du speedrun", fiche.Nom, attendue)
                : new Decision(Statut.Exclue, "pas la ROM des états de départ du speedrun", fiche.Nom, attendue);
        }

        if (!hashes.Any(Publiees(profil).Contains))
            return new Decision(Statut.Exclue, "version absente du profil de classement", fiche.Nom, attendue);
        if (reference is not null && hashes.Any(reference.Empreintes.Contains))
            return new Decision(Statut.Reference, "version de référence", fiche.Nom, attendue);

        // Prouvee : nommee par le .MEM, ou du meme nom No-Intro qu'un dump propre que le .MEM nomme (autre en-tete).
        var prouvee = hashes.Any(dumpsDuMem.ContainsKey)
            || (fiche.Nom is { Length: > 0 } nom && dumpsDuMem.Keys.Any(h =>
                FicheDe(systeme, [h], dumpsDuMem, referentiel) is { Propre: true } f && string.Equals(f.Nom, nom, StringComparison.OrdinalIgnoreCase)));
        if (!prouvee)
            return new Decision(Statut.Exclue, "version officielle non prouvée par la définition de score du jeu", fiche.Nom, attendue);
        return fiche.Pal
            ? new Decision(Statut.Pal, "version PAL (50 Hz) : classement PAL", fiche.Nom, attendue)
            : new Decision(Statut.Admise, "version officielle prouvée", fiche.Nom, attendue);
    }

    /// <summary>Le rang d'un verdict pour choisir LA ROM d'un jeu : la reference, puis une version admise, puis PAL.</summary>
    public static int Rang(Decision d) => d.Statut switch
    {
        Statut.Reference => 0,
        Statut.Admise => 1,
        Statut.Pal => 2,
        _ => 9,
    };

    /// <summary>Toutes les empreintes que le profil publie (md5, sha256, sans en-tete).</summary>
    public static HashSet<string> Publiees(ContentHashes? profil)
        => new((profil?.Md5 ?? []).Concat(profil?.Sha256 ?? []).Concat(profil?.Rhash ?? []).Where(h => h.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    // ── Le .MEM ────────────────────────────────────────────────────────────────────────────

    private static readonly Regex HashDuMem = new(@"hash\s*=\s*""([0-9a-fA-F]{32})""\s*,\s*label\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>Les dumps que le .MEM nomme (md5 → libelle), vide sans .MEM.</summary>
    public static IReadOnlyDictionary<string, string> DumpsDuMem(string? cheminMem)
    {
        var sortie = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cheminMem)) return sortie;
        try
        {
            if (!File.Exists(cheminMem)) return sortie;
            foreach (Match m in HashDuMem.Matches(File.ReadAllText(cheminMem)))
                sortie.TryAdd(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sortie;
    }

    // ── Le referentiel sur disque ──────────────────────────────────────────────────────────

    /// <summary>
    /// Le referentiel de la borne (resources/gamelist/systems/&lt;sys&gt;_lt.json, une entree JSON par ligne). Lu A LA DEMANDE :
    /// une recherche ne parse que les lignes qui contiennent l'empreinte ou le nom cherche, et son resultat reste en cache.
    /// </summary>
    public sealed class ReferentielSurDisque : IReferentiel
    {
        public static ReferentielSurDisque Borne { get; } = new(Path.Combine(AppContext.BaseDirectory, "resources", "gamelist", "systems"));

        private readonly string _racine;
        private readonly ConcurrentDictionary<string, IReadOnlyList<Entree>> _cache = new(StringComparer.OrdinalIgnoreCase);

        public ReferentielSurDisque(string racine) => _racine = racine;

        public IReadOnlyList<Entree> ParEmpreinte(string systeme, string empreinte)
            => _cache.GetOrAdd("h|" + systeme + "|" + empreinte, _ => Chercher(systeme, empreinte.ToLowerInvariant(),
                e => e.Md5.Contains(empreinte, StringComparer.OrdinalIgnoreCase) || e.Autres.Contains(empreinte, StringComparer.OrdinalIgnoreCase)));

        public IReadOnlyList<Entree> ParNom(string systeme, string nom)
            => _cache.GetOrAdd("n|" + systeme + "|" + nom, _ => Chercher(systeme, nom,
                e => e.Entree.Noms.Contains(nom, StringComparer.OrdinalIgnoreCase)));

        private IReadOnlyList<Entree> Chercher(string systeme, string aiguille, Func<(Entree Entree, IReadOnlyList<string> Md5, IReadOnlyList<string> Autres), bool> garde)
        {
            var chemin = Path.Combine(_racine, systeme + "_lt.json");
            var trouvees = new List<Entree>();
            if (!File.Exists(chemin) || aiguille.Length == 0) return trouvees;
            try
            {
                foreach (var ligne in File.ReadLines(chemin))
                {
                    if (ligne.Length == 0 || ligne.IndexOf(aiguille, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (Lire(ligne) is { } lu && garde(lu)) trouvees.Add(lu.Entree);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return trouvees;
        }

        internal static (Entree Entree, IReadOnlyList<string> Md5, IReadOnlyList<string> Autres)? Lire(string ligne)
        {
            try
            {
                using var doc = JsonDocument.Parse(ligne);
                var r = doc.RootElement;
                var noms = new List<string>();
                if (Texte(r, "fn") is { } fn) noms.Add(fn);
                if (r.TryGetProperty("aka", out var aka) && aka.ValueKind == JsonValueKind.Array)
                    foreach (var a in aka.EnumerateArray())
                        if (Texte(a, "fn") is { } n && !noms.Contains(n)) noms.Add(n);
                var drapeaux = Liste(r, "flg");
                foreach (var cle in new[] { "rk", "t" })
                    if (Texte(r, cle) is { } v && !v.Equals("game", StringComparison.OrdinalIgnoreCase) && !v.Equals("video", StringComparison.OrdinalIgnoreCase))
                        drapeaux.Add(v);
                var md5 = new List<string>();
                var autres = new List<string>();
                if (r.TryGetProperty("hsh", out var hsh) && hsh.ValueKind == JsonValueKind.Array)
                    foreach (var h in hsh.EnumerateArray())
                    {
                        if (Texte(h, "md5") is { } m) md5.Add(m.ToLowerInvariant());
                        if (Texte(h, "sha1") is { } s) autres.Add(s.ToLowerInvariant());
                    }
                return (new Entree(noms, drapeaux, Liste(r, "reg"), md5), md5, autres);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Texte(JsonElement e, string cle)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(cle, out var v) && v.ValueKind == JsonValueKind.String
               && v.GetString() is { Length: > 0 } s ? s : null;

        private static List<string> Liste(JsonElement e, string cle)
        {
            var sortie = new List<string>();
            if (e.TryGetProperty(cle, out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) sortie.Add(s);
            return sortie;
        }
    }
}
