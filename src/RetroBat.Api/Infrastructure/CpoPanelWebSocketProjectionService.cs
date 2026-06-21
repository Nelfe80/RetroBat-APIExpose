using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

public sealed class CpoPanelWebSocketProjectionService : IHostedService, IDisposable
{
    private const int PanelSelectionDebounceMs = 15;
    private readonly IEventBus _eventBus;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly PanelsCatalogService _panels;
    private readonly ControlFilesCatalogService _controlFiles;
    private readonly PanelDefinitionProjectionService _panelProjection;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly EmulationStationSystemConfigService _systemConfig;
    private readonly ApiContext _context;
    private readonly ILogger<CpoPanelWebSocketProjectionService>? _logger;
    private readonly object _publishLock = new();
    private readonly object _pendingPanelLock = new();
    private string _lastPublishedPanelKey = string.Empty;
    private PendingPanelProjection? _pendingPanelProjection;
    private CancellationTokenSource? _pendingPanelCts;
    private long _lastQueuedPanelSequence;
    private long _lastPublishedPanelSequence;
    private IDisposable? _subscription;

    public CpoPanelWebSocketProjectionService(
        IEventBus eventBus,
        ApiExposeRuntimeOptionsService runtimeOptions,
        PanelsCatalogService panels,
        ControlFilesCatalogService controlFiles,
        PanelDefinitionProjectionService panelProjection,
        EmulationStationSettingsService settingsService,
        EmulationStationSystemConfigService systemConfig,
        ApiContext context,
        ILogger<CpoPanelWebSocketProjectionService>? logger = null)
    {
        _eventBus = eventBus;
        _runtimeOptions = runtimeOptions;
        _panels = panels;
        _controlFiles = controlFiles;
        _panelProjection = panelProjection;
        _settingsService = settingsService;
        _systemConfig = systemConfig;
        _context = context;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _pendingPanelCts?.Cancel();
        _pendingPanelCts?.Dispose();
    }

    private void OnEvent(EventEnvelope envelope)
    {
        var isSystemSelection = string.Equals(envelope.Type, "ui.system.selected.raw", StringComparison.OrdinalIgnoreCase);
        var isGameSelection = string.Equals(envelope.Type, "ui.game.selected.raw", StringComparison.OrdinalIgnoreCase);
        var isGameStart = IsGameStartEvent(envelope.Type);
        var isSettingsChange = string.Equals(envelope.Type, "panel.settings.changed", StringComparison.OrdinalIgnoreCase);
        if (!isSystemSelection &&
            !isGameSelection &&
            !isGameStart &&
            !isSettingsChange)
        {
            return;
        }

        if (!_runtimeOptions.IsCpoPanelWebSocketPushEnabled())
        {
            return;
        }

        try
        {
            var changedKeys = ExtractChangedKeys(envelope);
            if (isSettingsChange)
            {
                var changedSystemIds = ExtractPanelSystemIds(envelope).ToList();
                if (changedSystemIds.Count == 0)
                {
                    var currentSystemId = ResolveCurrentSystemId(_context.Ui.Selected);
                    if (!string.IsNullOrWhiteSpace(currentSystemId))
                    {
                        changedSystemIds.Add(currentSystemId);
                    }
                }

                foreach (var changedSystemId in changedSystemIds.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var selectedForSystem = _context.Ui.Selected != null &&
                        string.Equals(_context.Ui.Selected.SystemId, changedSystemId, StringComparison.OrdinalIgnoreCase)
                            ? _context.Ui.Selected
                            : null;
                    PublishProjection(envelope, changedSystemId, selectedForSystem, changedKeys, forcePublish: true);
                }

                return;
            }

            var sequence = ReadLongProperty(envelope.Payload, "Sequence");
            var receivedAtUtc = ReadDateTimeProperty(envelope.Payload, "ReceivedAtUtc") ?? DateTime.UtcNow;
            if (isGameStart)
            {
                var running = _context.Ui.Running ?? _context.Ui.Selected;
                var runningSystemId = ResolveCurrentSystemId(running);
                PublishProjection(
                    envelope,
                    runningSystemId,
                    running,
                    Array.Empty<string>(),
                    forcePublish: true,
                    sequence: sequence,
                    receivedAtUtc: receivedAtUtc);
                return;
            }

            var selection = ReadProperty(envelope.Payload, "Selection");
            var systemId = ReadStringProperty(selection, "SystemId");
            GameReference? selected = null;

            if (isGameSelection)
            {
                selected = new GameReference
                {
                    SystemId = systemId,
                    GamePath = ReadStringProperty(selection, "GamePath"),
                    GameName = ReadStringProperty(selection, "GameName")
                };
            }

            if (string.IsNullOrWhiteSpace(systemId))
            {
                selected = isGameSelection ? _context.Ui.Selected : null;
                systemId = selected?.SystemId ?? _context.Ui.SelectedSystem?.Name ?? string.Empty;
            }

            QueueLatestPanelProjection(envelope, systemId, selected, Array.Empty<string>(), sequence, receivedAtUtc);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to publish CPO panel WebSocket projection.");
        }
    }

