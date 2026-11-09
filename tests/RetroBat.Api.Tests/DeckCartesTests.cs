using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES ECRANS DE DEPART ET DE BILAN DU PLAYERDECK (demande du user du 09/11) : qui les montre (DeckCartes), ce que l'API
/// donne au Deck pour les remplir (lancement decide dans l'instantane, page plein ecran de l'API, verdict du site, rang
/// vise au score), et ce que le Deck lui dit en retour (deck.card).
/// </summary>
public sealed class DeckCartesTests : IDisposable
{
    private const string Rom19xx = @"E:\RetroBat\roms\fbneo\19xx.zip";
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "deck-cartes-" + Guid.NewGuid().ToString("N"));

    public DeckCartesTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private DeckLaunchService Service(SimpleEventBus bus, ApiContext contexte, DeckStateService? etat = null)
    {
        var catalogue = DeckCatalogue.Lire(Catalogue());
        var png = Path.Combine(_dossier, "rendu.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4e, 0x47]);
        return new DeckLaunchService(bus, contexte, etat, fichierDesChoix: Path.Combine(_dossier, "choix.json"))
        {
            Catalogue = () => catalogue,
            Ecran = () => (1920, 1200),
            RacineDuDecor = Path.Combine(_dossier, "decorations", DeckDecor.Nom),
            DemarrerLeHost = false,
            Rendeur = (e, l, h, _) => new DeckRendu(png, new DeckDecor.Trou((l - h * 3 / 4) / 2, 0, h * 3 / 4, h), l, h, false, 1),
            Identite = (s, c, g) => new DeckIdentite.Cle("fbneo/19xx", "19xx", "essai"),
        };
    }

    /// <summary>Un dossier de Deck minimal : un theme officiel de 19XX.</summary>
    private string Catalogue()
    {
        var dossier = Path.Combine(_dossier, "playerdeck");
        Directory.CreateDirectory(Path.Combine(dossier, "themes"));
        File.WriteAllText(Path.Combine(dossier, "themes", "19xx-hangar.json"),
            "{\"format\":\"nelfeplay.deck-theme/1\",\"id\":\"19xx-hangar\",\"name\":\"Hangar\",\"version\":3,\"deck\":{\"default\":true},\"match\":{\"roms\":[\"19xx\"]}}");
        return dossier;
    }

    [Fact]
    public void World_Scoring_montre_les_ecrans_du_Deck_et_une_epreuve_du_panneau_garde_ses_pages()
    {
        var bus = new SimpleEventBus();
        var lances = new List<DeckLancement>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.launch") lances.Add((DeckLancement)e.Payload!); });
        var service = Service(bus, new ApiContext { PlayerPseudo = "player" });

        var ws = service.Preparer(DeckModes.WorldScoring, "fbneo", Rom19xx);
        Assert.Equal("posed", ws.Status);
        Assert.Equal(DeckCartes.Deck, ws.Cards);

        var panneau = service.Preparer(DeckModes.Speedrun, "fbneo", Rom19xx, cartes: DeckCartes.Api);
        Assert.Equal(DeckCartes.Api, panneau.Cards);

        // la route REST (sans page) : les ecrans du Deck ; une valeur inconnue retombe sur le Deck
        Assert.Equal(DeckCartes.Deck, service.Preparer(DeckModes.Speedrun, "fbneo", Rom19xx).Cards);
        Assert.Equal(DeckCartes.Deck, service.Preparer(DeckModes.Speedrun, "fbneo", Rom19xx, cartes: "autre").Cards);

        Assert.Equal(4, lances.Count);
        Assert.Contains("\"Cards\":\"api\"", JsonSerializer.Serialize(lances[1]));
    }

    [Fact]
    public void L_instantane_porte_le_lancement_decide_et_la_page_de_l_API()
    {
        var bus = new SimpleEventBus();
        var contexte = new ApiContext { PlayerPseudo = "player" };
        var etat = new DeckStateService(bus, contexte);
        etat.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var service = Service(bus, contexte, etat);
        try
        {
            Assert.Null(etat.Snapshot("x").Launch);
            service.Preparer(DeckModes.WorldScoring, "fbneo", Rom19xx);
            // un Deck demarre a l'instant (avant le lanceur) sait qu'il doit poser son ecran de depart
            var s = etat.Snapshot("connect");
            Assert.Equal("posed", s.Launch!.Status);
            Assert.Equal(DeckCartes.Deck, s.Launch.Cards);
            Assert.Contains("\"Launch\":{", etat.SnapshotJson("connect"));

            // la page de preparation d'une epreuve : gardee tant qu'elle est ouverte
            bus.PublishAsync(new EventEnvelope { Type = "deck.page", Payload = new DeckPage("preparation", true, "api", DateTime.UtcNow) });
            Assert.True(etat.Snapshot("x").Page!.Open);
            bus.PublishAsync(new EventEnvelope { Type = "deck.page", Payload = new DeckPage("preparation", false, "api", DateTime.UtcNow) });
            Assert.Null(etat.Snapshot("x").Page);

            // le jeu part : le lancement est rattache a la partie, l'instantane ne le repete plus
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = Rom19xx } });
            Assert.Null(etat.Snapshot("x").Launch);
            Assert.Equal("posed", etat.Snapshot("x").Game!.Deck!.Status);
        }
        finally
        {
            etat.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Le_verdict_du_site_va_au_bilan_du_Deck_seulement_pour_une_partie_avec_Deck()
    {
        var bus = new SimpleEventBus();
        var resultats = new List<DeckResult>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.result") resultats.Add((DeckResult)e.Payload!); });
        var contexte = new ApiContext { PlayerPseudo = "player" };
        var etat = new DeckStateService(bus, contexte);
        etat.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var service = Service(bus, contexte, etat);
        void Verdict(string statut, int? rang) => bus.PublishAsync(new EventEnvelope
        {
            Type = "scoring.verdict",
            Payload = new { SessionId = "s", RomGroup = "19xx", SystemId = "arcade", Ruleset = "1cc", Status = statut, Score = 412300L, Rank = rang, Continues = (long?)0, DurationMs = (long?)754000 },
        });
        try
        {
            // une partie lancee par ES, sans Deck : rien
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = Rom19xx } });
            Verdict("published", 3);
            Assert.Empty(resultats);
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });

            // la meme partie avec son Deck : le verdict part au bilan, meme apres la fin de la partie
            service.Preparer(DeckModes.WorldScoring, "fbneo", Rom19xx);
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = Rom19xx } });
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            Verdict("published", 3);
            var r = Assert.Single(resultats);
            Assert.True(r.Certified);
            Assert.Equal(3, r.Rank);
            Assert.Equal(412300, r.Score);
            Assert.Equal(0, r.Continues);
            Assert.Equal(754000, r.DurationMs);
            Assert.Equal(etat.Snapshot("x").Game!.LaunchId, r.LaunchId);
            Assert.Equal(r, etat.Snapshot("x").Result);

            Verdict("held", null);
            Assert.False(resultats[^1].Certified);
            Assert.Null(resultats[^1].Rank);
        }
        finally
        {
            etat.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void L_ecran_du_Deck_est_annonce_sur_le_bus_sans_reponse_au_Deck()
    {
        var bus = new SimpleEventBus();
        var cartes = new List<DeckCard>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.card") cartes.Add((DeckCard)e.Payload!); });
        var etat = new DeckStateService(bus, new ApiContext());
        using var montre = JsonDocument.Parse("{\"Kind\":\"start\",\"State\":\"shown\",\"LaunchId\":\"abc\"}");
        Assert.Null(etat.Commande(new object(), "deck.card", montre.RootElement));
        using var faux = JsonDocument.Parse("{\"Kind\":\"autre\",\"State\":\"shown\"}");
        Assert.Null(etat.Commande(new object(), "deck.card", faux.RootElement));
        var c = Assert.Single(cartes);
        Assert.Equal(new DeckCard("start", "shown", "abc"), c);
    }

    [Fact]
    public void Au_score_le_rang_vise_est_la_ligne_au_dessus_du_record_perso()
    {
        var l = new DeckLancement(DeckModes.WorldScoring, "fbneo", "19xx", "19xx", "fbneo/19xx", "player", Rom19xx, "posed", "19xx-hangar", "Hangar", true,
            "official-default", ["19xx-hangar", "off"], null, null, DateTime.UtcNow);
        var board = DeckRankBoard.DuJeu("19xx", "1cc", ["1cc"]);
        DeckRankRow Ligne(int rang, string joueur, long valeur, bool moi = false) => new(rang, joueur, valeur, "home", moi);
        var table = new DeckRankTable([Ligne(1, "a", 900_000), Ligne(2, "b", 700_000), Ligne(3, "player", 500_000, true), Ligne(4, "c", 300_000)],
            DeckRankTable.Frais, DateTime.UtcNow, false, null, false);
        var r = DeckRankCalcul.Composer(l, board, table, "home", 0, null, false, "x");
        Assert.Equal("ranked", r.State);
        Assert.Equal(2, r.Target!.Rank);
        Assert.Equal(700_000, r.Target.Value);
        Assert.Equal(500_000, r.PersonalBest!.Value);
    }
}
