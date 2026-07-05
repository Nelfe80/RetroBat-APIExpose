using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/retroachievements")]
public sealed class RetroAchievementsController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private readonly RetroAchievementsService _service;

    public RetroAchievementsController(RetroAchievementsService service)
    {
        _service = service;
    }

    /// <summary>
    /// Returns the RetroAchievements module status and current session snapshot.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(RetroAchievementsStatusSnapshot), StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        return Ok(_service.GetStatus());
    }

    /// <summary>
    /// Returns the current RetroAchievements session state known by APIExpose.
    /// </summary>
    [HttpGet("session")]
    [ProducesResponseType(typeof(RetroAchievementsSessionSnapshot), StatusCodes.Status200OK)]
    public IActionResult GetSession()
    {
        return Ok(_service.GetSession());
    }

    /// <summary>
    /// Returns cached/enriched RetroAchievements data for the current game when a RA game id is known.
    /// </summary>
    [HttpGet("cache/current-game")]
    [ProducesResponseType(typeof(RetroAchievementsCacheGameSnapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentGameCache(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetCurrentGameCacheAsync(cancellationToken));
    }

    /// <summary>
    /// Clears the persistent RetroAchievements API/media cache.
    /// </summary>
    [HttpPost("cache/clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearCache(CancellationToken cancellationToken)
    {
        await _service.ClearCacheAsync(cancellationToken);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Serves a local RetroAchievements media file from APIExpose cache.
    /// </summary>
    [HttpGet("media/{category}/{fileName}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetCachedMedia(string category, string fileName)
    {
        if (!_service.TryResolveCachedMedia(category, fileName, out var path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        if (!ContentTypeProvider.TryGetContentType(path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(path, contentType);
    }
}

[ApiController]
[Route("dorequest.php")]
public sealed class RetroAchievementsProxyController : ControllerBase
{
    private readonly RetroAchievementsService _service;

    public RetroAchievementsProxyController(RetroAchievementsService service)
    {
        _service = service;
    }

    /// <summary>
    /// Transparent RetroAchievements proxy endpoint for RetroArch cheevos_custom_host.
    /// </summary>
    [HttpGet]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task Proxy(CancellationToken cancellationToken)
    {
        await _service.ProxyAsync(HttpContext, cancellationToken);
    }
}
