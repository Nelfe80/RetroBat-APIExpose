using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE SCORE PROGRESSE, LE RACCOURCI DE SORTIE N'EST PAS UNE PARTIE (13/11). DoDonPachi DaiOuJou de Gogerad (API 1.9.173) :
/// 11 822 230 certifie et publie premier sur 1CC et 1LC, jeu lance puis quitte en 15,5 s (replay de 931 images), sans jouer.
/// Les chiffres des cas sont ceux des passeports de la borne et des classements publics (E:\claude_travail\temp\certification) :
/// les vraies parties a gros sauts passent, les lectures parasites et la partie de Gogerad simulee ne passent pas.
/// </summary>
public class ProgressionCertifieeTests
{
    private const string MemDaiOuJou = """
        collectibles = {
          { address=0X395B, type="u8", condition="change", action="CREDITS", desc="Inserted credits remaining" },
        },
        """;

    /// <summary>DaiOuJou au 13/11 : record public 1 999 820 ; gain appris 35 110 en une seconde (la vraie partie 554 380 de la borne).</summary>
    private static readonly ProgressionDuScore.Reperes DaiOuJou = new(35110, 1999820);

    /// <summary>La partie de Gogerad simulee : 931 images, un 0 a l'image 120, puis 11 822 230 a l'image 900.</summary>
    private static readonly List<(long frame, long total)> RunDeGogerad = new() { (120, 0), (900, 11822230) };

    private static List<(long frame, long total)> Montee(long debut, long fin, long images0, long images1, int n)
        => Enumerable.Range(0, n).Select(i => (images0 + i * (images1 - images0) / (n - 1), debut + i * (fin - debut) / (n - 1))).ToList();

    // ── La progression ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_saut_de_Gogerad_est_impossible()
    {
        var raison = ProgressionDuScore.Juger(RunDeGogerad, 11822230, 11822230, 931, DaiOuJou);
        Assert.NotNull(raison);
        Assert.Contains("record publie", raison);
    }

    [Fact]
    public void Sans_gain_appris_le_record_suffit_a_juger()
    {
        Assert.NotNull(ProgressionDuScore.Juger(RunDeGogerad, 11822230, 11822230, 931, new(null, 1999820)));
    }

    [Fact]
    public void Une_premiere_lecture_deja_haute_sans_montee_est_impossible()
    {
        // Ms. Pac-Man 906 030 (22/09) : une seule lecture, images non datees ; record public 47 050.
        Assert.NotNull(ProgressionDuScore.Juger(new List<(long, long)> { (0, 906030) }, 906030, 0, 0, new(6651, 47050)));
    }

    [Theory]
    // run (debut, sommet), plus grand pas, gain appris, record : les lectures parasites refusees jadis pour d'autres raisons.
    [InlineData(53408L, 15777936L, 15724528L, 6651L, 47050L)]          // Ms. Pac-Man 15 777 936 (21/09)
    [InlineData(4000000L, 10000000L, 6000000L, 0L, 224670L)]            // Shinobi 10 000 000 (15/09)
    [InlineData(1677723600L, 1677723700L, 1677706300L, 2769L, 976340L)] // Metal Slug 3 1 677 723 700 (15/09)
    public void Les_lectures_parasites_de_la_borne_sont_impossibles(long debut, long sommet, long pas, long gain, long record)
    {
        var run = new List<(long, long)> { (0, debut), (0, sommet) };
        Assert.NotNull(ProgressionDuScore.Juger(run, sommet, pas, 15000, new(gain > 0 ? gain : null, record)));
    }

