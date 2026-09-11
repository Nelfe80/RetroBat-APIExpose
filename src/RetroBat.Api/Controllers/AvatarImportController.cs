using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Avatar;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Sharing;

namespace RetroBat.Api.Controllers;

/// <summary>
/// La planche d'avatar arrive du NAVIGATEUR de son joueur, et cette borne en devient le premier
/// detenteur.
///
/// Le navigateur ne peut pas la remettre par un fetch : il est sur nelfeplay.com en HTTPS, et un fetch
/// vers le loopback est bloque. Il NAVIGUE donc une fenetre vers /nelfeplay/avatar/receive en portant
/// la planche dans le FRAGMENT de l'adresse. Le fragment ne quitte jamais le navigateur : la page
/// servie ici le lit et l'envoie a sa propre origine, qui est celle de la borne. Aucune dependance a
/// `window.opener`, qu'une politique d'ouverture cross-origin peut couper.
///
/// Rien n'est garde parce qu'un navigateur le dit. Les octets doivent tomber sur l'empreinte annoncee,
/// avoir la forme d'une planche, ET l'index de la plateforme doit donner cette meme empreinte pour ce
/// quadruplet. Une page quelconque qui posterait ici une image inventee ne ferait rien entrer : seule
/// une planche deja declaree par son joueur est gardee, puis partagee.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
public sealed class AvatarImportController : ControllerBase
{
    private readonly AvatarSheetStore _avatars;
    private readonly ReplayHoldingsReporter _recensement;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AvatarImportController> _logger;

