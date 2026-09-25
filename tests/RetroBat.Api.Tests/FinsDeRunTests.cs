using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un joueur a vu certifier en 1CC un score obtenu AVEC un continue, sur 19xx et sur Altered
/// Beast. Le découpage ne connaissait que les chutes de score, et un continue d'arcade conserve le
/// score : rien ne se coupait. Les suites de valeurs ci-dessous sont celles mesurées sur borne le
/// 24 septembre 2026.
/// </summary>
public class FinsDeRunTests
{
    private static EvenementDeVie Perte(string a, int? v, long frame, int joueur = 1)
        => new(a, true, v, frame, joueur);

    private static EvenementDeVie Gain(string a, int v, long frame, int joueur = 1)
        => new(a, false, v, frame, joueur);

    [Fact]
    public void Sans_continue_rien_ne_se_coupe()
    {
        // Ms. Pac-Man, compteur interne : 3 vies, trois morts, la dernière à la frame 5961, et
        // aucun nouveau crédit. La partie entière est le run.
        var fins = FinsDeRun.Calculer(
        [
            Perte("0x614", 2, 3672),
            Perte("0x614", 1, 4663),
            Perte("0x614", 0, 5961),
        ]);

        Assert.Empty(fins);
    }

    [Fact]
    public void Dix_neuf_xx_mesure_la_coupe_tombe_au_continue_derniere_vie_comprise()
    {
        // 19xx, sonde du 2026-09-25 21:17 : le compteur compte les vies EN RÉSERVE. 2 → 1 (1re mort),
        // 1 → 0 (2e mort : la DERNIÈRE vie commence), puis 2 au continue. Couper à zéro certifiait
        // 5 300 ; le 1CC juste est 8 700, la fin de la dernière vie.
        var fins = FinsDeRun.Calculer(
        [
            Perte("0xFF82EC", 1, 2100),
            Perte("0xFF82EC", 0, 2348),
            Perte("0xFF82EC", 2, 3400),
        ]);
        Assert.Equal([3400L], fins);

        var traj = new List<(long frame, long total)>
        {
            (1500, 100), (2000, 4900), (2300, 5300), (2360, 5400), (2365, 8100), (2500, 8700),
            (3402, 10901), (3600, 13301),
        };
        Assert.Equal(8700, NelfePlayScoringReporter.SelectBestRun(traj, fins)[^1].total);
    }

    [Fact]
    public void Dix_neuf_xx_sans_continue_la_partie_entiere_compte()
    {
        // La partie de 26 500 : deux morts lues, la troisième ne fait plus bouger la réserve, et
        // aucun crédit ensuite. Rien ne se coupe.
        Assert.Empty(FinsDeRun.Calculer([Perte("0xFF82EC", 1, 2845), Perte("0xFF82EC", 0, 3400)]));
    }

    [Fact]
    public void Altered_Beast_mesure_le_drapeau_de_boss_n_est_pas_un_compteur_de_vies()
    {
        // Sonde du 2026-09-25 21:14 : le bloc des vies d'Altered Beast range aussi « FLAG BOSS DEATH »
        // (0x315D), qui descend 14 fois d'un cran mais remonte sans cesse. Les vraies vies (0xFFE018)
        // font 2, 1, 0 puis, au continue, remontent a 2. C'est leur continue qui doit couper.
        var boss = new[] { 1, 2, 1, 2, 3, 4, 5, 4, 3, 2, 1, 3, 2, 3, 1, 2, 3, 2, 1, 0, 2, 4, 6 };
        var evenements = new List<EvenementDeVie>();
        for (var i = 0; i < boss.Length; i++) evenements.Add(Perte("0x315D", boss[i], 100 + i * 10));
        evenements.Add(Perte("0xFFE018", 1, 400));
        evenements.Add(Perte("0xFFE018", 0, 500));
        evenements.Add(Gain("0xFFE018", 2, 900));

        Assert.Equal([900L], FinsDeRun.Calculer(evenements.OrderBy(e => e.Frame).ToList()));
    }

    [Fact]
    public void Un_1up_sur_la_derniere_vie_n_est_pas_un_continue()
    {
        // Parti de 2 : un extend sur la dernière vie ne remonte qu'à 1, sous le niveau du départ.
        Assert.Empty(FinsDeRun.Calculer(
        [
            Perte("0xFF82EC", 1, 100), Perte("0xFF82EC", 0, 200), Gain("0xFF82EC", 1, 300),
        ]));
    }

