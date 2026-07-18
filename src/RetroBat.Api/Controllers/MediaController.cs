using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Local Media Manager")]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private static readonly TimeSpan VisibleMediaReallocationReloadDelay = TimeSpan.FromSeconds(4);
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private readonly IMediaStore _mediaStore;
    private readonly MediaMaintenanceService _mediaMaintenanceService;
    private readonly GamelistGenerationService _gamelistGenerationService;
    private readonly LocalGamelistUpdateService _localGamelistUpdateService;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly LocalScrapingPreviewService _localScrapingPreviewService;
    private readonly RemoteScrapingService _remoteScrapingService;
    private readonly MediaRuntimeState _mediaRuntimeState;
    private readonly GameListImpactWarningService _gameListImpactWarningService;

    public MediaController(
        IMediaStore mediaStore,
        MediaMaintenanceService mediaMaintenanceService,
        GamelistGenerationService gamelistGenerationService,
        LocalGamelistUpdateService localGamelistUpdateService,
        GamelistUpdateService gamelistUpdateService,
        LocalScrapingPreviewService localScrapingPreviewService,
        RemoteScrapingService remoteScrapingService,
        MediaRuntimeState mediaRuntimeState,
        GameListImpactWarningService gameListImpactWarningService)
    {
        _mediaStore = mediaStore;
        _mediaMaintenanceService = mediaMaintenanceService;
        _gamelistGenerationService = gamelistGenerationService;
        _localGamelistUpdateService = localGamelistUpdateService;
        _gamelistUpdateService = gamelistUpdateService;
        _localScrapingPreviewService = localScrapingPreviewService;
        _remoteScrapingService = remoteScrapingService;
        _mediaRuntimeState = mediaRuntimeState;
        _gameListImpactWarningService = gameListImpactWarningService;
    }

    /// <summary>
    /// Serves a local canonical media file resolved by APIExpose.
    /// </summary>
    /// <remarks>
    /// This endpoint only serves local media. It does not trigger remote scraping.
    /// </remarks>
    [HttpGet("{*path}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMedia(string path)
    {
        var fileToServe = await _mediaStore.ResolveAsync(path, "marquee");
        if (fileToServe == null || !System.IO.File.Exists(fileToServe))
        {
            return NotFound();
        }

        if (!ContentTypeProvider.TryGetContentType(fileToServe, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(fileToServe, contentType);
    }

    /// <summary>
    /// Disabled legacy remote rescrape endpoint.
    /// </summary>
    /// <remarks>
    /// Remote scraping was archived on 2026-05-09. This endpoint is kept visible in Swagger
    /// as a deprecated compatibility marker and always returns HTTP 400.
    /// Use <c>POST /api/v1/Media/rescrape/local</c> or local gamelist generation instead.
    /// </remarks>
    [Obsolete("Remote scraping is archived. Use local resync or local gamelist generation.")]
    [Tags("Auto Scraping Manager")]
    [HttpPost("rescrape/remote")]
    [ProducesResponseType(typeof(MediaMaintenanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MediaMaintenanceResponse>> ForceRemoteRescrape([FromBody] MediaScrapeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediaMaintenanceService.ForceRemoteRescrapeAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
    }

    /// <summary>
    /// Rebuilds local media projection for one game from the canonical media store.
    /// </summary>
    /// <remarks>
    /// This clears projected ES media for the target game, then regenerates projection and gamelist
    /// data from already available local canonical media. It does not queue remote scraping.
    /// </remarks>
    [Tags("Auto Scraping Manager")]
    [HttpPost("rescrape/local")]
    [ProducesResponseType(typeof(MediaMaintenanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MediaMaintenanceResponse>> ForceLocalResync([FromBody] MediaScrapeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediaMaintenanceService.ForceLocalResyncAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
    }

    /// <summary>
    /// Reports the current scraping capability of this build.
    /// </summary>
    /// <remarks>
    /// The current baseline uses a new provider-based remote scraper contract. Local projection
    /// always runs first. ES game ids are generated locally from the official
    /// <c>MD5(path)</c> formula. Live refreshes use <c>/addgames</c> only; ratings are
    /// normalized to the EmulationStation ratio format, for example <c>0.4</c> for 2/5 stars.
    /// </remarks>
    [Tags("Auto Scraping Manager")]
    [HttpGet("scraping/status")]
    [ProducesResponseType(typeof(ScrapingStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<ScrapingStatusResponse> GetScrapingStatus()
    {
        return Ok(new ScrapingStatusResponse
        {
            RemoteScrapingEnabled = _remoteScrapingService.GetStatus().AutoScrapingEnabled,
            LegacyScrapingArchived = true,
            LegacyArchivePath = ".archive/scraping-legacy-20260509",
            LocalProjectionEnabled = true,
            EsGameIdStrategy = "generated-locally",
            EsGameIdFormula = "MD5(absolute ES path using forward slashes)",
            Message = "Legacy remote scraping is archived. The new auto scraping workflow is local-first and provider-based. Live refresh uses /addgames with rating normalized as 0.0.",
            Remote = _remoteScrapingService.GetStatus()
        });
    }

    /// <summary>
    /// Previews the new local scraping engine decisions without writing files.
    /// </summary>
    /// <remarks>
    /// This dry-run scans <c>media/</c> and <c>media/user/</c>, resolves exact and inherited
    /// local media candidates, and returns the volatile ES targets that would be produced later.
    /// It does not copy files, update gamelists or call a remote scraper.
    /// </remarks>
    [HttpPost("local-scrape/preview")]
    [ProducesResponseType(typeof(LocalScrapePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LocalScrapePreviewResponse>> PreviewLocalScrape([FromBody] LocalScrapePreviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _localScrapingPreviewService.PreviewAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
    }

    /// <summary>
    /// Generates gamelist media entries from local canonical media.
    /// </summary>
    /// <remarks>
    /// Accepted scopes are <c>game</c>, <c>system</c> and <c>all</c>. This endpoint does not
    /// download remote media.
    /// </remarks>
    [HttpPost("gamelist/generate")]
    [ProducesResponseType(typeof(GamelistGenerationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GamelistGenerationResponse>> GenerateGamelist([FromBody] GamelistGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gamelistGenerationService.GenerateAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
    }

    /// <summary>
    /// Updates gamelists from already indexed local media and texts.
    /// </summary>
    /// <remarks>
    /// This endpoint does not download media and does not project files into rom folders. It reads
    /// each target gamelist once, resolves local canonical media from <c>media</c> and <c>media/user</c>,
    /// then writes at most once per gamelist when XML changes are needed.
    /// </remarks>
    [HttpPost("gamelist/update-local")]
    [ProducesResponseType(typeof(LocalGamelistUpdateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<LocalGamelistUpdateResponse>> UpdateLocalGamelists(
        [FromBody] LocalGamelistUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _localGamelistUpdateService.UpdateAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
        catch (Exception exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiErrorResponse { Error = exception.ToString() });
        }
    }

    /// <summary>
    /// Normalizes local metadata caches, then can explicitly normalize existing gamelist text fields or rewrite gamelists from the cleaned cache.
    /// </summary>
    /// <remarks>
    /// This endpoint does not scrape or download media. It cleans already local text bundles such as
    /// <c>media/systems/{system}/games/{slug}/texts/metadata-{lang}.json</c>, then can run the
    /// existing <c>gamelist.xml</c> text fields when <c>NormalizeExistingGamelists</c> is true, then can run the
    /// local gamelist update pass when <c>UpdateGamelists</c> is true. Gamelist writes must stay an explicit maintenance action.
    /// </remarks>
    [HttpPost("metadata/normalize")]
    [ProducesResponseType(typeof(MetadataNormalizationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<MetadataNormalizationResponse>> NormalizeMetadata(
        [FromBody] MetadataNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediaMaintenanceService.NormalizeLocalMetadataAsync(request, cancellationToken);
            if (request.NormalizeExistingGamelists)
            {
                var gamelistTextNormalization = await _mediaMaintenanceService.NormalizeExistingGamelistsAsync(
                    request,
                    cancellationToken);
                result.GamelistTextNormalization = gamelistTextNormalization;
                result.GamelistsUpdated = gamelistTextNormalization.GamelistsUpdated > 0;
            }

            if (request.UpdateGamelists)
            {
                var gamelistUpdate = await _localGamelistUpdateService.UpdateAsync(
                    new LocalGamelistUpdateRequest
                    {
                        Scope = string.IsNullOrWhiteSpace(request.Scope) ? "all" : request.Scope,
                        SystemId = request.SystemId,
                        GamePath = request.GamePath,
                        GameName = request.GameName,
                        GameSlug = request.GameSlug,
                        PreferredLanguage = request.PreferredLanguage
                    },
                    cancellationToken);
                result.GamelistsUpdated |= gamelistUpdate.SystemsUpdated > 0;
                result.GamelistUpdate = gamelistUpdate;
            }

            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
        catch (Exception exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiErrorResponse { Error = exception.ToString() });
        }
    }

    /// <summary>
    /// Reallocates visible gamelist media slots from current APIExpose media allocation settings.
    /// </summary>
    /// <remarks>
    /// This endpoint only rewrites gamelist media references such as <c>image</c>,
    /// <c>marquee</c> and <c>thumbnail</c>. It does not import, copy or download media.
    /// Use it after changing media allocation options like logo/marquee source.
    /// </remarks>
    [HttpPost("gamelist/refresh-selections")]
    [ProducesResponseType(typeof(GamelistRefreshSelectionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GamelistRefreshSelectionsResponse>> RefreshGamelistSelections(
        [FromBody] GamelistRefreshSelectionsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var scope = string.IsNullOrWhiteSpace(request.Scope)
                ? "system"
                : request.Scope.Trim().ToLowerInvariant();
            if (scope == "system")
            {
                if (string.IsNullOrWhiteSpace(request.SystemId))
                {
                    throw new InvalidOperationException("SystemId is required for scope=system.");
                }

                await _gameListImpactWarningService.WarnIfGameSelectedAsync(
                    "La reallocation des slots visibles",
                    cancellationToken);
                var systemId = request.SystemId.Trim();
                _mediaRuntimeState.RequestVisibleMediaReallocationWorkflow(
                    VisibleMediaReallocationReloadDelay,
                    new VisibleMediaReallocationRequest(scope, systemId, "api"));

                return Ok(new GamelistRefreshSelectionsResponse
                {
                    Scope = scope,
                    Systems = [systemId],
                    SystemsProcessed = 0,
                    SystemsUpdated = 0,
                    ReloadGamesRequested = true,
                    Message = $"Gamelist media reallocation queued for {systemId}. EmulationStation reloadgames will be requested after normalization."
                });
            }

            if (scope == "all")
            {
                await _gameListImpactWarningService.WarnIfGameSelectedAsync(
                    "La reallocation des slots visibles",
                    cancellationToken);
                _mediaRuntimeState.RequestVisibleMediaReallocationWorkflow(
                    VisibleMediaReallocationReloadDelay,
                    new VisibleMediaReallocationRequest(scope, string.Empty, "api"));

                return Ok(new GamelistRefreshSelectionsResponse
                {
                    Scope = scope,
                    SystemsProcessed = 0,
                    SystemsUpdated = 0,
                    ReloadGamesRequested = true,
                    Message = "Gamelist media reallocation queued for all systems. EmulationStation reloadgames will be requested after normalization."
                });
            }

            throw new InvalidOperationException("Scope must be system or all.");
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
    }

    /// <summary>
    /// Restores media tags from a richer gamelist backup into the current gamelist.
    /// </summary>
    /// <remarks>
    /// Dry-run is enabled by default. The current gamelist remains the authority for games and paths.
    /// </remarks>
    [HttpPost("gamelist/consolidate")]
    [ProducesResponseType(typeof(GamelistConsolidateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GamelistConsolidateResponse>> ConsolidateGamelist([FromBody] GamelistConsolidateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gamelistUpdateService.ConsolidateGamelistFromRichBackupAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiErrorResponse { Error = exception.Message });
        }
    }
}
