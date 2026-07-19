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
        var provider = _providers.OfType<RetroArchWrapperProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "RetroArch wrapper provider not registered" });
        }

        // Cible explicite OU contexte courant : dans les deux cas la MÊME
        // résolution que le runtime (alias.json, nom normalisé, repli arcade)
        // — un chemin naïf <system>/<rom>.MEM ne trouve jamais les fichiers
        // édités.
        var snapshot = !string.IsNullOrWhiteSpace(system) && !string.IsNullOrWhiteSpace(rom)
            ? provider.ResolveDefinitionFor(rom.Trim(), system.Trim())
            : provider.GetDefinitionSnapshot();
        var systemId = snapshot.SystemId;
        var romId = snapshot.Rom;
        var definitionFile = snapshot.DefinitionFile;

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

    // Marche Lua superficielle — MÊMES règles que MarqueeManagerSetup /
    // LedManagerSetup (MemSignalCatalog, héritées du MemSignalsParser de
    // RetroCreator) : tables nommées = segments de famille (équilibre
    // d'accolades), seuls les signaux de la section top-level `events`
    // comptent, entrées no_log/no_survey mortes, IGNORE/UNKNOWN = bruit,
    // action= ET les valeurs d'action_map sont des signaux.
    private static readonly Regex MemTableOpenRegex = new(@"^\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*\{", RegexOptions.Compiled);
    private static readonly Regex MemActionRegex = new(@"action\s*=\s*""([A-Z0-9_]+)""", RegexOptions.Compiled);
    private static readonly Regex MemActionMapRegex = new(@"action_map\s*=\s*\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex MemMapValueRegex = new(@"=\s*""([A-Z0-9_]+)""", RegexOptions.Compiled);
    private static readonly Regex MemDescRegex = new(@"desc\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex MemAddressRegex = new(@"address\s*=\s*(0[xX][0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex MemTypeRegex = new(@"type\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex MemConditionRegex = new(@"condition\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex MemScoreKindRegex = new(@"score_kind\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    private static bool IsDeadMemEntry(string line)
        => line.Contains("no_log=true", StringComparison.OrdinalIgnoreCase)
           || line.Contains("no_log = true", StringComparison.OrdinalIgnoreCase)
           || line.Contains("no_survey=true", StringComparison.OrdinalIgnoreCase)
           || line.Contains("no_survey = true", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RetroArchDefinitionSignal> ParseMemSignals(string content)
    {
        var signals = new List<RetroArchDefinitionSignal>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var familyStack = new Stack<(string Name, int Depth)>();
        var depth = 0;
        var inEvents = false;
        var eventsDepth = 0;

        void Add(string action, string line)
        {
            if (action is "IGNORE" or "UNKNOWN" || !seen.Add(action))
            {
                return;
            }

            var path = familyStack.Reverse().Select(f => f.Name).ToArray();
            signals.Add(new RetroArchDefinitionSignal
            {
                Family = path.Length > 0 ? path[0] : string.Empty,
                Resource = path.Length > 1 ? string.Join('.', path.Skip(1)) : string.Empty,
                Action = action,
                Address = MemAddressRegex.Match(line) is { Success: true } a ? a.Groups[1].Value : string.Empty,
                Type = MemTypeRegex.Match(line) is { Success: true } t ? t.Groups[1].Value : string.Empty,
                Condition = MemConditionRegex.Match(line) is { Success: true } c ? c.Groups[1].Value : string.Empty,
                ScoreKind = MemScoreKindRegex.Match(line) is { Success: true } k ? k.Groups[1].Value : null,
                Description = MemDescRegex.Match(line) is { Success: true } d ? d.Groups[1].Value : null
            });
        }

        foreach (var line in content.Split('\n'))
        {
            var open = MemTableOpenRegex.Match(line);
            if (open.Success)
            {
                var name = open.Groups[1].Value;
                // Seul le `events` TOP-LEVEL ouvre la section : certains jeux
                // imbriquent une famille nommée events (flow.events) — elle
                // doit s'empiler comme famille, pas réinitialiser la section.
                if (name.Equals("events", StringComparison.OrdinalIgnoreCase) && !inEvents)
                {
                    inEvents = true;
                    eventsDepth = depth;
                }
                else if (inEvents)
                {
                    familyStack.Push((name, depth));
                }
            }

            if (inEvents && !IsDeadMemEntry(line))
            {
                foreach (Match m in MemActionRegex.Matches(line))
                {
                    Add(m.Groups[1].Value, line);
                }

                foreach (Match map in MemActionMapRegex.Matches(line))
                {
                    foreach (Match v in MemMapValueRegex.Matches(map.Groups[1].Value))
                    {
                        Add(v.Groups[1].Value, line);
                    }
                }
            }

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
            while (familyStack.Count > 0 && depth <= familyStack.Peek().Depth)
            {
                familyStack.Pop();
            }

            if (inEvents && depth <= eventsDepth)
            {
                inEvents = false;
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
