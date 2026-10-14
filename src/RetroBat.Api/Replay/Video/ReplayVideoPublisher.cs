using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LES FILMS DES RECORDS DE CETTE BORNE (CDC video V2, §4.1). Le serveur dit quoi filmer, la borne rend le
/// film, le serveur ouvre une session d'envoi sur la chaine officielle, et la borne envoie le fichier
/// directement chez Google : le serveur ne voit passer aucun octet de video. L'adresse de session vaut
/// autorisation pour cette seule video : elle ne se journalise pas.
/// </summary>
public sealed class ReplayVideoPublisher
{
    private readonly ReplayVideoRenderer _rendu;
    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReplayVideoPublisher> _logger;

    public ReplayVideoPublisher(ReplayVideoRenderer rendu, IReplayManifestStore manifests, IReplayObjectStore objects,
        NelfePlayDeviceStore devices, IHttpClientFactory httpFactory, ILogger<ReplayVideoPublisher> logger)
    {
        _rendu = rendu; _manifests = manifests; _objects = objects; _devices = devices; _httpFactory = httpFactory;
        _logger = logger;
    }

    public sealed record Cible(string ReplayId, long? Debut, long? Fin);

    public sealed record Rapport(int Cibles, int Envoyees, IReadOnlyList<string> Details);

    /// <summary>Filme et envoie au plus <paramref name="limite"/> records de cette borne.</summary>
    public async Task<Rapport> PublierAsync(int limite, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return new Rapport(0, 0, new[] { "borne non appairee" });

        // Demander la liste suffit au site pour finaliser les films deja envoyes (visibilite, playlist) :
        // une limite de 0 ne fait que cela.
        var cibles = await CiblesAsync(credential, ct).ConfigureAwait(false);
        var details = new List<string>();
        var envoyees = 0;
        foreach (var cible in cibles)
        {
            if (envoyees >= limite) break;
            var manifest = _manifests.GetManifest(cible.ReplayId);
            if (manifest is null || !_objects.HasObject(manifest.Object.Sha256))
            {
                details.Add(cible.ReplayId + " : replay absent de cette borne");
                continue;
            }

            var film = await _rendu.RendreAsync(cible.ReplayId, cible.Debut, cible.Fin, ct).ConfigureAwait(false);
            if (!film.Ok || film.Fichier is null)
            {
                details.Add(cible.ReplayId + " : rendu " + (film.Raison ?? "incomplet"));
                continue;
            }

            try
            {
                var videoId = await EnvoyerAsync(credential, cible.ReplayId, film.Fichier, ct).ConfigureAwait(false);
                if (videoId is null)
                {
                    details.Add(cible.ReplayId + " : envoi refuse");
                    continue;
                }
                var confirme = await PosterAsync(credential, "videos/complete", new JsonObject
                {
                    ["replay_id"] = cible.ReplayId,
                    ["youtube_video_id"] = videoId,
                    ["run_start_frame"] = film.Debut,
                    ["run_end_frame"] = film.Fin,
                    ["fps"] = manifest.Frames.NominalFps,
                }, ct).ConfigureAwait(false);
                details.Add(cible.ReplayId + " : envoyee (" + videoId + ")" + (confirme is null ? ", confirmation refusee" : ""));
                if (confirme is not null) envoyees++;
            }
            finally
            {
                // Le film n'est plus utile : envoye, ou a refaire au prochain passage.
                try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(film.Fichier)!)!, recursive: true); }
                catch { /* zone de travail */ }
            }
        }
        _logger.LogInformation("Video : {Envoyees} film(s) envoye(s) sur {Cibles} cible(s). {Details}",
            envoyees, cibles.Count, string.Join(" | ", details));
        return new Rapport(cibles.Count, envoyees, details);
    }

    private async Task<IReadOnlyList<Cible>> CiblesAsync(string credential, CancellationToken ct)
    {
        try
        {
            using var client = Client(credential, TimeSpan.FromSeconds(60));
            using var reponse = await client.GetAsync("/api/v1/agent/videos/targets", ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return Array.Empty<Cible>();
            using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("targets", out var liste)) return Array.Empty<Cible>();
            var sortie = new List<Cible>();
            foreach (var c in liste.EnumerateArray())
            {
                var id = c.TryGetProperty("replay_id", out var r) ? r.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                sortie.Add(new Cible(id!, Nombre(c, "run_start_frame"), Nombre(c, "run_end_frame")));
            }
            return sortie;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video : liste des records a filmer injoignable.");
            return Array.Empty<Cible>();
        }
    }

    /// <summary>Ouvre la session, envoie le film chez Google, rend l'identifiant YouTube (ou null).</summary>
    private async Task<string?> EnvoyerAsync(string credential, string replayId, string fichier, CancellationToken ct)
    {
        var taille = new FileInfo(fichier).Length;
        var session = await PosterAsync(credential, "videos/upload-session", new JsonObject
        {
            ["replay_id"] = replayId,
            ["size"] = taille,
            ["type"] = "video/x-matroska",
        }, ct).ConfigureAwait(false);
        var adresse = (string?)session?["session"];
        if (string.IsNullOrEmpty(adresse)) return null;

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(30);
        await using var flux = File.OpenRead(fichier);
        using var contenu = new StreamContent(flux);
        contenu.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        contenu.Headers.ContentLength = taille;
        using var reponse = await http.PutAsync(adresse, contenu, ct).ConfigureAwait(false);
        var corps = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!reponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Video : envoi de {ReplayId} refuse par Google ({Statut}).", replayId, (int)reponse.StatusCode);
            return null;
        }
        return (string?)(JsonNode.Parse(corps) as JsonObject)?["id"];
    }

    private async Task<JsonObject?> PosterAsync(string credential, string route, JsonObject corps, CancellationToken ct)
    {
        using var client = Client(credential, TimeSpan.FromSeconds(60));
        using var contenu = new StringContent(corps.ToJsonString(), Encoding.UTF8, "application/json");
        using var reponse = await client.PostAsync("/api/v1/agent/" + route, contenu, ct).ConfigureAwait(false);
        var texte = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!reponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Video : {Route} refuse ({Statut}) : {Corps}", route, (int)reponse.StatusCode,
                texte.Length > 200 ? texte[..200] : texte);
            return null;
        }
        return JsonNode.Parse(texte) as JsonObject;
    }

    private HttpClient Client(string credential, TimeSpan delai)
    {
        var client = _httpFactory.CreateClient();
        client.Timeout = delai;
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
        return client;
    }

    private static long? Nombre(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
