using System.Text;
using System.Text.Json;
using RetroBat.Api.Avatar;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Déclare à la plateforme ce que cette borne CONSERVE (CDC §54, §86).
///
/// Sans ce recensement, « durable » et « degraded » restent des mots : le §54 fixe des objectifs
/// (cinq copies dans deux régions pour un Top 50, dix dans trois pour un record du monde) que rien
/// ne permet d'évaluer tant que personne ne compte.
///
/// Ce qu'on déclare est la PRÉSERVATION, pas la joignabilité. Une borne chez un particulier n'est
/// de toute façon pas atteignable en entrant ; la question à laquelle ce recensement répond est
/// « si cette machine disparaît, ce record existe-t-il encore ailleurs ».
///
/// La déclaration est un ENVOI SORTANT, comme les scores : aucun routeur à configurer. Elle ne
/// contient que des hashes, jamais un nom de fichier, un chemin, ni quoi que ce soit de la
/// machine. Et ce que la borne ne déclare plus, elle ne le détient plus : sans cet oubli, un
/// compte ne ferait que monter et finirait par décrire un réseau qui n'existe pas.
/// </summary>
public sealed class ReplayHoldingsReporter : BackgroundService
{
    private const string Path = "/api/v1/agent/nelfenet/holdings";
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(6);
    private static readonly TimeSpan PremierEssai = TimeSpan.FromMinutes(3);

    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly ReplaySharePolicy _policy;
    private readonly AvatarSheetStore _avatars;
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ReplayHoldingsReporter> _logger;

    public ReplayHoldingsReporter(IReplayManifestStore manifests, IReplayObjectStore objects,
        IReplayMetadataStore meta, ReplaySharePolicy policy, AvatarSheetStore avatars,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IHttpClientFactory httpFactory,
        IConfiguration config, ILogger<ReplayHoldingsReporter> logger)
    {
        _manifests = manifests; _objects = objects; _meta = meta; _policy = policy; _avatars = avatars;
        _devices = devices; _httpFactory = httpFactory; _config = config; _logger = logger;
    }

    /// <summary>Déclarer, c'est se signaler. On ne le fait pas sans que le partage ait été activé :
    /// une borne qui ne participe pas n'a aucune raison d'apparaître dans un recensement.</summary>
    private bool ReplaysParticipent => _config.GetValue("Replay:Share:Enabled", false)
                                       || _config.GetValue("Replay:Replication:Enabled", false);

    /// <summary>
    /// Les planches d'avatar se partagent PAR DÉFAUT, et ce n'est pas la même décision que pour un
    /// replay : une planche se fabrique à partir d'un pseudo et d'une famille déjà publics, elle est
    /// faite pour être vue de tous les spectateurs, et sans détenteur déclaré aucune autre borne ne
    /// peut la recevoir. `Avatar:Share:Enabled=false` la retire du recensement.
    /// </summary>
    private bool AvatarsPartages => _config.GetValue("Avatar:Share:Enabled", true);

    private bool Enabled => ReplaysParticipent || AvatarsPartages;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReportAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : déclaration de conservation en erreur."); }

            try { await Task.Delay(Cadence, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public sealed record ReportResult(bool Sent, int Declared, string? Reason);

    public async Task<ReportResult> ReportAsync(CancellationToken ct)
    {
        if (!Enabled) return new ReportResult(false, 0, "not_participating");

        var credential = _devices.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return new ReportResult(false, 0, "device_not_paired");

        var partage = _policy.SharingEnabled;
        var objets = new List<object>();
        if (ReplaysParticipent)
        {
            foreach (var manifest in _manifests.ListManifests())
            {
                if (!File.Exists(_objects.ObjectPath(manifest.Object.Sha256))) continue;
                var visibilite = _meta.GetMeta(manifest.ReplayId)?.Visibility ?? "private";
                objets.Add(new
                {
                    sha256 = manifest.Object.Sha256,
                    // Servable par CETTE borne, ce qui est une autre question que « conservée ».
                    shareable = partage && string.Equals(visibilite, "public", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        // Les planches dans le MÊME envoi : la plateforme oublie tout ce qu'une borne ne déclare
        // plus, et deux envois séparés s'effaceraient l'un l'autre à chaque passage.
        if (AvatarsPartages)
        {
            foreach (var sha in _avatars.ADeclarer())
            {
                objets.Add(new { sha256 = sha, shareable = true });
            }
        }

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
                Content = new StringContent(JsonSerializer.Serialize(new { objects = objets }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-NelfePlay-Device", credential);

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ReportResult(false, objets.Count, "http_" + (int)response.StatusCode);

            _logger.LogInformation("Replay : {Count} objet(s) déclaré(s) au recensement.", objets.Count);
            return new ReportResult(true, objets.Count, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : recensement injoignable.");
            return new ReportResult(false, objets.Count, "unreachable");
        }
    }
}