    [Fact]
    public void Le_compteur_AFFICHE_ne_tronque_pas_la_derniere_vie()
    {
        // LE PIEGE. Sur Ms. Pac-Man l'affiche tombe a zero alors qu'il reste une vie a jouer :
        // couper la volerait au joueur le score de sa derniere vie. L'interne descend d'un pas de
        // plus, donc c'est lui qu'on retient -- la meme regle que l'audit 1LC.
        var fins = FinsDeRun.Calculer(
        [
            Perte("0x615", 1, 3672), Perte("0x614", 2, 3672),
            Perte("0x615", 0, 4663), Perte("0x614", 1, 4663),
            Perte("0x614", 0, 5961),
        ]);

        // Aucun continue : aucune coupe, et surtout pas sur le compteur affiché.
        Assert.Empty(fins);
    }

    [Fact]
    public void Un_continue_ouvre_un_second_run()
    {
        // 19xx : une vie, mort a la frame 900, continue, mort a nouveau a 2400. Deux fins, donc
        // deux runs -- c'est ce qui manquait pour que 1CC veuille dire quelque chose.
        var fins = FinsDeRun.Calculer(
        [
            Gain("0xFF82EC", 1, 100),
            Perte("0xFF82EC", 0, 900),
            Gain("0xFF82EC", 1, 1500),
            Perte("0xFF82EC", 0, 2400),
        ]);

        // La coupe tombe au continue (1500) ; la seconde mort n'est suivie d'aucun crédit.
        Assert.Equal([1500L], fins);
    }

    [Fact]
    public void Une_jauge_d_energie_ne_borne_aucun_run()
    {
        // Altered Beast : 29 declenchements en une partie, par pas de quatre. Prise pour un
        // compteur de vies, elle aurait coupe le run a la premiere barre perdue.
        var fins = FinsDeRun.Calculer(
        [
            Perte("0x4B", 44, 100), Perte("0x4B", 40, 120), Perte("0x4B", 36, 140),
            Perte("0x4B", 4, 300), Perte("0x4B", 0, 320),
        ]);

        Assert.Empty(fins);
    }

    [Fact]
    public void Un_drapeau_ne_borne_aucun_run()
    {
        // « Dead » garde sa valeur : 10, 10 sur Ms. Pac-Man ; 0, 0 sur Double Dragon.
        Assert.Empty(FinsDeRun.Calculer([Perte("0x604", 10, 100), Perte("0x604", 10, 200)]));
        Assert.Empty(FinsDeRun.Calculer([Perte("0x3C1", 0, 100), Perte("0x3C1", 0, 200)]));
    }

    [Fact]
    public void Un_reglage_DIP_ne_borne_aucun_run()
    {
        // 1942 : une valeur isolee, aucune suite.
        Assert.Empty(FinsDeRun.Calculer([Perte("0x181", 0, 500)]));
    }

    [Fact]
    public void Les_vies_d_un_autre_joueur_ne_bornent_pas_ce_run()
    {
        var fins = FinsDeRun.Calculer(
        [
            Perte("0x3EA", 1, 100), Perte("0x3EA", 0, 900), Gain("0x3EA", 2, 1000),
            Perte("0x448", 1, 200, joueur: 2), Perte("0x448", 0, 400, joueur: 2), Gain("0x448", 2, 450, joueur: 2),
        ]);

        Assert.Equal([1000L], fins);
    }

    [Fact]
    public void Un_1up_ne_termine_rien_et_ne_casse_pas_le_compte()
    {
        // La vie gagnee remonte le compteur sans qu'aucune mort n'ait eu lieu : elle n'ouvre pas
        // de run, et la descente reprend ensuite normalement.
        var fins = FinsDeRun.Calculer(
        [
            Perte("0x614", 2, 100),
            Gain("0x614", 3, 500),
            Perte("0x614", 2, 900), Perte("0x614", 1, 1200), Perte("0x614", 0, 1500),
            Gain("0x614", 3, 2000),
        ]);

        // Le 1-up d'avant zéro ne coupe rien ; le continue d'après zéro coupe, à son moment.
        Assert.Equal([2000L], fins);
    }

    [Fact]
    public void Sans_compteur_credible_on_ne_coupe_rien()
    {
        // Mieux vaut ne pas decouper que decouper au hasard : un run tronque a tort vole un
        // record, et c'est pire que de laisser passer un continue.
        Assert.Empty(FinsDeRun.Calculer([]));
        Assert.Empty(FinsDeRun.Calculer([Perte("0x1", null, 100), Perte("0x1", null, 200)]));
    }

