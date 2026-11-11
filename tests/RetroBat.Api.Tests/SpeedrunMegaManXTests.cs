using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE MEGA MAN X (SNES, (USA) (Rev 1) crc32 ded53c64, prepares le 11/11, NON ouverts) : les huit stages des
/// Mavericks en depart direct sur l'etat d'entree du stage (pendant READY, 30 images avant la descente de X), de X pose
/// (0x0BAA 0x22 -> 0) au dernier coup sur le Maverick (0x0E68 = 0xFF), confirme par son arme (0x40). Les traces viennent du
/// banc snes9x sans fenetre du 11/11 : la pause et la mort depuis l'etat de Chill Penguin, l'arrivee depuis un etat pris de la
/// meme facon au point de reprise 2 (le coureur de banc triche pour atteindre le boss).
/// </summary>
public class SpeedrunMegaManXTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"mega-man-x.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("mega-man-x.MEM"));

    // slug, stage 0x1F7A, arme du boss
    public static IEnumerable<object[]> Stages()
        => new[]
        {
            ("chill-penguin", 8, 0x1F96), ("spark-mandrill", 6, 0x1F92), ("armored-armadillo", 3, 0x1F8C), ("launch-octopus", 1, 0x1F88),
            ("boomer-kuwanger", 7, 0x1F94), ("sting-chameleon", 2, 0x1F8A), ("storm-eagle", 5, 0x1F90), ("flame-mammoth", 4, 0x1F8E),
        }.Select(n => new object[] { n.Item1, n.Item2, n.Item3 });

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Theory]
    [MemberData(nameof(Stages))]
    public void Chaque_stage_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int stage, int arme)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"snes/mega-man-x/{slug}-entree.state", r.Etat!.Fichier);
        Assert.Equal(["snes9x"], r.Coeurs);
        Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel);
        Assert.Contains(r.Depart, c => c.Adresse == 0x1F7A && c.Egal == stage);
        var depart = Assert.Single(r.Independant!.Depart.Evenements);
        Assert.Equal((0x0BAAL, 0x00), (depart.Adresse!.Value, depart.Valeur!.Value));
        var arrivee = Assert.Single(r.Arrivee);
        Assert.Equal((0x0E68L, 0xFF), (arrivee.Adresse!.Value, arrivee.Valeur!.Value));
        var confirmation = Assert.Single(r.Confirmations);
        Assert.Equal(((long)arme, 0x40), (confirmation.Adresse!.Value, confirmation.Valeur!.Value));
    }

    [Fact]
    public void Les_huit_epreuves_ont_chacune_leur_classement_dans_l_ordre_de_la_route()
    {
        var reglements = Stages().Select(n => Lire((string)n[0])).ToList();
        Assert.Equal(8, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.All(reglements, r => Assert.StartsWith("ch-mmx-", r.Ruleset));
        Assert.Equal(Enumerable.Range(1, 8), reglements.Select(r => r.Ordre));
        Assert.Equal(8, reglements.Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    /// <summary>
    /// La trace d'arrivee part du point de reprise 2 (le coureur de banc n'atteint pas le boss depuis le debut du stage) : pour ce
    /// seul test, le reglement perd ses conditions sur le point de reprise 0x1F81 ; tout le reste est celui du depot.
    /// </summary>
    private static Reglement SansPointDeReprise(string slug)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Fichier($"mega-man-x.{slug}.json")))!;
        static void Retirer(System.Text.Json.Nodes.JsonArray a)
        {
            foreach (var c in a.Where(c => (string?)c!["value"]?["address"] == "0x1F81").ToList()) a.Remove(c);
        }
        Retirer(doc["start"]!.AsArray());
        Retirer(doc["timing"]!["independent"]!["start"]!["when"]!.AsArray());
        return Reglement.Lire(doc.ToJsonString());
    }

    [Fact]
    public void Chill_Penguin_de_X_pose_au_dernier_coup_confirme_par_l_arme()
    {
        var p = Assert.Single(BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier("mmx-penguin-arrivee.trace"))),
            LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)), [("chill-penguin", SansPointDeReprise("chill-penguin")), ("spark-mandrill", Lire("spark-mandrill"))]));
        Assert.Equal("chill-penguin", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(DEPART_ARRIVEE, p.Depart);
        Assert.Equal(DERNIER_COUP - DEPART_ARRIVEE, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Le_point_de_reprise_2_n_arme_pas_le_depart()
        => Assert.Empty(Jouer("mmx-penguin-arrivee", "chill-penguin"));

    [Fact]
    public void Une_mort_fait_echouer_le_stage()
    {
        var p = Assert.Single(Jouer("mmx-penguin-mort", "chill-penguin"));
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
        // apres la mort, le jeu remet 0x0BAA a 0 ecran noir (image 372), vie a 0 : ce n'est pas un depart
    }

    [Fact]
    public void Une_pause_annule_la_tentative()
    {
        var p = Assert.Single(Jouer("mmx-penguin-pause", "chill-penguin"));
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }

    [Fact]
    public void Le_coeur_hote_snes_porte_les_fantomes_des_stages()
    {
        Assert.Equal("snes9x_ghosts", FantomesDuSpeedrun.CoeurDe(Lire("chill-penguin")));
        Assert.Contains(FantomesDuSpeedrun.Liste, h => h.Groupe == "mega-man-x" && h.Crc == "ded53c64" && h.SurEtatSeulement);
    }

    // Images de la trace (la premiere image apres le chargement de l'etat vaut 1), relevees au banc du 11/11.
    private const long DEPART_ARRIVEE = 30;
    private const long DERNIER_COUP = 598;
}
