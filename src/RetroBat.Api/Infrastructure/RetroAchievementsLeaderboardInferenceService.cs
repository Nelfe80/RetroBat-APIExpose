using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Providers.RetroArchWrapper;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Passive fallback for runtimes that keep leaderboard start/tracker callbacks
/// inside the emulator process. It correlates a qualified level timer, the RA
/// rich presence and the cached leaderboard catalog. Events are explicitly
/// marked inferred; native runtime events remain authoritative when available.
/// </summary>
public sealed class RetroAchievementsLeaderboardInferenceService : IHostedService, IDisposable
{
    private const int MaxDisplayReferenceEntries = 5000;
    private const int RawReferenceReadBudget = 50000;

    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StartEqualityRegex = new(
        @"(?<![A-Za-z0-9])0x[HhWwXx]?\s*(?<address>[0-9A-Fa-f]+)\s*=\s*(?<value>-?\d+)(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "in", "on", "at", "zone", "stage", "level", "speedrun", "fastest", "time"
    };

    private readonly IEventBus _eventBus;
    private readonly RetroAchievementsService _retroAchievements;
    private readonly RetroAchievementsLeaderboardHistoryStore _history;
    private readonly RetroArchWrapperProvider _wrapper;
    private readonly ILogger<RetroAchievementsLeaderboardInferenceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _contextSync = new();
    private readonly SortedDictionary<string, long> _progression = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, long> _memoryValuesByAddress = new();
    private readonly Dictionary<int, (string Definition, IReadOnlyList<MemoryEquality> Selectors)> _startSelectorCache = new();
    private IDisposable? _subscription;
    private int? _activeLeaderboardId;
    // The RetroArch log is authoritative for WHICH leaderboard is running. Adopting
    // its id keeps inference (scoreboard, live timer, references) on the same
    // leaderboard the game actually started, so consumers never see competing ids.
    // -1 = none; volatile int is atomically read/written across threads.
    private volatile int _logAuthoritativeId = -1;
    private volatile int _terminalLeaderboardId = -1;
    private string _terminalContextSignature = string.Empty;
    private IReadOnlyList<RetroAchievementsLeaderboardEntry> _entries = Array.Empty<RetroAchievementsLeaderboardEntry>();
    private string _lastFormattedValue = string.Empty;
    private string _lastRawValue = string.Empty;
    private int? _lastInferredRank;
    private long? _activePersonalBestScore;
    private string _activePersonalBestFormattedScore = string.Empty;
    private long _progressionRevision;
    private long? _lastLevelTimerValue;
    private ActiveAttemptContext? _activeAttempt;
    private bool _hasResolvedLevelInSession;
    // Gameplay gate for early (pre-timer) activation: true between GAME_PLAYING and
    // DEMO_MODE/session end, so level markers only arm a speedrun during real play.
    private volatile bool _inGameplay;

