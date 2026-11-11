using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Pattern F (CDC PlayerDeck Patterns, F-9) : le record du monde de l'epreuve dans deck.trial (Comparisons.Record, premier du
/// classement lu) et les splits du record perso gardes sur la borne (deck.splits.pb, Milestones.PbSplitsMs / PbSegments).
/// </summary>
public sealed class DeckSplitsDuRecordTests : IDisposable
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static readonly Reglement Couteau = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v7.json")));

    private readonly string _ancien = DeckPbSplits.Fichier;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "deck-pb-splits-" + Guid.NewGuid().ToString("N") + ".json");

    public DeckSplitsDuRecordTests() => DeckPbSplits.Fichier = _tmp;

    public void Dispose()
    {
        DeckPbSplits.Fichier = _ancien;
        if (File.Exists(_tmp)) File.Delete(_tmp);
    }

    private static DeckLancement Lancement() => new(DeckModes.Speedrun, "fbneo", "mslug3", "metal-slug-3", "arcade/metal-slug-3", "nelfe",
        @"E:\RetroBat\roms\fbneo\mslug3.zip", "posed", "metal-slug-3-pixel", "Rebel Iron", true, "official-default",
        ["metal-slug-3-pixel", "off"], null, null, DateTime.UtcNow);

    private static DeckRankRow L(int rang, string joueur, long valeur, bool moi = false) => new(rang, joueur, valeur, "", moi);

    private static DeckTrialSession Session(string etat, long? officiel = null, long? pb = null)
        => new("s1", "a1", 1, etat, null, 1700, 1000, officiel, false, null, pb, new Dictionary<string, int>());

    private static DeckTrial Composer(DeckTrialPlan p, DeckTrialSession s, DeckRank? rang = null, DeckPbSplits.Trace? splits = null)
        => DeckTrialCalcul.Composer(p, "L1", s, rang, null, new Dictionary<string, int>(), new Dictionary<string, IReadOnlyList<int>>(), 0, [], [], splits);

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
    public void Les_splits_d_un_nouveau_record_se_gardent_et_reviennent_au_lancement_suivant()
    {
        var p = DeckTrialPlan.De(Couteau);
        var arrivee = Composer(p, Session(SessionSpeedrun.Finished, officiel: 7420));
        Assert.True(arrivee.Comparisons.NewPersonalBest);
        var fin = (long)Math.Round(7420 * 1000 / arrivee.Clock.FrameRate);
        string Corps(long frames, long dernier) => "{\"ChallengeId\":\"" + p.ChallengeId + "\",\"Frames\":" + frames
            + ",\"Segments\":[0,1,2],\"SplitsMs\":[40100,81250," + dernier + "]}";

        // refus : pas d'arrivee, autre temps, dernier split loin du temps officiel
        Assert.Equal("not_a_new_best", DeckPbSplits.Recevoir(JsonDocument.Parse(Corps(7420, fin)).RootElement, Composer(p, Session(SessionSpeedrun.Running)), DateTime.UtcNow).Refus);
        Assert.Equal("frames_mismatch", DeckPbSplits.Recevoir(JsonDocument.Parse(Corps(7400, fin)).RootElement, arrivee, DateTime.UtcNow).Refus);
        Assert.Equal("last_split_off_official_time", DeckPbSplits.Recevoir(JsonDocument.Parse(Corps(7420, fin + 5000)).RootElement, arrivee, DateTime.UtcNow).Refus);
        Assert.Null(DeckPbSplits.Lire(p.ChallengeId));

        var (gardee, refus) = DeckPbSplits.Recevoir(JsonDocument.Parse(Corps(7420, fin)).RootElement, arrivee, DateTime.UtcNow);
        Assert.Null(refus);
        Assert.Equal(new long[] { 40100, 81250, fin }, gardee!.SplitsMs);
        Assert.Equal(7420, DeckPbSplits.Lire(p.ChallengeId)!.Frames);

        // un temps plus lent ne remplace pas la trace gardee
        Assert.False(DeckPbSplits.Garder(gardee with { Frames = 7500 }));
        Assert.Equal(7420, DeckPbSplits.Lire(p.ChallengeId)!.Frames);

        // au lancement suivant : la reference des splits, tant qu'elle est celle du meilleur temps du journal
        var t = Composer(p, Session(SessionSpeedrun.Running, pb: 7420), splits: DeckPbSplits.Lire(p.ChallengeId));
        Assert.Equal(new long[] { 40100, 81250, fin }, t.Milestones!.PbSplitsMs);
        Assert.Equal(new long[] { 0, 1, 2 }, t.Milestones.PbSegments);
        Assert.Equal(0, t.Milestones.Total);
        var plusVite = Composer(p, Session(SessionSpeedrun.Running, pb: 7000), splits: DeckPbSplits.Lire(p.ChallengeId));
        Assert.Null(plusVite.Milestones);
    }
}
