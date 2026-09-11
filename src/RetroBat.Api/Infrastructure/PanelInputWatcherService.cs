using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Publishes cabinet button presses on /ws/panel, already resolved to a panel SLOT.
///
/// This is what turns a drawn panel into a WIRING CHECK: press the bottom-left button,
/// see the bottom-left button light up on the marquee. If another one lights up, the
/// wiring is wrong and it shows in a second - no config file to read, no guessing.
///
/// A consumer receives a slot and a function, never a raw button index. The translation
/// chain is the cabinet's own, end to end:
///
///   press → SDL2 (RetroArch's own) → RetroPad identity → CabinetButtons → slot
///
/// and CabinetButtons is exactly what the LedManager wizard measured on this cabinet,
/// so the slot published here is the slot the LEDs light and the panel draws.
/// </summary>
public sealed class PanelInputWatcherService : IHostedService, IDisposable
{
    private const int PollIntervalMs = 25; // ~40 Hz: a press is never missed, the cost is nil

    private readonly IEventBus _eventBus;
    private readonly ILogger<PanelInputWatcherService>? _logger;
    private CabinetInputReader? _reader;
    private long _ticks;
    private bool _pollFailed;
    private bool _firstPressLogged;
    private string _lastDeviceKey = "";
    private const int RescanEveryTicks = 200; // ~5 s at 25 ms
    // SDL est lié au thread : le hotplug (add/remove) n'est vu QUE sur le thread qui a fait
    // SDL_Init, et seulement si on y pompe les événements. On possède donc SDL sur UN thread
    // dédié FIXE (pas un Task.Run + await, dont le thread migre entre les awaits).
    private Thread? _thread;
    private volatile bool _stop;
    // Pendant une LECTURE replay ou une SESSION DE JEU, personne ne (dé)branche un panel : on
    // suspend la ré-énumération SDL (SDL_QuitSubSystem+InitSubSystem = synchro forcée coûteuse)
    // — la LECTURE des appuis, elle, continue. Deux drapeaux distincts : un replay lancé pendant
    // qu'ES croit encore le jeu ouvert ne doit pas ré-armer le hotplug en se terminant.
    private volatile bool _replayActive;
    private volatile bool _gameActive;
    private IDisposable? _busSub;

