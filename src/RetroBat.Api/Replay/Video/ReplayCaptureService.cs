using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LA CAPTURE DES PARTIES (decision du 14 octobre 2026). La borne filme chaque partie enregistree, avec RetroArch,
/// pendant le jeu ; le film ne part sur YouTube que si la partie est un record, sinon il est efface.
/// <list type="bullet">
/// <item>RetroArch filme l'image du coeur A SA TAILLE D'ORIGINE (decision du 15 octobre 2026 ; avant, agrandie x4), sans
/// les shaders ni les decors du joueur, avec nos reglages d'encodage (<see cref="ReglagesDuFilm"/> : x264 medium,
/// animation, CRF 14, 4:4:4). Meme fidelite que le film x4 d'avant pour 59 % de son poids et 0,27 coeur au lieu de
/// 0,69. Le film demarre et s'arrete avec le replay de la partie. <c>Video:Capture:Format = "agrandi"</c> revient au
/// film x4.</item>
/// <item>Un film a la taille d'origine ne part pas tel quel sur YouTube : pour un record, la borne fabrique d'abord sa
/// version YouTube (redressee, agrandie sans lissage, FLV) avec NelfePlay.LiveStreaming.exe, hors partie.</item>
/// <item>Un garde-fou (<see cref="GardeDeCapture"/>) coupe la capture si le jeu ralentit ; la borne ne filme
/// alors plus pendant 30 jours.</item>
/// <item>Un jeu vertical sort couche (RetroArch 1.22 filme avant de tourner l'image) : il se reconnait a l'en-tete
/// du film (<see cref="MatroskaEntete"/>). Le sens de rotation, annonce par le coeur dans le journal de lancement,
/// permet de le redresser a la fin en corrigeant l'en-tete, sans reencoder (<see cref="MatroskaRotation"/>) ;
/// YouTube l'affiche alors droit. Sans sens connu, la capture s'arrete aussitot.</item>
/// <item>Apres la partie, hors de toute partie, la borne demande au site si c'est un record : oui, elle envoie le
/// film directement chez Google ; non, elle l'efface. Sans reponse decidable, elle redemande, puis efface.</item>
/// <item>UNE PARTIE SANS REPLAY SE FILME AUSSI (19XX sous le coeur MAME, 14 octobre 2026). Sous un coeur ou
/// l'enregistreur renonce au replay (RetroArch 1.22.2 plante en encodant l'etat du coeur MAME), le film part au meme
/// depart (<c>replay.recording.skipped</c>) et s'arrete a la fin de la partie certifiee, a la baisse du score ou a la
/// fin du jeu. Il porte un identifiant a lui (<c>fm_</c>) et les sessions des scores publies pendant et juste apres
/// lui : le site y retrouve la partie et son rang. La cadence du jeu se suit alors aux trames que le plugin Lua de
/// MAME joint a ses signaux.</item>
/// </list>
/// </summary>
public sealed class ReplayCaptureService : BackgroundService
{
    private static readonly TimeSpan Releve = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PasEncoreAuPlus = TimeSpan.FromHours(3);

    /// <summary>
    /// Avant la premiere demande : le replay se finalise, le score part, le site les relie et depose le replay sur
    /// le miroir, sans quoi il ne s'affiche pas encore au classement. Quelques secondes a une minute.
    /// </summary>
    private static readonly TimeSpan PremiereDemandeApres = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModesteDurant = TimeSpan.FromDays(30);
    private const string EchelleVoulue = "4";

    /// <summary>En dessous, un film agrandi est trop petit pour YouTube : l'echelle x4 n'a pas pris.</summary>
    private const long LargeurMinimale = 512;

    /// <summary>
    /// Les reglages d'encodage du film a la taille d'origine (mesures du 15 octobre 2026, CDC video V2 §9 bis) : x264
    /// medium, tune animation, CRF 14, en 4:4:4 (la couleur de chaque pixel du jeu), son AAC a 48 kHz. Le facteur
    /// d'agrandissement de retroarch.cfg ne compte pas avec un fichier de reglages : celui-ci dit 1.
    /// </summary>
    internal const string ReglagesDuFilm =
        "vcodec = \"libx264\"\n" +
        "acodec = \"aac\"\n" +
        "pix_fmt = \"yuv444p\"\n" +
        "format = \"matroska\"\n" +
        "threads = \"2\"\n" +
        "scale_factor = \"1\"\n" +
        "sample_rate = \"48000\"\n" +
        "video_preset = \"medium\"\n" +
        "video_tune = \"animation\"\n" +
        "video_crf = \"14\"\n" +
        "audio_b = \"128k\"\n";

