using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Game Events")]
[Route("api/v1/[controller]")]
public class HiscoresController : ControllerBase
{
    private readonly ILogger<HiscoresController> _logger;
    private readonly ApiContext _context;
    private readonly IHiscoreService _hiscoreService;

    public HiscoresController(ILogger<HiscoresController> logger, ApiContext context, IHiscoreService hiscoreService)
    {
        _logger = logger;
        _context = context;
        _hiscoreService = hiscoreService;
    }

    /// <summary>
    /// Extracts the hiscore table of the current game (no parameters) or of a
    /// target identified by MAME rom ids or file md5.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetHiscore([FromQuery] string? ids, [FromQuery] string? md5)
    {
        GameReference? targetGame = null;
        if (string.IsNullOrEmpty(ids) && string.IsNullOrEmpty(md5))
        {
            targetGame = _context.Ui.Running ?? _context.Ui.Selected;
            if (targetGame == null)
            {
                return BadRequest(new { message = "You must provide an 'ids' or 'md5' parameter, or have a running/selected game." });
            }
        }
        else
        {
            var current = _context.Ui.Running ?? _context.Ui.Selected;
            if (current != null && (current.GameId == ids || current.GameId == md5 || current.Details?.Md5 == md5 || current.Details?.Md5 == ids))
            {
                targetGame = current;
            }

            if (targetGame == null)
            {
                return NotFound(new { message = "Fetching an arbitrary hiscore not matching the current game is currently pending external search functionality." });
            }
        }

        _logger.LogInformation("[Hiscores] Requested hiscore for target game {GameName}", targetGame.GameName);
        var result = await _hiscoreService.ExtractAsync(targetGame, HttpContext.RequestAborted);

        if (result.Status == "error")
        {
            return StatusCode(500, result);
        }

        return Ok(result);
    }
}
