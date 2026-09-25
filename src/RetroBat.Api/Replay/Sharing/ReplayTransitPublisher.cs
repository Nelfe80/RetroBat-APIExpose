using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Envoie un replay au miroir de la plateforme, sur un geste EXPLICITE.
///
/// Rien ne part tout seul. La visibilité par défaut d'un replay est privée, et publier est une
/// décision du propriétaire de la borne, jamais un effet de bord de l'enregistrement ou du
/// scellement d'un score.
///
/// On monte l'objet ET son manifeste. Sans le manifeste, une borne qui n'a jamais vu ce replay
/// ne saurait pas quel core ni quelle ROM employer, et l'objet seul serait illisible. Le
/// manifeste est conçu pour ça : identifiants canoniques et empreintes, aucun chemin local,
/// aucune donnée de machine (CDC §1.6).
/// </summary>
public sealed class ReplayTransitPublisher
{
    private const string PublishPath = "/api/v1/agent/nelfenet/publish";
    private const string UnpublishPath = "/api/v1/agent/nelfenet/unpublish";

    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReplayTransitPublisher> _logger;

    public ReplayTransitPublisher(IReplayManifestStore manifests, IReplayObjectStore objects,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<ReplayTransitPublisher> logger)
    {
        _manifests = manifests; _objects = objects; _devices = devices;
        _config = config; _httpFactory = httpFactory; _logger = logger;
    }

    public sealed record PublishResult(bool Ok, string? Error = null);

    /// <summary>Le TRANSIT, c'est-à-dire la plateforme : elle reçoit et relaie vers l'amorce,
    /// elle ne sert jamais l'objet elle-même (CDC DEV §101.2).</summary>
    private string TransitBase()
    {
        var url = _config["Replay:Share:TransitUrl"];
        if (string.IsNullOrWhiteSpace(url)) url = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;
        return (url ?? string.Empty).TrimEnd('/');
    }

