// Ported from LedManager (LedManager.Setup/Input/GamepadReader.cs), deliberately and
// without rewriting: it is the reader that measured the CabinetButtons mapping in the
// first place, so reading presses ANY other way would risk resolving a button to a
// different identity than the one the cabinet was configured with.
//
// It lives here because a cabinet must be able to check its wiring WITHOUT LedManager
// installed - the whole point of the exercise. Two copies of this logic is the price;
// two DIFFERENT answers to "which button is this" would be the bug.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Reads gamepad presses the way RetroArch really resolves them: through the SAME
/// SDL2.dll RetroArch uses (emulators\retroarch\SDL2.dll) and the SAME
/// gamecontrollerdb.txt RetroBat ships (system\tools\gamecontrollerdb.txt).
///
/// Crucially, it parses that DB ITSELF (matching the device GUID) and reads the RAW
/// joystick buttons/axes - instead of trusting SDL's GameController layer, whose
/// built-in database can shadow RetroBat's entry and hand back a different button
/// order (this is what silently swapped X/Y for a DirectInput arcade encoder). By
/// applying RetroBat's own mapping to the raw inputs, the identity we measure is
/// exactly what RetroArch sees - so CabinetButtons drives both rmp and MAME cfg
/// correctly with no downstream change.
///
/// The SDL→RetroPad face swap (a→b, b→a, x→y, y→x, shoulders, triggers) is the fixed
/// sdl2 joypad-driver convention, universal to every controller - not a particularism.
/// </summary>
public sealed class CabinetInputReader : IDisposable
{
    // ---- SDL2 P/Invoke (only the handful we need) --------------------------

    private const string Lib = "SDL2";

