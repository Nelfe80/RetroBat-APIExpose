using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using RetroBat.Providers.MameOutputs;
using RetroBat.Providers.RetroArchWrapper;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Game Events")]
[Route("api/v1/[controller]")]
public class OutputsController : ControllerBase
{
    private readonly IEnumerable<IProvider> _providers;

    public OutputsController(IEnumerable<IProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Returns the latest in-memory snapshot of MAME network outputs.
    /// </summary>
    /// <response code="200">Current MAME output snapshot.</response>
    /// <response code="404">MAME outputs provider is not registered.</response>
    [HttpGet("mame")]
    [ProducesResponseType(typeof(RetroBat.Domain.Events.MameOutputEvent), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetMameOutputs()
    {
        var provider = _providers.OfType<MameOutputsProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "MAME outputs provider not registered" });
        }

        return Ok(provider.GetSnapshot());
    }

    /// <summary>
    /// Returns the current RetroArch wrapper runtime snapshot ingested from the local named pipe.
    /// </summary>
    /// <remarks>
    /// This endpoint exposes the consolidated runtime state read from <c>\\.\pipe\RetroBatArcadePipe</c>.
    /// It reflects the latest parsed wrapper signals, the resolved ROM/system context, and the current in-memory signal cache.
    /// </remarks>
    /// <response code="200">Current RetroArch wrapper runtime snapshot.</response>
    /// <response code="404">RetroArch wrapper provider is not registered.</response>
    [HttpGet("retroarch")]
    [ProducesResponseType(typeof(RetroBat.Domain.Events.RetroArchRuntimeSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetRetroArchOutputs()
    {
        var provider = _providers.OfType<RetroArchWrapperProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "RetroArch wrapper provider not registered" });
        }

        return Ok(provider.GetSnapshot());
    }

    /// <summary>
    /// Returns the resolved RetroArch wrapper RAM definition for the current game context.
    /// </summary>
    /// <remarks>
    /// The API resolves the current <c>resources/ram/&lt;system&gt;/&lt;rom&gt;.MEM</c> file and any matching <c>alias.json</c> entry.
    /// This endpoint describes the definition file the wrapper runtime should currently map against.
    /// </remarks>
    /// <response code="200">Resolved RetroArch RAM definition snapshot.</response>
    /// <response code="404">RetroArch wrapper provider is not registered.</response>
    [HttpGet("retroarch/definition")]
    [ProducesResponseType(typeof(RetroBat.Domain.Events.RetroArchDefinitionSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetRetroArchDefinition()
    {
        var provider = _providers.OfType<RetroArchWrapperProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "RetroArch wrapper provider not registered" });
        }

        return Ok(provider.GetDefinitionSnapshot());
    }

