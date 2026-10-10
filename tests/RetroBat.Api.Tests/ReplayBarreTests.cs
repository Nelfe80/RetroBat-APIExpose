using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Overlay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La barre du replay (2026-10-10). La legende des reactions manquait a toutes les bornes sauf celle qui fabrique la
/// release : la planche etait lue dans media/, un dossier que ni la mise a jour ni l'installeur ne livrent. Le sceau
/// certifie mordait sur « SCORE » et sur le nombre : sa place etait faite d'espaces, que MeasureString ignore en fin de
/// chaine. Le double appui sur START n'etait annonce nulle part, et le rappel ajoute ne doit pas passer sous la carte.
/// </summary>
public class ReplayBarreTests
{
    // Lecture, recul/avance, retour au debut, quitter, reduire : l'ordre ou ils cedent leur place.
    private static readonly int[] Ordre = { 4, 2, 1, 0, 3 };
    private static readonly float[] Largeurs = { 170f, 230f, 160f, 135f, 180f };
    private const float Ecart = 30f;

    [Fact]
    public void Tous_les_rappels_tiennent_quand_la_place_suffit()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ReplayOverlayService.RappelsQuiTiennent(Largeurs, Ordre, Ecart, 2000f));
    }

    [Fact]
    public void Le_double_appui_cede_sa_place_en_premier_et_les_autres_gardent_leur_ordre()
    {
        var total = Largeurs.Sum() + 4 * Ecart;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ReplayOverlayService.RappelsQuiTiennent(Largeurs, Ordre, Ecart, total));
        Assert.Equal(new[] { 0, 1, 2, 3 }, ReplayOverlayService.RappelsQuiTiennent(Largeurs, Ordre, Ecart, total - 1f));
        Assert.Equal(new[] { 0, 1, 3 }, ReplayOverlayService.RappelsQuiTiennent(Largeurs, Ordre, Ecart, 170f + 230f + 135f + 2 * Ecart));
    }

    [Fact]
    public void Quitter_part_en_dernier()
    {
        Assert.Equal(new[] { 3 }, ReplayOverlayService.RappelsQuiTiennent(Largeurs, Ordre, Ecart, 140f));
        Assert.Empty(ReplayOverlayService.RappelsQuiTiennent(Largeurs, Ordre, Ecart, 100f));
    }

    [Fact]
    public void Un_nom_de_jeu_trop_long_s_abrege_et_un_nom_court_reste_entier()
    {
        static float Mesure(string t) => t.Length * 10f;
        Assert.Equal("BUBBLE BOBBLE", ReplayOverlayService.Abreger("BUBBLE BOBBLE", 200f, Mesure));
        Assert.Equal("TEENAGE M…", ReplayOverlayService.Abreger("TEENAGE MUTANT NINJA TURTLES", 100f, Mesure));
        // L'espace qui precederait les points de suspension saute.
        Assert.Equal("TEENAGE…", ReplayOverlayService.Abreger("TEENAGE MUTANT NINJA TURTLES", 85f, Mesure));
        Assert.Equal("…", ReplayOverlayService.Abreger("TEENAGE", 5f, Mesure));
    }

    [Fact]
    public void La_planche_des_reactions_voyage_dans_l_exe()
    {
        using (var flux = typeof(ReplayReactionSprites).Assembly.GetManifestResourceStream(ReplayReactionSprites.Ressource))
        {
            Assert.NotNull(flux);
        }
        using var planche = new ReplayReactionSprites(NullLogger.Instance);
        Assert.True(planche.Ok);
    }
}
