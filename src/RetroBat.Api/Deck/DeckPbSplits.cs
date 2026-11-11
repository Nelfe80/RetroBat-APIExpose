using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck;

/// <summary>
/// LES SPLITS DU RECORD PERSO, GARDES SUR LA BORNE (CDC PlayerDeck Patterns, pattern F, F-5 et F-9) : a l'arrivee d'une
/// tentative qui bat le record perso, le Deck envoie les temps de passage de ses segments (<c>deck.splits.pb</c> : valeur de
/// chaque segment, temps cumule a sa fin, en ms du chrono officiel) ; l'API les garde a cote du journal des tentatives, un
/// jeu de splits par epreuve (le meilleur temps), et les republie dans <c>deck.trial</c> (<c>Milestones.PbSplitsMs</c> et
/// <c>PbSegments</c>) au lancement suivant : ils deviennent la reference des splits du Deck. Affichage seulement : ni le
/// resultat, ni le passeport, ni le classement n'en dependent ; le fichier peut etre efface sans dommage.
/// </summary>
public static class DeckPbSplits
{
    /// <summary>Une trace : le temps officiel du record (images), la valeur de chaque segment et son temps cumule (ms).</summary>
    public sealed record Trace(string ChallengeId, long Frames, IReadOnlyList<long> Segments, IReadOnlyList<long> SplitsMs, DateTime AtUtc);

    /// <summary>Le nombre de segments au plus (celui du Deck, SplitTrack.Max).</summary>
    public const int Max = 40;

    /// <summary>Tolerance entre la fin du dernier segment et le temps officiel (le Deck ferme le dernier segment sur lui).</summary>
    public const long ToleranceMs = 1000;

    private static readonly object Verrou = new();

    /// <summary>Le fichier (a cote du journal des tentatives) ; remplacable par les tests.</summary>
    public static string Fichier { get; set; } = Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "deck-pb-splits.json");

    private static Dictionary<string, Trace> Tout()
    {
        try
        {
            if (!File.Exists(Fichier)) return new(StringComparer.OrdinalIgnoreCase);
            var d = JsonSerializer.Deserialize<Dictionary<string, Trace>>(File.ReadAllText(Fichier));
            return d is null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, Trace>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>La trace gardee pour cette epreuve, ou null.</summary>
    public static Trace? Lire(string? challengeId)
    {
        if (string.IsNullOrWhiteSpace(challengeId)) return null;
        lock (Verrou) return Tout().TryGetValue(challengeId, out var t) ? t : null;
    }

    /// <summary>Pourquoi une trace est refusee (null : acceptable). <paramref name="cadence"/> : images par seconde de l'epreuve.</summary>
    public static string? Refus(Trace t, double cadence)
    {
        if (string.IsNullOrWhiteSpace(t.ChallengeId)) return "challenge_required";
        if (t.Frames <= 0) return "frames_required";
        if (t.Segments.Count == 0 || t.Segments.Count != t.SplitsMs.Count || t.Segments.Count > Max) return "splits_shape";
        long avant = 0;
        foreach (var s in t.SplitsMs)
        {
            if (s < avant) return "splits_not_increasing";
            avant = s;
        }

        foreach (var v in t.Segments)
            if (v < 0 || v >= 64) return "segment_out_of_range";
        var officiel = (long)Math.Round(t.Frames * 1000 / (cadence > 0 ? cadence : 60));
        return Math.Abs(avant - officiel) > ToleranceMs ? "last_split_off_official_time" : null;
    }

    /// <summary>Garde la trace si elle est la premiere ou au moins aussi rapide que celle gardee. Rend vrai si elle est ecrite.</summary>
    public static bool Garder(Trace t)
    {
        lock (Verrou)
        {
            var tout = Tout();
            if (tout.TryGetValue(t.ChallengeId, out var avant) && avant.Frames < t.Frames) return false;
            tout[t.ChallengeId] = t;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Fichier)!);
                var tmp = Fichier + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(tout, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, Fichier, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Le message <c>deck.splits.pb</c> du Deck, contre l'epreuve en cours : accepte seulement a l'arrivee (FINISHED) d'une
    /// tentative qui bat le record perso, pour cette epreuve et ce temps officiel. Rend (trace gardee ou null, raison du refus).
    /// </summary>
    public static (Trace? Gardee, string? Refus) Recevoir(JsonElement corps, DeckTrial? epreuve, DateTime maintenant)
    {
        if (epreuve is null) return (null, "no_trial");
        if (corps.ValueKind != JsonValueKind.Object) return (null, "invalid");
        string? Str(string n) => (corps.TryGetProperty(n, out var v) || corps.TryGetProperty(char.ToLowerInvariant(n[0]) + n[1..], out v))
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? Num(string n) => (corps.TryGetProperty(n, out var v) || corps.TryGetProperty(char.ToLowerInvariant(n[0]) + n[1..], out v))
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
        List<long> Liste(string n)
        {
            var l = new List<long>();
            if ((corps.TryGetProperty(n, out var v) || corps.TryGetProperty(char.ToLowerInvariant(n[0]) + n[1..], out v)) && v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var y) && l.Count <= Max) l.Add(y);
            return l;
        }

        var id = Str("ChallengeId");
        var frames = Num("Frames");
        if (!string.Equals(id, epreuve.ChallengeId, StringComparison.OrdinalIgnoreCase)) return (null, "other_challenge");
        if (epreuve.State != "FINISHED" || !epreuve.Comparisons.NewPersonalBest) return (null, "not_a_new_best");
        if (frames is null || epreuve.Clock.Frames != frames) return (null, "frames_mismatch");
        var t = new Trace(epreuve.ChallengeId, frames.Value, Liste("Segments"), Liste("SplitsMs"), maintenant);
        if (Refus(t, epreuve.Clock.FrameRate) is { } r) return (null, r);
        return Garder(t) ? (t, null) : (null, "slower_than_kept");
    }
}
