using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LES FILMS DES RECORDS (CDC video V2, §4.1). Le serveur dit quoi filmer, la borne rend le film, le serveur
/// ouvre une session d'envoi sur la chaine officielle, et la borne envoie le fichier directement chez Google :
/// le serveur ne voit passer aucun octet de video. L'adresse de session vaut autorisation pour cette seule
/// video : elle ne se journalise pas.
///
/// Le serveur donne aussi son ROLE a la borne (decision du 14 octobre 2026) : une station de rendu filme le haut
/// de tous les classements et va chercher les replays des autres comme pour une lecture ; la borne d'un joueur
/// ne filme rien, ou ses propres records si le serveur le lui demande.
/// </summary>
public sealed class ReplayVideoPublisher
{
    public const string RoleStation = "station";
    public const string RoleJoueur = "player";
    public const string RoleAucun = "none";

    private readonly ReplayVideoRenderer _rendu;
    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly ReplayManifestFetcher _manifestFetcher;
    private readonly IReplaySourceResolver _sources;
    private readonly NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReplayVideoPublisher> _logger;

    public ReplayVideoPublisher(ReplayVideoRenderer rendu, IReplayManifestStore manifests, IReplayObjectStore objects,
        IReplayMetadataStore meta, ReplayManifestFetcher manifestFetcher, IReplaySourceResolver sources,
        NelfePlayDeviceStore devices, IHttpClientFactory httpFactory, ILogger<ReplayVideoPublisher> logger)
    {
        _rendu = rendu; _manifests = manifests; _objects = objects; _meta = meta; _manifestFetcher = manifestFetcher;
        _sources = sources; _devices = devices; _httpFactory = httpFactory; _logger = logger;
    }

    public sealed record Cible(string ReplayId, long? Debut, long? Fin);

    /// <summary>Ce que le site demande a cette borne.</summary>
    /// <param name="Role"><see cref="RoleStation"/>, <see cref="RoleJoueur"/> ou <see cref="RoleAucun"/> ; null
    /// quand le site n'a pas repondu (ou une borne non appairee).</param>
    public sealed record Liste(string? Role, IReadOnlyList<Cible> Cibles);

    /// <param name="Rendus">Les rendus tentes : zero quand rien de ce que le site demande n'est filmable ici.</param>
    /// <param name="EnvoiRefuse">Le site ou Google ont refuse un envoi (quota du jour, par exemple).</param>
    public sealed record Rapport(int Cibles, int Rendus, int Envoyees, IReadOnlyList<string> Details,
        bool EnvoiRefuse = false);

    /// <summary>
    /// Une borne occupee n'est pas un echec du replay : ces rendus-la ne se signalent pas, le prochain passage
    /// reprendra.
    /// </summary>
    private static readonly HashSet<string> RaisonsPassageres = new(StringComparer.Ordinal)
    {
        "lecture_en_cours", "emulateur_en_cours", "rendu_en_cours",
    };

    /// <summary>Filme et envoie au plus <paramref name="limite"/> records de ce que le site demande.</summary>
    public async Task<Rapport> PublierAsync(int limite, CancellationToken ct)
        => await FilmerAsync(await ListerAsync(ct).ConfigureAwait(false), limite, ct).ConfigureAwait(false);

    /// <summary>
    /// Le role de cette borne et ce qu'elle doit filmer. Demander la liste suffit au site pour finaliser les
    /// films deja envoyes (visibilite, playlist).
    /// </summary>
    public async Task<Liste> ListerAsync(CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return new Liste(null, Array.Empty<Cible>());
        return await CiblesAsync(credential, ct).ConfigureAwait(false);
    }

