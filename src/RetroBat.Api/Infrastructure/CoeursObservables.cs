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
    /// de confort du joueur : FBNeo s'il est declare (le replay et la mesure y sont les plus surs),
    /// sinon MAME recent, sinon, hors arcade, le premier coeur libretro. Null : rien a imposer.
    /// </summary>
    public static (string Emulator, string Core)? MeilleurLancement(IReadOnlyList<EmulationStationSystemEmulatorCore> coeursDuSysteme)
    {
        static bool Libretro(EmulationStationSystemEmulatorCore entree) =>
            entree.Emulator.Equals("libretro", StringComparison.OrdinalIgnoreCase);

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

    private static bool EstCoeurMame(string coeur) => coeur.StartsWith("mame", StringComparison.OrdinalIgnoreCase);

    private static bool EstCoeurArcade(string coeur) =>
        EstCoeurMame(coeur) ||
        coeur.StartsWith("fbneo", StringComparison.OrdinalIgnoreCase) ||
        coeur.StartsWith("fbalpha", StringComparison.OrdinalIgnoreCase) ||
        coeur.Equals("geolith", StringComparison.OrdinalIgnoreCase);
}
