using System;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Au tout premier lancement d'un coeur, l'annonce disait « certifiable » puis, quatre secondes
/// plus tard, « aucun score » (19xx sous MAME 2003-Plus, 2026-09-26). Pour l'arcade, la liste
/// blanche tranche d'emblee.
/// </summary>
public class AnnonceCoeurTests
{
    private static string[] Liste(string fichiers)
        => fichiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Theory]
    [InlineData("mame2003_plus", false)]
    [InlineData("fbalpha2012", false)]
    [InlineData("mame", true)]
    [InlineData("fbneo", true)]
    public void Un_coeur_d_arcade_est_juge_d_emblee(string fichiers, bool attendu)
        => Assert.Equal(attendu, NelfePlayScoringReporter.VerdictArcade(Liste(fichiers)));

    [Theory]
    [InlineData("genesis_plus_gx")]          // hors arcade : c'est l'experience qui juge
    [InlineData("")]                         // MAME autonome : aucune fiche RetroArch
    [InlineData("mame2003_plus,mame")]       // un nom, deux verdicts : on ne tranche pas
    [InlineData("fbneo,genesis_plus_gx")]    // melange : on ne tranche pas
    public void Hors_de_ces_cas_la_liste_blanche_ne_tranche_pas(string fichiers)
        => Assert.Null(NelfePlayScoringReporter.VerdictArcade(Liste(fichiers)));
}
