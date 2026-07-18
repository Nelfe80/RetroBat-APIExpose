using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Prototype surface for the future multi-RetroBat hub.
/// </summary>
[ApiController]
[Tags("Internal & Prototype")]
[Route("api/v1/[controller]")]
public class HubController : ControllerBase
{
    /// <summary>
    /// Registers a RetroBat/APIExpose node on the future hub.
    /// </summary>
    /// <remarks>Current implementation is a placeholder; see docs/MULTI_RETROBAT_HUB.md.</remarks>
    [HttpPost("register")]
    public IActionResult RegisterNode([FromBody] RegisterPayload payload)
    {
        // Handle node registration for ArcadeHub mode
        return Ok(new { status = "registered", hubMode = "active" });
    }

    /// <summary>
    /// Lists registered hub nodes.
    /// </summary>
    /// <remarks>Current implementation returns prototype data; see docs/MULTI_RETROBAT_HUB.md.</remarks>
    [HttpGet("nodes")]
    public IActionResult GetNodes()
    {
        return Ok(new[] {
            new { nodeId = "cab-01", status = "online", mode = "node" }
        });
    }
}

public class RegisterPayload
{
    public string NodeId { get; set; } = string.Empty;
    public string DiscoveryUrl { get; set; } = string.Empty;
}
