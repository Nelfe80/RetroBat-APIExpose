using RetroBat.Api.Deck;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DECK DANS LES DIRECTS YOUTUBE DU WORLD SCORING (H-D28) : seul un CHALLENGE diffuse en LIVE, Deck pose, le montre ; les
/// arguments du PlayerDeck du direct et de l'exe du direct ; un exe du direct trop ancien n'en recoit rien.
/// </summary>
public sealed class DeckDuDirectTests
{
    private static DeckLancement Lancement(string status = "posed", string mode = DeckModes.WorldScoring, bool diffuse = true, string? deck = "metal-slug-3-pixel")
        => new(mode, "fbneo", "mslug3", "metal-slug-3", "fbneo/metal-slug-3", "player", @"E:\roms\fbneo\mslug3.zip", status, deck, "Rebel Iron", true,
            "official", [deck ?? "x", DeckChoix.Off], null, null, DateTime.UtcNow) { Diffuse = diffuse };

    [Fact]
    public void Seul_un_CHALLENGE_diffuse_avec_le_Deck_pose_montre_le_Deck_dans_le_direct()
    {
        Assert.True(DeckDuDirect.PourCeLancement(Lancement()));
        Assert.False(DeckDuDirect.PourCeLancement(null));
        Assert.False(DeckDuDirect.PourCeLancement(Lancement(diffuse: false)));            // CHALLENGE prive
        Assert.False(DeckDuDirect.PourCeLancement(Lancement(status: "off")));             // Deck retire au lancement
        Assert.False(DeckDuDirect.PourCeLancement(Lancement(status: "window")));          // aucun decor chez le joueur
        Assert.False(DeckDuDirect.PourCeLancement(Lancement(mode: DeckModes.Speedrun)));
        Assert.False(DeckDuDirect.PourCeLancement(Lancement(mode: DeckModes.Multiplayer)));
        Assert.False(DeckDuDirect.PourCeLancement(Lancement(deck: null)));
    }

    [Fact]
    public void Le_Deck_du_direct_part_au_cadre_du_direct_a_dix_images_par_seconde_et_s_arrete_avec_l_API()
    {
        Assert.Equal(@"--direct-out Local\nelfeplay-deck-direct-abc --direct-parent 4242 --direct-fps 10 --render-size 1280x720",
            DeckDuDirect.Arguments(@"Local\nelfeplay-deck-direct-abc", 4242));
        Assert.Equal(["--deck-memoire", "m"], DeckDuDirect.Options("m"));
        var nom = DeckDuDirect.NouveauNom();
        Assert.StartsWith(@"Local\nelfeplay-deck-direct-", nom);
        Assert.DoesNotContain(' ', nom);
        Assert.NotEqual(nom, DeckDuDirect.NouveauNom());
    }

    [Fact]
    public void Un_exe_du_direct_absent_ou_trop_ancien_ne_recoit_pas_le_Deck()
    {
        Assert.False(LanceurDuDirect.SaitLeDeck(null));
        Assert.False(LanceurDuDirect.SaitLeDeck(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N") + ".exe")));
        Assert.Equal(new Version(0, 7, 3), LanceurDuDirect.VersionDuDeck);
        // sans lancement avec Deck, rien ne demarre
        Assert.Null(DeckDuDirect.Demarrer(null, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }

    [Fact]
    public void Le_marquage_du_direct_reste_hors_des_messages_du_Deck()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Lancement());
        Assert.DoesNotContain("Diffuse", json);
    }
}