    public RetroAchievementsLeaderboardInferenceService(
        IEventBus eventBus,
        RetroAchievementsService retroAchievements,
        RetroAchievementsLeaderboardHistoryStore history,
        RetroArchWrapperProvider wrapper,
        ILogger<RetroAchievementsLeaderboardInferenceService> logger)
    {
        _eventBus = eventBus;
        _retroAchievements = retroAchievements;
        _history = history;
        _wrapper = wrapper;
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
        if (envelope.Type.Equals("ui.game.started", StringComparison.OrdinalIgnoreCase) ||
            envelope.Type.Equals("ui.game.started.raw", StringComparison.OrdinalIgnoreCase))
        {
            ResetAttemptContext();
            return;
        }

        if (envelope.Type.Equals("retroachievements.catalog.updated", StringComparison.OrdinalIgnoreCase))
        {
            _ = _history.SynchronizeCatalogAsync(_retroAchievements.GetSession());
            return;
        }

        if (envelope.Type.Equals("retroarch.memory.changed", StringComparison.OrdinalIgnoreCase) ||
            envelope.Type.Equals("ingame.memory.changed", StringComparison.OrdinalIgnoreCase))
        {
            CaptureMemoryValue(envelope.Payload);
            return;
        }

        if (envelope.Type.Equals("retroarch.action", StringComparison.OrdinalIgnoreCase))
        {
            CaptureProgression(envelope.Payload);
            var enteredGameplay = TrackGameplayState(envelope.Payload);
            // The composed level timer only starts after the level's title card, so
            // waiting for it (or for rich presence) delays the speedrun overlay. Arm the
            // leaderboard the instant play begins: the GAME_PLAYING rising edge fires even
            // at the first level where FE10/FE11 never change (initial-candidate resolves
            // it), and level/act markers cover subsequent levels.
            if (enteredGameplay || ShouldEarlyActivate(envelope.Payload))
                _ = HandleTimerAsync(SynthesizeLevelTimerEnvelope(envelope));
            return;
        }

        if (envelope.Type.Equals("timer.live.changed", StringComparison.OrdinalIgnoreCase))
        {
            _ = HandleTimerAsync(envelope);
            return;
        }

        if (envelope.Type.Equals("retroachievements.leaderboard.changed", StringComparison.OrdinalIgnoreCase) &&
            ReadString(envelope.Payload, "Source").Equals("proxy", StringComparison.OrdinalIgnoreCase) &&
            IsResultState(ReadString(envelope.Payload, "State")))
        {
            _ = LearnSubmissionAsync(envelope.Payload);
            return;
        }

        // Native RetroArch-log leaderboard lifecycle: it names the real leaderboard id.
        if (envelope.Type.StartsWith("retroachievements.runtime.leaderboard.", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = envelope.Type[(envelope.Type.LastIndexOf('.') + 1)..];
            var id = (int?)ReadLong(envelope.Payload, "LeaderboardId");
            if (suffix.Equals("started", StringComparison.OrdinalIgnoreCase))
            {
                // The log can start broad "full game" speedruns at game load while the
                // level timer is later matched to a per-stage leaderboard. Only let a
                // log event become authoritative when the leaderboard itself is scoped
                // to a level/stage/act; otherwise rich presence + timer matching wins.
                if (id is > 0 && _logAuthoritativeId < 0)
                {
                    var session = _retroAchievements.GetSession();
                    if (session.LeaderboardsById.TryGetValue(id.Value, out var lb) &&
                        IsTimedSpeedrun(lb) &&
                        IsLevelScopedSpeedrun(lb))
                    {
                        _logAuthoritativeId = id.Value;
                        _terminalLeaderboardId = -1;
                    }
                }
            }
            else // failed / submitting → the authoritative run is over
            {
                if (id is > 0)
                {
                    _terminalLeaderboardId = id.Value;
                    lock (_contextSync)
                        _terminalContextSignature = _activeAttempt?.Context.Signature ?? string.Empty;
                }
                if (id == null || id == _logAuthoritativeId || id == _activeLeaderboardId)
                {
                    var formattedValue = ReadString(envelope.Payload, "FormattedValue");
                    if (!string.IsNullOrWhiteSpace(formattedValue))
                    {
                        _lastFormattedValue = formattedValue;
                    }

                    _logAuthoritativeId = -1;
                    _ = EndActiveAsync(suffix);
                }
            }
            return;
        }

        if (envelope.Type.Equals("ui.game.ended", StringComparison.OrdinalIgnoreCase) ||
            envelope.Type.Equals("ui.game.ended.raw", StringComparison.OrdinalIgnoreCase) ||
            envelope.Type.Equals("retroachievements.session.ended", StringComparison.OrdinalIgnoreCase))
        {
            _logAuthoritativeId = -1;
            _terminalLeaderboardId = -1;
            _terminalContextSignature = string.Empty;
            ResetAttemptContext();
            _ = EndActiveAsync("ended");
        }
    }

    private async Task HandleTimerAsync(EventEnvelope envelope)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (!_retroAchievements.AreLeaderboardsEnabled())
            {
                await EndActiveCoreAsync("disabled");
                return;
            }

            var role = ReadString(envelope.Payload, "TimerRole");
            var sourceKey = ReadString(envelope.Payload, "SourceKey");
            if (!role.Equals("level", StringComparison.OrdinalIgnoreCase) &&
                !sourceKey.Contains("level", StringComparison.OrdinalIgnoreCase)) return;

            var session = _retroAchievements.GetSession();
            if (session.GameId == null || session.LeaderboardsById.Count == 0)
            {
                await EndActiveCoreAsync("context-lost");
                return;
            }

            var attempt = CaptureAttemptContext(envelope.Payload, session.RichPresence);
            RetroAchievementsLeaderboardInfo? selected;
            RetroAchievementsLeaderboardResolution? learnedResolution = null;
            var authoritative = _logAuthoritativeId;
            var timerValue = ReadLong(envelope.Payload, "Remaining") ?? ReadLong(envelope.Payload, "Value");
            var shouldResolveDefinition = _activeLeaderboardId == null || timerValue is >= 0 and <= 2;
            var memory = shouldResolveDefinition ? GetCurrentMemory(envelope.Payload) : null;
            var storedResolution = shouldResolveDefinition && memory != null
                ? await _history.ResolveFromStartSelectorsAsync(
                    session.GameId.Value,
                    memory,
                    allowInitialCandidate: !_hasResolvedLevelInSession,
                    session.Username)
                : null;
            var definitionCandidates = shouldResolveDefinition
                ? SelectLeaderboardsByStartDefinition(session.LeaderboardsById.Values, memory ?? new Dictionary<long, long>())
                : Array.Empty<RetroAchievementsLeaderboardInfo>();
            if (authoritative > 0 && session.LeaderboardsById.TryGetValue(authoritative, out var authLeaderboard))
            {
                // The RetroArch log already told us the exact leaderboard: use it and
                // ignore rich-presence guessing so both sources converge on one id.
                selected = authLeaderboard;
            }
            else if (storedResolution != null &&
                     session.LeaderboardsById.TryGetValue(storedResolution.LeaderboardId, out var storedLeaderboard))
            {
                learnedResolution = storedResolution;
                selected = storedLeaderboard;
            }
            else if (definitionCandidates.Count > 0)
            {
                learnedResolution = definitionCandidates.Count == 1
                    ? await _history.ResolveLeaderboardAsync(session.GameId.Value, definitionCandidates[0].Id, session.Username)
                    : await _history.ResolveLastSubmittedAsync(
                        session.GameId.Value,
                        definitionCandidates.Select(item => item.Id).ToArray(),
                        session.Username);
                selected = learnedResolution != null &&
                           session.LeaderboardsById.TryGetValue(learnedResolution.LeaderboardId, out var submittedLeaderboard)
                    ? submittedLeaderboard
                    : definitionCandidates.Count == 1 ? definitionCandidates[0] : null;
            }
            else if (attempt != null &&
                     (learnedResolution = await _history.ResolveAsync(session.GameId.Value, attempt.Context, session.Username)) != null &&
                     session.LeaderboardsById.TryGetValue(learnedResolution.LeaderboardId, out var learnedLeaderboard) &&
                     IsTimedSpeedrun(learnedLeaderboard) &&
                     IsLevelScopedSpeedrun(learnedLeaderboard))
            {
                selected = learnedLeaderboard;
            }
            else if (!string.IsNullOrWhiteSpace(session.RichPresence))
            {
                // Rich presence available → match leaderboard by context (level name, act, etc.)
                selected = SelectLeaderboard(session.LeaderboardsById.Values, session.RichPresence);
            }
            else
            {
                // Rich presence not yet received (first ping delayed) → fall back to the first
                // time-format leaderboard available. Will be re-matched once rich presence arrives.
                selected = null;
            }

            if (selected == null)
            {
                if (_activeLeaderboardId is { } activeId &&
                    session.LeaderboardsById.TryGetValue(activeId, out var activeLeaderboard))
                {
                    await PublishAsync(activeLeaderboard, "updated", envelope.Payload, envelope.CorrelationId);
                    return;
                }
                await EndActiveCoreAsync("context-lost");
                return;
            }

            if (authoritative <= 0 && selected.Id == _terminalLeaderboardId)
            {
                await EndActiveCoreAsync("terminal-suppressed");
                return;
            }

            if (_activeLeaderboardId != selected.Id)
            {
                await EndActiveCoreAsync("context-changed");
                _activeLeaderboardId = selected.Id;
                _entries = Array.Empty<RetroAchievementsLeaderboardEntry>();
                _lastFormattedValue = string.Empty;
                _lastRawValue = string.Empty;
                _lastInferredRank = null;
                _activePersonalBestScore = learnedResolution?.PersonalBestScore;
                _activePersonalBestFormattedScore = learnedResolution?.PersonalBestFormattedScore ?? string.Empty;
                _hasResolvedLevelInSession = true;
                var entriesLoad = _retroAchievements.GetLeaderboardEntriesAsync(selected.Id, MaxDisplayReferenceEntries);
                if (await Task.WhenAny(entriesLoad, Task.Delay(150)) == entriesLoad)
                {
                    _entries = await entriesLoad;
                }
                await PublishAsync(selected, "started", envelope.Payload, envelope.CorrelationId);
                if (!entriesLoad.IsCompleted)
                {
                    _ = CompleteEntriesLoadAsync(selected.Id, selected, envelope.Payload, envelope.CorrelationId, entriesLoad);
                }
            }
            else
            {
                await PublishAsync(selected, "updated", envelope.Payload, envelope.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Leaderboard inference ignored timer event");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CaptureProgression(object? payload)
    {
        var action = ReadString(payload, "ActionType");
        if (!action.StartsWith("PROGRESSION_", StringComparison.OrdinalIgnoreCase)) return;
        var value = ReadLong(payload, "Value");
        if (value == null) return;

        lock (_contextSync)
        {
            CaptureAddressValueCore(ReadString(payload, "Address"), value.Value);
            if (_progression.TryGetValue(action, out var previous) && previous == value.Value) return;
            _progression[action] = value.Value;
            _progressionRevision++;
        }
    }

    // Returns true only on the rising edge into gameplay (menu/demo -> playing), so a
    // repeated GAME_PLAYING signal cannot re-arm on every frame. DEMO_MODE clears the
    // gate so the next real GAME_PLAYING edge re-arms.
    private bool TrackGameplayState(object? payload)
    {
        var action = ReadString(payload, "ActionType");
        if (action.Equals("GAME_PLAYING", StringComparison.OrdinalIgnoreCase))
        {
            if (_inGameplay) return false;
            _inGameplay = true;
            return true;
        }
        if (action.Equals("DEMO_MODE", StringComparison.OrdinalIgnoreCase))
            _inGameplay = false;
        return false;
    }

    private bool ShouldEarlyActivate(object? payload)
    {
        if (!_inGameplay) return false;
        var action = ReadString(payload, "ActionType");
        if (action.Equals("LEVEL_TIMER", StringComparison.OrdinalIgnoreCase)) return false;
        return action.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("NEW_LEVEL", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("PROGRESSION_STAGE", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("PROGRESSION_ZONE", StringComparison.OrdinalIgnoreCase);
    }

    private static EventEnvelope SynthesizeLevelTimerEnvelope(EventEnvelope source)
        => new()
        {
            Type = "timer.live.changed",
            CorrelationId = source.CorrelationId,
            Payload = new SyntheticLevelTimerPayload
            {
                SystemId = ReadString(source.Payload, "SystemId"),
                Rom = ReadString(source.Payload, "Rom")
            }
        };

    private void CaptureMemoryValue(object? payload)
    {
        var signal = ReadProperty(payload, "signal") ?? ReadProperty(payload, "Signal");
        var address = FirstNonEmpty(ReadString(signal, "Address"), ReadString(payload, "Address"));
        var value = ReadLong(signal, "Value") ?? ReadLong(payload, "Value");
        if (value == null) return;
        lock (_contextSync) CaptureAddressValueCore(address, value.Value);
    }

    private void CaptureAddressValueCore(string address, long value)
    {
        if (TryParseAddress(address, out var parsedAddress))
            _memoryValuesByAddress[parsedAddress] = value;
    }

    private ActiveAttemptContext? CaptureAttemptContext(object? timerPayload, string richPresence)
    {
        var timerValue = ReadLong(timerPayload, "Remaining") ?? ReadLong(timerPayload, "Value");
        if (timerValue == null) return null;

        lock (_contextSync)
        {
            var reset = _lastLevelTimerValue != null && timerValue.Value < _lastLevelTimerValue.Value && timerValue.Value <= 2;
            var progressionChanged = _activeAttempt != null && _activeAttempt.ProgressionRevision != _progressionRevision;
            // Progression memory often changes a few frames before the game resets its
            // level clock. Keep the running attempt until that reset arrives, otherwise
            // the next leaderboard starts with the previous level's final time.
            var changedContext = _activeAttempt == null || (progressionChanged && timerValue.Value <= 2);
            var lateContextLabel = NormalizeContextLabel(richPresence);
            var needsLateEnrichment = _activeAttempt != null &&
                !_activeAttempt.Context.IsDiscriminating &&
                !string.IsNullOrWhiteSpace(lateContextLabel);
            if (reset || changedContext || needsLateEnrichment)
            {
                var progression = new SortedDictionary<string, long>(_progression, StringComparer.OrdinalIgnoreCase);
                var contextLabel = progression.Count == 0 ? lateContextLabel : string.Empty;
                var nextAttempt = new ActiveAttemptContext(
                    new RetroAchievementsLeaderboardContext
                    {
                        SystemId = ReadString(timerPayload, "SystemId"),
                        Rom = ReadString(timerPayload, "Rom"),
                        TimerSourceKey = "LEVEL_TIMER",
                        Progression = progression,
                        ContextLabel = contextLabel
                    },
                    _progressionRevision);
                _activeAttempt = nextAttempt;
                if (_terminalLeaderboardId > 0 &&
                    nextAttempt.Context.IsDiscriminating &&
                    !nextAttempt.Context.Signature.Equals(_terminalContextSignature, StringComparison.Ordinal))
                {
                    _terminalLeaderboardId = -1;
                    _terminalContextSignature = string.Empty;
                }
            }

            _lastLevelTimerValue = timerValue.Value;
            return _activeAttempt;
        }
    }

    private async Task LearnSubmissionAsync(object? payload)
    {
        try
        {
            var leaderboardId = (int?)ReadLong(payload, "LeaderboardId");
            if (leaderboardId is not > 0) return;
            var session = _retroAchievements.GetSession();
            if (!session.LeaderboardsById.TryGetValue(leaderboardId.Value, out var leaderboard) ||
                !IsTimedSpeedrun(leaderboard) ||
                !IsLevelScopedSpeedrun(leaderboard))
            {
                return;
            }

            ActiveAttemptContext? attempt;
            lock (_contextSync) attempt = _activeAttempt;
            if (attempt == null || !attempt.Context.IsDiscriminating) return;

            await _history.RecordSubmissionAsync(
                session,
                leaderboard,
                attempt.Context,
                ReadString(payload, "Value"),
                ReadString(payload, "FormattedValue"),
                (int?)ReadLong(payload, "Rank"),
                ReadLong(payload, "PersonalBestScore") ?? ReadLong(payload, "BestScore"),
                FirstNonEmpty(
                    ReadString(payload, "PersonalBestFormattedScore"),
                    ReadString(payload, "BestFormattedScore")));
            _logger.LogInformation(
                "Learned RetroAchievements leaderboard {LeaderboardId} for context {ContextSignature}",
                leaderboardId,
                attempt.Context.Signature);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to learn RetroAchievements leaderboard submission context");
        }
    }

    private void ResetAttemptContext()
    {
        lock (_contextSync)
        {
            _progression.Clear();
            _memoryValuesByAddress.Clear();
            _startSelectorCache.Clear();
            _progressionRevision = 0;
            _lastLevelTimerValue = null;
            _activeAttempt = null;
            _hasResolvedLevelInSession = false;
            _inGameplay = false;
        }
    }

    private static string NormalizeContextLabel(string richPresence)
    {
        if (string.IsNullOrWhiteSpace(richPresence)) return string.Empty;
        var context = richPresence.Split(new[] { '•', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return Regex.Replace(context.Trim().ToLowerInvariant(), @"\s+", " ");
    }

    private IReadOnlyList<RetroAchievementsLeaderboardInfo> SelectLeaderboardsByStartDefinition(
        IEnumerable<RetroAchievementsLeaderboardInfo> leaderboards,
        IReadOnlyDictionary<long, long> memory)
    {
        if (memory.Count == 0) return Array.Empty<RetroAchievementsLeaderboardInfo>();

        var matches = leaderboards
            .Where(item => !item.Hidden && IsTimedSpeedrun(item) && IsLevelScopedSpeedrun(item))
            .Select(item =>
            {
                var selectors = GetStartEqualities(item);
                var known = selectors.Where(selector => memory.ContainsKey(selector.Address)).ToList();
                var valid = known.Count > 0 && known.All(selector => memory[selector.Address] == selector.Value);
                return new { Leaderboard = item, Score = valid ? known.Count : 0 };
            })
            .Where(item => item.Score > 0)
            .ToList();
        if (matches.Count == 0) return Array.Empty<RetroAchievementsLeaderboardInfo>();
        var bestScore = matches.Max(item => item.Score);
        var tied = matches
            .Where(item => item.Score == bestScore)
            .Select(item => item.Leaderboard)
            .OrderBy(item => item.Id)
            .ToList();
        if (tied.Count <= 1) return tied;

        // Tie because a discriminating selector address was never emitted (a
        // change-triggered progression byte still at its power-on value 0). Assume
        // unknown selectors equal 0 and keep the leaderboard(s) fully satisfied under
        // that assumption; this picks Green Hill Act 1 over Act 2/3 at cold start.
        var exact = tied
            .Where(item => GetStartEqualities(item).All(selector =>
                memory.TryGetValue(selector.Address, out var value) ? value == selector.Value : selector.Value == 0))
            .ToList();
        if (exact.Count == 1) return exact;
        if (exact.Count > 1) tied = exact;
        var initial = tied.Where(IsInitialCandidate).ToList();
        return initial.Count == 1 ? initial : tied;
    }

    // Mirrors RetroAchievementsLeaderboardHistoryStore.InitialCandidate: a level-scoped
    // speedrun whose start selectors are all zero — i.e. the first level of the game.
    private bool IsInitialCandidate(RetroAchievementsLeaderboardInfo info)
    {
        var selectors = GetStartEqualities(info);
        return selectors.Count > 0 &&
               selectors.All(selector => selector.Value == 0) &&
               IsLevelScopedSpeedrun(info);
    }

    private Dictionary<long, long> GetCurrentMemory(object? timerPayload)
    {
        Dictionary<long, long> memory;
        lock (_contextSync) memory = new Dictionary<long, long>(_memoryValuesByAddress);
        var snapshot = _wrapper.GetSnapshot();
        var timerSystem = ReadString(timerPayload, "SystemId");
        var timerRom = ReadString(timerPayload, "Rom");
        if (snapshot.Connected &&
            (string.IsNullOrWhiteSpace(timerSystem) || snapshot.SystemId.Equals(timerSystem, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(timerRom) || snapshot.Rom.Equals(timerRom, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var signal in snapshot.Signals)
                if (signal.Value != null && TryParseAddress(signal.Address, out var address))
                    memory[address] = signal.Value.Value;
        }
        return memory;
    }

    private IReadOnlyList<MemoryEquality> GetStartEqualities(RetroAchievementsLeaderboardInfo leaderboard)
    {
        lock (_contextSync)
        {
            if (_startSelectorCache.TryGetValue(leaderboard.Id, out var cached) &&
                cached.Definition.Equals(leaderboard.Definition, StringComparison.Ordinal))
                return cached.Selectors;
        }
        var selectors = ParseStartEqualities(leaderboard.Definition);
        lock (_contextSync) _startSelectorCache[leaderboard.Id] = (leaderboard.Definition, selectors);
        return selectors;
    }

    private static IReadOnlyList<MemoryEquality> ParseStartEqualities(string definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return Array.Empty<MemoryEquality>();
        var start = definition.Split(new[] { "::" }, StringSplitOptions.None)[0];
        return StartEqualityRegex.Matches(start)
            .Select(match =>
            {
                var address = long.Parse(match.Groups["address"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var value = long.Parse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return new MemoryEquality(address, value);
            })
            .Distinct()
            .ToList();
    }

    private static bool TryParseAddress(string raw, out long address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var normalized = raw.Trim().Replace(" ", string.Empty).TrimStart('$');
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = normalized[2..];
        if (normalized.Length > 0 && "HhWwXx".Contains(normalized[0])) normalized = normalized[1..];
        return long.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private async Task LoadEntriesAsync(int leaderboardId)
    {
        try
        {
            var entries = await _retroAchievements.GetLeaderboardEntriesAsync(leaderboardId, RawReferenceReadBudget);
            await _gate.WaitAsync();
            try
            {
                if (_activeLeaderboardId == leaderboardId)
                {
                    _entries = entries;
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to warm leaderboard {LeaderboardId} entries", leaderboardId);
        }
    }

    private async Task CompleteEntriesLoadAsync(
        int leaderboardId,
        RetroAchievementsLeaderboardInfo leaderboard,
        object? timerPayload,
        string correlationId,
        Task<IReadOnlyList<RetroAchievementsLeaderboardEntry>> entriesLoad)
    {
        try
        {
            var entries = await entriesLoad;
            await _gate.WaitAsync();
            try
            {
                if (_activeLeaderboardId != leaderboardId) return;
                _entries = entries;
                await PublishAsync(leaderboard, "updated", timerPayload, correlationId);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to complete leaderboard {LeaderboardId} entries preload", leaderboardId);
        }
    }

    private async Task PublishAsync(
        RetroAchievementsLeaderboardInfo leaderboard,
        string state,
        object? timerPayload,
        string correlationId)
    {
        var raw = ReadLong(timerPayload, "Remaining") ?? ReadLong(timerPayload, "Value");
        var unit = ReadString(timerPayload, "Unit");
        var formatted = FormatTimer(raw, unit);
        var session = _retroAchievements.GetSession();
        var lowerIsBetter = IsTimedSpeedrun(leaderboard) || leaderboard.LowerIsBetter;
        var referenceEntries = BuildDisplayReferenceEntries(_entries, lowerIsBetter);
        var reference = referenceEntries.FirstOrDefault();
        var inferredRank = InferRank(formatted, _entries, lowerIsBetter);
        _lastRawValue = raw?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _lastFormattedValue = formatted;
        _lastInferredRank = inferredRank;
        await _retroAchievements.PublishLeaderboardAsync(new RetroAchievementsLeaderboardEvent
        {
            Source = "retroachievements.runtime.inferred",
            Confidence = "inferred",
            State = state,
            GameId = session.GameId,
            LeaderboardId = leaderboard.Id,
            Title = leaderboard.Title,
            Value = _lastRawValue,
            FormattedValue = formatted,
            Rank = inferredRank,
            User = session.Username,
            PersonalBestScore = _activePersonalBestScore,
            PersonalBestFormattedScore = _activePersonalBestFormattedScore,
            ReferenceRank = reference?.Rank ?? (!string.IsNullOrWhiteSpace(leaderboard.TopUser) ? 1 : null),
            ReferenceUser = reference?.User ?? leaderboard.TopUser,
            ReferenceFormattedScore = reference?.FormattedScore ?? leaderboard.TopFormattedScore,
            BadgePath = leaderboard.BadgeUrl,
            BadgeRemoteUrl = leaderboard.BadgeRemoteUrl,
            ReferenceEntries = referenceEntries
        }, correlationId);
    }

    private Task EndActiveAsync(string state)
    {
        return EndWithGateAsync(state);
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

    private async Task EndWithGateAsync(string state)
    {
        await _gate.WaitAsync();
        try { await EndActiveCoreAsync(state); }
        finally { _gate.Release(); }
    }

    private async Task EndActiveCoreAsync(string state)
    {
        if (_activeLeaderboardId is not { } id) return;
        var session = _retroAchievements.GetSession();
        session.LeaderboardsById.TryGetValue(id, out var info);
        await _retroAchievements.PublishLeaderboardAsync(new RetroAchievementsLeaderboardEvent
        {
            Source = "retroachievements.runtime.inferred",
            Confidence = "inferred",
            State = IsResultState(state) ? state : "ended",
            GameId = session.GameId,
            LeaderboardId = id,
            Title = info?.Title ?? string.Empty,
            Value = _lastRawValue,
            FormattedValue = _lastFormattedValue,
            Rank = _lastInferredRank,
            User = session.Username,
            PersonalBestScore = _activePersonalBestScore,
            PersonalBestFormattedScore = _activePersonalBestFormattedScore,
            BadgePath = info?.BadgeUrl ?? string.Empty,
            BadgeRemoteUrl = info?.BadgeRemoteUrl ?? string.Empty,
            ReferenceEntries = BuildDisplayReferenceEntries(_entries, info == null || IsTimedSpeedrun(info) || info.LowerIsBetter),
            Response = state
        });
        _activeLeaderboardId = null;
        _entries = Array.Empty<RetroAchievementsLeaderboardEntry>();
        _lastFormattedValue = string.Empty;
        _lastRawValue = string.Empty;
        _lastInferredRank = null;
        _activePersonalBestScore = null;
        _activePersonalBestFormattedScore = string.Empty;
    }

    private static bool IsResultState(string state)
        => state.Equals("submitting", StringComparison.OrdinalIgnoreCase)
            || state.Equals("submitted", StringComparison.OrdinalIgnoreCase)
            || state.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static RetroAchievementsLeaderboardInfo? SelectLeaderboard(
        IEnumerable<RetroAchievementsLeaderboardInfo> leaderboards,
        string richPresence)
    {
        var context = richPresence.Split(new[] { '•', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0];
        var contextTokens = Tokens(context).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (contextTokens.Count == 0) return null;
        var contextNumbers = contextTokens.Where(token => token.All(char.IsDigit)).ToArray();

        return leaderboards
            .Where(item => !item.Hidden && IsTimedSpeedrun(item) && IsLevelScopedSpeedrun(item))
            .Select(item => new
            {
                Item = item,
                Tokens = Tokens(item.Title).ToArray()
            })
            .Where(candidate => contextNumbers.All(number => candidate.Tokens.Contains(number, StringComparer.OrdinalIgnoreCase)))
            .Select(candidate => new
            {
                candidate.Item,
                Score = candidate.Tokens.Count(token => contextTokens.Contains(token))
            })
            .Where(candidate => candidate.Score >= 3)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Item.Id)
            .Select(candidate => candidate.Item)
            .FirstOrDefault();
    }

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
            .ToList();

        if (parsed.Count == 0)
        {
            return entries.Take(MaxDisplayReferenceEntries).ToList();
        }

        var buckets = new HashSet<long>();
        var result = new List<RetroAchievementsLeaderboardEntry>();
        foreach (var item in parsed)
        {
            var bucket = (long)Math.Floor(item.Seconds!.Value * 4d);
            if (!buckets.Add(bucket))
            {
                continue;
            }

            result.Add(item.Entry);
            if (result.Count >= MaxDisplayReferenceEntries)
            {
                break;
            }
        }

        return result;
    }

    private static bool IsTimedSpeedrun(RetroAchievementsLeaderboardInfo info)
    {
        // A leaderboard is time-based if the format says TIME, OR the top score looks like a time.
        var isTimeFormat = info.Format.Contains("TIME", StringComparison.OrdinalIgnoreCase)
            || (info.TopFormattedScore.Length > 0 && info.TopFormattedScore.Contains(':'));

        if (!isTimeFormat) return false;

        // If format explicitly says TIME, accept without further keyword checks.
        if (info.Format.Contains("TIME", StringComparison.OrdinalIgnoreCase)) return true;

        // For leaderboards detected via TopFormattedScore, require at least one speedrun keyword.
        return info.Title.Contains("speedrun", StringComparison.OrdinalIgnoreCase)
            || info.Description.Contains("fast", StringComparison.OrdinalIgnoreCase)
            || info.Description.Contains("least time", StringComparison.OrdinalIgnoreCase)
            || info.Description.Contains("complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLevelScopedSpeedrun(RetroAchievementsLeaderboardInfo info)
    {
        var text = $"{info.Title} {info.Description}";
        return text.Contains("act", StringComparison.OrdinalIgnoreCase)
            || text.Contains("level", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("zone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("course", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mission", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Tokens(string value)
        => WordRegex.Matches(value.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length > 1 || token.All(char.IsDigit))
            .Where(token => !IgnoredWords.Contains(token));

    private static string FormatTimer(long? value, string unit)
    {
        if (value == null) return string.Empty;
        var seconds = unit.Equals("ms", StringComparison.OrdinalIgnoreCase) || unit.Contains("millisecond", StringComparison.OrdinalIgnoreCase)
            ? value.Value / 1000d
            : value.Value;
        if (unit.Equals("s", StringComparison.OrdinalIgnoreCase) || unit.Contains("second", StringComparison.OrdinalIgnoreCase) ||
            unit.Equals("ms", StringComparison.OrdinalIgnoreCase) || unit.Contains("millisecond", StringComparison.OrdinalIgnoreCase))
        {
            var centiseconds = (long)Math.Round(Math.Max(0, seconds) * 100d, MidpointRounding.AwayFromZero);
            var minutes = centiseconds / 6000;
            var displaySeconds = (centiseconds / 100) % 60;
            var displayCentiseconds = centiseconds % 100;
            return $"{minutes:00}:{displaySeconds:00}.{displayCentiseconds:00}";
        }
        return value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string ReadString(object? payload, string name)
    {
        if (payload is JsonElement element && element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
        var propertyInfo = payload?.GetType().GetProperties()
            .FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return propertyInfo?.GetValue(payload)?.ToString() ?? string.Empty;
    }

    private static object? ReadProperty(object? payload, string name)
    {
        if (payload is JsonElement element && element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
        return payload?.GetType().GetProperties()
            .FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.GetValue(payload);
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static long? ReadLong(object? payload, string name)
        => long.TryParse(ReadString(payload, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private sealed record ActiveAttemptContext(
        RetroAchievementsLeaderboardContext Context,
        long ProgressionRevision);

    private sealed record MemoryEquality(long Address, long Value);

    // Zero-valued level timer used to arm the leaderboard from a level marker before the
    // game's own clock (composed minute/second) has produced its first sample.
    private sealed class SyntheticLevelTimerPayload
    {
        public string TimerRole { get; init; } = "level";
        public string SourceKey { get; init; } = "LEVEL_TIMER";
        public long Value { get; init; }
        public string Unit { get; init; } = "seconds";
        public string SystemId { get; init; } = string.Empty;
        public string Rom { get; init; } = string.Empty;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _gate.Dispose();
    }
}
