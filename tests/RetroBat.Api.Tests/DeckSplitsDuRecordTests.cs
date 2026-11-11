using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Pattern F (CDC PlayerDeck Patterns, F-9) : le record du monde de l'epreuve dans deck.trial (Comparisons.Record, premier du
/// classement lu). Les splits (record perso, record du monde) n'ont qu'une source, le coeur hote des fantomes (integration
/// 1.9.169) : deck.splits.pb est refuse, les cles lues par le Deck 0.1.9 dans les fantomes sont tenues.
/// </summary>
public sealed class DeckSplitsDuRecordTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static readonly Reglement Couteau = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v7.json")));

    private static DeckLancement Lancement() => new(DeckModes.Speedrun, "fbneo", "mslug3", "metal-slug-3", "arcade/metal-slug-3", "nelfe",
        @"E:\RetroBat\roms\fbneo\mslug3.zip", "posed", "metal-slug-3-pixel", "Rebel Iron", true, "official-default",
        ["metal-slug-3-pixel", "off"], null, null, DateTime.UtcNow);

    private static DeckRankRow L(int rang, string joueur, long valeur, bool moi = false) => new(rang, joueur, valeur, "", moi);

    private static DeckTrialSession Session(string etat, long? officiel = null, long? pb = null)
        => new("s1", "a1", 1, etat, null, 1700, 1000, officiel, false, null, pb, new Dictionary<string, int>());

    private static DeckTrial Composer(DeckTrialPlan p, DeckTrialSession s, DeckRank? rang = null)
        => DeckTrialCalcul.Composer(p, "L1", s, rang, null, new Dictionary<string, int>(), new Dictionary<string, IReadOnlyList<int>>(), 0, [], []);

    [Fact]
    public void Le_premier_du_classement_au_temps_est_le_record_du_monde()
    {
        var epreuve = new DeckRankBoard(DeckRankBoard.Epreuve, "metal-slug-3", "speedrun-m1", DeckRankBoard.CleTemps, true, 1, 59.18, "SPEEDRUN");
        DeckRankRow[] temps = [L(2, "bob", 6200), L(1, "ace", 5918), L(3, "nelfe", 7102, moi: true)];
        var table = new DeckRankTable(temps, DeckRankTable.Frais, DateTime.UtcNow, false, 3, true);
        var course = DeckRankCalcul.Composer(Lancement(), epreuve, table, "home", null, null, false, "x");
        Assert.Equal(("ace", 5918L, "1:40.00"), (course.Leader!.Player, course.Leader.Value, course.Leader.Text));

        var p = DeckTrialPlan.De(Couteau);
        var t = Composer(p, Session(SessionSpeedrun.Running), course);
        Assert.Equal((5918L, 1, "ace"), (t.Comparisons.Record!.Frames, t.Comparisons.Record.Rank, t.Comparisons.Record.Player));

        // classement vide : ni premier, ni record
        var vide = DeckRankCalcul.Composer(Lancement(), epreuve, new DeckRankTable([], DeckRankTable.Frais, DateTime.UtcNow, false, 0, true), "home", null, null, false, "x");
        Assert.Null(vide.Leader);
        Assert.Null(Composer(p, Session(SessionSpeedrun.Running), vide).Comparisons.Record);
    }

    [Fact]
    public void Les_splits_mesures_par_le_Deck_sont_refuses_la_reference_est_celle_des_fantomes()
    {
        // Integration 1.9.169 : une seule source de splits, le coeur hote des fantomes ; deck.splits.pb ne se garde plus
        var deck = new DeckStateService(new SimpleEventBus(), new ApiContext());
        using var corps = JsonDocument.Parse("""{"ChallengeId":"x","Frames":7420,"Segments":[0,1,2],"SplitsMs":[40100,81250,125386]}""");
        using var accuse = JsonDocument.Parse(deck.Commande(new object(), "deck.splits.pb", corps.RootElement)!);
        Assert.Equal("deck.ack", accuse.RootElement.GetProperty("Type").GetString());
        var charge = accuse.RootElement.GetProperty("Payload");
        Assert.False(charge.GetProperty("Accepted").GetBoolean());
        Assert.Equal(DeckStateService.RefusDesSplitsDuDeck, charge.GetProperty("Reason").GetString());

        // les points de passage de deck.trial ne portent plus de splits du record (aucune cle PbSplitsMs / PbSegments)
        var t = Composer(DeckTrialPlan.De(Couteau), Session(SessionSpeedrun.Running, pb: 7420));
        var json = JsonSerializer.Serialize(t);
        Assert.DoesNotContain("PbSplitsMs", json);
        Assert.DoesNotContain("PbSegments", json);
    }

    [Fact]
    public void Les_cles_que_le_Deck_0_1_9_lit_dans_les_fantomes_sont_tenues()
    {
        // speedrun.ghost.gap : le Deck 0.1.9 lit "ghost" (= label de la liste) et "gap_ms"
        var ecart = JsonDocument.Parse(JsonSerializer.Serialize(new EcartAuFantome("ASHTASTE92 (WR)", "rp_X", true, "00000040:5", 5, 7, 16017, -2983))).RootElement;
        Assert.Equal("ASHTASTE92 (WR)", ecart.GetProperty("ghost").GetString());
        Assert.Equal(-2983, ecart.GetProperty("gap_ms").GetInt64());

        // speedrun.session, bloc ghosts : reference, list[{ id, label, tint, time_ms, status }] (+ role, splits)
        var bloc = new FantomesPublies("rp_X", null, 512, [new FantomePublie("rp_X", "ASHTASTE92 (WR)", "world", "#FFD23F", 26300, "ready",
            [new SplitPublie("00000040:1", 3517)])]);
        var g = JsonDocument.Parse(JsonSerializer.Serialize(bloc)).RootElement;
        Assert.Equal("rp_X", g.GetProperty("reference").GetString());
        var f = g.GetProperty("list")[0];
        Assert.Equal(("rp_X", "ASHTASTE92 (WR)", "#FFD23F", 26300L, "ready"),
            (f.GetProperty("id").GetString(), f.GetProperty("label").GetString(), f.GetProperty("tint").GetString(), f.GetProperty("time_ms").GetInt64(), f.GetProperty("status").GetString()));
        Assert.Equal(3517, f.GetProperty("splits")[0].GetProperty("ms").GetInt64());
    }
}
