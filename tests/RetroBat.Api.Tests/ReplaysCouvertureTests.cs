using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Recording;
using RetroBat.Api.Scoring;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES REPLAYS D'OLD AF GAMER (16/11). GigaWing : l'enregistrement « s'arrete et se relance trois fois », le record de
/// 58,4 milliards part avec 33 s prises au debut d'un run de 6 min, le 1LC de Progear avec un replay qui commence apres la
/// premiere vie perdue. Trois correctifs : un score ne part qu'avec un replay qui couvre son run ; seule une vraie remise a
/// zero arrete le replay ; un voyant dit au joueur quand il enregistre.
/// </summary>
public sealed class ReplaysCouvertureTests
{
    // ─────────────────────────── 1. Un replay qui couvre le run ───────────────────────────

    private static List<(long frame, long total)> Run(params (long, long)[] points) => points.ToList();

    [Fact]
    public void Le_record_GigaWing_du_14_11_part_sans_replay()
    {
        // Run de l'image 2391 a 24325 ; l'enregistrement du debut s'arrete 33 s apres (1949 images).
        var run = Run((2391, 10), (2500, 50_000), (12_000, 9_000_000_000), (24_325, 58_365_996_240));
        var enregistrements = new List<(string, long, long?)> { ("rp_debut", 2380, 2380 + 1949) };
        Assert.Null(CouvertureDuReplay.DuRun(enregistrements, run));
        // L'ancienne regle (le pic, sinon le dernier replay ouvert) le rattachait quand meme : plus de repli.
        Assert.Null(NelfePlayScoringReporter.ReplayDuMeilleurRun(enregistrements, run));
    }

    [Fact]
    public void Le_bout_de_11_s_a_la_fin_du_run_du_15_11_ne_le_couvre_pas()
    {
        var run = Run((1908, 10), (2000, 40_000), (7047, 4_566_598_870));
        var enregistrements = new List<(string, long, long?)> { ("rp_1", 1900, 2600), ("rp_2", 2650, 4000), ("rp_fin", 6404, 7047 + 300) };
        Assert.Null(CouvertureDuReplay.DuRun(enregistrements, run));
        // L'ancienne regle prenait le bout qui contient le pic.
        Assert.Equal("rp_fin", NelfePlayScoringReporter.ReplayDuMeilleurRun(enregistrements, run));
    }

    [Fact]
    public void Un_enregistrement_du_depart_au_pic_couvre_le_run()
    {
        var run = Run((1000, 0), (1100, 10), (9000, 25_700));
        Assert.Equal("rp", CouvertureDuReplay.DuRun(new List<(string, long, long?)> { ("rp", 990, 9100) }, run));
        // Encore ouvert : sa fin est inconnue, il couvre tout ce qui suit son debut.
        Assert.Equal("rp", CouvertureDuReplay.DuRun(new List<(string, long, long?)> { ("rp", 990, null) }, run));
    }

    [Fact]
    public void Le_depart_est_la_premiere_montee_avec_cinq_secondes_de_marge()
    {
        // Le score pose au START (image 100) precede les premiers points (image 1100) : le replay parti au score, a 1300, couvre.
        var run = Run((100, 0), (1100, 10), (5000, 5000));
        Assert.Equal(1100, CouvertureDuReplay.DepartDuRun(run));
        Assert.Equal("rp", CouvertureDuReplay.DuRun(new List<(string, long, long?)> { ("rp", 1100 + CouvertureDuReplay.ToleranceDuDepart, null) }, run));
        Assert.Null(CouvertureDuReplay.DuRun(new List<(string, long, long?)> { ("rp", 1101 + CouvertureDuReplay.ToleranceDuDepart, null) }, run));
        // Un run sans montee : sa premiere lecture.
        Assert.Equal(7, CouvertureDuReplay.DepartDuRun(Run((7, 5), (9, 5))));
        Assert.Null(CouvertureDuReplay.DepartDuRun(Run()));
    }

