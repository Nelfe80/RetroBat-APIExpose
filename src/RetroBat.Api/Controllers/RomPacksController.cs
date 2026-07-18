using System.Text;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Roms Manager")]
[Route("api/v1/rom-packs")]
public sealed class RomPacksController : ControllerBase
{
    private readonly RomPackInstallerService _romPackInstaller;

    public RomPacksController(RomPackInstallerService romPackInstaller)
    {
        _romPackInstaller = romPackInstaller;
    }

    /// <summary>
    /// Ensures the ROM about to launch is extracted from its pack (on-the-fly
    /// installer). Body is the raw launcher arguments as text/plain.
    /// </summary>
    [HttpPost("on-the-fly/ensure-launch-rom")]
    [Consumes("text/plain")]
    public async Task<ActionResult<OnTheFlyRomInstallResult>> EnsureLaunchRom(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawArguments = await reader.ReadToEndAsync(cancellationToken);
        var result = await _romPackInstaller.EnsureLaunchRomAsync(rawArguments, cancellationToken);
        return Ok(result);
    }

    /// <summary>Rescans the package-installer folder and rebuilds the ROM pack index.</summary>
    [HttpPost("rescan")]
    public async Task<ActionResult<RomPackInstallerIndex>> Rescan(CancellationToken cancellationToken)
    {
        var result = await _romPackInstaller.RescanAsync(cancellationToken);
        return Ok(result);
    }
}
