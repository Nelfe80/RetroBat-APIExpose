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
    private readonly RetroBat.Api.Replay.Video.EchelleDesFilms? _echelle;
    private readonly RetroBat.Api.Infrastructure.CertifiedSettingsService? _certifies;
    private readonly RetroBat.Api.Infrastructure.NotificationsDesLancementsEs? _notifications;

    public RomPacksController(RomPackInstallerService romPackInstaller,
        RetroBat.Api.Replay.Video.EchelleDesFilms? echelle = null,
        RetroBat.Api.Infrastructure.CertifiedSettingsService? certifies = null,
        RetroBat.Api.Infrastructure.NotificationsDesLancementsEs? notifications = null)
    {
        _romPackInstaller = romPackInstaller;
        _echelle = echelle;
        _certifies = certifies;
        _notifications = notifications;
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
        // Le script game-start d'ES passe avant le lanceur : le facteur du film du jeu se pose ici.
        _echelle?.PoserPourLeLancement(rawArguments);
        // Et les reglages certifies du jeu (rembobinage, run-ahead, sauvegarde auto), que le lanceur lit juste apres.
        _certifies?.NeutraliserAvantLeLancement(rawArguments);
        // Les notifications de RetroArch d'une partie NelfePlay lancee par ES : la cle par jeu, rendue des que l'emulateur parait (11/11)
        _notifications?.PoserAvantLeLancement(rawArguments);
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
