using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Deck;

// ─────────────────────────────── le plan : ce que le reglement dit de l'epreuve (fixe au lancement) ───────────────────────────────

/// <summary>Un compteur du reglement : son libelle, le compte qui fait echouer (<c>FailsAt</c> = max + 1), sa penalite par unite.</summary>
public sealed record DeckTrialCounterPlan(string Id, string Label, int? Max, int? FailsAt, int? PenaltyFramesEach);

/// <summary>Une condition d'echec du reglement (« fail ») : l'action du .MEM et son libelle (« NO DEATH »).</summary>
public sealed record DeckTrialFailPlan(string Id, string Label, string? Action);

/// <summary>
/// Une efficacite : le rapport entre deux compteurs, « actions » (coups de couteau donnes) et « resultats » (ennemis touches),
/// chacun un compteur du reglement ou un compte d'affichage (<c>tallies</c>). <c>Format</c> : percent | ratio.
/// </summary>
public sealed record DeckTrialEfficiencyPlan(string Id, string Label, string Actions, string Results, string Format);

/// <summary>Un compte d'affichage : un evenement du .MEM compte pendant la tentative (jamais certifie).</summary>
public sealed record DeckTrialTally(string Id, string Label, string? Action, long? Address, int? Value);

/// <summary>
/// LE PLAN DE L'EPREUVE (CDC PlayerDeck H-D23), tire du reglement seul, fixe pour le lancement : il sert au rendu du decor
/// (les plaques de la couche epreuve sont dans le fond pose par RetroArch) et a chaque message <c>deck.trial</c>.
/// <c>Category</c> : challenge | speedrun. <c>Locked</c> : les adresses tenues par les verrous du reglement (un instrument du
/// theme lie a l'une d'elles peut ceder sa place, si le theme le declare). <c>Yields</c> : les instruments que le reglement
/// retire lui-meme (« display.yields »). <c>Milestones</c> : le nombre de points de passage.
/// </summary>
public sealed record DeckTrialPlan(
    string ChallengeId,
    string Ruleset,
    int Version,
    string Kind,
    string Category,
    string Title,
    string? Summary,
    string RomGroup,
    double FrameRate,
    string RankingMetric,
    bool LowerIsBetter,
    IReadOnlyList<DeckTrialCounterPlan> Counters,
    IReadOnlyList<DeckTrialFailPlan> Fails,
    IReadOnlyList<DeckTrialEfficiencyPlan> Efficiency,
    int Milestones,
    IReadOnlyList<string> Locked,
    IReadOnlyList<string> Yields)
{
    /// <summary>Les comptes d'affichage de l'efficacite (lus dans les signaux du .MEM par le service) ; pas publies.</summary>
    [JsonIgnore]
    public IReadOnlyList<DeckTrialTally> Tallies { get; init; } = [];

    /// <summary>Les points de passage du reglement (adresse, suite des valeurs) ; pas publies.</summary>
    [JsonIgnore]
    public IReadOnlyList<Etape> Steps { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Le plan en JSON, tel que le Deck le lit (<c>--trial</c> du rendu du decor). Stable : sert aussi de cle de cache.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Le plan d'un reglement. Libelles anglais (le Deck est en anglais), surcharges par « display.labels ». Pur.
    /// </summary>
    public static DeckTrialPlan De(Reglement r)
    {
        var aff = Affichage.Lire(r.AffichageJson);
        string Libelle(string cle, string defaut) => aff.Labels.TryGetValue(cle, out var l) && l.Length > 0 ? l : defaut;

        var compteurs = r.Compteurs.Select(c => new DeckTrialCounterPlan(c.Id, Libelle(c.Id, Mot(c.Id)), c.Maximum,
            c.Maximum is { } m ? m + 1 : null, c.PenaliteFrames is > 0 ? c.PenaliteFrames : null)).ToList();
        var echecs = new List<DeckTrialFailPlan>();
        foreach (var e in r.Echecs)
        {
            var id = e.Action ?? (e.Adresse is { } a ? Hex.Ecrire(a) : "fail");
            if (echecs.Any(x => x.Id == id)) continue;
            echecs.Add(new DeckTrialFailPlan(id, Libelle(id, LibelleDEchec(e.Action)), e.Action));
        }

        var ids = compteurs.Select(c => c.Id).Concat(aff.Tallies.Select(t => t.Id)).ToHashSet(StringComparer.Ordinal);
        var efficacite = aff.Efficiency.Where(x => ids.Contains(x.Actions) && ids.Contains(x.Results)).ToList();
        var critere = r.Rang.FirstOrDefault() ?? new CritereDeRang("frames", true);
        return new DeckTrialPlan(
            r.ChallengeId, r.Ruleset, r.Version, r.Kind, r.EstUnChallenge ? "challenge" : "speedrun",
            r.Titre("en"), r.Resume("en"), r.RomGroup, r.Cadence, critere.Mesure, critere.PlusPetitMeilleur,
            compteurs, echecs, efficacite, r.Etapes.Sum(e => Math.Max(0, e.Suite.Count - 1)),
            r.Verrous.Select(v => Hex.Ecrire(v.Adresse)).ToList(), aff.Yields)
        {
            Tallies = aff.Tallies,
            Steps = r.Etapes,
        };
    }

    /// <summary>« hits » -> « HITS », « enemy_kills » -> « ENEMY KILLS ».</summary>
    internal static string Mot(string id) => id.Replace('_', ' ').Replace('-', ' ').Trim().ToUpperInvariant();

    /// <summary>Le libelle d'une condition d'echec, par son action du .MEM : ce qu'il ne faut pas faire, en anglais.</summary>
    internal static string LibelleDEchec(string? action) => action switch
    {
        "LOSE_LIFE" => "NO DEATH",
        "GAME_OVER" => "NO GAME OVER",
        "AMMO_LOSE" => "NO AMMO SPENT",
        "CONTINUE_SCREEN" => "NO CONTINUE",
        "TIME_OVER" or "TIMER_OUT" => "BEAT THE TIMER",
        "PAUSE_ON" => "NO PAUSE",
        null or "" => "NO FAIL EVENT",
        var a => "NO " + Mot(a),
    };

    /// <summary>Le bloc « display » du reglement, lu sans jamais faire echouer le reglement (un bloc mal forme ne montre rien de plus).</summary>
    private sealed record Affichage(
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyList<string> Yields,
        IReadOnlyList<DeckTrialTally> Tallies,
        IReadOnlyList<DeckTrialEfficiencyPlan> Efficiency)
    {
        private static readonly Affichage Vide = new(new Dictionary<string, string>(), [], [], []);

        public static Affichage Lire(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Vide;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var d = doc.RootElement;
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (d.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Object)
                    foreach (var p in l.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String) labels[p.Name] = p.Value.GetString()!.ToUpperInvariant();
                var yields = Chaines(d, "yields");
                var tallies = new List<DeckTrialTally>();
                if (d.TryGetProperty("tallies", out var ts) && ts.ValueKind == JsonValueKind.Array)
                    foreach (var t in ts.EnumerateArray())
                    {
                        var id = Texte(t, "id");
                        if (id is null || tallies.Any(x => x.Id == id)) continue;
                        var ev = t.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object ? e : t;
                        long? adresse = Texte(ev, "address") is { } a ? Hex.Lire(a) : null;
                        int? valeur = Texte(ev, "value") is { } v ? (int)Hex.Lire(v) : ev.TryGetProperty("value", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : null;
                        var action = Texte(ev, "action");
                        if (action is null && adresse is null) continue;
                        tallies.Add(new DeckTrialTally(id, (Texte(t, "label") ?? Mot(id)).ToUpperInvariant(), action, adresse, valeur));
                    }

                var eff = new List<DeckTrialEfficiencyPlan>();
                if (d.TryGetProperty("efficiency", out var es) && es.ValueKind == JsonValueKind.Array)
                    foreach (var e in es.EnumerateArray())
                    {
                        var (id, actions, results) = (Texte(e, "id"), Texte(e, "actions"), Texte(e, "results"));
                        if (id is null || actions is null || results is null) continue;
                        var format = Texte(e, "format") is "ratio" ? "ratio" : "percent";
                        eff.Add(new DeckTrialEfficiencyPlan(id, (Texte(e, "label") ?? "EFFICIENCY").ToUpperInvariant(), actions, results, format));
                    }

                return new Affichage(labels, yields, tallies, eff);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException)
            {
                return Vide;
            }
        }

        private static List<string> Chaines(JsonElement e, string nom)
            => e.TryGetProperty(nom, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];

        private static string? Texte(JsonElement e, string nom)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }
}

// ─────────────────────────────── le message deck.trial ───────────────────────────────

/// <summary>
/// Le chrono officiel de la tentative. <c>StartClockMs</c> : l'heure de la borne au depart (meme horloge que le Deck) ; en
/// course (<c>Running</c>), le Deck le fait avancer lui-meme : (maintenant - StartClockMs) + <c>PenaltyFrames</c>, sans un
/// message par image. <c>Frames</c> : le chrono fige (arrivee, provisoire ou finie, ou tentative close), penalites comprises.
/// </summary>
public sealed record DeckTrialClock(double FrameRate, long? StartClockMs, bool Running, long? Frames, string? Text, bool Provisional,
    long PenaltyFrames, string? PenaltyText);

/// <summary>Un compteur en direct : valeur, maximum, compte qui fait echouer, restant (decompte), alerte, penalite cumulee.</summary>
public sealed record DeckTrialCounter(string Id, string Label, int Value, int? Max, int? FailsAt, int? Left, bool Warn,
    int? PenaltyFramesEach, long PenaltyFrames, string? PenaltyText, bool Tripped);

/// <summary>Une condition d'echec et son etat : ok (tenue), tripped (la tentative a echoue par elle), idle (pas de tentative partie).</summary>
public sealed record DeckTrialFail(string Id, string Label, string? Action, string State);

/// <summary>
/// Une efficacite en direct : les deux comptes, le pourcentage (resultats / actions, null sans action), le texte pret a
/// dessiner (« 62% » ou « 8/13 ») et l'historique court des tentatives closes de la session (le plus recent d'abord).
/// </summary>
public sealed record DeckTrialEfficiency(string Id, string Label, string Format, string ActionsLabel, string ResultsLabel, int Actions, int Results,
    int? Percent, string? Text, IReadOnlyList<int> History);

/// <summary>Une marque a battre (record perso, cible du rang vise) ou le rang fige a l'arrivee.</summary>
public sealed record DeckTrialMark(long Frames, string Text, int? Rank, string? Player, string Source);

/// <summary><c>PersonalBest</c> : le meilleur du journal local et du classement lu ; <c>Target</c> : le temps du rang vise ; <c>FinalRank</c> : le rang a l'arrivee.</summary>
public sealed record DeckTrialComparisons(DeckTrialMark? PersonalBest, DeckTrialMark? Target, int? FinalRank, bool NewPersonalBest);

/// <summary>Les points de passage : combien, combien atteints, et le temps de chacun (ms du chrono) ; les meilleurs de la session.</summary>
public sealed record DeckTrialMilestones(int Total, int Reached, IReadOnlyList<long> SplitsMs, IReadOnlyList<long> BestSplitsMs);

/// <summary>
/// deck.trial (CDC PlayerDeck H-D23) : l'epreuve en cours, pour la couche epreuve du Deck. Tout est tire du reglement et de la
/// session de l'API, sans reseau pendant la course (la cible et le record viennent du classement lu au lancement par le rang).
/// <c>State</c> : celui de la session (IDLE, ARMED, RUNNING, FINISHED, ABORTED, INVALID, RESTARTING) ; <c>Cause</c> : l'action
/// ou le compteur qui a fait echouer. Affichage seulement : ni le resultat, ni le passeport, ni le prevol, ni les verrous ne
/// changent.
/// </summary>
public sealed record DeckTrial(
    string? LaunchId,
    string ChallengeId,
    string Ruleset,
    string Category,
    string Kind,
    string Title,
    string? SessionId,
    string? AttemptId,
    int AttemptNumber,
    string State,
    string? Cause,
    DeckTrialClock Clock,
    IReadOnlyList<DeckTrialCounter> Counters,
    IReadOnlyList<DeckTrialFail> Fails,
    IReadOnlyList<DeckTrialEfficiency> Efficiency,
    DeckTrialComparisons Comparisons,
    DeckTrialMilestones? Milestones,
    IReadOnlyList<string> Locked,
    IReadOnlyList<string> Yields,
    long Revision);

/// <summary>Ce que la session dit a un instant (lu dans speedrun.session et speedrun.attempt.ended).</summary>
public sealed record DeckTrialSession(
    string? SessionId,
    string? AttemptId,
    int AttemptNumber,
    string State,
    string? Cause,
    long? StartFrame,
    long? StartClockMs,
    long? OfficialFrames,
    bool OfficialProvisional,
    long? EndFrame,
    long? PersonalBestFrames,
    IReadOnlyDictionary<string, int> Counters)
{
    public static readonly DeckTrialSession Avant = new(null, null, 0, "IDLE", null, null, null, null, false, null, null, new Dictionary<string, int>());

    /// <summary>L'instantane speedrun.session (JSON du contrat /ws/speedrun) ; null s'il n'est pas lisible.</summary>
    public static DeckTrialSession? Lire(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var compteurs = new Dictionary<string, int>(StringComparer.Ordinal);
        if (p.TryGetProperty("counters", out var c) && c.ValueKind == JsonValueKind.Object)
            foreach (var x in c.EnumerateObject())
                if (x.Value.ValueKind == JsonValueKind.Number && x.Value.TryGetInt32(out var n)) compteurs[x.Name] = n;
        JsonElement depart = p.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        JsonElement officiel = p.TryGetProperty("official", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        long? pb = p.TryGetProperty("comparisons", out var cp) && cp.ValueKind == JsonValueKind.Object
                   && cp.TryGetProperty("personalBest", out var b) && b.ValueKind == JsonValueKind.Object ? Long(b, "frames") : null;
        return new DeckTrialSession(Str(p, "sessionId"), Str(p, "attemptId"), (int)(Long(p, "attemptNumber") ?? 0), Str(p, "state") ?? "IDLE",
            Str(p, "cause"), Long(depart, "frame"), Long(depart, "clockMs"), Long(officiel, "frames"),
            officiel.ValueKind == JsonValueKind.Object && officiel.TryGetProperty("provisional", out var pv) && pv.ValueKind == JsonValueKind.True,
            null, pb, compteurs);
    }

    internal static string? Str(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long? Long(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
}

/// <summary>Le calcul du message, pur : il se teste sans bus ni reseau.</summary>
public static class DeckTrialCalcul
{
    /// <summary>L'alerte d'un compteur a maximum : au plus ce nombre restant, ou un dixieme du compte qui fait echouer.</summary>
    public static int SeuilDAlerte(int failsAt) => Math.Max(3, failsAt / 10);

    public const int Historique = 5;

    public static DeckTrial Composer(DeckTrialPlan plan, string? launchId, DeckTrialSession s, DeckRank? rang, DeckTrialMark? cibleRetenue,
        IReadOnlyDictionary<string, int> comptes, IReadOnlyDictionary<string, IReadOnlyList<int>> historiques, int etapesAtteintes,
        IReadOnlyList<long> temps, IReadOnlyList<long> meilleursTemps)
    {
        var cadence = plan.FrameRate > 0 ? plan.FrameRate : 60;
        var partie = s.StartClockMs is not null || s.StartFrame is not null;
        var echouee = s.State == SessionSpeedrun.Invalid && s.Cause is not null;

        // Les compteurs : valeur, decompte, penalite cumulee
        long penalite = 0;
        var compteurs = new List<DeckTrialCounter>(plan.Counters.Count);
        foreach (var c in plan.Counters)
        {
            var v = s.Counters.TryGetValue(c.Id, out var n) ? n : 0;
            var p = c.PenaltyFramesEach is { } e ? (long)e * v : 0;
            penalite += p;
            int? reste = c.FailsAt is { } f ? Math.Max(0, f - v) : null;
            compteurs.Add(new DeckTrialCounter(c.Id, c.Label, v, c.Max, c.FailsAt, reste, reste is { } r && c.FailsAt is { } fa && r <= SeuilDAlerte(fa),
                c.PenaltyFramesEach, p, p > 0 ? Secondes(p, cadence) : null, echouee && s.Cause == c.Id));
        }

        var echecs = plan.Fails.Select(f => new DeckTrialFail(f.Id, f.Label, f.Action,
            echouee && (s.Cause == f.Action || s.Cause == f.Id) ? "tripped" : partie ? "ok" : "idle")).ToList();

        // Le chrono : fige a l'arrivee (le temps officiel comprend deja les penalites, l'arrivee provisoire non) ou a l'echec
        long? fige = s.OfficialFrames is { } of ? of + (s.OfficialProvisional ? penalite : 0)
            : s.EndFrame is { } fin && s.StartFrame is { } dep && s.State != SessionSpeedrun.Running ? Math.Max(0, fin - dep) + penalite
            : !partie ? 0 : null;
        var court = s.State == SessionSpeedrun.Running && s.OfficialFrames is null && s.StartClockMs is not null;
        var chrono = new DeckTrialClock(cadence, s.StartClockMs, court, court ? null : fige, court || fige is null ? null : Chronometrage.Afficher(fige.Value, cadence),
            s.OfficialProvisional, penalite, penalite > 0 ? Secondes(penalite, cadence) : null);

        // L'efficacite : les comptes du reglement ou d'affichage
        int Compte(string id) => s.Counters.TryGetValue(id, out var n) ? n : comptes.TryGetValue(id, out var t) ? t : 0;
        string Nom(string id) => plan.Counters.FirstOrDefault(c => c.Id == id)?.Label ?? plan.Tallies.FirstOrDefault(t => t.Id == id)?.Label ?? DeckTrialPlan.Mot(id);
        var efficacite = plan.Efficiency.Select(e =>
        {
            int a = Compte(e.Actions), r = Compte(e.Results);
            int? pc = a > 0 ? (int)Math.Round(r * 100.0 / a, MidpointRounding.AwayFromZero) : null;
            var texte = e.Format == "ratio" ? (a > 0 ? r.ToString(CultureInfo.InvariantCulture) + "/" + a.ToString(CultureInfo.InvariantCulture) : null)
                : pc is { } x ? x.ToString(CultureInfo.InvariantCulture) + "%" : null;
            return new DeckTrialEfficiency(e.Id, e.Label, e.Format, Nom(e.Actions), Nom(e.Results), a, r, pc, texte,
                historiques.TryGetValue(e.Id, out var h) ? h : []);
        }).ToList();

        // Les comparaisons : le meilleur du journal local et du classement lu ; la cible du rang vise ; le rang fige
        DeckTrialMark? pb = null;
        if (s.PersonalBestFrames is { } local) pb = new DeckTrialMark(local, Chronometrage.Afficher(local, cadence), null, null, "local");
        if (rang?.PersonalBest is { } site && plan.RankingMetric == "frames"
            && (pb is null || DeckRankCalcul.Mieux(site.Value, pb.Frames, plan.LowerIsBetter)))
            pb = new DeckTrialMark(site.Value, site.Text ?? Chronometrage.Afficher(site.Value, cadence), site.Rank, site.Player, "board");
        var cible = rang?.Target is { } t2 && plan.RankingMetric == "frames"
            ? new DeckTrialMark(t2.Value, t2.Text ?? Chronometrage.Afficher(t2.Value, cadence), t2.Rank, t2.Player, "board")
            : cibleRetenue;
        var final = rang is { Final: true, Rank: { } rk } ? rk : (int?)null;
        var nouveau = s.State == SessionSpeedrun.Finished && s.OfficialFrames is { } ff && (pb is null || DeckRankCalcul.Mieux(ff, pb.Frames, plan.LowerIsBetter) || ff == pb.Frames && pb.Source == "local");

        var etapes = plan.Milestones > 0 ? new DeckTrialMilestones(plan.Milestones, etapesAtteintes, temps, meilleursTemps) : null;
        return new DeckTrial(launchId, plan.ChallengeId, plan.Ruleset, plan.Category, plan.Kind, plan.Title, s.SessionId, s.AttemptId, s.AttemptNumber,
            s.State, s.Cause, chrono, compteurs, echecs, efficacite, new DeckTrialComparisons(pb, cible, final, nouveau), etapes,
            plan.Locked, plan.Yields, 0);
    }

    /// <summary>« +3 s », « +1.5 s » : une penalite en secondes.</summary>
    public static string Secondes(long frames, double cadence)
        => "+" + (frames / cadence).ToString("0.#", CultureInfo.InvariantCulture) + " s";
}
