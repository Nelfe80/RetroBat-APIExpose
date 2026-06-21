using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public interface IEsControllerInputBackend
{
    string Name { get; }
    EsControllerBackendStatus GetStatus(ApiExposeOptions.EsControllerOptions options);
    Task SendInputAsync(string input, int holdMs, ApiExposeOptions.EsControllerOptions options, CancellationToken cancellationToken);
    Task RightClickAsync(ApiExposeOptions.EsControllerOptions options, bool warn, CancellationToken cancellationToken);
    Task ReleaseAllAsync(ApiExposeOptions.EsControllerOptions options);
}

public class EsControllerInputBackendProvider
{
    private readonly IReadOnlyDictionary<string, IEsControllerInputBackend> _backends;

    public EsControllerInputBackendProvider(IEnumerable<IEsControllerInputBackend> backends)
    {
        _backends = backends.ToDictionary(backend => backend.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IEsControllerInputBackend Resolve(string backendName)
    {
        var requested = string.IsNullOrWhiteSpace(backendName) ? "dry-run" : backendName.Trim();
        return _backends.TryGetValue(requested, out var backend)
            ? backend
            : new MissingEsControllerInputBackend(requested);
    }
}

public class DryRunEsControllerInputBackend : IEsControllerInputBackend
{
    public string Name => "dry-run";

    public EsControllerBackendStatus GetStatus(ApiExposeOptions.EsControllerOptions options)
    {
        return new EsControllerBackendStatus
        {
            Backend = Name,
            Ready = true,
            DryRun = true,
            Message = "Dry-run backend active: navigation plans are computed but no controller input is sent."
        };
    }

    public Task SendInputAsync(string input, int holdMs, ApiExposeOptions.EsControllerOptions options, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task RightClickAsync(ApiExposeOptions.EsControllerOptions options, bool warn, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ReleaseAllAsync(ApiExposeOptions.EsControllerOptions options)
    {
        return Task.CompletedTask;
    }
}

public class KeyboardEsControllerInputBackend : IEsControllerInputBackend
{
    private readonly IToastNotificationService _toastNotificationService;

    private static readonly IReadOnlyDictionary<string, string> EsInputAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = "up",
        ["down"] = "down",
        ["left"] = "left",
        ["right"] = "right",
        ["confirm"] = "a",
        ["enter"] = "a",
        ["back"] = "b",
        ["a"] = "a",
        ["b"] = "b",
        ["x"] = "x",
        ["y"] = "y",
        ["start"] = "start",
        ["select"] = "select",
        ["menu"] = "start",
        ["pageup"] = "pageup",
        ["pagedown"] = "pagedown",
        ["home"] = "l2",
        ["end"] = "r2",
        ["l2"] = "l2",
        ["r2"] = "r2"
    };

    public KeyboardEsControllerInputBackend(IToastNotificationService toastNotificationService)
    {
        _toastNotificationService = toastNotificationService;
    }

    public string Name => "keyboard";

    public EsControllerBackendStatus GetStatus(ApiExposeOptions.EsControllerOptions options)
    {
        var mapping = LoadKeyboardMapping();
        var missing = EsControllerInputs.Allowed
            .Where(input => !TryResolveKey(mapping, input, out _))
            .ToArray();
        var esRunning = TryGetEmulationStationMainWindow(out _);
        var foreground = IsEmulationStationForeground();
        var foregroundOk = !options.RequireEmulationStationForeground || foreground || (options.FocusEmulationStationBeforeInput && esRunning);
        var ready = missing.Length == 0 && foregroundOk;
        var status = new EsControllerBackendStatus
        {
            Backend = Name,
            Ready = ready,
            DryRun = false,
            Message = ready
                ? "Keyboard backend ready: inputs will use the existing keyboard mapping from es_input.cfg."
                : "Keyboard backend is not ready."
        };

        status.Details["esInputPath"] = RetroBatPaths.EmulationStationConfigRoot + "\\es_input.cfg";
        status.Details["requireEmulationStationForeground"] = options.RequireEmulationStationForeground.ToString();
        status.Details["focusEmulationStationBeforeInput"] = options.FocusEmulationStationBeforeInput.ToString();
        status.Details["clickEmulationStationIfFocusFails"] = options.ClickEmulationStationIfFocusFails.ToString();
        status.Details["focusWarningEnabled"] = options.FocusWarningEnabled.ToString();
        status.Details["keyboardDelivery"] = "post-message";
        status.Details["emulationStationRunning"] = esRunning.ToString();
        status.Details["emulationStationForeground"] = foreground.ToString();
        status.Details["missingInputs"] = string.Join(",", missing);
        status.Details["mapping"] = string.Join(",", mapping.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));

        if (missing.Length > 0)
        {
            status.Message = "Keyboard backend is missing required mappings in es_input.cfg: " + string.Join(", ", missing);
        }
        else if (!foregroundOk)
        {
            status.Message = "Keyboard backend is waiting for EmulationStation to be the foreground window.";
        }

        return status;
    }

    public async Task SendInputAsync(string input, int holdMs, ApiExposeOptions.EsControllerOptions options, CancellationToken cancellationToken)
    {
        var mapping = LoadKeyboardMapping();
        if (!TryResolveKey(mapping, input, out var key))
        {
            throw new InvalidOperationException($"No keyboard mapping found for input '{input}'.");
        }

        if (options.RequireEmulationStationForeground)
        {
            if (!IsEmulationStationForeground() && options.FocusEmulationStationBeforeInput)
            {
                await ShowFocusWarningAsync(options, "APIExpose prend le focus EmulationStation", cancellationToken);
                FocusEmulationStation();
                await Task.Delay(120, cancellationToken);
            }

            if (!IsEmulationStationForeground() && options.ClickEmulationStationIfFocusFails)
            {
                await ShowFocusWarningAsync(options, "APIExpose clique dans EmulationStation pour reprendre le focus", cancellationToken);
                ClickEmulationStationWindow();
                await Task.Delay(120, cancellationToken);
            }

            if (!IsEmulationStationForeground())
            {
                throw new InvalidOperationException("EmulationStation is not the foreground window.");
            }
        }

        KeyDown(key);
        try
        {
            await Task.Delay(holdMs, cancellationToken);
        }
        finally
        {
            KeyUp(key);
        }
    }

    public async Task RightClickAsync(ApiExposeOptions.EsControllerOptions options, bool warn, CancellationToken cancellationToken)
    {
        if (warn && options.RightClickWarningEnabled)
        {
            await ShowFocusWarningAsync(options, "APIExpose envoie un clic droit dans EmulationStation", cancellationToken);
        }

        FocusEmulationStation();
        await Task.Delay(120, cancellationToken);
        RightClickEmulationStationWindow();
    }

    public Task ReleaseAllAsync(ApiExposeOptions.EsControllerOptions options)
    {
        return Task.CompletedTask;
    }

    private async Task ShowFocusWarningAsync(
        ApiExposeOptions.EsControllerOptions options,
        string message,
        CancellationToken cancellationToken)
    {
        if (!options.FocusWarningEnabled)
        {
            return;
        }

        await _toastNotificationService.EnqueueAsync(new ToastNotification
        {
            Type = "UI",
            Title = "APIExpose Controller",
            Message = message,
            DurationMs = Math.Clamp(options.FocusWarningDurationMs, 300, 5000),
            Position = ToastPosition.TopCenter,
            Animation = ToastAnimation.SlideFromTop
        }, cancellationToken);
    }

    private static Dictionary<string, string> LoadKeyboardMapping()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_input.cfg");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var document = XDocument.Load(path);
            var keyboardNode = document
                .Descendants("inputConfig")
                .FirstOrDefault(node => string.Equals(node.Attribute("type")?.Value, "keyboard", StringComparison.OrdinalIgnoreCase));
            if (keyboardNode == null)
            {
                return result;
            }

            foreach (var input in keyboardNode.Elements("input"))
            {
                var name = input.Attribute("name")?.Value?.Trim();
                var type = input.Attribute("type")?.Value?.Trim();
                var id = input.Attribute("id")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(name) &&
                    string.Equals(type, "key", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(id))
                {
                    result[name] = id;
                }
            }
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static bool TryResolveKey(IReadOnlyDictionary<string, string> mapping, string input, out KeyboardKey key)
    {
        key = default;
        var normalized = EsControllerInputs.Normalize(input);
        if (TryResolveDirectKey(normalized, out key))
        {
            return true;
        }

        if (!EsInputAliases.TryGetValue(normalized, out var esInputName))
        {
            return false;
        }

        if (!mapping.TryGetValue(esInputName, out var sdlKeyIdText) || !int.TryParse(sdlKeyIdText, out var sdlKeyId))
        {
            return false;
        }

        return TryMapSdlKey(sdlKeyId, out key);
    }

    private static bool TryResolveDirectKey(string input, out KeyboardKey key)
    {
        var virtualKey = input switch
        {
            "f5" => (ushort)0x74,
            _ => (ushort)0
        };

        if (virtualKey == 0)
        {
            key = default;
            return false;
        }

        key = new KeyboardKey(virtualKey, (ushort)MapVirtualKey(virtualKey, 0));
        return true;
    }

    private static bool TryMapSdlKey(int sdlKeyId, out KeyboardKey key)
    {
        ushort virtualKey = sdlKeyId switch
        {
            8 => 0x08, // Backspace
            9 => 0x09, // Tab
            13 => 0x0D, // Enter
            27 => 0x1B, // Escape
            32 => 0x20, // Space
            1073741898 => 0x24, // Home
            1073741899 => 0x21, // PageUp
            1073741901 => 0x23, // End
            1073741902 => 0x22, // PageDown
            1073741903 => 0x27, // Right
            1073741904 => 0x25, // Left
            1073741905 => 0x28, // Down
            1073741906 => 0x26, // Up
            _ when sdlKeyId >= '0' && sdlKeyId <= '9' => (ushort)sdlKeyId,
            _ when sdlKeyId >= 'a' && sdlKeyId <= 'z' => (ushort)char.ToUpperInvariant((char)sdlKeyId),
            _ when sdlKeyId >= 'A' && sdlKeyId <= 'Z' => (ushort)sdlKeyId,
            _ => (ushort)0
        };

        if (virtualKey == 0)
        {
            key = default;
            return false;
        }

        key = new KeyboardKey(virtualKey, (ushort)MapVirtualKey(virtualKey, 0));
        return true;
    }

    private static bool IsEmulationStationForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "emulationstation", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetEmulationStationMainWindow(out IntPtr mainWindow)
    {
        mainWindow = IntPtr.Zero;
        try
        {
            var process = Process.GetProcessesByName("emulationstation")
                .Where(p => p.MainWindowHandle != IntPtr.Zero)
                .OrderByDescending(p => p.StartTime)
                .FirstOrDefault();
            if (process == null)
            {
                return false;
            }

            mainWindow = process.MainWindowHandle;
            return mainWindow != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static void FocusEmulationStation()
    {
        if (!TryGetEmulationStationMainWindow(out var mainWindow))
        {
            return;
        }

        var foregroundWindow = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(mainWindow, out _);
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);

        var attachedTarget = targetThread != 0 && AttachThreadInput(currentThread, targetThread, true);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            _ = ShowWindow(mainWindow, 9);
            _ = BringWindowToTop(mainWindow);
            _ = SetActiveWindow(mainWindow);
            _ = SetFocus(mainWindow);
            _ = SetForegroundWindow(mainWindow);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    private static void ClickEmulationStationWindow()
    {
        if (!TryGetEmulationStationMainWindow(out var mainWindow) ||
            !GetWindowRect(mainWindow, out var rect))
        {
            return;
        }

        var x = rect.Left + Math.Max(1, (rect.Right - rect.Left) / 2);
        var y = rect.Top + Math.Max(1, (rect.Bottom - rect.Top) / 2);
        if (!GetCursorPos(out var previous))
        {
            previous = new POINT { X = x, Y = y };
        }

        _ = SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        _ = SetCursorPos(previous.X, previous.Y);
    }

    private static void RightClickEmulationStationWindow()
    {
        if (!TryGetEmulationStationMainWindow(out var mainWindow) ||
            !GetWindowRect(mainWindow, out var rect))
        {
            return;
        }

        var x = rect.Left + Math.Max(1, (rect.Right - rect.Left) / 2);
        var y = rect.Top + Math.Max(1, (rect.Bottom - rect.Top) / 2);
        if (!GetCursorPos(out var previous))
        {
            previous = new POINT { X = x, Y = y };
        }

        _ = SetCursorPos(x, y);
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
        _ = SetCursorPos(previous.X, previous.Y);
    }

    private static void KeyDown(KeyboardKey key)
    {
        SendKeyboardInput(key, keyUp: false);
    }

    private static void KeyUp(KeyboardKey key)
    {
        SendKeyboardInput(key, keyUp: true);
    }

    private static void SendKeyboardInput(KeyboardKey key, bool keyUp)
    {
        if (TryGetEmulationStationMainWindow(out var mainWindow))
        {
            _ = PostMessage(
                mainWindow,
                keyUp ? 0x0101u : 0x0100u,
                (IntPtr)key.VirtualKey,
                BuildKeyMessageLParam(key, keyUp));
            return;
        }

        var flags = 0x0008u; // KEYEVENTF_SCANCODE
        if (keyUp)
        {
            flags |= 0x0002u;
        }

        if (IsExtendedKey(key.VirtualKey))
        {
            flags |= 0x0001u;
        }

        var input = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = key.ScanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort virtualKey)
    {
        return virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;
    }

    private static IntPtr BuildKeyMessageLParam(KeyboardKey key, bool keyUp)
    {
        var value = 1 | (key.ScanCode << 16);
        if (IsExtendedKey(key.VirtualKey))
        {
            value |= 1 << 24;
        }

        if (keyUp)
        {
            value |= 1 << 30;
            value |= unchecked((int)0x80000000);
        }

        return (IntPtr)value;
    }

    private readonly record struct KeyboardKey(ushort VirtualKey, ushort ScanCode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private struct POINT
    {
        public int X;
        public int Y;
    }

}

public class MissingEsControllerInputBackend : IEsControllerInputBackend
{
    public MissingEsControllerInputBackend(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public EsControllerBackendStatus GetStatus(ApiExposeOptions.EsControllerOptions options)
    {
        return new EsControllerBackendStatus
        {
            Backend = Name,
            Ready = false,
            DryRun = false,
            Message = $"Unknown ES controller backend '{Name}'."
        };
    }

    public Task SendInputAsync(string input, int holdMs, ApiExposeOptions.EsControllerOptions options, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException($"Unknown ES controller backend '{Name}'.");
    }

    public Task RightClickAsync(ApiExposeOptions.EsControllerOptions options, bool warn, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException($"Unknown ES controller backend '{Name}'.");
    }

    public Task ReleaseAllAsync(ApiExposeOptions.EsControllerOptions options)
    {
        return Task.CompletedTask;
    }
}
