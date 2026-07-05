namespace RetroBat.Domain.Models;

public class HiscoreEntry
{
    public string Rank { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class HiscoreExtractionResult
{
    public string QueryId { get; set; } = string.Empty;
    public string QueryMd5 { get; set; } = string.Empty;
    public string RomName { get; set; } = string.Empty;
    public string RomPath { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public string Status { get; set; } = "not_found";
    public string Message { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public List<HiscoreEntry> Scores { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
