using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE MODE D'ENTRAINEMENT (Metal Slug 3, 14/11) : un point d'entree par mission, zone et boss, kind « training ». Il a la
/// famille d'un challenge (non certifie, .MEM de la couche des challenges), son propre onglet TRAINING au panneau, ses groupes
/// par mission (« group » du reglement), et il n'est jamais classe : aucun temps n'en part, ni record ni place.
/// Les reglements du Data Pack (metal-slug-3.training-*.json) sont relus ici : .MEM, verrou des vies, depart et arrivee.
/// </summary>
public class DefisEntrainementTests
{
    private static string Dossier => Path.Combine(AppContext.BaseDirectory, "Reglements");

    private static DefisPanelService Service() => new(null!, null!, null!, null!, null!);

    private static int _ids;

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    /// <summary>Un point d'entrainement minimal, a la forme de ceux du Data Pack.</summary>
    private static Reglement Point(string id, int ordre, int mission, int section, string groupeFr = "Mission 1", string groupeEn = "Mission 1")
        => Reglement.Lire($$"""
        {
          "schema": "nelfe.challenge.v2", "challenge_id": "metal-slug-3.{{id}}", "version": 1, "ruleset": "ch-ms3-{{id}}-v1",
          "order": {{ordre}}, "kind": "training",
          "group": { "fr": "{{groupeFr}}", "en": "{{groupeEn}}" },
          "title": { "fr": "Point {{id}}", "en": "Point {{id}}" },
          "summary": { "fr": "S'exercer", "en": "Practice" },
          "game": { "system_id": "arcade", "rom_group": "metal-slug-3" },
          "mem_sha256": "0fb6762a946cb3844633b5e57ba15c0c08a1a37f34ad8d57bbdc2e744a514084",
          "start_policy": "canonical_state",
          "state": { "file": "arcade/metal-slug-3/{{id}}.state", "name": "{{id}}-fbneo", "sha256": "{{new string('a', 64)}}" },
          "start": [ { "value": { "address": "0x10FDB6", "equals": "0x01" } },
                     { "value": { "address": "0x10040C", "equals": "0x{{mission:X2}}" } } ],
          "timing": { "official": "independent", "independent": {
            "start": { "event": { "action": "MOUNT_STATE", "address": "0x1002C1", "leaves": "0x00" },
                       "when": [ { "value": { "address": "0x10040C", "equals": "0x{{mission:X2}}" } } ] },
            "finish": { "event": { "action": "PROGRESSION_STAGE", "address": "0x10040C", "leaves": "0x{{mission:X2}}", "value": "0x{{mission + 1:X2}}" } } } },
          "void": [],
          "ranking": { "direction": "lower_better", "metric": "frames" },
          "cores": [ "fbneo" ],
          "runtime": { "core_ram": { "base": "0x100000", "swap16": true }, "frame_rate": 59.185606 }
        }
        """);

    private static SessionSpeedrun Partir(Reglement r, int mission, int section, long top, int vehicule = 0x01)
    {
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "e" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        s.RecevoirImage([S(1, "PROGRESSION_STAGE", "0x10040D", section)]);
        foreach (var a in s.AdressesInconnues)
            s.Relever(a, a switch { 0x10FDB6 => 1, 0x10040C => mission, 0x10040D => section, _ => 0 });
        s.RecevoirImage([S(top, "MOUNT_STATE", "0x1002C1", vehicule)]);
        return s;
    }

    [Fact]
    public void Un_point_d_entrainement_est_de_la_famille_des_challenges_mais_n_est_jamais_classe()
    {
        var r = Point("training-m1-00", 100, 0, 0);
        Assert.True(r.EstUnEntrainement);
        Assert.True(r.EstUnChallenge);
        Assert.Equal("Mission 1", r.Groupe("fr"));
        Assert.Equal("Mission 1", r.Groupe("de"));
        // partie normale (decision du user du 14/11) : aucun verrou
        Assert.Empty(r.Verrous);
    }

