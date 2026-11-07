using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE ROCKET KNIGHT ADVENTURES (Mega Drive, (USA) crc32 a6efec47, prepares le 06/11, NON ouverts) : sept stages en
/// depart direct sur l'etat du portrait « STAGE N » (Easy), et l'Any% Easy. Chrono independant (le jeu n'a pas de chrono) : de la
/// main au joueur (0xB028 de 2 a 3) au premier ecran du stage suivant (scene 0xFB12). Traces du banc Genesis Plus GX sans fenetre
/// du 06/11 ; la fin du stage 1 est mesuree depuis sa derniere scene (la cinematique du dirigeable, scene 4) chargee au portrait.
/// </summary>
public class SpeedrunRkaTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"rocket-knight-adventures.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("rocket-knight-adventures.MEM"));

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Theory]
    [InlineData(1, 0x05)] [InlineData(2, 0x28)] [InlineData(3, 0x0D)] [InlineData(4, 0x17)] [InlineData(5, 0x19)] [InlineData(6, 0x1B)] [InlineData(7, 0x32)]
    public void Chaque_stage_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(int n, int sceneDArrivee)
    {
        var r = Lire($"stage-{n}");
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"megadrive/rocket-knight-adventures/stage-{n}-panneau.state", r.Etat!.Fichier);
        Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel);
        Assert.Contains(r.Depart, c => c.Adresse == 0xB088 && c.Egal == n);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFB16 && c.Egal == 1);   // Easy
        var arrivee = Assert.Single(r.Arrivee);
        Assert.Equal((0xFB12L, sceneDArrivee), (arrivee.Adresse!.Value, arrivee.Valeur!.Value));
    }

    [Fact]
    public void L_Any_Easy_cite_seulement_des_lignes_du_MEM()
        => Assert.Empty(Lire("any-easy").VerifierContre(Mem));

    [Fact]
    public void Le_stage_1_finit_au_premier_ecran_du_stage_2()
    {
        // main au joueur a l'image 85, l'etat du jeu tombe a 0 et la scene passe a 5 a l'image 1609
        var p = Jouer("rka-stage-1-fin", "stage-1", "stage-2")[0];
        Assert.Equal("stage-1", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(85, p.Depart);
        Assert.Equal(1609 - 85, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Une_mort_fait_echouer_le_stage()
    {
        var p = Jouer("rka-mort", "stage-1")[0];
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
    }

    [Fact]
    public void Une_pause_annule_la_tentative()
    {
        var p = Jouer("rka-pause", "stage-1")[0];
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }
}
