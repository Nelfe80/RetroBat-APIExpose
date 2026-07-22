using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

/// <summary>
/// OBS de la borne, au service du hub de salle (diffusion des parties sur les
/// écrans et les streams — Lot B des Écrans de la salle) :
/// - status : OBS installé ? websocket configuré ? en marche ?
/// - websocket : active obs-websocket (mot de passe généré ou existant
///   conservé) — le hub s'en sert ensuite pour piloter scènes et sorties ;
/// - launch/quit : démarrage minimisé avec profil/collection dédiés (le
///   setup personnel de la machine n'est jamais touché).
/// Aucune installation ici : OBS est provisionné par l'installateur du hub à
/// l'enrôlement de la borne.
/// </summary>
[ApiController]
[Tags("System & Health")]
[Route("api/v1/obs")]
public sealed class ObsController : ControllerBase
{
    private static readonly string[] KnownPaths =
    [
        @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
        @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe"
    ];

    private static string? FindObs()
    {
        foreach (var path in KnownPaths)
        {
            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe");
            var value = key?.GetValue(null) as string;
            return value is not null && System.IO.File.Exists(value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string WebSocketConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    private static bool IsRunning() => Process.GetProcessesByName("obs64").Length > 0;

    public sealed record GameScreen(int X, int Y, int Width, int Height);

    public sealed record ObsStatus(
        bool Installed, string? Path, bool Running,
        bool WebSocketEnabled, int WebSocketPort, GameScreen? GameScreen = null);

    /// <summary>OBS presence, run state, obs-websocket configuration — and the
    /// bounds of the RETROBAT screen (les bornes ont souvent plusieurs écrans :
    /// jeu, marquee, écran carte… — la capture doit viser le bon).</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ObsStatus), StatusCodes.Status200OK)]
    public ActionResult<ObsStatus> Status()
    {
        var obs = FindObs();
        var (enabled, port, _) = ReadWebSocketConfig();
        return Ok(new ObsStatus(obs is not null, obs, IsRunning(), enabled, port, ResolveGameScreen()));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>Écran où tourne EmulationStation (même logique que le badge
    /// QR) — celui que la diffusion doit capturer.</summary>
    private static GameScreen? ResolveGameScreen()
    {
        try
        {
            var handle = FindWindowW(null, "EmulationStation");
            if (handle == IntPtr.Zero)
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero &&
                            process.ProcessName.Contains("emulationstation", StringComparison.OrdinalIgnoreCase))
                        {
                            handle = process.MainWindowHandle;
                            break;
                        }
                    }
                }
            }

            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            {
                return null;
            }

            var screen = System.Windows.Forms.Screen.FromRectangle(
                System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            return new GameScreen(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Ensures obs-websocket is enabled and returns its credentials (the hub
    /// stores them). An existing password is KEPT — this call is idempotent.
    /// OBS must be closed for a fresh write to stick (it rewrites its config
    /// on exit) ; when running with websocket already on, current values are
    /// returned as-is.
    /// </summary>
    [HttpPost("websocket")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult EnableWebSocket()
    {
        if (FindObs() is null)
        {
            return NotFound(new { error = "OBS non installé sur cette borne." });
        }

        var (enabled, port, password) = ReadWebSocketConfig();
        if (enabled && password.Length > 0)
        {
            return Ok(new { port, password });
        }

        if (IsRunning())
        {
            // Écrire pendant qu'OBS tourne serait perdu à sa fermeture.
            return Conflict(new { error = "OBS est ouvert — fermez-le pour activer le websocket." });
        }

        var newPassword = password.Length > 0
            ? password
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var config = new JsonObject
        {
            ["first_load"] = false,
            ["server_enabled"] = true,
            ["server_port"] = port,
            ["alerts_enabled"] = false,
            ["auth_required"] = true,
            ["server_password"] = newPassword
        };
        Directory.CreateDirectory(Path.GetDirectoryName(WebSocketConfigPath)!);
        System.IO.File.WriteAllText(WebSocketConfigPath, config.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        return Ok(new { port, password = newPassword });
    }

    /// <summary>
    /// Écrit le profil OBS « RetroBorne » COMPLET sur disque, OBS FERMÉ (il
    /// est quitté d'abord s'il tourne) : sortie « enregistrement » avancée
    /// FFmpeg → SRT vers le hub. C'est la seule façon fiable — les
    /// paramètres poussés en websocket sur un profil actif ne rebranchent
    /// pas les sorties (leçon : un StartRecord partait en mp4 Simple).
    /// Le profil par défaut de la machine n'est jamais touché.
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Provision([FromBody] ObsProvisionRequest request)
    {
        if (FindObs() is null)
        {
            return NotFound(new { error = "OBS non installé sur cette borne." });
        }

        if (string.IsNullOrWhiteSpace(request.SrtUrl) || !request.SrtUrl.StartsWith("srt://"))
        {
            return BadRequest(new { error = "srtUrl requis (srt://…)." });
        }

        if (IsRunning())
        {
            await Quit();
            // Attendre la fin REELLE : un zombie encore énuméré faisait
            // échouer la relance qui suit (« déjà en marche » puis plus rien).
            for (var wait = 0; wait < 16 && IsRunning(); wait++)
            {
                await Task.Delay(500);
            }
        }

        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "basic", "profiles", "RetroBorne");
        Directory.CreateDirectory(profileDir);
        // FFVEncoderId/FFAEncoderId : identifiants de codec FFmpeg (H264=27,
        // AAC=86018) — la sortie FFmpeg d'OBS choisit l'encodeur par id.
        var ini = $"""
            [General]
            Name=RetroBorne

            [Output]
            Mode=Advanced

            [AdvOut]
            RecType=FFmpeg
            FFOutputToFile=false
            FFURL={request.SrtUrl}
            FFFormat=mpegts
            FFFormatMimeType=video/mp2t
            FFExtension=ts
            FFIgnoreCompat=true
            FFVEncoder=libx264
            FFVEncoderId=27
            FFVBitrate={Math.Clamp(request.VideoBitrate ?? 4000, 500, 20000)}
            FFVGOPSize=60
            FFVCustom=preset=veryfast tune=zerolatency
            FFAEncoder=aac
            FFAEncoderId=86018
            FFABitrate=160
            FFAudioMixes=1

            [Video]
            BaseCX=1920
            BaseCY=1080
            OutputCX=1280
            OutputCY=720
            FPSType=0
            FPSCommon=30
            """;
        System.IO.File.WriteAllText(Path.Combine(profileDir, "basic.ini"), ini);
        return Ok(new { ok = true, profile = "RetroBorne" });
    }

    /// <summary>Starts OBS minimized on the dedicated profile/collection —
    /// never the machine's default setup. No-op when already running.</summary>
    [HttpPost("launch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Launch([FromBody] ObsLaunchRequest? request)
    {
        var obs = FindObs();
        if (obs is null)
        {
            return NotFound(new { error = "OBS non installé sur cette borne." });
        }

        if (IsRunning())
        {
            return Ok(new { started = false, running = true });
        }

        var args = "--minimize-to-tray --disable-shutdown-check --multi";
        if (!string.IsNullOrWhiteSpace(request?.Profile))
        {
            args += $" --profile \"{request.Profile}\"";
        }

        if (!string.IsNullOrWhiteSpace(request?.Collection))
        {
            args += $" --collection \"{request.Collection}\"";
        }

        Process.Start(new ProcessStartInfo(obs, args)
        {
            WorkingDirectory = Path.GetDirectoryName(obs)!,
            UseShellExecute = false
        });
        return Ok(new { started = true, running = true });
    }

    /// <summary>Closes OBS (graceful, then kill after a grace period).</summary>
    [HttpPost("quit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Quit()
    {
        foreach (var process in Process.GetProcessesByName("obs64"))
        {
            using (process)
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(4000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        await Task.CompletedTask;
        return Ok(new { running = IsRunning() });
    }

    private static (bool Enabled, int Port, string Password) ReadWebSocketConfig()
    {
        try
        {
            if (!System.IO.File.Exists(WebSocketConfigPath))
            {
                return (false, 4455, "");
            }

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(WebSocketConfigPath));
            var root = doc.RootElement;
            var enabled = root.TryGetProperty("server_enabled", out var e) && e.GetBoolean();
            var port = root.TryGetProperty("server_port", out var p) && p.TryGetInt32(out var portValue)
                ? portValue
                : 4455;
            var password = root.TryGetProperty("server_password", out var pw) ? pw.GetString() ?? "" : "";
            return (enabled, port, password);
        }
        catch (Exception)
        {
            return (false, 4455, "");
        }
    }
}

public sealed record ObsLaunchRequest(string? Profile = null, string? Collection = null);

public sealed record ObsProvisionRequest(string SrtUrl, int? VideoBitrate = null);
