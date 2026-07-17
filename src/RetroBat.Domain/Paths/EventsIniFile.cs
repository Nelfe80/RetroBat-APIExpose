using System.Collections.Generic;
using System.IO;

namespace RetroBat.Domain.Paths;

/// <summary>
/// Shared reader for events.ini. The ES hooks write the file at cursor speed;
/// a reader holding the default FileShare.Read blocks those writes and the
/// selection is silently lost. Every read must go through this tolerant open
/// (writers and the atomic rename replace stay allowed while we read); torn
/// content is handled by the watcher's snapshot/completeness guards.
/// </summary>
public static class EventsIniFile
{
    public static string[] ReadAllLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }
}
