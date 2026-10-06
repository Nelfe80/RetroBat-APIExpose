using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un mode dit par plusieurs drapeaux (2026-10-06) : Bubble Bobble a trois codes d'ecran titre, chacun
/// leve son octet (Original 0xE5D1, Power-Up 0xE5D2, Super 0xE5DB). Chaque mode a son classement ; le
/// profil dit les drapeaux qu'il couvre.
/// </summary>
public sealed class ModesParDrapeauxTests
{
    private static JsonElement Profil(string regle, int valeur, bool defaut, int original, int powerUp, int super)
    {
        var drapeaux = new System.Text.Json.Nodes.JsonObject { ["0xE5D1"] = original, ["0xE5D2"] = powerUp, ["0xE5DB"] = super };
        var mode = new System.Text.Json.Nodes.JsonObject { ["value"] = valeur, ["default"] = defaut, ["flags"] = drapeaux };
        var profil = new System.Text.Json.Nodes.JsonObject { ["ruleset"] = regle, ["mode"] = mode };
        return JsonDocument.Parse(profil.ToJsonString()).RootElement.Clone();
    }

    private static readonly JsonElement[] Profils =
    {
        Profil("1cc", 0, true, 0, 0, 0),
        Profil("1cc-original", 2, false, 1, 0, 0),
        Profil("1cc-power-up", 4, false, 0, 1, 0),
        Profil("1cc-super", 8, false, 0, 0, 1),
        JsonDocument.Parse("""{"ruleset":"1cc-multi"}""").RootElement.Clone(),
    };

    private static ContexteDeJeu Avec(params (string Adresse, int Valeur)[] drapeaux)
    {
        var c = ContexteDeJeu.Vide;
        foreach (var (a, v) in drapeaux) c = c.AvecDrapeau(a, v).AvecMode(v);
        return c;
    }

    [Fact]
    public void Les_profils_disent_quatre_modes()
    {
        var modes = ModesDeJeu.ModesParDrapeaux(Profils);
        Assert.Equal(new[] { 0, 2, 4, 8 }, modes.Select(m => m.Valeur));
    }

    [Theory]
    [InlineData("0XE5D2", 1, 4)]   // le wrapper ecrit 0XE5D2
    [InlineData("0xe5db", 1, 8)]   // le pont MAME ecrit 0xe5db
    [InlineData("0X00E5D1", 1, 2)]
    public void Un_drapeau_leve_donne_son_mode(string adresse, int valeur, int attendu)
    {
        var modes = ModesDeJeu.ModesParDrapeaux(Profils);
        Assert.Equal(attendu, ModesDeJeu.Resoudre(Avec((adresse, valeur)), modes).Mode);
    }

    [Fact]
    public void Un_drapeau_retombe_redonne_le_mode_normal()
    {
        var modes = ModesDeJeu.ModesParDrapeaux(Profils);
        var c = Avec(("0XE5DB", 1), ("0XE5DB", 0));
        Assert.Equal(0, ModesDeJeu.Resoudre(c, modes).Mode);
    }

    [Fact]
    public void Deux_codes_a_la_fois_n_ont_pas_de_classement()
    {
        var modes = ModesDeJeu.ModesParDrapeaux(Profils);
        var c = ModesDeJeu.Resoudre(Avec(("0XE5D2", 1), ("0XE5DB", 1)), modes);
        Assert.Equal(ModesDeJeu.SansClassement, c.Mode);
        // Et donc aucun profil : rien n'est soumis pour cette partie.
        Assert.Null(ModesDeJeu.ChoisirProfil(Profils, c.Mode));
    }

    [Fact]
    public void Sans_drapeau_mesure_c_est_le_mode_de_demarrage()
    {
        var modes = ModesDeJeu.ModesParDrapeaux(Profils);
        var c = ModesDeJeu.Resoudre(ContexteDeJeu.Vide, modes);
        Assert.Null(c.Mode);
        Assert.Equal("1cc", ModesDeJeu.ChoisirProfil(Profils, c.Mode)!.Value.GetProperty("ruleset").GetString());
    }

