using System.Text.Json;
using RetroBat.Api.Replay.Models;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Va chercher sur la plateforme le manifeste d'un replay que la borne n'a pas (ou plus).
///
/// Le cas : une borne a purge ses replays, le manifeste est parti avec, mais le classement
/// pointe toujours le replay parce que le miroir le detient. Sans manifeste, la borne ne sait
/// ni quel objet demander ni comment le lire, et refusait avant tout reseau. La plateforme
/// garde le manifeste de tout replay seme au miroir : on le lui demande, on verifie qu'il parle
/// bien du replay demande, et la lecture suit son chemin normal (objet par les pairs ou le
/// miroir, puis verification d'integrite).
/// </summary>
public sealed class ReplayManifestFetcher
{
    private static readonly TimeSpan Delai = TimeSpan.FromSeconds(15);
    // Les memes options que le magasin et la replication : un manifeste s'ecrit en snake_case,
    // et un lecteur camelCase n'y trouverait ni replay_id ni object.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReplayManifestFetcher> _logger;

    public ReplayManifestFetcher(IHttpClientFactory httpFactory, ILogger<ReplayManifestFetcher> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Le manifeste, ou null si la plateforme ne le connait pas ou ne repond pas.</summary>
    public async Task<ReplayManifest?> FetchAsync(string replayId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(replayId) || replayId.Length > 40)
        {
            return null;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Delai);
            var url = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl.TrimEnd('/')
                      + "/api/v1/replay/" + Uri.EscapeDataString(replayId) + "/manifest";
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var reponse = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("Replay : la plateforme n'a pas de manifeste pour {ReplayId} (HTTP {Code}).",
                    replayId, (int) reponse.StatusCode);
                return null;
            }
            var corps = await reponse.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<ReplayManifest>(corps, Json);
            if (manifest is null || !string.Equals(manifest.ReplayId, replayId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(manifest.Object?.Sha256))
            {
                // Un manifeste qui ne parle pas du replay demande ne sert a rien, et en faire une
                // lecture serait jouer autre chose que ce qu'on a demande.
                _logger.LogWarning("Replay : manifeste incoherent recu pour {ReplayId}, ignore.", replayId);
                return null;
            }
            _logger.LogInformation("Replay : manifeste de {ReplayId} recu de la plateforme.", replayId);
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Replay : manifeste de {ReplayId} injoignable.", replayId);
            return null;
        }
    }
}
