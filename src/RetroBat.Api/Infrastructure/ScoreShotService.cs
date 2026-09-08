using System.Text.Json;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// La photo du moment où le record se fait.
///
/// Le problème que ça résout : à la fermeture du jeu il n'y a plus rien à photographier.
/// EmulationStation ne lance son hook `game-end` qu'APRÈS la sortie de l'émulateur, et la
/// soumission part sur la fin de session, donc plus tard encore. L'image doit donc être prise
/// PENDANT la partie, ce qui pose la question du moment.
///
/// Le moment retenu : quand le score franchit le seuil d'entrée dans le TOP du jeu. La plateforme
/// répond ce seuil en un appel au début de la partie. La quasi-totalité des parties ne le
/// franchissent jamais et ne coûtent donc pas une seule capture ; celles qui le franchissent sont
/// exactement celles qui méritent une image.
///
/// Ce qui reste en mémoire : une seule image, la plus récente, remplacée à chaque nouveau sommet.
/// Elle ne monte que si le serveur a PUBLIÉ le score, et elle est effacée sinon. Une partie qui ne
/// donne rien ne laisse donc aucune trace sur le disque.
///
/// RetroArch seulement pour l'instant. MAME standalone a son propre `snapshot()`, joignable par le
/// pont Lua déjà déployé ; c'est la même parité que celle qui reste à faire pour l'anti-triche.
/// </summary>
public sealed class ScoreShotService : BackgroundService
{
    /// <summary>Combien de places au classement comptent comme « le top ». Franchir la dixième
    /// place est le premier moment où une partie devient intéressante à montrer.</summary>
    private const int TailleDuTop = 10;

    /// <summary>Entre deux photos. Un score qui monte vite en déclencherait des dizaines ; on veut
    /// la dernière, pas toutes.</summary>
    private static readonly TimeSpan EntreDeuxPhotos = TimeSpan.FromSeconds(20);

    /// <summary>RetroArch écrit son PNG sans rien répondre : on attend de voir le fichier
    /// apparaître, brièvement.</summary>
    private static readonly TimeSpan AttenteFichier = TimeSpan.FromSeconds(3);

    private readonly IEventBus _bus;
    private readonly RetroArchReplayClient _retroarch;
    private readonly NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ScoreShotService> _logger;

    private readonly object _gate = new();
    private IDisposable? _subscription;

    // État de la partie en cours.
    private string _romGroup = "";
    private string _systemId = "";
    private long? _seuil;                 // null = le tableau n'est pas plein, tout entre
    private bool _seuilConnu;
    private bool _plusPetitEstMeilleur;
    private bool _arme;                   // le seuil a été franchi au moins une fois
    private long _scorePhotographie;
    private DateTime _dernierePhoto = DateTime.MinValue;
    private string? _enAttente;           // le fichier gardé pour cette partie

    public ScoreShotService(IEventBus bus, RetroArchReplayClient retroarch,
        NelfePlayDeviceStore devices, IHttpClientFactory httpFactory, ILogger<ScoreShotService> logger)
    {
        _bus = bus; _retroarch = retroarch; _devices = devices;
        _httpFactory = httpFactory; _logger = logger;
    }

