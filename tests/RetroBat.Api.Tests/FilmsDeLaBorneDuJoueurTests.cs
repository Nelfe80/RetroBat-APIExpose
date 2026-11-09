using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FILMS DES RECORDS DU 09/11 : rendu sur la borne du joueur, station en dernier recours, controle de fidelite, raisons
/// de non-capture, sens des jeux verticaux.
/// </summary>
public sealed class FilmsDeLaBorneDuJoueurTests
{
    // --- ROM et coeur exacts pour un film ---

    [Fact]
    public void Un_film_refuse_une_rom_ou_un_coeur_approchants()
    {
        Assert.Null(ReplayVideoRenderer.RefusDuRuntime(new ResolvedRuntime("c.dll", "r.zip", ExactCore: true, ExactRom: true)));
        Assert.Equal("rom_inexacte", ReplayVideoRenderer.RefusDuRuntime(new ResolvedRuntime("c.dll", "r.zip", ExactCore: true, ExactRom: false)));
        Assert.Equal("coeur_inexact", ReplayVideoRenderer.RefusDuRuntime(new ResolvedRuntime("c.dll", "r.zip", ExactCore: false, ExactRom: true)));
        Assert.Equal("rom_inexacte", ReplayVideoRenderer.RefusDuRuntime(new ResolvedRuntime("c.dll", "r.zip", ExactCore: false, ExactRom: false)));
    }

    [Fact]
    public void Le_rendu_d_un_film_se_fait_sans_recalage_des_points_de_controle()
    {
        var profil = ReplayVideoRenderer.ProfilDeRendu(@"C:\rendu\sauvegardes", @"C:\rendu\films");
        Assert.Contains("replay_checkpoint_deserialize = \"false\"", profil);
    }

    // --- Controle de fidelite ---

    private const double Fps = 59.63;

    [Fact]
    public void Le_score_certifie_lu_avant_la_fin_du_run_rend_le_film_fidele()
    {
        // Sonde du 09/11 (Garegga de NeoGeoff) : 94 540 lu de la frame 7840 a 8562, remis a zero a 8568.
        var releves = new List<(long, long)> { (7785, 93_960), (7823, 94_500), (7840, 94_540), (8200, 94_540), (8562, 94_540), (8568, 0) };
        var v = ControleDeFidelite.Juger([new PointDuScore(8562, 94_540)], releves, Fps);
        Assert.Equal(ControleDeFidelite.Fidele, v.Issue);
    }

    [Fact]
    public void Un_score_qui_ne_retrouve_pas_le_certifie_est_une_desync()
    {
        // Garegga 1LC du 08/11 : 10 620 certifie, la partie rejouee sans recalage finit ailleurs.
        var releves = new List<(long, long)> { (4000, 16_290), (4100, 16_290), (4300, 16_310) };
        var v = ControleDeFidelite.Juger([new PointDuScore(4300, 10_620)], releves, Fps);
        Assert.Equal(ControleDeFidelite.Desync, v.Issue);
        Assert.Contains("10620", v.Detail);
    }

    [Fact]
    public void Un_score_franchi_entre_deux_lectures_reste_un_doute()
    {
        var releves = new List<(long, long)> { (1000, 5_000), (1012, 7_000) };
        Assert.Equal(ControleDeFidelite.NonVerifie, ControleDeFidelite.Juger([new PointDuScore(1012, 6_000)], releves, Fps).Issue);
    }

    [Fact]
    public void Sans_lecture_ni_point_le_film_n_est_pas_verifie()
    {
        Assert.Equal(ControleDeFidelite.NonVerifie, ControleDeFidelite.Juger([], [(10, 5)], Fps).Issue);
        Assert.Equal(ControleDeFidelite.NonVerifie, ControleDeFidelite.Juger([new PointDuScore(10, 5)], [], Fps).Issue);
        // Les lectures loin de la fin du run (plus de 15 s avant) ne comptent pas.
        Assert.Equal(ControleDeFidelite.NonVerifie,
            ControleDeFidelite.Juger([new PointDuScore(5000, 5)], [(1000, 7)], Fps).Issue);
    }

