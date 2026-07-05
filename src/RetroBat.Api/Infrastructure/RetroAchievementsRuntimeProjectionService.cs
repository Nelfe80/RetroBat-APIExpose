using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Passive bridge between emulator/runtime providers and the public RA contract.
/// It never edits emulator configuration and never performs network requests.
/// </summary>
public sealed class RetroAchievementsRuntimeProjectionService : IHostedService, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly RetroAchievementsService _retroAchievements;
    private readonly ILogger<RetroAchievementsRuntimeProjectionService> _logger;
    private IDisposable? _subscription;

    public RetroAchievementsRuntimeProjectionService(
        IEventBus eventBus,
        RetroAchievementsService retroAchievements,
        ILogger<RetroAchievementsRuntimeProjectionService> logger)
    {
        _eventBus = eventBus;
        _retroAchievements = retroAchievements;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void OnEvent(EventEnvelope envelope)
    {
        try
        {
            if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envelope.Type, "ui.game.ended.raw", StringComparison.OrdinalIgnoreCase))
            {
                _ = _retroAchievements.EndSessionAsync("game-ended");
                return;
            }

            if (envelope.Type.StartsWith("retroachievements.runtime.challenge.", StringComparison.OrdinalIgnoreCase))
            {
                var value = ConvertPayload<RetroAchievementsChallengeEvent>(envelope.Payload);
                if (value != null)
                {
                    value.State = envelope.Type[(envelope.Type.LastIndexOf('.') + 1)..];
                    _ = _retroAchievements.PublishChallengeAsync(value, envelope.CorrelationId);
                }
                return;
            }

            if (envelope.Type.StartsWith("retroachievements.runtime.leaderboard.", StringComparison.OrdinalIgnoreCase))
            {
                if (!_retroAchievements.AreLeaderboardsEnabled())
                {
                    _logger.LogDebug("Ignoring runtime RetroAchievements leaderboard event while ES leaderboards are disabled: {EventType}", envelope.Type);
                    return;
                }

                _ = PublishRuntimeLeaderboardAsync(envelope);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to project runtime RetroAchievements event {EventType}", envelope.Type);
        }
    }

    private async Task PublishRuntimeLeaderboardAsync(EventEnvelope envelope)
    {
        try
        {
            var value = ConvertPayload<RetroAchievementsLeaderboardEvent>(envelope.Payload);
            if (value == null) return;

            value.State = envelope.Type[(envelope.Type.LastIndexOf('.') + 1)..];
            await EnrichLeaderboardEventAsync(value);
            await _retroAchievements.PublishLeaderboardAsync(value, envelope.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to enrich runtime RetroAchievements leaderboard event {EventType}", envelope.Type);
        }
    }

    private async Task EnrichLeaderboardEventAsync(RetroAchievementsLeaderboardEvent value)
    {
        if (value.LeaderboardId is not > 0) return;
        var session = _retroAchievements.GetSession();
        if (!session.LeaderboardsById.TryGetValue(value.LeaderboardId.Value, out var leaderboard)) return;

        if (string.IsNullOrWhiteSpace(value.Title)) value.Title = leaderboard.Title;
        if (value.GameId == null) value.GameId = session.GameId;
        if (string.IsNullOrWhiteSpace(value.User)) value.User = session.Username;
        if (string.IsNullOrWhiteSpace(value.BadgePath)) value.BadgePath = leaderboard.BadgeUrl;
        if (string.IsNullOrWhiteSpace(value.BadgeRemoteUrl)) value.BadgeRemoteUrl = leaderboard.BadgeRemoteUrl;
        if (string.IsNullOrWhiteSpace(value.ReferenceUser)) value.ReferenceUser = leaderboard.TopUser;
        if (string.IsNullOrWhiteSpace(value.ReferenceFormattedScore)) value.ReferenceFormattedScore = leaderboard.TopFormattedScore;
        value.ReferenceRank ??= string.IsNullOrWhiteSpace(leaderboard.TopUser) ? null : 1;

        if (!IsTimedLeaderboard(leaderboard) || !IsSpeedrunDisplayState(value.State)) return;
        if (value.State.Equals("started", StringComparison.OrdinalIgnoreCase)) return;

        var entries = await _retroAchievements.GetLeaderboardEntriesAsync(value.LeaderboardId.Value, 50000);
        value.ReferenceEntries = BuildDisplayReferenceEntries(entries, lowerIsBetter: true);
        var reference = value.ReferenceEntries.FirstOrDefault();
        if (reference != null)
        {
            value.ReferenceRank = reference.Rank;
            value.ReferenceUser = reference.User;
            value.ReferenceFormattedScore = reference.FormattedScore;
        }

        if (value.Rank == null && !string.IsNullOrWhiteSpace(value.FormattedValue))
            value.Rank = InferRank(value.FormattedValue, entries, lowerIsBetter: true);
    }

    private static bool IsTimedLeaderboard(RetroAchievementsLeaderboardInfo leaderboard)
        => leaderboard.Format.Contains("TIME", StringComparison.OrdinalIgnoreCase) ||
           leaderboard.TopFormattedScore.Contains(':', StringComparison.OrdinalIgnoreCase);

    private static bool IsResultState(string state)
        => state.Equals("submitting", StringComparison.OrdinalIgnoreCase)
            || state.Equals("submitted", StringComparison.OrdinalIgnoreCase)
            || state.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpeedrunDisplayState(string state)
        => state.Equals("started", StringComparison.OrdinalIgnoreCase) || IsResultState(state);

    private static List<RetroAchievementsLeaderboardEntry> BuildDisplayReferenceEntries(
        IReadOnlyList<RetroAchievementsLeaderboardEntry> entries,
        bool lowerIsBetter)
    {
        var parsed = entries
            .Select(entry => new
            {
                Entry = entry,
                Seconds = TryParseRaceTime(entry.FormattedScore, out var seconds) ? seconds : (double?)null
            })
            .Where(item => item.Seconds is > 0)
            .OrderBy(item => lowerIsBetter ? item.Seconds!.Value : -item.Seconds!.Value)
            .ThenBy(item => item.Entry.Rank)
            .Take(5000)
            .Select(item => item.Entry)
            .ToList();

        return parsed.Count > 0 ? parsed : entries.Take(5000).ToList();
    }

    private static int? InferRank(string formattedValue, IReadOnlyList<RetroAchievementsLeaderboardEntry> entries, bool lowerIsBetter)
    {
        if (!TryParseRaceTime(formattedValue, out var currentSeconds) || entries.Count == 0) return null;
        var valid = entries
            .Select(entry => new { Entry = entry, Seconds = TryParseRaceTime(entry.FormattedScore, out var seconds) ? seconds : (double?)null })
            .Where(item => item.Seconds is > 0)
            .OrderBy(item => item.Seconds)
            .ThenBy(item => item.Entry.Rank)
            .ToList();
        if (valid.Count == 0) return null;

        if (lowerIsBetter)
        {
            if (currentSeconds < valid[0].Seconds!.Value) return 1;
            foreach (var item in valid)
                if (currentSeconds <= item.Seconds!.Value + 0.0001d) return item.Entry.Rank;
            return valid.Max(item => item.Entry.Rank) + 1;
        }

        var descending = valid.OrderByDescending(item => item.Seconds).ThenBy(item => item.Entry.Rank).ToList();
        if (currentSeconds > descending[0].Seconds!.Value) return 1;
        foreach (var item in descending)
            if (currentSeconds >= item.Seconds!.Value - 0.0001d) return item.Entry.Rank;
        return descending.Max(item => item.Entry.Rank) + 1;
    }

    private static bool TryParseRaceTime(string value, out double totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out totalSeconds);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            totalSeconds = minutes * 60d + seconds;
            return true;
        }
        return false;
    }

    private static T? ConvertPayload<T>(object? payload)
    {
        if (payload == null) return default;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