    /// <summary>
    /// Returns the RAM definition itself: raw content, server-computed SHA-256
    /// and the parsed signal list. Defaults to the current game context;
    /// pass system and rom to target a specific definition.
    /// </summary>
    /// <remarks>
    /// The SHA-256 is computed server-side over the raw file bytes, so every
    /// consumer (including remote Live Contest viewers) shares the exact same
    /// definition identity without reading the plugin folder from disk.
    /// Signals are a best-effort projection of the mem-curator entries; the
    /// raw <c>content</c> stays the authoritative contract.
    /// </remarks>
    /// <response code="200">Definition content, hash and parsed signals.</response>
    /// <response code="404">No definition resolved for the target context.</response>
    [HttpGet("retroarch/definition/content")]
    [ProducesResponseType(typeof(RetroArchDefinitionContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRetroArchDefinitionContent(
        [FromQuery] string? system = null,
        [FromQuery] string? rom = null,
        CancellationToken cancellationToken = default)
    {
        string systemId;
        string romId;
        string definitionFile;
        if (!string.IsNullOrWhiteSpace(system) && !string.IsNullOrWhiteSpace(rom))
        {
            systemId = system.Trim();
            romId = rom.Trim();
            definitionFile = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, romId + ".MEM");
        }
        else
        {
            var provider = _providers.OfType<RetroArchWrapperProvider>().FirstOrDefault();
            if (provider == null)
            {
                return NotFound(new { message = "RetroArch wrapper provider not registered" });
            }

            var snapshot = provider.GetDefinitionSnapshot();
            systemId = snapshot.SystemId;
            romId = snapshot.Rom;
            definitionFile = snapshot.DefinitionFile;
        }

        if (string.IsNullOrWhiteSpace(definitionFile) || !System.IO.File.Exists(definitionFile))
        {
            return NotFound(new { message = "No RAM definition resolved for the target context.", systemId, rom = romId });
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(definitionFile, cancellationToken);
        var content = Encoding.UTF8.GetString(bytes);
        return Ok(new RetroArchDefinitionContentResponse
        {
            SystemId = systemId,
            Rom = romId,
            DefinitionFile = definitionFile,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            SizeBytes = bytes.Length,
            LastWriteUtc = System.IO.File.GetLastWriteTimeUtc(definitionFile),
            Content = content,
            Signals = ParseMemSignals(content)
        });
    }

    private static readonly Regex MemFamilyRegex = new(@"^\s{4}([\w-]+)\s*=\s*\{", RegexOptions.Compiled);
    private static readonly Regex MemResourceRegex = new(@"^\s{6}([\w-]+)\s*=\s*\{", RegexOptions.Compiled);
    private static readonly Regex MemEntryRegex = new(
        @"\{\s*address\s*=\s*(0[xX][0-9A-Fa-f]+)\s*,\s*type\s*=\s*""([^""]+)""\s*,\s*condition\s*=\s*""([^""]+)""\s*,\s*action\s*=\s*""([^""]+)""(?<tail>[^}]*)\}",
        RegexOptions.Compiled);
    private static readonly Regex MemTailFieldRegex = new(@"([\w_]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    private static IReadOnlyList<RetroArchDefinitionSignal> ParseMemSignals(string content)
    {
        var signals = new List<RetroArchDefinitionSignal>();
        var family = string.Empty;
        var resource = string.Empty;
        foreach (var line in content.Split('\n'))
        {
            var familyMatch = MemFamilyRegex.Match(line);
            if (familyMatch.Success)
            {
                family = familyMatch.Groups[1].Value;
                continue;
            }

            var resourceMatch = MemResourceRegex.Match(line);
            if (resourceMatch.Success)
            {
                resource = resourceMatch.Groups[1].Value;
                continue;
            }

            foreach (Match entry in MemEntryRegex.Matches(line))
            {
                var signal = new RetroArchDefinitionSignal
                {
                    Family = family,
                    Resource = resource,
                    Address = entry.Groups[1].Value,
                    Type = entry.Groups[2].Value,
                    Condition = entry.Groups[3].Value,
                    Action = entry.Groups[4].Value
                };
                foreach (Match field in MemTailFieldRegex.Matches(entry.Groups["tail"].Value))
                {
                    switch (field.Groups[1].Value)
                    {
                        case "score_kind":
                            signal.ScoreKind = field.Groups[2].Value;
                            break;
                        case "desc":
                            signal.Description = field.Groups[2].Value;
                            break;
                    }
                }

                signals.Add(signal);
            }
        }

        return signals;
    }
}

/// <summary>RAM definition content, identity hash and parsed signals.</summary>
public sealed class RetroArchDefinitionContentResponse
{
    /// <example>megadrive</example>
    public string SystemId { get; set; } = string.Empty;
    /// <example>sonic-the-hedgehog</example>
    public string Rom { get; set; } = string.Empty;
    public string DefinitionFile { get; set; } = string.Empty;
    /// <summary>SHA-256 of the raw file bytes (uppercase hex) — the definition identity.</summary>
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    /// <summary>Raw mem-curator Lua content — the authoritative contract.</summary>
    public string Content { get; set; } = string.Empty;
    public IReadOnlyList<RetroArchDefinitionSignal> Signals { get; set; } = [];
}

public sealed class RetroArchDefinitionSignal
{
    /// <example>scoring</example>
    public string Family { get; set; } = string.Empty;
    /// <example>points</example>
    public string Resource { get; set; } = string.Empty;
    /// <example>0XC974</example>
    public string Address { get; set; } = string.Empty;
    /// <example>u16le</example>
    public string Type { get; set; } = string.Empty;
    /// <example>change</example>
    public string Condition { get; set; } = string.Empty;
    /// <example>SCORE_STATE</example>
    public string Action { get; set; } = string.Empty;
    /// <example>game</example>
    public string? ScoreKind { get; set; }
    /// <example>score</example>
    public string? Description { get; set; }
}
