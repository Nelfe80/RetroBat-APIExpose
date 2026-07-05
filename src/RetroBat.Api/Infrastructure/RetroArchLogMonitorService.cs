using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Monitors the EmulationStation stdout log (es_launch_stdout.log) for [RCHEEVOS]: lines
/// emitted by RetroArch/rcheevos at verbose log level.
///
/// Detected events are published to the internal event bus where
/// RetroAchievementsRuntimeProjectionService converts them to public RA contracts:
///   retroachievements.runtime.leaderboard.started  → retroachievements.leaderboard.changed (state=started)
///   retroachievements.runtime.leaderboard.failed   → retroachievements.leaderboard.changed (state=failed)
///   retroachievements.runtime.challenge.active     → retroachievements.challenge.changed   (state=active)
///   retroachievements.runtime.challenge.inactive   → retroachievements.challenge.changed   (state=inactive)
///
/// Limitations:
///   Challenge indicators (SHOW/HIDE) are NOT written to the log by rcheevos — they only
///   update the gfx_widgets OSD. Challenge detection via log is therefore limited to any
///   verbose messages that future RetroArch/rcheevos versions may add. The framework is
///   ready to process them when they appear.
/// </summary>
public sealed class RetroArchLogMonitorService : BackgroundService
{
    private const string RcheevosTag = "[RCHEEVOS]"; // no colon — actual format: "[INFO] [RCHEEVOS] Message text"
    private const string InfoPrefix  = "[INFO]";

    // RetroArch is launched by ES which captures its stdout in es_launch_stdout.log.
    // The [RCHEEVOS]: messages appear there with log_verbosity enabled (set by MameStartupConfigHostedService).
    private static readonly string[] LogCandidates =
    {
        RetroBatPaths.EsLaunchStdoutLogPath,
    };

    private readonly IEventBus _eventBus;
    private readonly ILogger<RetroArchLogMonitorService> _logger;

    public RetroArchLogMonitorService(IEventBus eventBus, ILogger<RetroArchLogMonitorService> logger)
    {
        _eventBus = eventBus;
        _logger   = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        // Monitor all candidate log files in parallel.
        // RetroArch may write to retroarch.log (log_to_file=true) OR to stdout captured by ES
        // (es_launch_stdout.log). Both are tailed simultaneously so we catch messages regardless
        // of which file RetroArch uses in this installation.
        => Task.WhenAll(LogCandidates.Select(path => TailFileAsync(path, stoppingToken)));

    private async Task TailFileAsync(string logPath, CancellationToken stoppingToken)
    {
        // Wait until the file exists (ES/RetroArch may not have started yet)
        while (!stoppingToken.IsCancellationRequested && !File.Exists(logPath))
            await Task.Delay(5_000, stoppingToken).ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested) return;
        _logger.LogInformation("[RetroArchLogMonitor] Monitoring {Path}", logPath);

        // Find the start of the CURRENT session in the log file so we don't miss leaderboard
        // messages that were written before the monitor started (e.g., immediately after game load).
        // We scan backwards from the end to find the last "Starting session for game" marker.
        long offset = FindCurrentSessionOffset(logPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, stoppingToken).ConfigureAwait(false);

                var info = new FileInfo(logPath);
                if (!info.Exists) { offset = 0; continue; }

                // Detect log rotation or truncation (new session rewrites the file)
                if (info.Length < offset) offset = 0;
                if (info.Length == offset) continue;

                await using var stream = new FileStream(
                    logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, leaveOpen: true);

                string? line;
                while ((line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false)) != null)
                    ParseLine(line);

