using System.Text.Json;
using RetroBat.Api.Deck.Canal;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE CANAL v2 (1.9.213, wrapper 0.350, decision du user du 23/11 : debloquer Battle Garegga, Dangun Feveron et RayForce) : canal en
/// version 2 (meme disposition), profil <c>nelfeplay.view-profile/2</c> (jauges, distance precise, table d'ennemis unique, enemy
/// facultatif), et l'API qui ne demande le canal d'un .VIEW v2 qu'a un wrapper 0.350 ou plus. Les profils v1 ne changent pas (les
/// essais de CanalProfilsTests et CanalDuDeckTests le prouvent a l'octet).
/// </summary>
[Collection("CanalProfils")]
public class CanalV2Tests
{
    private static readonly string FichierDoj = Path.Combine(AppContext.BaseDirectory, "Profils", "ram", "arcade", "dodonpachi-daioujou.VIEW.json");

    private const string ProfilGaregga = """
        {
          "schema": "nelfeplay.view-profile/2",
          "id": "battle-garegga",
          "view": "battle-garegga",
          "roms": ["bgaregga"],
          "screen": { "w": 240, "h": 320 },
          "ship": { "x": { "scale": "10/1751", "offset": 0.65 }, "y": { "scale": "-10/1269", "offset": 322.0 } },
          "enemy": { "table": "ennemis", "column_x": "x", "column_y": "y", "column_type": "routine",
                     "x": { "scale": "1/64", "offset": -0.5 }, "y": { "scale": "-1/64", "offset": 320 }, "drawn_mask": "0x8000", "signed": true },
          "gauges": [ { "id": "rank", "field": "rang" }, { "id": "rank_rate", "field": "rank_cadence" } ],
          "distance": { "field": "distance", "scale": "1/512" }
        }
        """;

