namespace RetroBat.Domain.Models;

public class LocalizedTextRecord
{
    public string Language { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool WasWritten { get; set; }
}

public class LocalizedTextCandidate
{
    public string Language { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
}

public class LocalizedTextBundle
{
    public string Language { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedAtUtc { get; set; }
}
