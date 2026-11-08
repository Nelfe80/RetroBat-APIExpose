using System.Text.Json.Nodes;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FILMS DES RENCONTRES MULTIPLAYER (CDC films MULTIPLAYER et MASS RUN v1.0, lots M1 a M3, 08/11) : la portion coupee
/// dans le film de l'hote, ce que la borne dit au site, et le spectateur cache des consoles reliees avant les invites (D-M9).
/// </summary>
public sealed class FilmsDesRencontresTests
{
    [Fact]
    public void La_portion_va_du_vrai_depart_moins_2_s_a_la_fin_lue_plus_la_fermeture()
    {
        // Le relevé attendu au journal du CDC (§4.1, M1) : « portion 31,2 s a 152,4 s du film ».
        var (portion, raison) = ReplayCaptureService.PortionDeLaRencontre(33.2, 147.4, 5, 160);
        Assert.Equal("rencontre", raison);
        Assert.NotNull(portion);
        Assert.Equal(31.2, portion!.DebutS);
        Assert.Equal(152.4, portion.FinS);
        Assert.True(portion.Complete);
        Assert.Equal(121.2, portion.DureeS);
    }

    [Fact]
    public void La_fin_de_la_portion_est_bornee_par_la_fin_du_film()
    {
        var (portion, _) = ReplayCaptureService.PortionDeLaRencontre(20, 157, 8, 160);
        Assert.Equal(160, portion!.FinS);
        Assert.True(portion.Complete);
        // Un vrai depart tout au debut du film : la portion part de la premiere image.
        Assert.Equal(0, ReplayCaptureService.PortionDeLaRencontre(1, 60, 5, 70).Portion!.DebutS);
    }

    [Fact]
    public void Sans_vrai_depart_il_n_y_a_pas_de_portion()
    {
        // Title Fight du 27/10 : « 0/1 invite(s) a leur place en 93 s », partie a un seul humain.
        var (portion, raison) = ReplayCaptureService.PortionDeLaRencontre(null, 120, 5, 175);
        Assert.Null(portion);
        Assert.Equal("sans vrai depart", raison);
        // Un film qui commence apres le vrai depart (le debut de la rencontre y manque) n'en a pas non plus.
        Assert.Null(ReplayCaptureService.PortionDeLaRencontre(-3, 120, 5, 175).Portion);
        Assert.Null(ReplayCaptureService.PortionDeLaRencontre(180, null, null, 175).Portion);
        Assert.Null(ReplayCaptureService.PortionDeLaRencontre(10, 100, 5, 0).Portion);
    }

    [Fact]
    public void Sans_fin_lue_la_portion_finit_1_s_avant_le_film_et_demande_20_s()
    {
        var (longue, _) = ReplayCaptureService.PortionDeLaRencontre(10, null, null, 40);
        Assert.Equal(8, longue!.DebutS);
        Assert.Equal(39, longue.FinS);
        Assert.False(longue.Complete);

        var (courte, raison) = ReplayCaptureService.PortionDeLaRencontre(10, null, null, 25);
        Assert.Null(courte);
        Assert.Contains("moins de 20 s", raison);

        // Une fin lue avant le vrai depart (signal d'une autre manche) ne vaut pas fin : la portion reste incomplete.
        Assert.False(ReplayCaptureService.PortionDeLaRencontre(30, 12, 5, 80).Portion!.Complete);
    }

    [Fact]
    public void Une_rencontre_complete_part_meme_sous_20_s()
    {
        var (portion, _) = ReplayCaptureService.PortionDeLaRencontre(5, 15, 5, 30);
        Assert.NotNull(portion);
        Assert.True(portion!.Complete);
        Assert.Equal(17, portion.DureeS);
    }

    [Fact]
    public void Une_rencontre_de_plus_de_6_minutes_ne_part_pas()
    {
        var (portion, raison) = ReplayCaptureService.PortionDeLaRencontre(10, 375, 5, 400);
        Assert.Null(portion);
        Assert.Contains("6 min", raison);
        Assert.NotNull(ReplayCaptureService.PortionDeLaRencontre(10, 360, 5, 400).Portion);
    }

    [Fact]
    public void Les_reperes_se_ramenent_au_debut_du_film()
    {
        var debut = new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);
        Assert.Equal(33.25, ReplayCaptureService.SecondesDuFilm(debut, debut.AddSeconds(33.25)));
        Assert.Null(ReplayCaptureService.SecondesDuFilm(debut, null));

