using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class RetroAchievementsLeaderboardHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<RetroAchievementsLeaderboardHistoryStore> _logger;

    public RetroAchievementsLeaderboardHistoryStore(ILogger<RetroAchievementsLeaderboardHistoryStore> logger)
    {
        _logger = logger;
    }

    public async Task SynchronizeCatalogAsync(RetroAchievementsSessionSnapshot session)
    {
        if (session.GameId is not > 0 || session.LeaderboardsById.Count == 0) return;
        var timed = session.LeaderboardsById.Values
            .Where(item => !item.Hidden && item.Format.Contains("TIME", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selectorsById = RetroAchievementsLeaderboardDefinitionRules.BuildLevelSelectors(timed.Where(IsLevelScoped));

        await _gate.WaitAsync();
        try
        {
            var history = await ReadAsync(session.GameId.Value);
            history.SchemaVersion = 2;
            history.GameId = session.GameId.Value;
            history.GameTitle = session.GameTitle;
            history.CatalogRevision = session.CatalogRevision;
            history.UpdatedAtUtc = DateTime.UtcNow;
            foreach (var leaderboard in timed)
            {
                var stored = history.Leaderboards.FirstOrDefault(item => item.LeaderboardId == leaderboard.Id);
                if (stored == null)
                {
                    stored = new RetroAchievementsStoredLeaderboard { LeaderboardId = leaderboard.Id };
                    history.Leaderboards.Add(stored);
                }
                stored.Title = leaderboard.Title;
                stored.Description = leaderboard.Description;
                stored.Format = leaderboard.Format;
                stored.Scope = IsLevelScoped(leaderboard) ? "level" : "game";
                stored.LowerIsBetter = leaderboard.LowerIsBetter;
                stored.DefinitionHash = Hash(leaderboard.Definition);
                stored.StartSelectors = selectorsById.GetValueOrDefault(leaderboard.Id) ?? new();
                stored.InitialCandidate = stored.Scope == "level" &&
                    stored.StartSelectors.Count > 0 &&
                    stored.StartSelectors.All(item => item.Value == 0);
            }
            history.Leaderboards = history.Leaderboards.OrderBy(item => item.LeaderboardId).ToList();
            await WriteAsync(history);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RetroAchievementsLeaderboardResolution?> ResolveFromStartSelectorsAsync(
        int gameId,
        IReadOnlyDictionary<long, long> memory,
        bool allowInitialCandidate,
        string username)
    {
        await _gate.WaitAsync();
        try
        {
            var history = await ReadAsync(gameId);
            var matches = history.Leaderboards
                .Where(item => item.Scope.Equals("level", StringComparison.OrdinalIgnoreCase) && item.StartSelectors.Count > 0)
                .Select(item =>
                {
                    var known = item.StartSelectors.Where(selector => memory.ContainsKey(selector.Address)).ToList();
                    var valid = known.Count > 0 && known.All(selector => memory[selector.Address] == selector.Value);
                    return new { Leaderboard = item, Score = valid ? known.Count : 0 };
                })
                .Where(item => item.Score > 0)
                .ToList();

            List<RetroAchievementsStoredLeaderboard> candidates;
            if (matches.Count > 0)
            {
                var bestScore = matches.Max(item => item.Score);
                candidates = matches.Where(item => item.Score == bestScore).Select(item => item.Leaderboard).ToList();
                if (candidates.Count > 1)
                    candidates = DisambiguatePartialSelectorTie(candidates, memory);
            }
            else if (allowInitialCandidate)
            {
                candidates = history.Leaderboards.Where(item => item.InitialCandidate).ToList();
            }
            else
            {
                return null;
            }

            var selected = SelectLastSubmitted(candidates) ?? (candidates.Count == 1 ? candidates[0] : null);
            return selected == null ? null : BuildResolution(selected, username);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RetroAchievementsLeaderboardResolution?> ResolveAsync(
        int gameId,
        RetroAchievementsLeaderboardContext context,
        string username)
    {
        if (!context.IsDiscriminating) return null;

        await _gate.WaitAsync();
        try
        {
            var history = await ReadAsync(gameId);
            var match = history.Leaderboards
                .SelectMany(leaderboard => leaderboard.Contexts.Select(item => new HistoryMatch(leaderboard, item)))
                .Where(item => item.Context.Signature.Equals(context.Signature, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Context.Observations)
                .ThenByDescending(item => item.Context.LastObservedAtUtc)
                .FirstOrDefault();
            if (match == null) return null;
            return BuildResolution(match.Leaderboard, username);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RetroAchievementsLeaderboardResolution?> ResolveLeaderboardAsync(
        int gameId,
        int leaderboardId,
        string username)
    {
        await _gate.WaitAsync();
        try
        {
            var history = await ReadAsync(gameId);
            var leaderboard = history.Leaderboards.FirstOrDefault(item => item.LeaderboardId == leaderboardId);
            return leaderboard == null ? null : BuildResolution(leaderboard, username);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RetroAchievementsLeaderboardResolution?> ResolveLastSubmittedAsync(
        int gameId,
        IReadOnlyCollection<int> leaderboardIds,
        string username)
    {
        if (leaderboardIds.Count == 0) return null;
        var acceptedIds = leaderboardIds.ToHashSet();
        await _gate.WaitAsync();
        try
        {
            var history = await ReadAsync(gameId);
            var leaderboard = history.Leaderboards
                .Where(item => acceptedIds.Contains(item.LeaderboardId))
                .Select(item => new
                {
                    Leaderboard = item,
                    LastSubmittedAtUtc = item.Contexts
                        .Where(context => !string.IsNullOrWhiteSpace(context.LastSubmittedValue) ||
                                          !string.IsNullOrWhiteSpace(context.LastFormattedValue))
                        .Select(context => (DateTime?)context.LastObservedAtUtc)
                        .Max()
                })
                .Where(item => item.LastSubmittedAtUtc != null)
                .OrderByDescending(item => item.LastSubmittedAtUtc)
                .Select(item => item.Leaderboard)
                .FirstOrDefault();
            return leaderboard == null ? null : BuildResolution(leaderboard, username);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RetroAchievementsLeaderboardResolution BuildResolution(
        RetroAchievementsStoredLeaderboard leaderboard,
        string username)
    {
        var personalBest = leaderboard.UserBests.FirstOrDefault(item =>
            item.User.Equals(username, StringComparison.OrdinalIgnoreCase));
        return new RetroAchievementsLeaderboardResolution
        {
            LeaderboardId = leaderboard.LeaderboardId,
            PersonalBestScore = personalBest?.Score,
            PersonalBestFormattedScore = personalBest?.FormattedScore ?? string.Empty
        };
    }

    // Several level leaderboards tie on the KNOWN start selectors when a discriminating
    // selector address was never emitted — a change-triggered progression byte still
    // sitting at its power-on value (0). Assume unknown selectors equal 0 and keep only
    // the leaderboard(s) fully satisfied under that assumption (e.g. Green Hill Act 1 vs
    // Act 2/3 at cold start). Prefer the initial candidate as a last resort; never let
    // the caller fall back to "last submitted", which flips to the wrong act.
    private static List<RetroAchievementsStoredLeaderboard> DisambiguatePartialSelectorTie(
        List<RetroAchievementsStoredLeaderboard> candidates,
        IReadOnlyDictionary<long, long> memory)
    {
        var exact = candidates
            .Where(item => item.StartSelectors.All(selector =>
                memory.TryGetValue(selector.Address, out var value) ? value == selector.Value : selector.Value == 0))
            .ToList();
        if (exact.Count == 1) return exact;
        if (exact.Count > 1) candidates = exact;
        var initial = candidates.Where(item => item.InitialCandidate).ToList();
        return initial.Count == 1 ? initial : candidates;
    }

    private static RetroAchievementsStoredLeaderboard? SelectLastSubmitted(
        IEnumerable<RetroAchievementsStoredLeaderboard> leaderboards)
        => leaderboards
            .Select(item => new
            {
                Leaderboard = item,
                LastSubmittedAtUtc = item.Contexts
                    .Where(context => !string.IsNullOrWhiteSpace(context.LastSubmittedValue) ||
                                      !string.IsNullOrWhiteSpace(context.LastFormattedValue))
                    .Select(context => (DateTime?)context.LastObservedAtUtc)
                    .Max()
            })
            .Where(item => item.LastSubmittedAtUtc != null)
            .OrderByDescending(item => item.LastSubmittedAtUtc)
            .Select(item => item.Leaderboard)
            .FirstOrDefault();

    private static bool IsLevelScoped(RetroAchievementsLeaderboardInfo leaderboard)
    {
        var text = $"{leaderboard.Title} {leaderboard.Description}";
        return text.Contains("act", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("level", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("stage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("zone", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("course", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("lap", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("mission", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record HistoryMatch(
        RetroAchievementsStoredLeaderboard Leaderboard,
        RetroAchievementsStoredContext Context);

    public async Task RecordSubmissionAsync(
        RetroAchievementsSessionSnapshot session,
        RetroAchievementsLeaderboardInfo leaderboard,
        RetroAchievementsLeaderboardContext context,
        string value,
        string formattedValue,
        int? rank,
        long? bestScore,
        string bestFormattedScore)
    {
        if (session.GameId is not > 0 || !context.IsDiscriminating) return;

        await _gate.WaitAsync();
        try
        {
            var history = await ReadAsync(session.GameId.Value);
            history.SchemaVersion = 2;
            history.GameId = session.GameId.Value;
            history.GameTitle = session.GameTitle;
            history.CatalogRevision = session.CatalogRevision;
            history.UpdatedAtUtc = DateTime.UtcNow;

            var storedLeaderboard = history.Leaderboards.FirstOrDefault(item => item.LeaderboardId == leaderboard.Id);
            if (storedLeaderboard == null)
            {
                storedLeaderboard = new RetroAchievementsStoredLeaderboard { LeaderboardId = leaderboard.Id };
                history.Leaderboards.Add(storedLeaderboard);
            }

            storedLeaderboard.Title = leaderboard.Title;
            storedLeaderboard.Description = leaderboard.Description;
            storedLeaderboard.Format = leaderboard.Format;
            storedLeaderboard.Scope = "level";
            storedLeaderboard.LowerIsBetter = leaderboard.LowerIsBetter;
            storedLeaderboard.DefinitionHash = Hash(leaderboard.Definition);

            var submittedScore = long.TryParse(value, out var parsedSubmittedScore) ? parsedSubmittedScore : (long?)null;
            var candidateBestScore = bestScore ?? submittedScore;
            var candidateBestFormatted = !string.IsNullOrWhiteSpace(bestFormattedScore)
                ? bestFormattedScore
                : formattedValue;
            if (!string.IsNullOrWhiteSpace(session.Username) && candidateBestScore != null)
            {
                var userBest = storedLeaderboard.UserBests.FirstOrDefault(item =>
                    item.User.Equals(session.Username, StringComparison.OrdinalIgnoreCase));
                if (userBest == null)
                {
                    userBest = new RetroAchievementsStoredUserBest { User = session.Username };
                    storedLeaderboard.UserBests.Add(userBest);
                }

                var improves = userBest.Score == null ||
                    (leaderboard.LowerIsBetter && candidateBestScore.Value < userBest.Score.Value) ||
                    (!leaderboard.LowerIsBetter && candidateBestScore.Value > userBest.Score.Value);
                if (improves || bestScore != null)
                {
                    userBest.Score = candidateBestScore;
                    userBest.FormattedScore = candidateBestFormatted;
                    userBest.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            var storedContext = storedLeaderboard.Contexts.FirstOrDefault(item =>
                item.Signature.Equals(context.Signature, StringComparison.OrdinalIgnoreCase));
            if (storedContext == null)
            {
                storedContext = new RetroAchievementsStoredContext { Signature = context.Signature };
                storedLeaderboard.Contexts.Add(storedContext);
            }

            storedContext.SystemId = context.SystemId;
            storedContext.Rom = context.Rom;
            storedContext.TimerSourceKey = context.TimerSourceKey;
            storedContext.Progression = new SortedDictionary<string, long>(context.Progression, StringComparer.OrdinalIgnoreCase);
            storedContext.ContextLabel = context.ContextLabel;
            storedContext.Confidence = context.Progression.Count > 0 ? "high" : "medium";
            storedContext.Observations++;
            storedContext.LastObservedAtUtc = DateTime.UtcNow;
            storedContext.LastSubmittedValue = value;
            storedContext.LastFormattedValue = formattedValue;
            storedContext.LastRank = rank;

            history.Leaderboards = history.Leaderboards.OrderBy(item => item.LeaderboardId).ToList();
            foreach (var item in history.Leaderboards)
            {
                item.Contexts = item.Contexts.OrderBy(contextItem => contextItem.Signature, StringComparer.OrdinalIgnoreCase).ToList();
                item.UserBests = item.UserBests.OrderBy(userBest => userBest.User, StringComparer.OrdinalIgnoreCase).ToList();
            }
            await WriteAsync(history);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist RetroAchievements leaderboard history for game {GameId}", session.GameId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RetroAchievementsLeaderboardHistory> ReadAsync(int gameId)
    {
        var path = ResolvePath(gameId);
        if (!File.Exists(path)) return new RetroAchievementsLeaderboardHistory { GameId = gameId };
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<RetroAchievementsLeaderboardHistory>(stream, JsonOptions)
                ?? new RetroAchievementsLeaderboardHistory { GameId = gameId };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to read RetroAchievements leaderboard history {Path}", path);
            return new RetroAchievementsLeaderboardHistory { GameId = gameId };
        }
    }

    private static async Task WriteAsync(RetroAchievementsLeaderboardHistory history)
    {
        var path = ResolvePath(history.GameId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, history, JsonOptions);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string ResolvePath(int gameId)
        => Path.Combine(RetroBatPaths.PluginRoot, "resources", "ra", "leaderboards", gameId + ".json");

    private static string Hash(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class RetroAchievementsLeaderboardContext
{
    public string SystemId { get; init; } = string.Empty;
    public string Rom { get; init; } = string.Empty;
    public string TimerSourceKey { get; init; } = string.Empty;
    public SortedDictionary<string, long> Progression { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string ContextLabel { get; init; } = string.Empty;
    public bool IsDiscriminating => Progression.Count > 0 || !string.IsNullOrWhiteSpace(ContextLabel);
    public string Signature => BuildSignature();

    private string BuildSignature()
    {
        var progression = string.Join("|", Progression.Select(item => $"{item.Key.ToUpperInvariant()}={item.Value}"));
        var raw = $"{SystemId.Trim().ToLowerInvariant()}|{Rom.Trim().ToLowerInvariant()}|{TimerSourceKey.Trim().ToUpperInvariant()}|{progression}|{ContextLabel.Trim().ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..24];
    }
}

public sealed class RetroAchievementsLeaderboardHistory
{
    public int SchemaVersion { get; set; } = 2;
    public int GameId { get; set; }
    public string GameTitle { get; set; } = string.Empty;
    public string CatalogRevision { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public List<RetroAchievementsStoredLeaderboard> Leaderboards { get; set; } = new();
}

public sealed class RetroAchievementsStoredLeaderboard
{
    public int LeaderboardId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string Scope { get; set; } = "level";
    public bool LowerIsBetter { get; set; }
    public string DefinitionHash { get; set; } = string.Empty;
    public List<RetroAchievementsStoredMemorySelector> StartSelectors { get; set; } = new();
    public bool InitialCandidate { get; set; }
    public List<RetroAchievementsStoredContext> Contexts { get; set; } = new();
    public List<RetroAchievementsStoredUserBest> UserBests { get; set; } = new();
}

public sealed class RetroAchievementsStoredMemorySelector
{
    public long Address { get; set; }
    public string AddressHex => $"0x{Address:X}";
    public long Value { get; set; }
}

public sealed class RetroAchievementsStoredUserBest
{
    public string User { get; set; } = string.Empty;
    public long? Score { get; set; }
    public string FormattedScore { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class RetroAchievementsLeaderboardResolution
{
    public int LeaderboardId { get; set; }
    public long? PersonalBestScore { get; set; }
    public string PersonalBestFormattedScore { get; set; } = string.Empty;
}

public sealed class RetroAchievementsStoredContext
{
    public string Signature { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public string TimerSourceKey { get; set; } = string.Empty;
    public SortedDictionary<string, long> Progression { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ContextLabel { get; set; } = string.Empty;
    public string Confidence { get; set; } = "high";
    public int Observations { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
    public string LastSubmittedValue { get; set; } = string.Empty;
    public string LastFormattedValue { get; set; } = string.Empty;
    public int? LastRank { get; set; }
}
