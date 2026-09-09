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
        string SystemId, long Score, long TargetFrame);

    public sealed record Rapport(bool Ran, int Candidates, int Captured, int Sent, string? Reason,
        IReadOnlyList<string> Details);

    /// <summary>
    /// Les records de CETTE borne qui méritent une image, d'après la plateforme.
    ///
    /// C'est elle qui doit le dire, et pas nous : le lien entre un record et son replay n'existe
    /// que de son côté. Ici, le <c>session_id</c> d'un manifeste est une séance de LECTURE, pas
    /// la séance de scoring, et rien ne rapproche les deux. C'est aussi elle qui sait quel score
    /// est en tête de son classement, alors que la borne ne connaît que ses propres parties.
    ///
    /// Nous ne gardons de son avis que ce que nous détenons vraiment : un replay dont le
    /// manifeste et l'objet manquent ne se rejoue pas, quoi qu'elle en dise.
    /// </summary>
    public async Task<IReadOnlyList<Candidat>> CandidatsAsync(CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return Array.Empty<Candidat>();

        JsonElement racine;
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                NelfePlayAgentService.BaseUrl.TrimEnd('/') + "/api/v1/agent/scores/shot-targets?limit=50");
            request.Headers.Add("X-NelfePlay-Device", credential);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<Candidat>();
            var corps = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(corps);
            if (!doc.RootElement.TryGetProperty("targets", out var cibles)) return Array.Empty<Candidat>();
            racine = cibles.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture record : liste des records injoignable.");
            return Array.Empty<Candidat>();
        }

        var manifests = _store.ListManifests().ToDictionary(m => m.ReplayId, StringComparer.Ordinal);
        var sortie = new List<Candidat>();
        foreach (var cible in racine.EnumerateArray())
        {
            var replayId = Texte(cible, "replay_id");
            if (replayId.Length == 0 || !manifests.TryGetValue(replayId, out var manifest)) continue;
            // Les octets sont-ils là ? On regarde le fichier, sans le rehacher : la vérification
            // d'intégrité est le travail du lecteur, qui la fait déjà avant de lancer.
            if (!File.Exists(_store.ObjectPath(manifest.Object.Sha256))) continue;

            // Fin de la course, en retrait de la marge : l'instant du record.
            var fin = manifest.Frames.RunEnd ?? manifest.Frames.ReplayEnd;
            if (fin <= 0) continue;

            sortie.Add(new Candidat(
                Texte(cible, "session_id"), replayId,
                Texte(cible, "rom_group"), Texte(cible, "ruleset"), Texte(cible, "system_id"),
                Nombre(cible, "score"),
                Math.Max(manifest.Frames.Start, fin - MargeFrames)));
        }
        return sortie.OrderByDescending(c => c.Score).ToList();
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

            var candidats = (await CandidatsAsync(ct).ConfigureAwait(false)).Take(Math.Max(1, limit)).ToList();
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

    /// <summary>
    /// Combien de reculs on s'autorise quand l'image ne montre rien, et de combien. La fin d'une
    /// course tombe souvent sur une transition ; cinq secondes en arriere suffisent en general a
    /// retrouver du jeu a l'ecran.
    /// </summary>
    private const int EssaisMax = 4;

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

            // « playing » veut dire que RetroArch tient, pas que le replay défile. Un SEEK envoyé
            // avant que le cœur n'ait vraiment pris la main est simplement perdu : c'est ce qui a
            // produit une image d'un tout autre endroit de la partie au premier essai. On attend
            // donc de VOIR la frame avancer avant de viser.
            if (!await AttendreDefilementAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Capture record : le replay ne défile pas pour {ReplayId}.", c.ReplayId);
                return null;
            }

            var recul = (long)Math.Max(60, _playback.GetState().NominalFps * 5);
            var destination = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "scoreshots",
                "regen-" + c.SessionId + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var paused = false;

            for (var essai = 0; essai < EssaisMax; essai++)
            {
                var vise = Math.Max(0, c.TargetFrame - essai * recul);

                // On vise la frame, puis on ATTEND de l'avoir atteinte : RetroArch rejoint le
                // checkpoint le plus proche puis rejoue les entrées, donc l'arrivée n'est pas
                // immédiate et photographier tout de suite donnerait une image d'ailleurs.
                if (paused) { await _retroarch.PauseToggleAsync(ct).ConfigureAwait(false); paused = false; }
                var reponse = await _retroarch.SeekAsync(vise, ct).ConfigureAwait(false);
                _logger.LogInformation("Capture record : SEEK {Frame} -> {Reponse}", vise,
                    (reponse ?? "aucune réponse").Trim());

                if (!await AttendreFrameAsync(vise, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false))
                {
                    // On ne photographie PAS au hasard. Une image du mauvais moment publiée comme
                    // « le record » est pire que pas d'image du tout : elle est fausse, et rien à
                    // l'écran ne le dirait.
                    _logger.LogWarning(
                        "Capture record : frame {Frame} jamais atteinte pour {Rom} (observée : {Vue}). Rien n'est envoyé.",
                        vise, c.RomGroup, _playback.GetState().Frame);
                    return null;
                }

                // Geler l'image avant de la prendre : sans pause, la lecture continue de courir
                // vers la fin, et `--eof-exit` referme RetroArch pendant l'écriture du PNG.
                await _retroarch.PauseToggleAsync(ct).ConfigureAwait(false);
                paused = true;
                await Task.Delay(400, ct).ConfigureAwait(false);

                var avant = DateTime.UtcNow.AddSeconds(-1);
                await _retroarch.ScreenshotAsync(ct).ConfigureAwait(false);
                var fichier = await AttendreFichierAsync(avant, ct).ConfigureAwait(false);
                if (fichier is null)
                {
                    _logger.LogWarning("Capture record : aucun fichier produit pour {Rom}.", c.RomGroup);
                    return null;
                }

                File.Move(fichier, destination, overwrite: true);
                // Le tampon d'un coeur n'est pas oriente : un shoot vertical en sort couche.
                if (OperatingSystem.IsWindows())
                {
                    ScoreShotImage.Redresser(destination, c.SystemId, c.RomGroup, _logger);
                    // La toute fin d'une course tombe souvent sur un fondu ou une intro de boss.
                    // Plutot que de publier un ecran vide, on recule de cinq secondes et on
                    // recommence. Le dernier essai est garde tel quel : une image imparfaite vaut
                    // mieux que pas d'image, et le choix reste rejouable a la main.
                    if (!ScoreShotImage.MontreLeJeu(destination, _logger) && essai < EssaisMax - 1)
                    {
                        _logger.LogInformation("Capture record : {Rom}, on recule de {Recul} frames.",
                            c.RomGroup, recul);
                        continue;
                    }
                }
                return destination;
            }
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

    /// <summary>Le replay DÉFILE-t-il ? Deux relevés croissants suffisent à le dire, et c'est ce
    /// qui distingue « RetroArch est là » de « le cœur a pris la main ».</summary>
    private async Task<bool> AttendreDefilementAsync(TimeSpan limite, CancellationToken ct)
    {
        var fin = DateTime.UtcNow + limite;
        var precedente = -1L;
        while (DateTime.UtcNow < fin)
        {
            var e = _playback.GetState();
            if (e.State != "playing") return false;
            if (e.Frame > 0 && precedente >= 0 && e.Frame > precedente) return true;
            precedente = e.Frame;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Attend d'être arrivé. Deux façons d'y être : la frame tombe près de la cible, ou elle
    /// CESSE DE BOUGER — près de la fin, RetroArch s'immobilise sur la dernière image du replay,
    /// et c'est précisément celle qu'on veut.
    /// </summary>
    private async Task<bool> AttendreFrameAsync(long cible, TimeSpan limite, CancellationToken ct)
    {
        var fin = DateTime.UtcNow + limite;
        var precedente = -1L;
        var immobile = 0;
        while (DateTime.UtcNow < fin)
        {
            var e = _playback.GetState();
            if (e.State != "playing") return false;

            // À une demi-seconde près : la télémétrie ne rend pas chaque frame, et viser
            // l'égalité exacte attendrait indéfiniment.
            if (Math.Abs(e.Frame - cible) <= Math.Max(30, (long)(e.NominalFps / 2))) return true;

            immobile = e.Frame == precedente ? immobile + 1 : 0;
            precedente = e.Frame;
            // Immobile depuis plus d'une seconde ET au-delà du checkpoint visé : la lecture est
            // arrivée au bout, on y est.
            if (immobile >= 4 && e.Frame >= cible - Math.Max(120, (long)e.NominalFps * 2)) return true;

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
