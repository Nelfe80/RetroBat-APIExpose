using System.Text.Json;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

internal static class MediaUpdateAuditLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task AppendAsync(
        MediaProjectionPlan plan,
        string action,
        string media,
        string status,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RetroBatPaths.PluginRoot, ".log", "media-update-audit.jsonl");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            action,
            media,
            status,
            systemId = plan.SystemId,
            frontendSystemId = plan.FrontendSystemId,
            gameSlug = plan.GameSlug,
            gameName = string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.ProjectionBaseName : plan.DisplayName,
            gamePath = plan.GamePath,
            esGameId = plan.EsGameId,
            sourceGameId = plan.ScreenScraperGameId,
            details
        };
        var line = JsonSerializer.Serialize(entry, JsonOptions);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}

internal static class RefreshTrackingLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task AppendAsync(
        string refreshType,
        string status,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        return AppendCoreAsync(refreshType, status, null, details, cancellationToken);
    }

    public static Task AppendAsync(
        MediaProjectionPlan plan,
        string refreshType,
        string status,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        return AppendCoreAsync(refreshType, status, plan, details, cancellationToken);
    }

    private static async Task AppendCoreAsync(
        string refreshType,
        string status,
        MediaProjectionPlan? plan,
        object? details,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(RetroBatPaths.PluginRoot, ".log", "refresh-tracking.jsonl");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            refreshType,
            status,
            systemId = plan?.SystemId ?? string.Empty,
            frontendSystemId = plan?.FrontendSystemId ?? string.Empty,
            gameSlug = plan?.GameSlug ?? string.Empty,
            gameName = plan == null
                ? string.Empty
                : string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.ProjectionBaseName : plan.DisplayName,
            gamePath = plan?.GamePath ?? string.Empty,
            esGameId = plan?.EsGameId ?? string.Empty,
            details
        };
        var line = JsonSerializer.Serialize(entry, JsonOptions);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}
