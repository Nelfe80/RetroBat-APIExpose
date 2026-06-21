using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Media;

public sealed class ScreenScraperCapabilityService
{
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(1);
    private readonly object _lock = new();
    private readonly Queue<DateTime> _recentRequestSlots = new();
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<ScreenScraperCapabilityService>? _logger;
    private ScreenScraperCapabilitySnapshot _snapshot = ScreenScraperCapabilitySnapshot.Unknown;

    public ScreenScraperCapabilityService(
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<ScreenScraperCapabilityService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public ScreenScraperCapabilitySnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return _snapshot with
            {
                EffectiveQueueConcurrency = ResolveQueueConcurrencyCore(_options.CurrentValue.Scraping, _snapshot),
                EffectiveMediaConcurrency = ResolveMediaConcurrencyCore(_options.CurrentValue.Scraping, _snapshot),
                LocalLogicalProcessors = Environment.ProcessorCount
            };
        }
    }

    public int ResolveMaxQueueWorkers()
    {
        var options = _options.CurrentValue.Scraping;
        var cap = options.RemoteScrapeConcurrencyCap > 0 ? options.RemoteScrapeConcurrencyCap : 1;
        var workers = options.RemoteScrapeMaxQueueWorkers > 0 ? options.RemoteScrapeMaxQueueWorkers : cap;
        return Math.Clamp(Math.Max(workers, cap), 1, 32);
    }

    public int ResolveQueueConcurrency()
    {
        lock (_lock)
        {
            return ResolveQueueConcurrencyCore(_options.CurrentValue.Scraping, _snapshot);
        }
    }

    public int ResolveMediaConcurrency()
    {
        lock (_lock)
        {
            return ResolveMediaConcurrencyCore(_options.CurrentValue.Scraping, _snapshot);
        }
    }

    public async Task WaitForRemoteRequestSlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delay = TryReserveRequestSlot();
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    public void UpdateFromResponse(JsonElement root, string source)
    {
        if (!TryReadSsUser(root, out var user))
        {
            return;
        }

        lock (_lock)
        {
            _snapshot = new ScreenScraperCapabilitySnapshot
            {
                UserId = user.UserId,
                Level = user.Level,
                Contribution = user.Contribution,
                MaxThreads = user.MaxThreads,
                MaxDownloadSpeedKbPerSecond = user.MaxDownloadSpeedKbPerSecond,
                RequestsToday = user.RequestsToday,
                FailedRequestsToday = user.FailedRequestsToday,
                MaxRequestsPerMinute = user.MaxRequestsPerMinute,
                MaxRequestsPerDay = user.MaxRequestsPerDay,
                MaxFailedRequestsPerDay = user.MaxFailedRequestsPerDay,
                FavoriteRegion = user.FavoriteRegion,
                LastUpdatedAtUtc = DateTime.UtcNow,
                Source = string.IsNullOrWhiteSpace(source) ? "screenscraper" : source.Trim(),
                EffectiveQueueConcurrency = ResolveQueueConcurrencyCore(_options.CurrentValue.Scraping, user),
                EffectiveMediaConcurrency = ResolveMediaConcurrencyCore(_options.CurrentValue.Scraping, user),
                LocalLogicalProcessors = Environment.ProcessorCount
            };
        }

        _logger?.LogDebug(
            "ScreenScraper capabilities updated from {Source}: user={UserId}, maxThreads={MaxThreads}, maxRequestsPerMinute={MaxRequestsPerMinute}, maxDownloadSpeed={MaxDownloadSpeed}.",
            source,
            user.UserId,
            user.MaxThreads,
            user.MaxRequestsPerMinute,
            user.MaxDownloadSpeedKbPerSecond);
    }

    private TimeSpan TryReserveRequestSlot()
    {
        lock (_lock)
        {
            var limit = ResolveRequestsPerMinuteLimit(_options.CurrentValue.Scraping, _snapshot);
            if (limit <= 0)
            {
                return TimeSpan.Zero;
            }

            var now = DateTime.UtcNow;
            while (_recentRequestSlots.Count > 0 && now - _recentRequestSlots.Peek() >= RequestWindow)
            {
                _recentRequestSlots.Dequeue();
            }

            if (_recentRequestSlots.Count < limit)
            {
                _recentRequestSlots.Enqueue(now);
                return TimeSpan.Zero;
            }

            var oldest = _recentRequestSlots.Peek();
            return oldest.Add(RequestWindow) - now + TimeSpan.FromMilliseconds(25);
        }
    }

    private static int ResolveQueueConcurrencyCore(ApiExposeOptions.ScrapingOptions options, ScreenScraperCapabilitySnapshot snapshot)
    {
        var profile = NormalizeProfile(options.RemoteScrapePerformanceProfile);
        if (profile == "safe")
        {
            return 1;
        }

        var fallback = Math.Clamp(options.RemoteScrapeFallbackConcurrency <= 0 ? 1 : options.RemoteScrapeFallbackConcurrency, 1, 8);
        var cap = Math.Clamp(options.RemoteScrapeConcurrencyCap <= 0 ? fallback : options.RemoteScrapeConcurrencyCap, 1, 32);
        var userLimit = snapshot.MaxThreads > 0 ? snapshot.MaxThreads : fallback;
        var machineLimit = profile == "aggressive"
            ? Math.Max(1, Environment.ProcessorCount * 2)
            : Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(Math.Min(Math.Min(userLimit, cap), machineLimit), 1, cap);
    }

    private static int ResolveMediaConcurrencyCore(ApiExposeOptions.ScrapingOptions options, ScreenScraperCapabilitySnapshot snapshot)
    {
        var queueConcurrency = ResolveQueueConcurrencyCore(options, snapshot);
        var profile = NormalizeProfile(options.RemoteScrapePerformanceProfile);
        if (profile == "safe")
        {
            return 1;
        }

        var localIoLimit = profile == "aggressive" ? 4 : 3;
        return Math.Clamp(Math.Min(queueConcurrency, localIoLimit), 1, 8);
    }

    private static int ResolveRequestsPerMinuteLimit(ApiExposeOptions.ScrapingOptions options, ScreenScraperCapabilitySnapshot snapshot)
    {
        var providerLimit = snapshot.MaxRequestsPerMinute;
        var configuredCap = options.RemoteScrapeRequestsPerMinuteCap;
        var limit = providerLimit > 0 && configuredCap > 0
            ? Math.Min(providerLimit, configuredCap)
            : providerLimit > 0 ? providerLimit : configuredCap;
        if (limit <= 0)
        {
            return 0;
        }

        var margin = Math.Clamp(options.RemoteScrapeRequestMinuteSafetyMargin, 0, Math.Max(0, limit - 1));
        return Math.Max(1, limit - margin);
    }

    private static string NormalizeProfile(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "safe" => "safe",
            "balanced" => "balanced",
            "aggressive" => "aggressive",
            _ => "auto"
        };
    }

    private static bool TryReadSsUser(JsonElement root, out ScreenScraperCapabilitySnapshot snapshot)
    {
        snapshot = ScreenScraperCapabilitySnapshot.Unknown;
        if (!TryGetProperty(root, "response", out var response) ||
            !TryGetProperty(response, "ssuser", out var ssUser) ||
            ssUser.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        snapshot = new ScreenScraperCapabilitySnapshot
        {
            UserId = ReadString(ssUser, "id"),
            Level = ReadInt(ssUser, "niveau", "level"),
            Contribution = ReadInt(ssUser, "contribution"),
            MaxThreads = ReadInt(ssUser, "maxthreads"),
            MaxDownloadSpeedKbPerSecond = ReadInt(ssUser, "maxdownloadspeed"),
            RequestsToday = ReadInt(ssUser, "requeststoday"),
            FailedRequestsToday = ReadInt(ssUser, "requestskotoday"),
            MaxRequestsPerMinute = ReadInt(ssUser, "maxrequestsperdmin", "maxrequestspermin", "maxrequestsperminute"),
            MaxRequestsPerDay = ReadInt(ssUser, "maxrequestsperday"),
            MaxFailedRequestsPerDay = ReadInt(ssUser, "maxrequestskoperday"),
            FavoriteRegion = ReadString(ssUser, "favregion")
        };
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString().Trim();
            }
        }

        return string.Empty;
    }

    private static int ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return Math.Max(0, number);
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Max(0, parsed);
            }
        }

        return 0;
    }
}

public sealed record ScreenScraperCapabilitySnapshot
{
    public static ScreenScraperCapabilitySnapshot Unknown { get; } = new();

    public string UserId { get; init; } = string.Empty;
    public int Level { get; init; }
    public int Contribution { get; init; }
    public int MaxThreads { get; init; }
    public int MaxDownloadSpeedKbPerSecond { get; init; }
    public int RequestsToday { get; init; }
    public int FailedRequestsToday { get; init; }
    public int MaxRequestsPerMinute { get; init; }
    public int MaxRequestsPerDay { get; init; }
    public int MaxFailedRequestsPerDay { get; init; }
    public string FavoriteRegion { get; init; } = string.Empty;
    public DateTime LastUpdatedAtUtc { get; init; } = DateTime.MinValue;
    public string Source { get; init; } = string.Empty;
    public int EffectiveQueueConcurrency { get; init; } = 1;
    public int EffectiveMediaConcurrency { get; init; } = 1;
    public int LocalLogicalProcessors { get; init; } = Environment.ProcessorCount;
}
