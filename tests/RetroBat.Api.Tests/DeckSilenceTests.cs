using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DECK POSE FAIT TAIRE CE QUI PASSE DEVANT LUI (demande du user du 08/11, apres la 1.9.163) : les doublons du Deck ne
/// s'affichent pas (cartouche du defi), les messages utiles vont a sa plaque (deck.notice), les notifications de RetroArch sont
/// coupees pour ce lancement seulement (-OnScreenMsg 0). Sans Deck, rien ne change.
/// </summary>
public sealed class DeckSilenceTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "deck-silence-" + Guid.NewGuid().ToString("N"));

    public DeckSilenceTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private static readonly DeckDecor.Trou Trou = new(476, 58, 1444, 1083);

    private static DeckLancement Lancement(string status, bool notices, DeckDecor.Trou? trou = null)
        => new(DeckModes.WorldScoring, "fbneo", "mslug3", "metal-slug-3", "arcade/metal-slug-3", "player", @"E:\r\mslug3.zip", status,
            status == "posed" ? "metal-slug-3-pixel" : null, "REBEL IRON", true, "official-default", ["metal-slug-3-pixel", "off"],
            status == "posed" ? new DeckDecorPose("nelfeplay-deck", 2, new DeckEcran(1920, 1200), trou ?? Trou, trou ?? Trou, "x.png", false, 40) : null,
            null, DateTime.UtcNow)
        {
            Notices = notices,
            RetroArchMuet = notices && status == "posed",
        };

    private static DeckGame Jeu(string launchId, string etat, DeckLancement? deck)
        => new(launchId, etat, "fbneo", "arcade", "mslug3", "metal-slug-3", "mem", "Metal Slug 3", @"E:\r\mslug3.zip", "libretro", "fbneo", null, null,
            DateTime.UtcNow, etat == "ended" ? DateTime.UtcNow : null, null, null, null, 1, deck);

    private static DeckChoixPublie Choix(bool off, string now)
        => new(DeckModes.WorldScoring, "arcade/metal-slug-3", "player", off ? null : "metal-slug-3-pixel", off ? null : "REBEL IRON", off, now,
            "metal-slug-3-pixel", "", "gamepad", ["metal-slug-3-pixel", "off"]);

    // ─────────────────────────── la silence : qui se tait, et quand ───────────────────────────

    [Fact]
    public void Deck_pose_les_doublons_se_taisent_et_les_messages_vont_au_Deck()
    {
        var silence = new DeckSilence(new SimpleEventBus());
        var changements = 0;
        silence.Change += () => changements++;

        silence.Poser(Jeu("l1", "launching", Lancement("posed", notices: true)));
        Assert.True(silence.DeckVisible);
        Assert.True(silence.AuDeck);
        Assert.True(silence.RetroArchMuet);
        Assert.Equal(Trou, silence.ZoneDuJeu);
        Assert.Equal(1, changements);

        // le deck.game « running » du meme lancement ne change rien
        silence.Poser(Jeu("l1", "running", Lancement("posed", notices: true)));
        Assert.Equal(1, changements);

        // Off en jeu : le Deck se retire (le cartouche revient), mais RetroArch reste muet : les messages vont encore au Deck,
        // qui montre la plaque seule
        silence.Choisir(Choix(off: true, "hide"));
        Assert.False(silence.DeckVisible);
        Assert.True(silence.AuDeck);
        Assert.Null(silence.ZoneDuJeu);
        Assert.Equal(2, changements);

        silence.Choisir(Choix(off: false, "show"));
        Assert.True(silence.DeckVisible);
        Assert.Equal(3, changements);

        silence.Poser(Jeu("l1", "ended", Lancement("posed", notices: true)));
        Assert.False(silence.DeckVisible);
        Assert.False(silence.AuDeck);
        Assert.False(silence.RetroArchMuet);
    }

    [Theory]
    [InlineData("none", true)]      // jeu lance par ES (pas de Deck) ou Deck absent
    [InlineData("off", true)]       // Off des le lancement
    [InlineData("window", true)]    // decor impossible : le Deck reste cache (charte du 08/11)
    [InlineData("posed", false)]    // un Deck d'une version sans plaque : RetroArch garde ses messages
    public void Sans_Deck_pose_et_capable_rien_ne_change(string status, bool notices)
    {
        var silence = new DeckSilence(new SimpleEventBus());
        silence.Poser(Jeu("l2", "launching", Lancement(status, notices)));
        Assert.False(silence.DeckVisible);
        Assert.False(silence.AuDeck);
        Assert.False(silence.RetroArchMuet);
        Assert.Null(silence.ZoneDuJeu);
        Assert.False(silence.Dire(DeckNotices.Scoring("scoring_certifiable")));
    }

    [Fact]
    public void Un_jeu_sans_Deck_ne_garde_rien_du_lancement_precedent()
    {
        var silence = new DeckSilence(new SimpleEventBus());
        silence.Poser(Jeu("l1", "launching", Lancement("posed", notices: true)));
        silence.Poser(Jeu("l2", "launching", null));   // lance par ES juste apres, sans Deck
        Assert.False(silence.DeckVisible);
        Assert.False(silence.AuDeck);
        Assert.False(DeckSilence.DireSiPose(DeckNotices.Scoring("scoring_offline")) && DeckSilence.Courante is null);
    }

    [Fact]
    public void Dire_publie_deck_notice_numerote_seulement_quand_le_Deck_doit_l_entendre()
    {
        var bus = new SimpleEventBus();
        var notices = new List<DeckNotice>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.notice") notices.Add((DeckNotice)e.Payload!); });
        var silence = new DeckSilence(bus);

        Assert.False(silence.Dire(DeckNotices.Scoring("scoring_certifiable")));
        Assert.Empty(notices);

        silence.Poser(Jeu("l1", "launching", Lancement("posed", notices: true)));
        Assert.True(silence.Dire(DeckNotices.Scoring("scoring_certifiable")));
        Assert.True(silence.Dire(DeckNotices.Scoring("scoring_continue", 412300)));
        // un message que le Deck montre deja (coup recu) : rien a publier, mais le Deck l'a « pris » (RetroArch se tait)
        Assert.True(silence.Dire(DeckNotices.DuSpeedrun(new MessageJoueur("speedrun.msg.hit", "Hit!"))));
        Assert.Equal(2, notices.Count);
        Assert.True(notices[1].Sequence > notices[0].Sequence);
        Assert.Equal("412,300", notices[1].Value);
    }

    // ─────────────────────────── le cartouche du defi et le bandeau du haut ───────────────────────────

    [Theory]
    [InlineData(true, false, true)]    // defi, pas de Deck : comme avant
    [InlineData(true, true, false)]    // defi, Deck pose : le Deck a LIVE RANK, le cartouche se tait
    [InlineData(false, false, false)]  // pas de defi
    public void Le_cartouche_du_defi_ne_parait_pas_devant_le_Deck(bool defi, bool deckVisible, bool montre)
        => Assert.Equal(montre, ChallengeHudService.DoitSeMontrer(defi, deckVisible));

    [Theory]
    [InlineData(false, false, true, "CommeAvant")]
    [InlineData(false, false, false, "CommeAvant")]
    [InlineData(true, true, true, "AuDeck")]       // prevol, continue... : la plaque du Deck
    [InlineData(true, true, false, "DansLeJeu")]   // message de la plateforme, traduit : confine au trou du decor
    [InlineData(true, false, true, "AuDeck")]      // Deck retire en jeu, RetroArch muet : la plaque seule
    [InlineData(true, false, false, "CommeAvant")]
    public void Le_bandeau_du_haut_va_au_Deck_ou_reste_dans_le_jeu(bool auDeck, bool visible, bool anglais, string attendu)
        => Assert.Equal(attendu, LiveContestOverlayService.Router(auDeck, visible, anglais).ToString());

    [Fact]
    public void Le_bandeau_confine_ne_deborde_pas_sur_les_rails()
    {
        Assert.Equal(883, EsBanniereForm.Largeur(1920, null));          // comme avant
        Assert.Equal(761, EsBanniereForm.Largeur(1920, 810));           // Hangar 1920 x 1080 : trou de 810 px
        Assert.Equal(883, EsBanniereForm.Largeur(1920, 1444));          // Rebel Iron : le trou est assez large
    }

    // ─────────────────────────── les textes du Deck ───────────────────────────

    public static TheoryData<string, string, string?> Speedrun => new()
    {
        { "speedrun.msg.ready", "READY", null },
        { "speedrun.msg.failed", "FAILED", null },
        { "speedrun.msg.voided_pause", "VOIDED", null },
        { "speedrun.msg.voided_fast_forward", "VOIDED", null },
        { "speedrun.msg.finish", "FINISH", "6:13.47" },
        { "speedrun.msg.finish_best", "NEW BEST", "6:13.47" },
        { "speedrun.msg.finish_unranked", "NOT RANKED", "6:13.47" },
        { "speedrun.msg.slow_game", "SLOW GAME", "2.1%" },
        { "speedrun.msg.locks_missing", "LOCKS OFF", null },
        { "speedrun.msg.state_failed", "NOT STARTED", null },
        { "speedrun.msg.no_pause", "NO PAUSE", null },
        { "speedrun.msg.pause_held", "RESTART", null },
        { "speedrun.msg.debug_blocked", "DEBUG MODE", null },
    };

    [Theory]
    [MemberData(nameof(Speedrun))]
    public void Les_messages_du_speedrun_deviennent_des_plaques_courtes_en_anglais(string cle, string titre, string? valeur)
    {
        var m = new MessageJoueur(cle, "x", ("time", "6:13.47"), ("best", "6:01.00"), ("percent", "2.1"));
        var n = DeckNotices.DuSpeedrun(m)!;
        Assert.Equal(titre, n.Title);
        Assert.Equal(valeur, n.Value);
        Assert.Equal("speedrun", n.Source);
        Court(n);
    }

    [Theory]
    [InlineData("speedrun.msg.hit")]
    [InlineData("speedrun.msg.hit_left")]
    [InlineData("speedrun.msg.hit_penalty")]
    public void Un_coup_recu_ne_se_dit_pas_au_Deck_sa_couche_epreuve_le_montre(string cle)
        => Assert.Null(DeckNotices.DuSpeedrun(new MessageJoueur(cle, "Hit! {left} left", ("left", "97"))));

    [Fact]
    public void Le_prevol_et_les_bandeaux_du_scoring_ont_leur_plaque()
    {
        var ok = NelfePlayScoringReporter.NoticeDuPrevol(true, "", [], force: false, null, horsLigne: false);
        Assert.Equal(("CERTIFIABLE", DeckSeverity.Good, "FOR THE LEADERBOARD"), (ok.Title, ok.Severity, ok.Text));
        Assert.Equal("CERTIFIED SETTINGS", NelfePlayScoringReporter.NoticeDuPrevol(true, "", [], true, null, false).Text);
        Assert.Equal("SENT WHEN NELFEPLAY IS BACK", NelfePlayScoringReporter.NoticeDuPrevol(true, "", [], false, null, true).Text);
        var rembobinage = NelfePlayScoringReporter.NoticeDuPrevol(true, "", ["rewind"], false, null, false);
        Assert.Equal(("UNCERTIFIED", DeckSeverity.Alert, "TURN OFF REWIND"), (rembobinage.Title, rembobinage.Severity, rembobinage.Text));
        Assert.Equal("PENDING", NelfePlayScoringReporter.NoticeDuPrevol(false, "profile.core_mismatch", [], false, null, false).Title);
        Assert.Equal("UNCERTIFIED", NelfePlayScoringReporter.NoticeDuPrevol(false, "content.bootleg_unnamed", [], false, null, false).Title);

        foreach (var cle in new[] { "scoring_continue", "scoring_credit", "scoring_joined", "scoring_clear", "scoring_uncertified", "scoring_credits_muted",
                     "scoring_multiplayer", "scoring_offline", "scoring_nothing_measured", "scoring_none_measured", "scoring_emulator_pending", "scoring_settings_pending", "scoring_not_certifiable", "scoring_certifiable" })
        {
            Court(DeckNotices.Scoring(cle, 412300));
        }

        foreach (var (titre, texte) in new[] { (DeckNotices.DuChoix(true, true, null, "hide").Title, ""), (DeckNotices.Message("multiplayer.no_controller", "alert", "NO PAD", "CHECK YOUR CONTROLLER", "multiplayer").Title, "") })
        {
            Assert.InRange(titre.Length, 1, 12);
        }

        foreach (var cle in Array.Empty<string>())
        {
            Court(DeckNotices.Scoring(cle, 412300));
        }

        Assert.Equal(("CONTINUE", "412,300"), (DeckNotices.Scoring("scoring_continue", 412300).Title, DeckNotices.Scoring("scoring_continue", 412300).Value));
    }

    [Fact]
    public void Le_choix_du_Deck_se_dit_en_plaque()
    {
        Assert.Equal(("DECK OFF", "FRAME STAYS TILL RELAUNCH"), T(DeckNotices.DuChoix(true, true, null, "hide")));
        Assert.Equal(("DECK OFF", ""), T(DeckNotices.DuChoix(true, false, null, "hide")));
        Assert.Equal(("19XX", "FROM NEXT LAUNCH"), T(DeckNotices.DuChoix(false, true, "19XX", "next-launch")));
        Assert.Equal(("19XX HANGAR", "PLAYERDECK ON"), T(DeckNotices.DuChoix(false, true, "19XX Hangar", "show")));
        static (string, string) T(DeckNotice n) => (n.Title, n.Text);
    }

    /// <summary>Une plaque de rail : titre de 12 caracteres au plus, detail de 40, en majuscules ASCII (polices pixel du Deck).</summary>
    private static void Court(DeckNotice n)
    {
        Assert.InRange(n.Title.Length, 1, 12);
        Assert.InRange(n.Text.Length, 0, 40);
        Assert.Equal(n.Title.ToUpperInvariant(), n.Title);
        Assert.Equal(n.Text.ToUpperInvariant(), n.Text);
        Assert.All((n.Title + n.Text + n.Value).ToCharArray(), c => Assert.InRange(c, ' ', '~'));
        Assert.InRange(n.DurationMs, 2000, 10000);
    }

    // ─────────────────────────── le lancement : RetroArch muet pour ce lancement seulement ───────────────────────────

    [Fact]
    public void Un_Deck_qui_montre_les_messages_coupe_les_notifications_de_RetroArch_pour_ce_lancement()
    {
        var (service, commandes) = Service(notices: true);
        var l = service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip");
        Assert.Equal("posed", l.Status);
        Assert.Equal(["-bezel", "nelfeplay-deck", "-ratio", "custom", "-integerscale", "0", "-OnScreenMsg", "0"], l.Arguments);
        Assert.True(l.Notices);
        Assert.True(l.RetroArchMuet);
        Assert.Empty(commandes.Envoyees);

        // Un Deck d'une version sans plaque : les arguments de la 1.9.163, RetroArch garde ses messages
        var (ancien, _) = Service(notices: false);
        var a = ancien.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip");
        Assert.Equal(DeckDecor.ArgumentsDuLanceur, a.Arguments);
        Assert.False(a.RetroArchMuet);
    }

    [Fact]
    public void La_capacite_du_Deck_se_lit_dans_la_sortie_de_son_rendu()
    {
        Assert.True(DeckLaunchService.PorteLesNotices("""{"Deck":"x","Version":2,"Screen":{"Width":1920,"Height":1200},"Hole":{"X":476,"Y":58,"Width":1444,"Height":1083},"Features":["notice"]}"""));
        Assert.False(DeckLaunchService.PorteLesNotices("""{"Deck":"x","Version":2,"Screen":{"Width":1920,"Height":1200},"Hole":{"X":476,"Y":58,"Width":1444,"Height":1083}}"""));
        Assert.False(DeckLaunchService.PorteLesNotices("pas du json"));
    }

    [Fact]
    public void L_argument_qui_coupe_RetroArch_est_une_fonction_du_lanceur_et_jamais_une_cle_qui_fait_refuser_un_score()
    {
        // « OnScreenMsg » : fonction d'ES pour libretro, que le lanceur ecrit dans video_font_enable ; les cles surveillees par
        // les reglages certifies (rewind, run-ahead, sauvegarde auto) n'y sont pas, ni rien des options du coeur.
        Assert.Equal(["-OnScreenMsg", "0"], DeckDecor.ArgumentsSansNotifications);
        Assert.DoesNotContain(LeaderboardInputService.FonctionsNeutraliseesDuDefi, f => f.Equals("-OnScreenMsg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["-rewind", "0", "-runahead", "0", "-autosave", "0"], LeaderboardInputService.FonctionsNeutraliseesDuDefi);
        Assert.NotNull(RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur("", "fbneo", "libretro", "fbneo", @"E:\r\mslug3.zip", null,
            DeckDecor.ArgumentsDuLanceur.Concat(DeckDecor.ArgumentsSansNotifications)));
    }

    [Fact]
    public void Le_choix_en_jeu_va_a_la_plaque_du_Deck_et_plus_a_RetroArch()
    {
        var bus = new SimpleEventBus();
        var notices = new List<DeckNotice>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.notice") notices.Add((DeckNotice)e.Payload!); });
        var silence = new DeckSilence(bus);
        var (service, commandes) = Service(notices: true, bus);
        service.Silence = silence;
        var l = service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip");
        silence.Poser(Jeu("l1", "launching", l));

        var off = service.Choisir(DeckChoix.Off, "gamepad")!;
        Assert.Equal("hide", off.Now);
        Assert.Single(notices);
        Assert.Equal("DECK OFF", notices[0].Title);
        Assert.Empty(commandes.Envoyees);   // pas de SHOW_MSG : RetroArch est muet pour ce lancement
    }

    [Fact]
    public void Sans_silence_le_choix_en_jeu_parle_dans_RetroArch_comme_avant()
    {
        var (service, commandes) = Service(notices: false);
        service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip");
        service.Choisir(DeckChoix.Off, "gamepad");
        for (var i = 0; i < 50 && commandes.Envoyees.Count == 0; i++) Thread.Sleep(20);
        Assert.Equal(["SHOW_MSG PlayerDeck off (frame stays until next launch)"], commandes.Envoyees);
    }

    [Fact]
    public void Un_lancement_parti_sans_son_decor_ne_garde_pas_un_Deck_pose()
    {
        var bus = new SimpleEventBus();
        var lancements = new List<DeckLancement>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.launch") lancements.Add((DeckLancement)e.Payload!); });
        var (service, _) = Service(notices: true, bus);
        service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip");
        Assert.True(File.Exists(Path.Combine(_dossier, "decorations", DeckDecor.Nom, "games", "fbneo", "mslug3.png")));

        service.Abandonner("direct_launch_failed");
        Assert.Equal("none", service.EnCours!.Status);
        Assert.Empty(service.EnCours.Arguments);
        Assert.False(service.EnCours.RetroArchMuet);
        Assert.Equal("none", lancements[^1].Status);
        Assert.False(Directory.Exists(Path.Combine(_dossier, "decorations", DeckDecor.Nom)));
    }

    [Fact]
    public void Le_defi_diffuse_avec_le_Deck_porte_le_decor_les_fonctions_coupees_et_le_netplay_de_l_hote()
    {
        var ligne = LeaderboardInputService.ArgumentsDuDefiAvecLeDeck("-p1index 0", new RetroBat.Api.Netplay.EsLaunchArguments.Resolution("fbneo", "libretro", "fbneo"),
            @"E:\RetroBat\roms\fbneo\mslug3.zip", [.. DeckDecor.ArgumentsDuLanceur, .. DeckDecor.ArgumentsSansNotifications],
            RetroBat.Api.Netplay.NetplayHostService.ArgumentsDeLHote)!;
        Assert.Contains("-bezel nelfeplay-deck -ratio custom -integerscale 0 -OnScreenMsg 0", ligne);
        Assert.Contains("-rewind 0 -runahead 0 -autosave 0", ligne);
        Assert.Contains("-netplaymode host", ligne);
        Assert.True(ligne.IndexOf("-netplaymode", StringComparison.Ordinal) < ligne.IndexOf("-rom", StringComparison.Ordinal));
        Assert.Contains("-system fbneo -emulator libretro -core fbneo", ligne);

        // le defi prive : la meme ligne, sans netplay
        var prive = LeaderboardInputService.ArgumentsDuDefiAvecLeDeck("", new RetroBat.Api.Netplay.EsLaunchArguments.Resolution("fbneo", "libretro", "fbneo"),
            @"E:\RetroBat\roms\fbneo\mslug3.zip", DeckDecor.ArgumentsDuLanceur, [])!;
        Assert.DoesNotContain("-netplaymode", prive);
    }

    [Fact]
    public void En_MULTIPLAYER_le_Deck_donne_ses_arguments_au_lanceur()
    {
        var jeu = new MultiplayerGame { Id = "19xx" };
        var avec = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", "19xx.zip", jeu, 1, [], decor: DeckDecor.Nom,
            argumentsDuDeck: [.. DeckDecor.ArgumentsDuLanceur, .. DeckDecor.ArgumentsSansNotifications])!;
        Assert.Contains("-bezel nelfeplay-deck -ratio custom -integerscale 0 -OnScreenMsg 0", avec);
        var ancien = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", "19xx.zip", jeu, 1, [], decor: DeckDecor.Nom)!;
        Assert.DoesNotContain("OnScreenMsg", ancien);
    }

    [Theory]
    [InlineData(null, "true")]
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    [InlineData("false", "false")]
    public void La_lecture_d_un_replay_reprend_les_notifications_voulues_par_ES(string? valeur, string attendu)
    {
        var fichier = Path.Combine(_dossier, "es_settings.cfg");
        File.WriteAllText(fichier, "<?xml version=\"1.0\"?>\n<config>\n"
            + (valeur is null ? "" : "<string name=\"global.OnScreenMsg\" value=\"" + valeur + "\" />\n") + "</config>\n");
        Assert.Equal(attendu, DeckDecor.NotificationsDEs(fichier));
    }

    // ─────────────────────────── outils ───────────────────────────

    private (DeckLaunchService, CommandesMuettes) Service(bool notices, SimpleEventBus? bus = null)
    {
        var racine = Path.Combine(_dossier, "deck");
        Directory.CreateDirectory(Path.Combine(racine, "themes"));
        File.WriteAllText(Path.Combine(racine, "themes", "metal-slug-3-pixel.json"),
            "{ \"format\": \"nelfeplay.deck-theme/1\", \"id\": \"metal-slug-3-pixel\", \"name\": \"REBEL IRON\", \"version\": 2, \"deck\": { \"default\": true },"
            + " \"match\": { \"roms\": [\"mslug3\"], \"variantOf\": \"metal-slug-3\" } }");
        var catalogue = DeckCatalogue.Lire(racine);
        var png = Path.Combine(_dossier, "rendu.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4e, 0x47]);
        var commandes = new CommandesMuettes();
        var service = new DeckLaunchService(bus ?? new SimpleEventBus(), new ApiContext { PlayerPseudo = "player" },
            fichierDesChoix: Path.Combine(_dossier, "choix.json"), commandes: commandes)
        {
            Catalogue = () => catalogue,
            Ecran = () => (1920, 1200),
            RacineDuDecor = Path.Combine(_dossier, "decorations", DeckDecor.Nom),
            DemarrerLeHost = false,
            Identite = (s, c, g) => new DeckIdentite.Cle("arcade/metal-slug-3", "metal-slug-3", "essai"),
            Rendeur = (e, l, h, _) => new DeckRendu(png, Trou, l, h, false, 1) { Notices = notices },
        };
        return (service, commandes);
    }

    private sealed class CommandesMuettes : ICommandesRetroArch
    {
        public readonly List<string> Envoyees = new();
        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500) => Task.FromResult<string?>(null);
        public Task EnvoyerAsync(string commande, CancellationToken ct) { lock (Envoyees) Envoyees.Add(commande); return Task.CompletedTask; }
    }
}