    [Fact]
    public void Les_vraies_parties_a_gros_sauts_passent()
    {
        // 1942, 13 020 : +10 000 d'un coup (bonus de fin de niveau), 77 % du score. Gain appris SANS elle : 100 en une seconde.
        var mille9 = new List<(long, long)> { (3842, 2900), (3855, 2930), (3954, 2960), (3966, 2990), (3996, 3020), (4019, 13020) };
        Assert.Null(ProgressionDuScore.Juger(mille9, 13020, 10000, 4443, new(100, 219750)));
        // Alex Kidd, 4 400 : +4 255 en 10 images, 97 % du score. Gain appris sans elle : 1 060.
        Assert.Null(ProgressionDuScore.Juger(new List<(long, long)> { (10741, 145), (10751, 4400) }, 4400, 4255, 21308, new(1060, 75000)));
        // Bubble Bobble 1LC, 2 010 : +2 000 a la 40e seconde.
        Assert.Null(ProgressionDuScore.Juger(new List<(long, long)> { (2321, 10), (2383, 2010) }, 2010, 2000, 3730, new(2000, 2405570)));
        // Giga Wing, 41 959 290 : des sauts de 9 645 000.
        var giga = Montee(20, 32314290, 4969, 10147, 90).Append((10168L, 41959290L)).ToList();
        Assert.Null(ProgressionDuScore.Juger(giga, 41959290, 9645000, 10983, new(9645000, 9719610960)));
        // La vraie partie de DaiOuJou de la borne, 554 380, qui monte.
        Assert.Null(ProgressionDuScore.Juger(Montee(0, 554380, 2000, 9146, 300), 554380, 9140, 9146, DaiOuJou));
    }

    [Fact]
    public void La_demo_de_DaiOuJou_monte_et_n_est_pas_un_saut()
    {
        // Elle est refusee par le depart (2 appuis), jamais par la progression : la demo joue vraiment.
        Assert.Null(ProgressionDuScore.Juger(Montee(0, 2554070, 3109, 5555, 345), 2554070, 182010, 8484, DaiOuJou));
    }

    [Fact]
    public void Sans_aucune_reference_seul_le_saut_seul_de_la_premiere_minute_est_retenu()
    {
        var aucun = ProgressionDuScore.Reperes.Aucun;
        Assert.NotNull(ProgressionDuScore.Juger(new List<(long, long)> { (120, 0), (1200, 500000) }, 500000, 500000, 2400, aucun));
        // Une montee, meme rapide, n'est pas un saut seul.
        Assert.Null(ProgressionDuScore.Juger(Montee(0, 500000, 120, 1200, 10), 500000, 55556, 2400, aucun));
        // Apres la premiere minute : rien a redire sans reference.
        Assert.Null(ProgressionDuScore.Juger(new List<(long, long)> { (120, 0), (4000, 500000) }, 500000, 500000, 6000, aucun));
    }

    [Fact]
    public void Les_checkpoints_jugees_sont_ceux_du_passeport()
    {
        // La demo de DaiOuJou : 345 lectures, 87 checkpoints signes.
        Assert.Equal(87, ProgressionDuScore.IndicesSignes(345).Count);
        Assert.Equal(new[] { 0, 1, 2 }, ProgressionDuScore.IndicesSignes(3));
        var points = ProgressionDuScore.PointsSignes(new List<(long, long)> { (1, 10), (2, 20), (3, 30) }, 31);
        Assert.Equal((3L, 31L), points[^1]);
    }

    [Fact]
    public void Le_gain_en_une_seconde_vient_du_plus_grand_pas_ou_des_ecarts_dates()
    {
        Assert.Equal(10000, ProgressionDuScore.GainEnUneSeconde(new List<(long, long)> { (0, 0), (600, 1000) }, 10000));
        Assert.Equal(6000, ProgressionDuScore.GainEnUneSeconde(new List<(long, long)> { (60, 0), (660, 60000) }, 100));
    }

    [Fact]
    public void Les_reperes_viennent_de_la_reponse_de_profil()
    {
        var r = ProgressionDuScore.DuCorpsDuProfil("""{"ok":true,"open":true,"profiles":[],"progression":{"record":1999820,"gain_1s":35110}}""");
        Assert.Equal(1999820, r.Record);
        Assert.Equal(35110, r.GainEnUneSeconde);
        Assert.Equal(ProgressionDuScore.Reperes.Aucun, ProgressionDuScore.DuCorpsDuProfil("""{"ok":true,"open":true}"""));
        Assert.Equal(ProgressionDuScore.Reperes.Aucun, ProgressionDuScore.DuCorpsDuProfil("pas du json"));
        Assert.Null(ProgressionDuScore.DuCorpsDuProfil("""{"progression":{"record":null,"gain_1s":0}}""").Record);
    }