    [Fact]
    public void Le_dernier_enregistrement_qui_couvre_est_choisi()
    {
        var run = Run((100, 0), (200, 10), (900, 900));
        var enregistrements = new List<(string, long, long?)> { ("rp_a", 50, 1000), ("rp_b", 60, null) };
        Assert.Equal("rp_b", CouvertureDuReplay.DuRun(enregistrements, run));
    }

    [Fact]
    public void Le_1LC_de_Progear_part_sans_le_replay_qui_commence_apres_la_vie_perdue()
    {
        // Run 3913..11073, premiere vie perdue a 6692, replay ouvert vers 9400.
        var run1lc = Run((3913, 0), (3950, 100), (6650, 65_280));
        var enregistrements = new List<(string, long, long?)> { ("rp_tard", 9400, 12_700) };
        Assert.Null(CouvertureDuReplay.DeLaPremiereVie(enregistrements, run1lc, 6692));
    }

    [Fact]
    public void Le_1LC_garde_un_replay_arrete_apres_sa_vie_perdue_meme_si_le_1CC_n_en_a_pas()
    {
        var run = Run((1000, 0), (1100, 10), (4000, 20_000), (9000, 90_000));
        var run1lc = Run((1000, 0), (1100, 10), (4000, 20_000));
        var enregistrements = new List<(string, long, long?)> { ("rp", 1000, 5000) };
        Assert.Null(CouvertureDuReplay.DuRun(enregistrements, run));
        Assert.Equal("rp", CouvertureDuReplay.DeLaPremiereVie(enregistrements, run1lc, 4200));
        // Aucune vie perdue : jusqu'au pic du 1LC.
        Assert.Equal("rp", CouvertureDuReplay.DeLaPremiereVie(enregistrements, run1lc, null));
        Assert.Null(CouvertureDuReplay.DeLaPremiereVie(enregistrements, run1lc, 5200));
    }

    // ─────────────────────────── 2. Seule une vraie remise a zero arrete le replay ───────────────────────────

    private static readonly DateTime T0 = new(2026, 11, 14, 11, 14, 0, DateTimeKind.Utc);

    private static List<long> Arrets(long depart, bool finDePartie, params (int Ms, long Total)[] lectures)
    {
        var salves = new SalvesDeScore();
        var baisse = new ArretALaBaisse();
        long? precedent = null;
        var arrets = new List<long>();
        foreach (var (ms, total) in lectures)
        {
            var (reference, nouvelle) = salves.Lire(precedent, T0.AddMilliseconds(ms));
            if (baisse.Lire(total, reference, nouvelle, enregistrement: true, monteeVue: true, depart, finDePartie)) arrets.Add(total);
            precedent = total;
        }
        return arrets;
    }

    [Fact]
    public void La_retenue_de_GigaWing_lue_avec_un_mot_ancien_n_arrete_plus_le_replay()
    {
        // Banc du 16/11 (rp_06GXKGW1, image 1876) : 0x70CE passe de 2 a 3 ; lu encore a 2, le total tombe a 208 259 050.
        // Le mot ancien tient sur deux salves (l'ancienne regle confirmait et arretait).
        Assert.Empty(Arrets(0, false, (0, 282_485_120), (100, 208_259_050), (200, 209_000_000), (300, 309_500_000)));
    }

    [Fact]
    public void La_premiere_retenue_dans_le_mot_des_centaines_de_millions_n_arrete_rien()
    {
        // NeoGeoff, 14/11 02:13 : a la premiere retenue dans 0x70CE, le total faux ne garde que les chiffres du bas.
        Assert.Empty(Arrets(0, false, (0, 99_950_000), (100, 1_157_820), (200, 1_208_680), (300, 101_300_000)));
    }

    [Fact]
    public void Une_vraie_remise_a_zero_arrete_toujours_le_replay()
    {
        Assert.Equal(new long[] { 10 }, Arrets(0, false, (0, 309_500_000), (100, 0), (5000, 10), (6000, 20)));
        Assert.Equal(new long[] { 100 }, Arrets(0, false, (0, 12000), (1000, 25000), (5000, 0), (30000, 100), (31000, 200)));
    }

