using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

/// <summary>Consolidated UI state (GET Context/state).</summary>
public sealed class ContextStateResponse
{
    /// <example>browsing</example>
    public string State { get; set; } = string.Empty;
    public SystemDetails? SelectedSystem { get; set; }
    public GameReference? SelectedGame { get; set; }
    public GameReference? RunningGame { get; set; }
}

/// <summary>Current game (GET Context/current-game).</summary>
public sealed class CurrentGameResponse
{
    /// <example>game-selected</example>
    public string State { get; set; } = string.Empty;
    /// <example>megadrive</example>
    public string SystemId { get; set; } = string.Empty;
    /// <example>e143c3705d0bc727f9daa65448cad68c</example>
    public string GameId { get; set; } = string.Empty;
    /// <example>./Sonic The Hedgehog (USA, Europe).zip</example>
    public string Path { get; set; } = string.Empty;
    /// <example>Sonic The Hedgehog</example>
    public string Name { get; set; } = string.Empty;
    public LaunchDetails? Launch { get; set; }
    public GameDetails? Details { get; set; }
}

/// <summary>Current system (GET Context/current-system).</summary>
public sealed class CurrentSystemResponse
{
    /// <example>system-selected</example>
    public string State { get; set; } = string.Empty;
    public SystemDetails System { get; set; } = new();
}

/// <summary>Full context snapshot (GET Context).</summary>
public sealed class ContextSnapshotResponse
{
    /// <example>1.0</example>
    public string SchemaVersion { get; set; } = string.Empty;
    public NodeState Node { get; set; } = new();
    public GameState Ui { get; set; } = new();
    public ContextTimeResponse Time { get; set; } = new();
}

public sealed class ContextTimeResponse
{
    public DateTime Utc { get; set; }
}

/// <summary>Liveness payload (GET Health).</summary>
public sealed class HealthResponse
{
    /// <example>healthy</example>
    public string Status { get; set; } = string.Empty;
    /// <example>1.3.5+20260719.020000.abcdef12</example>
    public string Version { get; set; } = string.Empty;
}

/// <summary>Version payload (GET Version).</summary>
public sealed class VersionResponse
{
    /// <example>1.3.5+20260719.020000.abcdef12</example>
    public string Version { get; set; } = string.Empty;
    /// <example>RetroBat Local API</example>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Readiness payload (GET startup/ready — also the 503 body while starting).</summary>
public sealed class StartupReadyResponse
{
    /// <example>ready</example>
    public string Status { get; set; } = string.Empty;
    /// <example>true</example>
    public bool Ready { get; set; }
    public DateTimeOffset? ReadyAtUtc { get; set; }
}