    private static string DossierLocal
        => Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "scoreshots");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _subscription = _bus.Subscribe<EventEnvelope>(OnEvent); }
        catch (Exception ex) { _logger.LogDebug(ex, "Capture record : abonnement au bus impossible."); }
        stoppingToken.Register(() => _subscription?.Dispose());
        return Task.CompletedTask;
    }

    private void OnEvent(EventEnvelope envelope)
    {
        try
        {
            switch (envelope.Type?.ToLowerInvariant())
            {
                case "ui.game.started":
                    Oublier("nouvelle partie");
                    break;
                case "scoring.listener.attestation":
                    _ = OnAttestationAsync(ToJson(envelope.Payload));
                    break;
                case "score.live.changed":
                    _ = OnScoreAsync(ToJson(envelope.Payload));
                    break;
                case "scoring.verdict":
                    _ = OnVerdictAsync(ToJson(envelope.Payload));
                    break;
                case "ui.game.ended":
                    // On ne jette PAS ici : le verdict arrive après la fermeture du jeu, et
                    // c'est lui qui décide si l'image monte. Le ménage se fait au verdict, ou
                    // à la partie suivante si aucun verdict n'est venu.
                    break;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Capture record : évènement {Type} ignoré.", envelope.Type); }
    }

    /// <summary>Le jeu est identifié : on demande à la plateforme ce qu'il faut faire pour entrer
    /// dans son top. Un seul appel par partie.</summary>
    private async Task OnAttestationAsync(JsonElement root)
    {
        var rom = Texte(root, "Rom");
        var systeme = Texte(root, "SystemId");
        if (rom.Length == 0) return;

        lock (_gate)
        {
            if (_romGroup == rom && _seuilConnu) return;
            _romGroup = rom; _systemId = systeme;
            _seuilConnu = false; _seuil = null; _arme = false; _scorePhotographie = 0;
        }

        try
        {
            var credential = ResolveCredential();
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var url = NelfePlayAgentService.BaseUrl.TrimEnd('/')
                + "/api/v1/scores/threshold?top=" + TailleDuTop
                + "&rom_group=" + Uri.EscapeDataString(rom);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(credential)) request.Headers.Add("X-NelfePlay-Device", credential);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var racine = doc.RootElement;
            long? seuil = racine.TryGetProperty("threshold", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt64() : null;
            var bas = racine.TryGetProperty("direction", out var d)
                && d.GetString() == "lower_better";

            lock (_gate)
            {
                _seuil = seuil; _plusPetitEstMeilleur = bas; _seuilConnu = true;
            }
            _logger.LogInformation(
                "Capture record : {Rom} entre dans le top {Top} {Condition}.",
                rom, TailleDuTop,
                seuil is null ? "sans condition (classement incomplet)"
                    : (bas ? "sous " : "au-dessus de ") + seuil.Value.ToString("N0"));
        }
        catch (Exception ex)
        {
            // Hors ligne : on ne photographie pas. Mieux vaut aucune image qu'une image de
            // chaque partie, qu'il faudrait ensuite trier.
            _logger.LogDebug(ex, "Capture record : seuil indisponible pour {Rom}.", _romGroup);
        }
    }

    /// <summary>Le score a bougé. C'est ici que se décide la photo.</summary>
    private async Task OnScoreAsync(JsonElement root)
    {
        if (!root.TryGetProperty("Score", out var s) || !s.TryGetInt64(out var total)) return;

        bool photographier;
        lock (_gate)
        {
            if (!_seuilConnu || _romGroup.Length == 0) return;

            if (!_arme)
            {
                // Franchir le seuil : le moment où cette partie devient intéressante.
                _arme = _seuil is null
                    ? total > 0
                    : (_plusPetitEstMeilleur ? total <= _seuil.Value : total >= _seuil.Value);
                if (!_arme) return;
                _logger.LogInformation("Capture record : {Rom} entre dans le top à {Score}.", _romGroup, total.ToString("N0"));
            }

            // Une fois armé, on suit le score : c'est la DERNIÈRE photo qui compte, donc on
            // n'en reprend une que si le score s'est réellement amélioré depuis la précédente.
            var mieux = _enAttente is null
                || (_plusPetitEstMeilleur ? total < _scorePhotographie : total > _scorePhotographie);
            photographier = mieux && DateTime.UtcNow - _dernierePhoto >= EntreDeuxPhotos;
            if (photographier)
            {
                _dernierePhoto = DateTime.UtcNow;
                _scorePhotographie = total;
            }
        }

        if (photographier) await PhotographierAsync().ConfigureAwait(false);
    }

    /// <summary>Envoie la commande, puis adopte le fichier qui vient d'apparaître.</summary>
    private async Task PhotographierAsync()
    {
        var avant = DateTime.UtcNow.AddSeconds(-1);   // marge d'horloge sur l'écriture du fichier
        try { await _retroarch.ScreenshotAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Capture record : commande refusée."); return; }

        var apparu = await AttendreFichierAsync(avant).ConfigureAwait(false);
        if (apparu is null)
        {
            _logger.LogDebug("Capture record : aucun fichier apparu (RetroArch absent ?).");
            return;
        }

        try
        {
            Directory.CreateDirectory(DossierLocal);
            var destination = Path.Combine(DossierLocal, "pending.png");
            // DÉPLACER, pas copier : le dossier des captures appartient au joueur, et on ne va
            // pas y laisser une image par sommet. Si la fenêtre de deux secondes nous faisait
            // adopter une capture prise à la main au même instant, elle serait ici, pas perdue.
            File.Move(apparu, destination, overwrite: true);
            lock (_gate) { _enAttente = destination; }
            _logger.LogInformation("Capture record : image gardée pour {Rom} à {Score}.",
                _romGroup, _scorePhotographie.ToString("N0"));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Capture record : image non conservée."); }
    }

    private static async Task<string?> AttendreFichierAsync(DateTime apres)
    {
        var dossier = DossierRetroArch();
        if (dossier is null || !Directory.Exists(dossier)) return null;
        var limite = DateTime.UtcNow + AttenteFichier;
        while (DateTime.UtcNow < limite)
        {
            try
            {
                var candidat = new DirectoryInfo(dossier)
                    .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                    .Where(f => f.LastWriteTimeUtc >= apres)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                // Fichier encore en cours d'écriture : on le saute, il sera là au tour suivant.
                if (candidat is not null && candidat.Length > 0 && Lisible(candidat.FullName))
                    return candidat.FullName;
            }
            catch { /* le dossier bouge sous nos pieds : on retente */ }
            await Task.Delay(150).ConfigureAwait(false);
        }
        return null;
    }

    private static bool Lisible(string chemin)
    {
        try
        {
            using var fs = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Où RetroArch écrit ses captures. On lit sa configuration plutôt que de supposer :
    /// le dossier est déplaçable et une supposition fausse ne se verrait jamais.</summary>
    private static string? DossierRetroArch()
    {
        try
        {
            var cfg = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.cfg");
            if (File.Exists(cfg))
            {
                foreach (var ligne in File.ReadLines(cfg))
                {
                    if (!ligne.StartsWith("screenshot_directory", StringComparison.Ordinal)) continue;
                    var eq = ligne.IndexOf('=');
                    if (eq < 0) continue;
                    var valeur = ligne[(eq + 1)..].Trim().Trim('"');
                    if (valeur.Length > 0 && valeur != ":\\" && Directory.Exists(valeur)) return valeur;
                }
            }
        }
        catch { /* configuration illisible : on retombe sur le dossier habituel */ }

        var defaut = Path.Combine(RetroBatPaths.RetroBatRoot, "screenshots");
        return Directory.Exists(defaut) ? defaut : null;
    }

    /// <summary>Le verdict est tombé : l'image monte si le score a été publié, sinon elle part.</summary>
    private async Task OnVerdictAsync(JsonElement root)
    {
        string? fichier;
        lock (_gate) { fichier = _enAttente; }
        if (fichier is null) return;

        var statut = Texte(root, "Status");
        var session = Texte(root, "SessionId");
        if (statut != "published" || session.Length == 0)
        {
            Oublier($"verdict « {(statut.Length > 0 ? statut : "inconnu")} »");
            return;
        }

        try
        {
            var credential = ResolveCredential();
            if (string.IsNullOrEmpty(credential)) { Oublier("machine non appairée"); return; }

            var octets = await File.ReadAllBytesAsync(fichier).ConfigureAwait(false);
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var url = NelfePlayAgentService.BaseUrl.TrimEnd('/')
                + "/api/v1/agent/scores/shot?origin=live&session_id=" + Uri.EscapeDataString(session);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(octets),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            request.Headers.Add("X-NelfePlay-Device", credential);

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var corps = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogInformation("Capture record : envoi {Code} - {Corps}", (int)response.StatusCode, corps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture record : envoi impossible.");
        }
        finally
        {
            // Dans tous les cas on ne garde rien : l'image d'une partie finie n'a plus d'usage
            // ici, et la plateforme est la seule à décider si elle valait d'être conservée.
            Oublier("envoi terminé");
        }
    }

    private void Oublier(string raison)
    {
        string? fichier;
        lock (_gate)
        {
            fichier = _enAttente;
            _enAttente = null;
            _arme = false; _scorePhotographie = 0; _dernierePhoto = DateTime.MinValue;
        }
        if (fichier is null) return;
        try { File.Delete(fichier); } catch { /* elle repartira au prochain remplacement */ }
        _logger.LogDebug("Capture record : image écartée ({Raison}).", raison);
    }

    private string? ResolveCredential()
    {
        var appaire = _devices.GetCredential();
        if (!string.IsNullOrEmpty(appaire)) return appaire;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }
        }
        catch { /* pas de credential anonyme : la borne n'enverra rien */ }
        return null;
    }

    private static JsonElement ToJson(object? payload)
        => JsonSerializer.SerializeToElement(payload);

    private static string Texte(JsonElement root, string nom)
        => root.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
