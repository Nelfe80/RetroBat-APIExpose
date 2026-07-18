using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Prototype surface for future LIP/LAY rule compilation and routing.
/// </summary>
[ApiController]
[Tags("Internal & Prototype")]
[Route("api/v1/[controller]")]
public class RulesController : ControllerBase
{
    /// <summary>
    /// Lists active future rule sets.
    /// </summary>
    /// <remarks>Current implementation returns prototype data; see docs/MULTI_RETROBAT_HUB.md.</remarks>
    [HttpGet("active")]
    public IActionResult GetActiveRules()
    {
        // Mock returning active rule sets
        return Ok(new object[] {
            new {
                ruleSetId = "lip:Nintendo64:Arcade-Shark-8B",
                scope = new { systemId = "n64", layout = "8-Button" }
            },
            new {
                ruleSetId = "lay:chasehq:Marquee_Only",
                scope = new { systemId = "mame", machine = "chasehq" }
            }
        });
    }

    /// <summary>
    /// Compiles a future LIP/LAY rule source.
    /// </summary>
    /// <remarks>Current implementation is a placeholder; see docs/MULTI_RETROBAT_HUB.md.</remarks>
    [HttpPost("compile")]
    public IActionResult Compile([FromBody] object source)
    {
        // This is a placeholder for LIP/LAY compilation API
        return Accepted(new { status = "compiled", ir = "..." });
    }
}
