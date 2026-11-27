using RetroBat.Api.Adventure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.VocalRoom;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>HOTKEY + Y en jeu (27/11), ligne et badge du panneau Adventure, indice de depart.</summary>
public sealed class MicroEnJeuTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Y_n_arme_le_geste_qu_en_jeu_avec_la_hotkey(bool enJeu, bool hotkey, bool attendu)
        => Assert.Equal(attendu, MicroEnJeu.Arme(enJeu, hotkey));

    [Fact]
    public void Court_coupe_le_micro_maintenu_coupe_le_son_non_arme_rien()
    {
        Assert.Equal(GesteDuVocal.Micro, MicroEnJeu.Relache(true, 150, 600));
        Assert.Equal(GesteDuVocal.Son, MicroEnJeu.Relache(true, 600, 600));
        Assert.Equal(GesteDuVocal.Son, MicroEnJeu.Relache(true, 2000, 600));
        Assert.Equal(GesteDuVocal.Rien, MicroEnJeu.Relache(false, 2000, 600));
    }

    [Fact]
    public void Chaque_issue_a_une_cle_du_data_pack_et_aucune_pour_rien()
    {
        Assert.Null(MicroEnJeu.Annonce(IssueDuGeste.Aucune));
        Assert.Equal("multiplayer.voice.mic_on", MicroEnJeu.Annonce(IssueDuGeste.MicroOuvert)!.Value.Cle);
        Assert.Equal("MIC OFF", MicroEnJeu.Annonce(IssueDuGeste.MicroCoupe)!.Value.Defaut);
        Assert.Equal("multiplayer.voice.sound_off", MicroEnJeu.Annonce(IssueDuGeste.SonCoupe)!.Value.Cle);
        Assert.Equal("multiplayer.voice.sound_on", MicroEnJeu.Annonce(IssueDuGeste.SonRendu)!.Value.Cle);
    }

    private sealed class BusMuet : IEventBus
    {
        public IDisposable Subscribe<T>(Action<T> handler) where T : class => new Rien();
        public Task PublishAsync<T>(T evt) where T : class => Task.CompletedTask;
        private sealed class Rien : IDisposable { public void Dispose() { } }
    }

    private static EventEnvelope Bouton(string type, string identite)
        => new() { Type = type, Payload = new { Player = 1, Identity = identite } };

    private static (MicroEnJeuService, List<bool>) Service()
    {
        var appels = new List<bool>();
        var s = new MicroEnJeuService(new BusMuet(), m => { lock (appels) appels.Add(m); return Task.FromResult(IssueDuGeste.MicroCoupe); }, () => 600)
        {
            Annonce = (_, _) => { },
        };
        return (s, appels);
    }

    [Fact]
    public async Task Y_seul_en_jeu_ne_touche_pas_au_vocal()
    {
        var (s, appels) = Service();
        s.SurEvenement(new EventEnvelope { Type = "ui.game.started" });
        s.SurEvenement(Bouton("panel.input.pressed", "y"));
        s.SurEvenement(Bouton("panel.input.released", "y"));
        await Task.Delay(100);
        Assert.Empty(appels);
    }

    [Fact]
    public async Task Hotkey_plus_Y_court_en_jeu_coupe_le_micro()
    {
        var (s, appels) = Service();
        s.SurEvenement(new EventEnvelope { Type = "ui.game.started" });
        s.SurEvenement(Bouton("panel.input.pressed", "select"));
        s.SurEvenement(Bouton("panel.input.pressed", "y"));
        s.SurEvenement(Bouton("panel.input.released", "y"));
        s.SurEvenement(Bouton("panel.input.released", "select"));
        await Task.Delay(200);
        Assert.Equal([false], appels);
    }

    [Fact]
    public async Task Hors_jeu_la_hotkey_ne_commande_rien_ici()
    {
        var (s, appels) = Service();
        s.SurEvenement(Bouton("panel.input.pressed", "select"));
        s.SurEvenement(Bouton("panel.input.pressed", "y"));
        s.SurEvenement(Bouton("panel.input.released", "y"));
        await Task.Delay(100);
        Assert.Empty(appels);
        s.SurEvenement(new EventEnvelope { Type = "ui.game.started" });
        Assert.True(s.EnJeu);
        s.SurEvenement(new EventEnvelope { Type = "ui.game.ended" });
        Assert.False(s.EnJeu);
    }

    [Fact]
    public void La_ligne_du_haut_d_une_aventure_nomme_le_jeu_et_ne_dit_pas_classic()
    {
        string T(string k, string d) => d;
        Assert.Equal("Zelda Four Swords (USA) · ADVENTURE · no ranking", MultiplayerPanelService.InfoDuJeuAventure("Zelda Four Swords", "USA", T));
        Assert.Equal("Secret of Mana · ADVENTURE · no ranking", MultiplayerPanelService.InfoDuJeuAventure("Secret of Mana", null, T));
    }

    [Theory]
    [InlineData(2, "2")]
    [InlineData(3, "2 and 3")]
    [InlineData(4, "2, 3 and 4")]
    public void Les_places_de_l_indice(int joueurs, string attendu) => Assert.Equal(attendu, AdventureIndice.Places(joueurs, "and"));

    [Fact]
    public void L_indice_vient_de_la_cle_du_catalogue_jamais_du_jeu()
    {
        string T(string k, string? d) => k switch
        {
            "adventure.hint.and" => "and",
            "adventure.hint.press_start_to_join" => "Players {players}: press START when your character joins the party",
            "adventure.hint.press_start_to_join_one" => "Player {players}: press START",
            _ => d ?? string.Empty,
        };
        Assert.Equal("Players 2 and 3: press START when your character joins the party", AdventureIndice.Composer("adventure.hint.press_start_to_join", 3, T));
        Assert.Equal("Player 2: press START", AdventureIndice.Composer("adventure.hint.press_start_to_join", 2, T));
        Assert.Null(AdventureIndice.Composer(null, 3, T));
        Assert.Null(AdventureIndice.Composer("cle.inconnue", 3, T));
    }

    [Fact]
    public void Le_catalogue_lit_start_hint_et_la_version_peut_le_surcharger()
    {
        var spec = new AdventureSpec { StartHint = "a" };
        Assert.Equal("a", spec.PourLeContenu(null).StartHint);
    }

    private static MultiplayerRoom Salle(string room, string jeu, string etat, bool aventure, int presents)
        => new()
        {
            Room = room,
            Game = jeu,
            State = etat,
            Adventure = aventure ? new MultiplayerAventure { Id = "adv_" + room } : null,
            Players = Enumerable.Range(0, presents).Select(_ => new MultiplayerJoueur { Present = true }).ToList(),
        };

    [Fact]
    public void Chaque_panneau_ne_voit_que_ses_salles()
    {
        var salles = new[]
        {
            Salle("a", "sf2", "waiting", false, 1), Salle("b", "mana", "waiting", true, 1),
            Salle("c", "kirby", "countdown", true, 2), Salle("d", "zelda", "playing", true, 2),
        };
        Assert.Equal(["a"], MultiplayerPanelService.SelectionDuPanneau(salles, false).Select(r => r.Room));
        Assert.Equal(["b", "c"], MultiplayerPanelService.SelectionDuPanneau(salles, true).Select(r => r.Room));
    }

    [Fact]
    public void Une_salle_d_aventure_ou_personne_n_est_venu_se_ferme_sauf_celle_du_jeu_ouvert()
    {
        var salles = new[]
        {
            Salle("a", "sf2", "waiting", false, 1),
            Salle("b", "mana", "waiting", true, 1),
            Salle("c", "kirby", "waiting", true, 2),
            Salle("d", "shining", "countdown", true, 1),
            Salle("e", "zelda", "waiting", true, 1),
        };
        Assert.Equal(["b", "e"], MultiplayerPanelService.SallesSeules(salles, null).Select(r => r.Room));
        Assert.Equal(["e"], MultiplayerPanelService.SallesSeules(salles, "mana").Select(r => r.Room));
    }

    private static string T(string k, string d) => d;

    private static MultiplayerInvitation Invitation(string jeu, string? aventure)
        => new() { Invitation = "inv1", Game = jeu, Adventure = aventure, From = new MultiplayerJoueur { Pseudo = "Alice" } };

    private static AventuresDuJeu Aventures(bool enCours)
        => new("", 4, [new AventureLigne("adv1", "", 2, null, enCours, "", ["Bob"])]);

    [Fact]
    public void Une_invitation_recue_est_montree_en_tete_et_passe_avant()
    {
        var offre = MultiplayerPanelService.OffreDEntreeAventure([Invitation("mana", "adv9")], "mana", Aventures(true), false, T);
        Assert.Equal("Invitation from Alice: A to join", offre!.Texte);
        Assert.Equal("inv1", offre.Invitation);
        Assert.Null(offre.Aventure);
    }

    [Fact]
    public void Une_aventure_dont_la_salle_attend_est_montree_en_tete()
    {
        var offre = MultiplayerPanelService.OffreDEntreeAventure([], "mana", Aventures(true), false, T);
        Assert.Equal("Bob is waiting for you: A to join", offre!.Texte);
        Assert.Equal("adv1", offre.Aventure);
    }

    [Fact]
    public void Ouvrir_le_panneau_seul_ne_propose_aucune_entree()
    {
        Assert.Null(MultiplayerPanelService.OffreDEntreeAventure([], "mana", Aventures(false), false, T));
        Assert.Null(MultiplayerPanelService.OffreDEntreeAventure([Invitation("kirby", "adv2"), Invitation("mana", null)], "mana", null, false, T));
        Assert.Null(MultiplayerPanelService.OffreDEntreeAventure([Invitation("mana", "adv9")], "mana", Aventures(true), true, T));
    }
}
