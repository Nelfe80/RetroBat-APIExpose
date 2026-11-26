using RetroBat.Api.Replay.Models;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// (26/11) LES FANTOMES DU MODE TRAINING DES SHMUPS SOUS FBNEO (DaiOuJou) : l'identite d'arcade, le choix des replays par
/// version du jeu, le controle du coeur enregistreur et du contenu, la fiche du panneau (meme selecteur que les speedruns).
/// </summary>
public class FantomesTrainingFbneoTests
{
    /// <summary>Le point d'entrainement tel que le Data Pack le portera (dodonpachi-daioujou.training-fantomes.json).</summary>
    private const string Ruleset = """
    {
      "schema": "nelfe.challenge.v2", "challenge_id": "dodonpachi-daioujou.training-fantomes", "version": 1,
      "ruleset": "ch-ddpdoj-tr-fantomes-v1", "order": 100, "kind": "training",
      "group": { "fr": "Fantomes", "en": "Ghosts" },
      "title": { "fr": "Fantomes", "en": "Ghosts" },
      "summary": { "en": "Normal game with the ghosts of your record and the world record; nothing is ranked or sent" },
      "game": { "system_id": "arcade", "rom_group": "dodonpachi-daioujou" },
      "start_policy": "normal_boot",
      "ghosts": { "engine": "fbneo", "crc": "be7be29b", "game": "dodonpachi-daioujou", "rules": ["1cc", "1cc-black-label"] },
      "start": [],
      "timing": { "official": "independent", "independent": {
        "start": { "event": { "action": "GAME_MODE", "address": "0x3810" } },
        "finish": { "event": { "action": "PROGRESSION_STAGE", "address": "0xD56C" } } } },
      "void": [],
      "ranking": { "direction": "lower_better", "metric": "frames" },
      "cores": [ "fbneo" ],
      "runtime": { "frame_rate": 59.185606 }
    }
    """;

    private static ReplayManifest Manifeste(string? coeur, string? nomDuCoeur, IReadOnlyList<ReplayRomFile>? fichiers)
        => new(ReplayManifest.SchemaId, "rp_x", "s", new ReplayGame("ddpdojblka", "arcade", "dodonpachi-daioujou", null, null),
            DateTime.UtcNow, "local",
            new ReplayRuntime("r", "1.22", null, coeur, null, null, "bsv2", nomDuCoeur, fichiers),
            new ReplayObjectRef(new string('a', 64), 1), new ReplayFrames(0, 0, 100, 100, 59.18), null, new ReplayRecovery(false));

    private static readonly ReplayRomFile[] Ddpdojblka = [new("ddb_1dot.u45", "265f26cd"), new("ddp3blk_defaults.nv", "a1651904")];
    private const string Abea = "abea9d8195e1435f366aa0def3401edd546ffec85c5fc3e7e8725f600dcc6415";

    [Fact]
    public void L_identite_d_arcade_est_le_crc32_de_la_liste_triee_des_crc32_des_fichiers()
    {
        Assert.Equal("be7be29b", FantomesDuSpeedrun.CrcDArcade(["265f26cd", "a1651904"]));
        Assert.Equal("be7be29b", FantomesDuSpeedrun.CrcDArcade(["a1651904", "265F26CD"]));
    }

    [Fact]
    public void Le_reglement_du_training_dit_ses_fantomes()
    {
        var r = Reglement.Lire(Ruleset);
        Assert.True(r.EstUnEntrainement);
        Assert.True(FantomesDuSpeedrun.PourLeTraining(r));
        Assert.False(FantomesDuSpeedrun.PourCeDefi(r));
        Assert.Equal("be7be29b", r.FantomesDeTraining!.Crc);
        Assert.Equal(["1cc", "1cc-black-label"], r.FantomesDeTraining.Regles);
        Assert.Equal("normal_boot", r.StartPolicy);
    }

