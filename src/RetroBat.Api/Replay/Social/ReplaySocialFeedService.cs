using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Social;

/// <summary>
/// Va chercher les événements sociaux d'un replay et les verse au journal local (LOT R9).
///
/// C'est la moitié « distribuée » du lot. La plateforme reste la source la plus commode, mais
/// elle n'est plus la seule possible : un événement porte sa preuve, donc un pair qui le relaie
/// vaut exactement le centre. On demande donc au centre d'abord, aux pairs ensuite, et on
/// s'arrête dès qu'on a quelque chose — la réunion est une union, il n'y a rien à arbitrer.
///
/// Le moment choisi est le LANCEMENT d'une lecture : les bulles doivent être prêtes quand
/// l'image démarre. On repasse à la fin, parce que nos propres réactions viennent d'être
/// remontées et nous reviennent signées.
///
/// Aucun événement n'est retenu sans vérification. Sans clé épinglée, on ne retient rien du
/// tout : mieux vaut un replay sans réactions d'autrui qu'un replay où n'importe qui écrit.
/// </summary>
public sealed class ReplaySocialFeedService : BackgroundService
{
    private const string CheminPlateforme = "/api/v1/social/events";
    private const string CheminResume = "/api/v1/social/summary";
    private const string CheminPair = "/api/v1/replay/social";

    private readonly ReplayStore _store;
    private readonly ReplaySocialStore _social;
    private readonly SocialIssuerPin _pin;
    private readonly ReplayPeerDirectory _peers;
    private readonly RetroBat.Api.Avatar.AvatarSheetStore _avatars;
    private readonly ReplayRelayService _relais;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplaySocialFeedService> _logger;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _aChercher = new();