    // ── Les valeurs parasites du demarrage ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Une_premiere_lecture_haute_suivie_de_la_remise_a_zero_ne_compte_pas()
    {
        var traj = new List<(long, long)> { (100, 11822230), (300, 0), (400, 100), (500, 300), (600, 800) };
        var (reste, ecartees, raison) = ProgressionDuScore.SansParasiteDeDemarrage(traj, 3000, DaiOuJou);
        Assert.Equal(1, ecartees);
        Assert.Equal((300L, 0L), reste[0]);
        Assert.NotNull(raison);
        // Le meilleur run est alors la vraie partie.
        Assert.Equal(800, NelfePlayScoringReporter.SelectBestRun(reste)[^1].total);
    }

    [Fact]
    public void Un_saut_impossible_suivi_de_la_remise_a_zero_ne_compte_pas()
    {
        var traj = new List<(long, long)> { (100, 0), (150, 11822230), (300, 0), (400, 100), (500, 300) };
        var (reste, ecartees, _) = ProgressionDuScore.SansParasiteDeDemarrage(traj, 3000, DaiOuJou);
        Assert.Equal(2, ecartees);
        Assert.Equal(3, reste.Count);
    }

    [Fact]
    public void Une_vraie_premiere_partie_suivie_d_une_autre_reste()
    {
        var traj = Montee(0, 50000, 100, 3000, 40).Concat(Montee(0, 20000, 3100, 6000, 30)).ToList();
        var (reste, ecartees, _) = ProgressionDuScore.SansParasiteDeDemarrage(traj, 6000, DaiOuJou);
        Assert.Equal(0, ecartees);
        Assert.Equal(traj.Count, reste.Count);
    }

    [Fact]
    public void Sans_remise_a_zero_rien_n_est_ecarte()
    {
        // Rien ne dit ou commence la partie : la progression la jugera, le site tranchera.
        var (reste, ecartees, _) = ProgressionDuScore.SansParasiteDeDemarrage(RunDeGogerad.ToList(), 931, DaiOuJou);
        Assert.Equal(0, ecartees);
        Assert.Equal(2, reste.Count);
    }

    // ── Le depart : raccourci de sortie, plancher d'appuis, partie courte ────────────────────────────────────────────────

    [Fact]
    public void Le_raccourci_de_sortie_se_reconnait_a_la_fin_de_la_session()
    {
        var departs = new List<DepartDeJoueur> { new(1, 885) };
        Assert.Equal(new DepartReel.Raccourci(880, 885), DepartReel.RaccourciDeSortie(new long[] { 880 }, departs, 931));
        // Un START de depart, loin de la fin, n'est pas le raccourci.
        Assert.Null(DepartReel.RaccourciDeSortie(new long[] { 880 }, departs, 9000));
        // Un SELECT trop ancien (une piece mise une minute plus tot) non plus.
        Assert.Null(DepartReel.RaccourciDeSortie(new long[] { 100 }, departs, 931));
        // Sans SELECT ni hotkey lus : rien.
        Assert.Null(DepartReel.RaccourciDeSortie(Array.Empty<long>(), departs, 931));
    }

    [Fact]
    public void Les_appuis_du_raccourci_ne_comptent_pas()
    {
        var sans = new DepartReel.Appuis(2, 20).SansLeRaccourci(new DepartReel.Raccourci(880, 885), 931);
        Assert.Equal(0, sans.Nombre);
        Assert.Equal(0, sans.ImagesTenues);
        var vraie = new DepartReel.Appuis(498, 5644).SansLeRaccourci(new DepartReel.Raccourci(9100, 9105), 9146);
        Assert.Equal(496, vraie.Nombre);
        Assert.Equal(new DepartReel.Appuis(5, 9), new DepartReel.Appuis(5, 9).SansLeRaccourci(null, 931));
    }

