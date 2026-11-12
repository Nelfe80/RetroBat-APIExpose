using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA DEMONSTRATION DE DODONPACHI DAIOUJOU CERTIFIEE (11/11, 15:56-15:58 UTC, API 1.9.169) : 2 554 070, premiere des
/// classements 1CC et 1LC, au nom de la borne. Bandeau DEMONSTRATION, INSERT COIN, CREDITS:0 a l'ecran ; aucune entree
/// dans le replay ; press_count 2 (20 images tenues) au passeport ; 41 s de score qui monte (images 3109 a 5555).
/// Journal : douze START lus au panel entre 16:56:58 et 16:58:40 (heure locale), aucun « credit consomme », le meilleur
/// run de la demo (345 lectures) soumis. La partie jouee juste apres (17:08) : credit consomme a l'image 2000, 498 appuis.
/// </summary>
public class DemoCertifieeTests
{
    private const string MemDaiOuJou = """
        lifecycle = {
          { address=0X3926, type="u8", condition="change", action="DEMO_MODE", desc="[1 byte] Demo indicator" },
        },
        collectibles = {
          { address=0X395B, type="u8", condition="change", action="CREDITS", desc="Inserted credits remaining" },
        },
        """;

    private const string MemSansDepart = """
        { address=0XFF4D15, type="u8", condition="change", action="SCORE_STATE", desc="Score" },
        """;

    private const string MemGameStart = """
        { address=0X10FDB6, type="u8", condition="eq", value=0X01, action="GAME_START", desc="P1 playing" },
        """;

    /// <summary>Les douze START du journal, aux images de la session (une seconde vaut 60 images).</summary>
    private static readonly List<DepartDeJoueur> StartsDeLaDemo =
        new[] { 3000L, 3500, 4700, 4840, 4855, 4878, 5300, 5320, 7900, 8140, 8155, 8170 }.Select(f => new DepartDeJoueur(1, f)).ToList();

    /// <summary>Le meilleur run de la demo : 0 a 2 554 070 de l'image 3109 a l'image 5555.</summary>
    private static readonly List<(long frame, long total)> RunDeLaDemo = Enumerable.Range(0, 345)
        .Select(i => (3109L + i * (5555 - 3109) / 344, (long)i * 2554070 / 344)).ToList();

    private static DepartReel.Bilan Demo(string? mem = MemDaiOuJou, long? appuis = 2, long? tenues = 20,
        List<EvenementDeCredit>? credits = null, List<DepartDeJoueur>? departs = null, bool gameStart = false, bool impose = false)
    {
        var (c, g) = DepartReel.LignesDeDepart(mem);
        credits ??= new List<EvenementDeCredit>();
        return new DepartReel.Bilan(c || credits.Count > 0, g, credits, departs ?? StartsDeLaDemo, gameStart, impose,
            new DepartReel.Appuis(appuis, tenues), DepartReel.ImagesDuRun(RunDeLaDemo));
    }

    [Fact]
    public void La_demo_de_DaiOuJou_n_est_pas_certifiee()
    {
        var refus = DepartReel.Refus(Demo());
        Assert.NotNull(refus);
        Assert.Contains("presque aucun appui", refus);
    }

    [Fact]
    public void Le_run_de_la_demo_dure_41_secondes_et_demande_5_appuis()
    {
        var images = DepartReel.ImagesDuRun(RunDeLaDemo);
        Assert.Equal(2446, images);
        Assert.Equal(5, DepartReel.AppuisRequis(images));
        Assert.False(DepartReel.AppuisSuffisants(new DepartReel.Appuis(2, 20), images));
    }

    [Fact]
    public void Les_START_tapes_pendant_la_demo_sans_piece_ne_font_pas_un_depart()
    {
        // Si les douze START avaient atteint le jeu : le nombre y est (12 pour 5), la porte 2 passe ; aucun credit
        // consomme pour une ligne de credits declaree, et 120 images tenues sur 2 446 : pas une partie.
        var refus = DepartReel.Refus(Demo(appuis: 12, tenues: 120));
        Assert.NotNull(refus);
        Assert.Contains("aucun credit consomme", refus);
    }

    [Fact]
    public void Une_piece_lue_puis_jamais_consommee_n_est_pas_un_depart()
    {
        // Une piece mise pendant la demo, START jamais presse au bon moment : la ligne parle, rien ne la consomme.
        var credits = new List<EvenementDeCredit> { new(1, 2500) };
        var refus = DepartReel.Refus(Demo(appuis: 400, tenues: 1500, credits: credits));
        Assert.NotNull(refus);
        Assert.Contains("ligne des credits est lue", refus);
    }

    [Fact]
    public void La_partie_jouee_juste_apres_avec_son_credit_est_certifiee()
    {
        // 17:08 : piece (0 -> 1), START a l'image 2000 (1 -> 0), 498 appuis, run de 7 807 images.
        var credits = new List<EvenementDeCredit> { new(1, 1800), new(0, 2000) };
        var run = new List<(long, long)> { (2000, 0), (9807, 554380) };
        var (c, g) = DepartReel.LignesDeDepart(MemDaiOuJou);
        var bilan = new DepartReel.Bilan(c, g, credits, new List<DepartDeJoueur> { new(1, 1998) }, false, false,
            new DepartReel.Appuis(498, 9000), DepartReel.ImagesDuRun(run));
        Assert.Null(DepartReel.Refus(bilan));
    }

