namespace RetroBat.Domain.Models;

public class MediaAliasIndex
{
    public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
