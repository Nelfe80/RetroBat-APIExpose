namespace RetroBat.Domain.Events;

/// <summary>
/// Generic event envelope published by the internal event bus and broadcast over WebSocket and API UDP.
/// </summary>
public class EventEnvelope
{
    /// <example>retroarch.action</example>
    public string Type { get; set; } = string.Empty;
    /// <example>2026-04-28T21:00:00.123Z</example>
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    /// <example>cab-01</example>
    public string NodeId { get; set; } = "cab-01";
    /// <example>7ddf69a8-6cc1-4828-9148-a4dcf36e01e1</example>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public object? Payload { get; set; }
}

/// <summary>
/// Snapshot of the latest MAME output state known by the API.
/// </summary>
public class MameOutputEvent
{
    /// <example>mame.network</example>
    public string Source { get; set; } = "mame.network";
    /// <example>8000</example>
    public int Port { get; set; } = 8000;
    /// <example>chasehq</example>
    public string MachineName { get; set; } = string.Empty;
    public List<MameSignal> Signals { get; set; } = new();
}

/// <summary>
/// Single MAME output signal entry.
/// </summary>
public class MameSignal
{
    /// <example>genout5</example>
    public string Key { get; set; } = string.Empty;
    /// <example>1</example>
    public int Value { get; set; }
    /// <example>2026-04-28T21:00:00.123Z</example>
    public DateTime Ts { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Resolved RetroArch wrapper RAM definition for the current context.
/// </summary>
public class RetroArchDefinitionSnapshot
{
    /// <example>resources.ram</example>
    public string Source { get; set; } = "resources.ram";
    /// <example>megadrive</example>
    public string SystemId { get; set; } = string.Empty;
    /// <example>sonic-the-hedgehog</example>
    public string Rom { get; set; } = string.Empty;
    /// <example>resources/ram/megadrive/sonic-the-hedgehog.MEM</example>
    public string DefinitionFile { get; set; } = string.Empty;
    /// <example>resources/ram/megadrive/alias.json</example>
    public string AliasFile { get; set; } = string.Empty;
    /// <example>true</example>
    public bool AliasMatched { get; set; }
    /// <example>true</example>
    public bool DefinitionExists { get; set; }
}

/// <summary>
/// Current runtime snapshot ingested from the RetroArch wrapper named pipe.
/// </summary>
public class RetroArchRuntimeSnapshot
{
    /// <example>retroarch.wrapper.pipe</example>
    public string Source { get; set; } = "retroarch.wrapper.pipe";
    /// <example>\\\\.\\pipe\\RetroBatArcadePipe</example>
    public string Pipe { get; set; } = @"\\.\pipe\RetroBatArcadePipe";
    /// <example>true</example>
    public bool Connected { get; set; }
    /// <example>megadrive</example>
    public string SystemId { get; set; } = string.Empty;
    /// <example>sonic-the-hedgehog</example>
    public string Rom { get; set; } = string.Empty;
    /// <example>resources/ram/megadrive/sonic-the-hedgehog.MEM</example>
    public string DefinitionFile { get; set; } = string.Empty;
    /// <example>2026-04-28T21:00:00.123Z</example>
    public DateTime? LastMessageAt { get; set; }
    /// <example>[21:00:00.123] [ADDR:0xFFF00A] [VAL:0xB5] [UDP_OUT] TYPE:SCORE :COIN_GAIN | SOURCE:Active sound effect (Ring collected) | VALUE:181 | RATE:21</example>
    public string LastRawMessage { get; set; } = string.Empty;
    public List<RetroArchRuntimeSignal> Signals { get; set; } = new();
}

/// <summary>
/// Single parsed runtime signal emitted by the RetroArch wrapper.
/// </summary>
public class RetroArchRuntimeSignal
{
    /// <example>SCORE.COIN_GAIN</example>
    public string Key { get; set; } = string.Empty;
    /// <example>SCORE</example>
    public string Channel { get; set; } = string.Empty;
    /// <example>COIN_GAIN</example>
    public string Name { get; set; } = string.Empty;
    /// <example>Active sound effect (Ring collected)</example>
    public string SourceDescription { get; set; } = string.Empty;
    /// <example>0xFFF00A</example>
    public string Address { get; set; } = string.Empty;
    /// <example>0xB5</example>
    public string RawValueHex { get; set; } = string.Empty;
    /// <example>181</example>
    public int? Value { get; set; }
    /// <example>21</example>
    public int? Rate { get; set; }
    public string RawLine { get; set; } = string.Empty;
    /// <example>2026-04-28T21:00:00.123Z</example>
    public DateTime Ts { get; set; } = DateTime.UtcNow;
}