    [Fact]
    public void Une_seance_finie_n_est_jamais_retenue_pour_l_envoi_et_le_dit_au_joueur()
    {
        var r = Point("training-m1-03", 105, 0, 3);
        var s = Partir(r, 0, 3, 91);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        // une mort ne fait rien echouer, un continue non plus : rien n'est interdit
        s.RecevoirImage([S(400, "LOSE_LIFE", "0x1002BB", 0x01)]);
        s.RecevoirImage([S(500, "CONTINUE_SCREEN", "0x10FDB6", 0x02)]);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        s.RecevoirImage([S(4091, "PROGRESSION_STAGE", "0x10040C", 0x01)]);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        Assert.Null(s.PrendreLaMeilleure());
        var messages = s.PrendreLesMessages();
        Assert.Contains(messages, m => m.Cle == "speedrun.msg.training_ready");
        Assert.Contains(messages, m => m.Cle == "speedrun.msg.training_finish");
        Assert.DoesNotContain(messages, m => m.Cle is "speedrun.msg.ready" or "speedrun.msg.finish_best" or "speedrun.msg.finish");
        // le temps reste au joueur, a titre d'information
        Assert.Equal(4000, s.MeilleurTemps);
    }

    /// <summary>
    /// UNE PARTIE NORMALE JUSQU'AU GAME OVER (banc du 14/11, Sol Dae Rokker, vrai coeur, aucun appui) : morts 2 -> 1 -> 0, ecran
    /// de continue (0x10FDB6 = 02), game over (03), puis le titre (mission 00) et les demos (06, 07, 05). Rien ne finit la
    /// seance : ni le game over, ni le retour au titre, ni les demos ; la fermeture du jeu l'abandonne. Avec un continue, la
    /// mission reprend et sa vraie fin (03 -> 04) finit la seance.
    /// </summary>
    [Fact]
    public void Un_game_over_ne_finit_pas_la_seance_seule_la_vraie_fin_de_mission()
    {
        var r = Reglement.Lire(File.ReadAllText(Path.Combine(Dossier, "metal-slug-3.training-m4-02.json")));
        var s = Partir(r, 3, 2, 91);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        foreach (var (image, action, adresse, valeur) in new[]
                 {
                     (995L, "LOSE_LIFE", "0x1002BB", 1), (1600L, "LOSE_LIFE", "0x1002BB", 0), (2072L, "CONTINUE_SCREEN", "0x10FDB6", 2),
                     (3350L, "CONTINUE_SCREEN", "0x10FDB6", 3), (4496L, "PROGRESSION_STAGE", "0x10040C", 0), (4971L, "CONTINUE_SCREEN", "0x10FDB6", 0),
                     (5290L, "PROGRESSION_STAGE", "0x10040C", 6), (8020L, "PROGRESSION_STAGE", "0x10040C", 0), (8238L, "PROGRESSION_STAGE", "0x10040C", 5),
                 })
            s.RecevoirImage([S(image, action, adresse, valeur)]);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        s.JeuFerme();
        Assert.Equal(SessionSpeedrun.Aborted, s.Etat);
        Assert.Null(s.PrendreLaMeilleure());

        var c = Partir(r, 3, 2, 91);
        c.RecevoirImage([S(2072, "CONTINUE_SCREEN", "0x10FDB6", 2)]);
        c.RecevoirImage([S(2163, "CONTINUE_SCREEN", "0x10FDB6", 1)]);
        c.RecevoirImage([S(3800, "PROGRESSION_STAGE", "0x10040C", 4)]);
        Assert.Equal(SessionSpeedrun.Finished, c.Etat);
        Assert.Null(c.PrendreLaMeilleure());
    }

    [Fact]
    public void Le_depart_ne_depend_pas_de_la_section_l_aire_peut_passer_seule_a_la_suivante()
    {
        // Mission 3 et mission finale : la premiere aire passe seule a la suivante ; le soldat se pose dans la mission
        var r = Point("training-m3-00", 300, 2, 0);
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "e" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        s.RecevoirImage([S(1, "PROGRESSION_STAGE", "0x10040D", 0)]);
        foreach (var a in s.AdressesInconnues) s.Relever(a, a switch { 0x10FDB6 => 1, 0x10040C => 2, _ => 0 });
        s.RecevoirImage([S(40, "PROGRESSION_STAGE", "0x10040D", 1)]);
        s.RecevoirImage([S(120, "MOUNT_STATE", "0x1002C1", 0x01)]);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
    }

