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

    /// <summary>NBA Jam TE (livraison du 29/10) : P1 et P2 contre P3 et P4, un score par camp, fin a l'ecran FINAL.</summary>
    private static readonly MultiplayerGame NbaJam = new()
    {
        Id = "arcade/nbajamte", System = "arcade", Players = 4, EloType = "sport", SeatOrder = [1, 3, 2, 4],
        RankingEnd = new()
        {
            Rule = MultiplayerRankingEnd.ScoreFige, Over = new() { CoreRam = "0x1D8DA", Value = 50 },
            Seats =
            [
                new() { Seat = 1, CoreRam = "0x1D5FA", Bytes = 2, Camp = 1 }, new() { Seat = 2, CoreRam = "0x1D5FA", Bytes = 2, Camp = 1 },
                new() { Seat = 3, CoreRam = "0x1D5FC", Bytes = 2, Camp = 2 }, new() { Seat = 4, CoreRam = "0x1D5FC", Bytes = 2, Camp = 2 },
            ],
        },
    };

    private static MultiplayerRoom RoomA(int joueurs) => new()
    {
        Players = Enumerable.Range(1, joueurs).Select(place => new MultiplayerJoueur { Handle = "j" + place, Host = place == 1, Seat = place }).ToList(),
    };

    [Fact]
    public void L_ordre_des_places_met_deux_humains_face_a_face()
    {
        // v1.1 §8.3 : a deux, la place 2 de la plateforme joue le P3 du jeu, dans l'autre camp, et demande son port.
        Assert.True(NbaJam.ClassePour([1, 2]));
        Assert.Equal([3], NbaJam.PortsDeLaPlace(2));
        Assert.Equal([2], NbaJam.PortsDeLaPlace(3));
        Assert.Equal([1], NbaJam.PortsDeLaPlace(1));

        var partie = MultiplayerOrdreNetplay.PourLaRoom(new MultiplayerInstalledGame(NbaJam, "nbajamte.zip", null, "md5"), RoomA(2)).Game;
        Assert.Equal("0x1D5FC", partie.RankingEnd!.Place(2)!.CoreRam);
        Assert.Equal(2, partie.RankingEnd.Place(2)!.Camp);
        Assert.Equal("0x1D5FA", partie.RankingEnd.Place(3)!.CoreRam);
        // Remis dans l'ordre de la plateforme, il se lit tel quel ; le port, lui, reste celui du jeu.
        Assert.True(partie.ClassePour([1, 2]));
        Assert.Equal([3], partie.PortsDeLaPlace(2));
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [3] = 2, [2] = 3, [4] = 4 }, partie.PlacesDuNetplay);

        // Sans l'ordre, les deux humains seraient dans le meme camp : rencontre non classee.
        Assert.False((NbaJam with { SeatOrder = null }).ClassePour([1, 2]));
    }

    /// <summary>Slam Masters (livraison du 29/10) : duel sans camp a deux, deux contre deux a trois ou quatre.</summary>
    private static readonly MultiplayerGame SlamMasters = new()
    {
        Id = "arcade/slammast", System = "arcade", Players = 4, EloType = "combat",
        RankingEnd = new()
        {
            Players = [3, 4], Rule = MultiplayerRankingEnd.Manches, FirstTo = 1, Guard = new() { CoreRam = "0xA095", Value = 1 },
            Seats =
            [
                new() { Seat = 1, CoreRam = "0x929E", Camp = 1 }, new() { Seat = 2, CoreRam = "0x949E", Camp = 1 },
                new() { Seat = 3, CoreRam = "0x969E", Camp = 2 }, new() { Seat = 4, CoreRam = "0x989E", Camp = 2 },
            ],
        },
        RankingEnds =
        [
            new()
            {
                Players = [2], Rule = MultiplayerRankingEnd.Manches, FirstTo = 1, Guard = new() { CoreRam = "0xA095", Value = 1 },
                Seats = [new() { Seat = 1, CoreRam = "0x929E" }, new() { Seat = 2, CoreRam = "0x949E" }],
            },
        ],
    };

    [Fact]
    public void Une_fin_par_nombre_de_joueurs_choisit_celle_de_la_partie()
    {
        // v1.1 §7, n° 16 : a deux, le duel sans camp ; a trois, les equipes.
        Assert.True(SlamMasters.ClassePour([1, 2]));
        Assert.True(SlamMasters.ClassePour([1, 2, 3]));
        Assert.False(SlamMasters.RankingEnd!.Classable([1, 2]));

        var aDeux = MultiplayerOrdreNetplay.PourLaRoom(new MultiplayerInstalledGame(SlamMasters, "slammast.zip", null, "md5"), RoomA(2)).Game;
        Assert.Equal([2], aDeux.RankingEnd!.Players);
        Assert.False(aDeux.RankingEnd.AvecCamps);
        Assert.Null(aDeux.RankingEnds);
        var aQuatre = MultiplayerOrdreNetplay.PourLaRoom(new MultiplayerInstalledGame(SlamMasters, "slammast.zip", null, "md5"), RoomA(4)).Game;
        Assert.Equal([3, 4], aQuatre.RankingEnd!.Players);

        // Un nombre qu'aucune fin ne prend : partie libre.
        var sansTrois = SlamMasters with { RankingEnd = SlamMasters.RankingEnd with { Players = [4] } };
        Assert.False(sansTrois.ClassePour([1, 2, 3]));
        Assert.False(MultiplayerOrdreNetplay.PourLaRoom(new MultiplayerInstalledGame(sansTrois, "slammast.zip", null, "md5"), RoomA(3)).Game.FinLue);
    }

    [Fact]
    public void Un_jeu_sans_depart_par_place_garde_l_ordre_des_places()
    {
        var jeu = new MultiplayerInstalledGame(Bomberman with { CoreStart = null }, "b.zip", null, "md5");
        Assert.Same(jeu, MultiplayerOrdreNetplay.PourLaRoom(jeu, Room(2)));
    }
}
