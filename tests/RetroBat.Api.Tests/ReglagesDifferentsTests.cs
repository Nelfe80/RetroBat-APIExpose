using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>25/11/2026 : la liste des reglages qui different du profil, portee au site hors signature (carte APIExpose).</summary>
public sealed class ReglagesDifferentsTests
{
    private const string Coeur = "abea9d81" + "00000000000000000000000000000000000000000000000000000000";
    private const string Ancien = "800f25e9" + "00000000000000000000000000000000000000000000000000000000";

    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Usine(JsonElement profil, int i)
    {
        var v = profil.GetProperty("core_options_expected")[i].GetProperty("values");
        return string.Join(";", v.EnumerateObject().Select(p => p.Name + "=" + p.Value.GetString()).OrderBy(x => x, StringComparer.Ordinal));
    }

    private static readonly JsonElement Ms3 = Profil(
        "{\"core_options_expected\":[" +
        "{\"cores\":[\"" + Ancien + "\"],\"values\":{\"fbneo-dipswitch-mslug3-Difficulty\":\"4\",\"fbneo-neogeo-mode\":\"MVS\"}}," +
        "{\"cores\":[\"" + Coeur + "\"],\"values\":{\"fbneo-dipswitch-mslug3-Difficulty\":\"4\",\"fbneo-neogeo-mode\":\"MVS\"}}]}");

    private static string Filtre(string brut) => NelfePlayScoringReporter.FilterGameplayCoreOptions(brut)!;

    private static Dictionary<string, string> Paire(string s) => ReglagesDifferents.Paires(s);

