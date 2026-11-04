using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE SUPER MARIO BROS. (NES, demande du user du 03/11 : « tous les niveaux etc comme Sonic »). 32 niveaux en
/// depart direct sur l'etat de l'ecran « WORLD x-y », l'Any% et le Warpless en partie neuve. Les parties viennent du banc fceumm
/// du 03/11 (hote libretro hors borne, un bot qui court) : chaque trace porte, image par image, les adresses du .MEM qui ont
/// change ; le banc des replays les fait passer par le wrapper simule et l'evaluateur, comme sur la borne.
/// </summary>
public class SpeedrunMarioTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"super-mario-bros.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("super-mario-bros.MEM"));

    public static IEnumerable<object[]> Niveaux()
        => Enumerable.Range(0, 8).SelectMany(m => Enumerable.Range(0, 4).Select(n => new object[] { $"{m + 1}-{n + 1}", m, n }));

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Fact]
    public void Les_34_epreuves_ont_chacune_leur_classement_dans_l_ordre_du_jeu()
    {
        var reglements = new[] { "any", "warpless" }.Concat(Niveaux().Select(n => (string)n[0])).Select(Lire).ToList();
        Assert.Equal(34, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 34), reglements.Select(r => r.Ordre));
        Assert.All(reglements, r => Assert.Equal(["fceumm"], r.Coeurs));
        Assert.All(reglements, r => Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel));
        Assert.Equal(32, reglements.Where(r => r.Kind == "speedrun_level").Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Niveaux))]
    public void Chaque_niveau_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int monde, int niveau)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"nes/super-mario-bros/{slug}-panneau.state", r.Etat!.Fichier);
        Assert.Contains(r.Depart, c => c.Adresse == 0x075F && c.Egal == monde);
        Assert.Contains(r.Depart, c => c.Adresse == 0x075C && c.Egal == niveau);
        Assert.Contains(r.Depart, c => c.Adresse == 0x0770 && c.Egal == 1);
        // Un chateau finit a la hache (0x0770 = 2), les autres au mat (0x000E = 4).
        var arrivee = Assert.Single(r.Arrivee);
        Assert.Equal(niveau == 3 ? (0x0770L, 2) : (0x000EL, 4), (arrivee.Adresse!.Value, arrivee.Valeur!.Value));
    }

    [Fact]
    public void Le_jeu_complet_cite_seulement_des_lignes_du_MEM()
    {
        Assert.Empty(Lire("any").VerifierContre(Mem));
        Assert.Empty(Lire("warpless").VerifierContre(Mem));
        Assert.Equal("normal_boot", Lire("any").StartPolicy);
    }

    [Fact]
    public void Monde_1_1_du_depart_au_mat()
    {
        // Main au joueur a l'image 196 (154 images apres Start), mat a l'image 1655.
        var p = Assert.Single(Jouer("smb-1-1-mat", "1-1", "1-2", "1-4"));
        Assert.Equal("1-1", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(1655 - 196, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Monde_1_4_du_depart_a_la_hache()
    {
        var p = Assert.Single(Jouer("smb-1-4-hache", "1-1", "1-4"));
        Assert.Equal("1-4", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(1547 - 196, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Une_pause_annule_la_tentative()
    {
        var p = Assert.Single(Jouer("smb-1-1-pause", "1-1"));
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }

    [Fact]
    public void Un_niveau_rate_se_relance_seul_et_l_any_non()
    {
        // demande du user du 04/11 : une mort en speedrun de niveau renvoie au debut du niveau, chrono a 0
        Assert.True(SessionSpeedrun.SeRelanceSeul(Lire("1-1")));
        Assert.False(SessionSpeedrun.SeRelanceSeul(Lire("any")));
    }

    [Fact]
    public void La_page_de_depart_ne_porte_pas_la_ligne_des_fantomes()
    {
        // 04/11 : la fiche a deux colonnes ; un 7e champ ouvrait une 4e rangee et la carte passait sur le bouton HOLD.
        var fantomes = new FantomesDuSpeedrun(null!, null!, null!, null!, null!);
        var service = new DefisPanelService(null!, null!, null!, null!, null!, fantomes: fantomes);

        var page = service.ComposerLaPreparation(Lire("1-1"), "Super Mario Bros.", null, false, "fr");

        Assert.Equal(6, page.Fiche!.Count);
        Assert.DoesNotContain(page.Fiche!, c => c.Etiquette == "Ghosts");
    }

    [Fact]
    public void Une_vie_perdue_fait_echouer_un_niveau()
    {
        var p = Assert.Single(Jouer("smb-1-1-mort", "1-1"));
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
    }

    [Fact]
    public void L_any_finit_a_la_hache_de_la_fin_du_jeu_pas_a_celle_d_un_chateau_des_mondes_1_a_7()
    {
        // Partie neuve : 1-1, vie perdue vers 1-4, hache (image 1737), monde 2, vie perdue vers 1-4 jouee en monde 8 :
        // la hache de l'image 3967 joue la fin du jeu, le 5e message de la princesse la confirme.
        var passages = Jouer("smb-any-simule", "any", "warpless", "1-1");
        var any = Assert.Single(passages, p => p.Defi == "any");
        Assert.Equal(EtatTentative.Finie, any.Resultat.Etat);
        Assert.Equal(3967 - 196, any.Resultat.FramesOfficielles);
        // Le Warpless voit le monde revenir de 2 a 1 puis sauter a 8 : etapes hors d'ordre.
        var warpless = Assert.Single(passages, p => p.Defi == "warpless");
        Assert.Equal(EtatTentative.Ratee, warpless.Resultat.Etat);
        Assert.Equal("milestone_out_of_order", warpless.Resultat.Cause);
    }

    [Fact]
    public void La_demo_du_titre_ne_fait_partir_aucun_chrono()
    {
        // Les 40 premieres images de chaque trace sont le titre (0x000E = 8, mode 0) : aucun depart avant Start.
        var passages = Jouer("smb-1-1-mat", "any", "warpless");
        Assert.All(passages, p => Assert.Equal(196, p.Depart));
    }
}
