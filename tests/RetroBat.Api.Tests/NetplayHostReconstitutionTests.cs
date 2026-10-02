using System.Text.Json;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un defi diffuse sur un jeu absent des journaux d'ES (2026-10-01, Metal Slug 3) : l'hebergement
/// reconstitue le lancement au lieu de partir sans direct. Le systeme vient du dossier de la ROM,
/// le choix du jeu de sa fiche ES.
/// </summary>
public sealed class NetplayHostReconstitutionTests
{
    [Theory]
    [InlineData(@"E:\RetroBat\roms\fbneo\mslug3.zip", "fbneo")]
    [InlineData(@"E:\RetroBat\roms\psx\Un jeu\disque.cue", "psx")]
    [InlineData("\"E:\\RetroBat\\roms\\mame\\19xx.zip\"", "mame")]
    [InlineData(@"E:/RetroBat/roms/megadrive/sonic.zip", "megadrive")]
    [InlineData(@"E:\RetroBat\roms\perdu.zip", "")]
    [InlineData(@"D:\ailleurs\fbneo\mslug3.zip", "")]
    public void LeSystemeEstLePremierDossierSousRoms(string rom, string attendu)
        => Assert.Equal(attendu, NetplayHostService.SystemeDuChemin(rom, @"E:\RetroBat\roms"));

    [Fact]
    public void LaFicheDuJeuDonneSonEmulateurEtSonCoeur()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "path": "./1942.zip", "emulator": "libretro", "core": "fbneo" },
              { "path": "./mslug3.zip", "emulator": "libretro", "core": "fbneo" },
              { "path": "./sonic.zip" }
            ]
            """);

        Assert.Equal(("libretro", "fbneo"), NetplayHostService.FicheDans(doc.RootElement, @"E:\RetroBat\roms\fbneo\MSLUG3.zip"));
        Assert.Equal(((string?)null, (string?)null), NetplayHostService.FicheDans(doc.RootElement, @"E:\RetroBat\roms\fbneo\sonic.zip"));
        Assert.Equal(((string?)null, (string?)null), NetplayHostService.FicheDans(doc.RootElement, @"E:\RetroBat\roms\fbneo\absent.zip"));
    }
}

/// <summary>
/// Les reactions sont celles d'un SPECTATEUR (2026-10-02) : un invite qui joue ne voit pas la
/// facade et ses boutons de jeu ne partent pas en reactions.
/// </summary>
public sealed class LiveSpectateStateTests
{
    [Fact]
    public void Un_joueur_n_est_pas_spectateur()
    {
        var etat = new LiveSpectateState();
        etat.Ouvrir("session", "jeton", peutJouer: true);
        Assert.True(etat.Actif);
        Assert.False(etat.Spectateur);

        etat.Ouvrir("session", "jeton", peutJouer: false);
        Assert.True(etat.Spectateur);

        etat.Fermer();
        Assert.False(etat.Actif);
        Assert.False(etat.Spectateur);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    public void Le_bandeau_d_arrivee_existe_dans_chaque_langue(string langue)
    {
        foreach (var cle in new[] { "netplay_player_title", "netplay_player_sub", "netplay_spectator_title", "netplay_spectator_sub", "netplay_this_live" })
        {
            var texte = RetroBat.Api.Infrastructure.CabinetAnnounceText.Get(cle, langue);
            Assert.NotEqual(cle, texte);
            if (cle.EndsWith("_title", StringComparison.Ordinal)) Assert.Contains("{0}", texte);
        }
    }
}

/// <summary>
/// L'hote annonce le mot de passe que RetroArch applique (2026-10-02) : ES pouvait remettre un
/// ancien mot de passe dans es_settings avant le lancement, et l'invite restait bloque.
/// </summary>
public sealed class NetplayMotsDePasseTests
{
    [Fact]
    public void Les_mots_de_passe_se_lisent_dans_retroarch_cfg()
    {
        var cfg = "netplay_nickname = \"NELFE\"\nnetplay_password = \"ab12cd34\"\nnetplay_public_announce = \"true\"\nnetplay_spectate_password = \"\"\n";
        var (joueur, spectateur) = NetplayHostService.LireMotsDePasse(cfg);
        Assert.Equal("ab12cd34", joueur);
        Assert.Equal("", spectateur);
    }

    [Fact]
    public void Une_cle_absente_reste_inconnue()
    {
        var (joueur, spectateur) = NetplayHostService.LireMotsDePasse("video_driver = \"gl\"\n");
        Assert.Null(joueur);
        Assert.Null(spectateur);
    }

    [Fact]
    public void Une_cle_voisine_ne_se_confond_pas()
    {
        var (joueur, _) = NetplayHostService.LireMotsDePasse("netplay_password_old = \"x\"\nnetplay_password = \"bon\"\n");
        Assert.Equal("bon", joueur);
    }
}
