using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA NOUVELLE PARTIE DANS LE MEME LANCEMENT (deck.run, 11/11). Le user sur Bubble Bobble : apres un game over, la partie
/// suivante gardait au Deck les vies perdues, le meilleur round et les salles secretes. La sequence rejouee est celle de la
/// boite noire de la borne (rp_06GWQGY28185TV92P14RCZB51M, 11/11 17:59 UTC) et du journal du scoring de la meme partie :
/// 8 610 points, game over a l'image 4560, demo, credit consomme a l'image 5214, nouvelle partie.
/// </summary>
public sealed class DeckNouvellePartieTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 17, 59, 27, DateTimeKind.Utc);
    private static DateTime A(double secondes) => T0.AddSeconds(secondes);

    // ─────────────────────────── la regle ───────────────────────────

    [Fact]
    public void Le_score_qui_retombe_a_zero_ouvre_une_nouvelle_partie_apres_sa_fenetre()
    {
        var r = new DeckNouvellePartie(T0);
        Assert.False(r.Score(1, 1000, "retroarch.wrapper", A(22)));
        Assert.False(r.Score(1, 8610, "retroarch.wrapper", A(36)));
        // game over, demo : le score reste affiche, rien ne bouge
        Assert.False(r.Depart("credit consomme", A(66)));   // le credit de la nouvelle partie : une partie marquait, on attend le score
        Assert.False(r.Score(1, 0, "retroarch.wrapper", A(67)));
        Assert.True(r.ZeroEnAttente);
        Assert.False(r.Echeance(A(67.5)));
        Assert.Equal(0, r.Run);
        Assert.True(r.Echeance(A(67.8)));
        Assert.Equal(1, r.Run);
        Assert.Equal(DeckNouvellePartie.RaisonScore, r.Raison);
        Assert.Equal(8610, r.PicPrecedent);
        Assert.Equal(A(67), r.DebutUtc);
        Assert.False(r.ZeroEnAttente);
    }

    [Fact]
    public void Une_lecture_plus_basse_apres_le_zero_confirme_tout_de_suite()
    {
        var r = new DeckNouvellePartie(T0);
        r.Score(1, 8610, null, A(36));
        r.Score(1, 0, null, A(67));
        Assert.True(r.Score(1, 1000, null, A(67.2)));
        Assert.Equal(1, r.Run);
        // la partie neuve compte depuis ses propres points : son zero suivant est une autre partie
        Assert.False(r.Score(1, 0, null, A(90)));
        Assert.True(r.Echeance(A(91)));
        Assert.Equal(2, r.Run);
        Assert.Equal(1000, r.PicPrecedent);
    }

    [Fact]
    public void Un_zero_de_passage_qui_revient_au_score_d_avant_n_est_pas_une_partie()
    {
        // un score en plusieurs mots ecrit en deux temps (somme des emplacements d'un stage)
        var r = new DeckNouvellePartie(T0);
        r.Score(1, 52_000, null, A(10));
        r.Score(1, 0, null, A(11));
        Assert.False(r.Score(1, 52_000, null, A(11.05)));
        Assert.False(r.ZeroEnAttente);
        Assert.False(r.Echeance(A(20)));
        Assert.Equal(0, r.Run);
    }

    [Fact]
    public void Un_continue_qui_garde_le_score_ne_remet_rien_a_zero()
    {
        // 19XX, DaiOuJou, Ketsui : le continue garde le score (Capcom y ecrit le nombre de continues au dernier chiffre)
        var r = new DeckNouvellePartie(T0);
        r.Score(1, 10_900, null, A(100));
        Assert.False(r.Depart("credit consomme", A(120)));
        Assert.False(r.Score(1, 10_901, null, A(121)));
        Assert.False(r.Echeance(A(200)));
        Assert.Equal(0, r.Run);
    }

    [Fact]
    public void Un_depart_sans_point_marque_ouvre_une_partie_mais_pas_deux_fois_de_suite()
    {
        var r = new DeckNouvellePartie(T0);
        // le depart lu au lancement meme : c'est deja la partie 0
        Assert.False(r.Depart("credit consomme", A(1)));
        // demo d'attraction (aucun point du joueur 1), puis credit et START
        Assert.True(r.Depart("credit consomme", A(40)));
        Assert.Equal(1, r.Run);
        Assert.Equal(DeckNouvellePartie.RaisonDepart, r.Raison);
        Assert.False(r.Depart("GAME_START", A(41)));        // meme depart, deux lectures
        Assert.False(r.Depart("score qui monte", A(60)));   // apres les points : n'ouvre rien
        Assert.Equal(1, r.Run);
    }

    [Fact]
    public void Le_joueur_2_et_RetroAchievements_ne_comptent_pas()
    {
        var r = new DeckNouvellePartie(T0);
        r.Score(1, 5000, "retroarch.wrapper", A(10));
        r.Score(2, 0, "retroarch.wrapper", A(11));
        r.Score(1, 0, "retroachievements", A(12));
        Assert.False(r.ZeroEnAttente);
        Assert.False(r.Echeance(A(30)));
        Assert.Equal(0, r.Run);
    }

    [Fact]
    public void Une_tentative_d_epreuve_ouvre_toujours_une_partie()
    {
        var r = new DeckNouvellePartie(T0);
        Assert.True(r.Tentative(A(2)));
        Assert.True(r.Tentative(A(30)));
        Assert.Equal(2, r.Run);
        Assert.Equal(DeckNouvellePartie.RaisonTentative, r.Raison);
    }

    // ─────────────────────────── le service, par le bus ───────────────────────────

    [Fact]
    public async Task Bubble_Bobble_game_over_puis_nouvelle_partie_publie_deck_run_au_depart_et_jamais_au_game_over()
    {
        var bus = new SimpleEventBus();
        var deck = new DeckStateService(bus, new ApiContext()) { FenetreDuZero = TimeSpan.FromMilliseconds(80) };
        var runs = new List<DeckRun>();
        using var abonnement = bus.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type == "deck.run" && e.Payload is DeckRun r) lock (runs) runs.Add(r);
        });
        await deck.StartAsync(CancellationToken.None);
        try
        {
            void Signal(string nom, string adresse, long valeur, long image) => bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe", SystemId = "fbneo", Rom = "bublbobl",
                    DefinitionFile = "resources/ram/arcade/bubble-bobble.MEM",
                    signal = new RetroArchRuntimeSignal { Key = "ACTION." + nom, Channel = "ACTION", Name = nom, Address = adresse, Value = (int)valeur, Frame = image },
                },
            });
            void Score(long valeur) => bus.PublishAsync(new EventEnvelope
            {
                Type = "score.live.changed",
                Payload = new ScoreLiveEvent { Player = 1, Score = valeur, SystemId = "fbneo", Rom = "bublbobl", Source = "retroarch.wrapper" },
            });
            void Depart(string source) => bus.PublishAsync(new EventEnvelope { Type = "scoring.partie.depart", Payload = new { Source = source } });

            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\bublbobl.zip" } });
            var lancement = deck.LaunchIdCourant!;
            Assert.Equal(0, deck.Snapshot("connect").Run!.Run);

            // la partie 1 (boite noire) : depart, trois morts, 8 610 points, game over
            Depart("credit consomme");
            Signal("GAME_PLAYING", "0xE691", 0, 1681);
            Signal("LOSE_LIFE", "0xE645", 2, 1681);
            Signal("LOSE_LIFE", "0xE691", 2, 2099);
            Score(2000); Score(4010); Score(8610);
            Signal("LOSE_LIFE", "0xE691", 2, 3927);
            Signal("LOSE_LIFE", "0xE691", 2, 4375);
            Signal("GAME_OVER", "0xE691", 0x80, 4560);
            Signal("GAIN_LIFE", "0xE645", 0xFF, 4560);
            Signal("DEMO_MODE", "0xE5D3", 1, 5119);
            Signal("CREDITS", "0xE366", 2, 5179);
            await Task.Delay(200);
            Assert.Empty(runs);   // le game over et la demo ne remettent rien a zero : le bilan reste lisible

            // la partie 2 : credit consomme (une partie marquait : on attend le score), puis le score retombe a zero
            Signal("CREDITS", "0xE366", 1, 5214);
            Depart("credit consomme");
            Signal("LOSE_LIFE", "0xE645", 27, 5217);
            Assert.Empty(runs);
            Score(0);
            Assert.Empty(runs);   // le zero tient sa fenetre
            await Task.Delay(400);
            var run = Assert.Single(runs);
            Assert.Equal(lancement, run.LaunchId);
            Assert.Equal(1, run.Run);
            Assert.Equal("score-zero", run.Reason);
            Assert.Equal(8610, run.PreviousPeak);
            Assert.Equal(1, deck.Snapshot("connect").Run!.Run);

            // la partie 2 marque : rien de plus
            Score(1000);
            Score(7520);
            await Task.Delay(200);
            Assert.Single(runs);

            // l'instantane et son JSON portent la partie en cours
            using var json = JsonDocument.Parse(deck.SnapshotJson("request"));
            var r = json.RootElement.GetProperty("Payload").GetProperty("Run");
            Assert.Equal(1, r.GetProperty("Run").GetInt32());
            Assert.Equal(lancement, r.GetProperty("LaunchId").GetString());

            // un nouveau lancement repart de la partie 0
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            Assert.Null(deck.Snapshot("connect").Run);
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\bublbobl.zip" } });
            Assert.Equal(0, deck.Snapshot("connect").Run!.Run);
        }
        finally
        {
            await deck.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Un_zero_juge_apres_la_fin_du_lancement_ne_publie_rien()
    {
        var bus = new SimpleEventBus();
        var deck = new DeckStateService(bus, new ApiContext()) { FenetreDuZero = TimeSpan.FromMilliseconds(80) };
        var runs = 0;
        using var abonnement = bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.run") Interlocked.Increment(ref runs); });
        await deck.StartAsync(CancellationToken.None);
        try
        {
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\bublbobl.zip" } });
            bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 1, Score = 4000, Source = "retroarch.wrapper" } });
            bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 1, Score = 0, Source = "retroarch.wrapper" } });
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            await Task.Delay(300);
            Assert.Equal(0, runs);
        }
        finally
        {
            await deck.StopAsync(CancellationToken.None);
        }
    }
}