    [Fact]
    public void Un_profil_v2_se_lit_et_un_profil_v1_garde_ses_refus()
    {
        var p = CanalProfils.DepuisJson(ProfilGaregga, out var erreur);
        Assert.True(p is not null, erreur);
        Assert.Equal(2, p!.SchemaVersion);
        Assert.Equal("ennemis", p.TableEnnemis);
        Assert.Equal("routine", p.ColonneType);
        Assert.Equal("vie", p.ColonneVie);
        Assert.Equal([new CanalJauge("rank", "rang"), new CanalJauge("rank_rate", "rank_cadence")], p.Jauges);
        Assert.Equal(1.0 / 512.0, p.DistanceEchelle);
        Assert.False(p.SansEnnemis);

        // un profil v1 ne connait pas les cles v2 : refuse comme avant
        var v1 = ProfilGaregga.Replace("view-profile/2", "view-profile/1");
        Assert.Null(CanalProfils.DepuisJson(v1, out erreur));
        Assert.Contains("cle inconnue", erreur);
        Assert.Null(CanalProfils.DepuisJson(ProfilGaregga.Replace("view-profile/2", "view-profile/3"), out erreur));

        // v2 : enemy facultatif ; jauge en double refusee ; cle inconnue d'une jauge refusee
        var sans = CanalProfils.DepuisJson(ProfilGaregga.Replace("\"enemy\"", "\"enemy_\"").Replace("\"enemy_\": {", "\"comment\": \"x\", \"z\": {"), out _);
        Assert.Null(sans);   // une cle inconnue (z) reste refusee
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ProfilGaregga)!;
        json.Remove("enemy");
        var sansEnnemi = CanalProfils.DepuisJson(JsonSerializer.Serialize(json), out erreur);
        Assert.True(sansEnnemi is not null, erreur);
        Assert.True(sansEnnemi!.SansEnnemis);
        Assert.Null(CanalProfils.DepuisJson(ProfilGaregga.Replace("\"id\": \"rank_rate\"", "\"id\": \"rank\""), out erreur));
        Assert.Contains("double", erreur);
        Assert.Null(CanalProfils.DepuisJson(ProfilGaregga.Replace("\"field\": \"rang\"", "\"field\": \"rang\", \"scale\": 2"), out erreur));
        Assert.Contains("gauges", erreur);
        Assert.Null(CanalProfils.DepuisJson(ProfilGaregga.Replace("\"scale\": \"1/512\"", "\"scale\": 0"), out erreur));
        Assert.Contains("distance.scale", erreur);
    }

    [Fact]
    public void Le_format_du_VIEW_se_lit_a_cote_du_profil_ou_dans_le_dossier_officiel()
    {
        var racine = Path.Combine(Path.GetTempPath(), "vue_" + Guid.NewGuid().ToString("N"));
        try
        {
            var arcade = Path.Combine(racine, "arcade");
            var user = Path.Combine(racine, ".user", "arcade");
            Directory.CreateDirectory(arcade);
            Directory.CreateDirectory(user);
            File.WriteAllText(Path.Combine(arcade, "a.VIEW"), "﻿# commentaire\r\n  -- autre\r\nnelfeplay.view 2 # v2\r\nchamp x 0 u8\r\n");
            File.WriteAllText(Path.Combine(arcade, "b.VIEW"), "nelfeplay.view 1\nchamp x 0 u8\n");
            Assert.Equal(2, CanalProfils.FormatDeLaVue(Path.Combine(arcade, "a.VIEW.json"), "a"));
            Assert.Equal(1, CanalProfils.FormatDeLaVue(Path.Combine(arcade, "b.VIEW.json"), "b"));
            Assert.Equal(1, CanalProfils.FormatDeLaVue(Path.Combine(arcade, "c.VIEW.json"), "c"));        // pas de .VIEW : comme avant
            Assert.Equal(2, CanalProfils.FormatDeLaVue(Path.Combine(user, "a.VIEW.json"), "a"));          // essai .user, .VIEW officiel
            Assert.Equal(1, CanalProfils.FormatDeLaVue(null, "a"));
        }
        finally { try { Directory.Delete(racine, true); } catch { } }
    }

    [Fact]
    public void Le_canal_d_un_VIEW_v2_n_est_demande_qu_au_wrapper_0_350()
    {
        var racine = Path.Combine(Path.GetTempPath(), "profils_v2_" + Guid.NewGuid().ToString("N"));
        var avant = CanalProfils.DossierRam;
        var version = CanalDuDeck.VersionDuWrapper;
        try
        {
            var arcade = Path.Combine(racine, "arcade");
            Directory.CreateDirectory(arcade);
            File.Copy(FichierDoj, Path.Combine(arcade, "dodonpachi-daioujou.VIEW.json"));
            File.WriteAllText(Path.Combine(arcade, "dodonpachi-daioujou.VIEW"), "nelfeplay.view 1\nchamp distance 0x130CA u16le\n");
            File.WriteAllText(Path.Combine(arcade, "battle-garegga.VIEW.json"), ProfilGaregga);
            File.WriteAllText(Path.Combine(arcade, "battle-garegga.VIEW"), "nelfeplay.view 2\nchamp distance 0xC0 u32w\n");
            CanalProfils.DossierRam = racine;
            Assert.Equal(2, CanalProfilAffichage.Pour("bgaregga")!.FormatVue);
            Assert.Equal(1, CanalProfilAffichage.Pour("ddpdojblka")!.FormatVue);

            CanalDuDeck.VersionDuWrapper = () => 349;
            var refus = CanalDuDeck.RefusWrapperAncien;
            Assert.False(CanalDuDeck.Demander("bgaregga"));            // le 0.349 refuserait le .VIEW v2 : on ne pose pas canal=on
            Assert.Equal(refus + 1, CanalDuDeck.RefusWrapperAncien);
            Assert.False(CanalDuDeck.Voulu("bgaregga"));
            Assert.True(CanalDuDeck.Demander("ddpdojblka"));           // un .VIEW v1 : le 0.349 le lit, rien ne change
            CanalDuDeck.Retirer();

            CanalDuDeck.VersionDuWrapper = () => 350;
            Assert.True(CanalDuDeck.Demander("bgaregga"));
            Assert.True(CanalDuDeck.Voulu("bgaregga"));
            var texte = new System.Text.StringBuilder();
            CanalDuDeck.Ecrire(texte, "bgaregga");
            Assert.Equal("canal=on\n", texte.ToString());
            CanalDuDeck.Retirer();
        }
        finally
        {
            CanalDuDeck.VersionDuWrapper = version;
            CanalDuDeck.Retirer();
            CanalProfils.DossierRam = avant;
            try { Directory.Delete(racine, true); } catch { }
        }
    }

    [Fact]
    public void Un_canal_v2_se_lit_et_deck_live_porte_le_rang_la_distance_precise_et_les_ennemis_d_une_table_unique()
    {
        var champs = new[] { "rang", "rank_cadence", "distance", "vaisseau_x", "vaisseau_y", "vies", "niveau" };
        var tables = new[] { new CanalEcrivainSimule.Table("ennemis", 31, 31, ["routine", "drapeau", "y", "x"]) };
        using var e = new CanalEcrivainSimule("battle-garegga", champs, tables, 5, version: 2);
        Assert.Equal(2, CanalFormat.Version(e.Tampon));
        Assert.True(CanalFormat.TailleDeclaree(e.Tampon) > 0);
        var d = CanalFormat.LireDescripteur(e.Tampon)!;
        Assert.Equal(2, d.Version);

        var profil = CanalProfils.DepuisJson(ProfilGaregga, out _)!;
        var c = new CanalAffichageCalcul(profil, d, "bgaregga");
        Assert.True(c.Utile);
        // rang 24 bits 0xEF1234 ; distance 32 bits au-dela de 65 536 ; un ennemi dessine, un non dessine, un de type 0 (case libre)
        int Brut(double px) => (int)Math.Round((px + 0.5) * 64);
        e.Ecrire(10, [0xEF1234, 22, 600_000, 17510, 12690 * 10, 2, 1], [[
            [3, 0xDA2F8, 0x8000, (int)Math.Round((320 - 100) * 64.0), Brut(50)],
            [4, 0xDA2C8, 0x0000, 0, Brut(60)],
            [5, 0, 0x8000, (int)Math.Round((320 - 50) * 64.0), Brut(70)]]]);
        var bloc = new CanalBloc(d);
        Assert.True(CanalFormat.Lire(e.Tampon, d, bloc));
        var m = c.Convertir(bloc);
        Assert.Equal(600_000, m.Dist);
        Assert.Equal(Math.Round(600_000 / 512.0, 3), m.DistPx);
        Assert.Equal(0xEF1234, m.Gauges!["rank"]);
        Assert.Equal(22, m.Gauges["rank_rate"]);
        Assert.Equal([3, 50, 100, 3, 0], m.Enemies);        // seule la case 3 : dessinee, type non nul ; classe generique (3)
        var json = JsonSerializer.Serialize(m);
        Assert.Contains("\"Gauges\":{\"rank\":15667764,\"rank_rate\":22}", json);
        Assert.Contains("\"DistPx\":", json);

        // un changement du seul rang se publie (Different)
        e.Ecrire(11, [0xEF1234 - 22, 22, 600_000, 17510, 126900, 2, 1], [[[3, 0xDA2F8, 0x8000, (int)Math.Round(220 * 64.0), Brut(50)]]]);
        Assert.True(CanalFormat.Lire(e.Tampon, d, bloc));
        Assert.True(CanalAffichageCalcul.Different(m, c.Convertir(bloc)));
    }

    [Fact]
    public void Un_message_d_un_profil_v1_n_a_ni_DistPx_ni_Gauges()
    {
        var e = CanalScenarioSimule.DaiOuJou(9);
        var d = CanalFormat.LireDescripteur(e.Tampon)!;
        var c = new CanalAffichageCalcul(CanalProfils.DepuisJson(File.ReadAllText(FichierDoj), out _)!, d, "ddpdojblka");
        var bloc = new CanalBloc(d);
        e.Ecrire(1, new int[CanalScenarioSimule.Champs.Length], [[], [], []]);
        Assert.True(CanalFormat.Lire(e.Tampon, d, bloc));
        var json = JsonSerializer.Serialize(c.Convertir(bloc));
        Assert.DoesNotContain("DistPx", json);
        Assert.DoesNotContain("Gauges", json);
    }

    [Fact]
    public void Une_version_de_canal_inconnue_est_refusee()
    {
        using var e = new CanalEcrivainSimule("x", ["a"], [], 1, version: 3);
        Assert.Equal(-1, CanalFormat.TailleDeclaree(e.Tampon));
        Assert.Null(CanalFormat.LireDescripteur(e.Tampon));
    }
}
