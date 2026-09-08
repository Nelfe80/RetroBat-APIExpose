using System.Text.Json;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Refait l'image d'un record en REJOUANT son replay.
///
/// C'est la bonne façon de produire cette image, pour deux raisons. Elle est REPRODUCTIBLE :
/// même objet replay, même image, refaisable par quiconque détient les octets, ce qui la range
/// dans la même famille que le reste de la chaîne certifiée. Et elle rattrape le PASSÉ : les
/// records déjà publiés n'ont jamais été photographiés, et il n'y a aucune autre manière de le
/// faire maintenant que la partie est finie depuis longtemps.
///
/// Le moment photographié est la FIN DE LA COURSE. Ce n'est pas un choix esthétique : le
/// protocole dit `result_source = final`, donc le score du record EST le total de fin. Les
/// frames des checkpoints, elles, valent zéro sur ce chemin (le rapporteur le documente déjà :
/// « les frames du listener ne sont pas fiables ici »), donc viser un instant intermédiaire
/// reviendrait à interpoler une position qu'on ne mesure pas.
///
/// L'image est prise en DÉFINITION D'ORIGINE : la lecture d'un replay force
/// `video_gpu_screenshot=false`, donc RetroArch photographie le tampon du cœur, pas la sortie
/// mise à l'échelle de cet écran-là.
///
/// Cette opération PREND L'ÉCRAN : elle lance RetroArch en vrai. Elle refuse donc de démarrer si
/// quoi que ce soit tourne, et elle ne se déclenche jamais toute seule.
/// </summary>
public sealed class ScoreShotRegenerator
{
    /// <summary>Combien avant la toute fin. Assez pour que RetroArch ait le temps d'écrire le
    /// PNG avant que `--eof-exit` ne referme la lecture, assez peu pour que l'écran montre bien
    /// la fin de la course.</summary>
    private const long MargeFrames = 90;

    private readonly ReplayStore _store;
    private readonly ReplayPlaybackService _playback;
    private readonly RetroArchReplayClient _retroarch;
    private readonly NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ScoreShotRegenerator> _logger;

    private readonly SemaphoreSlim _uneSeuleAlaFois = new(1, 1);

    public ScoreShotRegenerator(ReplayStore store, ReplayPlaybackService playback,
        RetroArchReplayClient retroarch, NelfePlayDeviceStore devices,
        IHttpClientFactory httpFactory, ILogger<ScoreShotRegenerator> logger)
    {
        _store = store; _playback = playback; _retroarch = retroarch;
        _devices = devices; _httpFactory = httpFactory; _logger = logger;
    }

    public sealed record Candidat(string SessionId, string ReplayId, string RomGroup, string Ruleset,
        long Score, long TargetFrame);

    public sealed record Rapport(bool Ran, int Candidates, int Captured, int Sent, string? Reason,
        IReadOnlyList<string> Details);

    /// <summary>
    /// Les records de CETTE borne qui méritent une image : le meilleur de chaque classement,
    /// et seulement s'il a gardé son replay.
    ///
    /// On ne rejoue pas les 38 parties publiées pour en voir 34 refusées : la plateforme ne
    /// conserve que l'image du meilleur, autant ne rejouer que celui-là. Le tri se fait ici sur
    /// ce qu'on détient, la plateforme retranche ensuite ce qui n'est pas le meilleur du monde.
    /// </summary>
    public IReadOnlyList<Candidat> Candidats()
    {
        var dossier = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "certified");
        if (!Directory.Exists(dossier)) return Array.Empty<Candidat>();

        // Un manifeste par session : c'est le manifeste qui porte le lien, pas nous.
        var parSession = new Dictionary<string, Replay.Models.ReplayManifest>(StringComparer.Ordinal);
        foreach (var m in _store.ListManifests())
        {
            if (!string.IsNullOrEmpty(m.SessionId)) parSession[m.SessionId] = m;
        }

