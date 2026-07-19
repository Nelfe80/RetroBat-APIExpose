using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Annonce de challenge sur l'écran de la borne (fenêtre 80 % centrée :
/// fanart, logo, objectif, « Tenez-vous prêt ! », compte à rebours, QR de
/// participation). Poussée par le hub de salle avant le coup d'envoi —
/// route volontairement HORS du préfixe /api/v1/overlay (loopback-only).
/// </summary>
[ApiController]
[Tags("Cabinet Overlays")]
[Route("api/v1/challenge-announce")]
public sealed class ChallengeAnnounceController : ControllerBase
{
    private readonly ChallengeAnnounceOverlayService _overlay;
    private readonly LiveContestOverlayService _gameOverlay;

    public ChallengeAnnounceController(
        ChallengeAnnounceOverlayService overlay, LiveContestOverlayService gameOverlay)
    {
        _overlay = overlay;
        _gameOverlay = gameOverlay;
    }

    /// <summary>État courant de l'annonce.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ChallengeAnnounceOverlayService.AnnounceState), StatusCodes.Status200OK)]
    public IActionResult GetState() => Ok(_overlay.GetState());

    /// <summary>Affiche (ou met à jour) l'annonce. Les médias (fanart, logo)
    /// sont résolus localement depuis le gamelist à partir de gamePath.</summary>
    /// <response code="202">Annonce appliquée.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Apply([FromBody] ChallengeAnnounceRequest request, CancellationToken cancellationToken)
    {
        await _overlay.ApplyAsync(
            request.Visible ?? true,
            request.GamePath,
            request.GameName,
            request.Objective,
            request.StartsAtUtc,
            request.QrImageUrl,
            cancellationToken);
        return Accepted(_overlay.GetState());
    }

    /// <summary>Retire l'annonce.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Hide(CancellationToken cancellationToken)
    {
        await _overlay.ApplyAsync(false, null, null, null, null, null, cancellationToken);
        return Accepted(_overlay.GetState());
    }

    /// <summary>
    /// Consignes de challenge en HAUT de l'écran de jeu :
    /// « press-start » = invite à commencer la partie (START) ;
    /// « hold » = partie en pause, ne plus rien toucher ;
    /// « clear » = retire le bandeau. Poussées par le hub au fil du flux.
    /// </summary>
    /// <response code="202">Bandeau appliqué.</response>
    [HttpPost("phase")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Phase([FromBody] ChallengePhaseRequest request)
    {
        switch ((request.Phase ?? "").ToLowerInvariant())
        {
            case "press-start":
                _gameOverlay.ShowTop("CHALLENGE",
                    "Appuyez sur START pour commencer la partie",
                    "Elle sera mise en pause automatiquement — prête pour le départ", 0);
                // emulatorlauncher/ES peut rester DEVANT RetroArch après le
                // lancement : on le remet au premier plan dès qu'il a une
                // fenêtre (même filet que Live Contest).
                _ = FocusRetroArchWhenUpAsync();
                break;
            case "hold":
                _gameOverlay.ShowTop("CHALLENGE",
                    "Ne touchez plus à rien !",
                    "Partie en pause — départ imminent, attendez le décompte", 0);
                break;
            default:
                _gameOverlay.Hide();
                break;
        }

        return Accepted(new { phase = request.Phase });
    }

    /// <summary>
    /// DÉPART façon Live Contest : décompte 5-4-3-2-1 dans le jeu (overlay
    /// in-game centré) calé sur startsAtUtc, dépause à zéro pile, « GO ! ».
    /// Poussé par le hub quand le gérant lance le challenge — tout le monde
    /// part en même temps.
    /// </summary>
    /// <response code="202">Décompte engagé.</response>
    [HttpPost("go")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Go([FromBody] ChallengeGoRequest request)
    {
        var startsAt = request.StartsAtUtc ?? DateTime.UtcNow.AddSeconds(6);
        var overlay = _gameOverlay;
        _ = Task.Run(async () =>
        {
            try
            {
                // Même détection honnête que Live Contest : sans jeu chargé,
                // pas de décompte mensonger.
                var probe = await SendRetroArchUdpAsync("GET_STATUS", expectResponse: true);
                if (string.IsNullOrEmpty(probe) || probe.Contains("CONTENTLESS", StringComparison.Ordinal))
                {
                    return;
                }

                for (var n = 5; n >= 1; n--)
                {
                    var wait = startsAt.AddSeconds(-n) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait);
                    }

                    overlay.ShowCenter(n.ToString(), "Départ dans…", 0);
                }

                var final = startsAt - DateTime.UtcNow;
                if (final > TimeSpan.Zero)
                {
                    await Task.Delay(final);
                }

                var state = await SendRetroArchUdpAsync("GET_STATUS", expectResponse: true);
                if (state.Contains("PAUSED", StringComparison.Ordinal))
                {
                    await SendRetroArchUdpAsync("PAUSE_TOGGLE", expectResponse: false);
                }

                FocusRetroArch();
                overlay.ShowCenter("GO !", "", 1800);
            }
            catch (Exception)
            {
            }
        });
        return Accepted(new { startsAtUtc = startsAt });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void FocusRetroArch()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("retroarch"))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(process.MainWindowHandle);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task FocusRetroArchWhenUpAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(2000);
            if (System.Diagnostics.Process.GetProcessesByName("retroarch")
                .Any(process => process.MainWindowHandle != IntPtr.Zero))
            {
                FocusRetroArch();
                return;
            }
        }
    }

    private static async Task<string> SendRetroArchUdpAsync(string command, bool expectResponse)
    {
        using var udp = new UdpClient();
        var bytes = Encoding.UTF8.GetBytes(command);
        await udp.SendAsync(bytes, bytes.Length, "127.0.0.1", 55355);
        if (!expectResponse)
        {
            return string.Empty;
        }

        var receive = udp.ReceiveAsync();
        var done = await Task.WhenAny(receive, Task.Delay(1000));
        return done == receive ? Encoding.UTF8.GetString((await receive).Buffer) : string.Empty;
    }
}

public sealed record ChallengeGoRequest(DateTime? StartsAtUtc);

public sealed record ChallengePhaseRequest(string? Phase);

public sealed record ChallengeAnnounceRequest(
    bool? Visible,
    string? GamePath,
    string? GameName,
    string? Objective,
    DateTime? StartsAtUtc,
    string? QrImageUrl);