        // Le debut du film est sa creation, si elle est plausible ; sinon l'instant de la capture.
        var maintenant = debut.AddSeconds(40);
        Assert.Equal(debut, ReplayCaptureService.DebutDuFilm(debut, maintenant));
        Assert.Equal(maintenant, ReplayCaptureService.DebutDuFilm(maintenant.AddSeconds(5), maintenant));
        Assert.Equal(maintenant, ReplayCaptureService.DebutDuFilm(maintenant.AddHours(-1), maintenant));
        Assert.Equal(maintenant, ReplayCaptureService.DebutDuFilm(null, maintenant));
    }

    [Fact]
    public void Les_instants_du_bus_se_relisent_en_utc()
    {
        var utc = new DateTime(2026, 11, 8, 21, 0, 3, 250, DateTimeKind.Utc);
        Assert.Equal(utc, ReplayCaptureService.Instant(new { Room = "r", Utc = utc }, "Utc"));
        Assert.Null(ReplayCaptureService.Instant(new { Room = "r" }, "Utc"));
        Assert.True(ReplayCaptureService.MemeSalle("abc", "abc"));
        Assert.True(ReplayCaptureService.MemeSalle(null, "abc"));
        Assert.False(ReplayCaptureService.MemeSalle("abc", "def"));
    }

    private static ReplayCaptureService.CaptureGardee Rencontre(bool complete = true)
        => new("mp_06GRJXJS0RJTDSZT0JDJ9PVXSG", @"C:\films\mp.mkv", 0, 0, new DateTime(2026, 11, 8, 21, 5, 0, DateTimeKind.Utc),
            SansReplay: true, Origine: true, DureeS: 160, DebutSequenceS: 31.2, FinSequenceS: 152.4,
            Room: new string('a', 32), DebutUtc: new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc), VraiDepartS: 33.2,
            FinLueS: 147.4, RencontreComplete: complete);

    [Fact]
    public void La_demande_d_une_rencontre_dit_sa_gameroom_sa_portion_et_l_heure_de_la_borne()
    {
        var champs = ReplayCaptureService.ChampsDeLaRencontre(Rencontre(), new DateTime(2026, 11, 8, 21, 10, 0, DateTimeKind.Utc));
        Assert.Equal("multiplayer", (string?)champs["source"]);
        Assert.Equal(new string('a', 32), (string?)champs["room"]);
        Assert.Equal(31.2, (double?)champs["start_s"]);
        Assert.Equal(152.4, (double?)champs["end_s"]);
        Assert.True((bool?)champs["complete"]);
        Assert.Equal("2026-11-08T21:00:31.200Z", (string?)champs["portion_start_utc"]);
        Assert.Equal("2026-11-08T21:02:32.400Z", (string?)champs["portion_end_utc"]);
        Assert.Equal("2026-11-08T21:10:00.000Z", (string?)champs["device_now_utc"]);
        // Les cartes de l'ecran de lancement VS et de l'ecran de fin (lot M4) : rien tant que l'exe ne les pose pas.
        Assert.Equal(0, (double?)champs["intro_s"]);
        Assert.Equal(0, (double?)champs["outro_s"]);
        // Aucun pseudo, aucune adresse de relais, aucun mot de passe : la gameroom suffit au site.
        Assert.DoesNotContain("relay", champs.ToJsonString());
        Assert.DoesNotContain("password", champs.ToJsonString());
    }

    [Fact]
    public void Une_rencontre_sans_heure_de_film_ne_dit_pas_de_portion_en_utc()
    {
        var champs = ReplayCaptureService.ChampsDeLaRencontre(Rencontre() with { DebutUtc = null }, DateTime.UtcNow);
        Assert.False(champs.ContainsKey("portion_start_utc"));
        Assert.True(champs.ContainsKey("start_s"));
    }

    [Fact]
    public void La_demande_d_envoi_d_une_rencontre_remplace_la_source_et_les_frames()
    {
        var demande = new JsonObject
        {
            ["replay_id"] = "mp_x", ["size"] = 10, ["source"] = "capture", ["run_start_frame"] = 0, ["run_end_frame"] = 0, ["fps"] = 0,
        };
        var champs = ReplayCaptureService.ChampsDeLaRencontre(Rencontre(), DateTime.UtcNow);
        var fusion = ReplayVideoPublisher.AvecLaRencontre(demande, champs);
        Assert.Equal("multiplayer", (string?)fusion["source"]);
        Assert.False(fusion.ContainsKey("run_start_frame"));
        Assert.False(fusion.ContainsKey("fps"));
        Assert.Equal("mp_x", (string?)fusion["replay_id"]);
        Assert.Equal(new string('a', 32), (string?)fusion["room"]);
        // Les champs de la rencontre restent utilisables pour la demande suivante (copies, pas deplaces).
        Assert.Equal(new string('a', 32), (string?)champs["room"]);
    }

    [Fact]
    public void La_taille_annoncee_avant_la_version_ne_depasse_jamais_le_plafond()
    {
        Assert.Equal(121_200_000, ReplayCaptureService.TailleEstimee(Rencontre(), 300_000_000));
        Assert.Equal(300_000_000, ReplayCaptureService.TailleEstimee(Rencontre() with { FinSequenceS = 400 }, 300_000_000));
        // Avec les cartes du lot M4, leur duree compte aussi.
        Assert.Equal(130_200_000, ReplayCaptureService.TailleEstimee(Rencontre() with { CarteAvantS = 3, CarteApresS = 6 }, 300_000_000));
    }

    [Fact]
    public void Une_fiche_de_rencontre_se_relit_avec_ses_reperes()
    {
        var g = Rencontre() with { Coop = true, Issue = "mission_cleared", DuSpectateur = true };
        var relue = System.Text.Json.JsonSerializer.Deserialize<ReplayCaptureService.CaptureGardee>(System.Text.Json.JsonSerializer.Serialize(g));
        Assert.Equal(g, relue);
        // Une fiche d'avant les rencontres (un record) se relit sans gameroom.
        var ancienne = System.Text.Json.JsonSerializer.Deserialize<ReplayCaptureService.CaptureGardee>(
            "{\"ReplayId\":\"fm_x\",\"Fichier\":\"f.mkv\",\"DebutFilm\":0,\"FinFilm\":10,\"CreeeUtc\":\"2026-11-01T00:00:00Z\"}");
        Assert.Null(ancienne!.Room);
        Assert.False(ancienne.RencontreComplete);
    }

    [Fact]
    public void Le_spectateur_passe_avant_les_invites_seulement_sur_des_consoles_reliees_prevues()
    {
        Assert.True(MultiplayerLaunchService.SpectateurAvantLesInvites(prevu: true, vueDuSpectateur: true, enService: true));
        Assert.False(MultiplayerLaunchService.SpectateurAvantLesInvites(prevu: false, vueDuSpectateur: true, enService: true));
        Assert.False(MultiplayerLaunchService.SpectateurAvantLesInvites(prevu: true, vueDuSpectateur: false, enService: true));
        Assert.False(MultiplayerLaunchService.SpectateurAvantLesInvites(prevu: true, vueDuSpectateur: true, enService: false));
        // L'interrupteur est en service : les consoles reliees ont leur film des la v1 (D-M9).
        Assert.True(MultiplayerSpectateur.AvantLesInvites);
    }

    [Fact]
    public void Le_spectateur_ne_fait_jamais_depasser_la_limite_de_publication_de_la_plateforme()
    {
        var maintenant = new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);
        // Loin de la limite : 60 s de connexion et 15 s pour que la capture prenne son film.
        Assert.Equal(TimeSpan.FromSeconds(75), MultiplayerLaunchService.AttenteDuSpectateur(maintenant, maintenant.AddSeconds(85)));
        // Pres de la limite : ce qui reste, moins 5 s de marge.
        Assert.Equal(TimeSpan.FromSeconds(25), MultiplayerLaunchService.AttenteDuSpectateur(maintenant, maintenant.AddSeconds(30)));
        // Limite passee : la session part sans spectateur.
        Assert.Equal(TimeSpan.Zero, MultiplayerLaunchService.AttenteDuSpectateur(maintenant, maintenant.AddSeconds(4)));
    }

    [Fact]
    public void Le_film_du_spectateur_ne_compte_que_s_il_est_connecte()
    {
        // Sans spectateur lance, la marque « filme » ne vaut rien : la session ne l'attend pas.
        MultiplayerSpectateur.Arreter();
        MultiplayerSpectateur.NoterFilme();
        Assert.False(MultiplayerSpectateur.Filme);
        MultiplayerSpectateur.Arreter();
        Assert.False(MultiplayerSpectateur.Filme);
    }

    [Fact]
    public void Les_evenements_de_la_rencontre_ont_leur_nom()
    {
        Assert.Equal("multiplayer.match.started", MultiplayerLaunchService.EvenementRencontreCommencee);
        Assert.Equal("multiplayer.match.ended", MultiplayerLaunchService.EvenementRencontreFinie);
    }
}
