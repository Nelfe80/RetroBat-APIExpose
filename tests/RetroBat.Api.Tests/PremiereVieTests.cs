using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE 1LC, SCORE DE LA PREMIERE VIE (decision user du 2026-10-09). La meme partie donne son 1CC et son
/// 1LC, chacun a son classement. Les mesures viennent du labo d'Alex Kidd in Miracle World : trois vies,
/// la premiere perdue a la frame 1318 (vies 3 puis 2), score garde d'une vie a l'autre.
/// </summary>
public class PremiereVieTests
{
    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? Regle(JsonElement? p) => p?.GetProperty("ruleset").GetString();

    private static readonly List<(long frame, long total)> Run =
    [
        (760, 0), (900, 200), (1200, 600), (1500, 800), (2300, 2800),
    ];

    [Fact]
    public void Le_1LC_s_arrete_a_la_premiere_vie_perdue()
    {
        var (run, mort) = PremiereVie.Couper(Run, [(1318L, 1), (2089L, 1)]);
        Assert.Equal(1318, mort);
        Assert.Equal(600, run[^1].total);
        Assert.Equal(3, run.Count);
    }

    [Fact]
    public void Sans_vie_perdue_le_1LC_est_le_run_entier()
    {
        var (run, mort) = PremiereVie.Couper(Run, []);
        Assert.Null(mort);
        Assert.Equal(2800, run[^1].total);
    }

    [Fact]
    public void Une_mort_d_une_partie_precedente_ne_compte_pas()
    {
        // Le run retenu commence a la frame 760 : la mort de la frame 400 est celle d'une autre partie.
        var (_, mort) = PremiereVie.Couper(Run, [(400L, 1), (1318L, 1)]);
        Assert.Equal(1318, mort);
    }

    [Fact]
    public void La_mort_du_joueur_2_ne_coupe_pas_le_1LC_du_joueur_1()
    {
        var (_, mort) = PremiereVie.Couper(Run, [(1000L, 2), (2089L, 1)]);
        Assert.Equal(2089, mort);
    }

    [Fact]
    public void Une_lecture_a_la_frame_de_la_mort_reste_dans_le_1LC()
    {
        var (run, _) = PremiereVie.Couper(Run, [(1500L, 1)]);
        Assert.Equal(800, run[^1].total);
    }

    [Fact]
    public void Le_profil_1LC_ne_recoit_jamais_le_1CC_d_une_partie()
    {
        var unLC = Profil("""{"ruleset":"1lc"}""");
        var unCC = Profil("""{"ruleset":"1cc"}""");
        var multi = Profil("""{"ruleset":"1cc-multi"}""");
        // Meme en tete de liste (version plus haute), le 1LC n'est pas le profil d'une partie seule.
        Assert.Equal("1cc", Regle(ModesDeJeu.ChoisirProfil([unLC, unCC, multi], null)));
        Assert.Equal("1lc", Regle(ModesDeJeu.ChoisirProfil1LC([unLC, unCC, multi], null)));
        Assert.Equal(["1cc"], ModesDeJeu.DuSolo([unLC, unCC, multi]).Select(p => Regle(p)).ToList());
    }

    [Fact]
    public void Un_jeu_sans_1LC_ouvert_ne_soumet_pas_de_1LC()
    {
        Assert.Null(ModesDeJeu.ChoisirProfil1LC([Profil("""{"ruleset":"1cc"}""")], null));
    }

    [Fact]
    public void Un_1LC_a_modes_suit_le_mode_joue()
    {
        var a = Profil("""{"ruleset":"1lc-original","mode":{"value":1,"default":true}}""");
        var b = Profil("""{"ruleset":"1lc-super","mode":{"value":2}}""");
        var cc = Profil("""{"ruleset":"1cc-original","mode":{"value":1,"default":true}}""");
        Assert.Equal("1lc-super", Regle(ModesDeJeu.ChoisirProfil1LC([a, b, cc], 2)));
        Assert.Equal("1lc-original", Regle(ModesDeJeu.ChoisirProfil1LC([a, b, cc], null)));
        Assert.Equal("1cc-original", Regle(ModesDeJeu.ChoisirProfil([a, b, cc], 1)));
    }