    [Flags]
    private enum InitFlags : uint
    {
        Joystick = 0x00000200,
        GameController = 0x00002000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlGuid
    {
        public ulong Lo;
        public ulong Hi;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_Init(InitFlags flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_Quit();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_NumJoysticks();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_JoystickOpen(int deviceIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_JoystickClose(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_JoystickUpdate();

    // Pomper les events SDL est indispensable pour DÉTECTER un branchement/débranchement :
    // sans ça, SDL_NumJoysticks() reste périmé après un replug (le hotplug n'est jamais vu).
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_PumpEvents();

    // Instance-id SDL d'un device par index (sans l'ouvrir) : sert à la signature de
    // branchement — il CHANGE à chaque replug, même si le compte de joysticks est identique.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_JoystickGetDeviceInstanceID(int deviceIndex);

    // Statut d'un joystick OUVERT : passe à false quand il est débranché — détecté par
    // SDL_JoystickUpdate SANS énumération/hotplug. C'est notre signal fiable « re-scanner ».
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_JoystickGetAttached(IntPtr joystick);

    // Ré-init du seul sous-système joystick : force une découverte FRAÎCHE des devices,
    // indépendante du pompage d'événements et du thread. C'est ce qui rattrape un replug.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_InitSubSystem(InitFlags flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(InitFlags flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_JoystickNumButtons(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_JoystickNumAxes(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern byte SDL_JoystickGetButton(IntPtr joystick, int button);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern short SDL_JoystickGetAxis(IntPtr joystick, int axis);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern byte SDL_JoystickGetHat(IntPtr joystick, int hat);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern SdlGuid SDL_JoystickGetGUID(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_JoystickGetGUIDString(SdlGuid guid, byte[] pszGUID, int cbGUID);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_JoystickName(IntPtr joystick);

    // ---- model -------------------------------------------------------------

    /// <summary>Result of one measurement: the RetroPad identity emitted, the device
    /// that emitted it, and the raw DirectInput button index (or -1 for an axis).</summary>
    public sealed record Press(string Identity, int DeviceIndex, int RawButton);

    /// <summary>An open device with RetroBat's mapping resolved onto its raw inputs.</summary>
    private sealed class Device
    {
        public required IntPtr Handle { get; init; }
        public required string Guid { get; init; }
        public required string Name { get; init; }
        public Dictionary<int, string> ButtonToIdentity { get; } = new();
        public List<(int Axis, int Sign, string Identity)> AxisToIdentity { get; } = new();
        public bool HasMapping => ButtonToIdentity.Count > 0 || AxisToIdentity.Count > 0;

        // Canal DIRECTIONS additif (dpad/hat/stick gauche), séparé de la résolution des
        // boutons cabinet ci-dessus. Sert uniquement au transport Replay.
        public List<(int Hat, int Mask, string Direction)> HatDirections { get; } = new();
        public List<(int Button, string Direction)> ButtonDirections { get; } = new();
        public List<(int Axis, int Sign, string Direction)> AxisDirections { get; } = new();

        /// <summary>D'ou vient le mappage : utile quand une manette ne repond pas.</summary>
        public string MappingSource { get; set; } = "aucun";
    }

    /// <summary>SDL controller-name → RetroPad identity (fixed sdl2 face swap).</summary>
    private static readonly IReadOnlyDictionary<string, string> FaceSwap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "b", ["b"] = "a", ["x"] = "y", ["y"] = "x",
        ["leftshoulder"] = "l", ["rightshoulder"] = "r",
        ["lefttrigger"] = "l2", ["righttrigger"] = "r2",
        ["back"] = "select", ["start"] = "start",
        ["leftstick"] = "l3", ["rightstick"] = "r3",
    };

    /// <summary>Dpad names → direction de transport Replay (additif, hors CabinetButtons).
    /// Le stick gauche (leftx/lefty) est traité à part car un seul axe porte deux directions.</summary>
    private static readonly IReadOnlyDictionary<string, string> DpadDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dpup"] = "up", ["dpdown"] = "down", ["dpleft"] = "left", ["dpright"] = "right",
    };

    /// <summary>Analog triggers/axes count as pressed past ~50% of the +32767 range.</summary>
    private const short AxisThreshold = 16000;

    private static bool _resolverInstalled;
    private readonly List<Device> _devices = new();
    private IReadOnlyList<string[]> _dbLines = Array.Empty<string[]>();
    private string _retroBatRoot = string.Empty;
    private bool _initialized;

    // ---- lifecycle ---------------------------------------------------------

    public (bool Ok, string Message) Initialize(string retroBatRoot)
    {
        try
        {
            InstallResolver(Path.Combine(retroBatRoot, "emulators", "retroarch", "SDL2.dll"));

            // read joystick events without owning a focused SDL window (we are WPF)
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");

            // Une manette Xbox se lit par XInput, pas par RawInput. Le pilote RawInput de ce SDL
            // (2.0.14) ouvre bien la manette (GUID en ...7200, boutons et chapeau declares) mais
            // n'en livre JAMAIS un appui dans ce processus : il attend des messages WM_INPUT
            // qu'aucune boucle de fenetre ne lui remet ici. Mesure du 2026-09-17 sur la borne :
            // en RawInput, zero appui en 40 s de A et de dpad ; par XInput (GUID en ...7801),
            // chaque appui vu. Le panel (DirectInput) n'est pas concerne, ES non plus : il a
            // son propre SDL et sa propre boucle.
            SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");

            if (SDL_Init(InitFlags.Joystick) != 0)
            {
                return (false, "SDL_Init a échoué.");
            }

            _initialized = true;

            _retroBatRoot = retroBatRoot;
            var db = Path.Combine(retroBatRoot, "system", "tools", "gamecontrollerdb.txt");
            _dbLines = LoadDb(db);
            return (true, $"{_dbLines.Count} mappages chargés depuis gamecontrollerdb.txt");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Opens every joystick and resolves RetroBat's mapping onto its raw
    /// inputs. Returns the number of devices we can read an identity from.</summary>
    public int OpenControllers()
    {
        CloseDevices();
        var count = SDL_NumJoysticks();
        for (var i = 0; i < count; i++)
        {
            var handle = SDL_JoystickOpen(i);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            var name = DeviceName(handle);

            // A virtual pad is not a panel. vJoy, ViGEm and the like are created by
            // software - a mapping tool, a streaming client, a wheel driver - and they
            // report the same identities as a real pad. Left in, one would occupy a
            // panel slot the cabinet does not have, and its player numbering would push
            // the REAL panel to player 2.
            if (IsVirtual(name))
            {
                SDL_JoystickClose(handle);
                continue;
            }

            var device = new Device { Handle = handle, Guid = GuidString(handle), Name = name };
            ApplyMapping(device);
            _devices.Add(device);
        }

        return _devices.Count(d => d.HasMapping);
    }

    /// <summary>The names of the devices actually kept, in player order - what the log
    /// has to show for "player 2 lit up" to ever be explainable.</summary>
    public IReadOnlyList<string> DeviceNames => _devices.Select(d => d.Name).ToList();

    /// <summary>Les appareils ouverts (GUID SDL, nom), dans l'ordre des index de Snapshot.</summary>
    public IReadOnlyList<PlayerNumbering.Device> Devices => _devices.Select(d => new PlayerNumbering.Device(d.Guid, d.Name)).ToList();

    /// <summary>Les memes appareils, avec l'etage qui a resolu leur mappage. Pour le
    /// journal : « 0 mapped device(s) » ne disait pas OU la resolution avait echoue.</summary>
    public IReadOnlyList<string> DeviceMappings =>
        _devices.Select(d => $"{d.Name} [{d.MappingSource}, GUID {d.Guid}]").ToList();

    /// <summary>How many joysticks Windows currently shows, WITHOUT touching the ones
    /// already open. Asking this before reopening is what lets the watcher leave a
    /// working device alone.</summary>
    public static int AttachedCount()
    {
        try
        {
            SDL_JoystickUpdate();
            return SDL_NumJoysticks();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Signature des joysticks ACTUELLEMENT branchés : compte + instance-ids SDL.
    /// Elle change à un unplug+replug MÊME à compte égal (nouvel instance-id) — là où le
    /// simple compte ne bougeait pas, laissant le watcher sur un handle mort. On pompe les
    /// events SDL d'abord, sinon le hotplug n'est pas détecté.</summary>
    public static string AttachedSignature()
    {
        try
        {
            SDL_PumpEvents();
            SDL_JoystickUpdate();
            var n = SDL_NumJoysticks();
            var sb = new System.Text.StringBuilder();
            sb.Append(n);
            for (var i = 0; i < n; i++)
            {
                sb.Append(':').Append(SDL_JoystickGetDeviceInstanceID(i));
            }
            return sb.ToString();
        }
        catch
        {
            return "err";
        }
    }

    /// <summary>Pompe les événements SDL + met à jour les joysticks ouverts. À appeler
    /// régulièrement sur LE thread qui a fait SDL_Init : c'est ce qui fait détecter à SDL
    /// les branchements/débranchements (hotplug) et rafraîchit l'état des boutons.</summary>
    public void Pump()
    {
        try
        {
            SDL_PumpEvents();
            SDL_JoystickUpdate();
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Vrai si on a AU MOINS un device ouvert ET que tous nos handles sont encore
    /// connectés. Un débranchement fait passer SDL_JoystickGetAttached à false sur le handle
    /// OUVERT — signal fiable, sans dépendre du hotplug ni du pompage d'événements.</summary>
    public bool HasWorkingDevices()
    {
        if (_devices.Count == 0) return false;
        try
        {
            SDL_JoystickUpdate();
            foreach (var d in _devices)
            {
                if (SDL_JoystickGetAttached(d.Handle) == 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ré-énumère les joysticks À FROID : ferme, quitte puis ré-initialise le
    /// sous-système joystick de SDL (découverte fraîche, indépendante du thread/pompage),
    /// puis rouvre. C'est ce qui rattrape un replug que SDL_NumJoysticks() seul ne voit pas.</summary>
    public int ForceReenumerate()
    {
        try
        {
            CloseDevices();
            SDL_QuitSubSystem(InitFlags.Joystick);
            SDL_InitSubSystem(InitFlags.Joystick);
            SDL_JoystickUpdate();
        }
        catch
        {
            // best-effort : même si le teardown échoue, on tente une réouverture.
        }
        return OpenControllers();
    }

    private static readonly string[] VirtualMarkers = { "vjoy", "virtual", "vigem", "dummy", "emulated" };

    private static bool IsVirtual(string name) =>
        VirtualMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string DeviceName(IntPtr joystick)
    {
        var ptr = SDL_JoystickName(joystick);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    /// <summary>Waits for a FRESH press (edge) on any open device and returns the
    /// RetroPad identity + device + raw button. Inputs already held when the call
    /// starts are ignored. Returns null on cancellation.</summary>
    public async Task<Press?> WaitForPressAsync(CancellationToken token)
    {
        if (!_initialized || _devices.Count == 0)
        {
            return null;
        }

        var held = new HashSet<(int, string)>(Snapshot().Select(p => (p.DeviceIndex, p.Identity)));

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(16, CancellationToken.None).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                break;
            }

            var now = Snapshot();
            foreach (var pressed in now)
            {
                if (!held.Contains((pressed.DeviceIndex, pressed.Identity)))
                {
                    return pressed;
                }
            }

            var active = now.Select(p => (p.DeviceIndex, p.Identity)).ToHashSet();
            held.RemoveWhere(h => !active.Contains(h));
        }

        return null;
    }

    /// <summary>Everything held down right now. Public here: the watcher polls it and
    /// diffs, where the wizard only waited for the next new press.</summary>
    public List<Press> Snapshot()
    {
        SDL_JoystickUpdate();
        var result = new List<Press>();
        for (var d = 0; d < _devices.Count; d++)
        {
            var device = _devices[d];
            foreach (var (button, identity) in device.ButtonToIdentity)
            {
                if (SDL_JoystickGetButton(device.Handle, button) != 0)
                {
                    result.Add(new Press(identity, d, button));
                }
            }

            foreach (var (axis, sign, identity) in device.AxisToIdentity)
            {
                var value = SDL_JoystickGetAxis(device.Handle, axis);
                var pressed = sign < 0 ? value < -AxisThreshold : value > AxisThreshold;
                if (pressed)
                {
                    result.Add(new Press(identity, d, -1));
                }
            }
        }

        return result;
    }

    /// <summary>Everything held on the DIRECTION channel right now (identity in
    /// up/down/left/right), read from the dpad hat, the dpad-as-button/axis mapping, or the
    /// left stick. Kept SEPARATE from <see cref="Snapshot"/> so cabinet button resolution
    /// (and the wiring wizard's WaitForPress) is untouched.</summary>
    public List<Press> SnapshotDirections()
    {
        SDL_JoystickUpdate();
        var result = new List<Press>();
        for (var d = 0; d < _devices.Count; d++)
        {
            var device = _devices[d];

            foreach (var (hat, mask, direction) in device.HatDirections)
            {
                if ((SDL_JoystickGetHat(device.Handle, hat) & mask) != 0)
                {
                    result.Add(new Press(direction, d, -1));
                }
            }

            foreach (var (button, direction) in device.ButtonDirections)
            {
                if (SDL_JoystickGetButton(device.Handle, button) != 0)
                {
                    result.Add(new Press(direction, d, button));
                }
            }

            foreach (var (axis, sign, direction) in device.AxisDirections)
            {
                var value = SDL_JoystickGetAxis(device.Handle, axis);
                var pressed = sign < 0 ? value < -AxisThreshold : value > AxisThreshold;
                if (pressed)
                {
                    result.Add(new Press(direction, d, -1));
                }
            }
        }

        return result;
    }

    /// <summary>Records a dpad/stick token onto the direction channel. Understands the
    /// three gamecontrollerdb encodings (hat hX.Y, button bN, axis [±]aN) plus the left
    /// stick (leftx/lefty), where one axis carries two opposite directions.</summary>
    private static void TryAddDirection(Device device, string name, string rawValue)
    {
        // Left stick: a single axis maps to two directions (negative vs positive).
        if (name.Equals("leftx", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lefty", StringComparison.OrdinalIgnoreCase))
        {
            var (axis, inverted) = ParseStickAxis(rawValue);
            if (axis < 0) return;
            var negSign = inverted ? +1 : -1; // '~' flips which end is which
            var posSign = -negSign;
            if (name.Equals("leftx", StringComparison.OrdinalIgnoreCase))
            {
                device.AxisDirections.Add((axis, negSign, "left"));
                device.AxisDirections.Add((axis, posSign, "right"));
            }
            else
            {
                device.AxisDirections.Add((axis, negSign, "up"));
                device.AxisDirections.Add((axis, posSign, "down"));
            }

            return;
        }

        if (!DpadDir.TryGetValue(name, out var dir)) return;

        var value = rawValue.TrimEnd('~');
        var sign = 0;
        if (value.Length > 0 && (value[0] == '+' || value[0] == '-'))
        {
            sign = value[0] == '-' ? -1 : 1;
            value = value[1..];
        }

        if (value.Length < 2) return;
        var kind = value[0];
        var body = value[1..];

        if (kind == 'h')
        {
            var dot = body.IndexOf('.');
            if (dot <= 0) return;
            if (int.TryParse(body[..dot], out var hat) && int.TryParse(body[(dot + 1)..], out var mask))
            {
                device.HatDirections.Add((hat, mask, dir));
            }
        }
        else if (kind == 'b')
        {
            if (int.TryParse(body, out var button)) device.ButtonDirections.Add((button, dir));
        }
        else if (kind == 'a')
        {
            if (int.TryParse(body, out var axis)) device.AxisDirections.Add((axis, sign == 0 ? 1 : sign, dir));
        }
    }

    /// <summary>Parses a leftx/lefty axis token ("a0", "a0~", "+a0"): axis index and whether
    /// the '~' inversion flag is set. Returns axis -1 when it is not an axis token.</summary>
    private static (int Axis, bool Inverted) ParseStickAxis(string raw)
    {
        var inverted = raw.EndsWith("~", StringComparison.Ordinal);
        var value = raw.TrimEnd('~');
        if (value.Length > 0 && (value[0] == '+' || value[0] == '-')) value = value[1..];
        if (value.Length >= 2 && value[0] == 'a' && int.TryParse(value[1..], out var axis)) return (axis, inverted);
        return (-1, false);
    }

    // ---- gamecontrollerdb ---------------------------------------------------

    /// <summary>Parses the DB into split token lines (GUID first, then name, then
    /// key:value pairs), keeping only Windows / platform-less entries.</summary>
    private static IReadOnlyList<string[]> LoadDb(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string[]>();
        }

        var lines = new List<string[]>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var tokens = line.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var platform = tokens.FirstOrDefault(t => t.StartsWith("platform:", StringComparison.OrdinalIgnoreCase));
            if (platform is not null && !platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(tokens);
        }

        return lines;
    }

    /// <summary>
    /// Resout le mappage de cet appareil, en trois etages.
    ///
    /// 1. GUID exact dans gamecontrollerdb.txt.
    /// 2. Les 24 premiers caracteres du GUID (bus + identifiants constructeur et produit).
    ///    Windows moderne suffixe le GUID selon le pilote (RawInput 7200, HIDAPI USB 6800,
    ///    HIDAPI Bluetooth 6803) la ou la base finit par des zeros : la comparaison exacte
    ///    echouait donc pour TOUTES les manettes du commerce, et le panneau restait muet.
    /// 3. La configuration d'EmulationStation (es_input.cfg). Une manette absente de la base
    ///    a forcement ete configuree la au moins une fois : c'est le cas des encodeurs
    ///    d'arcade sans marque.
    /// </summary>
    private void ApplyMapping(Device device)
    {
        var (entry, source) = FindDbEntry(_dbLines, device.Guid);

        if (entry is null)
        {
            if (ApplyEmulationStationMapping(device))
            {
                device.MappingSource = "es_input.cfg";
            }

            return; // unknown device: no identity resolved (caller warns)
        }

        ApplyDbEntry(device, entry);
        if (device.HasMapping)
        {
            device.MappingSource = source;
        }
    }

    /// <summary>GUID que SDL 2.0.x donne a une manette ouverte par XInput : « xinput » en
    /// hexadecimal, suivi du sous-type. Aucune de ces manettes ne porte de constructeur ni de
    /// produit.</summary>
    private const string XInputGuidPrefix = "78696e707574";

    /// <summary>
    /// La ligne de gamecontrollerdb.txt d'une manette, et l'etage qui l'a trouvee.
    ///
    /// 1. GUID exact ;
    /// 2. XINPUT : le SDL de RetroArch (2.0.14) ouvre les manettes Xbox par XInput, avec un GUID
    ///    qui vaut « xinput » en hexadecimal, sans constructeur ni produit. La base les range sous
    ///    la cle litterale « xinput » : sans cet etage, aucune manette Xbox ouverte par XInput
    ///    n'etait reconnue (une manette Xbox « ne remontait pas », constate le 2026-09-17).
    ///    Quand le SDL sait retrouver constructeur et produit, le GUID XInput est plutot
    ///    « 030000005e0400008e02000000007801 » (« x » puis le sous-type en queue) : c'est
    ///    l'etage 3 qui le resout ;
    /// 3. constructeur + produit (24 premiers caracteres) : le suffixe de pilote change d'une
    ///    machine a l'autre (RawInput, HIDAPI) et la base n'en porte aucun.
    /// </summary>
    internal static (string[]? Entry, string Source) FindDbEntry(IReadOnlyList<string[]> db, string guid)
    {
        var entry = db.FirstOrDefault(t => string.Equals(t[0], guid, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            return (entry, "GUID exact");
        }

        if (guid.StartsWith(XInputGuidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            entry = db.FirstOrDefault(t => string.Equals(t[0], "xinput", StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                return (entry, "xinput");
            }
        }

        if (guid.Length >= 24)
        {
            var prefixe = guid[..24];
            entry = db.FirstOrDefault(t => t[0].Length >= 24
                && string.Equals(t[0][..24], prefixe, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                return (entry, "constructeur+produit");
            }
        }

        return (null, "aucun");
    }

    /// <summary>Applique les jetons d'une ligne de gamecontrollerdb.txt.</summary>
    private static void ApplyDbEntry(Device device, string[] entry)
    {
        foreach (var token in entry.Skip(2))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = token[..colon];
            if (!FaceSwap.TryGetValue(name, out var identity))
            {
                // Additif : dpad / stick gauche → directions de transport Replay. Ne touche
                // PAS la résolution des boutons cabinet (ButtonToIdentity/AxisToIdentity).
                TryAddDirection(device, name, token[(colon + 1)..]);
                continue; // dpad / guide / misc - not a cabinet identity
            }

            var value = token[(colon + 1)..].TrimEnd('~'); // '~' = inverted axis
            var sign = 0;
            if (value.Length > 0 && (value[0] == '+' || value[0] == '-'))
            {
                sign = value[0] == '-' ? -1 : 1;
                value = value[1..];
            }

            if (value.Length < 2)
            {
                continue;
            }

            var kind = value[0];
            if (!int.TryParse(value[1..], out var index))
            {
                continue;
            }

            if (kind == 'b')
            {
                device.ButtonToIdentity[index] = identity;
            }
            else if (kind == 'a')
            {
                device.AxisToIdentity.Add((index, sign, identity));
            }
            // 'h' (hat) inputs are dpad directions - not cabinet identities
        }
    }

    /// <summary>Noms d'EmulationStation vers identites RetroPad. ES suit la convention SNES :
    /// « b » est le bouton du BAS (valider), « a » celui de droite.</summary>
    private static readonly IReadOnlyDictionary<string, string> EsToIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = "b", ["a"] = "a", ["y"] = "y", ["x"] = "x",
        ["pageup"] = "l", ["pagedown"] = "r",
        ["l2"] = "l2", ["r2"] = "r2", ["l3"] = "l3", ["r3"] = "r3",
        ["select"] = "select", ["start"] = "start",
    };

    /// <summary>Directions d'EmulationStation (dpad et stick gauche) pour le transport Replay.</summary>
    private static readonly IReadOnlyDictionary<string, string> EsToDirection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = "up", ["down"] = "down", ["left"] = "left", ["right"] = "right",
        ["joystick1up"] = "up", ["joystick1down"] = "down",
        ["joystick1left"] = "left", ["joystick1right"] = "right",
    };

    /// <summary>
    /// Dernier recours : la configuration que le joueur a faite dans EmulationStation. Un
    /// encodeur d'arcade sans marque n'est dans aucune base SDL, mais il a ete configure la.
    /// </summary>
    private bool ApplyEmulationStationMapping(Device device)
    {
        try
        {
            if (string.IsNullOrEmpty(_retroBatRoot))
            {
                return false;
            }

            var chemin = Path.Combine(_retroBatRoot, "emulationstation", ".emulationstation", "es_input.cfg");
            if (!File.Exists(chemin))
            {
                return false;
            }

            var configs = XDocument.Load(chemin).Root?
                .Elements("inputConfig")
                .Where(e => string.Equals((string?)e.Attribute("type"), "joystick", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (configs is null || configs.Count == 0)
            {
                return false;
            }

            // Le GUID d'abord, son prefixe ensuite (ES l'enregistre avec le suffixe de son
            // propre pilote), le nom en dernier.
            var config = configs.FirstOrDefault(e => string.Equals((string?)e.Attribute("deviceGUID"), device.Guid, StringComparison.OrdinalIgnoreCase))
                ?? (device.Guid.Length >= 24
                    ? configs.FirstOrDefault(e => ((string?)e.Attribute("deviceGUID") ?? string.Empty).Length >= 24
                        && string.Equals(((string?)e.Attribute("deviceGUID"))![..24], device.Guid[..24], StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? configs.FirstOrDefault(e => string.Equals((string?)e.Attribute("deviceName"), device.Name, StringComparison.OrdinalIgnoreCase));
            if (config is null)
            {
                return false;
            }

            foreach (var input in config.Elements("input"))
            {
                var name = (string?)input.Attribute("name") ?? string.Empty;
                var type = (string?)input.Attribute("type") ?? string.Empty;
                if (!int.TryParse((string?)input.Attribute("id"), out var id))
                {
                    continue;
                }
                _ = int.TryParse((string?)input.Attribute("value"), out var value);

                if (EsToIdentity.TryGetValue(name, out var identity))
                {
                    if (type.Equals("button", StringComparison.OrdinalIgnoreCase))
                    {
                        device.ButtonToIdentity[id] = identity;
                    }
                    else if (type.Equals("axis", StringComparison.OrdinalIgnoreCase))
                    {
                        device.AxisToIdentity.Add((id, Math.Sign(value), identity));
                    }

                    continue;
                }

                if (!EsToDirection.TryGetValue(name, out var direction))
                {
                    continue;
                }

                if (type.Equals("hat", StringComparison.OrdinalIgnoreCase))
                {
                    device.HatDirections.Add((id, value, direction));
                }
                else if (type.Equals("button", StringComparison.OrdinalIgnoreCase))
                {
                    device.ButtonDirections.Add((id, direction));
                }
                else if (type.Equals("axis", StringComparison.OrdinalIgnoreCase))
                {
                    device.AxisDirections.Add((id, Math.Sign(value), direction));
                }
            }

            return device.HasMapping;
        }
        catch (Exception)
        {
            // Un es_input.cfg absent, tronque ou illisible ne doit pas empecher la lecture
            // des autres manettes.
            return false;
        }
    }

    private static string GuidString(IntPtr joystick)
    {
        var guid = SDL_JoystickGetGUID(joystick);
        var buffer = new byte[33];
        SDL_JoystickGetGUIDString(guid, buffer, buffer.Length);
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private void CloseDevices()
    {
        foreach (var device in _devices)
        {
            SDL_JoystickClose(device.Handle);
        }

        _devices.Clear();
    }

    private static void InstallResolver(string sdlPath)
    {
        if (_resolverInstalled)
        {
            return;
        }

        // route the "SDL2" DllImport to RetroArch's exact SDL2.dll
        NativeLibrary.SetDllImportResolver(typeof(CabinetInputReader).Assembly, (name, _, _) =>
            name == Lib && File.Exists(sdlPath) ? NativeLibrary.Load(sdlPath) : IntPtr.Zero);
        _resolverInstalled = true;
    }

    public void Dispose()
    {
        CloseDevices();
        if (_initialized)
        {
            SDL_Quit();
            _initialized = false;
        }
    }
}