    public AvatarImportController(AvatarSheetStore avatars, ReplayHoldingsReporter recensement,
        IHttpClientFactory httpFactory, ILogger<AvatarImportController> logger)
    {
        _avatars = avatars;
        _recensement = recensement;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// La page de remise. Le retour est une adresse CONNUE (hote NelfePlay en HTTPS), sinon celle
    /// par defaut : cette page ne doit pas pouvoir servir de tremplin vers ailleurs.
    /// </summary>
    [HttpGet("/nelfeplay/avatar/receive")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ContentResult Receive([FromQuery(Name = "to")] string? to)
    {
        // Serialise en JSON : le serialiseur echappe <, > et & , la valeur ne peut donc pas sortir
        // du script ou elle est posee.
        var retour = JsonSerializer.Serialize(NelfeReturnUrl.SafeBase(to));
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(PageDeRemise.Replace("__RETOUR__", retour, StringComparison.Ordinal), "text/html; charset=utf-8");
    }

    [HttpPost("/nelfeplay/avatar/import")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [RequestSizeLimit(AvatarSheetStore.OctetsMax + 1024)]
    public async Task<IActionResult> Import(
        [FromQuery] string? pseudo, [FromQuery] string? family, [FromQuery] string? variation,
        [FromQuery] string? generator, [FromQuery] string? sha256, CancellationToken ct)
    {
        var demande = Lire(pseudo, family, variation, generator);
        var sha = (sha256 ?? "").Trim().ToLowerInvariant();
        if (demande is null || !AvatarSheetStore.EstSha(sha))
        {
            return BadRequest(new { ok = false, error = "bad_request" });
        }

        var octets = await LireCorpsAsync(ct).ConfigureAwait(false);
        var forme = AvatarSheetStore.Valider(octets);
        if (forme is not null) return UnprocessableEntity(new { ok = false, error = forme });
        if (Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant() != sha)
        {
            return UnprocessableEntity(new { ok = false, error = "sha_mismatch" });
        }

        // L'index AVANT le magasin : sans lui, n'importe quelle page pourrait faire garder, puis
        // partager, une image inventee.
        var indexee = await EmpreinteIndexeeAsync(demande, ct).ConfigureAwait(false);
        if (indexee is null) return StatusCode(502, new { ok = false, error = "index_unreachable" });
        if (indexee.Length == 0) return UnprocessableEntity(new { ok = false, error = "not_declared" });
        if (indexee != sha) return Conflict(new { ok = false, error = "index_disagrees" });

        var r = await _avatars.ImporterAsync(octets, sha, ct).ConfigureAwait(false);
        if (!r.Ok) return UnprocessableEntity(new { ok = false, error = r.Erreur });
        _avatars.Cataloguer(demande.Pseudo, demande.Famille, demande.Variation, demande.Generateur, sha, verifiee: true);

        // Se declarer detentrice TOUT DE SUITE : tant que la borne ne l'a pas fait, aucune autre ne
        // peut demander la planche. Sans attendre, la fenetre du navigateur n'a pas a patienter.
        _ = Task.Run(async () =>
        {
            try { await _recensement.ReportAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Avatars : recensement apres import en erreur."); }
        });

        _logger.LogInformation("Avatars : planche de {Pseudo} ({Famille}) recue du navigateur.", demande.Pseudo, demande.Famille);
        return Ok(new { ok = true, sha256 = sha });
    }

    /// <summary>
    /// ESSAI : pose une planche fabriquee hors ligne, pour voir une foule simulee avec de vrais
    /// avatars. Elle ne passe pas par l'index, donc elle est cataloguee NON verifiee et n'est jamais
    /// annoncee au recensement. Refuse a toute autre machine que celle-ci.
    /// </summary>
    [HttpPost("/nelfeplay/dev/avatar")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [RequestSizeLimit(AvatarSheetStore.OctetsMax + 1024)]
    public async Task<IActionResult> DevAvatar(
        [FromQuery] string? pseudo, [FromQuery] string? family, [FromQuery] string? variation,
        [FromQuery] string? generator, CancellationToken ct)
    {
        var distant = HttpContext.Connection.RemoteIpAddress;
        if (distant is null || !IPAddress.IsLoopback(distant)) return NotFound();

        var demande = Lire(pseudo, family, variation, generator);
        if (demande is null) return BadRequest(new { ok = false, error = "bad_request" });

        var r = await _avatars.ImporterAsync(await LireCorpsAsync(ct).ConfigureAwait(false), null, ct).ConfigureAwait(false);
        if (!r.Ok) return UnprocessableEntity(new { ok = false, error = r.Erreur });
        _avatars.Cataloguer(demande.Pseudo, demande.Famille, demande.Variation, demande.Generateur, r.Sha256, verifiee: false);
        return Ok(new { ok = true, sha256 = r.Sha256 });
    }

    private sealed record Quadruplet(string Pseudo, string Famille, int Variation, string Generateur);

    /// <summary>Le pseudo tel quel (espaces retires) : la plateforme le compare a l'octet pres.</summary>
    private static Quadruplet? Lire(string? pseudo, string? famille, string? variation, string? generateur)
    {
        var p = (pseudo ?? "").Trim();
        var f = (famille ?? "").Trim().ToLowerInvariant();
        var g = (generateur ?? "").Trim().ToLowerInvariant();
        var v = (variation ?? "0").Trim();
        if (p.Length is 0 or > 60 || f.Length is 0 or > 20 || g.Length is 0 or > 40) return null;
        if (v.Length is 0 or > 4 || !v.All(char.IsAsciiDigit)) return null;
        return new Quadruplet(p, f, int.Parse(v, CultureInfo.InvariantCulture), g);
    }

    private async Task<byte[]> LireCorpsAsync(CancellationToken ct)
    {
        using var tampon = new MemoryStream();
        await Request.Body.CopyToAsync(tampon, ct).ConfigureAwait(false);
        return tampon.ToArray();
    }

    /// <summary>L'empreinte que l'index donne pour ce quadruplet, "" s'il n'en donne aucune, null si
    /// la plateforme ne repond pas.</summary>
    private async Task<string?> EmpreinteIndexeeAsync(Quadruplet q, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var url = NelfePlayAgentService.BaseUrl.TrimEnd('/') + "/api/v1/avatar"
                      + "?pseudo=" + Uri.EscapeDataString(q.Pseudo)
                      + "&family=" + Uri.EscapeDataString(q.Famille)
                      + "&generator=" + Uri.EscapeDataString(q.Generateur)
                      + "&variation=" + q.Variation.ToString(CultureInfo.InvariantCulture);
            using var reponse = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return null;
            var doc = JsonNode.Parse(await reponse.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)) as JsonObject;
            if (doc?["ok"]?.GetValue<bool>() != true) return "";
            return (doc["sha256"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Avatars : index de la plateforme injoignable.");
            return null;
        }
    }

    private const string PageDeRemise = """
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>NelfePlay</title>
        <style>
        html,body{margin:0;height:100%;background:#15102a;color:#efeaff;font:15px system-ui,sans-serif}
        body{display:grid;place-items:center}
        p{max-width:28rem;text-align:center;padding:1rem}
        </style>
        </head>
        <body>
        <p>Avatar : remise a la borne...</p>
        <script>
        (async () => {
          const RETOUR = __RETOUR__;
          const fin = (etat) => {
            const u = new URL(RETOUR);
            u.searchParams.set('avatar', etat);
            location.replace(u.toString());
          };
          // Le fragment porte la planche. On l'efface aussitot de l'historique.
          const brut = location.hash.slice(1);
          history.replaceState(null, '', location.pathname + location.search);
          let d;
          try { d = JSON.parse(decodeURIComponent(brut)); } catch (e) { return fin('bad_request'); }
          if (!d || typeof d.png !== 'string') { return fin('bad_request'); }
          let octets;
          try { octets = Uint8Array.from(atob(d.png), (c) => c.charCodeAt(0)); } catch (e) { return fin('bad_request'); }
          const q = new URLSearchParams({
            pseudo: String(d.pseudo || ''),
            family: String(d.family || ''),
            variation: String(d.variation == null ? 0 : d.variation),
            generator: String(d.generator || ''),
            sha256: String(d.sha256 || ''),
          });
          try {
            const r = await fetch('/nelfeplay/avatar/import?' + q, {
              method: 'POST',
              headers: { 'Content-Type': 'application/octet-stream' },
              body: octets,
            });
            const j = await r.json().catch(() => ({}));
            fin(j.ok === true ? 'stored' : (j.error || 'refused'));
          } catch (e) {
            fin('unreachable');
          }
        })();
        </script>
        </body>
        </html>
        """;
}
