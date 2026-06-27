using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

public sealed class RetroAchievementsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "u", "p", "t", "password", "token", "api_token", "y"
    };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly IEventBus _eventBus;
    private readonly IEsSettingsStore _esSettingsStore;
    private readonly HttpClient _proxyClient = new();
    private readonly HttpClient _apiClient = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private RetroAchievementsSessionSnapshot _session = new();
    private DateTime? _lastProxyAt;
    private DateTime? _lastCacheAt;
    private string _lastAction = string.Empty;
    private string _lastSource = string.Empty;
    private string _runtimeToken = string.Empty;

    public RetroAchievementsService(
        IOptionsMonitor<ApiExposeOptions> options,
        IEventBus eventBus,
        IEsSettingsStore esSettingsStore)
    {
        _options = options;
        _eventBus = eventBus;
        _esSettingsStore = esSettingsStore;
    }

    public RetroAchievementsStatusSnapshot GetStatus()
    {
        var options = _options.CurrentValue.RetroAchievements;
        return new RetroAchievementsStatusSnapshot
        {
            Enabled = options.Enabled,
            ProxyEnabled = options.Enabled && options.Proxy.Enabled,
            ApiEnabled = options.Enabled && options.Api.Enabled,
            CacheEnabled = options.Enabled && options.Cache.Enabled,
            TargetHost = NormalizeBaseUri(options.Proxy.TargetHost).ToString().TrimEnd('/'),
            WebSocket = "ws://127.0.0.1:12345/ws/retroachievements",
            LastSource = _lastSource,
            LastAction = _lastAction,
            LastProxyAt = _lastProxyAt,
            LastCacheAt = _lastCacheAt,
            Session = _session
        };
    }

    public RetroAchievementsSessionSnapshot GetSession() => _session;

    public bool TryResolveCachedMedia(string category, string fileName, out string path)
    {
        path = string.Empty;
        if (!IsAllowedCachedMediaCategory(category) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName) ||
            !string.Equals(safeFileName, fileName, StringComparison.Ordinal))
        {
            return false;
        }

        var root = ResolveCacheRoot();
        var candidate = Path.GetFullPath(Path.Combine(root, category, safeFileName));
        var categoryRoot = Path.GetFullPath(Path.Combine(root, category));
        if (!candidate.StartsWith(categoryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        var root = ResolveCacheRoot();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        EnsureCacheDirectories();
        await PublishAsync("retroachievements.cache.cleared", new
        {
            Source = "retroachievements.cache",
            RootPath = ToPluginRelativePath(root)
        }, cancellationToken);
    }

    public async Task<RetroAchievementsCacheGameSnapshot> GetCurrentGameCacheAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session.GameId == null)
        {
            return new RetroAchievementsCacheGameSnapshot
            {
                Ok = false,
                Message = "No RetroAchievements game id known for the current session."
            };
        }

        var gameInfo = await GetGameInfoAsync(session.GameId.Value, cancellationToken);
        JsonDocument? progress = null;
        if (!string.IsNullOrWhiteSpace(session.Username))
        {
            progress = await GetUserProgressAsync(session.GameId.Value, session.Username, cancellationToken);
        }

        return new RetroAchievementsCacheGameSnapshot
        {
            Ok = gameInfo != null,
            GameId = session.GameId,
            GameInfo = gameInfo?.RootElement.Clone(),
            UserProgress = progress?.RootElement.Clone(),
            CacheRoot = ToPluginRelativePath(ResolveCacheRoot())
        };
    }

    public async Task ProxyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.RetroAchievements;
        if (!options.Enabled || !options.Proxy.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { message = "RetroAchievements proxy disabled" }, cancellationToken);
            return;
        }

        var parameters = await ReadRequestParametersAsync(context.Request, cancellationToken);
        var action = GetParameter(parameters, "r");
        parameters = await EnsureProxyCredentialsAsync(action, parameters, cancellationToken);
        var correlationId = CreateCorrelationId(parameters);
        if (options.Proxy.EmitRequestEvents)
        {
            await PublishAsync("retroachievements.proxy.requested", new
            {
                Source = "proxy",
                Confidence = "high",
                Action = action,
                Parameters = Sanitize(parameters),
                Route = options.Proxy.Route
            }, cancellationToken, correlationId);
        }

        await ObserveProxyRequestAsync(action, parameters, cancellationToken);

        using var outbound = await CreateProxyRequestAsync(context.Request, parameters, options, cancellationToken);
        using var proxyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        proxyTimeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, options.Proxy.TimeoutMs)));
        using var response = await _proxyClient.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, proxyTimeout.Token);
        var bytes = await response.Content.ReadAsByteArrayAsync(proxyTimeout.Token);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

        await ObserveProxyResponseAsync(action, parameters, bytes, contentType, response.IsSuccessStatusCode, correlationId, cancellationToken);

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = contentType;
        foreach (var header in response.Headers)
        {
            if (!string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    public async Task<JsonDocument?> GetUserProfileAsync(string username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var document = await GetApiJsonAsync(
            "API_GetUserProfile.php",
            new Dictionary<string, string> { ["u"] = username },
            $"API_GetUserProfile_user_{SanitizeFilePart(username)}.json",
            preferFresh: false,
            cancellationToken);

        if (document != null)
        {
            await DownloadUserPicAsync(document.RootElement, cancellationToken);
        }

        return document;
    }

    public async Task<JsonDocument?> GetGameInfoAsync(int gameId, CancellationToken cancellationToken)
    {
        var document = await GetApiJsonAsync(
            "API_GetGame.php",
            new Dictionary<string, string> { ["i"] = gameId.ToString() },
            $"API_GetGame_game_{gameId}.json",
            preferFresh: false,
            cancellationToken);

        if (document != null)
        {
            await DownloadGameImagesAsync(document.RootElement, cancellationToken);
        }

        return document;
    }

    public async Task<JsonDocument?> GetUserProgressAsync(int gameId, string username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var document = await GetApiJsonAsync(
            "API_GetGameInfoAndUserProgress.php",
            new Dictionary<string, string> { ["g"] = gameId.ToString(), ["u"] = username },
            $"API_GetGameInfoAndUserProgress_game_{gameId}_user_{SanitizeFilePart(username)}.json",
            preferFresh: true,
            cancellationToken);

        if (document != null)
        {
            await DownloadGameImagesAsync(document.RootElement, cancellationToken);
            await DownloadBadgesAsync(document.RootElement, cancellationToken);
        }

        return document;
    }

    private async Task<JsonDocument?> GetApiJsonAsync(
        string endpoint,
        Dictionary<string, string> parameters,
        string cacheFileName,
        bool preferFresh,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.RetroAchievements;
        var cachePath = Path.Combine(ResolveCacheRoot(), "api", cacheFileName);
        EnsureCacheDirectories();

        if (!preferFresh && options.Cache.Enabled && File.Exists(cachePath))
        {
            return await LoadJsonFileAsync(cachePath, cancellationToken);
        }

        if (options.Enabled && options.Api.Enabled)
        {
            var credentials = ResolveRuntimeCredentials();
            var username = FirstNonEmpty(
                options.Api.Username,
                credentials.Username,
                _session.Username);
            var apiKey = FirstNonEmpty(
                options.Api.WebApiKey,
                Environment.GetEnvironmentVariable("APIEXPOSE_RETROACHIEVEMENTS_WEB_API_KEY"),
                EmbeddedSecretDefaults.RetroAchievementsWebApiKey);
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(apiKey))
            {
                var uri = BuildApiUri(endpoint, parameters, username, apiKey);
                try
                {
                    using var apiTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    apiTimeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, options.Api.TimeoutMs)));
                    using var response = await _apiClient.GetAsync(uri, apiTimeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(apiTimeout.Token);
                        var document = JsonDocument.Parse(bytes);
                        if (options.Cache.Enabled)
                        {
                            await WriteFileExclusiveAsync(cachePath, bytes, cancellationToken);
                        }

                        _lastCacheAt = DateTime.UtcNow;
                        _lastSource = "api";
                        _lastAction = endpoint;
                        return document;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
                {
                    await PublishAsync("retroachievements.cache.api.error", new
                    {
                        Source = "api",
                        Endpoint = endpoint,
                        Message = ex.Message
                    }, cancellationToken);
                }
            }
        }

        if (options.Cache.Enabled && File.Exists(cachePath))
        {
            return await LoadJsonFileAsync(cachePath, cancellationToken);
        }

        return null;
    }

    private async Task<Dictionary<string, string>> EnsureProxyCredentialsAsync(
        string action,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveRuntimeCredentials();
        var enriched = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        if (!enriched.ContainsKey("u") && !string.IsNullOrWhiteSpace(credentials.Username))
        {
            enriched["u"] = credentials.Username;
        }

        if (action.Equals("login", StringComparison.OrdinalIgnoreCase) &&
            !enriched.ContainsKey("p") &&
            !string.IsNullOrWhiteSpace(credentials.Password))
        {
            enriched["p"] = credentials.Password;
        }

        if (!enriched.ContainsKey("t"))
        {
            var token = FirstNonEmpty(credentials.Token, _runtimeToken);
            if (string.IsNullOrWhiteSpace(token) &&
                !string.IsNullOrWhiteSpace(credentials.Username) &&
                !string.IsNullOrWhiteSpace(credentials.Password))
            {
                token = await RequestRuntimeTokenAsync(credentials.Username, credentials.Password, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                _runtimeToken = token;
                enriched["t"] = token;
            }
        }

        return enriched;
    }

    private RetroAchievementsRuntimeCredentials ResolveRuntimeCredentials()
    {
        var settings = _esSettingsStore.ReadAllSettings();
        return new RetroAchievementsRuntimeCredentials
        {
            Username = FirstNonEmpty(
                TryGetSetting(settings, "global.retroachievements.username"),
                _session.Username),
            Password = FirstNonEmpty(
                TryGetSetting(settings, "global.retroachievements.password")),
            Token = FirstNonEmpty(
                TryGetSetting(settings, "global.retroachievements.token"),
                _runtimeToken)
        };
    }

    private async Task<string> RequestRuntimeTokenAsync(string username, string password, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.RetroAchievements;
        if (!options.Enabled || !options.Proxy.Enabled)
        {
            return string.Empty;
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["r"] = "login",
            ["u"] = username,
            ["p"] = password
        };
        var target = NormalizeBaseUri(options.Proxy.TargetHost);
        var route = string.IsNullOrWhiteSpace(options.Proxy.Route) ? "/dorequest.php" : options.Proxy.Route;
        var builder = new UriBuilder(new Uri(target, route.TrimStart('/')))
        {
            Query = BuildQuery(parameters)
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, options.Proxy.TimeoutMs)));
            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            request.Headers.UserAgent.ParseAdd("APIExpose-RetroAchievements-Token/1.0");
            using var response = await _proxyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (!TryParseJson(bytes, out var document) || document == null)
            {
                return string.Empty;
            }

            using (document)
            {
                return FirstNonEmpty(
                    ReadString(document.RootElement, "Token"),
                    ReadString(document.RootElement, "token"));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            await PublishAsync("retroachievements.token.error", new
            {
                Source = "proxy",
                Message = ex.Message
            }, cancellationToken);
            return string.Empty;
        }
    }

    private async Task ObserveProxyRequestAsync(
        string action,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var username = GetParameter(parameters, "u");
        var gameId = TryReadInt(parameters, "g");
        var gameHash = ResolveRequestGameHash(action, parameters);
        var hardcore = TryReadHardcore(parameters);
        var richPresence = action.Equals("ping", StringComparison.OrdinalIgnoreCase)
            ? GetParameter(parameters, "m")
            : string.Empty;

        if (string.IsNullOrWhiteSpace(username) &&
            gameId == null &&
            string.IsNullOrWhiteSpace(gameHash) &&
            hardcore == null &&
            string.IsNullOrWhiteSpace(richPresence))
        {
            return;
        }

        await UpdateSessionAsync(snapshot =>
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                snapshot.Username = username;
            }

            if (gameId != null)
            {
                snapshot.GameId = gameId;
            }

            if (!string.IsNullOrWhiteSpace(gameHash))
            {
                snapshot.GameHash = gameHash;
            }

            if (hardcore != null)
            {
                snapshot.Hardcore = hardcore.Value;
            }

            if (!string.IsNullOrWhiteSpace(richPresence))
            {
                snapshot.RichPresence = richPresence;
            }

            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);
    }

    private async Task ObserveProxyResponseAsync(
        string action,
        IReadOnlyDictionary<string, string> parameters,
        byte[] bytes,
        string contentType,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        _lastProxyAt = DateTime.UtcNow;
        _lastSource = "proxy";
        _lastAction = action;

        var parsed = TryParseJson(bytes, out var document) ? document : null;
        try
        {
            await CacheProxyResponseAsync(action, parameters, parsed, bytes, cancellationToken);

            switch (action.ToLowerInvariant())
            {
                case "login":
                    await PublishAsync("retroachievements.session.login.detected", new
                    {
                        Source = "proxy",
                        Confidence = "high",
                        Ok = ok,
                        Username = GetParameter(parameters, "u")
                    }, cancellationToken, correlationId);
                    break;
                case "achievementsets":
                    await HandleAchievementSetsResponseAsync(parameters, parsed, ok, correlationId, cancellationToken);
                    break;
                case "startsession":
                    await HandleStartSessionResponseAsync(parameters, parsed, ok, correlationId, cancellationToken);
                    break;
                case "gameid":
                    await HandleGameIdResponseAsync(parameters, parsed, ok, correlationId, cancellationToken);
                    break;
                case "patch":
                    await HandlePatchResponseAsync(parameters, parsed, ok, correlationId, cancellationToken);
                    break;
                case "awardachievement":
                    await HandleAchievementAwardResponseAsync(parameters, parsed, ok, correlationId, cancellationToken);
                    break;
                case "submitlbentry":
                    await PublishAsync("retroachievements.leaderboard.submit.confirmed", new
                    {
                        Source = "proxy",
                        Confidence = "high",
                        Ok = ok,
                        LeaderboardId = TryReadInt(parameters, "l"),
                        Response = SummarizeJson(parsed)
                    }, cancellationToken, correlationId);
                    break;
                case "ping":
                    await HandlePingResponseAsync(parameters, parsed, ok, correlationId, cancellationToken);
                    break;
            }

            if (_options.CurrentValue.RetroAchievements.Proxy.EmitResponseEvents)
            {
                await PublishAsync("retroachievements.proxy.response", new
                {
                    Source = "proxy",
                    Confidence = "high",
                    Action = action,
                    Ok = ok,
                    ContentType = contentType,
                    Response = SummarizeJson(parsed)
                }, cancellationToken, correlationId);
            }
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    private async Task HandleAchievementSetsResponseAsync(
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var gameId = ReadInt(response?.RootElement, "GameID") ??
                     ReadInt(response?.RootElement, "GameId") ??
                     ReadInt(response?.RootElement, "ID") ??
                     TryReadInt(parameters, "g");
        var title = ReadString(response?.RootElement, "Title") ??
                    ReadString(response?.RootElement, "GameTitle");
        var consoleId = ReadInt(response?.RootElement, "ConsoleID") ??
                        ReadInt(response?.RootElement, "ConsoleId");
        var imageIconUrl = ReadString(response?.RootElement, "ImageIconUrl") ??
                           ReadString(response?.RootElement, "ImageIcon");
        var gameHash = ResolveRequestGameHash("achievementsets", parameters);
        var achievementInfos = ExtractAchievementInfos(response?.RootElement);
        var warnings = ExtractAchievementWarnings(response?.RootElement);
        HydrateAchievementMediaLinks(achievementInfos.Values);
        HydrateAchievementMediaLinks(warnings);

        await UpdateSessionAsync(snapshot =>
        {
            snapshot.GameId = gameId ?? snapshot.GameId;
            if (!string.IsNullOrWhiteSpace(title))
            {
                snapshot.GameTitle = title;
            }

            if (!string.IsNullOrWhiteSpace(gameHash))
            {
                snapshot.GameHash = gameHash;
            }

            if (achievementInfos.Count > 0)
            {
                snapshot.AchievementsById = achievementInfos;
                snapshot.AchievementCount = achievementInfos.Count;
            }

            snapshot.GameImageIconRemoteUrl = imageIconUrl ?? snapshot.GameImageIconRemoteUrl;
            snapshot.GameImageIconUrl = ToCachedMediaPath(imageIconUrl, "game_images") ?? snapshot.GameImageIconUrl;

            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);

        QueueGameCacheWarmup(gameId, _session.Username);
        QueueGameIconDownload(imageIconUrl);
        QueueBadgeDownloads(achievementInfos.Values);
        var imageIconCachePath = ToCachedMediaPath(imageIconUrl, "game_images");

        await PublishAsync("retroachievements.achievementsets.loaded", new
        {
            Source = "proxy",
            Confidence = "high",
            Ok = ok,
            GameId = gameId,
            GameTitle = title,
            GameHash = gameHash,
            ConsoleId = consoleId,
            AchievementCount = achievementInfos.Count,
            ImageIconUrl = imageIconCachePath ?? imageIconUrl,
            ImageIconRemoteUrl = imageIconUrl,
            Response = SummarizeJson(response)
        }, cancellationToken, correlationId);

        if (warnings.Count > 0)
        {
            await PublishAsync("retroachievements.warning.detected", new
            {
                Source = "proxy",
                Confidence = "high",
                GameId = gameId,
                GameTitle = title,
                GameHash = gameHash,
                Warnings = warnings.Select(warning => new
                {
                    warning.Id,
                    warning.Title,
                    warning.Description,
                    warning.BadgeName
                }).ToArray()
            }, cancellationToken, correlationId);
        }
    }

    private async Task HandleStartSessionResponseAsync(
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var gameId = TryReadInt(parameters, "g") ??
                     ReadInt(response?.RootElement, "GameID") ??
                     ReadInt(response?.RootElement, "GameId");
        var gameHash = ResolveRequestGameHash("startsession", parameters);
        var hardcore = TryReadHardcore(parameters);
        var username = GetParameter(parameters, "u");
        var unlocks = ExtractUnlockIds(response?.RootElement, "Unlocks");
        var hardcoreUnlocks = ExtractUnlockIds(response?.RootElement, "HardcoreUnlocks");

        await UpdateSessionAsync(snapshot =>
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                snapshot.Username = username;
            }

            snapshot.GameId = gameId ?? snapshot.GameId;
            if (!string.IsNullOrWhiteSpace(gameHash))
            {
                snapshot.GameHash = gameHash;
            }

            if (hardcore != null)
            {
                snapshot.Hardcore = hardcore.Value;
            }

            foreach (var unlock in unlocks.Concat(hardcoreUnlocks))
            {
                snapshot.SessionUnlockedAchievements.Add(unlock);
            }

            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);

        QueueGameCacheWarmup(gameId, _session.Username);

        await PublishAsync("retroachievements.session.started", new
        {
            Source = "proxy",
            Confidence = "high",
            Ok = ok,
            Username = string.IsNullOrWhiteSpace(username) ? _session.Username : username,
            GameId = gameId,
            GameTitle = _session.GameTitle,
            GameHash = gameHash,
            Hardcore = hardcore ?? _session.Hardcore,
            UnlockCount = unlocks.Count,
            HardcoreUnlockCount = hardcoreUnlocks.Count,
            Response = SummarizeJson(response)
        }, cancellationToken, correlationId);
    }

    private async Task HandlePingResponseAsync(
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var richPresence = GetParameter(parameters, "m");
        var gameHash = ResolveRequestGameHash("ping", parameters);
        await UpdateSessionAsync(snapshot =>
        {
            if (!string.IsNullOrWhiteSpace(richPresence))
            {
                snapshot.RichPresence = richPresence;
            }

            if (!string.IsNullOrWhiteSpace(gameHash))
            {
                snapshot.GameHash = gameHash;
            }

            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);

        await PublishAsync("retroachievements.richpresence.ping", new
        {
            Source = "proxy",
            Confidence = "high",
            Ok = ok,
            GameId = _session.GameId,
            GameTitle = _session.GameTitle,
            GameHash = gameHash,
            RichPresence = richPresence,
            Response = SummarizeJson(response)
        }, cancellationToken, correlationId);
    }

    private async Task CacheProxyResponseAsync(
        string action,
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.RetroAchievements;
        if (!options.Cache.Enabled || response == null || bytes.Length == 0)
        {
            return;
        }

        var gameId = ReadInt(response.RootElement, "GameID") ??
                     ReadInt(response.RootElement, "GameId") ??
                     TryReadInt(parameters, "g") ??
                     _session.GameId;
        var hash = ResolveRequestGameHash(action, parameters);
        var key = gameId != null
            ? "game_" + gameId.Value
            : !string.IsNullOrWhiteSpace(hash)
                ? "hash_" + SanitizeFilePart(hash)
                : CreateCorrelationId(parameters);
        var cachePath = Path.Combine(ResolveCacheRoot(), "api", $"Proxy_{SanitizeFilePart(action)}_{key}.json");
        await WriteFileExclusiveAsync(cachePath, bytes, cancellationToken);
        _lastCacheAt = DateTime.UtcNow;
    }

    private async Task HandleGameIdResponseAsync(
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var gameId = ReadInt(response?.RootElement, "GameID") ??
                     ReadInt(response?.RootElement, "GameId") ??
                     ReadInt(response?.RootElement, "ID") ??
                     TryReadInt(parameters, "g");
        var gameHash = ResolveRequestGameHash("gameid", parameters);
        await UpdateSessionAsync(snapshot =>
        {
            snapshot.GameId = gameId;
            snapshot.GameHash = gameHash;
            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);

        QueueGameCacheWarmup(gameId, _session.Username);

        await PublishAsync("retroachievements.game.identified", new
        {
            Source = "proxy",
            Confidence = "high",
            Ok = ok,
            GameId = gameId,
            GameHash = gameHash,
            Response = SummarizeJson(response)
        }, cancellationToken, correlationId);
    }

    private async Task HandlePatchResponseAsync(
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var gameId = ReadInt(response?.RootElement, "ID") ??
                     ReadInt(response?.RootElement, "GameID") ??
                     TryReadInt(parameters, "g");
        var title = ReadString(response?.RootElement, "Title") ??
                    ReadString(response?.RootElement, "GameTitle");
        var achievementInfos = ExtractAchievementInfos(response?.RootElement);
        HydrateAchievementMediaLinks(achievementInfos.Values);
        var achievementCount = achievementInfos.Count > 0
            ? achievementInfos.Count
            : ReadAchievementCount(response?.RootElement);
        await UpdateSessionAsync(snapshot =>
        {
            snapshot.GameId = gameId ?? snapshot.GameId;
            snapshot.GameTitle = string.IsNullOrWhiteSpace(title) ? snapshot.GameTitle : title;
            snapshot.AchievementCount = achievementCount ?? snapshot.AchievementCount;
            if (achievementInfos.Count > 0)
            {
                snapshot.AchievementsById = achievementInfos;
            }

            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);

        QueueGameCacheWarmup(gameId, _session.Username);
        QueueBadgeDownloads(achievementInfos.Values);

        await PublishAsync("retroachievements.patch.loaded", new
        {
            Source = "proxy",
            Confidence = "high",
            Ok = ok,
            GameId = gameId,
            GameTitle = title,
            AchievementCount = achievementCount,
            Response = SummarizeJson(response)
        }, cancellationToken, correlationId);
    }

    private async Task HandleAchievementAwardResponseAsync(
        IReadOnlyDictionary<string, string> parameters,
        JsonDocument? response,
        bool ok,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var achievementId = TryReadInt(parameters, "a") ?? ReadInt(response?.RootElement, "AchievementID");
        var score = ReadInt(response?.RootElement, "Score");
        var softcoreScore = ReadInt(response?.RootElement, "SoftcoreScore");
        var gameId = TryReadInt(parameters, "g") ?? _session.GameId;
        var isWarningAward = IsRetroAchievementsWarningId(achievementId);
        RetroAchievementsAchievementInfo? achievement = null;
        if (achievementId != null && !isWarningAward)
        {
            _session.AchievementsById.TryGetValue(achievementId.Value, out achievement);
        }

        if (isWarningAward)
        {
            await PublishAsync("retroachievements.warning.detected", new
            {
                Source = "proxy",
                Confidence = "high",
                Ok = ok,
                WarningId = achievementId,
                GameId = _session.GameId,
                GameTitle = _session.GameTitle,
                GameHash = _session.GameHash,
                Username = _session.Username,
                Hardcore = _session.Hardcore,
                Response = SummarizeJson(response)
            }, cancellationToken, correlationId);
            return;
        }

        await UpdateSessionAsync(snapshot =>
        {
            snapshot.GameId = gameId ?? snapshot.GameId;
            snapshot.Score = score ?? snapshot.Score;
            snapshot.SoftcoreScore = softcoreScore ?? snapshot.SoftcoreScore;
            if (achievementId != null)
            {
                snapshot.SessionUnlockedAchievements.Add(achievementId.Value);
            }

            snapshot.LastEventAt = DateTime.UtcNow;
        }, cancellationToken);

        QueueGameCacheWarmup(gameId, _session.Username);

        await PublishAsync("retroachievements.achievement.unlock.confirmed", new
        {
            Source = "proxy",
            Confidence = achievement == null ? "medium" : "high",
            Ok = ok,
            AchievementId = achievementId,
            AchievementTitle = achievement?.Title ?? string.Empty,
            AchievementDescription = achievement?.Description ?? string.Empty,
            AchievementPoints = achievement?.Points,
            AchievementBadgeName = achievement?.BadgeName ?? string.Empty,
            AchievementBadgeUrl = achievement?.BadgeUrl ?? string.Empty,
            AchievementBadgeRemoteUrl = achievement?.BadgeRemoteUrl ?? string.Empty,
            GameId = _session.GameId,
            GameTitle = _session.GameTitle,
            GameHash = _session.GameHash,
            Username = _session.Username,
            Hardcore = _session.Hardcore,
            Score = score,
            SoftcoreScore = softcoreScore,
            Response = SummarizeJson(response)
        }, cancellationToken, correlationId);

        if (score != null)
        {
            await PublishAsync("score.live.changed", new
            {
                Source = "retroachievements.proxy",
                SourceKey = "RA_SCORE",
                SystemId = string.Empty,
                Rom = string.Empty,
                Player = 1,
                Score = score.Value,
                RawValue = score.Value,
                Composed = false,
                Confidence = "high",
                ScoreKind = "retroachievements",
                DefinitionFile = string.Empty,
                Parts = Array.Empty<object>()
            }, cancellationToken, correlationId);
        }
    }

    private void QueueGameCacheWarmup(int? gameId, string username)
    {
        if (gameId == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GetGameInfoAsync(gameId.Value, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(username))
                {
                    await GetUserProgressAsync(gameId.Value, username, CancellationToken.None);
                }
            }
            catch
            {
                // Background cache warmup must never slow or break the RetroAchievements proxy path.
            }
        });
    }

    private void QueueGameIconDownload(string? imageIconUrl)
    {
        if (string.IsNullOrWhiteSpace(imageIconUrl) ||
            !_options.CurrentValue.RetroAchievements.Api.DownloadGameImages)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var uri = ToMediaUri(imageIconUrl);
                await DownloadMediaAsync(uri, Path.Combine(ResolveCacheRoot(), "game_images", Path.GetFileName(uri.LocalPath)), CancellationToken.None);
            }
            catch
            {
                // Best effort cache enrichment only.
            }
        });
    }

    private void QueueBadgeDownloads(IEnumerable<RetroAchievementsAchievementInfo> achievements)
    {
        if (!_options.CurrentValue.RetroAchievements.Api.DownloadBadges)
        {
            return;
        }

        var badgeNames = achievements
            .Select(achievement => NormalizeBadgeFileName(achievement.BadgeName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (badgeNames.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var fileName in badgeNames)
            {
                try
                {
                    await DownloadMediaAsync(
                        ToMediaUri("/Badge/" + fileName),
                        Path.Combine(ResolveCacheRoot(), "badges", fileName),
                        CancellationToken.None);
                }
                catch
                {
                    // Best effort cache enrichment only.
                }
            }
        });
    }

    private void HydrateAchievementMediaLinks(IEnumerable<RetroAchievementsAchievementInfo> achievements)
    {
        foreach (var achievement in achievements)
        {
            var fileName = NormalizeBadgeFileName(achievement.BadgeName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                achievement.BadgeUrl = string.Empty;
                achievement.BadgeRemoteUrl = string.Empty;
                continue;
            }

            achievement.BadgeUrl = Path.Combine(ResolveCacheRoot(), "badges", fileName);
            achievement.BadgeRemoteUrl = "https://media.retroachievements.org/Badge/" + Uri.EscapeDataString(fileName);
        }
    }

    private static string ResolveRequestGameHash(string action, IReadOnlyDictionary<string, string> parameters)
    {
        var hash = action.Equals("ping", StringComparison.OrdinalIgnoreCase)
            ? GetParameter(parameters, "x")
            : GetParameter(parameters, "m");
        return LooksLikeHash(hash) ? hash : string.Empty;
    }

    private static bool? TryReadHardcore(IReadOnlyDictionary<string, string> parameters)
    {
        var value = GetParameter(parameters, "h");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var number))
        {
            return number != 0;
        }

        return bool.TryParse(value, out var flag) ? flag : null;
    }

    private static bool LooksLikeHash(string value)
    {
        if (value.Length is < 16 or > 64)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<int, RetroAchievementsAchievementInfo> ExtractAchievementInfos(JsonElement? root)
    {
        var achievements = new Dictionary<int, RetroAchievementsAchievementInfo>();
        if (root != null)
        {
            CollectAchievementInfos(root.Value, achievements);
        }

        return achievements;
    }

    private static List<RetroAchievementsAchievementInfo> ExtractAchievementWarnings(JsonElement? root)
    {
        var warnings = new List<RetroAchievementsAchievementInfo>();
        if (root != null)
        {
            CollectAchievementWarnings(root.Value, warnings);
        }

        return warnings;
    }

    private static void CollectAchievementInfos(JsonElement element, Dictionary<int, RetroAchievementsAchievementInfo> achievements)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadAchievementInfo(element, out var info))
                {
                    achievements[info.Id] = info;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectAchievementInfos(property.Value, achievements);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAchievementInfos(item, achievements);
                }

                break;
        }
    }

    private static bool TryReadAchievementInfo(JsonElement element, out RetroAchievementsAchievementInfo info)
    {
        var id = ReadInt(element, "AchievementID") ??
                 ReadInt(element, "AchievementId") ??
                 ReadInt(element, "ID") ??
                 ReadInt(element, "Id");
        var title = ReadString(element, "Title") ?? ReadString(element, "Name") ?? string.Empty;
        var description = ReadString(element, "Description") ?? ReadString(element, "Desc") ?? string.Empty;
        var points = ReadInt(element, "Points");
        var badgeName = ReadString(element, "BadgeName") ?? ReadString(element, "Badge") ?? string.Empty;
        var displayOrder = ReadInt(element, "DisplayOrder");
        var isAchievementShape = ReadInt(element, "AchievementID") != null ||
                                 ReadInt(element, "AchievementId") != null ||
                                 points != null ||
                                 !string.IsNullOrWhiteSpace(badgeName) ||
                                 !string.IsNullOrWhiteSpace(description);

        info = new RetroAchievementsAchievementInfo
        {
            Id = id ?? 0,
            Title = title,
            Description = description,
            Points = points,
            BadgeName = badgeName,
            DisplayOrder = displayOrder
        };
        return id != null && isAchievementShape && !IsRetroAchievementsWarningInfo(info);
    }

    private static void CollectAchievementWarnings(JsonElement element, List<RetroAchievementsAchievementInfo> warnings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadRawAchievementInfo(element, out var info) && IsRetroAchievementsWarningInfo(info))
                {
                    warnings.Add(info);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectAchievementWarnings(property.Value, warnings);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAchievementWarnings(item, warnings);
                }

                break;
        }
    }

    private static bool TryReadRawAchievementInfo(JsonElement element, out RetroAchievementsAchievementInfo info)
    {
        var id = ReadInt(element, "AchievementID") ??
                 ReadInt(element, "AchievementId") ??
                 ReadInt(element, "ID") ??
                 ReadInt(element, "Id");
        info = new RetroAchievementsAchievementInfo
        {
            Id = id ?? 0,
            Title = ReadString(element, "Title") ?? ReadString(element, "Name") ?? string.Empty,
            Description = ReadString(element, "Description") ?? ReadString(element, "Desc") ?? string.Empty,
            Points = ReadInt(element, "Points"),
            BadgeName = ReadString(element, "BadgeName") ?? ReadString(element, "Badge") ?? string.Empty,
            DisplayOrder = ReadInt(element, "DisplayOrder")
        };
        return id != null;
    }

    private static bool IsRetroAchievementsWarningInfo(RetroAchievementsAchievementInfo info)
    {
        return IsRetroAchievementsWarningId(info.Id) ||
               info.Title.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ||
               (info.Points == 0 && info.BadgeName.Equals("00000", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetroAchievementsWarningId(int? achievementId)
    {
        return achievementId is >= 101000000 and < 102000000;
    }

    private static HashSet<int> ExtractUnlockIds(JsonElement? root, string key)
    {
        var ids = new HashSet<int>();
        if (root == null || !TryGetProperty(root.Value, key, out var value))
        {
            return ids;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                    {
                        ids.Add(number);
                    }
                    else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out number))
                    {
                        ids.Add(number);
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (int.TryParse(property.Name, out var number))
                    {
                        ids.Add(number);
                    }
                    else if (ReadInt(property.Value, "ID") is { } id)
                    {
                        ids.Add(id);
                    }
                }

                break;
        }

        return ids;
    }

    private async Task UpdateSessionAsync(Action<RetroAchievementsSessionSnapshot> update, CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var copy = _session.Clone();
            update(copy);
            _session = copy;
        }
        finally
        {
            _sessionLock.Release();
        }

        await PublishAsync("retroachievements.session.updated", new
        {
            Source = "retroachievements.session",
            Confidence = "high",
            Session = _session
        }, cancellationToken);
    }

    private async Task PublishAsync(string type, object payload, CancellationToken cancellationToken, string? correlationId = null)
    {
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = type,
            Ts = DateTime.UtcNow,
            NodeId = "api-expose",
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString() : correlationId,
            Payload = payload
        });
    }

    private static async Task<Dictionary<string, string>> ReadRequestParametersAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Query)
        {
            values[pair.Key] = pair.Value.ToString();
        }

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            foreach (var pair in form)
            {
                values[pair.Key] = pair.Value.ToString();
            }
        }

        return values;
    }

    private static async Task<HttpRequestMessage> CreateProxyRequestAsync(
        HttpRequest request,
        IReadOnlyDictionary<string, string> parameters,
        ApiExposeOptions.RetroAchievementsOptions options,
        CancellationToken cancellationToken)
    {
        var target = NormalizeBaseUri(options.Proxy.TargetHost);
        var route = string.IsNullOrWhiteSpace(options.Proxy.Route) ? "/dorequest.php" : options.Proxy.Route;
        var builder = new UriBuilder(new Uri(target, route.TrimStart('/')));
        var message = new HttpRequestMessage();
        if (HttpMethods.IsGet(request.Method))
        {
            builder.Query = BuildQuery(parameters);
            message.Method = HttpMethod.Get;
        }
        else
        {
            message.Method = HttpMethod.Post;
            message.Content = new FormUrlEncodedContent(parameters);
        }

        message.RequestUri = builder.Uri;
        var incomingUserAgent = request.Headers.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(incomingUserAgent))
        {
            message.Headers.TryAddWithoutValidation("User-Agent", incomingUserAgent);
        }
        else
        {
            message.Headers.UserAgent.ParseAdd("APIExpose-RetroAchievements-Proxy/1.0");
        }

        if (!string.IsNullOrWhiteSpace(request.Headers.Accept))
        {
            message.Headers.TryAddWithoutValidation("Accept", request.Headers.Accept.ToString());
        }

        return await Task.FromResult(message);
    }

    private Uri BuildApiUri(string endpoint, Dictionary<string, string> parameters, string username, string apiKey)
    {
        var options = _options.CurrentValue.RetroAchievements;
        var baseUri = NormalizeBaseUri(options.Api.BaseUrl);
        var all = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["z"] = username,
            ["y"] = apiKey
        };
        var builder = new UriBuilder(new Uri(baseUri, endpoint.TrimStart('/')))
        {
            Query = BuildQuery(all)
        };
        return builder.Uri;
    }

    private async Task DownloadUserPicAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.RetroAchievements.Api.DownloadUserImages)
        {
            return;
        }

        var value = ReadString(root, "UserPic");
        if (!string.IsNullOrWhiteSpace(value))
        {
            await DownloadMediaAsync(ToMediaUri(value), Path.Combine(ResolveCacheRoot(), "user_images", Path.GetFileName(value)), cancellationToken);
        }
    }

    private async Task DownloadGameImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.RetroAchievements.Api.DownloadGameImages)
        {
            return;
        }

        foreach (var key in new[] { "GameIcon", "ImageIcon", "ImageTitle", "ImageIngame", "ImageBoxArt" })
        {
            var value = ReadString(root, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                await DownloadMediaAsync(ToMediaUri(value), Path.Combine(ResolveCacheRoot(), "game_images", Path.GetFileName(value)), cancellationToken);
            }
        }
    }

    private async Task DownloadBadgesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.RetroAchievements.Api.DownloadBadges)
        {
            return;
        }

        if (!TryGetProperty(root, "Achievements", out var achievements) || achievements.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var achievement in achievements.EnumerateObject())
        {
            var badgeName = ReadString(achievement.Value, "BadgeName");
            if (!string.IsNullOrWhiteSpace(badgeName))
            {
                var fileName = badgeName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? badgeName : badgeName + ".png";
                await DownloadMediaAsync(ToMediaUri("/Badge/" + fileName), Path.Combine(ResolveCacheRoot(), "badges", fileName), cancellationToken);
            }
        }
    }

    private async Task DownloadMediaAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ResolveCacheRoot());
        var fileLock = _fileLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination))
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("APIExpose-RetroAchievements-Cache/1.0");
            using var response = await _apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(destination);
            await stream.CopyToAsync(output, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            await PublishAsync("retroachievements.cache.media.error", new
            {
                Source = "api",
                Uri = uri.ToString(),
                Destination = ToPluginRelativePath(destination),
                Message = ex.Message
            }, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<JsonDocument?> LoadJsonFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            await PublishAsync("retroachievements.cache.read.error", new
            {
                Source = "cache",
                Path = ToPluginRelativePath(path),
                Message = ex.Message
            }, cancellationToken);
            return null;
        }
    }

    private async Task WriteFileExclusiveAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ResolveCacheRoot());
        var fileLock = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private string ResolveCacheRoot()
    {
        var configured = _options.CurrentValue.RetroAchievements.Cache.RootPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "media/retroachievements";
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configured));
    }

    private void EnsureCacheDirectories()
    {
        var root = ResolveCacheRoot();
        Directory.CreateDirectory(Path.Combine(root, "api"));
        Directory.CreateDirectory(Path.Combine(root, "badges"));
        Directory.CreateDirectory(Path.Combine(root, "game_images"));
        Directory.CreateDirectory(Path.Combine(root, "user_images"));
    }

    private static bool IsAllowedCachedMediaCategory(string category)
    {
        return category.Equals("badges", StringComparison.OrdinalIgnoreCase) ||
               category.Equals("game_images", StringComparison.OrdinalIgnoreCase) ||
               category.Equals("user_images", StringComparison.OrdinalIgnoreCase);
    }

    private string? ToCachedMediaPath(string? value, string? preferredCategory = null)
    {
        if (!TryMapCachedMedia(value, preferredCategory, out var category, out var fileName))
        {
            return null;
        }

        return Path.Combine(ResolveCacheRoot(), category, fileName);
    }

    private static bool TryMapCachedMedia(
        string? value,
        string? preferredCategory,
        out string category,
        out string fileName)
    {
        category = string.Empty;
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var uri = ToMediaUri(value);
        var path = uri.LocalPath.Replace('\\', '/');
        var rawFileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(preferredCategory) && IsAllowedCachedMediaCategory(preferredCategory))
        {
            category = preferredCategory;
        }
        else if (path.StartsWith("/Badge/", StringComparison.OrdinalIgnoreCase))
        {
            category = "badges";
        }
        else if (path.StartsWith("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            category = "game_images";
        }
        else if (path.StartsWith("/UserPic/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/UserPics/", StringComparison.OrdinalIgnoreCase))
        {
            category = "user_images";
        }
        else
        {
            return false;
        }

        fileName = rawFileName;
        return true;
    }

    private static string NormalizeBadgeFileName(string badgeName)
    {
        if (string.IsNullOrWhiteSpace(badgeName))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(badgeName.Trim());
        return fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".png";
    }

    private string ToPluginRelativePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(RetroBatPaths.PluginRoot);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : full;
    }

    private static Uri ToMediaUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        var normalized = value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
        return new Uri("https://media.retroachievements.org" + normalized);
    }

    private static Uri NormalizeBaseUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "https://retroachievements.org";
        }

        return new Uri(value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/");
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> Sanitize(IReadOnlyDictionary<string, string> parameters)
    {
        return parameters.ToDictionary(
            pair => pair.Key,
            pair => SensitiveKeys.Contains(pair.Key) ? "***" : pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string TryGetSetting(IReadOnlyDictionary<string, string> settings, string key)
    {
        return settings.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    private static int? TryReadInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return int.TryParse(GetParameter(parameters, key), out var value) ? value : null;
    }

    private static int? ReadInt(JsonElement? root, string key)
    {
        if (root == null || !TryGetProperty(root.Value, key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static string? ReadString(JsonElement? root, string key)
    {
        if (root == null || !TryGetProperty(root.Value, key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement root, string key, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(key) || property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static int? ReadAchievementCount(JsonElement? root)
    {
        if (root == null || !TryGetProperty(root.Value, "Achievements", out var achievements))
        {
            return null;
        }

        return achievements.ValueKind == JsonValueKind.Object ? achievements.EnumerateObject().Count() : null;
    }

    private object? SummarizeJson(JsonDocument? document)
    {
        if (document == null)
        {
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return document.RootElement.Clone();
        }

        var summary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (SensitiveKeys.Contains(property.Name))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                var cachedPath = ToCachedMediaPath(value);
                if (!string.IsNullOrWhiteSpace(cachedPath))
                {
                    summary[property.Name] = cachedPath;
                    if (!property.Name.EndsWith("RemoteUrl", StringComparison.OrdinalIgnoreCase))
                    {
                        summary[property.Name + "RemoteUrl"] = value;
                    }

                    continue;
                }
            }

            summary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => $"array[{property.Value.GetArrayLength()}]",
                JsonValueKind.Object => $"object[{property.Value.EnumerateObject().Count()}]",
                _ => property.Value.GetRawText()
            };
        }

        return summary;
    }

    private static bool TryParseJson(byte[] bytes, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static string CreateCorrelationId(IReadOnlyDictionary<string, string> parameters)
    {
        var raw = string.Join("|", parameters.OrderBy(pair => pair.Key).Select(pair => SensitiveKeys.Contains(pair.Key) ? pair.Key + "=***" : pair.Key + "=" + pair.Value));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return "ra-" + hash[..16];
    }

    private static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _proxyClient.Dispose();
        _apiClient.Dispose();
        _sessionLock.Dispose();
        foreach (var fileLock in _fileLocks.Values)
        {
            fileLock.Dispose();
        }
    }
}

public sealed class RetroAchievementsStatusSnapshot
{
    public bool Enabled { get; set; }
    public bool ProxyEnabled { get; set; }
    public bool ApiEnabled { get; set; }
    public bool CacheEnabled { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public string WebSocket { get; set; } = string.Empty;
    public string LastSource { get; set; } = string.Empty;
    public string LastAction { get; set; } = string.Empty;
    public DateTime? LastProxyAt { get; set; }
    public DateTime? LastCacheAt { get; set; }
    public RetroAchievementsSessionSnapshot Session { get; set; } = new();
}

internal sealed class RetroAchievementsRuntimeCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public sealed class RetroAchievementsSessionSnapshot
{
    public string Username { get; set; } = string.Empty;
    public int? GameId { get; set; }
    public string GameTitle { get; set; } = string.Empty;
    public string GameHash { get; set; } = string.Empty;
    public string GameImageIconUrl { get; set; } = string.Empty;
    public string GameImageIconRemoteUrl { get; set; } = string.Empty;
    public bool Hardcore { get; set; }
    public int? Score { get; set; }
    public int? SoftcoreScore { get; set; }
    public string RichPresence { get; set; } = string.Empty;
    public int? AchievementCount { get; set; }
    public Dictionary<int, RetroAchievementsAchievementInfo> AchievementsById { get; set; } = new();
    public HashSet<int> SessionUnlockedAchievements { get; set; } = new();
    public DateTime? LastEventAt { get; set; }

    public RetroAchievementsSessionSnapshot Clone()
    {
        return new RetroAchievementsSessionSnapshot
        {
            Username = Username,
            GameId = GameId,
            GameTitle = GameTitle,
            GameHash = GameHash,
            GameImageIconUrl = GameImageIconUrl,
            GameImageIconRemoteUrl = GameImageIconRemoteUrl,
            Hardcore = Hardcore,
            Score = Score,
            SoftcoreScore = SoftcoreScore,
            RichPresence = RichPresence,
            AchievementCount = AchievementCount,
            AchievementsById = AchievementsById.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone()),
            SessionUnlockedAchievements = new HashSet<int>(SessionUnlockedAchievements),
            LastEventAt = LastEventAt
        };
    }
}

public sealed class RetroAchievementsAchievementInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? Points { get; set; }
    public string BadgeName { get; set; } = string.Empty;
    public int? DisplayOrder { get; set; }
    public string BadgeUrl { get; set; } = string.Empty;
    public string BadgeRemoteUrl { get; set; } = string.Empty;

    public RetroAchievementsAchievementInfo Clone()
    {
        return new RetroAchievementsAchievementInfo
        {
            Id = Id,
            Title = Title,
            Description = Description,
            Points = Points,
            BadgeName = BadgeName,
            DisplayOrder = DisplayOrder,
            BadgeUrl = BadgeUrl,
            BadgeRemoteUrl = BadgeRemoteUrl
        };
    }
}

public sealed class RetroAchievementsCacheGameSnapshot
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? GameId { get; set; }
    public JsonElement? GameInfo { get; set; }
    public JsonElement? UserProgress { get; set; }
    public string CacheRoot { get; set; } = string.Empty;
}