    /// <summary>Le fichier de ces reglages, que retroarch.cfg designe (<c>video_record_config</c>).</summary>
    internal static string CheminDesReglagesDuFilm
        => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "film-origine.cfg");

    /// <summary>Films a la taille d'origine (par defaut), ou agrandis par RetroArch comme avant le 15 octobre 2026.</summary>
    internal static bool FilmsALaTailleDOrigine(IConfiguration configuration)
        => !string.Equals(configuration.GetValue("Video:Capture:Format", "origine"), "agrandi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apres trois fabrications ratees de sa version YouTube, un film n'est plus garde.</summary>
    private const int EchecsDeVersionMax = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IEventBus _bus;
    private readonly RetroArchReplayClient _ra;
    private readonly ReplayVideoPublisher _publication;
    private readonly IReplayManifestStore _manifests;
    private readonly ReplayCoreTimingProbe _coeur;
    private readonly ILogger<ReplayCaptureService> _logger;
    private readonly bool _actif;
    private readonly bool _origine;
    private readonly double _espaceLibreMinGo;
    private readonly TimeSpan _ageMax;
    private readonly TimeSpan _intervalleEnvoi;
    private readonly long _tailleMax;
    private readonly long _tailleLocaleMax;
    private readonly string _dossier;

    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _reveil = new(0);
    private readonly object _verrou = new();
    private CaptureEnCours? _enCours;
    private CancellationTokenSource? _envoiEnCours;
    private IDisposable? _abonnement;
    private CancellationToken _arret;

    private sealed class CaptureEnCours
    {
        public required string ReplayId;
        public required string Fichier;
        public long FrameDebut;
        /// <summary>La rotation annoncee par le coeur (quarts de tour antihoraires), null si inconnue.</summary>
        public int? Rotation;
        public bool EnteteVerifiee;
        /// <summary>Le film d'une partie sans replay : il s'arrete et se suit sans l'enregistreur.</summary>
        public bool SansReplay;
        /// <summary>D'ou viennent les trames qui suivent sa cadence (un seul emetteur, sinon les compteurs se melent).</summary>
        public string? SourceDesTrames;
        public bool Lent;
        public readonly GardeDeCapture Garde = new();
        public readonly Stopwatch Chrono = Stopwatch.StartNew();
    }

    /// <summary>
    /// Une capture gardee sur la borne : en attente de la decision du site, en cours d'envoi (<paramref name="Session"/>,
    /// pour reprendre la ou l'envoi s'est arrete), puis envoyee (<paramref name="VideoId"/>) jusqu'a ce que la video
    /// soit prete chez YouTube. Le film d'une partie sans replay (<paramref name="SansReplay"/>) porte les sessions de
    /// ses scores (<paramref name="Sessions"/>), par lesquelles le site retrouve la partie. Un film a la taille d'origine
    /// (<paramref name="Origine"/>) part par sa version YouTube (<paramref name="Version"/>, fabriquee avec le sens du
    /// jeu, <paramref name="Rotation"/>).
    /// </summary>
    public sealed record CaptureGardee(string ReplayId, string Fichier, long DebutFilm, long FinFilm, DateTime CreeeUtc,
        string? Session = null, string? VideoId = null, DateTime? EnvoyeeUtc = null,
        IReadOnlyList<string>? Sessions = null, bool SansReplay = false,
        bool Origine = false, int? Rotation = null, string? Version = null, int EchecsDeVersion = 0);

    /// <summary>Le prefixe des films sans replay : le site les reconnait et les relie par les sessions des scores.</summary>
    internal const string PrefixeSansReplay = "fm_";

    /// <summary>Les verdicts arrives juste apres un film sans replay sont les siens (la partie vient de finir).</summary>
    private static readonly TimeSpan SessionsTardivesPendant = TimeSpan.FromMinutes(10);

    private readonly object _verrouSessions = new();
    private readonly Dictionary<string, HashSet<string>> _sessionsDesFilms = new(StringComparer.Ordinal);
    private (string Id, DateTime Fin)? _dernierSansReplay;

    /// <summary>Une video envoyee qui n'est toujours pas prete apres ce delai : le film n'est plus garde.</summary>
    private static readonly TimeSpan PreteAuPlus = TimeSpan.FromHours(24);

    public ReplayCaptureService(IEventBus bus, RetroArchReplayClient ra, ReplayVideoPublisher publication,
        IReplayManifestStore manifests, IReplayObjectStore objects, ReplayCoreTimingProbe coeur, IConfiguration configuration,
        ILogger<ReplayCaptureService> logger)
    {
        _bus = bus; _ra = ra; _publication = publication; _manifests = manifests; _coeur = coeur; _logger = logger;
        _actif = configuration.GetValue("Video:Capture:Enabled", true);
        _origine = FilmsALaTailleDOrigine(configuration);
        _espaceLibreMinGo = Math.Max(1, configuration.GetValue("Video:Capture:MinFreeGb", 5.0));
        _ageMax = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Video:Capture:MaxAgeHours", 48)));
        _intervalleEnvoi = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Capture:UploadIntervalMinutes", 2)));
        // Le plus gros film ENVOYE (8 Go, le plafond du site : la version YouTube d'une partie de 19XX pese environ 3,7 Go
        // de l'heure). Le film sur le disque, lui, continue au-dela (decision du 15 octobre 2026) : le direct d'un record
        // tombe tard dans une longue partie, et il est la copie du film.
        _tailleMax = Math.Max(1, configuration.GetValue("Video:Capture:MaxMb", 8000)) * 1_000_000L;
        _tailleLocaleMax = Math.Max(_tailleMax / 1_000_000L, configuration.GetValue("Video:Capture:MaxLocalMb", 4000L)) * 1_000_000L;
        _dossier = Path.GetFullPath(Path.Combine(objects.TempRoot, "..", "captures"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _arret = stoppingToken;
        if (!_actif)
        {
            _logger.LogInformation("Capture : desactivee (Video:Capture:Enabled).");
            return;
        }
        try
        {
            Directory.CreateDirectory(_dossier);
            PreparerRetroArch();
            _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : demarrage impossible, aucune partie ne sera filmee.");
            return;
        }
        _logger.LogInformation("Capture : active, films dans {Dossier}.", _dossier);

        var prochainEnvoi = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reveille = await _reveil.WaitAsync(Releve, stoppingToken).ConfigureAwait(false);
                await SurveillerAsync(stoppingToken).ConfigureAwait(false);
                if (reveille || DateTime.UtcNow >= prochainEnvoi)
                {
                    prochainEnvoi = DateTime.UtcNow + _intervalleEnvoi;
                    await EnvoyerLesCapturesAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // L'hote s'arrete sur toute exception d'un service de fond (StopHost) : un tour rate ne doit
                // jamais emporter l'API.
                _logger.LogWarning(ex, "Capture : tour en echec.");
            }
        }
    }

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "replay.recording.started":
                    if (Chaine(e.Payload, "ReplayId") is { } demarre) _ = Task.Run(() => DemarrerAsync(demarre));
                    break;
                case "replay.recording.stopped":
                    if (Chaine(e.Payload, "ReplayId") is { } arrete)
                        _ = Task.Run(() => ArreterAsync(arrete, Entier(e.Payload, "LastFrame"), abandon: false, "fin du replay"));
                    break;
                // Une partie RetroArch sans replay (coeur MAME) : le film part quand meme, et s'arrete sans l'enregistreur.
                case "replay.recording.skipped":
                    _ = Task.Run(() => DemarrerAsync(PrefixeSansReplay + Ulid.New(), sansReplay: true));
                    break;
                case "scoring.run.ended":
                case "scoring.replay.stop":
                    if (_enCours is { SansReplay: true } sansReplay)
                        _ = Task.Run(() => ArreterAsync(sansReplay.ReplayId, null, abandon: false,
                            e.Type == "scoring.run.ended" ? "fin de la partie certifiee" : "le score a baisse"));
                    break;
                case "scoring.verdict":
                    NoterSession(Chaine(e.Payload, "SessionId"));
                    break;
                case "ingame.memory.changed":
                    if (_enCours is { SansReplay: true } suivi && TrameDuJeu(e.Payload) is { } trame)
                        SuivreLaCadence(suivi, trame.Source, trame.Frame);
                    break;
                case "ui.game.ended":
                    _ = Task.Run(() => ArreterAsync(null, null, abandon: false, "fin de partie"));
                    break;
                case "ui.game.started":
                    // Le joueur passe avant : un envoi en cours s'arrete, il reprendra apres la partie.
                    lock (_verrou)
                    {
                        try { _envoiEnCours?.Cancel(); } catch (Exception) { /* deja termine */ }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>
    /// Le replay de la partie demarre : la capture aussi, si la borne le peut. <paramref name="sansReplay"/> : la partie
    /// part sans replay, le film porte alors son propre identifiant.
    /// </summary>
    private async Task DemarrerAsync(string replayId, bool sansReplay = false)
    {
        await _operation.WaitAsync(_arret).ConfigureAwait(false);
        try
        {
            if (_enCours is not null || EstModeste()) return;
            var reglages = LireRetroArch();
            if (string.Equals(Valeur(reglages, "video_gpu_record"), "true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Capture : RetroArch filme l'image affichee (video_gpu_record), partie non filmee.");
                return;
            }
            var dossier = Valeur(reglages, "recording_output_directory");
            if (string.IsNullOrWhiteSpace(dossier))
            {
                _logger.LogInformation("Capture : aucun dossier de films dans retroarch.cfg, partie non filmee.");
                return;
            }
            Directory.CreateDirectory(dossier);
            if (EspaceLibreGo(dossier) is { } libre && libre < _espaceLibreMinGo)
            {
                _logger.LogInformation("Capture : {Libre:F1} Go libres, partie non filmee.", libre);
                return;
            }

            var avant = new HashSet<string>(Directory.GetFiles(dossier, "*.mkv"), StringComparer.OrdinalIgnoreCase);
            await _ra.RecordingToggleAsync(_arret).ConfigureAwait(false);
            var frame = (await _ra.GetActiveReplayAsync(_arret).ConfigureAwait(false))?.Frame ?? 0;
            string? fichier = null;
            for (var i = 0; i < 25 && fichier is null; i++)
            {
                await Task.Delay(200, _arret).ConfigureAwait(false);
                fichier = Directory.GetFiles(dossier, "*.mkv").Where(f => !avant.Contains(f))
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            if (fichier is null)
            {
                _logger.LogInformation("Capture : RetroArch n'a pas commence a filmer {ReplayId}.", replayId);
                return;
            }
            var statut = await _ra.GetStatusAsync(_arret).ConfigureAwait(false);
            var rotation = _coeur.ReadRotation(statut?.Crc32);
            lock (_verrouSessions)
            {
                // Une autre partie commence : les verdicts qui suivent ne sont plus ceux du film d'avant.
                _dernierSansReplay = null;
                _enCours = new CaptureEnCours
                {
                    ReplayId = replayId, Fichier = fichier, FrameDebut = frame, Rotation = rotation, SansReplay = sansReplay,
                };
            }
            if (sansReplay)
                _logger.LogInformation("Capture : partie sans replay filmee sous {ReplayId} (rotation du coeur : {Rotation}).",
                    replayId, rotation?.ToString() ?? "inconnue");
            else
                _logger.LogInformation("Capture : partie {ReplayId} filmee depuis la frame {Frame} (rotation du coeur : {Rotation}).",
                    replayId, frame, rotation?.ToString() ?? "inconnue");
        }
        catch (OperationCanceledException) { /* arret de l'API */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : demarrage de {ReplayId} en echec.", replayId);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Arrete la capture en cours : RetroArch ferme le film, la borne le range (ou l'efface s'il est
    /// <paramref name="abandon"/>ne). <paramref name="replayId"/> null : quelle que soit la capture.
    /// </summary>
    private async Task ArreterAsync(string? replayId, long? derniereFrame, bool abandon, string pourquoi)
    {
        await _operation.WaitAsync(_arret).ConfigureAwait(false);
        try
        {
            var c = _enCours;
            if (c is null || (replayId is not null && replayId != c.ReplayId)) return;
            lock (_verrouSessions)
            {
                _enCours = null;
                // Le verdict du 1CC arrive juste apres la fin de la partie : il est encore a ce film.
                if (c.SansReplay && !abandon) _dernierSansReplay = (c.ReplayId, DateTime.UtcNow);
            }

            // Un film qui ne grossit plus est deja ferme (RetroArch quitte) : un RECORDING_TOGGLE en
            // relancerait un autre.
            if (await GrossitAsync(c.Fichier).ConfigureAwait(false))
                await _ra.RecordingToggleAsync(_arret).ConfigureAwait(false);
            await AttendreLaFinAsync(c.Fichier).ConfigureAwait(false);

            if (abandon)
            {
                Effacer(c.Fichier);
                OublierLesSessions(c.ReplayId);
                _logger.LogInformation("Capture : {ReplayId} abandonnee ({Pourquoi}), film efface.", c.ReplayId, pourquoi);
                return;
            }
            var entete = MatroskaEntete.Lire(c.Fichier);
            var origine = entete is { Origine: true };
            // Jeu vertical (ou retourne) : un film agrandi par RetroArch se redresse dans son en-tete, sans toucher aux
            // images ; un film a la taille d'origine se redresse dans sa version YouTube.
            if (!origine && c.Rotation is 1 or 2 or 3)
            {
                if (MatroskaRotation.Redresser(c.Fichier, c.Rotation.Value))
                    _logger.LogInformation("Capture : {ReplayId} redressee (rotation {Rotation} du coeur).", c.ReplayId, c.Rotation);
                else
                    _logger.LogInformation("Capture : {ReplayId}, rotation non inscrite dans le film.", c.ReplayId);
                entete = MatroskaEntete.Lire(c.Fichier);
            }
            if (entete is null || (entete.Couchee && !(origine && c.Rotation is 1 or 3)) || (!origine && entete.Largeur < LargeurMinimale))
            {
                Effacer(c.Fichier);
                OublierLesSessions(c.ReplayId);
                _logger.LogInformation("Capture : {ReplayId} non gardee ({Raison}).", c.ReplayId,
                    entete is null ? "film illisible" : entete.Couchee ? "jeu vertical, film couche" : "image trop petite");
                return;
            }

            var destination = Path.Combine(_dossier, c.ReplayId + ".mkv");
            Effacer(destination);
            File.Move(c.Fichier, destination);
            var gardee = new CaptureGardee(c.ReplayId, destination, c.FrameDebut,
                Math.Max(c.FrameDebut, derniereFrame ?? 0), DateTime.UtcNow,
                Sessions: c.SansReplay ? SessionsDuFilm(c.ReplayId, null) : null, SansReplay: c.SansReplay,
                Origine: origine, Rotation: c.Rotation);
            await File.WriteAllTextAsync(Path.Combine(_dossier, c.ReplayId + ".json"),
                JsonSerializer.Serialize(gardee, Json), Encoding.UTF8, _arret).ConfigureAwait(false);
            _logger.LogInformation("Capture : {ReplayId} gardee ({Taille} octets, {Format}, {Pourquoi}), en attente de la decision du site.",
                c.ReplayId, new FileInfo(destination).Length,
                origine ? "taille d'origine " + entete.Largeur + "x" + entete.Hauteur : "agrandi " + entete.Largeur + "x" + entete.Hauteur, pourquoi);
            _reveil.Release();
        }
        catch (OperationCanceledException) { /* arret de l'API */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : arret en echec.");
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Le film en cours d'ecriture et la rotation du coeur, pour le direct d'un record (le direct en est la copie) ;
    /// null quand rien n'est filme.
    /// </summary>
    public (string Fichier, int? Rotation)? FilmEnCours()
    {
        var c = _enCours;
        return c is null ? null : (c.Fichier, c.Rotation);
    }

    /// <summary>
    /// Le verdict d'un score : sa session est celle du film sans replay en cours, ou de celui qui vient de finir (dix
    /// minutes au plus, tant qu'aucune autre partie n'est filmee). Le film d'une partie avec replay n'en a pas besoin.
    /// </summary>
    private void NoterSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session)) return;
        lock (_verrouSessions)
        {
            string? film = null;
            if (_enCours is { SansReplay: true } c) film = c.ReplayId;
            else if (_enCours is null && _dernierSansReplay is { } d && DateTime.UtcNow - d.Fin < SessionsTardivesPendant) film = d.Id;
            if (film is null) return;
            if (!_sessionsDesFilms.TryGetValue(film, out var sessions))
                _sessionsDesFilms[film] = sessions = new HashSet<string>(StringComparer.Ordinal);
            sessions.Add(session.Trim());
        }
    }

    /// <summary>Les sessions connues d'un film : celles deja gardees, plus celles notees depuis.</summary>
    private IReadOnlyList<string> SessionsDuFilm(string film, IReadOnlyList<string>? deja)
    {
        lock (_verrouSessions)
        {
            var toutes = new SortedSet<string>(deja ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (_sessionsDesFilms.TryGetValue(film, out var notees)) toutes.UnionWith(notees);
            return toutes.ToList();
        }
    }

    private void OublierLesSessions(string film)
    {
        lock (_verrouSessions) _sessionsDesFilms.Remove(film);
    }

    /// <summary>
    /// La cadence d'une partie sans replay se lit aux trames emulees que les signaux du jeu portent (plugin Lua de
    /// MAME). Un seul emetteur : deux compteurs differents se meleraient.
    /// </summary>
    private void SuivreLaCadence(CaptureEnCours c, string source, long frame)
    {
        lock (_verrouSessions)
        {
            if (c.Lent || !ReferenceEquals(_enCours, c)) return;
            c.SourceDesTrames ??= source;
            if (!string.Equals(c.SourceDesTrames, source, StringComparison.Ordinal)) return;
            if (!c.Garde.Relever(c.Chrono.Elapsed.TotalSeconds, frame)) return;
            c.Lent = true;
        }
        MarquerModeste();
        _ = Task.Run(() => ArreterAsync(c.ReplayId, null, abandon: true,
            "le jeu ralentissait (reference " + c.Garde.Reference.ToString("F1") + " images/s)"));
    }

    /// <summary>L'emetteur et la trame emulee d'un signal du jeu (<c>ingame.memory.changed</c>), ou null. Pur.</summary>
    internal static (string Source, long Frame)? TrameDuJeu(object? payload)
    {
        try
        {
            var el = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("Source", out var source) || source.ValueKind != JsonValueKind.String
                || !el.TryGetProperty("signal", out var signal) || signal.ValueKind != JsonValueKind.Object
                || !signal.TryGetProperty("Frame", out var f) || f.ValueKind != JsonValueKind.Number
                || !f.TryGetInt64(out var frame) || frame <= 0)
                return null;
            return (source.GetString() ?? "", frame);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Pendant la capture : l'en-tete (jeu vertical ?) puis la cadence du jeu.</summary>
    private async Task SurveillerAsync(CancellationToken ct)
    {
        var c = _enCours;
        if (c is null) return;

        if (!c.EnteteVerifiee && MatroskaEntete.Lire(c.Fichier) is { } entete)
        {
            c.EnteteVerifiee = true;
            // Un film couche se redresse a la fin (ou dans sa version YouTube), si l'on sait dans quel sens. Un film a la
            // taille d'origine est petit par nature.
            var redressable = entete.Couchee && c.Rotation is 1 or 3;
            if ((entete.Couchee && !redressable) || (!entete.Origine && entete.Largeur < LargeurMinimale))
            {
                await ArreterAsync(c.ReplayId, null, abandon: true,
                    entete.Couchee ? "jeu vertical, sens de rotation inconnu" : "image de " + entete.Largeur + " pixels, echelle x4 absente").ConfigureAwait(false);
                return;
            }
        }

        // Le film depasse le plafond d'envoi sans s'arreter : il ne partira pas sur YouTube, mais le direct d'un record
        // tardif en est la copie. Seuls le plafond local et l'espace libre du disque l'arretent.
        if (TailleDe(c.Fichier) > _tailleLocaleMax)
        {
            await ArreterAsync(c.ReplayId, null, abandon: true, "film plus gros que " + _tailleLocaleMax / 1_000_000 + " Mo").ConfigureAwait(false);
            return;
        }
        if (EspaceLibreGo(Path.GetDirectoryName(c.Fichier) ?? _dossier) is { } libre && libre < _espaceLibreMinGo)
        {
            await ArreterAsync(c.ReplayId, null, abandon: true, "plus que " + libre.ToString("F1") + " Go libres").ConfigureAwait(false);
            return;
        }

        var actif = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
        if (actif is not { Recording: true }) return;
        if (c.Garde.Relever(c.Chrono.Elapsed.TotalSeconds, actif.Frame))
        {
            MarquerModeste();
            await ArreterAsync(c.ReplayId, null, abandon: true,
                "le jeu ralentissait (reference " + c.Garde.Reference.ToString("F1") + " images/s)").ConfigureAwait(false);
        }
    }

    /// <summary>Hors de toute partie : chaque capture gardee est envoyee si c'est un record, effacee sinon.</summary>
    private async Task EnvoyerLesCapturesAsync(CancellationToken stoppingToken)
    {
        if (_enCours is not null || EmulatorForeground.EmulateurTourne()) return;
        var fiches = Directory.Exists(_dossier)
            ? Directory.GetFiles(_dossier, "*.json").OrderBy(File.GetCreationTimeUtc).ToList()
            : new List<string>();
        foreach (var fiche in fiches)
        {
            if (_enCours is not null || EmulatorForeground.EmulateurTourne()) return;
            CaptureGardee? g = null;
            try { g = JsonSerializer.Deserialize<CaptureGardee>(await File.ReadAllTextAsync(fiche, stoppingToken).ConfigureAwait(false)); }
            catch (Exception) { /* fiche abimee */ }
            if (g is null || !File.Exists(g.Fichier))
            {
                Effacer(fiche);
                continue;
            }

            var manifest = _manifests.GetManifest(g.ReplayId);
            var fps = manifest?.Frames.NominalFps is > 1 ? manifest.Frames.NominalFps : 60;
            var fin = Math.Max(g.FinFilm, manifest?.Frames.ReplayEnd ?? 0);

            // Deja envoyee : le film reste sur la borne jusqu'a ce que la video soit prete. Refusee par YouTube,
            // ou perdue, elle repart ; confirmation manquee, elle est refaite.
            if (g.VideoId is not null)
            {
                var etat = await _publication.EtatDuFilmAsync(g.ReplayId, stoppingToken).ConfigureAwait(false);
                if (etat == "ready")
                {
                    Oublier(g, fiche, "prete sur YouTube (" + g.VideoId + ")");
                    continue;
                }
                if (etat is "failed" or "none")
                {
                    _logger.LogInformation("Capture : {ReplayId} refusee ou perdue par YouTube, renvoyee.", g.ReplayId);
                    g = g with { VideoId = null, Session = null, EnvoyeeUtc = null };
                    await SauverAsync(g, fiche, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    if (etat == "uploading")
                        await _publication.ConfirmerAsync(g.ReplayId, g.VideoId, g.DebutFilm, fin, fps, stoppingToken).ConfigureAwait(false);
                    if (DateTime.UtcNow - (g.EnvoyeeUtc ?? g.CreeeUtc) > PreteAuPlus)
                        Oublier(g, fiche, "envoyee il y a plus de 24 heures (" + g.VideoId + ")");
                    continue;
                }
            }

            var age = DateTime.UtcNow - g.CreeeUtc;
            // Trop tot : le site ne connait pas encore la partie.
            if (age < PremiereDemandeApres) continue;
            if (age > _ageMax)
            {
                Oublier(g, fiche, "trop ancienne");
                continue;
            }
            if (TailleDe(g.Version ?? g.Fichier) > _tailleMax)
            {
                Oublier(g, fiche, "plus grosse que " + _tailleMax / 1_000_000 + " Mo");
                continue;
            }
            // Sans replay : les sessions des scores arrives depuis la fin du film le rejoignent.
            if (g.SansReplay && SessionsDuFilm(g.ReplayId, g.Sessions) is { } sessions && sessions.Count != (g.Sessions?.Count ?? 0))
            {
                g = g with { Sessions = sessions };
                await SauverAsync(g, fiche, stoppingToken).ConfigureAwait(false);
            }

            using var envoi = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_verrou) _envoiEnCours = envoi;
            try
            {
                var courante = g;
                // Film a la taille d'origine : sa version YouTube se fabrique d'abord, et seulement pour un record.
                if (courante.Origine && (courante.Version is null || !File.Exists(courante.Version)))
                {
                    var voulue = await _publication.CaptureVoulueAsync(courante.ReplayId, TailleDe(courante.Fichier),
                        courante.Sessions, envoi.Token).ConfigureAwait(false);
                    if (voulue.Issue != ReplayVideoPublisher.IssueDeCapture.Voulue)
                    {
                        SuivreLeRefus(courante, fiche, voulue, age);
                        continue;
                    }
                    courante = await FabriquerLaVersionAsync(courante, fiche, envoi.Token).ConfigureAwait(false);
                    if (courante.Version is null) continue;
                    if (TailleDe(courante.Version) > _tailleMax)
                    {
                        Oublier(courante, fiche, "version YouTube plus grosse que " + _tailleMax / 1_000_000 + " Mo");
                        continue;
                    }
                }
                var resultat = await _publication.EnvoyerCaptureAsync(courante.ReplayId, courante.Version ?? courante.Fichier,
                    courante.DebutFilm, fin, fps, courante.Sessions, courante.Session, async session =>
                    {
                        courante = courante with { Session = session };
                        await SauverAsync(courante, fiche, stoppingToken).ConfigureAwait(false);
                    }, envoi.Token).ConfigureAwait(false);
                if (resultat.Issue == ReplayVideoPublisher.IssueDeCapture.Envoyee)
                {
                    await SauverAsync(courante with { Session = null, VideoId = resultat.VideoId, EnvoyeeUtc = DateTime.UtcNow },
                        fiche, stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Capture : {ReplayId} envoyee ({Detail}), film garde jusqu'a la video prete.",
                        courante.ReplayId, resultat.Detail);
                }
                else SuivreLeRefus(courante, fiche, resultat, age);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Capture : envoi de {ReplayId} interrompu, une partie commence.", g.ReplayId);
                return;
            }
            finally
            {
                lock (_verrou) _envoiEnCours = null;
            }
        }
    }

    /// <summary>
    /// Ce que le site n'accepte pas encore : un refus definitif efface le film ; une partie toujours pas reliee a un
    /// score classe apres trois heures aussi ; le reste attend le prochain passage.
    /// </summary>
    private void SuivreLeRefus(CaptureGardee g, string fiche, ReplayVideoPublisher.ResultatDeCapture r, TimeSpan age)
    {
        if (r.Issue == ReplayVideoPublisher.IssueDeCapture.Inutile) Oublier(g, fiche, r.Detail);
        else if (r.Detail is "pas_encore" or "replay.not_owned" && age > PasEncoreAuPlus)
            Oublier(g, fiche, "toujours pas reliee a un score classe");
        else _logger.LogInformation("Capture : {ReplayId} attend ({Detail}).", g.ReplayId, r.Detail);
    }

    /// <summary>
    /// La version YouTube d'un film a la taille d'origine (NelfePlay.LiveStreaming.exe), hors partie : redressee,
    /// agrandie sans lissage, en FLV a cote du film. Une partie qui commence l'arrete (elle reprendra) ; apres trois
    /// echecs, le film n'est plus garde. Rend la capture, avec sa version si elle est prete.
    /// </summary>
    private async Task<CaptureGardee> FabriquerLaVersionAsync(CaptureGardee g, string fiche, CancellationToken ct)
    {
        var exe = LanceurDuDirect.Chemin(AppContext.BaseDirectory);
        if (exe is null)
        {
            _logger.LogWarning("Capture : {ReplayId} attend sa version YouTube, {Exe} absent.", g.ReplayId, LanceurDuDirect.Exe);
            return g;
        }
        var version = Path.ChangeExtension(g.Fichier, ".flv");
        Effacer(version);
        _logger.LogInformation("Capture : {ReplayId} est un record, fabrication de sa version YouTube.", g.ReplayId);
        var r = await LanceurDuDirect.FabriquerLaVersionAsync(exe, g.Fichier, version, g.Rotation ?? 0, _logger, ct)
            .ConfigureAwait(false);
        if (r.Code == 0 && TailleDe(version) > 0)
        {
            var faite = g with { Version = version };
            await SauverAsync(faite, fiche, _arret).ConfigureAwait(false);
            _logger.LogInformation("Capture : version YouTube de {ReplayId} prete ({Taille} octets en {Duree:F0} s).",
                g.ReplayId, TailleDe(version), r.Duree.TotalSeconds);
            return faite;
        }
        Effacer(version);
        var ratee = g with { EchecsDeVersion = g.EchecsDeVersion + 1 };
        if (ratee.EchecsDeVersion >= EchecsDeVersionMax)
        {
            Oublier(ratee, fiche, "version YouTube impossible (" + (r.DerniereLigne ?? "code " + r.Code) + ")");
            return ratee;
        }
        await SauverAsync(ratee, fiche, _arret).ConfigureAwait(false);
        _logger.LogWarning("Capture : version YouTube de {ReplayId} ratee (code {Code} : {Ligne}), nouvel essai au prochain passage.",
            g.ReplayId, r.Code, r.DerniereLigne);
        return ratee;
    }

    private static async Task SauverAsync(CaptureGardee g, string fiche, CancellationToken ct)
    {
        var temporaire = fiche + ".tmp";
        await File.WriteAllTextAsync(temporaire, JsonSerializer.Serialize(g, Json), Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temporaire, fiche, overwrite: true);
    }

    private void Oublier(CaptureGardee g, string fiche, string pourquoi)
    {
        Effacer(g.Fichier);
        if (g.Version is not null) Effacer(g.Version);
        Effacer(fiche);
        OublierLesSessions(g.ReplayId);
        _logger.LogInformation("Capture : {ReplayId} {Pourquoi}, film efface de la borne.", g.ReplayId, pourquoi);
    }

    /// <summary>
    /// Les reglages des films de RetroArch, poses au demarrage, hors partie, comme les autres reglages qu'APIExpose
    /// impose a RetroArch (et avant chaque lancement par <see cref="EchelleDesFilms"/>). Taille d'origine : le fichier de
    /// reglages d'APIExpose. Film agrandi : l'echelle x4 au moins (une image de jeu de 256 pixels de large n'aurait sur
    /// YouTube que du 240p flou).
    /// </summary>
    private void PreparerRetroArch()
    {
        try
        {
            var chemin = RetroBatPaths.RetroArchConfigPath;
            if (!File.Exists(chemin) || Process.GetProcessesByName("retroarch").Length > 0) return;
            if (_origine) EcrireLesReglagesDuFilm();
            var contenu = File.ReadAllText(chemin);
            var nouveau = ReglagesDesFilms(contenu, _origine, CheminDesReglagesDuFilm);
            if (!_origine)
            {
                // Le facteur de chaque jeu se pose avant son lancement (EchelleDesFilms) : au demarrage, seul un facteur
                // absent ou de 1 (celui d'origine de RetroArch) devient x4.
                var courant = nouveau ?? contenu;
                if (!(int.TryParse(ValeurDans(courant, "video_record_scale_factor"), out var actuel) && actuel >= 2))
                    nouveau = AvecReglage(courant, "video_record_scale_factor", EchelleVoulue) ?? nouveau;
            }
            if (nouveau is null) return;
            File.WriteAllText(chemin, nouveau, new UTF8Encoding(false));
            _logger.LogInformation("Capture : RetroArch filmera {Format}.",
                _origine ? "a la taille d'origine (" + CheminDesReglagesDuFilm + ")" : "agrandi x" + ValeurDans(nouveau, "video_record_scale_factor"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : reglages des films non poses.");
        }
    }

    /// <summary>Le fichier de reglages du film a la taille d'origine, reecrit s'il manque ou differe.</summary>
    internal static void EcrireLesReglagesDuFilm()
    {
        var cfg = CheminDesReglagesDuFilm;
        if (File.Exists(cfg) && File.ReadAllText(cfg) == ReglagesDuFilm) return;
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, ReglagesDuFilm, new UTF8Encoding(false));
    }

    /// <summary>
    /// Le contenu de retroarch.cfg avec les reglages des films, ou null s'il les a deja. Taille d'origine : qualite
    /// « personnalisee » (0) et notre fichier <paramref name="cfg"/>. Film agrandi : si nos reglages etaient poses, la
    /// qualite moyenne de RetroArch (2) revient. Pur, pour etre teste.
    /// </summary>
    internal static string? ReglagesDesFilms(string contenu, bool origine, string cfg)
    {
        if (origine)
        {
            var avecQualite = AvecReglage(contenu, "video_record_quality", "0");
            var avecFichier = AvecReglage(avecQualite ?? contenu, "video_record_config", cfg);
            return avecFichier ?? avecQualite;
        }
        if (ValeurDans(contenu, "video_record_quality") != "0"
            || !string.Equals(ValeurDans(contenu, "video_record_config"), cfg, StringComparison.OrdinalIgnoreCase))
            return null;
        return AvecReglage(AvecReglage(contenu, "video_record_config", "") ?? contenu, "video_record_quality", "2");
    }

    /// <summary>Le contenu de retroarch.cfg avec <c>cle = "valeur"</c>, ou null s'il l'a deja. Pur, pour etre teste.</summary>
    internal static string? AvecReglage(string contenu, string cle, string valeur)
    {
        var fin = contenu.Contains("\r\n") ? "\r\n" : "\n";
        var lignes = contenu.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var voulue = cle + " = \"" + valeur + "\"";
        for (var i = 0; i < lignes.Count; i++)
        {
            var l = lignes[i].TrimStart();
            if (!l.StartsWith(cle, StringComparison.Ordinal)) continue;
            var reste = l[cle.Length..].TrimStart();
            if (!reste.StartsWith('=')) continue;
            if (lignes[i].Trim() == voulue) return null;
            lignes[i] = voulue;
            return string.Join(fin, lignes);
        }
        if (lignes.Count > 0 && lignes[^1].Length == 0) lignes.Insert(lignes.Count - 1, voulue);
        else lignes.Add(voulue);
        return string.Join(fin, lignes);
    }

    /// <summary>La valeur de <c>cle</c> dans un contenu de retroarch.cfg, ou null. Pur.</summary>
    internal static string? ValeurDans(string contenu, string cle)
    {
        foreach (var ligne in contenu.Split('\n'))
        {
            var egal = ligne.IndexOf('=');
            if (egal > 0 && ligne[..egal].Trim() == cle) return ligne[(egal + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static Dictionary<string, string> LireRetroArch()
    {
        var reglages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ligne in File.ReadLines(RetroBatPaths.RetroArchConfigPath))
        {
            var egal = ligne.IndexOf('=');
            if (egal <= 0) continue;
            reglages[ligne[..egal].Trim()] = ligne[(egal + 1)..].Trim().Trim('"');
        }
        return reglages;
    }

    private static string? Valeur(Dictionary<string, string> reglages, string cle)
        => reglages.TryGetValue(cle, out var v) ? v : null;

    private static double? EspaceLibreGo(string dossier)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dossier))!).AvailableFreeSpace / 1e9; }
        catch (Exception) { return null; }
    }

    /// <summary>Le film grossit-il encore ? (RetroArch filme toujours.)</summary>
    private async Task<bool> GrossitAsync(string fichier)
    {
        try
        {
            var avant = new FileInfo(fichier).Length;
            await Task.Delay(1200, _arret).ConfigureAwait(false);
            return new FileInfo(fichier).Length != avant;
        }
        catch (Exception) when (!_arret.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Attend que RetroArch ait fini d'ecrire le film (taille stable deux secondes, trente au plus).</summary>
    private async Task AttendreLaFinAsync(string fichier)
    {
        long precedente = -1;
        for (var i = 0; i < 30; i++)
        {
            long taille;
            try { taille = new FileInfo(fichier).Length; }
            catch (Exception) { return; }
            if (taille == precedente) return;
            precedente = taille;
            await Task.Delay(2000, _arret).ConfigureAwait(false);
        }
    }

    private string CheminDeCapacite => Path.Combine(_dossier, "capacite.json");

    /// <summary>La borne a ralenti en filmant : elle ne filme plus pendant 30 jours.</summary>
    private bool EstModeste()
    {
        try
        {
            if (!File.Exists(CheminDeCapacite)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(CheminDeCapacite));
            return doc.RootElement.TryGetProperty("modeste_depuis", out var d) && d.TryGetDateTime(out var depuis)
                && DateTime.UtcNow - depuis.ToUniversalTime() < ModesteDurant;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void MarquerModeste()
    {
        try
        {
            File.WriteAllText(CheminDeCapacite, JsonSerializer.Serialize(new { modeste_depuis = DateTime.UtcNow }, Json));
            _logger.LogWarning("Capture : le jeu ralentissait pendant la capture, cette borne ne filme plus pendant {Jours} jours.",
                ModesteDurant.TotalDays);
        }
        catch (Exception) { /* au pire, elle retentera */ }
    }

    private static long TailleDe(string chemin)
    {
        try { return new FileInfo(chemin).Length; }
        catch (Exception) { return 0; }
    }

    private static void Effacer(string chemin)
    {
        try { if (File.Exists(chemin)) File.Delete(chemin); }
        catch (Exception) { /* repris au prochain tour */ }
    }

    private static string? Chaine(object? payload, string nom)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? Entier(object? payload, string nom)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void Dispose()
    {
        _abonnement?.Dispose();
        base.Dispose();
    }
}
