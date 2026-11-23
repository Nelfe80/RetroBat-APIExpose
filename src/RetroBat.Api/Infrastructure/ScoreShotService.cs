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
/// Le moment retenu : quand le score DÉPASSE LE RECORD du jeu. La plateforme répond ce record en
/// un appel au début de la partie. La quasi-totalité des parties ne le dépassent jamais et ne
/// coûtent donc pas une seule capture ; celle qui le dépasse est exactement celle qui mérite une
/// image, et c'est l'instant où il tombe qu'on photographie.
///
/// UNE photo par partie, pas une par sommet. La première version armait la capture à l'entrée
/// dans le top 10 puis reprenait une photo toutes les vingt secondes tant que le score montait :
/// un bon joueur voyait RetroArch annoncer « capture enregistrée » pendant toute sa partie
/// (retour de la flotte, 2026-09-21). L'image montre donc l'écran au moment où le record est
/// battu, pas le score final : c'est un prix accepté pour ne plus déranger le jeu.
///
/// Ce qui reste en mémoire : cette seule image. Elle ne monte que si le serveur a PUBLIÉ le
/// score, et elle est effacée sinon. Une partie qui ne donne rien ne laisse donc aucune trace
/// sur le disque.
///
/// Les DEUX émulateurs sont couverts : RetroArch par sa commande réseau `SCREENSHOT`, MAME
/// standalone par `snapshot()` via le pont Lua. Dans les deux cas on demande à l'émulateur de se
/// photographier LUI-MÊME plutôt que de capturer sa fenêtre : son tampon est à la définition
/// d'origine du jeu, là où une capture de fenêtre rendrait ce que cet écran-là affiche, mis à
/// l'échelle et filtré.
/// </summary>
public sealed class ScoreShotService : BackgroundService
{
    /// <summary>La place à dépasser : la première. Le seuil demandé à la plateforme est donc le
    /// record du jeu, et non l'entrée dans un top.</summary>
    private const int TailleDuTop = 1;

    /// <summary>RetroArch écrit son PNG sans rien répondre : on attend de voir le fichier
    /// apparaître, brièvement.</summary>
    private static readonly TimeSpan AttenteFichier = TimeSpan.FromSeconds(3);

    private readonly IEventBus _bus;
    private readonly RetroArchReplayClient _retroarch;
    private readonly MameLuaIngameProvider _mame;
    private readonly NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ScoreShotService> _logger;
    private readonly PartieNelfePlayService? _partie;

    private readonly object _gate = new();
    private IDisposable? _subscription;

    // État de la partie en cours.
    private string _romGroup = "";
    private string _systemId = "";
    private bool _seuilConnu;
    private readonly Dictionary<string, SeuilDeRegle> _regles = new(StringComparer.OrdinalIgnoreCase);
    private int _photosDeLaPartie;
    private readonly NelfePlayScoringReporter? _rapporteur;

    /// <summary>Le seuil d'UNE règle du jeu (1cc, 1lc, 1cc-extended...) et ce qu'il est devenu dans cette partie.</summary>
    private sealed class SeuilDeRegle
    {
        public long? Seuil;              // null = le tableau n'est pas plein, tout entre
        public bool PlusPetitEstMeilleur;
        public bool Arme;                // le record de CETTE règle est tombé : sa photo est prise
        public long ScorePhotographie;
        public string? Fichier;          // le fichier gardé pour cette règle
    }