    [Fact]
    public void Le_1lc_et_le_1cc_d_un_meme_replay_se_verifient_chacun_a_sa_fin()
    {
        var releves = new List<(long, long)> { (2950, 10_620), (3000, 10_620), (8000, 94_540) };
        var points = new[] { new PointDuScore(3000, 10_620), new PointDuScore(8000, 94_540) };
        Assert.Equal(ControleDeFidelite.Fidele, ControleDeFidelite.Juger(points, releves, Fps).Issue);
        // Un seul des deux faux suffit a refuser.
        var faux = new[] { new PointDuScore(3000, 10_620), new PointDuScore(8000, 99_999) };
        Assert.Equal(ControleDeFidelite.Desync, ControleDeFidelite.Juger(faux, releves, Fps).Issue);
    }

    [Fact]
    public void La_station_refuse_un_film_non_verifie_la_borne_du_joueur_l_envoie()
    {
        Assert.Null(ReplayVideoPublisher.RefusDeFidelite(ControleDeFidelite.Fidele, station: true));
        Assert.Equal("desync", ReplayVideoPublisher.RefusDeFidelite(ControleDeFidelite.Desync, station: false));
        Assert.Equal("desync", ReplayVideoPublisher.RefusDeFidelite(ControleDeFidelite.Desync, station: true));
        Assert.Equal("non_verifie", ReplayVideoPublisher.RefusDeFidelite(ControleDeFidelite.NonVerifie, station: true));
        Assert.Null(ReplayVideoPublisher.RefusDeFidelite(ControleDeFidelite.NonVerifie, station: false));
    }

    [Fact]
    public void La_liste_du_site_porte_les_scores_a_verifier()
    {
        var liste = ReplayVideoPublisher.LireLaListe("""
            {"ok":true,"role":"player","targets":[{"replay_id":"rp_1","run_start_frame":0,"run_end_frame":8562,
              "checks":[{"end_frame":8562,"score":94540},{"end_frame":3000,"score":10620},{"score":5}]},
              {"replay_id":"rp_2","run_start_frame":10,"run_end_frame":900,"challenge":true}]}
            """);
        Assert.Equal("player", liste.Role);
        Assert.Equal([new PointDuScore(8562, 94_540), new PointDuScore(3000, 10_620)], liste.Cibles[0].Controles);
        Assert.Null(liste.Cibles[1].Controles);
        Assert.True(liste.Cibles[1].Epreuve);
    }

    // --- Lecture du score dans la memoire du coeur ---

    private const string MemGaregga = """
        return {
          events = {
            scoring = {
              points = {
                { address=0X166C, type="u16le", condition="change", action="SCORE_STATE", player=1, score_kind="game", desc="P1 - Score [Upper 16 bits]" },
                { address=0X166E, type="u16le", condition="change", action="SCORE_STATE", player=1, score_kind="game", desc="P1 - Score [Lower 16 bits]" },
                { address=0X1764, type="u16le", condition="change", action="SCORE_STATE", player=2, score_kind="game", desc="P2 - Score [Upper 16 bits]" },
                { address=0X2000, type="u8", condition="change", action="SCORE_STATE", desc="Top Score" },
              },
            },
          },
        }
        """;

    [Fact]
    public void Les_lignes_du_score_sont_celles_du_joueur_1_hors_meilleur_score()
    {
        var lignes = LecteurDuScore.LignesDuScore(MemGaregga);
        Assert.Equal([0x166CL, 0x166EL], lignes.Select(l => l.Adresse));
        Assert.All(lignes, l => Assert.Equal(2, l.Octets));
        Assert.Contains("Upper 16", lignes[0].Description);
    }