    [Fact]
    public void Le_panneau_range_l_entrainement_dans_TRAINING_par_mission_et_dans_l_ordre_du_jeu()
    {
        var defis = new[]
        {
            Point("training-m2-00", 200, 1, 0, "Mission 2", "Mission 2"),
            Point("training-m1-04", 106, 0, 4),
            Point("training-m1-00", 100, 0, 0),
            Reglement.Lire(File.ReadAllText(Path.Combine(Dossier, "metal-slug-3.boss-m1.json"))),
        };
        var lignes = Service().Composer(defis, new Dictionary<string, (int, long?)> { ["metal-slug-3.training-m1-04"] = (3, 2000) }, "fr");

        Assert.Equal([DefisPanelModel.Onglet.Training, DefisPanelModel.Onglet.Challenges], lignes.Keys.OrderBy(k => k));
        var training = lignes[DefisPanelModel.Onglet.Training];
        Assert.Equal(["Mission 1", "Point training-m1-00", "Point training-m1-04", "Mission 2", "Point training-m2-00"],
            training.Select(l => l.Ligne.Nom));
        // sous son surtitre, un titre « Mission 1 · ... » perd sa mission
        Assert.Equal("Route du bateau", DefisPanelService.SansLeGroupe("Mission 1 · Route du bateau", "Mission 1"));
        Assert.Equal("マリンダイバー", DefisPanelService.SansLeGroupe("ミッション1・マリンダイバー", "ミッション1"));
        Assert.Equal("Mission 1", DefisPanelService.SansLeGroupe("Mission 1", "Mission 1"));
        Assert.Equal("Rambo", DefisPanelService.SansLeGroupe("Rambo", null));
        Assert.Equal([true, false, false, true, false], training.Select(l => l.Ligne.Groupe));
        // ni record ni temps : le nombre de seances, et la marque « tente » (jamais « reussi »)
        var m104 = training[2].Ligne;
        Assert.Equal(("", "3", "tente"), (m104.Colonne1, m104.Colonne2, m104.Marque));
        Assert.Equal(("", "", ""), (training[1].Ligne.Colonne1, training[1].Ligne.Colonne2, training[1].Ligne.Marque));
        // le Boss Rush garde son onglet et son groupe
        Assert.Equal(["Boss Rush", "Mission 1 : Huge Hermit"], lignes[DefisPanelModel.Onglet.Challenges].Select(l => l.Ligne.Nom));
    }

    [Fact]
    public void Un_classement_au_nom_d_un_point_d_entrainement_ne_fait_jamais_de_record()
    {
        var r = Point("training-m1-00", 100, 0, 0);
        var tableaux = new Dictionary<string, ClassementDEpreuve>
        {
            [r.Ruleset] = new ClassementDEpreuve(1, [new TempsClasse(1, "player", 1000)]),
        };
        Assert.Empty(Service().ComposerLesRecords([r], tableaux, "en", "player"));
    }

    [Fact]
    public void Les_pages_de_l_epreuve_disent_TRAINING_et_jamais_une_tentative_classee()
    {
        var r = Point("training-m1-04", 106, 0, 4);
        var service = Service();
        var preparation = service.ComposerLaPreparation(r, "Metal Slug 3", null, false, "en");
        Assert.Equal(("TRAINING", "FREE PRACTICE"), (preparation.Mot, preparation.Entete));
        Assert.Contains(preparation.Fiche!, c => c is { Etiquette: "Game", Valeur: "Normal: lives and continues" });
        Assert.DoesNotContain(preparation.Fiche!, c => c.Etiquette is "Record" or "You");

        // un temps fini : affiche, jamais envoye ; le bilan n'attend aucun verdict
        var bilan = service.BilanDe(r, "Metal Slug 3", null, new BilanDeSession("s", r.ChallengeId, 3, 1, 4000, SessionSpeedrun.Finished, null), sale: false);
        Assert.Equal((IssueDuBilan.NonEnvoye, 4000L), (bilan.Issue, bilan.Temps));
        var fin = service.ComposerLeBilan(bilan, "en");
        Assert.Equal("NOT SENT", fin.Titre);
        Assert.Equal("TRAINING", fin.MonScore!.Ruban);
        // sans temps fini : rien a dire de plus
        var rien = service.BilanDe(r, "Metal Slug 3", null, new BilanDeSession("s", r.ChallengeId, 1, 0, null, SessionSpeedrun.Aborted, "game_closed"), sale: false);
        Assert.Equal(IssueDuBilan.AucunTemps, rien.Issue);
    }

    [Fact]
    public void Un_point_d_entrainement_n_a_pas_d_image_de_bandeau()
    {
        var images = ImagesDesDefis.Pour([Point("training-m1-00", 100, 0, 0)], @"C:\jeux\mslug3.zip", @"C:\nulle-part", _ => @"C:\jeux\image.png");
        Assert.Empty(images);
    }