    [Fact]
    public void Le_profil_choisi_est_celui_du_mode_joue()
    {
        var modes = ModesDeJeu.ModesParDrapeaux(Profils);
        var c = ModesDeJeu.Resoudre(Avec(("0XE5D2", 1)), modes);
        Assert.Equal("1cc-power-up", ModesDeJeu.ChoisirProfil(Profils, c.Mode)!.Value.GetProperty("ruleset").GetString());
    }

    [Fact]
    public void Un_jeu_a_un_seul_octet_ne_change_pas()
    {
        // Tetris : 0xFFC0 = 0x37 (type A) / 0x77 (type B), profils sans drapeaux.
        var tetris = new[]
        {
            JsonDocument.Parse("""{"ruleset":"type-a","mode":{"value":55,"default":true}}""").RootElement.Clone(),
            JsonDocument.Parse("""{"ruleset":"type-b","mode":{"value":119}}""").RootElement.Clone(),
        };
        var modes = ModesDeJeu.ModesParDrapeaux(tetris);
        Assert.Empty(modes);
        var c = ModesDeJeu.Resoudre(Avec(("0XFFC0", 0x77)), modes);
        Assert.Equal(0x77, c.Mode);
        Assert.Equal("type-b", ModesDeJeu.ChoisirProfil(tetris, c.Mode)!.Value.GetProperty("ruleset").GetString());
    }

    [Fact]
    public void Une_ancienne_borne_ne_melange_jamais_un_mode_special_au_1cc()
    {
        // Avant le decodage, la borne prend la valeur lue (1) pour mode : aucun profil ne porte 1.
        Assert.Null(ModesDeJeu.ChoisirProfil(Profils, 1));
    }
}

/// <summary>Les modes se cumulent avec le 1CC MULTI (1cc-multi-super...), demande user 2026-10-06.</summary>
public sealed class MultiParModeTests
{
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement[] Profils =
    {
        P("""{"ruleset":"1cc","mode":{"value":0,"default":true,"flags":{"0xE5DB":0}}}"""),
        P("""{"ruleset":"1cc-super","mode":{"value":8,"flags":{"0xE5DB":1}}}"""),
        P("""{"ruleset":"1cc-multi","mode":{"value":0,"flags":{"0xE5DB":0}}}"""),
        P("""{"ruleset":"1cc-multi-super","mode":{"value":8,"flags":{"0xE5DB":1}}}"""),
    };

    [Theory]
    [InlineData("1cc-multi", true)]
    [InlineData("1cc-multi-super", true)]
    [InlineData("1cc-multiple", false)]
    [InlineData("1cc", false)]
    [InlineData("1cc-super", false)]
    public void Un_multi_se_reconnait_a_son_prefixe(string regle, bool multi)
        => Assert.Equal(multi, ModesDeJeu.EstMulti(regle));

    [Fact]
    public void Une_partie_seule_ne_va_jamais_a_un_multi()
    {
        Assert.Equal("1cc-super", ModesDeJeu.ChoisirProfil(Profils, 8)!.Value.GetProperty("ruleset").GetString());
        Assert.Equal("1cc", ModesDeJeu.ChoisirProfil(Profils, null)!.Value.GetProperty("ruleset").GetString());
    }

    [Theory]
    [InlineData(8, "1cc-multi-super")]
    [InlineData(0, "1cc-multi")]
    [InlineData(null, "1cc-multi")]
    public void Une_partie_a_plusieurs_va_au_multi_de_son_mode(int? mode, string attendu)
        => Assert.Equal(attendu, ModesDeJeu.ChoisirProfilMulti(Profils, mode)!.Value.GetProperty("ruleset").GetString());

    [Fact]
    public void Un_mode_sans_multi_ne_soumet_rien()
        => Assert.Null(ModesDeJeu.ChoisirProfilMulti(Profils, 4));

    [Fact]
    public void Un_seul_multi_sans_mode_vaut_pour_tous_les_modes()
    {
        // Metal Slug 3 : un 1CC et un 1CC MULTI, sans modes.
        var ms3 = new[] { P("""{"ruleset":"1cc"}"""), P("""{"ruleset":"1cc-multi"}""") };
        Assert.Equal("1cc-multi", ModesDeJeu.ChoisirProfilMulti(ms3, null)!.Value.GetProperty("ruleset").GetString());
        Assert.Equal("1cc", ModesDeJeu.ChoisirProfil(ms3, null)!.Value.GetProperty("ruleset").GetString());
    }
}