                offset = stream.Position;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "[RetroArchLogMonitor] Read error on {Path}; will retry", logPath);
                await Task.Delay(1_000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// Scans the log file backwards to find the byte offset of the last session start.
    /// This allows replaying leaderboard messages written before the monitor started.
    private static long FindCurrentSessionOffset(string logPath)
    {
        try
        {
            const string marker = "Starting session for game";
            var bytes = File.ReadAllBytes(logPath);
            // Scan backwards for the marker (ASCII/UTF-8)
            var markerBytes = System.Text.Encoding.UTF8.GetBytes(marker);
            for (var i = bytes.Length - markerBytes.Length; i >= 0; i--)
            {
                var match = true;
                for (var j = 0; j < markerBytes.Length; j++)
                    if (bytes[i + j] != markerBytes[j]) { match = false; break; }
                if (!match) continue;
                // Find start of the line containing the marker
                var lineStart = i;
                while (lineStart > 0 && bytes[lineStart - 1] != '\n') lineStart--;
                return lineStart;
            }
            // Marker not found — start from the current end to avoid replaying old sessions
            return bytes.Length;
        }
        catch { return 0; }
    }

    private void ParseLine(string line)
    {
        var pos = line.IndexOf(RcheevosTag, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return;

        var msg = line[(pos + RcheevosTag.Length)..].TrimStart();

        // ── Leaderboard events (require log_to_file=true AND log_verbosity=true) ──────
        // rcheevos (rc_client.c) RC_CLIENT_LOG_VERBOSE_FORMATTED messages:
        //   "Leaderboard 4 started: Green Hill Zone - Act 3"
        //   "Leaderboard 4 canceled: Green Hill Zone - Act 3"
        //   "Submitting 0:50.18 (3011) for leaderboard 2: Green Hill Zone - Act 1"

        if (msg.StartsWith("Leaderboard ", StringComparison.OrdinalIgnoreCase))
        {
            var spaceAfterLbId = msg.IndexOf(' ', 12);
            if (spaceAfterLbId < 0) return;
            if (!int.TryParse(msg[12..spaceAfterLbId], out var lbId)) return;

            var rest = msg[(spaceAfterLbId + 1)..];

            if (rest.StartsWith("started: ", StringComparison.OrdinalIgnoreCase))
            {
                var title = rest[9..].TrimEnd('\r', '\n');
                _logger.LogInformation("[RetroArchLogMonitor] LB {Id} started: {Title}", lbId, title);
                Publish("retroachievements.runtime.leaderboard.started", new
                {
                    Source        = "retroarch.log",
                    Confidence    = "high",
                    LeaderboardId = lbId,
                    Title         = title,
                    State         = "started"
                });
            }
            else if (rest.StartsWith("canceled: ", StringComparison.OrdinalIgnoreCase))
            {
                var title = rest[10..].TrimEnd('\r', '\n');
                _logger.LogInformation("[RetroArchLogMonitor] LB {Id} canceled: {Title}", lbId, title);
                Publish("retroachievements.runtime.leaderboard.failed", new
                {
                    Source        = "retroarch.log",
                    Confidence    = "high",
                    LeaderboardId = lbId,
                    Title         = title,
                    State         = "failed"
                });
            }
            return;
        }

        // "Submitting 0:50.18 (3011) for leaderboard 2: Green Hill Zone - Act 1"
        // Gives the exact submitted time — used as final FormattedValue in the result overlay.
        if (msg.StartsWith("Submitting ", StringComparison.OrdinalIgnoreCase))
        {
            // Extract time: first token after "Submitting "
            var spaceAfterTime = msg.IndexOf(' ', 11);
            if (spaceAfterTime < 0) return;
            var timeStr = msg[11..spaceAfterTime].Trim();

            // Find "for leaderboard N"
            var forLb = msg.IndexOf("for leaderboard ", StringComparison.OrdinalIgnoreCase);
            if (forLb < 0) return;
            var afterId = msg[(forLb + 16)..];
            var colon = afterId.IndexOf(':');
            var idStr  = colon >= 0 ? afterId[..colon].Trim() : afterId.Split(' ')[0].Trim();
            if (!int.TryParse(idStr, out var lbId)) return;

            _logger.LogInformation("[RetroArchLogMonitor] LB {Id} submitting: {Time}", lbId, timeStr);
            Publish("retroachievements.runtime.leaderboard.submitting", new
            {
                Source         = "retroarch.log",
                Confidence     = "high",
                LeaderboardId  = lbId,
                FormattedValue = timeStr,
                State          = "submitting"
            });
            return;
        }

        // ── Challenge indicator events ────────────────────────────────────────────
        // rcheevos currently does NOT log these; the framework is ready for future
        // RetroArch/rcheevos versions that add verbose challenge logging.
        //
        // Expected future format (speculative):
        //   "Challenge indicator shown for achievement 123 (BadgeName): Title"
        //   "Challenge indicator hidden for achievement 123"

        if (msg.StartsWith("Challenge indicator shown", StringComparison.OrdinalIgnoreCase))
        {
            ParseChallengeShow(msg);
        }
        else if (msg.StartsWith("Challenge indicator hidden", StringComparison.OrdinalIgnoreCase))
        {
            ParseChallengeHide(msg);
        }
    }

    private void ParseChallengeShow(string msg)
    {
        // Try: "Challenge indicator shown for achievement 123 (BadgeName): Title"
        var forIdx = msg.IndexOf("for achievement ", StringComparison.OrdinalIgnoreCase);
        if (forIdx < 0) return;

        var afterFor = msg[(forIdx + 16)..].TrimStart();
        var spaceIdx = afterFor.IndexOf(' ');
        var idStr    = spaceIdx >= 0 ? afterFor[..spaceIdx] : afterFor;
        if (!int.TryParse(idStr, out var achievementId)) return;

        var rest  = spaceIdx >= 0 ? afterFor[(spaceIdx + 1)..].TrimStart() : string.Empty;
        var badge = string.Empty;
        var title = rest;

        // Extract badge from "(BadgeName)"
        if (rest.StartsWith('('))
        {
            var end = rest.IndexOf(')');
            if (end > 0)
            {
                badge = rest[1..end];
                title = rest[(end + 1)..].TrimStart(':', ' ');
            }
        }

        _logger.LogDebug("[RetroArchLogMonitor] Challenge SHOW ach={Id} badge={Badge}", achievementId, badge);
        Publish("retroachievements.runtime.challenge.active", new
        {
            Source        = "retroarch.log",
            Confidence    = "high",
            AchievementId = achievementId,
            BadgePath     = badge,
            Title         = title.TrimEnd('\r', '\n'),
            State         = "active"
        });
    }

    private void ParseChallengeHide(string msg)
    {
        // Try: "Challenge indicator hidden for achievement 123"
        var forIdx = msg.IndexOf("for achievement ", StringComparison.OrdinalIgnoreCase);
        if (forIdx < 0) return;

        var afterFor = msg[(forIdx + 16)..].Trim();
        if (!int.TryParse(afterFor, out var achievementId)) return;

        _logger.LogDebug("[RetroArchLogMonitor] Challenge HIDE ach={Id}", achievementId);
        Publish("retroachievements.runtime.challenge.inactive", new
        {
            Source        = "retroarch.log",
            Confidence    = "high",
            AchievementId = achievementId,
            State         = "inactive"
        });
    }

    private void Publish(string type, object payload)
    {
        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type    = type,
            Payload = payload
        });
    }
}
