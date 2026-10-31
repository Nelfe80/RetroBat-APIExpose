namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES COEURS QUI MESURENT, NOMMES A L'AVANCE (liste blanche, demande user 2026-09-26).
///
/// La collection World Scoring proposait 19xx sous le systeme mame laisse en automatique. RetroBat
/// y choisit lui-meme un coeur MAME jeu par jeu (FindBestMameCore) et a pris MAME 2003-Plus, qui
/// n'expose pas sa memoire : aucun score possible, alors que la collection promettait le contraire.
/// Apprendre les coeurs aveugles apres coup (CoreMemoryCapability) prevenait trop tard. On ne
/// propose donc plus que ce qui a ete verifie :
/// - FBNeo sous RetroArch : le wrapper lit sa memoire ;
/// - MAME recent sous RetroArch et MAME autonome : le plugin Lua mesure.
/// Les autres coeurs d'arcade (anciens MAME, FBAlpha, FBNeo autonome) n'entrent pas.
/// Hors arcade, on ne juge pas ici : c'est le wrapper qui mesure les coeurs libretro de console.
/// </summary>
public static class CoeursObservables
{
    private static readonly HashSet<string> CoeursArcadeMesures = new(StringComparer.OrdinalIgnoreCase) { "fbneo", "mame" };
    // groovymame est MAME autonome, aligne sur lui (precision user 2026-09-27) : meme plugin Lua.
    private static readonly HashSet<string> EmulateursAutonomesMesures = new(StringComparer.OrdinalIgnoreCase) { "mame64", "groovymame" };

    public sealed record Verdict(bool Observable, string? Raison);

    /// <param name="lancement">L'emulateur et le coeur qui lanceront le jeu.</param>
    /// <param name="coeurChoisi">Un coeur est designe (pour le jeu ou son systeme) ; sinon RetroBat choisit.</param>
    /// <param name="coeursDuSysteme">Les coeurs que le systeme declare dans es_systems.cfg.</param>
    public static Verdict Juger(
        EmulationStationLaunchConfig lancement,
        bool coeurChoisi,
        IReadOnlyList<EmulationStationSystemEmulatorCore> coeursDuSysteme)
    {
        var emulateur = (lancement.Emulator ?? string.Empty).Trim();
        var coeur = (lancement.Core ?? string.Empty).Trim();
        if (emulateur.Length == 0)
        {
            // Configuration inconnue (es_systems illisible) : on ne retire rien sur une supposition.
            return new Verdict(true, null);
        }

        if (!emulateur.Equals("libretro", StringComparison.OrdinalIgnoreCase))
        {
            return EmulateursAutonomesMesures.Contains(emulateur)
                ? new Verdict(true, null)
                : new Verdict(false, $"l'émulateur « {emulateur} » ne se mesure pas");
        }

        if (!coeurChoisi && EstCoeurMame(coeur) &&
            coeursDuSysteme.Any(entree =>
                entree.Emulator.Equals("libretro", StringComparison.OrdinalIgnoreCase) &&
                EstCoeurMame(entree.Core) &&
                !CoeursArcadeMesures.Contains(entree.Core)))
        {
            return new Verdict(false,
                "en automatique, RetroBat choisit lui-même un cœur MAME par jeu, parfois un ancien qui ne mesure rien");
        }

        if (EstCoeurArcade(coeur) && !CoeursArcadeMesures.Contains(coeur))
        {
            return new Verdict(false, $"le cœur « {coeur} » ne se mesure pas");
        }

        return new Verdict(true, null);
    }