        var meilleurs = new Dictionary<string, Candidat>(StringComparer.OrdinalIgnoreCase);
        foreach (var fichier in Directory.EnumerateFiles(dossier, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(fichier));
                var racine = doc.RootElement;
                if (Texte(racine, "verdict") != "published") continue;
                if (!racine.TryGetProperty("passport", out var passeport)) continue;

                var sessionId = Texte(passeport, "session_id");
                if (sessionId.Length == 0 || !parSession.TryGetValue(sessionId, out var manifest)) continue;

                var jeu = passeport.TryGetProperty("game", out var g) ? g : default;
                var romGroup = Texte(jeu, "rom_group");
                var ruleset = Texte(jeu, "ruleset");
                if (romGroup.Length == 0) continue;

                var score = Nombre(passeport.TryGetProperty("metric", out var mt) ? mt : default, "value");

                // Fin de la course, en retrait de la marge : l'instant du record.
                var fin = manifest.Frames.RunEnd ?? manifest.Frames.ReplayEnd;
                if (fin <= 0) continue;
                var cible = Math.Max(manifest.Frames.Start, fin - MargeFrames);

                var cle = romGroup + "|" + ruleset;
                if (!meilleurs.TryGetValue(cle, out var deja) || score > deja.Score)
                    meilleurs[cle] = new Candidat(sessionId, manifest.ReplayId, romGroup, ruleset, score, cible);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Capture record : archive illisible {Fichier}.", fichier); }
        }

        return meilleurs.Values.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>Rejoue, photographie et envoie. Une seule à la fois, jamais pendant autre chose.</summary>
    public async Task<Rapport> RunAsync(int limit, CancellationToken ct)
    {
        if (!await _uneSeuleAlaFois.WaitAsync(0, ct).ConfigureAwait(false))
            return new Rapport(false, 0, 0, 0, "deja_en_cours", Array.Empty<string>());

        try
        {
            // Cette opération lance RetroArch en plein écran. La déclencher pendant une partie
            // couperait le jeu de quelqu'un.
            var etat = _playback.GetState();
            if (etat.State != "idle")
                return new Rapport(false, 0, 0, 0, "lecture_en_cours", Array.Empty<string>());
            if (System.Diagnostics.Process.GetProcessesByName("retroarch").Length > 0)
                return new Rapport(false, 0, 0, 0, "emulateur_en_cours", Array.Empty<string>());

            var candidats = Candidats().Take(Math.Max(1, limit)).ToList();
            var details = new List<string>();
            int captures = 0, envoyes = 0;

            foreach (var c in candidats)
            {
                ct.ThrowIfCancellationRequested();
                var image = await PhotographierAsync(c, ct).ConfigureAwait(false);
                if (image is null)
                {
                    details.Add($"{c.RomGroup} : aucune image");
                    continue;
                }
                captures++;
                var reponse = await EnvoyerAsync(c, image, ct).ConfigureAwait(false);
                if (reponse is not null) envoyes++;
                details.Add($"{c.RomGroup} ({c.Score:N0}) frame {c.TargetFrame} : {reponse ?? "envoi impossible"}");
                try { File.Delete(image); } catch { }
            }

            return new Rapport(true, candidats.Count, captures, envoyes, null, details);
        }
        finally { _uneSeuleAlaFois.Release(); }
    }

