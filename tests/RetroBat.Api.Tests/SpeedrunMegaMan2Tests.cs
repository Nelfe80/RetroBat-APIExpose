using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE MEGA MAN 2 (NES, (USA), prepares le 06/11, NON ouverts) : les huit stages des Robot Masters en depart
/// direct sur l'etat d'entree (READY du stage, partie neuve en Difficult, sans arme), du debut de la teleportation (0x0400 =
/// 0x1A) a la defaite du boss (0x00BD = 1). Les parties viennent du banc fceumm sans fenetre du 06/11 : chaque trace porte,
/// image par image depuis le chargement de l'etat, les adresses du .MEM (.user) qui ont change ; le banc des replays les
/// fait passer par le wrapper simule et l'evaluateur, comme sur la borne.
/// </summary>
public class SpeedrunMegaMan2Tests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"mega-man-2.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("mega-man-2.MEM"));

    public static IEnumerable<object[]> Stages()
        => new[] { ("heat-man", 0), ("air-man", 1), ("wood-man", 2), ("bubble-man", 3), ("quick-man", 4), ("flash-man", 5),
                   ("metal-man", 6), ("crash-man", 7) }
            .Select(n => new object[] { n.Item1, n.Item2 });

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Theory]
    [MemberData(nameof(Stages))]
    public void Chaque_stage_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int stage)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"nes/mega-man-2/{slug}-entree.state", r.Etat!.Fichier);
        Assert.Equal(["fceumm"], r.Coeurs);
        Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel);
        Assert.Contains(r.Depart, c => c.Adresse == 0x002A && c.Egal == stage);
        Assert.Contains(r.Depart, c => c.Adresse == 0x00CB && c.Egal == 1);
        var depart = Assert.Single(r.Independant!.Depart.Evenements);
        Assert.Equal((0x0400L, 0x1A), (depart.Adresse!.Value, depart.Valeur!.Value));
        var arrivee = Assert.Single(r.Arrivee);
        Assert.Equal((0x00BDL, 0x01), (arrivee.Adresse!.Value, arrivee.Valeur!.Value));
    }

    [Fact]
    public void Les_stages_partent_sur_le_coeur_a_fantomes()
    {
        // Le profil du coeur hote est un fichier (a9bd44bc.ini, Data Pack) : FantomesDuSpeedrun n'a qu'a nommer le jeu.
        Assert.All(Stages(), n => Assert.Equal("fceumm_ghosts", FantomesDuSpeedrun.CoeurDe(Lire((string)n[0]))));
    }

    [Fact]
    public void Les_huit_epreuves_ont_chacune_leur_classement_dans_l_ordre_du_jeu()
    {
        var reglements = Stages().Select(n => Lire((string)n[0])).ToList();
        Assert.Equal(8, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.All(reglements, r => Assert.StartsWith("ch-mm2-", r.Ruleset));
        Assert.Equal(Enumerable.Range(1, 8), reglements.Select(r => r.Ordre));
        Assert.Equal(8, reglements.Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    [Fact]
    public void Le_depart_est_la_teleportation_et_un_autre_stage_ne_part_pas()
    {
        // Le stage de Heat Man : seul son reglement part (0x002A = 0) ; la course n'a pas d'arrivee, elle reste en course.
        var p = Assert.Single(Jouer("mm2-heat-man-depart", "heat-man", "air-man", "crash-man"));
        Assert.Equal("heat-man", p.Defi);
        Assert.Equal(DEPART_HEAT, p.Depart);
    }

    [Fact]
    public void Heat_Man_de_la_teleportation_a_sa_defaite()
    {
        // Trace assemblee de deux morceaux mesures : le depart (jusqu'a l'image 120), puis le combat contre Heat Man depuis le
        // dernier point de passage du stage (Mega Man invincible et PV du boss a 1 : mesure seulement), jusqu'a 0x00BD = 1.
        var p = Assert.Single(Jouer("mm2-heat-man-course-assemblee", "heat-man", "air-man"));
        Assert.Equal("heat-man", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(DEPART_HEAT, p.Depart);
        Assert.Equal(DEFAITE_HEAT - DEPART_HEAT, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Une_chute_fait_echouer_le_stage()
    {
        var p = Assert.Single(Jouer("mm2-air-man-chute", "air-man"));
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
    }

    [Fact]
    public void Le_menu_des_armes_annule_la_tentative()
    {
        var p = Assert.Single(Jouer("mm2-heat-man-pause", "heat-man"));
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }

    // Images des traces (la premiere image apres le chargement de l'etat vaut 1), relevees au banc du 06/11.
    private const long DEPART_HEAT = 48;
    private const long DEFAITE_HEAT = 590;
}