    [Fact]
    public void Le_1LC_se_rattache_au_replay_du_1CC()
    {
        Assert.Equal("rp_ABC", RetroBat.Api.Infrastructure.NelfePlayScoringReporter.ReplayDuLien("rp_ABC|1lc"));
        Assert.Equal("rp_ABC", RetroBat.Api.Infrastructure.NelfePlayScoringReporter.ReplayDuLien("rp_ABC"));
    }

    // Le bloc de vies de Double Dragon tel que le Data Pack le porte (2026-10-09) : un drapeau de sante a
    // zero, le compteur du joueur 1, et ceux du joueur 2.
    private const string DoubleDragon = """
        lives = {
          { address=0X03C1, type="u8", condition="eq", value=0X00, action="LOSE_LIFE", player=1, desc="1P Dead (sante a zero)" },
          { address=0X03EA, type="u8", condition="increase", action="GAIN_LIFE", player=1, desc="1P Lives increased" },
          { address=0X03EA, type="u8", condition="decrease", action="LOSE_LIFE", player=1, desc="1P Lives decreased" },
          { address=0X041F, type="u8", condition="eq", value=0X00, action="LOSE_LIFE", player=2, desc="2P Dead (sante a zero)" },
          { address=0X0448, type="u8", condition="decrease", action="LOSE_LIFE", player=2, desc="2P Lives decreased" },
          -- { address=0X0500, type="u8", condition="decrease", action="LOSE_LIFE", desc="ligne en commentaire" },
          { address=0X0600, type="u8", condition="decrease", action="LOSE_LIFE", no_log=true, desc="ligne muette" },
        },
        """;

    [Fact]
    public void Les_compteurs_sont_les_lignes_LOSE_LIFE_qui_descendent()
    {
        Assert.Equal(["0x3EA", "0x448"], PremiereVie.Compteurs(DoubleDragon).OrderBy(a => a).ToList());
        Assert.Empty(PremiereVie.Compteurs(null));
    }

    [Fact]
    public void Un_drapeau_qui_parle_au_demarrage_ne_coupe_pas_le_1LC()
    {
        // La sante passe par zero au depart (frame 800), la vraie mort fait baisser le compteur a 1318.
        var pertes = new List<(string, long, int)> { ("0x03C1", 800, 1), ("0x3C1", 1300, 1), ("0x03EA", 1318, 1) };
        var retenues = PremiereVie.SurLesCompteurs(pertes, PremiereVie.Compteurs(DoubleDragon));
        var (_, mort) = PremiereVie.Couper(Run, retenues);
        Assert.Equal(1318, mort);
    }

    [Fact]
    public void Sans_compteur_entendu_toutes_les_pertes_comptent()
    {
        // Mort sur la derniere vie (le compteur ne descend pas), ou adresse lue autrement : le drapeau coupe.
        var pertes = new List<(string, long, int)> { ("0x03C1", 1300, 1) };
        var (_, mort) = PremiereVie.Couper(Run, PremiereVie.SurLesCompteurs(pertes, PremiereVie.Compteurs(DoubleDragon)));
        Assert.Equal(1300, mort);
    }

    [Fact]
    public void Le_speedrun_n_est_jamais_le_profil_d_une_partie_seule()
    {
        var speedrun = Profil("""{"ruleset":"speedrun"}""");
        var unCC = Profil("""{"ruleset":"1cc"}""");
        Assert.Equal("1cc", Regle(ModesDeJeu.ChoisirProfil([speedrun, unCC], null)));
    }

    [Fact]
    public void Des_points_marques_d_un_coup_avant_la_premiere_mort_font_un_1LC()
    {
        // Alex Kidd sur la borne (2026-10-09), lance au bouton sans START : 200 points, la premiere vie
        // perdue, puis 400, 600 et 800. Le 0 du depart n'est pas une lecture.
        var run = new List<(long frame, long total)> { (1500, 200), (1950, 400), (2350, 600), (2460, 800) };
        var (run1lc, mort) = PremiereVie.Couper(run, [(1843L, 1)]);
        Assert.Equal(1843, mort);
        Assert.True(PremiereVie.AMarque(run1lc));
        Assert.Equal(200, run1lc[^1].total);
    }

    [Fact]
    public void Rien_de_marque_avant_la_premiere_mort_ne_fait_pas_de_1LC()
    {
        Assert.False(PremiereVie.AMarque([]));
        // Un run ouvert par une remise a zero commence a 0.
        Assert.False(PremiereVie.AMarque([(100L, 0L)]));
        Assert.True(PremiereVie.AMarque([(100L, 0L), (200L, 300L)]));
        // Le score au depart (START vu) puis les premiers points : comme avant.
        Assert.True(PremiereVie.AMarque([(50L, 0L), (100L, 200L)]));
    }