    private async Task<string?> PhotographierAsync(Candidat c, CancellationToken ct)
    {
        _logger.LogInformation("Capture record : relecture de {Rom} ({Score}) pour la frame {Frame}.",
            c.RomGroup, c.Score.ToString("N0"), c.TargetFrame);

        var lancement = await _playback.PlayAsync(c.ReplayId, ct).ConfigureAwait(false);
        if (!lancement.Accepted)
        {
            _logger.LogWarning("Capture record : lecture refusée pour {ReplayId} ({Erreur}).", c.ReplayId, lancement.Error);
            return null;
        }

        try
        {
            if (!await AttendreEtatAsync("playing", TimeSpan.FromSeconds(45), ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Capture record : la lecture n'a jamais démarré pour {ReplayId}.", c.ReplayId);
                return null;
            }

            // On vise la frame, puis on ATTEND de l'avoir atteinte : RetroArch rejoint le
            // checkpoint le plus proche puis rejoue les entrées, donc l'arrivée n'est pas
            // immédiate et photographier tout de suite donnerait une image d'ailleurs.
            await _retroarch.SeekAsync(c.TargetFrame, ct).ConfigureAwait(false);
            var arrive = await AttendreFrameAsync(c.TargetFrame, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!arrive)
                _logger.LogInformation("Capture record : frame {Frame} pas confirmée, on photographie quand même.", c.TargetFrame);

            // Geler l'image avant de la prendre : sans pause, la lecture continue de courir vers
            // la fin, et `--eof-exit` referme RetroArch pendant l'écriture du PNG.
            await _retroarch.PauseToggleAsync(ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);

            var avant = DateTime.UtcNow.AddSeconds(-1);
            await _retroarch.ScreenshotAsync(ct).ConfigureAwait(false);
            var fichier = await AttendreFichierAsync(avant, ct).ConfigureAwait(false);
            if (fichier is null)
            {
                _logger.LogWarning("Capture record : aucun fichier produit pour {Rom}.", c.RomGroup);
                return null;
            }

            var destination = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "scoreshots",
                "regen-" + c.SessionId + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(fichier, destination, overwrite: true);
            return destination;
        }
        finally
        {
            try { await _playback.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            // Laisser RetroArch se refermer avant la relecture suivante : deux instances se
            // disputeraient le port de commande, et la seconde piloterait la première.
            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task<bool> AttendreEtatAsync(string voulu, TimeSpan limite, CancellationToken ct)
    {
        var fin = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fin)
        {
            var e = _playback.GetState();
            if (e.State == voulu) return true;
            if (e.State is "error" or "idle" && DateTime.UtcNow > fin - limite + TimeSpan.FromSeconds(10)) return false;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> AttendreFrameAsync(long cible, TimeSpan limite, CancellationToken ct)
    {
        var fin = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fin)
        {
            var e = _playback.GetState();
            if (e.State != "playing") return false;
            // À une demi-seconde près : la télémétrie ne rend pas chaque frame, et viser
            // l'égalité exacte attendrait indéfiniment.
            if (Math.Abs(e.Frame - cible) <= Math.Max(30, (long)(e.NominalFps / 2))) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<string?> AttendreFichierAsync(DateTime apres, CancellationToken ct)
    {
        var dossier = DossierRetroArch();
        if (dossier is null) return null;
        var fin = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < fin)
        {
            try
            {
                var candidat = new DirectoryInfo(dossier)
                    .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                    .Where(f => f.LastWriteTimeUtc >= apres && f.Length > 0)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (candidat is not null) return candidat.FullName;
            }
            catch { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static string? DossierRetroArch()
    {
        try
        {
            var cfg = Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot,
                "emulators", "retroarch", "retroarch.cfg");
            if (File.Exists(cfg))
            {
                foreach (var ligne in File.ReadLines(cfg))
                {
                    if (!ligne.StartsWith("screenshot_directory", StringComparison.Ordinal)) continue;
                    var eq = ligne.IndexOf('=');
                    if (eq < 0) continue;
                    var valeur = ligne[(eq + 1)..].Trim().Trim('"');
                    if (valeur.Length > 0 && Directory.Exists(valeur)) return valeur;
                }
            }
        }
        catch { }
        var defaut = Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot, "screenshots");
        return Directory.Exists(defaut) ? defaut : null;
    }

    private async Task<string?> EnvoyerAsync(Candidat c, string fichier, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return null;
        try
        {
            var octets = await File.ReadAllBytesAsync(fichier, ct).ConfigureAwait(false);
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var url = NelfePlayAgentService.BaseUrl.TrimEnd('/')
                + "/api/v1/agent/scores/shot?origin=replay"
                + "&session_id=" + Uri.EscapeDataString(c.SessionId)
                + "&frame=" + c.TargetFrame;
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(octets),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            request.Headers.Add("X-NelfePlay-Device", credential);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture record : envoi impossible pour {Rom}.", c.RomGroup);
            return null;
        }
    }

    private static string Texte(JsonElement racine, string nom)
        => racine.ValueKind == JsonValueKind.Object
            && racine.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Nombre(JsonElement racine, string nom)
    {
        if (racine.ValueKind != JsonValueKind.Object || !racine.TryGetProperty(nom, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p) ? p : 0;
    }
}