    public ReplaySocialFeedService(ReplayStore store, ReplaySocialStore social, SocialIssuerPin pin,
        ReplayPeerDirectory peers, IHttpClientFactory httpFactory, IConfiguration config,
        IEventBus bus, ILogger<ReplaySocialFeedService> logger,
        RetroBat.Api.Avatar.AvatarSheetStore avatars, ReplayRelayService relais)
    {
        _store = store; _social = social; _pin = pin; _peers = peers;
        _httpFactory = httpFactory; _config = config; _bus = bus; _logger = logger;
        _avatars = avatars; _relais = relais;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _bus.Subscribe<EventEnvelope>(OnBusEvent); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay social : abonnement au bus impossible."); }

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_aChercher.TryDequeue(out var replayId))
            {
                try { await FetchAsync(replayId, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogDebug(ex, "Replay social : récupération en erreur."); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public sealed record FetchResult(int Fetched, int Added, string Source, string? Reason);

    /// <summary>Récupère, vérifie et fusionne. Ne jette jamais : une borne hors ligne affiche ce
    /// qu'elle a déjà, ce qui est le comportement attendu d'un système distribué.</summary>
    public async Task<FetchResult> FetchAsync(string replayId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(replayId)) return new FetchResult(0, 0, "", "replay_missing");

        var epingle = await _pin.EnsureAsync(ct).ConfigureAwait(false);
        if (epingle is null) return new FetchResult(0, 0, "", "issuer_unpinned");

        var objet = _store.GetManifest(replayId)?.Object.Sha256 ?? string.Empty;

        // Le RESUME d'abord : c'est lui que le HUD dessine (chaleur, moments, cameos). Les
        // evenements bruts restent pour l'audit et le dedoublonnage de nos propres reactions.
        await LireResumeAsync(replayId, epingle, ct).ConfigureAwait(false);

        var (events, source) = await LirePlateformeAsync(replayId, objet, ct).ConfigureAwait(false);
        if (events.Count == 0)
            (events, source) = await LirePairsAsync(replayId, objet, ct).ConfigureAwait(false);
        if (events.Count == 0) return new FetchResult(0, 0, source, "aucune_source");

        var retenus = new List<SocialEvent>();
        var refuses = 0;
        foreach (var e in events)
        {
            // La cible est vérifiée ici et pas dans le vérificateur : lui dit si l'événement est
            // authentique, nous disons s'il nous concerne. Un événement authentique visant un
            // AUTRE replay n'a rien à faire dans ce journal.
            if (!string.Equals(e.TargetId, replayId, StringComparison.Ordinal)) { refuses++; continue; }
            var refus = SocialEventVerifier.Check(e, epingle.Spki, epingle.KeyId);
            if (refus.Length > 0)
            {
                refuses++;
                _logger.LogWarning("Replay social : événement refusé ({Refus}) pour {ReplayId}.", refus, replayId);
                continue;
            }
            retenus.Add(e);
        }

        var ajoutes = _social.Merge(replayId, retenus);
        if (ajoutes > 0)
            _logger.LogInformation(
                "Replay social : {Ajoutes} nouvel(s) événement(s) sur {Recus} reçus de {Source} pour {ReplayId}.",
                ajoutes, events.Count, source, replayId);
        else if (refuses > 0)
            _logger.LogInformation("Replay social : {Refuses} événement(s) écarté(s) pour {ReplayId}.", refuses, replayId);

        return new FetchResult(retenus.Count, ajoutes, source, null);
    }

    /// <summary>
    /// Le resume signe de la plateforme : verifie, range, et les planches des cameos demandees au
    /// relais si la borne ne les a pas. Un resume refuse ne remplace pas celui qu'on avait.
    /// </summary>
    private async Task LireResumeAsync(string replayId, SocialIssuerPin.Pin epingle, CancellationToken ct)
    {
        var baseUrl = _config["Replay:Share:TransitUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        JsonObject? enveloppe;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var url = baseUrl.TrimEnd('/') + CheminResume + "?replay_id=" + Uri.EscapeDataString(replayId);
            using var reponse = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return;   // 404 : pas encore de reaction, rien a dessiner
            enveloppe = JsonNode.Parse(await reponse.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay social : resume injoignable pour {ReplayId}.", replayId);
            return;
        }
        if (enveloppe is null) return;

        var (resume, refus) = SocialSummary.Verifier(enveloppe, epingle.Spki, epingle.KeyId);
        if (resume is null)
        {
            _logger.LogWarning("Replay social : resume refuse ({Refus}) pour {ReplayId}.", refus, replayId);
            return;
        }
        if (!string.Equals(resume.TargetId, replayId, StringComparison.Ordinal)) return;

        _social.SaveSummary(replayId, enveloppe);
        _logger.LogInformation(
            "Replay social : resume de {ReplayId} recu ({Reactions} reaction(s), {Spectateurs} spectateur(s), {Cameos} cameo(s)).",
            replayId, resume.TotalReactions, resume.TotalSpectateurs, resume.Cameos.Count);

        // Les planches des cameos : demandees maintenant, pour etre la a la frame.
        var demandees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in resume.Cameos)
        {
            var sha = c.Avatar.Planche;
            if (sha is null || _avatars.Has(sha) || !demandees.Add(sha)) continue;
            try { await _relais.RequestAsync(sha, ct, "avatar").ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay social : planche {Sha} non demandee.", sha[..12]); }
        }
    }

    private async Task<(IReadOnlyList<SocialEvent>, string)> LirePlateformeAsync(
        string replayId, string objet, CancellationToken ct)
    {
        var baseUrl = _config["Replay:Share:TransitUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return (Array.Empty<SocialEvent>(), "");

        var url = baseUrl.TrimEnd('/') + CheminPlateforme + "?replay_id=" + Uri.EscapeDataString(replayId);
        if (objet.Length == 64) url += "&object_sha256=" + objet;
        var events = await LireAsync(url, null, ct).ConfigureAwait(false);
        return (events, events.Count > 0 ? "plateforme" : "");
    }

    private async Task<(IReadOnlyList<SocialEvent>, string)> LirePairsAsync(
        string replayId, string objet, CancellationToken ct)
    {
        IReadOnlyList<ReplayPeer> pairs;
        try { pairs = await _peers.PeersAsync(ct).ConfigureAwait(false); }
        catch { return (Array.Empty<SocialEvent>(), ""); }

        foreach (var pair in pairs)
        {
            // Un miroir sert des OCTETS nommés par leur hash, pas une API de borne : l'interroger
            // ici ne donnerait qu'un 404 de plus.
            if (pair.Source.Contains(MirrorPeerSource.SourceTag, StringComparison.Ordinal)) continue;

            var url = pair.BaseUrl.TrimEnd('/') + CheminPair + "?replay_id=" + Uri.EscapeDataString(replayId);
            if (objet.Length == 64) url += "&object_sha256=" + objet;
            var events = await LireAsync(url, pair.ApiKey, ct).ConfigureAwait(false);
            if (events.Count > 0) return (events, "pair " + pair.Name);
        }
        return (Array.Empty<SocialEvent>(), "");
    }

    private async Task<IReadOnlyList<SocialEvent>> LireAsync(string url, string? apiKey, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Add("X-Api-Key", apiKey);

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<SocialEvent>();

            var corps = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (JsonNode.Parse(corps) is not JsonObject doc || doc["events"] is not JsonArray tableau)
                return Array.Empty<SocialEvent>();

            var liste = new List<SocialEvent>(tableau.Count);
            foreach (var item in tableau)
            {
                var e = SocialEvent.FromJson(item);
                if (e is not null) liste.Add(e);
            }
            return liste;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay social : flux illisible ({Url}).", url);
            return Array.Empty<SocialEvent>();
        }
    }

    private void OnBusEvent(EventEnvelope e)
    {
        // Au LANCEMENT, pour que les bulles soient prêtes quand l'image démarre ; à la FIN, parce
        // que nos propres réactions viennent de remonter et nous reviennent signées.
        if (e.Type is not ("replay.launching" or "replay.finished")) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(e.Payload);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("replayId", out var id) && id.GetString() is { Length: > 0 } valeur)
                _aChercher.Enqueue(valeur);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay social : évènement de lecture illisible."); }
    }
}