    // ---- 19XX de la borne, 25/11 : 1CC ET 1LC publies a 104 600 apres trois vies perdues (replay rp_06H16PG8G14H4X85CAZN8QAN6G).
    // Images et scores relus dans la boite noire du replay : vies 2 a l'image 736, 1 a 2541, 0 a 5087, GAME OVER a 9261 ;
    // le score monte jusqu'a 49 900 (image ~6 700), retombe a 183 pendant l'ecran de fin de stage (lecture 183 a l'image
    // 7 700, 84 900 entre deux), puis monte a 104 600 (image 9 115).
    private static readonly List<(long frame, long total)> Trajectoire19xx =
    [
        (1707, 100), (1900, 1700), (2300, 1900), (3000, 5800), (4400, 10700), (5000, 14600), (5800, 24300), (6400, 38400),
        (6700, 49900),
        (7700, 183), (7701, 84900), (7702, 183), (8069, 85000), (8091, 86000), (8101, 86100), (8172, 87100), (8191, 87700),
        (8239, 89500), (8289, 90300), (8345, 93300), (8445, 94900), (8667, 96200), (8861, 96800), (8916, 101600), (9044, 104000),
        (9114, 104300), (9115, 104600),
    ];

    [Fact]
    public void Le_19XX_du_25_11_le_run_retenu_commence_apres_les_deux_premieres_vies_perdues()
    {
        var run = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(Trajectoire19xx);
        Assert.Equal(104600, run[^1].total);
        Assert.True(run[0].frame > 5087, $"le run commence a l'image {run[0].frame}");

        // Ce que faisait Couper : les deux pertes sont d'avant le run, donc ecartees, donc « aucune vie perdue » (le bug).
        var (entier, mort) = PremiereVie.Couper(run, [(2541L, 1), (5087L, 1)]);
        Assert.Null(mort);
        Assert.Equal(104600, entier[^1].total);
    }

    [Fact]
    public void Une_perte_d_avant_le_run_sans_partie_neuve_entre_les_deux_interdit_le_1LC()
    {
        var run = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(Trajectoire19xx);
        // Un seul credit, consomme au depart (image 726) : rien ne montre une partie neuve apres la premiere perte.
        var sansPlace = PremiereVie.PerteSansPlace(run, [(2541L, 1), (5087L, 1)], Trajectoire19xx, [726L]);
        Assert.Equal(2541, sansPlace);
    }

    [Fact]
    public void Un_credit_une_fin_de_partie_ou_un_START_entre_la_perte_et_le_run_la_rend_a_l_autre_partie()
    {
        var run = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(Trajectoire19xx);
        Assert.Null(PremiereVie.PerteSansPlace(run, [(2541L, 1)], Trajectoire19xx, [726L, 6000L]));   // credit consomme ensuite
        Assert.Null(PremiereVie.PerteSansPlace(run, [(2541L, 1)], Trajectoire19xx, [run[0].frame]));  // a l'image du depart du run
        // Un depart d'AVANT la perte ne dit rien sur elle.
        Assert.Equal(2541, PremiereVie.PerteSansPlace(run, [(2541L, 1)], Trajectoire19xx, [2000L]));
    }

    [Fact]
    public void Une_perte_du_joueur_2_ou_posterieure_au_depart_du_run_ou_sans_point_marque_avant_elle_ne_gene_pas()
    {
        var run = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(Trajectoire19xx);
        Assert.Null(PremiereVie.PerteSansPlace(run, [(2541L, 2)], Trajectoire19xx, []));            // joueur 2
        Assert.Null(PremiereVie.PerteSansPlace(run, [(8500L, 1)], Trajectoire19xx, []));            // dans le run : Couper la place
        // Initialisation des vies d'un depart : aucune lecture positive avant elle.
        Assert.Null(PremiereVie.PerteSansPlace(run, [(1000L, 1)], Trajectoire19xx, []));
        // Partie ordinaire : le run commence au debut de la partie, la premiere perte est dedans.
        Assert.Null(PremiereVie.PerteSansPlace(Run, [(1318L, 1)], Run, [760L]));
    }
}
