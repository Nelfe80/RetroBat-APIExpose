using RetroBat.Domain.Models;

namespace RetroBat.Api.Infrastructure;

public static class EsControllerInputs
{
    public static readonly string[] Allowed =
    [
        "up",
        "down",
        "left",
        "right",
        "confirm",
        "enter",
        "back",
        "a",
        "b",
        "x",
        "y",
        "start",
        "select",
        "menu",
        "pageup",
        "pagedown",
        "home",
        "end",
        "l2",
        "r2",
        "f5"
    ];

    public static bool IsAllowed(string input)
    {
        return Allowed.Contains(input, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string input)
    {
        return (input ?? string.Empty).Trim().ToLowerInvariant();
    }
}

public class EsControllerStatus
{
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public string Backend { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public bool EsRunning { get; set; }
    public string CurrentView { get; set; } = string.Empty;
    public string CurrentSystem { get; set; } = string.Empty;
    public string CurrentGame { get; set; } = string.Empty;
    public bool NavigationInProgress { get; set; }
    public EsControllerBackendStatus BackendStatus { get; set; } = new();
    public EsSelectionSnapshot? LastSelection { get; set; }
    public string[] SupportedInputs { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public class EsControllerBackendStatus
{
    public string Backend { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public bool DryRun { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string> Details { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class EsSelectionSnapshot
{
    public string SystemId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; }

    public static EsSelectionSnapshot FromGame(GameReference game, string source)
    {
        return new EsSelectionSnapshot
        {
            SystemId = game.SystemId,
            GamePath = game.GamePath,
            GameName = game.GameName,
            GameId = game.GameId,
            Source = source,
            CapturedAtUtc = DateTime.UtcNow
        };
    }
}

public class EsControllerTapRequest
{
    /// <summary>Input to press: up, down, left, right, a, b, select, start...</summary>
    /// <example>down</example>
    public string Input { get; set; } = string.Empty;
    /// <example>1</example>
    public int Count { get; set; } = 1;
    /// <example>70</example>
    public int HoldMs { get; set; } = 70;
    /// <example>90</example>
    public int GapMs { get; set; } = 90;
}

public class EsControllerComboRequest
{
    /// <summary>Ordered inputs pressed one after the other.</summary>
    /// <example>["down","down","a"]</example>
    public List<string> Inputs { get; set; } = new();
    /// <example>70</example>
    public int HoldMs { get; set; } = 70;
    /// <example>90</example>
    public int GapMs { get; set; } = 90;
}

public class EsControllerGotoSystemRequest
{
    /// <summary>Target system id as known by EmulationStation.</summary>
    /// <example>snes</example>
    public string System { get; set; } = string.Empty;
    /// <summary>Also enter the system's game list after reaching it.</summary>
    /// <example>false</example>
    public bool Enter { get; set; }
    /// <example>70</example>
    public int HoldMs { get; set; } = 70;
    /// <example>90</example>
    public int GapMs { get; set; } = 90;
    /// <example>2500</example>
    public int VerifyTimeoutMs { get; set; } = 2500;
}

public class EsControllerGotoGameRequest
{
    /// <example>snes</example>
    public string System { get; set; } = string.Empty;
    /// <summary>Gamelist-relative ROM path; leave empty to match by name.</summary>
    /// <example>./Super Mario World (USA).zip</example>
    public string GamePath { get; set; } = string.Empty;
    /// <summary>Display name used when GamePath is empty.</summary>
    /// <example>Super Mario World</example>
    public string GameName { get; set; } = string.Empty;
    /// <example>70</example>
    public int HoldMs { get; set; } = 70;
    /// <example>90</example>
    public int GapMs { get; set; } = 90;
    /// <example>3500</example>
    public int VerifyTimeoutMs { get; set; } = 3500;
}

public class EsControllerRestoreSelectionRequest
{
    public string System { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public int HoldMs { get; set; } = 70;
    public int GapMs { get; set; } = 90;
    public int VerifyTimeoutMs { get; set; } = 3500;
}

public class EsControllerReloadGamesRequest
{
    public int DebounceMs { get; set; } = 0;
    public bool BypassLastGameSelectedGuard { get; set; } = true;
    public bool DryRun { get; set; }
}

public class EsControllerReloadGamesResult
{
    public bool Requested { get; set; }
    public bool DryRun { get; set; }
    public int DebounceMs { get; set; }
    public bool BypassLastGameSelectedGuard { get; set; }
    public bool RestoreSelectionAfterReloadGames { get; set; }
    public bool FrontendReloadRequested { get; set; }
    public bool RestoreAttempted { get; set; }
    public bool Restored { get; set; }
    public string RestoreReason { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class EsControllerProbeViewRequest
{
    public int HoldMs { get; set; } = 70;
    public int GapMs { get; set; } = 180;
    public int ObserveDelayMs { get; set; } = 800;
}

public class EsControllerRightClickRequest
{
    public bool Warn { get; set; } = true;
    public int ObserveDelayMs { get; set; } = 450;
}

public class EsControllerProbeViewResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DetectedView { get; set; } = string.Empty;
    public string GameNavigationAxis { get; set; } = string.Empty;
    public string GameForwardInput { get; set; } = string.Empty;
    public string GameBackwardInput { get; set; } = string.Empty;
    public string SystemNavigationAxis { get; set; } = string.Empty;
    public string SystemForwardInput { get; set; } = string.Empty;
    public string SystemBackwardInput { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<string> InputsSent { get; set; } = new();
    public List<EsControllerProbeStep> Steps { get; set; } = new();
}

public class EsControllerProbeStep
{
    public string Name { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public int ObservationMs { get; set; }
    public bool SelectionChanged { get; set; }
    public bool EventsIniChanged { get; set; }
    public bool ObservationTimedOut { get; set; }
    public EsControllerProbeSnapshot Before { get; set; } = new();
    public EsControllerProbeSnapshot After { get; set; } = new();
}

public class EsControllerProbeSnapshot
{
    public string View { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
}

public class EsControllerActionResult
{
    public bool Success { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public string TargetGame { get; set; } = string.Empty;
    public string PreviousSystem { get; set; } = string.Empty;
    public string PreviousGame { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public bool DryRun { get; set; }
    public List<string> InputsSent { get; set; } = new();
}

public class EsControllerConfigAuditResult
{
    public bool Enabled { get; set; }
    public string Backend { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public bool EsInputExists { get; set; }
    public string EsInputPath { get; set; } = string.Empty;
    public EsControllerBackendStatus BackendStatus { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class EsControllerConfigRepairRequest
{
    public bool DryRun { get; set; } = true;
}