    [Fact]
    public void Apres_un_GAME_OVER_toute_baisse_confirmee_arrete()
    {
        Assert.Equal(new long[] { 200_000_000 }, Arrets(0, true, (0, 282_485_120), (100, 208_259_050), (200, 200_000_000)));
    }

    [Fact]
    public void La_remise_a_zero_se_mesure_au_depart_du_run_ou_a_un_millieme()
    {
        Assert.True(ArretALaBaisse.EstUneRemiseAZero(0, 1000, 0));
        Assert.True(ArretALaBaisse.EstUneRemiseAZero(282_485, 282_485_120, 0));
        Assert.False(ArretALaBaisse.EstUneRemiseAZero(282_486, 282_485_120, 0));
        // Le score de depart du run (une partie qui commence a 500) : y revenir est une remise a zero.
        Assert.True(ArretALaBaisse.EstUneRemiseAZero(500, 80_000, 500));
        // Une valeur de depart haute (enregistrement adopte en cours de run) ne vaut que jusqu'a la moitie du score d'avant.
        Assert.False(ArretALaBaisse.EstUneRemiseAZero(208_259_050, 282_485_120, 250_000_000));
        Assert.False(ArretALaBaisse.EstUneRemiseAZero(1000, 1000, 0));
    }

    /// <summary>
    /// LE CAS GIGAWING DE BOUT EN BOUT, dans le rapporteur : un enregistreur factice ouvre un replay a chaque depart et
    /// l'arrete a chaque « scoring.replay.stop ». Sur une borne dont l'API ne lit pas les manettes (le depart au score est
    /// arme), trois fausses baisses suivies de remontees donnaient trois arrets et trois relances : un seul enregistrement
    /// maintenant. Une vraie remise a zero arrete toujours.
    /// </summary>
    [Fact]
    public async Task GigaWing_une_fausse_baisse_puis_la_remontee_laissent_un_seul_enregistrement()
    {
        var bus = new SimpleEventBus();
        var reporter = new NelfePlayScoringReporter(bus, new SansReseau(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance));
        var ouverts = new List<string>();
        var arrets = 0;
        string? enCours = null;
        void Ouvrir()
        {
            enCours = "rp_" + (ouverts.Count + 1);
            ouverts.Add(enCours);
            reporter.HandleEvent(new EventEnvelope { Type = "replay.recording.started", Payload = new { ReplayId = enCours, Frame = 0L } });
        }
        using var abonnement = bus.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type == "scoring.partie.depart" && enCours is null) Ouvrir();
            else if (e.Type == "scoring.replay.stop" && enCours is { } id)
            {
                arrets++;
                enCours = null;
                // Pas de « replay.finalized » ici : le rapporteur ecrirait ses liens dans l'etat de la borne.
                reporter.HandleEvent(new EventEnvelope { Type = "replay.recording.stopped", Payload = new { ReplayId = id, LastFrame = 0L } });
            }
        });

        async Task Score(long total, int pauseMs = 80)
        {
            reporter.HandleEvent(new EventEnvelope { Type = "score.live.changed", Payload = new { Score = total, Player = 1 } });
            await Task.Delay(pauseMs);
        }

        // Le premier point lance l'enregistrement (depart au score, manettes non lues).
        await Score(0);
        await Score(10);
        Assert.Single(ouverts);
        await Score(50_000);
        await Score(99_950_000);
        // Trois fausses baisses (mot de poids fort encore ancien) sur deux salves chacune, puis la remontee.
        await Score(1_157_820); await Score(1_208_680); await Score(101_300_000);
        await Score(282_485_120);
        await Score(208_259_050); await Score(209_000_000); await Score(309_500_000);
        await Score(399_000_000);
        await Score(304_719_740); await Score(305_000_000); await Score(405_000_000);
        Assert.Equal(0, arrets);
        Assert.Single(ouverts);

        // Une nouvelle partie : le score retombe a zero et le replay s'arrete, une fois (la relance de la partie suivante
        // attend le scellement du replay arrete, « replay.finalized », que ce banc ne joue pas : DepartParLeScoreTests).
        await Score(0); await Score(10); await Score(30); await Score(500);
        Assert.Equal(1, arrets);
        Assert.Single(ouverts);
    }

    private sealed class SansReseau : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SiteMuet());
    }

    private sealed class SiteMuet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("pas de reseau dans les tests");
    }

    // ─────────────────────────── 3. Le voyant du replay ───────────────────────────

    private sealed class Voyant
    {
        public readonly List<VoyantDuReplay.Annonce> Montrees = new();
        public readonly List<string> Es = new();
        public bool? Gameplay = true;
        public string Langue = "fr";
        public readonly VoyantDuReplay Service;

        public Voyant()
        {
            Service = new VoyantDuReplay(new SimpleEventBus(), null, a => Montrees.Add(a), t => { Es.Add(t); return Task.CompletedTask; },
                () => Gameplay, () => Langue);
        }

        public void Evenement(string type, object? payload = null)
            => Service.SurEvenement(new EventEnvelope { Type = type, Payload = payload ?? new { } });
    }

    [Fact]
    public void Le_debut_de_l_enregistrement_s_annonce_par_un_bandeau_court()
    {
        var v = new Voyant();
        v.Evenement("replay.recording.started", new { ReplayId = "rp_1", Speedrun = false, Categorie = (string?)null });
        var a = Assert.Single(v.Montrees);
        Assert.Equal("replay_rec", a.Cle);
        Assert.Equal(CabinetAnnounceText.Get("replay_rec_title", "fr"), a.Titre);
        Assert.False(a.Alerte);
        Assert.Equal("replay", a.AuDeck.Source);
        Assert.Equal("REC", a.AuDeck.Title);
    }

    [Fact]
    public void Ni_les_epreuves_ni_le_1CC_MULTI_n_ont_de_bandeau_de_depart()
    {
        var v = new Voyant();
        v.Evenement("replay.recording.started", new { ReplayId = "rp_1", Speedrun = true });
        v.Evenement("replay.recording.started", new { ReplayId = "rp_2", Speedrun = false, Categorie = "1cc-multi" });
        Assert.Empty(v.Montrees);
    }

    [Fact]
    public void Un_replay_interrompu_s_annonce_au_premier_temps_mort_jamais_en_jeu()
    {
        var v = new Voyant { Gameplay = true };
        v.Evenement("replay.recording.stopped", new { ReplayId = "rp_1", Raison = VoyantDuReplay.RaisonInterrompu });
        Assert.Empty(v.Montrees);
        v.Gameplay = null;   // le .MEM ne dit rien : silence
        v.Service.Relever();
        Assert.Empty(v.Montrees);
        v.Gameplay = false;  // continue, game over
        v.Service.Relever();
        var a = Assert.Single(v.Montrees);
        Assert.Equal("replay_stopped", a.Cle);
        Assert.True(a.Alerte);
        v.Service.Relever();
        Assert.Single(v.Montrees);
        v.Evenement("ui.game.ended");
        Assert.Empty(v.Es);
    }

    [Fact]
    public void Sans_temps_mort_le_joueur_l_apprend_au_menu_par_EmulationStation()
    {
        var v = new Voyant { Gameplay = true, Langue = "en" };
        v.Evenement("replay.recording.stopped", new { ReplayId = "rp_1", Raison = VoyantDuReplay.RaisonInterrompu });
        v.Evenement("ui.game.ended");
        var texte = Assert.Single(v.Es);
        Assert.StartsWith(CabinetAnnounceText.Get("replay_stopped_title", "en"), texte);
        Assert.Empty(v.Montrees);
    }

    [Fact]
    public void Les_arrets_normaux_ne_disent_rien()
    {
        var v = new Voyant { Gameplay = false };
        foreach (var raison in new[] { VoyantDuReplay.RaisonFin, VoyantDuReplay.RaisonContinue, VoyantDuReplay.RaisonNouvellePartie, VoyantDuReplay.RaisonRelance })
            v.Evenement("replay.recording.stopped", new { ReplayId = "rp_1", Raison = raison });
        v.Evenement("replay.recording.stopped", new { ReplayId = "rp_2" });
        v.Service.Relever();
        v.Evenement("ui.game.ended");
        Assert.Empty(v.Montrees);
        Assert.Empty(v.Es);
    }

    [Fact]
    public void Un_jeu_sans_replay_le_dit_une_fois_avec_son_motif()
    {
        var v = new Voyant();
        var le = new DateTime(2026, 10, 4, 9, 12, 3, DateTimeKind.Utc);
        v.Evenement("replay.recording.skipped", new { System = "fbneo", Game = "19xx", Motif = VoyantDuReplay.MotifPlantage, Le = le });
        v.Evenement("ui.game.started");
        v.Evenement("replay.recording.skipped", new { System = "fbneo", Game = "19xx", Motif = VoyantDuReplay.MotifPlantage, Le = le });
        var a = Assert.Single(v.Montrees);
        Assert.Equal("replay_off", a.Cle);
        Assert.Contains(le.ToLocalTime().ToString("yyyy-MM-dd"), a.Detail);
        Assert.Equal("RETROARCH CRASHED RECORDING IT", a.AuDeck.Text);

        v.Evenement("replay.recording.skipped", new { System = "mame", Game = "tgm2", Motif = VoyantDuReplay.MotifRetroArch });
        Assert.Equal(2, v.Montrees.Count);
        Assert.Equal(CabinetAnnounceText.Get("replay_off_retroarch_sub", "fr"), v.Montrees[1].Detail);
    }

    [Fact]
    public void Un_jeu_sans_replay_apres_un_plantage_dit_la_date_du_prochain_essai()
    {
        // 16/11 : l'inscription expire ; l'enregistreur publie la date du prochain essai (Jusqua) avec le motif.
        var v = new Voyant();
        var le = new DateTime(2026, 11, 15, 21, 4, 0, DateTimeKind.Utc);
        var jusqua = le.AddDays(7);
        v.Evenement("replay.recording.skipped", new { System = "fbneo", Game = "19xx", Motif = VoyantDuReplay.MotifPlantage, Le = le, Jusqua = jusqua });
        var a = Assert.Single(v.Montrees);
        Assert.Equal(string.Format(CabinetAnnounceText.Get("replay_off_crash_retry_sub", "fr"),
            le.ToLocalTime().ToString("yyyy-MM-dd"), jusqua.ToLocalTime().ToString("yyyy-MM-dd")), a.Detail);
    }

    [Theory]
    [InlineData(true, "gigawing", true)]
    [InlineData(false, "gigawing", false)]
    [InlineData(true, "autre", false)]
    [InlineData(null, null, false)]
    public void Interrompu_veut_le_meme_jeu_charge_et_RetroArch_qui_n_enregistre_plus(bool? charge, string? jeu, bool attendu)
        => Assert.Equal(attendu, VoyantDuReplay.Interrompu(charge, jeu, "gigawing", false));

    [Fact]
    public void Un_jeu_qui_s_enregistre_encore_n_est_pas_interrompu()
        => Assert.False(VoyantDuReplay.Interrompu(true, "gigawing", "gigawing", true));

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    public void Les_textes_du_voyant_existent_dans_les_six_langues(string langue)
    {
        foreach (var cle in new[] { "replay_rec_title", "replay_rec_sub", "replay_stopped_title", "replay_stopped_sub",
                     "replay_off_title", "replay_off_crash_sub", "replay_off_retroarch_sub" })
        {
            var texte = CabinetAnnounceText.Get(cle, langue);
            Assert.NotEqual(cle, texte);
            if (langue != "en") Assert.NotEqual(CabinetAnnounceText.Get(cle, "en"), texte);
        }
        Assert.Contains("{0}", CabinetAnnounceText.Get("replay_off_crash_sub", langue));
    }
}