    [Fact]
    public void Gogerad_simule_n_est_pas_certifie()
    {
        // La piece du SELECT (credit 1 a l'image 880), consommee par le START du raccourci (0 a l'image 885).
        var credits = new List<EvenementDeCredit> { new(1, 880), new(0, 885) };
        var departs = new List<DepartDeJoueur> { new(1, 885) };
        var raccourci = DepartReel.RaccourciDeSortie(new long[] { 880 }, departs, 931);
        Assert.NotNull(raccourci);
        var (c, g) = DepartReel.LignesDeDepart(MemDaiOuJou);
        var hausses = ProgressionDuScore.Hausses(RunDeGogerad);
        var bilan = new DepartReel.Bilan(c, g, DepartReel.CreditsSansLeRaccourci(credits, raccourci), DepartReel.DepartsSansLeRaccourci(departs, raccourci),
            false, false, new DepartReel.Appuis(2, 20).SansLeRaccourci(raccourci, 931), DepartReel.ImagesDuRun(RunDeGogerad), 931, hausses);
        var refus = DepartReel.Refus(bilan);
        Assert.NotNull(refus);
        Assert.Contains("presque aucun appui", refus);
        // Avant la 1.9.175, la meme partie passait les deux portes : la piece du raccourci valait credit consomme, 2 appuis suffisaient.
        Assert.True(DepartReel.CreditConsomme(credits, departs));
        Assert.Equal(2, DepartReel.AppuisRequis(DepartReel.ImagesDuRun(RunDeGogerad)));
    }

    [Fact]
    public void Gogerad_vu_par_une_borne_qui_ne_voit_pas_le_raccourci_reste_refuse()
    {
        // Le plancher : deux appuis ne suffisent jamais, meme tenus longtemps sur un run court.
        Assert.False(DepartReel.AppuisSuffisants(new DepartReel.Appuis(2, 400), 780));
        // Et la partie courte : 931 images, une hausse.
        Assert.NotNull(DepartReel.PartieTropCourte(931, ProgressionDuScore.Hausses(RunDeGogerad)));
    }

    [Theory]
    // images de session, hausses, refusee
    [InlineData(931L, 1, true)]     // Gogerad : 15,5 s, 0 puis 11 822 230
    [InlineData(1019L, 15, false)]  // Ms. Pac-Man 150 en 17 s : quinze hausses
    [InlineData(1799L, 2, true)]
    [InlineData(1800L, 1, false)]   // 30 s et plus : la progression et les appuis jugent
    [InlineData(0L, 0, false)]      // duree inconnue : on ne sait pas
    public void La_partie_courte_prouve_sa_progression(long images, int hausses, bool refusee)
    {
        Assert.Equal(refusee, DepartReel.PartieTropCourte(images, hausses) is not null);
    }

    [Fact]
    public void La_plus_courte_des_vraies_parties_publiees_passe_toujours()
    {
        // Ms. Pac-Man 150 en 17 s (fc 1019), 4 appuis, 152 images tenues, seize lectures de 0 a 150.
        var run = Enumerable.Range(0, 16).Select(i => ((long)(200 + i * 50), (long)i * 10)).ToList();
        var bilan = new DepartReel.Bilan(false, false, new List<EvenementDeCredit>(), new List<DepartDeJoueur>(), false, false,
            new DepartReel.Appuis(4, 152), DepartReel.ImagesDuRun(run), 1019, ProgressionDuScore.Hausses(run));
        Assert.Null(DepartReel.Refus(bilan));
    }

    [Fact]
    public void La_session_signe_les_appuis_du_raccourci()
    {
        var avec = NelfePlayScoringReporter.AvecLesAppuisDeSortie("""{"press_count":2,"frame_count":931}""", new DepartReel.Raccourci(880, 885));
        Assert.Contains("\"exit_presses\":2", avec);
        Assert.Equal("""{"press_count":2}""", NelfePlayScoringReporter.AvecLesAppuisDeSortie("""{"press_count":2}""", null));
        Assert.Equal(931, NelfePlayScoringReporter.ImagesDeLaSession("""{"frame_count":931}"""));
        Assert.Equal(0, NelfePlayScoringReporter.ImagesDeLaSession("pas du json"));
    }
}
