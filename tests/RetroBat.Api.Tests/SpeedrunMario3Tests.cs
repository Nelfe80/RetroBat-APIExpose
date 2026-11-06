using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE SUPER MARIO BROS. 3 (NES, (USA) (Rev 1), prepares la nuit du 06/11, NON ouverts) : les six niveaux du
/// monde 1 en depart direct sur l'etat d'entree (A sur le panneau de la carte, 4 images apres). Les parties viennent du banc
/// fceumm sans fenetre du 06/11 (un joueur automatique qui court) : chaque trace porte, image par image depuis le chargement
/// de l'etat, les adresses du .MEM (.user) qui ont change ; le banc des replays les fait passer par le wrapper simule et
/// l'evaluateur, comme sur la borne.
/// </summary>
public class SpeedrunMario3Tests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"super-mario-bros-3.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("super-mario-bros-3.MEM"));

    public static IEnumerable<object[]> Niveaux()
        => new[] { ("1-1", 0x01), ("1-2", 0x03), ("1-3", 0x01), ("1-4", 0x04), ("1-5", 0x0E), ("1-6", 0x04) }
            .Select(n => new object[] { n.Item1, n.Item2 });

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Theory]
    [MemberData(nameof(Niveaux))]
    public void Chaque_niveau_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int tuiles)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"nes/super-mario-bros-3/{slug}-entree.state", r.Etat!.Fichier);
        Assert.Equal(["fceumm"], r.Coeurs);
        Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel);
        Assert.Contains(r.Depart, c => c.Adresse == 0x0727 && c.Egal == 0);
        var depart = Assert.Single(r.Independant!.Depart.Evenements);
        Assert.Equal((0x070AL, tuiles), (depart.Adresse!.Value, depart.Valeur!.Value));
        var arrivee = Assert.Single(r.Arrivee);
        Assert.Equal((0x05F3L, 0x81), (arrivee.Adresse!.Value, arrivee.Valeur!.Value));
    }

    [Fact]
    public void Les_six_epreuves_ont_chacune_leur_classement_dans_l_ordre_du_jeu()
    {
        var reglements = Niveaux().Select(n => Lire((string)n[0])).ToList();
        Assert.Equal(6, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 6), reglements.Select(r => r.Ordre));
        Assert.Equal(6, reglements.Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    [Fact]
    public void Monde_1_1_du_chargement_du_niveau_a_la_carte()
    {
        var p = Assert.Single(Jouer("smb3-1-1-course", "1-1", "1-2", "1-4", "1-5"));
        Assert.Equal("1-1", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(DEPART_1_1, p.Depart);
        Assert.Equal(ARRIVEE_1_1 - DEPART_1_1, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Une_mort_fait_echouer_le_niveau()
    {
        var p = Assert.Single(Jouer("smb3-1-1-mort", "1-1"));
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
    }

    [Fact]
    public void Une_pause_annule_la_tentative()
    {
        var p = Assert.Single(Jouer("smb3-1-1-pause", "1-1"));
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }

    // Images de la trace (la premiere image apres le chargement de l'etat vaut 1), relevees au banc du 06/11.
    private const long DEPART_1_1 = 44;
    private const long ARRIVEE_1_1 = 1445;
}