    [Fact]
    public void Une_partie_sans_mort_n_a_aucune_fin_de_run()
    {
        Assert.Empty(FinsDeRun.Calculer([Gain("0x614", 3, 100), Gain("0x614", 4, 800)]));
    }
    // ── Le decoupage lui-meme : c'est lui qui decide du score certifie ──

    private static List<(long frame, long total)> Traj(params (long, long)[] pts) => [.. pts];

    [Fact]
    public void Le_score_certifie_s_arrete_a_la_derniere_vie_perdue()
    {
        // LE CAS SIGNALE. Le joueur monte a 5000, perd sa derniere vie, continue, et monte a
        // 12000. Le score conserve par le continue faisait passer les deux pour un seul run :
        // 12000 etait certifie 1CC. Le run s'arrete desormais a 5000.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((100, 1000), (200, 3000), (300, 5000), (400, 8000), (500, 12000)),
            [300L]);

        Assert.Equal(5000, meilleur[^1].total);
    }

    [Fact]
    public void Sans_fin_de_run_le_decoupage_ne_change_pas()
    {
        // La regression a ne pas commettre : tous les jeux dont les vies ne sont pas lisibles
        // doivent continuer a se comporter exactement comme avant.
        var traj = Traj((100, 1000), (200, 3000), (300, 5000));
        Assert.Equal(
            RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(traj),
            RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(traj, []));
    }

    [Fact]
    public void Le_meilleur_des_deux_runs_est_retenu()
    {
        // Le premier run vaut mieux que le second : on garde le premier. « Le meilleur run,
        // jamais le dernier » -- un mauvais essai qui suit ne doit pas voler le record.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((100, 9000), (200, 9000), (300, 2000), (400, 4000)),
            [200L]);

        Assert.Equal(9000, meilleur[^1].total);
    }

    // ── Les trames REELLES : la mort ne tombe presque jamais sur une lecture de score ──

    [Fact]
    public void La_mort_entre_deux_lectures_coupe_quand_meme()
    {
        // LE CAS DE PRODUCTION, que la premiere version ratait. Le wrapper transmet la trame de la
        // mort (455) ; les lectures de score portent celle du dernier changement de score (400,
        // 600). Elles ne coincident pas. La premiere version exigeait l'egalite et ne coupait
        // donc rien : le 12000 du continue etait certifie 1CC comme avant le correctif.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((100, 1000), (250, 3000), (400, 5000), (600, 8000), (700, 12000)),
            [455L]);

        Assert.Equal(5000, meilleur[^1].total);
    }

    [Fact]
    public void Sur_le_pont_Lua_tout_a_la_trame_zero_ne_coupe_rien()
    {
        // MAME autonome : aucun evenement de trame ne circule, lectures et morts valent toutes 0.
        // Aucune mort ne tombe « entre » deux lectures de meme trame : le decoupage ordinaire
        // s'applique, comme avant la 1.9.0, et le score final est garde.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((0, 100), (0, 800), (0, 2500), (0, 7300)),
            [0L, 0L, 0L]);

        Assert.Equal(7300, meilleur[^1].total);
        Assert.Equal(4, meilleur.Count);
    }

    [Fact]
    public void Sur_le_pont_Lua_une_nouvelle_partie_se_voit_toujours_a_la_chute()
    {
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((0, 100), (0, 900), (0, 4200), (0, 20), (0, 1500)),
            [0L, 0L]);

        Assert.Equal(4200, meilleur[^1].total);
    }

    [Fact]
    public void Une_mort_apres_la_derniere_lecture_ne_change_rien()
    {
        // Game over final : la partie s'arrete, il n'y a rien apres a separer.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((100, 1000), (200, 3000), (300, 5000)),
            [950L]);

        Assert.Equal(5000, meilleur[^1].total);
    }

    [Fact]
    public void Une_mort_avant_toute_lecture_ne_change_rien()
    {
        // Une vie perdue pendant l'attract ou avant le premier point : aucun run a fermer.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((100, 1000), (200, 3000), (300, 5000)),
            [40L]);

        Assert.Equal(5000, meilleur[^1].total);
    }

    [Fact]
    public void Deux_continues_trois_runs_seul_le_premier_compte()
    {
        // 19xx a une vie : mort a 480, continue, mort a 910, continue. Le score reporte monte a
        // chaque fois ; seuls les points gagnes avant la premiere mort sont a lui.
        var meilleur = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.SelectBestRun(
            Traj((100, 2000), (300, 6000), (450, 9000), (600, 12000), (800, 20000), (1000, 26000)),
            [480L, 910L]);

        Assert.Equal(9000, meilleur[^1].total);
    }
}