    /// <summary>
    /// NOTRE COEUR FONCTIONNEL pour un systeme, celui que World Scoring impose quel que soit le choix
    /// de confort du joueur. Un systeme de console : son propre coeur (<see cref="CoeurDeConsole"/>).
    /// Un systeme d'arcade : FBNeo s'il est declare (le replay et la mesure y sont les plus surs),
    /// sinon MAME recent, sinon le premier coeur libretro hors arcade. Null : rien a imposer.
    /// </summary>
    public static (string Emulator, string Core)? MeilleurLancement(IReadOnlyList<EmulationStationSystemEmulatorCore> coeursDuSysteme)
    {
        static bool Libretro(EmulationStationSystemEmulatorCore entree) =>
            entree.Emulator.Equals("libretro", StringComparison.OrdinalIgnoreCase);

        if (CoeurDeConsole(coeursDuSysteme) is { } propre)
        {
            return ("libretro", propre);
        }

        foreach (var voulu in new[] { "fbneo", "mame" })
        {
            if (coeursDuSysteme.Any(entree => Libretro(entree) && entree.Core.Equals(voulu, StringComparison.OrdinalIgnoreCase)))
            {
                return ("libretro", voulu);
            }
        }

        var autre = coeursDuSysteme.FirstOrDefault(entree =>
            Libretro(entree) && entree.Core.Length > 0 && !EstCoeurArcade(entree.Core));
        return autre is null ? null : ("libretro", autre.Core);
    }

    /// <summary>
    /// LE COEUR PROPRE D'UN SYSTEME DE CONSOLE (31/10/2026) : son premier coeur libretro, quand ce n'est pas un coeur
    /// d'arcade. RetroBat declare FBNeo ou MAME en secours sur des dizaines de systemes (Mega Drive, Master System, Game
    /// Gear, PC Engine, MSX...), ou ils ne lisent que leurs propres sets : Sonic sous FBNeo s'arrete sur « Romset is
    /// unknown ». Leur capacite ne vaut donc que sur un systeme d'arcade. Null : un systeme d'arcade (ou sans libretro).
    /// </summary>
    public static string? CoeurDeConsole(IReadOnlyList<EmulationStationSystemEmulatorCore> coeursDuSysteme)
    {
        var premier = coeursDuSysteme.FirstOrDefault(entree =>
            entree.Emulator.Equals("libretro", StringComparison.OrdinalIgnoreCase) && entree.Core.Length > 0);
        return premier is null || EstCoeurArcade(premier.Core) ? null : premier.Core;
    }

    /// <summary>
    /// CE QUE PERMET UN LANCEMENT D'ARCADE (decision du user du 25 octobre 2026 : « le coeur supportant le plus de choses
    /// en priorite »). FBNeo sous RetroArch : mesure, replay et film (3) ; MAME sous RetroArch : mesure et film, pas de
    /// replay, RetroArch 1.22.2 plantant en ecrivant l'etat de MAME (2) ; MAME autonome : la mesure seule (1). Tout autre
    /// lancement : 0, il ne se compare pas. Pur.
    /// </summary>
    public static int Capacite(string? emulateur, string? coeur)
    {
        var e = (emulateur ?? string.Empty).Trim();
        var c = (coeur ?? string.Empty).Trim();
        if (e.Equals("libretro", StringComparison.OrdinalIgnoreCase))
        {
            return c.Equals("fbneo", StringComparison.OrdinalIgnoreCase) ? 3
                : c.Equals("mame", StringComparison.OrdinalIgnoreCase) ? 2
                : 0;
        }
        return EmulateursAutonomesMesures.Contains(e) ? 1 : 0;
    }

    /// <summary>
    /// Un coeur libretro d'arcade, designe par son fichier, mesure-t-il ? Null hors arcade : ce
    /// n'est pas la liste blanche qui en juge.
    /// </summary>
    public static bool? CoeurArcadeMesure(string coeur)
        => EstCoeurArcade(coeur) ? CoeursArcadeMesures.Contains(coeur) : null;

    private static bool EstCoeurMame(string coeur) => coeur.StartsWith("mame", StringComparison.OrdinalIgnoreCase);

    public static bool EstCoeurArcade(string coeur) =>
        EstCoeurMame(coeur) ||
        coeur.StartsWith("fbneo", StringComparison.OrdinalIgnoreCase) ||
        coeur.StartsWith("fbalpha", StringComparison.OrdinalIgnoreCase) ||
        coeur.Equals("geolith", StringComparison.OrdinalIgnoreCase);
}
