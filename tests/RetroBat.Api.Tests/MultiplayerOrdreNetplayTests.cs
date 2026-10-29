using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES JOUEURS DANS L'ORDRE DU NETPLAY (29/10, Bomberman PSP) : le joueur 1 du jeu est l'hote ; quand la plateforme lui a
/// donne la place 2, les etats, la fin et les statistiques du catalogue passent dans l'ordre des places.
/// </summary>
public sealed class MultiplayerOrdreNetplayTests
{
    private static readonly MultiplayerGame Bomberman = new()
    {
        Id = "psp/bomberman", System = "psp", Players = 2,
        End = new MultiplayerEnd { FirstTo = 3, Seats = [new() { Seat = 1, CoreRam = "0xDED8CB" }, new() { Seat = 2, CoreRam = "0xDED8D7" }] },
        CoreStart = new MultiplayerCoreStart
        {
            Path = "PPSSPP/netplay/ULUS10121.state",
            Seats = [new() { Seat = 1, State = "states/psp/bomberman/p1.state.gz" }, new() { Seat = 2, State = "states/psp/bomberman/p2.state.gz" }],
        },
    };

    private static MultiplayerRoom Room(int placeDeLHote) => new()
    {
        Players =
        [
            new() { Handle = "hote", Host = true, Seat = placeDeLHote },
            new() { Handle = "invite", Seat = 3 - placeDeLHote },
        ],
    };

    [Fact]
    public void L_hote_a_la_place_1_rien_ne_change()
    {
        var jeu = new MultiplayerInstalledGame(Bomberman, "b.zip", null, "md5");
        Assert.Same(jeu, MultiplayerOrdreNetplay.PourLaRoom(jeu, Room(1)));
    }

    [Fact]
    public void L_hote_a_la_place_2_prend_l_etat_et_les_compteurs_du_joueur_1()
    {
        var jeu = MultiplayerOrdreNetplay.PourLaRoom(new MultiplayerInstalledGame(Bomberman, "b.zip", null, "md5"), Room(2)).Game;

        Assert.Equal("states/psp/bomberman/p1.state.gz", jeu.CoreStart!.DeLaPlace(2)!.State);
        Assert.Equal("states/psp/bomberman/p2.state.gz", jeu.CoreStart.DeLaPlace(1)!.State);
        Assert.Equal("0xDED8CB", jeu.End!.Seats.Single(s => s.Seat == 2).CoreRam);
        Assert.Equal("0xDED8D7", jeu.End.Seats.Single(s => s.Seat == 1).CoreRam);

        var profil = new MultiplayerStatsProfil
        {
            Signals = new Dictionary<string, MultiplayerStatsSignal> { ["bombes"] = new() { Seats = new Dictionary<string, string> { ["1"] = "0x00", ["2"] = "0xBC" } } },
        };
        var permute = MultiplayerOrdreNetplay.Permuter(profil, jeu.PlacesDuNetplay);
        Assert.Equal("0x00", permute.Signals["bombes"].Seats["2"]);
        Assert.Equal("0xBC", permute.Signals["bombes"].Seats["1"]);
    }

    [Fact]
    public void Un_jeu_sans_depart_par_place_garde_l_ordre_des_places()
    {
        var jeu = new MultiplayerInstalledGame(Bomberman with { CoreStart = null }, "b.zip", null, "md5");
        Assert.Same(jeu, MultiplayerOrdreNetplay.PourLaRoom(jeu, Room(2)));
    }
}