    /// <summary>Filme et envoie au plus <paramref name="limite"/> records de la liste.</summary>
    public async Task<Rapport> FilmerAsync(Liste liste, int limite, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return new Rapport(0, 0, 0, new[] { "borne non appairee" });

        var cibles = liste.Cibles;
        var details = new List<string>();
        var envoyees = 0;
        var rendus = 0;
        var refuse = false;
        foreach (var cible in cibles)
        {
            // La limite compte les rendus TENTES, pas les envois reussis : un site qui refuse ne doit pas
            // faire filmer toute la liste (vecu le 14 octobre, une erreur du site et dix rendus de suite).
            if (rendus >= limite) break;
            var manifest = _manifests.GetManifest(cible.ReplayId);
            if (manifest is null || !_objects.HasObject(manifest.Object.Sha256))
            {
                // Une station filme les records des autres : elle va chercher leur replay.
                manifest = liste.Role == RoleStation
                    ? await AcquerirAsync(cible.ReplayId, manifest, ct).ConfigureAwait(false)
                    : null;
                if (manifest is null)
                {
                    details.Add(cible.ReplayId + " : replay absent de cette borne");
                    await SignalerEchecAsync(credential, cible.ReplayId, "replay_absent", ct).ConfigureAwait(false);
                    continue;
                }
            }

            rendus++;
            var dossier = Path.Combine(_objects.TempRoot, "video", cible.ReplayId);
            try
            {
                var film = await _rendu.RendreAsync(cible.ReplayId, cible.Debut, cible.Fin, ct).ConfigureAwait(false);
                if (!film.Ok || film.Fichier is null)
                {
                    var raison = film.Raison ?? "incomplet";
                    details.Add(cible.ReplayId + " : rendu " + raison);
                    // Sinon le meme echec reviendrait en tete a chaque passage et bloquerait la file : le site
                    // le compte comme un essai, le repropose plus tard, puis l'abandonne.
                    if (!RaisonsPassageres.Contains(raison))
                        await SignalerEchecAsync(credential, cible.ReplayId, "rendu_" + raison, ct).ConfigureAwait(false);
                    continue;
                }

                var videoId = await EnvoyerAsync(credential, cible.ReplayId, film.Fichier, ct).ConfigureAwait(false);
                if (videoId is null)
                {
                    // Session refusee par le site ou envoi refuse par Google : on s'arrete la, le prochain
                    // passage reprendra.
                    details.Add(cible.ReplayId + " : envoi refuse");
                    refuse = true;
                    break;
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
                // Le film n'est plus utile : envoye, a refaire au prochain passage, ou interrompu.
                try { if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true); }
                catch { /* zone de travail */ }
            }
        }
        _logger.LogInformation("Video : {Envoyees} film(s) envoye(s) sur {Cibles} cible(s). {Details}",
            envoyees, cibles.Count, string.Join(" | ", details));
        return new Rapport(cibles.Count, rendus, envoyees, details, refuse);
    }

