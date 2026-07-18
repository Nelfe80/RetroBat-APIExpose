using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Notifications & UI")]
[Route("api/v1/es-notifications")]
public sealed class EsNotificationsController : ControllerBase
{
    private readonly IEmulationStationNotificationService _notificationService;

    public EsNotificationsController(IEmulationStationNotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>
    /// Pushes a native EmulationStation notification through the ES API on port 1234.
    /// </summary>
    /// <remarks>
    /// This is not an APIExpose toast overlay. It forwards the message to
    /// <c>http://127.0.0.1:1234/notify</c>.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PushEsNotification(
        [FromBody] EsNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "Message is required." });
        }

        await _notificationService.NotifyAsync(request.Message, cancellationToken);
        return Accepted(new
        {
            queued = true,
            endpoint = "api/v1/es-notifications",
            target = "http://127.0.0.1:1234/notify",
            request.Message
        });
    }

    /// <summary>
    /// Shows a native EmulationStation message box through the ES API on port 1234.
    /// </summary>
    /// <remarks>
    /// This forwards the message to <c>http://127.0.0.1:1234/messagebox</c>.
    /// It uses the same native ES API notification channel as <c>/notify</c>.
    /// </remarks>
    [HttpPost("messagebox")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PushEsMessageBox(
        [FromBody] EsNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "Message is required." });
        }

        await _notificationService.MessageBoxAsync(request.Message, cancellationToken);
        return Accepted(new
        {
            queued = true,
            endpoint = "api/v1/es-notifications/messagebox",
            target = "http://127.0.0.1:1234/messagebox",
            request.Message
        });
    }
}

public sealed class EsNotificationRequest
{
    public string Message { get; set; } = string.Empty;
}
