using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA MANETTE PASSEE AU JOUEUR COURANT (CDC Classements v1.0 §8.3, Worms) : ce que le journal de RetroArch dit de la borne,
/// en anglais et en francais, et quand la borne bascule entre joueur et spectateur (NETPLAY_GAME_WATCH, banc du 30/10).
/// </summary>
public class MultiplayerRelaisDeLaManetteTests
{
    private static readonly MultiplayerTurnRelay Worms = new()
    {
        CoreRam = "0x1A2B", Values = new Dictionary<string, int> { ["0"] = 1, ["1"] = 2 }, Port = 1, SettleMs = 300,
    };

    [Theory]
    [InlineData("[INFO] [Netplay] You have joined as player 1", MultiplayerReponseDuNetplay.Rejoint)]
    [InlineData("[INFO] [Netplay] You have joined as player 1 (ping: 16 ms)", MultiplayerReponseDuNetplay.Rejoint)]
    [InlineData("[INFO] [Netplay] Vous avez rejoint le jeu en tant que joueur 1 (ping: 16 ms)", MultiplayerReponseDuNetplay.Rejoint)]
    [InlineData("[INFO] [Netplay] You have left the game", MultiplayerReponseDuNetplay.Quitte)]
    [InlineData("[INFO] [Netplay] Vous avez quitté le jeu", MultiplayerReponseDuNetplay.Quitte)]
    [InlineData("[INFO] [Netplay] The input devices requested are not available", MultiplayerReponseDuNetplay.Refuse)]
    [InlineData("[INFO] [Netplay] Les dispositifs d'entrée demandés ne sont pas disponibles", MultiplayerReponseDuNetplay.Refuse)]
    [InlineData("[INFO] [Netplay] invite has joined as player 1 (ping: 12 ms)", null)]
    [InlineData("[INFO] [Netplay] Player hote has left the game", null)]
    [InlineData("[INFO] [Core] You have left the game", null)]
    public void Le_journal_dit_ce_que_fait_cette_borne(string ligne, MultiplayerReponseDuNetplay? attendue)
        => Assert.Equal(attendue, MultiplayerRelaisDeLaManette.Lire(ligne));

    [Fact]
    public void L_hote_rend_la_manette_quand_le_tour_passe_a_l_invite()
    {
        var hote = new MultiplayerRelaisEnCours(Worms, 1);
        // Rien avant la premiere reponse du journal : l'etat de depart de la borne n'est pas encore connu.
        Assert.False(hote.Observer(1, 0));
        hote.Noter(MultiplayerReponseDuNetplay.Rejoint, 100);
        Assert.False(hote.Observer(0, 200));
        Assert.False(hote.Observer(0, 600));
        // Le tour passe a la place 2 : il faut qu'il tienne 300 ms.
        Assert.False(hote.Observer(1, 1000));
        Assert.False(hote.Observer(1, 1200));
        Assert.True(hote.Observer(1, 1300));
        // Une seule bascule a la fois, jusqu'a la reponse.
        Assert.False(hote.Observer(1, 1400));
        hote.Noter(MultiplayerReponseDuNetplay.Quitte, 1500);
        Assert.False(hote.Joue);
        Assert.False(hote.Observer(1, 1600));
        // Le tour revient : il la redemande.
        Assert.False(hote.Observer(0, 2000));
        Assert.True(hote.Observer(0, 2300));
    }

    [Fact]
    public void L_invite_redemande_tant_que_la_manette_est_tenue()
    {
        var invite = new MultiplayerRelaisEnCours(Worms, 2);
        // Au lancement, l'hote tient la manette : la demande de l'invite est refusee, il regarde.
        invite.Noter(MultiplayerReponseDuNetplay.Refuse, 0);
        Assert.False(invite.Observer(0, 100));
        Assert.False(invite.Observer(1, 1000));
        Assert.True(invite.Observer(1, 1300));
        // Le sortant ne l'a pas encore rendue : refus, nouvelle demande 500 ms plus tard.
        invite.Noter(MultiplayerReponseDuNetplay.Refuse, 1400);
        Assert.False(invite.Observer(1, 1600));
        Assert.True(invite.Observer(1, 1900));
        invite.Noter(MultiplayerReponseDuNetplay.Rejoint, 2000);
        Assert.True(invite.Joue);
        Assert.False(invite.Observer(1, 2500));
    }

    [Fact]
    public void Une_valeur_hors_de_la_table_ne_change_personne_de_main()
    {
        var hote = new MultiplayerRelaisEnCours(Worms, 1);
        hote.Noter(MultiplayerReponseDuNetplay.Rejoint, 0);
        Assert.False(hote.Observer(7, 100));
        Assert.False(hote.Observer(7, 5000));
        Assert.True(hote.Joue);
    }

    [Fact]
    public void Une_bascule_sans_reponse_arrete_le_relais()
    {
        // La commande bascule : sans reponse, la redemander pourrait rendre la manette a contretemps.
        var hote = new MultiplayerRelaisEnCours(Worms, 1);
        hote.Noter(MultiplayerReponseDuNetplay.Rejoint, 0);
        Assert.False(hote.Observer(1, 100));
        Assert.True(hote.Observer(1, 400));
        Assert.False(hote.Observer(1, 400 + MultiplayerRelaisEnCours.SansReponseMs));
        Assert.True(hote.HorsService);
        Assert.False(hote.Observer(0, 20000));
    }

    [Fact]
    public void Le_jeu_a_tour_demande_la_seule_manette_et_se_valide()
    {
        var jeu = new MultiplayerGame { Id = "snes/worms", System = "snes", Players = 2, TurnRelay = Worms };
        Assert.Equal([1], jeu.PortsDeLaPlace(1));
        Assert.Equal([1], jeu.PortsDeLaPlace(2));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeRelais(jeu));
        Assert.Equal(2, Worms.PlaceDuTour(1));
        Assert.Null(Worms.PlaceDuTour(5));

        const string Refus = "relais de la manette incoherent (adresse, valeurs vers places, port ; sans ordre, ports ni depart par place)";
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { TurnRelay = Worms with { Values = new Dictionary<string, int> { ["0"] = 3 } } }));
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { TurnRelay = Worms with { Values = new Dictionary<string, int> { ["x"] = 1 } } }));
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { TurnRelay = Worms with { Port = 0 } }));
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { SeatOrder = [2, 1] }));
    }
}
