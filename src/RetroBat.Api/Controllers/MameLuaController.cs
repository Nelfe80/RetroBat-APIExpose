using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Diagnostic du pont MAME Lua ingame : sessions et compteurs de
/// declenchement par adresse. Consomme par le banc de validation des .MEM
/// arcade (tools/mem-curator/mame_batch_validate.py).
/// </summary>
[ApiController]
[Tags("Game Events")]
[Route("api/v1/mamelua")]
public class MameLuaController : ControllerBase
{
    private readonly MameLuaIngameProvider? _provider;

    public MameLuaController(IEnumerable<IProvider> providers)
    {
        _provider = providers.OfType<MameLuaIngameProvider>().FirstOrDefault();
    }

    /// <summary>Diagnostic snapshot of the MAME Lua ingame bridge sessions (501 when the provider is off).</summary>
    [HttpGet("sessions")]
    public IActionResult Sessions()
        => _provider is null
            ? StatusCode(StatusCodes.Status501NotImplemented)
            : Ok(_provider.SessionsSnapshot());
}
