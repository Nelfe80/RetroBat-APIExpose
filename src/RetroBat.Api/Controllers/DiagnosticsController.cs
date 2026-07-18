using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Read access to the APIExpose observability logs (.log/*.jsonl): what the
/// engine decided and why, without opening files on disk.
/// </summary>
[ApiController]
[Tags("Internal & Prototype")]
[Route("api/v1/diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    private static readonly Regex LogNameRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private static string LogRoot => Path.Combine(RetroBatPaths.PluginRoot, ".log");

    /// <summary>Lists the available jsonl diagnostic logs.</summary>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(IReadOnlyList<DiagnosticLogDescriptor>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DiagnosticLogDescriptor>> GetLogs()
    {
        if (!Directory.Exists(LogRoot))
        {
            return Ok(Array.Empty<DiagnosticLogDescriptor>());
        }

        var logs = Directory.EnumerateFiles(LogRoot, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new DiagnosticLogDescriptor(
                Path.GetFileNameWithoutExtension(info.Name),
                info.Length,
                info.LastWriteTimeUtc))
            .ToArray();
        return Ok(logs);
    }

    /// <summary>
    /// Returns the most recent entries of one diagnostic log (newest last).
    /// </summary>
    /// <response code="200">Parsed entries; lines that are not valid JSON are returned as raw strings.</response>
    /// <response code="404">Unknown log name.</response>
    [HttpGet("logs/{name}")]
    [ProducesResponseType(typeof(IReadOnlyList<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetLog(string name, [FromQuery] int recent = 50)
    {
        if (!LogNameRegex.IsMatch(name ?? string.Empty))
        {
            return NotFound(new { message = "Unknown log name." });
        }

        var path = Path.Combine(LogRoot, name + ".jsonl");
        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { message = "Unknown log name.", available = "GET /api/v1/diagnostics/logs" });
        }

        var clampedCount = Math.Clamp(recent, 1, 1000);
        var lines = new LinkedList<string>();
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lines.AddLast(line);
            if (lines.Count > clampedCount)
            {
                lines.RemoveFirst();
            }
        }

        var entries = new List<object>(lines.Count);
        foreach (var line in lines)
        {
            try
            {
                entries.Add(JsonSerializer.Deserialize<JsonElement>(line));
            }
            catch (JsonException)
            {
                entries.Add(line);
            }
        }

        return Ok(entries);
    }
}

public sealed record DiagnosticLogDescriptor(string Name, long SizeBytes, DateTime LastWriteUtc);