    /// <summary>
    /// L'amorce détient-elle déjà cet objet ? Une question posée par le HASH, donc sans
    /// ambiguïté : le nom de l'asset EST le hash. C'est le test d'achèvement du semis, et il ne
    /// demande de conserver aucun état local.
    /// </summary>
    public async Task<bool> IsOnSeedAsync(string sha256, CancellationToken ct)
    {
        var template = _config["Replay:Share:MirrorUrlTemplate"];
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{sha}", StringComparison.Ordinal)) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            // La forme compressée d'abord (les envois récents), puis la brute (les anciens).
            var brute = template.Replace("{sha}", sha256, StringComparison.Ordinal);
            foreach (var url in new[] { ReplayCompression.UrlCompressee(brute), brute })
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : amorce injoignable pour la verification de {Sha}.", sha256[..8]);
            return false;
        }
    }

    public async Task<PublishResult> PublishAsync(string replayId, CancellationToken ct)
    {
        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return new PublishResult(false, "REPLAY_NOT_FOUND");

        var objectPath = _objects.ObjectPath(manifest.Object.Sha256);
        if (!File.Exists(objectPath)) return new PublishResult(false, "REPLAY_OBJECT_UNAVAILABLE");

        var credential = _devices.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return new PublishResult(false, "DEVICE_NOT_PAIRED");

        var manifestPath = _manifests.ManifestPath(replayId);
        if (!File.Exists(manifestPath)) return new PublishResult(false, "REPLAY_MANIFEST_INVALID");

        string? compresse = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // un objet peut peser quelques Mo sur une liaison montante modeste

            // Le manifeste part TEL QU'ÉCRIT sur le disque : le re-sérialiser risquerait de
            // changer un octet et de casser l'identité que ce document porte.
            var manifestJson = await File.ReadAllTextAsync(manifestPath, cts.Token).ConfigureAwait(false);

            // Compressé au départ : un replay de RetroArch 1.22.2 tient en 1 à 2 % de sa taille, et
            // le transit refuse au-delà de 2 Mo. L'empreinte envoyée reste celle du BRUT.
            if (ReplayCompression.Active(_config))
            {
                Directory.CreateDirectory(_objects.TempRoot);
                compresse = Path.Combine(_objects.TempRoot, $"publish-{manifest.Object.Sha256}.replay.gz");
                var taille = await ReplayCompression.CompresserAsync(objectPath, compresse, cts.Token).ConfigureAwait(false);
                _logger.LogInformation("Replay : {ReplayId} compressé pour l'envoi, {Brut} -> {Gz} octets.",
                    replayId, manifest.Object.Size, taille);
            }

            var (ok, code, corps) = await EnvoyerAsync(manifestJson, replayId, manifest.Object.Sha256,
                compresse ?? objectPath, compresse is not null, credential, cts.Token).ConfigureAwait(false);

            // UN SERVEUR QUI NE CONNAÎT PAS ENCORE LA COMPRESSION hache le fichier reçu tel quel,
            // trouve l'empreinte du compressé et refuse. On renvoie alors le brut, une fois : la
            // borne mise à jour avant le site ne perd pas sa publication.
            if (!ok && compresse is not null && corps.Contains("object_hash_mismatch", StringComparison.Ordinal))
            {
                _logger.LogInformation("Replay : le transit ne lit pas encore les objets compressés, envoi du brut.");
                (ok, code, corps) = await EnvoyerAsync(manifestJson, replayId, manifest.Object.Sha256,
                    objectPath, false, credential, cts.Token).ConfigureAwait(false);
            }

            if (!ok)
            {
                _logger.LogWarning("Replay : publication refusée par le transit ({Code}) : {Body}", code, Trim(corps));
                return new PublishResult(false, "TRANSIT_REFUSED");
            }

            _logger.LogInformation("Replay : {ReplayId} poussé au transit ({Size} octets bruts).", replayId, manifest.Object.Size);
            return new PublishResult(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PublishResult(false, "TRANSIT_TIMEOUT");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : poussée vers le transit impossible.");
            return new PublishResult(false, "TRANSIT_UNAVAILABLE");
        }
        finally
        {
            if (compresse is not null)
            {
                try { File.Delete(compresse); } catch (IOException) { /* temporaire : le prochain envoi l'ecrase */ }
            }
        }
    }

    /// <summary>Un envoi au transit : l'objet (brut ou compressé), son manifeste et son empreinte BRUTE.</summary>
    private async Task<(bool Ok, int Code, string Corps)> EnvoyerAsync(string manifestJson, string replayId,
        string sha256, string fichier, bool compresse, string credential, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(manifestJson), "manifest" },
            { new StringContent(replayId), "replay_id" },
            { new StringContent(sha256), "object_sha256" },
        };
        if (compresse) content.Add(new StringContent(ReplayCompression.Encodage), "object_encoding");

        await using var stream = File.OpenRead(fichier);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            compresse ? "application/gzip" : "application/octet-stream");
        content.Add(file, "object", sha256 + ".replay" + (compresse ? ReplayCompression.Suffixe : ""));

        var client = _httpFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, TransitBase() + PublishPath) { Content = content };
        request.Headers.Add("X-NelfePlay-Device", credential);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var corps = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, corps);
    }

    public async Task<PublishResult> UnpublishAsync(string replayId, CancellationToken ct)
    {
        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return new PublishResult(false, "REPLAY_NOT_FOUND");

        var credential = _devices.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return new PublishResult(false, "DEVICE_NOT_PAIRED");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Post, TransitBase() + UnpublishPath)
            {
                Content = new StringContent($"{{\"object_sha256\":\"{manifest.Object.Sha256}\"}}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-NelfePlay-Device", credential);
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? new PublishResult(true) : new PublishResult(false, "TRANSIT_REFUSED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : retrait du transit impossible.");
            return new PublishResult(false, "TRANSIT_UNAVAILABLE");
        }
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}