    public ScoreShotService(IEventBus bus, RetroArchReplayClient retroarch,
        MameLuaIngameProvider mame, NelfePlayDeviceStore devices, IHttpClientFactory httpFactory,
        ILogger<ScoreShotService> logger, PartieNelfePlayService? partie = null, NelfePlayScoringReporter? rapporteur = null)
    {
        _rapporteur = rapporteur;
        _bus = bus; _retroarch = retroarch; _mame = mame; _devices = devices;
        _httpFactory = httpFactory; _logger = logger; _partie = partie;
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

        // L'atelier de NelfeScoreLab : rien ne sera publie, et ses captures sont celles du Lab.
        if (RetroBat.Api.Scoring.ScoreLabAtelier.IsActive(DateTime.UtcNow, out _))
        {
            lock (_gate) { _romGroup = rom; _systemId = systeme; _seuilConnu = false; _regles.Clear(); _photosDeLaPartie = 0; }
            _logger.LogInformation("Capture record : {Rom} sous l'atelier NelfeScoreLab, pas de photo.", rom);
            return;
        }

        // HORS NELFEPLAY, PAS DE PHOTO (vu le 22/10 : « Capture record » pendant une partie MULTIPLAYER de SF2CE). Une
        // partie du joueur, ou de MULTIPLAYER, ne publie aucun score : son image ne monterait jamais.
        // UNE EPREUVE (03/11) : un speedrun se classe au temps. Avant meme son etat de depart, le demarrage de Sonic donnait un
        // score sans objet, pris pour un record battu.
        if (_partie is { EstSpeedrun: true })
        {
            lock (_gate) { _romGroup = rom; _systemId = systeme; _seuilConnu = false; _regles.Clear(); _photosDeLaPartie = 0; }
            _logger.LogInformation("Capture record : {Rom} en epreuve, pas de photo de score.", rom);
            return;
        }

        if (_partie is { EstNelfePlay: false })
        {
            lock (_gate) { _romGroup = rom; _systemId = systeme; _seuilConnu = false; _regles.Clear(); _photosDeLaPartie = 0; }
            _logger.LogInformation("Capture record : {Rom} hors NelfePlay, pas de photo.", rom);
            return;
        }

        lock (_gate)
        {
            if (_romGroup == rom && _seuilConnu) return;
            _romGroup = rom; _systemId = systeme;
            _seuilConnu = false; _regles.Clear(); _photosDeLaPartie = 0;
        }

        try
        {
            var credential = ResolveCredential();
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var racineUrl = NelfePlayAgentService.BaseUrl.TrimEnd('/');

            // CHAQUE CLASSEMENT A SA CAPTURE (decision du user du 23/11) : pas seulement le 1CC, aussi le 1LC et les modes
            // speciaux. La regle jouee n'est connue qu'a la fin de la partie ; on demande donc le seuil de CHAQUE regle
            // solo ouverte du jeu, et on photographie quand l'une d'elles est franchie (une photo par regle).
            string[] regles;
            using (var rp = new HttpRequestMessage(HttpMethod.Get, racineUrl + "/api/v1/agent/scores/profile?system_id="
                + Uri.EscapeDataString(systeme) + "&rom_group=" + Uri.EscapeDataString(rom)))
            {
                if (!string.IsNullOrEmpty(credential)) rp.Headers.Add("X-NelfePlay-Device", credential);
                using var rep = await client.SendAsync(rp).ConfigureAwait(false);
                if (!rep.IsSuccessStatusCode) return;
                regles = ScoreShotRegles.ReglesPhotographiables(await rep.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            var trouves = new Dictionary<string, SeuilDeRegle>(StringComparer.OrdinalIgnoreCase);
            foreach (var regle in regles)
            {
                var url = racineUrl + "/api/v1/scores/threshold?top=" + TailleDuTop
                    + "&rom_group=" + Uri.EscapeDataString(rom)
                    + "&ruleset=" + Uri.EscapeDataString(regle)
                    + (systeme.Length > 0 ? "&system=" + Uri.EscapeDataString(systeme) : "");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(credential)) request.Headers.Add("X-NelfePlay-Device", credential);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var racine = doc.RootElement;
                // Le site rend une reponse « vide » (ruleset vide) quand il ne connait pas la regle : on l'ecarte.
                if (!racine.TryGetProperty("ruleset", out var rs) || !string.Equals(rs.GetString(), regle, StringComparison.OrdinalIgnoreCase)) continue;
                long? seuil = racine.TryGetProperty("threshold", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt64() : null;
                var bas = racine.TryGetProperty("direction", out var d) && d.GetString() == "lower_better";
                trouves[regle] = new SeuilDeRegle { Seuil = seuil, PlusPetitEstMeilleur = bas };
                _logger.LogInformation(
                    "Capture record : {Rom} regle {Regle} : record (top {Top}) {Condition}.",
                    rom, regle, TailleDuTop,
                    seuil is null ? "sans condition (classement incomplet)"
                        : (bas ? "sous " : "au-dessus de ") + seuil.Value.ToString("N0"));
            }

            lock (_gate)
            {
                if (_romGroup != rom) return;   // une autre partie a commence pendant les appels
                _regles.Clear();
                foreach (var kv in trouves) _regles[kv.Key] = kv.Value;
                _seuilConnu = _regles.Count > 0;
            }
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
        // Un score vu hors de la fenetre de jeu (demo apres la partie) n'est jamais un record (21/11).
        if (_rapporteur?.ScoreHorsPartie() == true) return;

        List<string> nouvelles;
        var recordExistant = false;
        lock (_gate)
        {
            if (!_seuilConnu || _romGroup.Length == 0) return;

            // Une seule photo par REGLE et par partie : celle de l'instant où le record de cette règle tombe. Un record
            // déjà battu dans cette partie ne se rephotographie pas, quoi que fasse le score. Une même image sert les
            // règles qui tombent au même instant.
            nouvelles = new List<string>();
            foreach (var (regle, e) in _regles)
            {
                if (e.Arme) continue;
                if (!ScoreShotRegles.Franchit(e.Seuil, e.PlusPetitEstMeilleur, total)) continue;
                if (nouvelles.Count == 0 && _photosDeLaPartie >= ScoreShotRegles.PhotosMaxParPartie) continue;
                e.Arme = true; e.ScorePhotographie = total;
                nouvelles.Add(regle);
                recordExistant |= e.Seuil is not null;
                _logger.LogInformation("Capture record : {Rom} dépasse le record de {Regle} à {Score}.", _romGroup, regle, total.ToString("N0"));
            }
            if (nouvelles.Count == 0) return;
            _photosDeLaPartie++;
        }

        await PhotographierAsync(nouvelles, total).ConfigureAwait(false);

        // La plaque NEW RECORD au Deck (demande du user du 17/11) : le flash de RetroArch dit « capture », la plaque dit
        // « record battu ». Seulement quand un record existait : le premier score d'un jeu sans record n'en bat aucun.
        // Sans Deck pose, rien d'autre : le flash de la capture reste le signe pour tous.
        if (recordExistant) RetroBat.Api.Deck.DeckSilence.DireSiPose(RetroBat.Api.Deck.DeckNotices.Scoring("scoring_record", total));
    }

    /// <summary>
    /// Demande la photo à QUI joue, puis adopte le fichier qui vient d'apparaître.
    ///
    /// Deux émulateurs, deux commandes, une seule règle : on demande à l'émulateur de se
    /// photographier LUI-MÊME plutôt que de capturer sa fenêtre. Son tampon est à la définition
    /// d'origine du jeu ; une capture de fenêtre rendrait ce que cet écran-là affiche, mis à
    /// l'échelle et filtré, et dépendrait de l'affichage de la borne.
    /// </summary>
    private async Task PhotographierAsync(IReadOnlyList<string> regles, long score)
    {
        var avant = DateTime.UtcNow.AddSeconds(-1);   // marge d'horloge sur l'écriture du fichier
        var parMame = _mame.EstConnecte;
        // Ces échecs se disent au journal : la photo manquait chez un joueur, deux records de suite,
        // et rien ne le montrait (2026-10-10).
        try
        {
            var envoye = parMame
                ? await _mame.RequestSnapshotAsync(CancellationToken.None).ConfigureAwait(false)
                : true;
            if (!parMame) await _retroarch.ScreenshotAsync(CancellationToken.None).ConfigureAwait(false);
            if (!envoye) { _logger.LogInformation("Capture record : MAME n'a pas pris la demande, pas de photo."); return; }
        }
        catch (Exception ex) { _logger.LogInformation("Capture record : commande refusée ({Erreur}), pas de photo.", ex.Message); return; }

        var dossiers = parMame ? DossiersMame() : CapturesRetroArch.Dossiers();
        var apparu = await AttendreFichierAsync(avant, parMame, dossiers).ConfigureAwait(false);
        if (apparu is null)
        {
            _logger.LogInformation("Capture record : aucune image apparue en {Secondes} s dans {Dossiers}, pas de photo.",
                AttenteFichier.TotalSeconds,
                dossiers.Count == 0 ? "(aucun dossier)" : string.Join(" ; ", dossiers.Select(d => Directory.Exists(d) ? d : d + " (absent)")));
            return;
        }

        try
        {
            Directory.CreateDirectory(DossierLocal);
            var destination = Path.Combine(DossierLocal, "pending-" + ScoreShotRegles.NomDeFichier(regles[0]) + ".png");
            // DÉPLACER, pas copier : le dossier des captures appartient au joueur, et on ne va
            // pas y laisser une image par sommet. Si la fenêtre de deux secondes nous faisait
            // adopter une capture prise à la main au même instant, elle serait ici, pas perdue.
            File.Move(apparu, destination, overwrite: true);
            // Meme raison que pour la regeneration : le tampon du coeur n'est pas oriente.
            if (OperatingSystem.IsWindows()) ScoreShotImage.Redresser(destination, _systemId, _romGroup, _logger);
            var gardes = new List<(string Regle, string Fichier)> { (regles[0], destination) };
            // Les autres règles tombées au même instant partagent l'image (une copie chacune : elles montent et s'effacent seules).
            for (var i = 1; i < regles.Count; i++)
            {
                var copie = Path.Combine(DossierLocal, "pending-" + ScoreShotRegles.NomDeFichier(regles[i]) + ".png");
                File.Copy(destination, copie, overwrite: true);
                gardes.Add((regles[i], copie));
            }
            lock (_gate)
            {
                foreach (var (regle, fichier) in gardes)
                    if (_regles.TryGetValue(regle, out var e)) e.Fichier = fichier;
            }
            _logger.LogInformation("Capture record : image gardée pour {Rom} ({Regles}) à {Score}.",
                _romGroup, string.Join(", ", regles), score.ToString("N0"));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Capture record : image non conservée."); }
    }

    /// <summary>
    /// Attend qu'un PNG apparaisse. On cherche dans les DEUX endroits possibles, et en
    /// profondeur : RetroArch écrit à plat, MAME range par jeu (<c>snap/&lt;rom&gt;/0000.png</c>).
    /// </summary>
    private static async Task<string?> AttendreFichierAsync(DateTime apres, bool parMame, IReadOnlyList<string> dossiers)
    {
        if (dossiers.Count == 0) return null;
        var limite = DateTime.UtcNow + AttenteFichier;
        while (DateTime.UtcNow < limite)
        {
            foreach (var dossier in dossiers)
            {
                try
                {
                    // RetroArch : un sous-dossier par jeu s'il range ses captures par contenu, et un
                    // dossier absent est guetté (il peut le créer en écrivant). Voir CapturesRetroArch.
                    var candidat = parMame
                        ? new DirectoryInfo(dossier)
                            .EnumerateFiles("*.png", SearchOption.AllDirectories)
                            .Where(f => f.LastWriteTimeUtc >= apres)
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .FirstOrDefault()
                        : CapturesRetroArch.PlusRecente(dossier, apres);
                    // Fichier encore en cours d'écriture : on le saute, il sera là au tour suivant.
                    if (candidat is not null && candidat.Length > 0 && Lisible(candidat.FullName))
                        return candidat.FullName;
                }
                catch { /* le dossier bouge sous nos pieds : on retente */ }
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Les dossiers où MAME peut avoir écrit. On ne devine pas : on regarde les deux
    /// que RetroBat utilise, et le plus récent tranche.</summary>
    private static List<string> DossiersMame()
    {
        var candidats = new[]
        {
            Path.Combine(RetroBatPaths.RetroBatRoot, "screenshots"),
            Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "mame", "snap"),
        };
        return candidats.Where(Directory.Exists).ToList();
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


    /// <summary>Le verdict est tombé : l'image de SA règle monte si le score a été publié, sinon elle part.
    /// Une partie envoie un verdict par classement (1CC puis 1LC...) : chacun prend l'image de sa règle.</summary>
    private async Task OnVerdictAsync(JsonElement root)
    {
        var regle = Texte(root, "Ruleset");
        string? fichier;
        lock (_gate)
        {
            fichier = _regles.TryGetValue(regle, out var e) ? e.Fichier : null;
        }
        if (fichier is null) return;

        var statut = Texte(root, "Status");
        var session = Texte(root, "SessionId");
        if (statut != "published" || session.Length == 0)
        {
            OublierRegle(regle, $"verdict « {(statut.Length > 0 ? statut : "inconnu")} »");
            return;
        }

        try
        {
            var credential = ResolveCredential();
            if (string.IsNullOrEmpty(credential)) { OublierRegle(regle, "machine non appairée"); return; }

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
            _logger.LogInformation("Capture record : envoi {Regle} {Code} - {Corps}", regle, (int)response.StatusCode, corps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture record : envoi impossible.");
        }
        finally
        {
            // Dans tous les cas on ne garde rien de cette règle : l'image d'une partie finie n'a plus d'usage
            // ici, et la plateforme est la seule à décider si elle valait d'être conservée.
            OublierRegle(regle, "envoi terminé");
        }
    }

    /// <summary>Efface l'image d'une seule règle (son verdict est tombé) ; les autres attendent le leur.</summary>
    private void OublierRegle(string regle, string raison)
    {
        string? fichier = null;
        lock (_gate)
        {
            if (_regles.TryGetValue(regle, out var e)) { fichier = e.Fichier; e.Fichier = null; }
        }
        if (fichier is null) return;
        try { File.Delete(fichier); } catch { /* elle repartira au prochain remplacement */ }
        _logger.LogDebug("Capture record : image de {Regle} écartée ({Raison}).", regle, raison);
    }

    /// <summary>Nouvelle partie : toutes les images en attente partent (les verdicts de la précédente sont passés).</summary>
    private void Oublier(string raison)
    {
        List<string> fichiers;
        lock (_gate)
        {
            fichiers = _regles.Values.Where(e => e.Fichier is not null).Select(e => e.Fichier!).ToList();
            foreach (var e in _regles.Values) { e.Fichier = null; e.Arme = false; e.ScorePhotographie = 0; }
            _photosDeLaPartie = 0;
        }
        foreach (var f in fichiers)
        {
            try { File.Delete(f); } catch { /* elle repartira au prochain remplacement */ }
        }
        if (fichiers.Count > 0) _logger.LogDebug("Capture record : {N} image(s) écartée(s) ({Raison}).", fichiers.Count, raison);
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
