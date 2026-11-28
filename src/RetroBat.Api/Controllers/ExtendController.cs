using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Extend;

namespace RetroBat.Api.Controllers;

/// <summary>EXTEND MODE : l'etat des ROMs derivees d'un patch (CDC Extend Mode v0.1, partie C).</summary>
[ApiController]
[Tags("Extend")]
[Route("api/v1/extend")]
public sealed class ExtendController : ControllerBase
{
    private readonly ExtendRomsService _service;

    public ExtendController(ExtendRomsService service) => _service = service;

    /// <summary>Chaque fiche du catalogue <c>extend</c> du Data Pack : source trouvee ou non, ROM derivee presente, coeur pose, version.</summary>
    [HttpGet]
    public IActionResult Etat()
    {
        var (fiches, raison, passe) = _service.Etat;
        return Ok(new { conversions = fiches, reason = raison, last_pass_utc = passe });
    }
}
