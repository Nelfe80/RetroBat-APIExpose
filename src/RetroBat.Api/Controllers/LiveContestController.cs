using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Inscription Live Contest : la page de participation (plateforme) remet le
/// playToken a l'APIExpose local — c'est le SEUL point d'entree navigateur.
/// Toute l'orchestration (lancement, pause, scores, fermeture) est ensuite
/// assuree par <see cref="LiveContestClientService"/> en local.
/// </summary>
[ApiController]
[Route("api/v1/livecontest")]
public class LiveContestController : ControllerBase
{
    private static readonly string[] AllowedPlatformHosts =
    [
        "nelfetech.com",
        "www.nelfetech.com"
    ];

    private readonly LiveContestClientService _client;

    public LiveContestController(LiveContestClientService client)
    {
        _client = client;
    }

    /// <summary>Enrolls this PC into a Live Contest with the viewer's play token.</summary>
    /// <remarks>
    /// Example:
    ///
    ///     POST /api/v1/livecontest/enroll
    ///     {
    ///       "playToken": "xxxxx",
    ///       "platform": "https://www.nelfetech.com/retrocreator"
    ///     }
    /// </remarks>
    /// <response code="200">Enrolled; the local service now drives the contest.</response>
    /// <response code="400">Missing token or unsupported platform URL.</response>
    [HttpPost("enroll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Enroll([FromBody] EnrollPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.PlayToken) || payload.PlayToken.Trim().Length < 16)
        {
            return BadRequest(new { message = "You must provide the playToken." });
        }

        // seule la plateforme officielle peut etre orchestree : pas question
        // de laisser un site arbitraire faire lancer des jeux a ce PC
        if (!Uri.TryCreate(payload.Platform, UriKind.Absolute, out var platform) ||
            platform.Scheme != Uri.UriSchemeHttps ||
            !AllowedPlatformHosts.Contains(platform.Host, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Unsupported platform URL." });
        }

        _client.Enroll(payload.PlayToken, platform.ToString());
        return Ok(new { status = "enrolled" });
    }

    /// <summary>Current Live Contest client state (phase, value, readiness).</summary>
    /// <response code="200">State returned.</response>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus() => Ok(_client.Status());

    /// <summary>Withdraws from the current Live Contest.</summary>
    /// <response code="200">Enrollment cleared.</response>
    [HttpDelete("enroll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Cancel()
    {
        _client.Withdraw();
        return Ok(new { status = "withdrawn" });
    }
}

public class EnrollPayload
{
    /// <summary>Play token issued by the platform after the viewer confirms.</summary>
    public string PlayToken { get; set; } = string.Empty;

    /// <summary>Platform base URL (must be the official Live Contest platform).</summary>
    /// <example>https://www.nelfetech.com/retrocreator</example>
    public string Platform { get; set; } = string.Empty;
}