    public PanelInputWatcherService(IEventBus eventBus, ILogger<PanelInputWatcherService>? logger = null)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Suspendre le hotplug pendant une lecture replay (et le reprendre à la fin).
        _busSub = _eventBus.Subscribe<EventEnvelope>(OnBusEvent);
        // Tout le travail SDL (init, pompage, ouverture, lecture) doit vivre sur CE thread
        // et lui seul : c'est la condition pour que SDL voie un panel (dé)branché.
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "PanelInputWatcher" };
        _thread.Start();
        return Task.CompletedTask;
    }

    private void OnBusEvent(EventEnvelope e)
    {
        // Lecture replay (lancée par nous) ET session de jeu (ES : game-start/game-end → ui.game.*).
        if (string.Equals(e.Type, "replay.started", StringComparison.Ordinal)) _replayActive = true;
        else if (string.Equals(e.Type, "replay.finished", StringComparison.Ordinal)) _replayActive = false;
        else if (string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal)) _gameActive = true;
        else if (string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal)) _gameActive = false;
        // Filet : si un game-end se perd (ES tué/crash), naviguer dans le menu prouve qu'aucun jeu
        // ne tourne → on ré-arme le hotplug (sinon un panel rebranché ne serait plus jamais détecté).
        else if (string.Equals(e.Type, "ui.game.selected", StringComparison.Ordinal)) _gameActive = false;
    }

    // Boucle SYNCHRONE sur le thread dédié : aucun await (qui migrerait le thread et
    // casserait le hotplug SDL). SDL_Init, pompage, ouverture et lecture y vivent ensemble.
    private void RunLoop()
    {
        try
        {
            _reader = new CabinetInputReader();
            var (ok, message) = _reader.Initialize(RetroBatPaths.RetroBatRoot);
            if (!ok)
            {
                // no pad, no SDL, no cabinet: the rest of APIExpose does not care
                _logger?.LogInformation("Panel input watcher not started: {Message}", message);
                _reader.Dispose();
                _reader = null;
                return;
            }

            var mapped = _reader.OpenControllers();
            _logger?.LogInformation("Panel input watcher started: {Message}, {Mapped} mapped device(s) [{Names}]",
                message, mapped, string.Join(", ", _reader.DeviceNames));
            _lastDeviceKey = mapped + "|" + string.Join(",", _reader.DeviceNames);

            // (device, identity) of everything currently down: the diff between two polls is
            // what becomes a press and a release
            var held = new HashSet<(int Device, string Identity)>();

            while (!_stop)
            {
                try
                {
                    // Pomper les événements SDL sur CE thread (celui de SDL_Init) est ce qui
                    // fait voir un branchement/débranchement — impossible depuis un autre thread.
                    _reader!.Pump();

                    if (++_ticks % RescanEveryTicks == 0) RescanIfChanged();

                    // Boutons cabinet (canal historique) + directions (canal additif dpad/stick).
                    var now = _reader!.Snapshot()
                        .Select(p => (Device: p.DeviceIndex, p.Identity))
                        .Concat(_reader!.SnapshotDirections().Select(p => (Device: p.DeviceIndex, p.Identity)))
                        .ToHashSet();

                    foreach (var down in now.Where(x => !held.Contains(x)))
                    {
                        Publish("panel.input.pressed", down.Device, down.Identity);
                    }

                    foreach (var up in held.Where(x => !now.Contains(x)).ToList())
                    {
                        Publish("panel.input.released", up.Device, up.Identity);
                    }

                    held = now;
                }
                catch (Exception ex)
                {
                    // Loud once, then quiet - a repeating failure must not drown the file.
                    if (!_pollFailed)
                    {
                        _pollFailed = true;
                        _logger?.LogWarning(ex, "Panel input poll failed; presses are no longer being read.");
                    }
                    Thread.Sleep(1000);
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Panel input watcher thread crashed.");
        }
    }

    /// <summary>
    /// Reopens the joysticks only when their number changed. Says what it found: a
    /// cabinet whose panel stops answering must be able to show, from the log alone,
    /// whether the device disappeared or the presses did.
    /// </summary>
    private void RescanIfChanged()
    {
        var reader = _reader;
        if (reader is null) return;
        // Pendant une lecture replay OU une session de jeu : pas de ré-énumération SDL (synchro
        // forcée) — le panel reste lu, mais on ne re-détecte pas un (dé)branchement improbable
        // pendant qu'on joue. Repris dès le retour au menu (game-end / fin de lecture).
        //
        // ET quand un emulateur tourne sans qu'ES l'ait dit : une partie lancee par l'API (repli
        // netplay) n'emet aucun game-start, et la re-init toutes les 5 s se voyait alors a
        // l'ecran, un creux de cadence periodique mesure pendant un direct.
        if (_replayActive || _gameActive || EmulatorForeground.EmulateurTourne()) return;

        // SDL_NumJoysticks ne reflète PAS le hotplug (même pompé sur le thread SDL) : seule
        // une ré-init du sous-système joystick (quit+init) ré-énumère vraiment les devices.
        // On le fait donc toutes les ~5 s sur CE thread dédié FIXE (énumération cohérente —
        // contrairement à Task.Run+await qui migrait le thread). On ne loggue que sur changement.
        var mapped = reader.ForceReenumerate();
        var key = mapped + "|" + string.Join(",", reader.DeviceNames);
        if (key == _lastDeviceKey) return;
        _lastDeviceKey = key;
        _logger?.LogInformation("Panel input re-scan: {Mapped} mapped [{Names}]",
            mapped, string.Join(", ", reader.DeviceNames));
    }

    private void Publish(string type, int device, string identity)
    {
        // the device index is the player: pad 0 drives panel 1
        var player = device + 1;
        var slot = ResolveSlot(player, identity);
        var system = SystemInput(identity);

        // One line for the first press of a session: proof the cabinet is being read at
        // all. Without it, "nothing lights up" cannot be told apart from "nothing was
        // pressed" - and the two need opposite fixes.
        if (type.EndsWith(".pressed", StringComparison.Ordinal) && !_firstPressLogged)
        {
            _firstPressLogged = true;
            _logger?.LogInformation("First cabinet press read: player {Player}, identity {Identity}, slot {Slot}, system {System}",
                player, identity, slot?.ToString() ?? "-", system ?? "-");
        }

        _eventBus.PublishAsync(new EventEnvelope
        {
            Type = type,
            Payload = new
            {
                Player = player,
                Slot = slot,
                // START and COIN are wired on their own pins, outside the numbered
                // slots. Reporting them as "no slot" left a consumer unable to tell an
                // unwired button from one that simply is not part of the eight - which
                // is precisely the question a wiring check is asking.
                System = system,
                Identity = identity,
                Device = device
            }
        });
    }

    /// <summary>The system input an identity stands for, or null when it is a panel
    /// button.</summary>
    private static string? SystemInput(string identity) => identity.ToLowerInvariant() switch
    {
        "start" => "START",
        "select" => "SELECT",
        "l3" => "L3",
        "r3" => "R3",
        "up" or "down" or "left" or "right" => "DPAD",
        _ => null
    };

    /// <summary>
    /// The slot this identity reaches on THIS cabinet. The per-player cartography wins
    /// when it exists - a two-panel cabinet can be wired differently on each side - and
    /// the shared map answers otherwise. Null when the identity reaches no slot: a face
    /// button of a pad that is not part of the panel is not a panel event.
    /// </summary>
    private int? ResolveSlot(int player, string identity)
    {
        // THE PER-PLAYER CARTOGRAPHY FIRST: it is what the LedManager wizard measured on
        // THIS panel, through the same reader this service uses, and what the remaps and
        // the MAME cfg are written against - so the marquee lights the very button the
        // game will fire.
        //
        // The global CabinetButtons is a legacy FALLBACK only. The two maps once diverged
        // - the reader used SDL's GameController layer while RetroArch used
        // gamecontrollerdb, so they named x/y and the shoulders/triggers differently, and
        // this resolved through the global on purpose. The reader now parses
        // gamecontrollerdb itself and matches RetroArch, so the per-player map is the
        // single truth; preferring the stale global lit the neighbouring button after a
        // recabling (x/y, L1/L2 and R1/R2 swapped) even though the games stayed correct.
        var map = CabinetCartographyStore.Read(player);
        if (map.Count == 0 && player != 1) map = CabinetCartographyStore.Read(1);
        if (map.Count == 0) map = ReadLegacyCabinetButtons();

        foreach (var (slot, wired) in map)
        {
            if (wired.Equals(identity, StringComparison.OrdinalIgnoreCase) && int.TryParse(slot, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>The cabinet-wide legacy map, kept only as a fallback for a cabinet with
    /// no per-player cartography yet. The per-player map the wizard measured wins - see
    /// <see cref="ResolveSlot"/>.</summary>
    private static IReadOnlyDictionary<string, string> ReadLegacyCabinetButtons()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");
            if (!File.Exists(path)) return result;

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                ?["ApiExpose"]?["PanelRemapExport"]?["CabinetButtons"] as System.Text.Json.Nodes.JsonObject;
            if (node is null) return result;

            foreach (var entry in node)
            {
                if (entry.Value is System.Text.Json.Nodes.JsonValue value
                    && value.TryGetValue<string>(out var identity))
                {
                    result[entry.Key] = identity;
                }
            }
        }
        catch
        {
            // unreadable settings: no map, the press is simply reported without a slot
        }

        return result;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop = true;
        _busSub?.Dispose();
        try { _thread?.Join(2000); } catch { /* arrêt best-effort */ }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stop = true;
        _busSub?.Dispose();
        try { _thread?.Join(2000); } catch { /* arrêt best-effort */ }
        _reader?.Dispose();
    }
}