    private void QueueLatestPanelProjection(
        EventEnvelope envelope,
        string systemId,
        GameReference? selected,
        IReadOnlyList<string> changedKeys,
        long sequence,
        DateTime receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return;
        }

        lock (_pendingPanelLock)
        {
            if (sequence > 0 && sequence < _lastQueuedPanelSequence)
            {
                _logger?.LogDebug(
                    "Stale panel projection ignored before queue: sequence={Sequence}, lastQueued={LastQueued}",
                    sequence,
                    _lastQueuedPanelSequence);
                return;
            }

            if (sequence > 0)
            {
                _lastQueuedPanelSequence = sequence;
            }

            _pendingPanelProjection = new PendingPanelProjection(
                envelope,
                systemId,
                selected,
                changedKeys,
                sequence,
                receivedAtUtc);

            _pendingPanelCts?.Cancel();
            _pendingPanelCts?.Dispose();
            _pendingPanelCts = new CancellationTokenSource();
            var cts = _pendingPanelCts;
            _ = Task.Run(() => PublishLatestPanelProjectionAsync(cts), cts.Token);
        }
    }

    private async Task PublishLatestPanelProjectionAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(PanelSelectionDebounceMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PendingPanelProjection? pending;
        lock (_pendingPanelLock)
        {
            if (!ReferenceEquals(_pendingPanelCts, cts) || cts.IsCancellationRequested)
            {
                return;
            }

            pending = _pendingPanelProjection;
            _pendingPanelProjection = null;
            _pendingPanelCts = null;
        }

        if (pending == null)
        {
            return;
        }

        PublishProjection(
            pending.Envelope,
            pending.SystemId,
            pending.Selected,
            pending.ChangedKeys,
            sequence: pending.Sequence,
            receivedAtUtc: pending.ReceivedAtUtc);

        cts.Dispose();
    }

    private void PublishProjection(
        EventEnvelope envelope,
        string systemId,
        GameReference? selected,
        IReadOnlyList<string> changedKeys,
        bool forcePublish = false,
        long sequence = 0,
        DateTime? receivedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return;
        }

        var rom = selected == null ? string.Empty : Path.GetFileNameWithoutExtension(selected.GamePath ?? selected.GameName ?? string.Empty);
        var launchConfig = _systemConfig.ResolveLaunchConfig(systemId, _settingsService.GetAllSettings());
        var core = selected?.Launch?.Core ?? launchConfig.Core;
        var requestedLayout = _runtimeOptions.GetDatasThemeExposePanelLayout(systemId);
        var snapshot = _panels.GetSnapshot(systemId, rom, core, requestedLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return;
        }

        var panelKey = BuildPublishedPanelKey(snapshot);
        lock (_publishLock)
        {
            if (!forcePublish && sequence > 0 && sequence <= _lastPublishedPanelSequence)
            {
                _logger?.LogDebug(
                    "Stale panel projection ignored before publish: sequence={Sequence}, lastPublished={LastPublished}",
                    sequence,
                    _lastPublishedPanelSequence);
                return;
            }

            if (!forcePublish && string.Equals(panelKey, _lastPublishedPanelKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastPublishedPanelKey = panelKey;
            if (sequence > 0)
            {
                _lastPublishedPanelSequence = sequence;
            }
        }

        var publishedAtUtc = DateTime.UtcNow;
        var activeLayout = snapshot.Layouts.FirstOrDefault(layout =>
            string.Equals(layout.Id, snapshot.ActiveLayoutId, StringComparison.OrdinalIgnoreCase)) ??
            snapshot.Layouts.FirstOrDefault();
        var activePanel = _panelProjection.Build(snapshot, activeLayout);

        var payload = new
        {
            Source = "resources/dynpanels",
            Trigger = envelope.Type,
            Sequence = sequence,
            ChangedKeys = changedKeys,
            PanelKey = panelKey,
            Emulator = launchConfig.Emulator,
            snapshot.SystemId,
            snapshot.Rom,
            snapshot.Core,
            snapshot.Scope,
            snapshot.SystemPanelFile,
            snapshot.CorePanelFile,
            snapshot.GamePanelFile,
            snapshot.DefaultLayoutId,
            snapshot.ActiveLayoutId,
            snapshot.ActiveLayoutSource,
            RequestedLayout = requestedLayout,
            ActiveLayout = activeLayout,
            ActivePanel = activePanel,
            ControlFiles = string.IsNullOrWhiteSpace(snapshot.Rom)
                ? new PanelControlFilesSnapshot()
                : _controlFiles.GetForRom(snapshot.Rom),
            Latency = new
            {
                Trigger = envelope.Type,
                Sequence = sequence,
                ReceivedAtUtc = receivedAtUtc,
                PublishedAtUtc = publishedAtUtc,
                AgeMs = receivedAtUtc.HasValue ? Math.Max(0, (int)(publishedAtUtc - receivedAtUtc.Value).TotalMilliseconds) : 0
            }
        };

        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "panel.state",
            NodeId = envelope.NodeId,
            CorrelationId = envelope.CorrelationId,
            Payload = payload
        });

        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "cpo.panel.config.selected",
            NodeId = envelope.NodeId,
            CorrelationId = envelope.CorrelationId,
            Payload = payload
        });
    }

    private static string BuildPublishedPanelKey(PanelThemeSnapshot snapshot)
    {
        var scope = (snapshot.Scope ?? string.Empty).Trim();
        var systemId = (snapshot.SystemId ?? string.Empty).Trim();
        var activeLayoutId = (snapshot.ActiveLayoutId ?? string.Empty).Trim();
        var panelFile = ResolveEffectivePanelFile(snapshot);
        var rom = string.Equals(scope, "game", StringComparison.OrdinalIgnoreCase)
            ? (snapshot.Rom ?? string.Empty).Trim()
            : string.Empty;

        var keySource = string.Join(
            "|",
            scope.ToLowerInvariant(),
            systemId.ToLowerInvariant(),
            rom.ToLowerInvariant(),
            panelFile.ToLowerInvariant(),
            activeLayoutId.ToLowerInvariant());
        return ComputeShortHash(keySource);
    }

    private static string ComputeShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string ResolveEffectivePanelFile(PanelThemeSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.GamePanelFile) &&
            string.Equals(snapshot.Scope, "game", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.GamePanelFile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CorePanelFile) &&
            string.Equals(snapshot.Scope, "core", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.CorePanelFile.Trim();
        }

        return snapshot.SystemPanelFile.Trim();
    }

    private string ResolveCurrentSystemId(GameReference? selected)
    {
        return selected?.SystemId ?? _context.Ui.SelectedSystem?.Name ?? string.Empty;
    }

    private static bool IsGameStartEvent(string? type)
    {
        return string.Equals(type, "ui.game.started.raw", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "ui.game.started", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractPanelSystemIds(EventEnvelope envelope)
    {
        foreach (var key in ExtractChangedKeys(envelope))
        {
            var normalized = key.Trim();
            var markerIndex = normalized.IndexOf(".apiexpose_panel", StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0)
            {
                yield return normalized[..markerIndex];
                continue;
            }

            const string prefix = "apiexpose_panel_";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                normalized.Length > prefix.Length)
            {
                yield return normalized[prefix.Length..];
            }
        }
    }

    private static IReadOnlyList<string> ExtractChangedKeys(EventEnvelope envelope)
    {
        if (ReadProperty(envelope.Payload, "ChangedKeys") is not { } changedKeysValue)
        {
            return Array.Empty<string>();
        }

        if (changedKeysValue is IEnumerable<string> changedKeys)
        {
            return changedKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .ToList();
        }

        if (changedKeysValue is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!.Trim())
                .ToList();
        }

        return Array.Empty<string>();
    }

    private static object? ReadProperty(object? source, string propertyName)
    {
        if (source == null)
        {
            return null;
        }

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) ||
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        var propertyInfo = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return propertyInfo?.GetValue(source);
    }

    private static string ReadStringProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText(),
            JsonElement { ValueKind: JsonValueKind.True } => bool.TrueString,
            JsonElement { ValueKind: JsonValueKind.False } => bool.FalseString,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static long ReadLongProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            null => 0,
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), out var number) => number,
            string text when long.TryParse(text, out var number) => number,
            _ => 0
        };
    }

    private static DateTime? ReadDateTimeProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            DateTime date => date,
            DateTimeOffset date => date.UtcDateTime,
            JsonElement { ValueKind: JsonValueKind.String } element when DateTime.TryParse(element.GetString(), out var date) => date.ToUniversalTime(),
            string text when DateTime.TryParse(text, out var date) => date.ToUniversalTime(),
            _ => null
        };
    }

    private sealed record PendingPanelProjection(
        EventEnvelope Envelope,
        string SystemId,
        GameReference? Selected,
        IReadOnlyList<string> ChangedKeys,
        long Sequence,
        DateTime ReceivedAtUtc);
}
