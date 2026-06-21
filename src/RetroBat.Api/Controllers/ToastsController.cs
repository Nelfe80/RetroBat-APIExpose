using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ToastsController : ControllerBase
{
    private readonly IToastNotificationService _toastNotificationService;
    private readonly ITaskProgressService _taskProgressService;

    public ToastsController(
        IToastNotificationService toastNotificationService,
        ITaskProgressService taskProgressService)
    {
        _toastNotificationService = toastNotificationService;
        _taskProgressService = taskProgressService;
    }

    [HttpPost]
    public async Task<IActionResult> ShowToast([FromBody] ToastNotification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.Title) && string.IsNullOrWhiteSpace(notification.Message))
        {
            return BadRequest(new { error = "Title or message is required." });
        }

        await _toastNotificationService.EnqueueAsync(notification, cancellationToken);
        return Accepted(new
        {
            queued = true,
            notification.Type,
            notification.Title,
            notification.Message,
            notification.DurationMs,
            notification.Position,
            notification.Animation
        });
    }

    /// <summary>
    /// Displays the task progress overlay for a few seconds to verify visibility over EmulationStation.
    /// </summary>
    [HttpPost("task-progress/test")]
    public async Task<IActionResult> ShowTaskProgressTest(CancellationToken cancellationToken)
    {
        const string taskId = "reloadgames";
        _taskProgressService.Report(taskId, "Test progression APIExpose", 0, 3, "ouverture overlay");
        await Task.Delay(750, cancellationToken);
        _taskProgressService.Report(taskId, "Test progression APIExpose", 1, 3, "au-dessus de ES");
        await Task.Delay(750, cancellationToken);
        _taskProgressService.Report(taskId, "Test progression APIExpose", 2, 3, "fermeture automatique");
        _taskProgressService.Complete(taskId);
        return Accepted(new
        {
            queued = true,
            taskId
        });
    }
}