    [Fact]
    public void Un_reglage_conforme_ne_donne_aucune_difference_et_reconstruit_l_empreinte()
    {
        var joue = Filtre("fbneo-dipswitch-mslug3-Difficulty=4;fbneo-neogeo-mode=MVS;fbneo-lowpass-filter=disabled");
        var j = ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, Ms3);
        Assert.Equal(0, j["count"]!.GetValue<int>());
        Assert.Equal(1, j["expected_entry"]!.GetValue<int>());   // l'entree du coeur 261102, pas celle du coeur precedent
        Assert.False(j["truncated"]!.GetValue<bool>());
        Assert.Equal(Sha(Usine(Ms3, 1)), j["core_options_digest"]!.GetValue<string>());
    }

    [Fact]
    public void MS3_cles_ajoutees_par_le_coeur_261102_sont_inconnues_du_profil_et_sans_defaut()
    {
        var joue = Filtre("fbneo-dipswitch-mslug3-Difficulty=4;fbneo-neogeo-mode=MVS;fbneo-dipswitch-mslug3-Region=Europe;fbneo-memcard-mode=disabled");
        var j = ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, Ms3);
        var e = j["entries"]!.AsArray();
        Assert.Equal(2, e.Count);
        Assert.Equal("fbneo-dipswitch-mslug3-Region", (string?)e[0]!["key"]);
        Assert.Equal("Europe", (string?)e[0]!["value"]);
        Assert.Null(e[0]!["expected"]);
        Assert.Null(e[0]!["default"]);
        Assert.Equal("fbneo-memcard-mode", (string?)e[1]!["key"]);
        Assert.False(j["defaults_known"]!.GetValue<bool>());
    }

    [Fact]
    public void Le_defaut_du_coeur_est_porte_quand_la_borne_le_connait()
    {
        var d = ReglagesDifferents.Calculer(Paire("fbneo-dipswitch-mslug3-Region=Europe"), Paire("fbneo-neogeo-mode=MVS"),
            Paire("fbneo-dipswitch-mslug3-Region=Europe"));
        var region = Assert.Single(d, x => x.Key.EndsWith("Region"));
        Assert.Equal("Europe", region.Default);
        Assert.Equal(region.Value, region.Default);   // ajoutee a sa valeur par defaut : le site l'admet
    }

    [Fact]
    public void Bomb_Jack_sans_entree_du_coeur_tout_est_inconnu_du_profil()
    {
        var bomb = Profil("{\"core_options_expected\":[{\"cores\":[\"ffff\"],\"values\":{\"fbneo-neogeo-mode\":\"MVS\"}}]}");
        var joue = Filtre("fbneo-dipswitch-bombjack-Lives=3;fbneo-dipswitch-bombjack-Bonus=10000");
        var j = ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, bomb);
        Assert.Null(j["expected_entry"]);
        Assert.Equal(2, j["entries"]!.AsArray().Count);
        Assert.All(j["entries"]!.AsArray(), n => Assert.Null(n!["expected"]));
        Assert.Null(ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, Profil("{}"))["expected_entry"]);
    }

    [Fact]
    public void Cinq_vies_au_lieu_de_trois_est_une_valeur_differente_de_l_attendue()
    {
        var profil = Profil("{\"core_options_expected\":[{\"cores\":[\"" + Coeur + "\"],\"values\":{\"fbneo-dipswitch-bombjack-Lives\":\"3\"}}]}");
        var joue = Filtre("fbneo-dipswitch-bombjack-Lives=5");
        var j = ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, profil);
        var e = Assert.Single(j["entries"]!.AsArray())!;
        Assert.Equal("5", (string?)e["value"]);
        Assert.Equal("3", (string?)e["expected"]);
        Assert.False(e["cheat"]!.GetValue<bool>());
    }

    [Fact]
    public void Une_cle_attendue_absente_de_la_borne_a_une_valeur_nulle()
    {
        var profil = Profil("{\"core_options_expected\":[{\"values\":{\"fbneo-dipswitch-x-Lives\":\"3\",\"fbneo-neogeo-mode\":\"MVS\"}}]}");
        var d = ReglagesDifferents.Calculer(Paire("fbneo-neogeo-mode=MVS"), Paire("fbneo-dipswitch-x-Lives=3;fbneo-neogeo-mode=MVS"));
        var e = Assert.Single(d);
        Assert.Null(e.Value);
        Assert.Equal("3", e.Expected);
        Assert.NotNull(ReglagesDifferents.EntreeAttendue(profil, Coeur, Paire("fbneo-neogeo-mode=MVS")));   // sans « cores » : vise tous les coeurs
    }

    [Fact]
    public void Une_option_de_triche_active_est_marquee()
    {
        var joue = Filtre("fbneo-cheat-3-batsugunsp-Always_have_Max_Weapon_PL1=1 - Always have Max Weapon PL1;fbneo-cheat-1-batsugunsp-Other=0 - Disabled");
        var j = ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, Profil("{}"));
        var parCle = j["entries"]!.AsArray().ToDictionary(n => (string)n!["key"]!, n => n!["cheat"]!.GetValue<bool>());
        Assert.True(parCle["fbneo-cheat-3-batsugunsp-Always_have_Max_Weapon_PL1"]);
        Assert.False(parCle["fbneo-cheat-1-batsugunsp-Other"]);
    }

    [Fact]
    public void Le_site_reconstruit_l_empreinte_signee_avec_l_usine_et_la_liste()
    {
        var joue = Filtre("fbneo-dipswitch-mslug3-Difficulty=6;fbneo-neogeo-mode=MVS;fbneo-dipswitch-mslug3-Region=Europe");
        var j = ReglagesDifferents.EnJson(joue, Sha(joue), Coeur, Ms3);
        var rebati = Paire(Usine(Ms3, j["expected_entry"]!.GetValue<int>()));
        foreach (var n in j["entries"]!.AsArray())
        {
            var cle = (string)n!["key"]!;
            if (n["value"] is null) rebati.Remove(cle); else rebati[cle] = (string)n["value"]!;
        }
        var chaine = string.Join(";", rebati.Select(p => p.Key + "=" + p.Value).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(j["core_options_digest"]!.GetValue<string>(), Sha(chaine));
    }

    [Fact]
    public void La_liste_est_bornee_et_dit_qu_elle_est_coupee()
    {
        var brut = string.Join(";", Enumerable.Range(0, 120).Select(i => $"fbneo-dipswitch-g-K{i:000}=1"));
        var j = ReglagesDifferents.EnJson(brut, "d", Coeur, Profil("{}"));
        Assert.Equal(ReglagesDifferents.MaxEntrees, j["entries"]!.AsArray().Count);
        Assert.Equal(120, j["count"]!.GetValue<int>());
        Assert.True(j["truncated"]!.GetValue<bool>());
        var longue = ReglagesDifferents.EnJson("fbneo-dipswitch-g-K=" + new string('x', 500), "d", Coeur, Profil("{}"));
        Assert.True(longue["truncated"]!.GetValue<bool>());
        Assert.True(((string)longue["entries"]![0]!["value"]!).Length <= ReglagesDifferents.MaxTexte);
    }

    [Fact]
    public void Sans_reglage_le_champ_est_vide_et_sain()
    {
        var j = ReglagesDifferents.EnJson("", "x", null, Profil("{}"));
        Assert.Empty(j["entries"]!.AsArray());
        Assert.Null(j["core_sha256"]);
        Assert.Equal(ReglagesDifferents.Version, j["v"]!.GetValue<int>());
    }

    [Fact]
    public void La_route_d_avant_la_soumission_a_un_delai_court_et_borne()
    {
        // 1.9.218 : la liste part AVANT la soumission ; un site muet ne doit pas retarder la fin de partie de tout le delai
        // du client (10 s), seulement de quelques secondes.
        Assert.InRange(NelfePlayScoringReporter.DelaiDesReglagesDifferents, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }
}