    [Fact]
    public void Des_fantomes_ne_vont_qu_a_un_point_d_entrainement_et_le_crc_est_verifie()
    {
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Ruleset.Replace("\"kind\": \"training\"", "\"kind\": \"challenge_mode\"")));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Ruleset.Replace("be7be29b", "zz")));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Ruleset.Replace("\"engine\": \"fbneo\"", "\"engine\": \"mame\"")));
    }

    [Fact]
    public void Un_replay_n_est_propose_que_du_meme_fbneo_et_du_meme_contenu()
    {
        Assert.Null(FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "fbneo", Ddpdojblka), "be7be29b"));
        Assert.Equal("no_core", FantomesDuSpeedrun.ReplayRefuse(Manifeste(null, "fbneo", Ddpdojblka), "be7be29b"));
        Assert.Equal("other_core", FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "mame2003", Ddpdojblka), "be7be29b"));
        Assert.Equal("other_game", FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "fbneo", [new("autre.rom", "11111111")]), "be7be29b"));
        // contenu inconnu (manifeste ancien) : on ne devine pas
        Assert.Equal("other_game", FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "fbneo", null), "be7be29b"));
    }

    [Fact]
    public void Par_defaut_seul_le_fbneo_officiel_de_la_borne_est_admis()
    {
        Assert.Equal([Abea], FantomesDuSpeedrun.OfficielsAdmis.ToArray());
        Assert.Null(FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "fbneo", Ddpdojblka), "be7be29b"));
        Assert.Equal("other_core", FantomesDuSpeedrun.ReplayRefuse(Manifeste(new string('c', 64), "fbneo", Ddpdojblka), "be7be29b"));
    }

    [Fact]
    public void Un_autre_fbneo_est_admis_au_training_sous_controle_de_ses_points()
    {
        var autre = "800f25e9f9f0ace6191321cff02b985663bf84568352b29ca68325e94fb3446e";
        // un autre FBNeo officiel : admis si le .nghost porte ses points de controle (le coeur hote verifie en course)
        Assert.Null(FantomesDuSpeedrun.ReplayRefuseAuTraining(Manifeste(autre, "fbneo", Ddpdojblka), "be7be29b", portePoints: true));
        Assert.Equal("no_checkpoint", FantomesDuSpeedrun.ReplayRefuseAuTraining(Manifeste(autre, "fbneo", Ddpdojblka), "be7be29b", portePoints: false));
        // le FBNeo de la borne, avec ou sans points
        Assert.Null(FantomesDuSpeedrun.ReplayRefuseAuTraining(Manifeste(Abea, "fbneo", Ddpdojblka), "be7be29b", portePoints: false));
        // pas un FBNeo, autre contenu, coeur inconnu : refuses comme avant
        Assert.Equal("other_core", FantomesDuSpeedrun.ReplayRefuseAuTraining(Manifeste(autre, "mame2003", Ddpdojblka), "be7be29b", portePoints: true));
        Assert.Equal("other_game", FantomesDuSpeedrun.ReplayRefuseAuTraining(Manifeste(autre, "fbneo", [new("autre.rom", "11111111")]), "be7be29b", portePoints: true));
        Assert.Equal("no_core", FantomesDuSpeedrun.ReplayRefuseAuTraining(Manifeste(null, "fbneo", Ddpdojblka), "be7be29b", portePoints: true));
    }

    [Fact]
    public void Un_fbneo_non_admis_est_ecarte_quand_la_liste_est_donnee()
    {
        var avant = FantomesDuSpeedrun.OfficielsAdmis;
        try
        {
            FantomesDuSpeedrun.OfficielsAdmis = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { new('b', 64) };
            Assert.Equal("other_core", FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "fbneo", Ddpdojblka), "be7be29b"));
            FantomesDuSpeedrun.OfficielsAdmis = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Abea.ToUpperInvariant() };
            Assert.Null(FantomesDuSpeedrun.ReplayRefuse(Manifeste(Abea, "fbneo", Ddpdojblka), "be7be29b"));
        }
        finally { FantomesDuSpeedrun.OfficielsAdmis = avant; }
    }

    private static IReadOnlyList<(string Regle, IReadOnlyList<(string Joueur, string? Replay)> Lignes)> Classements() =>
    [
        ("1cc", new (string, string?)[] { ("Tails", "rp_a1"), ("Moi", "rp_a2"), ("Amy", null) }),
        ("1cc-black-label", new (string, string?)[] { ("Knuckles", null), ("Shadow", "rp_b1"), ("Moi", "rp_b2") }),
    ];

    [Fact]
    public void Par_defaut_mon_record_et_le_record_mondial_de_chaque_version()
    {
        var voulus = FantomesDuSpeedrun.ChoisirPourLeTraining(Classements(), "moi", ChoixDesFantomes.MonRecordEtMondial);
        Assert.Equal(["rp_a2", "rp_a1", "rp_b2", "rp_b1"], voulus.Select(v => v.Replay).ToArray());
        Assert.Equal(["1cc", "1cc", "1cc-black-label", "1cc-black-label"], voulus.Select(v => v.Regle).ToArray());
        Assert.Equal(["me", "world", "me", "world"], voulus.Select(v => v.Role).ToArray());
    }

    [Fact]
    public void Le_selecteur_choisit_mon_record_le_mondial_ou_rien()
    {
        Assert.Equal(["rp_a2", "rp_b2"], FantomesDuSpeedrun.ChoisirPourLeTraining(Classements(), "Moi", ChoixDesFantomes.MonRecord).Select(v => v.Replay).ToArray());
        Assert.Equal(["rp_a1", "rp_b1"], FantomesDuSpeedrun.ChoisirPourLeTraining(Classements(), "Moi", ChoixDesFantomes.RecordMondial).Select(v => v.Replay).ToArray());
        Assert.Empty(FantomesDuSpeedrun.ChoisirPourLeTraining(Classements(), "Moi", ChoixDesFantomes.Aucun));
    }

    [Fact]
    public void Mon_record_qui_est_le_record_mondial_ne_compte_qu_une_fois()
    {
        var tete = new[] { ("1cc", (IReadOnlyList<(string, string?)>)new (string, string?)[] { ("Moi", "rp_m"), ("Tails", "rp_t") }) };
        var seul = Assert.Single(FantomesDuSpeedrun.ChoisirPourLeTraining(tete, "Moi", ChoixDesFantomes.MonRecordEtMondial));
        Assert.Equal(("rp_m", "me"), (seul.Replay, seul.Role));
        // sans pseudo, pas de record « mien » : seul le mondial reste
        Assert.Empty(FantomesDuSpeedrun.ChoisirPourLeTraining(tete, "", ChoixDesFantomes.MonRecord));
    }

    [Fact]
    public void Quatre_fantomes_au_plus_et_aucun_fantome_dit_pourquoi()
    {
        var trois = new[] { "1cc", "a", "b" }.Select(r => (r, (IReadOnlyList<(string, string?)>)new (string, string?)[] { ("Z", "z" + r), ("Moi", "m" + r) })).ToList();
        Assert.Equal(4, FantomesDuSpeedrun.ChoisirPourLeTraining(trois, "Moi", ChoixDesFantomes.MonRecordEtMondial).Count);
        Assert.Equal("no_record", new PreparationDuTraining(0, []).MessageSansFantome);
        Assert.Equal("other_core", new PreparationDuTraining(0, ["other_core", "other_core", "other_game"]).MessageSansFantome);
        Assert.Equal("not_ready", new PreparationDuTraining(0, [], EnRetard: true).MessageSansFantome);
        Assert.Null(new PreparationDuTraining(2, ["other_core"]).MessageSansFantome);
    }

    [Fact]
    public void La_fiche_du_training_porte_la_case_des_fantomes_comme_les_speedruns_et_pas_de_redemarrage()
    {
        var f = new FantomesDuSpeedrun(null!, null!, null!, null!, null!);
        var service = new DefisPanelService(null!, null!, null!, null!, null!, fantomes: f) { LangueDApercu = "en" };
        var fiche = service.FicheDEntrainement(Reglement.Lire(Ruleset));
        Assert.Contains(fiche, c => c.Etiquette == "Ghosts" && c.Valeur == "My record + world record");
        Assert.DoesNotContain(fiche, c => c.Etiquette == "Restart");
        Assert.Contains(fiche, c => c.Valeur == "Normal boot");
    }

    [Fact]
    public void Les_messages_sans_fantome_sont_dits_au_joueur()
    {
        foreach (var raison in new[] { "other_core", "no_core", "other_game", "no_record" })
            Assert.StartsWith("No ghost", SpeedrunSessionService.MessageSansFantomeParDefaut(raison));
        Assert.StartsWith("Ghosts not ready", SpeedrunSessionService.MessageSansFantomeParDefaut("not_ready"));
    }
}
