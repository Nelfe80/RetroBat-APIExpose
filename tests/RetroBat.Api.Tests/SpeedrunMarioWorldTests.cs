using RetroBat.Api.MassRun;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE SUPER MARIO WORLD (SNES, (USA), prepares le 06/11 au jour, NON ouverts) : les niveaux de Yoshi's Island
/// et de Donut Plains (huit : Donut Plains 2 attend son arrivee) en depart direct sur l'etat d'ouverture du niveau (mode $0100 = 0x13, la main a Mario 30 images plus
/// tard). Les traces viennent du banc snes9x sans fenetre du 06/11 (un coureur de banc qui vole jusqu'au ruban) : image par
/// image depuis le chargement de l'etat, les adresses du .MEM candidat qui ont change ; le banc des replays les fait passer
/// par le wrapper simule et l'evaluateur, comme sur la borne.
/// </summary>
public class SpeedrunMarioWorldTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"super-mario-world.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("super-mario-world.MEM"));

    // slug, numero du niveau $13BF
    public static IEnumerable<object[]> Niveaux()
        => new[] { ("yi1", 0x29), ("yi2", 0x2A), ("yi3", 0x27), ("yi4", 0x26), ("dp1", 0x15), ("dp3", 0x05), ("dp4", 0x06), ("ds1", 0x0A) }
            .Select(n => new object[] { n.Item1, n.Item2 });

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Theory]
    [MemberData(nameof(Niveaux))]
    public void Chaque_niveau_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int niveau)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"snes/super-mario-world/{slug}-entree.state", r.Etat!.Fichier);
        Assert.Equal(["snes9x"], r.Coeurs);
        Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel);
        Assert.Contains(r.Depart, c => c.Adresse == 0x13BF && c.Egal == niveau);
        var depart = Assert.Single(r.Independant!.Depart.Evenements);
        Assert.Equal((0x0100L, 0x14), (depart.Adresse!.Value, depart.Valeur!.Value));
        var arrivee = Assert.Single(r.Arrivee);
        Assert.Equal((0x1493L, 0xFF), (arrivee.Adresse!.Value, arrivee.Valeur!.Value));
    }

    [Fact]
    public void Les_huit_epreuves_ont_chacune_leur_classement_dans_l_ordre_du_jeu()
    {
        var reglements = Niveaux().Select(n => Lire((string)n[0])).ToList();
        Assert.Equal(8, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.All(reglements, r => Assert.StartsWith("ch-smw-", r.Ruleset));
        Assert.Equal(Enumerable.Range(1, 8), reglements.Select(r => r.Ordre));
        Assert.Equal(8, reglements.Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    [Fact]
    public void Yoshi_s_Island_2_de_l_ouverture_au_ruban()
    {
        var p = Assert.Single(Jouer("smw-yi2-course", "yi2", "yi1", "dp1"));
        Assert.Equal("yi2", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(DEPART_YI2, p.Depart);
        Assert.Equal(ARRIVEE_YI2 - DEPART_YI2, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Une_mort_fait_echouer_le_niveau()
    {
        var p = Assert.Single(Jouer("smw-yi2-mort", "yi2"));
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
    }

    [Fact]
    public void Une_pause_annule_la_tentative()
    {
        var p = Assert.Single(Jouer("smw-yi2-pause", "yi2"));
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }

    [Fact]
    public void Mass_run_les_niveaux_au_ruban_avec_le_coeur_hote_snes()
    {
        var jeu = JeuxMassRun.Trouver("snes/super-mario-world")!;
        Assert.Equal(0xb19ed489u, jeu.Crc);
        Assert.Equal("snes9x_ghosts", jeu.Coeur);
        Assert.True(jeu.PosesEnImages);
        Assert.Matches(jeu.Niveaux, "yi2");
        Assert.DoesNotMatch(jeu.Niveaux, "iggy");
        Assert.Equal((0x1493u, 0xFFu), JeuxMassRun.ArriveeDuNiveau(jeu, "dp1"));
        Assert.EndsWith(Path.Combine("snes", "super-mario-world", "yi2-entree.state"), JeuxMassRun.CheminDeLEtat(jeu, "yi2", "racine"));
    }

    // Images de la trace (la premiere image apres le chargement de l'etat vaut 1), relevees au banc du 06/11.
    private const long DEPART_YI2 = 30;
    private const long ARRIVEE_YI2 = 701;
}
