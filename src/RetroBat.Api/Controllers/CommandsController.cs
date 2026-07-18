using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Context & Navigation")]
[Route("api/v1/[controller]")]
public class CommandsController : ControllerBase
{
    private static readonly Uri EmulationStationBaseUri = new("http://127.0.0.1:1234");
    private const string RetroArchHost = "127.0.0.1";
    private const int RetroArchPort = 55355;
    private readonly ApiContext _context;

    public CommandsController(ApiContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Launches a game through EmulationStation's local HTTP API.
    /// </summary>
    /// <remarks>
    /// The API resolves the ROM path from <c>romPath</c> directly, or from the current selected/running game when <c>gameId</c> matches.
    ///
    /// Example:
    ///
    ///     POST /api/v1/commands/launch
    ///     {
    ///       "romPath": "E:\\RetroBat\\roms\\mame\\llander.zip"
    ///     }
    /// </remarks>
    /// <param name="payload">Launch request using either a ROM path or the current game id.</param>
    /// <response code="202">Launch request accepted and forwarded to EmulationStation.</response>
    /// <response code="400">Missing or invalid payload.</response>
    /// <response code="404">ROM file not found.</response>
    /// <response code="502">EmulationStation launch API could not be reached.</response>
    [HttpPost("launch")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> LaunchGame([FromBody] LaunchPayload payload)
    {
        var romPath = ResolveRomPath(payload);
        if (string.IsNullOrWhiteSpace(romPath))
        {
            return BadRequest(new
            {
                message = "You must provide a romPath, or a gameId matching the current selected/running game."
            });
        }

        if (!Path.IsPathFullyQualified(romPath))
        {
            romPath = Path.GetFullPath(Path.Combine(RetroBatPaths.RetroBatRoot, romPath));
        }

        if (!System.IO.File.Exists(romPath))
        {
            return NotFound(new
            {
                message = "ROM file not found.",
                romPath
            });
        }

        using var client = new HttpClient { BaseAddress = EmulationStationBaseUri };
        using var content = new StringContent(romPath, Encoding.UTF8, "text/plain");
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync("/launch", content, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Failed to reach EmulationStation launch API.",
                error = ex.Message
            });
        }

        var responseBody = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, new
            {
                message = "EmulationStation launch API rejected the request.",
                romPath,
                esStatusCode = (int)response.StatusCode,
                esResponse = responseBody
            });
        }

        return Accepted(new
        {
            status = "launching",
            gameId = payload.GameId,
            romPath,
            esStatusCode = (int)response.StatusCode,
            esResponse = responseBody
        });
    }

    /// <summary>
    /// Sends a UDP command to a running RetroArch instance.
    /// </summary>
    /// <remarks>
    /// RetroArch network commands must be enabled in <c>retroarch.cfg</c>.
    ///
    /// Example:
    ///
    ///     POST /api/v1/commands/retroarch/command
    ///     {
    ///       "command": "GET_STATUS"
    ///     }
    /// </remarks>
    /// <param name="payload">RetroArch command payload.</param>
    /// <response code="200">Command sent and response received or timeout reported.</response>
    /// <response code="202">Command sent without waiting for a response.</response>
    /// <response code="400">Missing command.</response>
    /// <response code="502">RetroArch UDP command failed.</response>
    [HttpPost("retroarch/command")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SendRetroArchCommand([FromBody] RetroArchCommandPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Command))
        {
            return BadRequest(new { message = "You must provide a RetroArch command." });
        }

        var host = string.IsNullOrWhiteSpace(payload.Host) ? RetroArchHost : payload.Host.Trim();
        var port = payload.Port > 0 ? payload.Port : RetroArchPort;
        var commandText = payload.Command.Trim();

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = Math.Max(1, payload.TimeoutMs);

        var bytes = Encoding.UTF8.GetBytes(commandText);

        try
        {
            await udp.SendAsync(bytes, bytes.Length, host, port);

            if (!payload.ExpectResponse)
            {
                return Accepted(new
                {
                    status = "sent",
                    command = commandText,
                    host,
                    port
                });
            }

            var receiveTask = udp.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(payload.TimeoutMs, HttpContext.RequestAborted));
            if (completed != receiveTask)
            {
                return Ok(new
                {
                    status = "sent",
                    command = commandText,
                    host,
                    port,
                    response = string.Empty,
                    timedOut = true
                });
            }

            var result = await receiveTask;
            return Ok(new
            {
                status = "sent",
                command = commandText,
                host,
                port,
                response = Encoding.UTF8.GetString(result.Buffer),
                timedOut = false
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Failed to send RetroArch UDP command.",
                command = commandText,
                host,
                port,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Requests the current RetroArch execution status through the network command interface.
    /// </summary>
    /// <response code="200">Status returned by RetroArch or a timeout result.</response>
    /// <response code="502">RetroArch UDP command failed.</response>
    [HttpGet("retroarch/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> GetRetroArchStatus()
    {
        return SendRetroArchCommand(new RetroArchCommandPayload
        {
            Command = "GET_STATUS",
            ExpectResponse = true
        });
    }

    private string ResolveRomPath(LaunchPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RomPath))
        {
            return payload.RomPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(payload.GameId))
        {
            return string.Empty;
        }

        var candidates = new[]
        {
            _context.Ui.Running,
            _context.Ui.Selected
        };

        var match = candidates.FirstOrDefault(game =>
            game != null &&
            string.Equals(game.GameId, payload.GameId, StringComparison.OrdinalIgnoreCase));

        return match?.GamePath ?? string.Empty;
    }
}

public class LaunchPayload
{
    /// <summary>
    /// Current selected or running game id, used only if it matches the API context.
    /// </summary>
    /// <example>e143c3705d0bc727f9daa65448cad68c</example>
    public string GameId { get; set; } = string.Empty;

    /// <summary>
    /// Absolute or RetroBat-relative ROM path to launch.
    /// </summary>
    /// <example>E:\RetroBat\roms\mame\llander.zip</example>
    public string RomPath { get; set; } = string.Empty;

    /// <summary>
    /// Reserved for future launch options.
    /// </summary>
    /// <example></example>
    public string Options { get; set; } = string.Empty;
}

public class RetroArchCommandPayload
{
    public const int DefaultPort = 55355;

    /// <summary>
    /// RetroArch network command to send.
    /// </summary>
    /// <example>GET_STATUS</example>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// RetroArch host. Defaults to 127.0.0.1.
    /// </summary>
    /// <example>127.0.0.1</example>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// RetroArch UDP command port. Defaults to 55355.
    /// </summary>
    /// <example>55355</example>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Whether the API should wait for a UDP response.
    /// </summary>
    /// <example>true</example>
    public bool ExpectResponse { get; set; } = true;

    /// <summary>
    /// Response timeout in milliseconds when waiting for RetroArch.
    /// </summary>
    /// <example>1000</example>
    public int TimeoutMs { get; set; } = 1000;
}
