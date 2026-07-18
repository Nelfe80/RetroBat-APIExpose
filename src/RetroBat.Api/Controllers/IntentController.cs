using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Prototype surface for future high-level remote intents.
/// </summary>
[ApiController]
[Tags("Internal & Prototype")]
[Route("api/v1/[controller]")]
public class IntentController : ControllerBase
{
    /// <summary>
    /// Pushes a future display/navigation intent.
    /// </summary>
    /// <remarks>Current implementation acknowledges the payload without persistence; see docs/MULTI_RETROBAT_HUB.md.</remarks>
    [HttpPost("pushView")]
    public IActionResult PushView([FromBody] PushViewPayload payload)
    {
        // This simulates pushing an intent into the system
        // The IntentStore and Orchestrator would process it
        return Accepted(new {
            status = "accepted",
            intentId = Guid.NewGuid().ToString(),
            expiresAt = DateTime.UtcNow.AddSeconds(payload.TtlSeconds ?? 60)
        });
    }
}

public class PushViewPayload
{
    public string Target { get; set; } = string.Empty;
    public string ViewName { get; set; } = string.Empty;
    public int? TtlSeconds { get; set; }
    public string ResolvableContext { get; set; } = string.Empty;
}
