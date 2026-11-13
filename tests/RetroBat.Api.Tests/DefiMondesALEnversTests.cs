using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Recording;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// METAL SLUG 3, « LES MONDES A L'ENVERS » (mode choisi par le user le 13/11, wrapper 0.346). Meme depart, meme arrivee, meme
/// .MEM que Rambo (etat au parachute, chrono au poser du soldat, fin de la mission 1) ; quatre verrous (caillou, cailloux
/// infinis, jauge de nourriture) et un bloc « rules » que le wrapper evalue avant chaque image : chaque caillou lance a pied
/// fait passer au monde suivant (Normal, Rage rouge, Mars attaque, Grand froid), avec teinte, musique, arme, personnage et
/// monstres ; boss en monde normal. L'API ne joue pas les regles : elle les transporte (certified.txt, attestation, bilan,
/// replay) et en prouve l'empreinte.
/// </summary>
public class DefiMondesALEnversTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom)));

    private static readonly Reglement Mondes = Lire("metal-slug-3.mondes-a-l-envers.json");
    private static readonly Reglement Rambo = Lire("metal-slug-3.mission-1-rambo.json");

    private static int _ids;

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    [Fact]
    public void Meme_depart_meme_arrivee_meme_MEM_que_Rambo()
    {
        Assert.Equal(("metal-slug-3.mondes-a-l-envers", "ch-ms3-mondes-v1", 1, 3, "challenge_mode"),
            (Mondes.ChallengeId, Mondes.Ruleset, Mondes.Version, Mondes.Ordre, Mondes.Kind));
        Assert.True(Mondes.EstUnChallenge);
        Assert.Equal(Rambo.MemSha256, Mondes.MemSha256);
        Assert.Equal(Rambo.Etat!.Sha256, Mondes.Etat!.Sha256);
        Assert.Equal("arcade/metal-slug-3/mission-1-mondes.state", Mondes.Etat.Fichier);
        Assert.Empty(Mondes.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.v5.MEM"))));
        Assert.Equal("Les mondes à l'envers", Mondes.Titre("fr"));
        foreach (var langue in new[] { "fr", "en", "es", "ja", "ko", "zh" })
        {
            Assert.NotEqual(Mondes.ChallengeId, Mondes.Titre(langue));
            Assert.False(string.IsNullOrWhiteSpace(Mondes.Resume(langue)));
            Assert.DoesNotContain((char)0x2014, Mondes.Resume(langue)!);   // jamais de tiret cadratin
        }
    }

    [Fact]
    public void Les_verrous_tiennent_le_caillou_les_cailloux_infinis_et_la_jauge()
        => Assert.Equal("0x10EAF4,1,value,0x03;0x10EAF5,1,value,0x0A;0x10EAF6,1,value,0x00;0x10EAF7,1,value,0x00", Mondes.LignesDesVerrous);

    [Fact]
    public void Les_regles_partent_sur_une_ligne_avec_leur_empreinte()
    {
        var ligne = Mondes.LigneDesRegles!;
        Assert.StartsWith("rules={", ligne, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', ligne);
        Assert.DoesNotContain('\r', ligne);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Mondes.Regles!))).ToLowerInvariant(),
            Mondes.ReglesSha256);
        // Le bloc transporte est le bloc du reglement, au JSON compact pres.
        var doc = JsonDocument.Parse(File.ReadAllText(Fichier("metal-slug-3.mondes-a-l-envers.json")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(doc.RootElement.GetProperty("rules").GetRawText()), JsonNode.Parse(Mondes.Regles!)));
        var regles = JsonNode.Parse(Mondes.Regles!)!["rules"]!.AsArray().Select(r => (string)r!["id"]!).ToList();
        Assert.Equal(["depart", "section", "fin_partie", "boss", "fin_boss", "coup", "corps", "lancer", "naissance", "mort_soldat",
            "mort_zombie", "teinte_tenue", "musique_tenue", "arme_tenue", "arme_rendue", "personnage"], regles);
    }

    [Fact]
    public void Sans_regle_rien_ne_change()
    {
        Assert.Null(Rambo.Regles);
        Assert.Null(Rambo.LigneDesRegles);
        Assert.Equal("", Rambo.ReglesSha256);
    }

    [Fact]
    public void Certified_porte_les_verrous_la_couche_et_les_regles()
    {
        var lignes = Mondes.Verrous.Select(v => v.Ligne).ToList();
        lignes.Add(VerrousDuLancement.CoucheDesChallenges);
        lignes.Add(Mondes.LigneDesRegles!);
        var texte = new StringBuilder();
        VerrousDuLancement.Ecrire(texte, lignes);
        var sortie = texte.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["lock.00=0x10EAF4,1,value,0x03", "lock.01=0x10EAF5,1,value,0x0A", "lock.02=0x10EAF6,1,value,0x00",
            "lock.03=0x10EAF7,1,value,0x00", "mem_layer=defis", Mondes.LigneDesRegles!], sortie);
    }

    private static string Modifier(Action<JsonNode> changer)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fichier("metal-slug-3.mondes-a-l-envers.json")))!;
        changer(doc);
        return doc.ToJsonString();
    }

    [Fact]
    public void Une_regle_n_ecrit_jamais_une_adresse_que_le_reglement_lit()
    {
        // L'ecriture des munitions (u16 0x10EAFA) deplacee sur 0x10040B : elle couvrirait 0x10040C (la mission, l'arrivee).
        var faux = Modifier(d =>
        {
            var arme = d["rules"]!["rules"]!.AsArray().First(r => (string)r!["id"]! == "arme_tenue")!;
            arme["do"]![1]!["address"] = "0x10040B";
        });
        var ex = Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(faux));
        Assert.Contains("0x10040C", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Des_regles_partent_d_un_etat_et_ne_vont_qu_a_un_challenge()
    {
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Modifier(d => d["kind"] = "speedrun_full_game")));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Modifier(d => d["rules"]!.AsObject().Remove("rules"))));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Modifier(d => { d["start_policy"] = "normal_boot"; d.AsObject().Remove("state"); })));
    }

    private static string Bilan(string? sha, bool valides = true, long depuis = 120, long manques = 0)
    {
        var o = new JsonObject
        {
            ["frame_count"] = 5000,
            ["locks"] = Mondes.LignesDesVerrous,
            ["lock_frames"] = new JsonArray(Mondes.Verrous.Select(v => (JsonNode)new JsonObject
            {
                ["lock"] = v.Ligne, ["valid"] = true, ["applied"] = 4880, ["restored"] = 12, ["missed"] = 0, ["from_frame"] = 120,
            }).ToArray()),
        };
        if (sha is not null)
        {
            o["rules_sha256"] = sha;
            o["rules_valid"] = valides;
            o["rules_error"] = valides ? "" : "constante inconnue : X";
            o["rules_from_frame"] = depuis;
            o["rules_ticks"] = 4880;
            o["rules_missed"] = manques;
        }
        return o.ToJsonString();
    }

    [Fact]
    public void Le_bilan_prouve_les_regles()
    {
        Assert.Null(PasseportDeDefi.EcartDesVerrous(Bilan(Mondes.ReglesSha256), Mondes));
        Assert.Contains("regles lues", PasseportDeDefi.EcartDesVerrous(Bilan(null), Mondes)!);           // wrapper 0.345
        Assert.Contains("regles lues", PasseportDeDefi.EcartDesVerrous(Bilan(new string('0', 64)), Mondes)!);
        Assert.Contains("refusees", PasseportDeDefi.EcartDesVerrous(Bilan(Mondes.ReglesSha256, valides: false), Mondes)!);
        Assert.Contains("jamais armees", PasseportDeDefi.EcartDesVerrous(Bilan(Mondes.ReglesSha256, depuis: -1), Mondes)!);
        Assert.Contains("hors de la memoire", PasseportDeDefi.EcartDesVerrous(Bilan(Mondes.ReglesSha256, manques: 3), Mondes)!);
    }

    [Fact]
    public void Des_regles_au_bilan_d_un_defi_qui_n_en_a_pas_l_ecartent()
        => Assert.Contains("sans regle", PasseportDeDefi.EcartDesRegles(JsonNode.Parse("{\"rules_sha256\":\"" + new string('a', 64) + "\"}")!.AsObject(), Rambo)!);

    [Fact]
    public void Un_defi_sans_regle_garde_son_bilan_d_avant()
        => Assert.Null(PasseportDeDefi.EcartDesRegles(JsonNode.Parse("{\"locks\":\"\"}")!.AsObject(), Rambo));

    private static ReplayManifest Manifeste(string? verrous, string? regles) => new(
        ReplayManifest.SchemaId, "rp_1", "sess_1", new ReplayGame("arcade/mslug3", "arcade", "metal-slug-3", null, "f71f8c4c"),
        DateTime.UtcNow, "home",
        new ReplayRuntime("nelfe-arcade-r1", "1.22.2", null, null, null, null, "bsv", "fbneo", null, verrous, regles),
        new ReplayObjectRef(new string('a', 64), 10), new ReplayFrames(0, null, null, 100, 59.185606), null, new ReplayRecovery(false));

    [Fact]
    public void Le_replay_garde_et_rend_les_regles()
    {
        Assert.Equal(Mondes.Regles, ReplayRecorderService.ReglesDeLAttestation(JsonSerializer.SerializeToElement(new { Rules = Mondes.Regles })));
        Assert.Null(ReplayRecorderService.ReglesDeLAttestation(JsonSerializer.SerializeToElement(new { Rules = "" })));
        Assert.Null(ReplayRecorderService.ReglesDeLAttestation(JsonSerializer.SerializeToElement(new { WrapperVersion = "0.345.0.0" })));
        var lignes = LectureAvecVerrous.Lignes(Manifeste(Mondes.LignesDesVerrous, Mondes.Regles));
        Assert.Equal(5, lignes.Count);
        Assert.Equal(Mondes.LigneDesRegles, lignes[^1]);
        Assert.Equal(4, LectureAvecVerrous.Lignes(Manifeste(Mondes.LignesDesVerrous, null)).Count);
        var ancien = JsonSerializer.Deserialize<ReplayRuntime>("""{"RuntimeId":"nelfe-arcade-r1","ReplayFormat":"bsv","Locks":"0x1,1,value,0x01"}""");
        Assert.Null(ancien!.Rules);
    }

    [Fact]
    public void Un_replay_a_regles_sans_wrapper_0_346_n_est_pas_lu()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "mondes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "fbneo_libretro.dll"), "pas un wrapper");
            var certifie = Path.Combine(dossier, "certified.txt");
            var (_, erreur) = LectureAvecVerrous.Preparer(Manifeste(null, Mondes.Regles), @"E:\cores\fbneo_libretro.dll", @"E:\roms\mslug3.zip",
                new System.Diagnostics.ProcessStartInfo(), dossier, certifie);
            Assert.Equal(ReplayErrorCode.RuntimeIncompatible, erreur);
            Assert.False(File.Exists(certifie));
        }
        finally { try { Directory.Delete(dossier, true); } catch (IOException) { } }
    }

    /// <summary>Le site sert les reglements en JCS (cles triees) : le bloc relu garde ses regles, dans le meme ordre.</summary>
    [Fact]
    public void Le_reglement_servi_par_le_site_garde_ses_regles()
    {
        static JsonNode? Trier(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Trier(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Trier).ToArray()),
            _ => n?.DeepClone(),
        };
        var jcs = Trier(JsonNode.Parse(File.ReadAllText(Fichier("metal-slug-3.mondes-a-l-envers.json"))))!.ToJsonString();
        var servi = Reglement.Lire(jcs);
        Assert.NotNull(servi.Regles);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Mondes.Regles!)!["rules"], JsonNode.Parse(servi.Regles!)!["rules"]));
    }

    [Fact]
    public void La_mission_1_finie_est_classee_et_un_continue_annule()
    {
        string Jouer(bool continuer)
        {
            var s = new SessionSpeedrun(Mondes.ChallengeId, Mondes, () => "m" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
            s.RecevoirImage([S(800, "GAME_START", "0x10FDB6", 0x01)]);
            s.RecevoirImage([S(803, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
            s.RecevoirImage([S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
            foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
            s.RecevoirImage([S(1005, "MOUNT_STATE", "0x1002C1", 0x01)]);
            if (continuer) s.RecevoirImage([S(5000, "CONTINUE_SCREEN", "0x10FDB6", 0x02)]);
            s.RecevoirImage([S(13000, "PROGRESSION_STAGE", "0x10040D", 0x04)]);
            s.RecevoirImage([S(16000, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
            return s.Etat;
        }
        Assert.Equal("FINISHED", Jouer(false));
        Assert.NotEqual("FINISHED", Jouer(true));
    }
}
