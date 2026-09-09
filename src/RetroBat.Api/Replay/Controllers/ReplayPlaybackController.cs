using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>Lecture d'un replay (R2). Commandes logiques uniquement, jamais de chemin/commande libre.</summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/replay")]
public sealed class ReplayPlaybackController : ControllerBase
{
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayStore _store;

    public ReplayPlaybackController(ReplayPlaybackService playback, ReplayStore store)
    {
        _playback = playback;
        _store = store;
    }

    /// <summary>Appel depuis la machine elle-même ? Le diagnostic et les secrets n'en sortent pas.</summary>
    private bool IsLocalCaller()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }

    /// <summary>
    /// La clé de partage de CETTE borne, à confier à un pair pour qu'il puisse récupérer ses
    /// replays publics. Elle n'ouvre que la surface de partage, jamais l'administration de la
    /// machine. Ne sort qu'en boucle locale : un pair n'a aucune raison de la lire par l'API.
    /// </summary>
    [HttpGet("share-key")]
    public IActionResult ShareKey()
    {
        if (!IsLocalCaller()) return NotFound();
        var key = RetroBat.Api.Replay.Sharing.ReplayShareKeyStore.GetOrCreate();
        return Ok(new { ok = key.Length > 0, share_key = key });
    }

    // Clé canonique CDC / front : replay_id.
    public sealed record PlayRequest([property: JsonPropertyName("replay_id")] string ReplayId);

    [HttpPost("play")]
    public async Task<IActionResult> Play([FromBody] PlayRequest? req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.ReplayId))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });

        var r = await _playback.PlayAsync(req.ReplayId, ct);
        if (!r.Accepted)
        {
            var status = r.Error is ReplayErrorCode.ReplayNotFound ? 404
                : r.Error is ReplayErrorCode.ReplayAlreadyRunning or ReplayErrorCode.GameAlreadyRunning ? 409
                : 422;
            return StatusCode(status, new { ok = false, error = new { code = ToCode(r.Error) } });
        }
        return Ok(new { accepted = true, replay_id = req.ReplayId, state = r.State });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop(CancellationToken ct)
    {
        await _playback.StopAsync(ct);
        return Ok(new { ok = true });
    }

    public sealed record ControlRequest(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("seconds")] double? Seconds);

    /// <summary>Commande logique de lecture (aucune commande RetroArch libre).</summary>
    [HttpPost("control")]
    public async Task<IActionResult> Control([FromBody] ControlRequest? req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Action))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });

        switch (req.Action.ToLowerInvariant())
        {
            case "pause_toggle": await _playback.PauseToggleAsync(ct); break;
            case "next_checkpoint": await _playback.NextCheckpointAsync(ct); break;
            case "prev_checkpoint": await _playback.PreviousCheckpointAsync(ct); break;
            case "restart_run": await _playback.RestartRunAsync(ct); break;
            case "stop": await _playback.StopAsync(ct); break;
            // Seeks NOMMÉS (R3.11) : l'appelant exprime une INTENTION, le serveur décide la durée
            // (court/long) — l'UX peut évoluer sans casser le SDK.
            case "seek_back_short": await _playback.SeekShortBackwardAsync(ct); break;
            case "seek_forward_short": await _playback.SeekShortForwardAsync(ct); break;
            case "seek_back_long": await _playback.SeekLongBackwardAsync(ct); break;
            case "seek_forward_long": await _playback.SeekLongForwardAsync(ct); break;
            // Primitive bas niveau, gardée pour les outils techniques / le debug.
            case "seek_relative": await _playback.SeekRelativeAsync(req.Seconds ?? 0, ct); break;
            default: return BadRequest(new { ok = false, error = new { code = "REPLAY_COMMAND_UNSUPPORTED" } });
        }
        var s = _playback.GetState();
        return Ok(new { accepted = true, state = s.State, frame = s.Frame });
    }

    [HttpGet("state")]
    public IActionResult State()
    {
        var s = _playback.GetState();
        return Ok(new
        {
            mode = s.Mode,
            state = s.State,
            replay_id = s.ReplayId,
            frame = s.Frame,
            run_start_frame = s.RunStartFrame,
            run_end_frame = s.RunEndFrame,
            replay_end_frame = s.ReplayEndFrame,
            paused = s.Paused,
            error = s.Error,
            nominal_fps = s.NominalFps,
            fps_source = s.FpsSource,   // core | measured | default — d'où vient la base de temps
            card = s.Card is null ? null : new
            {
                game = s.Card.Game,
                system = s.Card.System,
                date = s.Card.DateText,
                player = s.Card.Player,
                score = s.Card.Score,
                rank = s.Card.Rank,
                certified = s.Card.Certified,
            },
        });
    }

    /// <summary>
    /// R3.2 — diagnostic : cadence que le core a annoncée pour le contenu chargé le plus récemment.
    /// Sert à vérifier la détection core par core (console PAL, arcade…) : on lance le jeu, on
    /// interroge ce point, on compare à la cadence attendue de la machine émulée.
    /// </summary>
    [HttpGet("core-timing")]
    public IActionResult CoreTiming([FromServices] RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe probe)
    {
        if (!IsLocalCaller()) return NotFound();
        var age = probe.LogAge();
        var t = probe.ReadLatest(ignoreAge: true); // diagnostic : on montre la valeur ET son âge
        if (t is null) return Ok(new { ok = false, reason = "no_av_info_in_log", log_age_seconds = age?.TotalSeconds });
        return Ok(new
        {
            ok = true,
            fps = t.Fps,
            width = t.Width,
            height = t.Height,
            sample_rate = t.SampleRate,
            crc32 = t.Crc32,
            log_age_seconds = age?.TotalSeconds,
            // Faux = le log ne parle plus de la partie en cours : le recorder REFUSERAIT cette valeur.
            usable_for_recording = age is not null && age < TimeSpan.FromMinutes(10),
        });
    }

    /// <summary>Diagnostic : force la remontée des réactions d'un replay.</summary>
    [HttpPost("upload-reactions")]
    public async Task<IActionResult> UploadReactions([FromQuery(Name = "replay_id")] string? replayId,
        [FromServices] RetroBat.Api.Replay.Sharing.ReplayReactionUploader uploader, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var r = await uploader.UploadAsync(replayId ?? string.Empty, ct);
        return Ok(new { sent = r.Sent, count = r.Count, reason = r.Reason });
    }

    /// <summary>Diagnostic : force une déclaration de conservation et rend son compte rendu.</summary>
    [HttpPost("declare-holdings")]
    public async Task<IActionResult> DeclareHoldings(
        [FromServices] RetroBat.Api.Replay.Sharing.ReplayHoldingsReporter reporter, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var r = await reporter.ReportAsync(ct);
        return Ok(new { sent = r.Sent, declared = r.Declared, reason = r.Reason });
    }

    /// <summary>
    /// Diagnostic : force un passage de l'agent de réplication et rend son compte rendu, plutôt
    /// que d'attendre la demi-heure de cadence pour savoir si la configuration tient.
    /// </summary>
    [HttpPost("replicate")]
    public async Task<IActionResult> Replicate(
        [FromServices] RetroBat.Api.Replay.Sharing.ReplayReplicationService agent, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var r = await agent.TickAsync(ct);
        return Ok(new { ran = r.Ran, fetched = r.Fetched, examined = r.Examined, reason = r.Reason });
    }

    /// <summary>
    /// Diagnostic NelfeNet : les pairs connus de cette borne et leur joignabilité. Ne renvoie
    /// JAMAIS les clés d'API, seulement le fait qu'une clé soit renseignée.
    /// </summary>
    [HttpGet("peers")]
    public async Task<IActionResult> Peers([FromServices] RetroBat.Api.Replay.Sharing.ReplayPeerDirectory directory,
        [FromServices] IHttpClientFactory httpFactory, CancellationToken ct,
        [FromQuery] bool refresh = true)
    {
        if (!IsLocalCaller()) return NotFound();
        var results = new List<object>();
        foreach (var p in await directory.PeersAsync(ct, refresh))
        {
            // La sonde interroge /status, qui n'existe que sur une BORNE. Un miroir n'en est pas
            // une : le sonder ainsi afficherait « injoignable » alors qu'il livre parfaitement.
            // On préfère dire « inconnu » plutôt qu'affirmer un faux.
            var isMirror = p.Source.Contains(RetroBat.Api.Replay.Sharing.MirrorPeerSource.SourceTag, StringComparison.Ordinal);
            bool? reachable = null;
            if (!isMirror)
            {
                reachable = false;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));
                    var client = httpFactory.CreateClient();
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    using var req = new HttpRequestMessage(HttpMethod.Get, p.BaseUrl.TrimEnd('/') + "/api/v1/status");
                    if (!string.IsNullOrWhiteSpace(p.ApiKey)) req.Headers.Add("X-Api-Key", p.ApiKey);
                    using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    reachable = res.IsSuccessStatusCode;
                }
                catch { reachable = false; }
            }
            results.Add(new
            {
                name = p.Name,
                base_url = p.BaseUrl,
                source = p.Source,               // par quelle porte on l'a apprise
                device_id = p.DeviceId,
                has_api_key = !string.IsNullOrWhiteSpace(p.ApiKey),
                reachable,                       // CONSTATÉ, jamais supposé
            });
        }
        return Ok(new { peers = results, total = results.Count });
    }

    /// <summary>
    /// Les records de cette borne qui meritent une image, sans rien lancer. A regarder AVANT de
    /// declencher la relecture : elle prend l'ecran, autant savoir ce qu'elle va faire.
    /// </summary>
    [HttpGet("shots/candidates")]
    public async Task<IActionResult> ShotCandidates(
        [FromServices] RetroBat.Api.Infrastructure.ScoreShotRegenerator regenerateur,
        CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var candidats = await regenerateur.CandidatsAsync(ct);
        return Ok(new
        {
            total = candidats.Count,
            candidates = candidats.Select(c => new
            {
                rom_group = c.RomGroup, ruleset = c.Ruleset, score = c.Score,
                replay_id = c.ReplayId, session_id = c.SessionId, frame = c.TargetFrame,
            }),
        });
    }

    /// <summary>
    /// Refait l'image des records en REJOUANT leurs replays, en definition d'origine.
    ///
    /// PREND L'ECRAN : RetroArch est lance pour de vrai, une fois par record. L'appel refuse de
    /// demarrer si une lecture ou un emulateur tourne deja.
    /// </summary>
    [HttpPost("shots/regenerate")]
    public async Task<IActionResult> RegenerateShots(
        [FromServices] RetroBat.Api.Infrastructure.ScoreShotRegenerator regenerateur,
        CancellationToken ct, [FromQuery] int limit = 5)
    {
        if (!IsLocalCaller()) return NotFound();
        var r = await regenerateur.RunAsync(limit, ct);
        return Ok(new
        {
            ran = r.Ran, candidates = r.Candidates, captured = r.Captured,
            sent = r.Sent, reason = r.Reason, details = r.Details,
        });
    }

    /// <summary>Réactions horodatées d'un replay (JSONL rejouable). Sert l'affichage (étape suivante).</summary>
    [HttpGet("reactions")]
    public IActionResult Reactions([FromQuery(Name = "replay_id")] string? replayId)
    {
        if (string.IsNullOrWhiteSpace(replayId))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });
        var items = _store.ReadReactions(replayId).Select(r => new
        {
            reaction = r.Reaction, level = r.Level, frame = r.Frame, ts_ms = r.TsMs, lang = r.Lang, chord = r.Chord,
        });
        return Ok(new { replay_id = replayId, items });
    }

    // ReplayErrorCode (PascalCase) -> code API stable UPPER_SNAKE_CASE.
    private static string ToCode(ReplayErrorCode e)
    {
        var s = e.ToString();
        var sb = new StringBuilder(s.Length + 6);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(s[i]));
        }
        return sb.ToString();
    }
}