    [Fact]
    public void Les_octets_lus_donnent_la_valeur_du_type_et_le_score_se_compose()
    {
        // READ_CORE_RAM 166c 4 a la sonde : 01 00 4C 71 ; mot fort 0x0001, mot faible 0x714C = 94 540.
        var octets = LecteurDuScore.OctetsDeLaReponse("READ_CORE_RAM 166c 01 00", 2);
        Assert.Equal([1, 0], octets);
        var lignes = LecteurDuScore.LignesDuScore(MemGaregga);
        Assert.Equal(1, LecteurDuScore.Valeur(lignes[0], [0x01, 0x00]));
        Assert.Equal(0x714C, LecteurDuScore.Valeur(lignes[1], [0x4C, 0x71]));
        Assert.Equal(0x4C71, LecteurDuScore.Valeur(lignes[1] with { Type = "u16be" }, [0x4C, 0x71]));
        Assert.Equal(0x0C, LecteurDuScore.Valeur(lignes[1] with { Masque = 0x0F }, [0x4C, 0x00]));
        Assert.Null(LecteurDuScore.OctetsDeLaReponse("READ_CORE_RAM 166c -1", 2));
        Assert.Null(LecteurDuScore.OctetsDeLaReponse("GET_STATUS PLAYING", 2));
    }

    [Fact]
    public void Le_score_se_compose_avec_l_agregateur_du_score_en_direct()
    {
        var fichier = Path.Combine(Path.GetTempPath(), "nelfe-test-garegga-" + Guid.NewGuid().ToString("N") + ".MEM");
        File.WriteAllText(fichier, MemGaregga);
        try
        {
            using var lecteur = LecteurDuScore.Depuis("arcade", "battle-garegga", fichier, MemGaregga);
            Assert.NotNull(lecteur);
            Assert.Equal(94_540, lecteur!.Composer([1, 0x714C]));
            // Une ligne illisible garde sa derniere valeur.
            Assert.Equal(65_536 + 0x7200, lecteur.Composer([null, 0x7200]));
        }
        finally
        {
            File.Delete(fichier);
        }
    }

    [Fact]
    public void Seuls_les_coeurs_releves_lisent_le_score_et_l_arcade_cherche_son_mem_dans_arcade()
    {
        Assert.True(LecteurDuScore.CoeurReleve("fbneo"));
        Assert.True(LecteurDuScore.CoeurReleve("fceumm"));
        Assert.False(LecteurDuScore.CoeurReleve("genesis_plus_gx"));
        Assert.False(LecteurDuScore.CoeurReleve(null));
        Assert.Equal(["arcade"], LecteurDuScore.SystemesDuMem("fbneo", "fb_alpha"));
        Assert.Equal(["nes"], LecteurDuScore.SystemesDuMem("nes", "nes"));
    }

    // --- Planificateur : manettes ---

    [Fact]
    public void Une_manette_qui_change_compte_comme_activite()
    {
        var repos = new ReplayVideoScheduler.EtatDeManette(0, 0, 0, 300, -200, 0, 0);
        Assert.True(ReplayVideoScheduler.ChangementDeManette(repos, repos with { Boutons = 0x1000 }));
        Assert.True(ReplayVideoScheduler.ChangementDeManette(repos, repos with { GachetteD = 200 }));
        Assert.True(ReplayVideoScheduler.ChangementDeManette(repos, repos with { GX = 32_000 }));
        // Le tremblement d'un stick au repos ne compte pas ; ni une manette qui apparait ou disparait.
        Assert.False(ReplayVideoScheduler.ChangementDeManette(repos, repos with { GX = 2_500, DY = -1_800 }));
        Assert.False(ReplayVideoScheduler.ChangementDeManette(repos, repos));
        Assert.False(ReplayVideoScheduler.ChangementDeManette(null, repos));
        Assert.False(ReplayVideoScheduler.ChangementDeManette(repos, null));
    }

