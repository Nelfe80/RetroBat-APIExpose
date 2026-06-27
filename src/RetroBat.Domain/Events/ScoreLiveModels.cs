namespace RetroBat.Domain.Events;

/// <summary>
/// Normalized live score event projected from RAM, Lua or output sources.
/// </summary>
public sealed class ScoreLiveEvent
{
    public string Source { get; set; } = string.Empty;
    public string ScoreKind { get; set; } = "game";
    public string SourceKey { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public int Player { get; set; } = 1;
    public long Score { get; set; }
    public long? RawValue { get; set; }
    public bool Composed { get; set; }
    public string Confidence { get; set; } = "medium";
    public string DefinitionFile { get; set; } = string.Empty;
    public List<ScoreLivePart> Parts { get; set; } = new();
    public DateTime Ts { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Normalized live timer event projected from RAM, RA progress or output sources.
/// </summary>
public sealed class TimerLiveEvent
{
    public string Source { get; set; } = string.Empty;
    public string TimerKind { get; set; } = "game";
    public string TimerRole { get; set; } = "unknown";
    public string SourceKey { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public int? Player { get; set; }
    public long Value { get; set; }
    public long? RawValue { get; set; }
    public long? MaxValue { get; set; }
    public long? Remaining { get; set; }
    public double? Progress01 { get; set; }
    public double? Urgency01 { get; set; }
    public string Direction { get; set; } = "unknown";
    public string Unit { get; set; } = "unknown";
    public string Confidence { get; set; } = "medium";
    public string DefinitionFile { get; set; } = string.Empty;
    public List<TimerLivePart> Parts { get; set; } = new();
    public DateTime Ts { get; set; } = DateTime.UtcNow;
}

public sealed class TimerLivePart
{
    public string Key { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string RawValueHex { get; set; } = string.Empty;
    public string Encoding { get; set; } = string.Empty;
    public long Value { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class ScoreLivePart
{
    public string Key { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string RawValueHex { get; set; } = string.Empty;
    public string Encoding { get; set; } = string.Empty;
    public long Value { get; set; }
    public long Weight { get; set; } = 1;
    public long Contribution { get; set; }
    public string Description { get; set; } = string.Empty;
}
