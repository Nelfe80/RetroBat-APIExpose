using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES ARRIVEES DES STAGES DE ROCKET KNIGHT ADVENTURES vues au banc sans fenetre (07/11, verification avant ouverture). Suite des
/// scenes lue dans la ROM (table « scene suivante » a 0x8322) : stage 1 : 1 2 3 11 4 -> 5 ; stage 3 : 40 41 42 43 45 46 -> 13 ;
/// stage 4 : 13 12 14 15 48 -> 23 ; stage 7 : 27 ... 35 -> 50 (sequence de fin). Stages 2, 5 et 6 retires (arrivee non vue). AIDES DE MESURE :
/// la scene de reprise est ecrite pendant le portrait de l'etat propose (le jeu la charge, 0xB088 garde le numero du stage), vie
/// de Sparkster remise pleine sous la moitie, joueur automatique. Chaque trace commence au chargement de l'etat (image 1 : portrait).
/// </summary>
public class SpeedrunRkaArriveesTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"rocket-knight-adventures.{slug}.json")));

    private static PassageMesure Jouer(int n)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier($"rka-stage-{n}-arrivee.trace"))),
            LignesDuMem.Lire(File.ReadAllText(Fichier("rocket-knight-adventures.MEM"))), [($"stage-{n}", Lire($"stage-{n}"))])[0];

    // stage, image de la main au joueur (0xB028 de 2 a 3), image de l'arrivee (0xFB12 = premiere scene du stage suivant)
    public static readonly TheoryData<int, long, long> Arrivees = new()
    {
        { 1, 79, 1600 },    // boss de la tour et cinematique du dirigeable (scene 4) -> 5
        { 3, 55, 985 },     // scene 45 -> 46 (cinematique) -> 13
        { 4, 70, 6771 },    // scene 15 -> 48 (cinematique) -> 23 ; des degats continus en scene 15 (voir plus bas)
        { 7, 69, 4205 },    // dernier boss (scene 35) battu, ecran blanc -> 50
    };

    [Theory]
    [MemberData(nameof(Arrivees))]
    public void L_arrivee_est_la_premiere_scene_du_stage_suivant(int n, long depart, long arrivee)
    {
        var p = Jouer(n);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(depart, p.Depart);
        Assert.Equal(arrivee, p.Resultat.FrameDeFin);
        Assert.Equal(arrivee - depart, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Le_MEM_n_a_plus_de_ligne_u32be_sur_la_scene()
    {
        // 07/11 : les lignes « 0xFB12 u32be » lisaient aussi 0xFB14, qui suit la vie ; des degats continus (scene 15 du stage 4,
        // un point toutes les 4 images) les faisaient couper par le wrapper et annulaient l'epreuve (sensor_muted). Retirees :
        // la scene se lit par la seule ligne u8.
        Assert.DoesNotContain(File.ReadAllLines(Fichier("rocket-knight-adventures.MEM")),
            l => l.Contains("address=0XFB12", StringComparison.OrdinalIgnoreCase) && l.Contains("type=\"u32be\""));
    }

    [Fact]
    public void Le_stage_7_et_l_Any_Easy_finissent_a_la_sequence_de_fin()
    {
        // 0x21 (33) est le milieu du stage 7 : l'arrivee est la scene 50 (0x32), premiere scene de la sequence de fin.
        foreach (var slug in new[] { "stage-7", "any-easy" })
        {
            var a = Assert.Single(Lire(slug).Arrivee);
            Assert.Equal((0xFB12L, 0x32), (a.Adresse!.Value, a.Valeur!.Value));
        }
    }
}
