using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La regle de langue des annonces de borne. Elle a trois couches et l'ordre
/// est tout : c'est ce qui fait qu'un joueur japonais lit du japonais sur une
/// borne francaise, et qu'un passant anonyme lit du francais sur la meme.
/// </summary>
public class CabinetAnnounceTextTests
{
    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("fr_FR", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("ZH_cn", "zh")]
    [InlineData("ko", "ko")]
    public void Normalise_les_codes_regionaux(string input, string expected)
        => Assert.Equal(expected, CabinetAnnounceText.Normalize(input));

    [Theory]
    [InlineData("de")]        // langue non servie
    [InlineData("francais")]  // pas un code
    [InlineData("")]
    [InlineData(null)]
    public void Rejette_ce_qui_n_est_pas_servi(string? input)
        => Assert.Equal(string.Empty, CabinetAnnounceText.Normalize(input));

    [Fact]
    public void Le_joueur_l_emporte_sur_la_borne()
        => Assert.Equal("ja", CabinetAnnounceText.Resolve("ja", "fr_FR"));

    [Fact]
    public void Sans_joueur_la_borne_decide()
        => Assert.Equal("fr", CabinetAnnounceText.Resolve(null, "fr_FR"));

    [Fact]
    public void Une_langue_de_joueur_non_servie_retombe_sur_la_borne()
        => Assert.Equal("fr", CabinetAnnounceText.Resolve("de", "fr-FR"));

    [Fact]
    public void Sans_rien_c_est_l_anglais()
        => Assert.Equal("en", CabinetAnnounceText.Resolve(null, null));

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    public void Chaque_langue_porte_toutes_les_chaines(string locale)
    {
        foreach (var key in new[]
        {
            "start_title", "start_sub", "hold_title", "hold_sub",
            "reached_title", "reached_sub", "end_title", "end_sub",
            "countdown", "go", "ready", "launching", "scan", "open_to_all",
        })
        {
            var text = CabinetAnnounceText.Get(key, locale);
            Assert.NotEqual(key, text);          // pas de cle nue affichee
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public void Une_langue_inconnue_lit_l_anglais()
        => Assert.Equal(
            CabinetAnnounceText.Get("hold_title", "en"),
            CabinetAnnounceText.Get("hold_title", "de"));

    [Fact]
    public void Les_langues_disent_des_choses_differentes()
    {
        // Garde-fou contre un copier-coller : si deux langues rendaient le
        // meme texte, le dictionnaire serait faux sans que rien ne le dise.
        var textes = new[] { "fr", "en", "es", "ja", "zh", "ko" }
            .Select(l => CabinetAnnounceText.Get("ready", l))
            .ToArray();
        Assert.Equal(textes.Length, textes.Distinct().Count());
    }
    [Theory]
    [InlineData("replay_uncertain")]
    [InlineData("replay_downloading")]
    [InlineData("replay_download_percent")]
    [InlineData("replay_download_eta")]
    [InlineData("replay_download_size")]
    [InlineData("replay_this_core")]
    [InlineData("replay_core_other")]
    [InlineData("replay_core_other_local")]
    [InlineData("replay_rom_other")]
    [InlineData("replay_rom_other_crc")]
    public void Les_bandeaux_du_replay_sont_traduits_avec_les_memes_trous(string cle)
    {
        // Ces textes etaient ecrits en dur en francais : une borne en anglais lisait
        // « Lecture incertaine ». Chaque langue doit avoir SA phrase (pas le repli anglais)
        // et les memes {0} {1} que le francais, sinon string.Format casse ou perd l'info.
        static string Trous(string texte) => string.Join(",",
            System.Text.RegularExpressions.Regex.Matches(texte, @"\{\d\}").Select(m => m.Value).OrderBy(v => v));
        var reference = Trous(CabinetAnnounceText.Get(cle, "fr"));
        foreach (var langue in new[] { "fr", "en", "es", "ja", "zh", "ko" })
        {
            var texte = CabinetAnnounceText.Get(cle, langue);
            Assert.NotEqual(cle, texte);
            Assert.Equal(reference, Trous(texte));
            if (langue != "en")
            {
                Assert.NotEqual(CabinetAnnounceText.Get(cle, "en"), texte);
            }
        }
    }
}