    [Fact]
    public void La_borne_du_joueur_rend_apres_trois_minutes_de_repos()
    {
        var maintenant = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        var repos = TimeSpan.FromMinutes(3);
        Assert.True(ReplayVideoScheduler.PeutFilmer(ReplayVideoPublisher.RoleJoueur, maintenant, maintenant.AddMinutes(-3), repos, false, false, false));
        Assert.False(ReplayVideoScheduler.PeutFilmer(ReplayVideoPublisher.RoleJoueur, maintenant, maintenant.AddMinutes(-2), repos, false, false, false));
        Assert.False(ReplayVideoScheduler.PeutFilmer(ReplayVideoPublisher.RoleJoueur, maintenant, maintenant.AddHours(-1), repos, true, false, false));
    }

    // --- Dossier des films ---

    [Fact]
    public void L_api_pose_le_dossier_des_films_quand_retroarch_cfg_n_en_a_pas()
    {
        var sans = "video_driver = \"gl\"\nrecording_output_directory = \"\"\n";
        Assert.Contains("recording_output_directory = \"D:\\RetroBat\\records\\output\"",
            ReplayCaptureService.AvecDossierDesFilms(sans, @"D:\RetroBat\records\output"));
        Assert.Contains("recording_output_directory = \"D:\\x\"", ReplayCaptureService.AvecDossierDesFilms("a = \"1\"\n", @"D:\x"));
        Assert.Null(ReplayCaptureService.AvecDossierDesFilms("recording_output_directory = \"E:\\films\"\n", @"D:\x"));
    }

    // --- Sens des jeux verticaux ---

    [Fact]
    public void La_rotation_de_mame_donne_les_quarts_de_tour_libretro()
    {
        Assert.Equal(3, SensDesJeuxVerticaux.DepuisMame(90));
        Assert.Equal(1, SensDesJeuxVerticaux.DepuisMame(270));
        Assert.Equal(2, SensDesJeuxVerticaux.DepuisMame(180));
        Assert.Equal(0, SensDesJeuxVerticaux.DepuisMame(0));
        Assert.Null(SensDesJeuxVerticaux.DepuisMame(45));
    }

    [Fact]
    public void Le_sens_se_lit_dans_la_sortie_de_listxml()
    {
        var xml = """
            <mame><machine name="contra" sourcefile="konami/contra.cpp"><description>Contra</description>
            <display tag="screen" type="raster" rotate="90" width="280" height="224" /></machine>
            <machine name="bgaregga"><display tag="screen" type="raster" rotate="270" width="320" height="240" /></machine>
            <machine name="pacman"><display tag="screen" type="raster" width="288" height="224" /></machine></mame>
            """;
        Assert.Equal(90, SensDesJeuxVerticaux.RotationDuListXml(xml, "contra"));
        Assert.Equal(270, SensDesJeuxVerticaux.RotationDuListXml(xml, "bgaregga"));
        Assert.Null(SensDesJeuxVerticaux.RotationDuListXml(xml, "pacman"));
        Assert.Null(SensDesJeuxVerticaux.RotationDuListXml(xml, "inconnu"));
    }

    [Fact]
    public void Une_image_deja_en_portrait_ne_tourne_pas_une_image_couchee_prend_le_sens_de_mame()
    {
        Assert.Equal(0, SensDesJeuxVerticaux.QuartsDeTour(224, 280, null));
        Assert.Equal(3, SensDesJeuxVerticaux.QuartsDeTour(280, 224, 3));
        Assert.Equal(1, SensDesJeuxVerticaux.QuartsDeTour(320, 240, 1));
        Assert.Null(SensDesJeuxVerticaux.QuartsDeTour(280, 224, null));
        Assert.Null(SensDesJeuxVerticaux.QuartsDeTour(280, 224, 2));
        // Et le film ainsi tourne est bien en portrait.
        Assert.True(ReplayCaptureService.PortraitUneFoisTourne(280, 224, SensDesJeuxVerticaux.QuartsDeTour(280, 224, 3)));
        Assert.True(ReplayCaptureService.PortraitUneFoisTourne(224, 280, SensDesJeuxVerticaux.QuartsDeTour(224, 280, null)));
    }
}