    // ── Les reglements du Data Pack ──

    public static IEnumerable<object[]> PointsDuDataPack()
        => Directory.GetFiles(Dossier, "metal-slug-3.training-*.json").Order(StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>
    /// L'APERCU DE L'ONGLET TRAINING (vrai rendu GDI+) : ecrit dans NELFEPLAY_APERCUS s'il est donne, avec les textes fr de
    /// NELFEPLAY_APERCUS_TEXTES (interface-texts.json du Data Pack) ; sinon, verifie seulement que le dessin aboutit.
    /// </summary>
    [Fact]
    public void Apercu_de_l_onglet_TRAINING_avec_les_points_du_Data_Pack()
    {
        var points = PointsDuDataPack().Select(o => Reglement.Lire(File.ReadAllText(Path.Combine(Dossier, (string)o[0])))).ToList();
        var textes = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_TEXTES") is { Length: > 0 } chemin
            ? new RetroBat.Api.Infrastructure.InterfaceTextService(chemin)
            : null;
        var panneau = new DefisPanelService(null!, null!, null!,
            new RetroBat.Api.Leaderboard.LeaderboardOverlayService(Microsoft.Extensions.Logging.Abstractions.NullLogger<RetroBat.Api.Leaderboard.LeaderboardOverlayService>.Instance),
            null!, textes: textes)
        {
            LangueDApercu = textes is null ? "en" : "fr",
        };
        var contenu = panneau.OuvrirPourEssai(points);
        Assert.Equal("TRAINING", contenu.Onglets[contenu.OngletCourant]);
        Assert.False(contenu.Bandeaux);
        Assert.NotEmpty(contenu.Bandeau);
        var png = RetroBat.Api.Leaderboard.LeaderboardOverlayService.ApercuDefis(RetroBat.Api.Leaderboard.EsMenuStyle.Lire(null), contenu, null!);
        Assert.NotEmpty(png);
        if (Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS") is { Length: > 0 } sortie)
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(sortie).FullName, "entrainement-ms3.png"), png);
    }

    [Fact]
    public void Le_Data_Pack_a_les_points_d_entree_des_cinq_missions()
    {
        var points = PointsDuDataPack().Select(o => Reglement.Lire(File.ReadAllText(Path.Combine(Dossier, (string)o[0])))).ToList();
        Assert.True(points.Count >= 40, $"{points.Count} points");
        Assert.Equal(5, points.Select(p => p.Groupe("en")).Distinct().Count());
        Assert.Equal(points.Count, points.Select(p => p.Ordre).Distinct().Count());
        Assert.Equal(points.Count, points.Select(p => p.Ruleset).Distinct().Count());
        Assert.Equal(points.Count, points.Select(p => p.Etat!.Sha256).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(PointsDuDataPack))]
    public void Chaque_point_du_Data_Pack_est_un_entrainement_conforme_au_MEM_des_challenges(string fichier)
    {
        var r = Reglement.Lire(File.ReadAllText(Path.Combine(Dossier, fichier)));
        Assert.True(r.EstUnEntrainement);
        Assert.Equal("metal-slug-3." + Path.GetFileNameWithoutExtension(fichier)["metal-slug-3.".Length..], r.ChallengeId);
        Assert.True(r.Ruleset.Length <= 32);
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.NotNull(r.Etat);
        Assert.StartsWith("arcade/metal-slug-3/entrainement-", r.Etat!.Fichier);
        Assert.Empty(r.VerifierContre(File.ReadAllBytes(Path.Combine(Dossier, "metal-slug-3.v5.MEM"))));
        Assert.Empty(r.Verrous);
        var fin = Assert.Single(r.Independant!.Arrivee.Evenements);
        Assert.Equal((0x10040CL, r.Depart[1].Egal + 1, r.Depart[1].Egal), (fin.Adresse!.Value, fin.Valeur, fin.Quitte));
        Assert.Empty(r.Echecs);
        Assert.Empty(r.AnnulationsJeu);
        Assert.Empty(r.AnnulationsBorne);
        foreach (var langue in new[] { "fr", "en", "es", "ja", "ko", "zh" })
        {
            Assert.True(r.Titres.ContainsKey(langue), langue);
            Assert.True(r.Resumes.ContainsKey(langue), langue);
            Assert.True(r.Groupes.ContainsKey(langue), langue);
        }
    }
}
