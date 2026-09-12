using System.Text;
using System.Text.Json;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Remonte à la plateforme les réactions enregistrées pendant une lecture (CDC §57, §58,
/// CDC DEV §101.6).
///
/// Charge inverse de celle d'un objet, donc trajet inverse. Une réaction pèse une centaine
/// d'octets et son intérêt est d'être AGRÉGÉE : elle n'a que faire de l'essaim, elle monte par le
/// canal sortant déjà utilisé pour les scores. Aucun routeur à traverser.
///
/// L'envoi est IDEMPOTENT par construction : les réactions sont append-only et la plateforme les
/// dédoublonne sur (replay, compte, frame, réaction). On peut donc renvoyer le journal entier
/// sans rien dupliquer, ce qui rend la reprise après une coupure triviale : il n'y a rien à
/// retenir de ce qui a déjà été envoyé.
///
/// La borne n'envoie jamais d'identité de compte : chaque réaction porte le jeton OPAQUE du
/// spectateur, que la plateforme résout. Une réaction sans jeton n'est pas envoyée, elle
/// n'appartiendrait à personne.
/// </summary>
public sealed class ReplayReactionUploader : BackgroundService
{
    private const string Path = "/api/v1/agent/nelfenet/reactions";

    private readonly ReplayStore _store;
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplayReactionUploader> _logger;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _aRemonter = new();

    public ReplayReactionUploader(ReplayStore store,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IHttpClientFactory httpFactory,
        IConfiguration config, IEventBus bus, ILogger<ReplayReactionUploader> logger)
    {
        _store = store; _devices = devices; _httpFactory = httpFactory;
        _config = config; _bus = bus; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _bus.Subscribe<EventEnvelope>(OnBusEvent); } catch (Exception ex) { _logger.LogDebug(ex, "Replay : abonnement au bus impossible."); }

        // Au demarrage, tout ce qui attend : des reactions faites avant un arret de l'API, ou
        // avant que la fin de lecture ne porte l'identifiant du replay, ne doivent pas rester ici.
        try
        {
            foreach (var replayId in _store.ReplaysWithReactions()) _aRemonter.Enqueue(replayId);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : balayage des reactions en attente impossible."); }

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_aRemonter.TryDequeue(out var replayId))
            {
                try { await UploadAsync(replayId, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogDebug(ex, "Replay : remontée des réactions en erreur."); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public sealed record UploadResult(bool Sent, int Count, string? Reason);

    public async Task<UploadResult> UploadAsync(string replayId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(replayId)) return new UploadResult(false, 0, "replay_missing");

        var credential = _devices.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return new UploadResult(false, 0, "device_not_paired");

        // Les réactions d'une même séance partagent le jeton du spectateur. On regroupe par jeton
        // pour le cas, rare mais possible, d'un replay regardé par deux personnes à la suite.
        // Groupé par spectateur ET par séance : chaque séance est un lot que la plateforme
        // pourra supplanter, jamais fusionner avec le précédent.
        // Seulement ce qui n'est pas encore parti : la plateforme sait ignorer un doublon, mais
        // renvoyer tout l'historique a chaque fin de lecture serait du trafic pour rien.
        var dejaParties = _store.ReadReactionsSent(replayId);
        var parJeton = _store.ReadReactions(replayId)
            .Where(r => !string.IsNullOrWhiteSpace(r.ViewerToken))
            .GroupBy(r => (Token: r.ViewerToken!, Seq: r.SessionSeq))
            .Where(g => !dejaParties.Contains(ReplayStore.ReactionGroupKey(g.Key.Token, g.Key.Seq)))
            .ToList();

        if (parJeton.Count == 0) return new UploadResult(false, 0, "no_identified_reaction");

        var total = 0;
        foreach (var groupe in parJeton)
        {
            var corps = new
            {
                replay_id = replayId,
                viewer_token = groupe.Key.Token,
                session_seq = groupe.Key.Seq,
                reactions = groupe.Select(r => new { reaction = r.Reaction, frame = r.Frame, level = r.Level }).ToList(),
            };

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var baseUrl = _config["Replay:Share:TransitUrl"];
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;

                var client = _httpFactory.CreateClient();
                client.Timeout = Timeout.InfiniteTimeSpan;
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl!.TrimEnd('/') + Path)
                {
                    Content = new StringContent(JsonSerializer.Serialize(corps), Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-NelfePlay-Device", credential);

                using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Replay : réactions refusées ({Code}) pour {ReplayId}.", (int)response.StatusCode, replayId);
                    continue;
                }
                // Acceptees ou ecartees par le budget, elles sont chez la plateforme : on ne les
                // renverra plus. Un refus HTTP, lui, laisse le groupe pour la prochaine fois.
                _store.MarkReactionsSent(replayId, ReplayStore.ReactionGroupKey(groupe.Key.Token, groupe.Key.Seq));
                total += groupe.Count();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Replay : remontée des réactions impossible pour {ReplayId}.", replayId);
            }
        }

        if (total > 0) _logger.LogInformation("Replay : {Count} réaction(s) remontée(s) pour {ReplayId}.", total, replayId);
        return new UploadResult(total > 0, total, total > 0 ? null : "not_sent");
    }

    private void OnBusEvent(EventEnvelope e)
    {
        // À la fin d'une lecture : c'est le moment où le journal est complet, et où la machine
        // n'est plus occupée à jouer.
        if (!string.Equals(e.Type, "replay.finished", StringComparison.Ordinal)) return;
        try
        {
            var json = JsonSerializer.Serialize(e.Payload);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("replayId", out var id) && id.GetString() is { Length: > 0 } value)
                _aRemonter.Enqueue(value);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : fin de lecture illisible pour la remontée."); }
    }
}
