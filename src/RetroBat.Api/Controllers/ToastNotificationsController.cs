using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/toast-notifications")]
public sealed class ToastNotificationsController : ControllerBase
{
    private readonly IToastNotificationService _toastNotificationService;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;

    public ToastNotificationsController(
        IToastNotificationService toastNotificationService,
        ApiExposeRuntimeOptionsService runtimeOptions)
    {
        _toastNotificationService = toastNotificationService;
        _runtimeOptions = runtimeOptions;
    }

    /// <summary>
    /// Pushes an APIExpose toast notification overlay above EmulationStation.
    /// </summary>
    /// <remarks>
    /// This endpoint is intended for Swagger/manual tests and integrations.
    /// It respects the RetroBat option <c>global.apiexpose.toast_notifications.enabled</c>.
    /// Native EmulationStation notifications pushed through the ES API on port 1234 are a separate channel.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PushToastNotification(
        [FromBody] ToastNotification notification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.Title) && string.IsNullOrWhiteSpace(notification.Message))
        {
            return BadRequest(new { error = "Title or message is required." });
        }

        if (!_runtimeOptions.AreToastNotificationsEnabled())
        {
            return Conflict(new { queued = false, reason = "toast_notifications_disabled" });
        }

        await _toastNotificationService.EnqueueAsync(notification, cancellationToken);
        return Accepted(new
        {
            queued = true,
            endpoint = "api/v1/toast-notifications",
            notification.Type,
            notification.Title,
            notification.Message,
            notification.DurationMs,
            notification.Position,
            notification.Animation
        });
    }
}
