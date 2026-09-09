using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Playback;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Sonde d'installation — servie par APIExpose lui-même, atteinte par NAVIGATION
/// depuis /setup (le navigateur bloque un fetch HTTPS→loopback ; une navigation,
/// non). /setup NAVIGUE ici, on teste côté serveur ce que le navigateur ne peut
/// pas voir, puis on REDIRIGE vers ?return= (le site) en portant le résultat dans
/// l'URL : rb (RetroBat/EmulationStation joignable), api (APIExpose = nous, donc
/// toujours 1), paired (machine liée) + pseudo. /setup lit ces paramètres et
/// affiche l'état + n'affiche « Lier » que si la machine n'est pas encore liée.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
[SupportedOSPlatform("windows")]
public sealed class SetupProbeController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _device;
    private readonly NelfePlayAgentService _agent;
    private readonly ReplayLaunchTokenStore _tokens;

    public SetupProbeController(IHttpClientFactory httpFactory, NelfePlayDeviceStore device,
        NelfePlayAgentService agent, ReplayLaunchTokenStore tokens)
    {
        _httpFactory = httpFactory;
        _device = device;
        _agent = agent;
        _tokens = tokens;
    }

    // Sonde de PRÉSENCE pour le funnel (technique popup) : le site ouvre cette URL loopback
    // dans une popup (window.open = navigation, autorisée contrairement à fetch/LNA). Si
    // APIExpose répond, on REDIRIGE la popup vers <to>/apiexpose-ok (même origine que le site)
    // qui signale « présent » au parent puis se ferme. Si APIExpose est éteint, la popup tombe
    // sur ERR_CONNECTION_REFUSED (contenu dans la popup) et le site conclut « absent » au timeout.
    [HttpGet("/nelfeplay/detect")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Detect([FromQuery(Name = "to")] string? to, CancellationToken ct)
    {
        // On porte le MÊME statut que /setup-probe (rb/api/paired/pseudo/device_id) : le
        // signal de présence sert aussi à /account (⚡ machine courante) sans autre aller-retour.
        // On JOINT un launch_token à usage unique : seule cette page nelfeplay.com le récupère
        // (signal scellé à son origine) et /replay/watch l'exige pour AUTO-lancer une lecture.
        var origin = NelfeReturnUrl.SafeOrigin(to);
        var status = await BuildStatusQueryAsync(ct).ConfigureAwait(false);
        return Redirect(origin + "/apiexpose-ok" + status + "&token=" + _tokens.Issue());
    }

    // Statut de la borne, en query : rb (RetroBat/ES joignable), api=1 (nous), paired, pseudo,
    // device_id. Partagé par /setup-probe et /nelfeplay/detect.
    private async Task<string> BuildStatusQueryAsync(CancellationToken ct)
    {
        var es = await EmulationStationReachableAsync(ct).ConfigureAwait(false);
        var paired = _device.IsPaired;
        var pseudo = paired ? _agent.Status.Pseudo : null;
        var deviceId = paired ? _device.DeviceId : null;

        var q = "?rb=" + (es ? "1" : "0") + "&api=1&paired=" + (paired ? "1" : "0");
        if (!string.IsNullOrWhiteSpace(pseudo)) { q += "&pseudo=" + Uri.EscapeDataString(pseudo); }
        if (!string.IsNullOrWhiteSpace(deviceId)) { q += "&device_id=" + Uri.EscapeDataString(deviceId); }
        return q;
    }

    [HttpGet("/setup-probe")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Probe([FromQuery(Name = "return")] string? returnUrl, CancellationToken ct)
    {
        var ret = NelfeReturnUrl.SafeBase(returnUrl);
        return Redirect(ret + await BuildStatusQueryAsync(ct).ConfigureAwait(false));
    }

    // EmulationStation expose une API HTTP sur :1234. On ne cherche pas un endpoint
    // précis : une RÉPONSE quelconque (même 404) prouve qu'ES écoute ; seuls un
    // refus de connexion ou un timeout signifient « absent ».
    private async Task<bool> EmulationStationReachableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(1500);
            using var resp = await client.GetAsync("http://127.0.0.1:1234/", cts.Token).ConfigureAwait(false);
            return true; // toute réponse HTTP = ES écoute
        }
        catch
        {
            return false; // connexion refusée / timeout = ES absent
        }
    }
}