    /// <summary>
    /// Ce replay ne se filme pas ici : le site le compte comme un essai. Sans reponse, rien de grave, il
    /// reviendra au prochain passage.
    /// </summary>
    private async Task SignalerEchecAsync(string credential, string replayId, string raison, CancellationToken ct)
    {
        try
        {
            await PosterAsync(credential, "videos/failed", new JsonObject
            {
                ["replay_id"] = replayId,
                ["reason"] = raison,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Video : echec de {ReplayId} non signale.", replayId);
        }
    }

    private async Task<Liste> CiblesAsync(string credential, CancellationToken ct)
    {
        try
        {
            using var client = Client(credential, TimeSpan.FromSeconds(60));
            using var reponse = await client.GetAsync("/api/v1/agent/videos/targets", ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return new Liste(null, Array.Empty<Cible>());
            return LireLaListe(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Video : liste des records a filmer injoignable.");
            return new Liste(null, Array.Empty<Cible>());
        }
    }

    /// <summary>
    /// La reponse du site. Un site d'avant les roles ne dit rien : la borne filme alors ses propres records,
    /// comme avant. Pur, pour etre teste.
    /// </summary>
    internal static Liste LireLaListe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var racine = doc.RootElement;
        var role = racine.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() switch
            {
                RoleStation => RoleStation,
                RoleAucun => RoleAucun,
                _ => RoleJoueur,
            }
            : RoleJoueur;
        var sortie = new List<Cible>();
        if (racine.TryGetProperty("targets", out var liste) && liste.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in liste.EnumerateArray())
            {
                var id = c.TryGetProperty("replay_id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                sortie.Add(new Cible(id!, Nombre(c, "run_start_frame"), Nombre(c, "run_end_frame")));
            }
        }
        return new Liste(role, sortie);
    }

    /// <summary>
    /// Le replay d'une autre borne, recupere comme pour une lecture : le manifeste aupres de la plateforme,
    /// l'objet aupres des miroirs et des noeuds, verifie avant d'etre range (et encore au rendu). Null s'il
    /// n'arrive pas.
    /// </summary>
    private async Task<ReplayManifest?> AcquerirAsync(string replayId, ReplayManifest? manifest, CancellationToken ct)
    {
        try
        {
            if (manifest is null)
            {
                manifest = await _manifestFetcher.FetchAsync(replayId, ct).ConfigureAwait(false);
                if (manifest is null) return null;
                _manifests.SaveManifest(manifest);
                if (_meta.GetMeta(replayId) is null)
                    _meta.SaveMeta(ReplayLocalMetadata.Fresh(replayId) with { CreatedByThisDevice = false });
            }
            var trouve = await _sources.FindObjectAsync(manifest, ct).ConfigureAwait(false);
            if (trouve == ReplayObjectSearch.Present && _objects.HasObject(manifest.Object.Sha256)) return manifest;
            _logger.LogInformation("Video : replay {ReplayId} pas encore disponible pour la station ({Etat}).", replayId, trouve);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Video : recuperation du replay {ReplayId} echouee.", replayId);
            return null;
        }
    }

    /// <summary>Ce que devient une capture apres sa demande d'envoi.</summary>
    public enum IssueDeCapture
    {
        /// <summary>Envoyee chez Google et confirmee au site.</summary>
        Envoyee,
        /// <summary>Pas un record, deja filme ou pas a cette borne : le film peut etre efface.</summary>
        Inutile,
        /// <summary>Pas encore decidable (score ou replay pas encore arrives), quota, reseau : on reessaiera.</summary>
        PlusTard,
    }

    /// <summary>
    /// Les refus qui rendent une capture inutile pour toujours. « replay.not_owned » n'en est pas un : juste
    /// apres la partie, le replay n'est pas encore relie au score (vecu le 14 octobre, Metal Slug 3 : demande
    /// 0,2 s apres la fin, replay finalise 5 s plus tard).
    /// </summary>
    private static readonly HashSet<string> RefusDefinitifs = new(StringComparer.Ordinal)
    {
        "pas_un_record", "deja_filme", "taille",
    };

    /// <summary>Ce que donne une demande d'envoi de capture : l'issue, son detail, et la video si elle existe.</summary>
    public sealed record ResultatDeCapture(IssueDeCapture Issue, string Detail, string? VideoId = null, bool Confirmee = false);

    /// <summary>
    /// LE FILM D'UNE PARTIE, CAPTE PENDANT LE JEU (decision du 14 octobre 2026). Le site dit si la partie est un
    /// record ; si oui, il ouvre la session d'envoi et la borne envoie le film directement chez Google, puis le
    /// confirme avec la portion filmee du replay (<paramref name="debutFilm"/> et <paramref name="finFilm"/>, en
    /// frames du replay). Le film d'une partie sans replay porte un identifiant a lui et les <paramref name="sessions"/>
    /// de ses scores, par lesquelles le site la retrouve.
    /// <para>L'ENVOI SE REPREND (remarque du 14 octobre 2026) : <paramref name="session"/> est celle d'un envoi
    /// precedent, ou null ; Google dit ce qu'il a deja recu et l'envoi repart de la, sans rouvrir de session ni
    /// recompter le quota. <paramref name="memoriser"/> garde la session sur la borne des son ouverture, pour
    /// qu'une partie lancee, une borne eteinte ou une coupure du reseau ne perdent rien.</para>
    /// </summary>
    public async Task<ResultatDeCapture> EnvoyerCaptureAsync(string replayId, string fichier, long debutFilm, long finFilm,
        double fps, IReadOnlyList<string>? sessions, string? session, Func<string?, Task> memoriser, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return new(IssueDeCapture.PlusTard, "borne non appairee");
        var taille = new FileInfo(fichier).Length;

        // Pas de redirection automatique : Google repond 308 « reprise incomplete » pendant un envoi.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        string? videoId = null;
        long dejaRecus = 0;
        if (!string.IsNullOrEmpty(session))
        {
            var (recus, id) = await EtatDeLaSessionAsync(http, session, taille, ct).ConfigureAwait(false);
            if (recus < 0)
            {
                // Session expiree ou perdue chez Google : on en rouvre une.
                session = null;
                await memoriser(null).ConfigureAwait(false);
            }
            else
            {
                dejaRecus = recus;
                videoId = id;
                if (dejaRecus > 0 && videoId is null)
                    _logger.LogInformation("Video : envoi de {ReplayId} repris a {Recus} octets sur {Taille}.", replayId, dejaRecus, taille);
            }
        }
        if (string.IsNullOrEmpty(session))
        {
            var demande = new JsonObject
            {
                ["replay_id"] = replayId,
                ["size"] = taille,
                ["type"] = "video/x-matroska",
                ["source"] = "capture",
            };
            // Le film d'une partie sans replay : le site retrouve la partie par les sessions de ses scores.
            if (sessions is { Count: > 0 })
                demande["sessions"] = new JsonArray(sessions.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            var (adresse, raison) = await OuvrirSessionAsync(credential, demande, ct).ConfigureAwait(false);
            if (adresse is null)
                return new(raison is not null && RefusDefinitifs.Contains(raison) ? IssueDeCapture.Inutile : IssueDeCapture.PlusTard,
                    raison ?? "site injoignable");
            session = adresse;
            await memoriser(session).ConfigureAwait(false);
        }

        videoId ??= await EnvoyerLeResteAsync(http, session, replayId, fichier, dejaRecus, taille, ct).ConfigureAwait(false);
        if (videoId is null) return new(IssueDeCapture.PlusTard, "envoi incomplet, repris au prochain passage");
        var confirmee = await ConfirmerAsync(replayId, videoId, debutFilm, finFilm, fps, ct).ConfigureAwait(false);
        return new(IssueDeCapture.Envoyee, videoId + (confirmee ? "" : ", confirmation a refaire"), videoId, confirmee);
    }

    /// <summary>Dit au site que la video est chez Google, avec la portion filmee du replay.</summary>
    public async Task<bool> ConfirmerAsync(string replayId, string videoId, long debutFilm, long finFilm, double fps,
        CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return false;
        try
        {
            return await PosterAsync(credential, "videos/complete", new JsonObject
            {
                ["replay_id"] = replayId,
                ["youtube_video_id"] = videoId,
                ["run_start_frame"] = debutFilm,
                ["run_end_frame"] = finFilm,
                ["fps"] = fps,
            }, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Video : confirmation de {ReplayId} non passee.", replayId);
            return false;
        }
    }

    /// <summary>
    /// L'etat du film d'un replay au site : « ready », « processing », « uploading », « failed », « none » ; null si
    /// le site ne repond pas.
    /// </summary>
    public async Task<string?> EtatDuFilmAsync(string replayId, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return null;
        try
        {
            using var client = Client(credential, TimeSpan.FromSeconds(30));
            using var reponse = await client.GetAsync("/api/v1/agent/videos/state?replay_id=" + Uri.EscapeDataString(replayId), ct)
                .ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return null;
            var corps = JsonNode.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            return (string?)corps?["status"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Video : etat du film de {ReplayId} inconnu.", replayId);
            return null;
        }
    }

    /// <summary>
    /// Ce que Google a deja recu de cette session : le nombre d'octets, et l'identifiant de la video si l'envoi est
    /// complet. -1 si la session n'existe plus.
    /// </summary>
    private async Task<(long Recus, string? VideoId)> EtatDeLaSessionAsync(HttpClient http, string adresse, long taille,
        CancellationToken ct)
    {
        try
        {
            using var requete = new HttpRequestMessage(HttpMethod.Put, adresse) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            requete.Content.Headers.ContentRange = new ContentRangeHeaderValue(taille);
            using var reponse = await http.SendAsync(requete, ct).ConfigureAwait(false);
            if ((int)reponse.StatusCode == 308)
                return (OctetsRecus(reponse.Headers.TryGetValues("Range", out var plages) ? plages.FirstOrDefault() : null), null);
            if (reponse.IsSuccessStatusCode)
                return (taille, (string?)(JsonNode.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject)?["id"]);
            return (-1, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Video : session d'envoi illisible, une autre sera ouverte.");
            return (-1, null);
        }
    }

    /// <summary>
    /// Les octets deja recus d'apres l'en-tete Range d'une reprise (« bytes=0-1048575 ») : 1048576 ici, 0 sans
    /// en-tete. Pur, pour etre teste.
    /// </summary>
    internal static long OctetsRecus(string? plage)
    {
        if (string.IsNullOrWhiteSpace(plage)) return 0;
        var tiret = plage.LastIndexOf('-');
        return tiret >= 0 && long.TryParse(plage[(tiret + 1)..].Trim(), out var dernier) && dernier >= 0 ? dernier + 1 : 0;
    }

    /// <summary>
    /// Envoie le film a partir de <paramref name="debut"/> (0 pour un premier envoi). Rend l'identifiant YouTube
    /// quand Google a tout recu ; null si l'envoi s'arrete avant (la session reste valable pour reprendre).
    /// </summary>
    private async Task<string?> EnvoyerLeResteAsync(HttpClient http, string adresse, string replayId, string fichier,
        long debut, long taille, CancellationToken ct)
    {
        await using var flux = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.Read);
        flux.Seek(debut, SeekOrigin.Begin);
        using var contenu = new StreamContent(flux);
        contenu.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        contenu.Headers.ContentLength = taille - debut;
        if (debut > 0) contenu.Headers.ContentRange = new ContentRangeHeaderValue(debut, taille - 1, taille);
        try
        {
            using var reponse = await http.PutAsync(adresse, contenu, ct).ConfigureAwait(false);
            if (reponse.IsSuccessStatusCode)
                return (string?)(JsonNode.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject)?["id"];
            _logger.LogWarning("Video : envoi de {ReplayId} interrompu par Google ({Statut}), repris au prochain passage.",
                replayId, (int)reponse.StatusCode);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation(ex, "Video : envoi de {ReplayId} coupe par le reseau, repris au prochain passage.", replayId);
            return null;
        }
    }

    /// <summary>
    /// Demande au site la session d'envoi : son adresse, ou la raison du refus (null si le site n'a pas repondu).
    /// L'adresse vaut autorisation pour cette seule video : elle ne se journalise pas.
    /// </summary>
    private async Task<(string? Adresse, string? Raison)> OuvrirSessionAsync(string credential, JsonObject demande,
        CancellationToken ct)
    {
        try
        {
            using var client = Client(credential, TimeSpan.FromSeconds(60));
            using var contenu = new StringContent(demande.ToJsonString(), Encoding.UTF8, "application/json");
            using var reponse = await client.PostAsync("/api/v1/agent/videos/upload-session", contenu, ct).ConfigureAwait(false);
            var corps = JsonNode.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            var adresse = (string?)corps?["session"];
            if (reponse.IsSuccessStatusCode && !string.IsNullOrEmpty(adresse)) return (adresse, null);
            var raison = (string?)corps?["reason"] ?? "http_" + (int)reponse.StatusCode;
            _logger.LogInformation("Video : envoi de {ReplayId} refuse par le site ({Raison}).", (string?)demande["replay_id"], raison);
            return (null, raison);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Video : site injoignable pour la session d'envoi.");
            return (null, null);
        }
    }

    /// <summary>Ouvre la session, envoie le film chez Google, rend l'identifiant YouTube (ou null).</summary>
    private async Task<string?> EnvoyerAsync(string credential, string replayId, string fichier, CancellationToken ct)
    {
        var taille = new FileInfo(fichier).Length;
        var (adresse, _) = await OuvrirSessionAsync(credential, new JsonObject
        {
            ["replay_id"] = replayId,
            ["size"] = taille,
            ["type"] = "video/x-matroska",
        }, ct).ConfigureAwait(false);
        if (adresse is null) return null;
        return await EnvoyerChezGoogleAsync(adresse, replayId, fichier, taille, ct).ConfigureAwait(false);
    }

    /// <summary>Le fichier, directement chez Google, a l'adresse de session. Rend l'identifiant YouTube (ou null).</summary>
    private async Task<string?> EnvoyerChezGoogleAsync(string adresse, string replayId, string fichier, long taille,
        CancellationToken ct)
    {
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

    /// <summary>La reponse du site a une demande de direct : l'adresse d'entree porte la cle de diffusion.</summary>
    /// <summary>
    /// La reponse du site. <c>Raison</c> « en_attente » : un autre direct de record est en cours (un seul a la fois,
    /// decision du 15 octobre 2026), la borne est a la place <c>Position</c> de la file et redemande au bout de
    /// <c>RedemanderApres</c> secondes.
    /// </summary>
    public sealed record DirectOuvert(bool Ok, string? LiveId, string? Ingest, string? Raison, int? Position = null, int? RedemanderApres = null);

    /// <summary>
    /// Demande au site le direct d'un record (CDC video V2, §5.8) : la partie passe devant le premier rang. Le site prend
    /// la machine du classement sur la ligne du premier rang. L'adresse rendue ne se journalise pas.
    /// </summary>
    public async Task<DirectOuvert> OuvrirLeDirectAsync(string romGroup, string regle, long score, bool essai, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return new(false, null, null, "borne non appairee");
        try
        {
            using var client = Client(credential, TimeSpan.FromSeconds(60));
            var demande = new JsonObject
            {
                ["system_id"] = "", ["rom_group"] = romGroup, ["ruleset"] = regle, ["score"] = score, ["test"] = essai,
            };
            using var contenu = new StringContent(demande.ToJsonString(), Encoding.UTF8, "application/json");
            using var reponse = await client.PostAsync("/api/v1/agent/live/record-start", contenu, ct).ConfigureAwait(false);
            var corps = JsonNode.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            if (reponse.IsSuccessStatusCode && (bool?)corps?["ok"] == true)
                return new(true, (string?)corps!["live_id"], (string?)corps["ingest"], null);
            return new(false, null, null, (string?)corps?["reason"] ?? "http_" + (int)reponse.StatusCode,
                (int?)corps?["position"], (int?)corps?["retry_after"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Direct : site injoignable ({Message}).", ex.Message);
            return new(false, null, null, "site injoignable");
        }
    }

    /// <summary>Le direct est fini (film arrete, partie finie) : le site le clot.</summary>
    public async Task CloreLeDirectAsync(string liveId, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return;
        try
        {
            await PosterAsync(credential, "live/record-end", new JsonObject { ["live_id"] = liveId }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Direct : fin du direct {Direct} non transmise ({Message}).", liveId, ex.Message);
        }
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
