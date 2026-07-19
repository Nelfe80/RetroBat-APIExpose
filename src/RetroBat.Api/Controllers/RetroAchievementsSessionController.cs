using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Identifiants RetroAchievements de SESSION (poussés par le hub au check-in
/// d'un joueur, retirés au checkout) : les succès et hiscores RA partent sur
/// le compte du joueur pendant qu'il joue sur la borne, puis la configuration
/// d'origine est restaurée.
/// </summary>
[ApiController]
[Tags("RetroAchievements")]
[Route("api/v1/retroachievements/session")]
public sealed class RetroAchievementsSessionController : ControllerBase
{
    private readonly CheevosSessionService _session;

    public RetroAchievementsSessionController(CheevosSessionService session)
    {
        _session = session;
    }

    /// <summary>Session active ? (identifiant masqué).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CheevosSessionService.SessionState), StatusCodes.Status200OK)]
    public ActionResult<CheevosSessionService.SessionState> Get() => Ok(_session.GetState());

    /// <summary>Pose les identifiants du joueur pour la durée de sa session.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Apply([FromBody] CheevosSessionRequest request)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        if (username.Length is < 2 or > 64 || password.Length is < 1 or > 128)
        {
            return BadRequest(new { error = "username et password requis." });
        }

        try
        {
            _session.Apply(username, password);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "retroarch.cfg introuvable sur cette borne." });
        }

        return Ok(_session.GetState());
    }

    /// <summary>Restaure la configuration RetroAchievements d'origine.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Clear()
    {
        _session.Clear();
        return Ok(_session.GetState());
    }
}

public sealed record CheevosSessionRequest(string? Username, string? Password);