    [Fact]
    public void La_piece_et_le_START_pendant_la_chauffe_valent_un_depart_si_le_joueur_joue()
    {
        // Aucune lecture des credits de toute la partie (piece et START dans les 120 premieres images) : le START
        // suivi des appuis d'une partie (assez d'appuis, des touches tenues) fait le depart.
        Assert.Null(DepartReel.Refus(Demo(appuis: 300, tenues: 1800, departs: new() { new(1, 100) })));
    }

    [Fact]
    public void La_premiere_lecture_qui_tombe_sur_le_START_est_le_credit_consomme()
    {
        // Piece au clavier pendant la chauffe : la premiere lecture (0) est celle du START, sans lecture d'avant.
        var credits = new List<EvenementDeCredit> { new(0, 3020) };
        Assert.True(DepartReel.CreditConsomme(credits, StartsDeLaDemo));
        Assert.Null(DepartReel.Refus(Demo(appuis: 300, tenues: 1800, credits: credits)));
    }

    [Fact]
    public void Appuis_inconnus_et_aucun_credit_consomme_la_demo_ne_passe_pas()
    {
        // Pont MAME : pas de press_count. La ligne de credits declaree, aucun credit consomme : refus.
        Assert.NotNull(DepartReel.Refus(Demo(appuis: null, tenues: null)));
    }

    [Fact]
    public void Appuis_inconnus_mais_credit_consomme_la_partie_passe()
    {
        var credits = new List<EvenementDeCredit> { new(1, 2900), new(0, 3000) };
        Assert.Null(DepartReel.Refus(Demo(appuis: null, tenues: null, credits: credits)));
    }

    [Fact]
    public void Un_jeu_sans_ligne_de_depart_ne_garde_que_la_porte_des_appuis()
    {
        Assert.NotNull(DepartReel.Refus(Demo(mem: MemSansDepart)));
        Assert.Null(DepartReel.Refus(Demo(mem: MemSansDepart, appuis: 12, tenues: 120)));
        Assert.Null(DepartReel.Refus(Demo(mem: MemSansDepart, appuis: null, tenues: null)));
    }

    [Fact]
    public void Le_GAME_START_du_MEM_fait_le_depart()
    {
        Assert.Null(DepartReel.Refus(Demo(mem: MemGameStart, appuis: 200, tenues: 900, gameStart: true)));
        // Declare et jamais tombe, START tapes sans partie : refus.
        Assert.NotNull(DepartReel.Refus(Demo(mem: MemGameStart, appuis: 12, tenues: 120)));
    }

    [Fact]
    public void Un_depart_impose_multiplayer_ou_labo_suffit_s_il_y_a_des_appuis()
    {
        Assert.Null(DepartReel.Refus(Demo(appuis: 200, tenues: 900, impose: true)));
        // Meme impose, une partie sans appui reste refusee.
        Assert.NotNull(DepartReel.Refus(Demo(appuis: 2, tenues: 20, impose: true)));
    }

    [Theory]
    // appuis, images tenues, images du run, attendu : les passeports publies les plus maigres de la borne.
    [InlineData(4L, 152L, 1019L, true)]      // Ms. Pac-Man 150, 17 s, quatre directions tenues
    [InlineData(29L, 693L, 1295L, true)]     // 19XX 7 300
    [InlineData(6L, 74L, 554L, true)]        // Batsugun Special, 9 s
    [InlineData(517L, 6800L, 170320L, true)] // Battle Garegga 19 050, tir automatique
    [InlineData(2L, 20L, 2446L, false)]      // la demo de DaiOuJou
    [InlineData(0L, 0L, 600L, false)]        // aucun appui, la regle d'avant
    public void La_porte_des_appuis_sur_des_parties_mesurees(long appuis, long tenues, long images, bool joue)
    {
        Assert.Equal(joue, DepartReel.AppuisSuffisants(new DepartReel.Appuis(appuis, tenues), images));
    }

    [Fact]
    public void Les_appuis_se_lisent_au_proces_verbal_du_wrapper()
    {
        var a = DepartReel.DeLaSession("""{"press_count":2,"press_frames_sum":20,"impossible_inputs":0}""");
        Assert.Equal(2, a.Nombre);
        Assert.Equal(20, a.ImagesTenues);
        var absent = DepartReel.DeLaSession("""{"impossible_inputs":0}""");
        Assert.Null(absent.Nombre);
        Assert.Null(DepartReel.AppuisSuffisants(absent, 2446));
    }

    [Fact]
    public void Une_ligne_morte_ou_commentee_ne_declare_rien()
    {
        var (c, g) = DepartReel.LignesDeDepart("""
            -- { address=0X395B, type="u8", condition="change", action="CREDITS" },
            { address=0X10FDB6, type="u8", condition="eq", value=0X01, action="GAME_START", no_log=true },
            """);
        Assert.False(c);
        Assert.False(g);
    }
}
