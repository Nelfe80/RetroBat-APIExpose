using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ANY% ET LE WARPLESS DE SUPER MARIO BROS. EN VERSION 2 (16/11, Data Pack fix/smb-any-reprise) : sur la borne d'Old AF Gamer,
/// apres une tentative annulee (pause), la suivante est partie a la reprise de Mario apres une mort, au point de passage de 1-1
/// (page 0x006D = 5). La v2 demande aussi la page 0 au depart : une course part du debut de 1-1, jamais de son milieu.
/// </summary>
public class SpeedrunMarioRepriseTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"super-mario-bros.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("super-mario-bros.MEM"));

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    /// <summary>Monde 0, niveau 0, partie en cours, page de Mario <paramref name="page"/>, puis Mario reprend la main.</summary>
    private static Evaluateur Reprendre(string slug, int page)
    {
        var ev = new Evaluateur(Lire(slug));
        ev.RecevoirImage([new Signal(130, "DEMO_MODE", 0x07A2, 0x17)]);
        foreach (var a in ev.AdressesInconnues) ev.Relever(a, a == 0x0770 ? 1 : a == 0x006D ? page : 0);
        ev.RecevoirImage([new Signal(5000, "LIVES_STATE", 0x000E, 0)]);
        ev.RecevoirImage([new Signal(5100, "GAME_PLAYING", 0x000E, 8)]);
        return ev;
    }

    [Theory]
    [InlineData("any.v2")]
    [InlineData("warpless.v2")]
    public void La_v2_cite_seulement_des_lignes_du_MEM_et_garde_le_classement_a_part(string slug)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal(2, r.Version);
        Assert.EndsWith("-v2", r.Ruleset);
        Assert.Equal("normal_boot", r.StartPolicy);
        Assert.Contains(r.Depart, c => c.Adresse == 0x006D && c.Egal == 0);
    }

    [Theory]
    [InlineData("any.v2")]
    [InlineData("warpless.v2")]
    public void Une_reprise_au_point_de_passage_de_1_1_ne_fait_partir_aucune_course(string slug)
        => Assert.Equal(EtatTentative.Preparee, Reprendre(slug, page: 5).Etat);

    [Theory]
    [InlineData("any.v2")]
    [InlineData("warpless.v2")]
    public void Le_debut_de_1_1_fait_partir_la_course(string slug)
        => Assert.Equal(EtatTentative.EnCourse, Reprendre(slug, page: 0).Etat);

    [Fact]
    public void La_v1_partait_sur_la_reprise()
        => Assert.Equal(EtatTentative.EnCourse, Reprendre("any", page: 5).Etat);

    [Fact]
    public void La_v2_mesure_la_partie_simulee_comme_la_v1()
    {
        var passages = Jouer("smb-any-simule", "any", "any.v2", "warpless.v2");
        Assert.Equal(3967 - 196, Assert.Single(passages, p => p.Defi == "any.v2").Resultat.FramesOfficielles);
        Assert.Equal(3967 - 196, Assert.Single(passages, p => p.Defi == "any").Resultat.FramesOfficielles);
        Assert.Equal("milestone_out_of_order", Assert.Single(passages, p => p.Defi == "warpless.v2").Resultat.Cause);
    }
}
