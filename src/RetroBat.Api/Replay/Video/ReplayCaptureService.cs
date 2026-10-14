using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
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
/// <item>RetroArch filme l'image du coeur agrandie x4 (<c>video_record_scale_factor</c>, pose au demarrage), sans
/// les shaders ni les decors du joueur. Le film demarre et s'arrete avec le replay de la partie.</item>
/// <item>Un garde-fou (<see cref="GardeDeCapture"/>) coupe la capture si le jeu ralentit ; la borne ne filme
/// alors plus pendant 30 jours.</item>
/// <item>Un jeu vertical sort couche (RetroArch 1.22 filme avant de tourner l'image) : il se reconnait a l'en-tete
/// du film (<see cref="MatroskaEntete"/>). Le sens de rotation, annonce par le coeur dans le journal de lancement,
/// permet de le redresser a la fin en corrigeant l'en-tete, sans reencoder (<see cref="MatroskaRotation"/>) ;
/// YouTube l'affiche alors droit. Sans sens connu, la capture s'arrete aussitot.</item>
/// <item>Apres la partie, hors de toute partie, la borne demande au site si c'est un record : oui, elle envoie le
/// film directement chez Google ; non, elle l'efface. Sans reponse decidable, elle redemande, puis efface.</item>
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

    /// <summary>En dessous, le film est trop petit pour YouTube : l'echelle x4 n'a pas pris.</summary>
    private const long LargeurMinimale = 512;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IEventBus _bus;
    private readonly RetroArchReplayClient _ra;
    private readonly ReplayVideoPublisher _publication;
    private readonly IReplayManifestStore _manifests;
    private readonly ReplayCoreTimingProbe _coeur;
    private readonly ILogger<ReplayCaptureService> _logger;
    private readonly bool _actif;
    private readonly double _espaceLibreMinGo;
    private readonly TimeSpan _ageMax;
    private readonly TimeSpan _intervalleEnvoi;
    private readonly long _tailleMax;
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
        public readonly GardeDeCapture Garde = new();
        public readonly Stopwatch Chrono = Stopwatch.StartNew();
    }

    /// <summary>
    /// Une capture gardee sur la borne : en attente de la decision du site, en cours d'envoi (<paramref name="Session"/>,
    /// pour reprendre la ou l'envoi s'est arrete), puis envoyee (<paramref name="VideoId"/>) jusqu'a ce que la video
    /// soit prete chez YouTube.
    /// </summary>
    public sealed record CaptureGardee(string ReplayId, string Fichier, long DebutFilm, long FinFilm, DateTime CreeeUtc,
        string? Session = null, string? VideoId = null, DateTime? EnvoyeeUtc = null);

    /// <summary>Une video envoyee qui n'est toujours pas prete apres ce delai : le film n'est plus garde.</summary>
    private static readonly TimeSpan PreteAuPlus = TimeSpan.FromHours(24);

    public ReplayCaptureService(IEventBus bus, RetroArchReplayClient ra, ReplayVideoPublisher publication,
        IReplayManifestStore manifests, IReplayObjectStore objects, ReplayCoreTimingProbe coeur, IConfiguration configuration,
        ILogger<ReplayCaptureService> logger)
    {
        _bus = bus; _ra = ra; _publication = publication; _manifests = manifests; _coeur = coeur; _logger = logger;
        _actif = configuration.GetValue("Video:Capture:Enabled", true);
        _espaceLibreMinGo = Math.Max(1, configuration.GetValue("Video:Capture:MinFreeGb", 5.0));
        _ageMax = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Video:Capture:MaxAgeHours", 48)));
        _intervalleEnvoi = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Capture:UploadIntervalMinutes", 2)));
        // Le plus gros film envoye (300 Mo, le plafond du site) : au-dela, la capture s'arrete en pleine partie.
        _tailleMax = Math.Max(1, configuration.GetValue("Video:Capture:MaxMb", 300)) * 1_000_000L;
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
            ImposerLEchelle();
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

    /// <summary>Le replay de la partie demarre : la capture aussi, si la borne le peut.</summary>
    private async Task DemarrerAsync(string replayId)
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
            _enCours = new CaptureEnCours { ReplayId = replayId, Fichier = fichier, FrameDebut = frame, Rotation = rotation };
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
            _enCours = null;

            // Un film qui ne grossit plus est deja ferme (RetroArch quitte) : un RECORDING_TOGGLE en
            // relancerait un autre.
            if (await GrossitAsync(c.Fichier).ConfigureAwait(false))
                await _ra.RecordingToggleAsync(_arret).ConfigureAwait(false);
            await AttendreLaFinAsync(c.Fichier).ConfigureAwait(false);

            if (abandon)
            {
                Effacer(c.Fichier);
                _logger.LogInformation("Capture : {ReplayId} abandonnee ({Pourquoi}), film efface.", c.ReplayId, pourquoi);
                return;
            }
            // Jeu vertical (ou retourne) : on inscrit la rotation dans l'en-tete, sans toucher aux images.
            if (c.Rotation is 1 or 2 or 3)
            {
                if (MatroskaRotation.Redresser(c.Fichier, c.Rotation.Value))
                    _logger.LogInformation("Capture : {ReplayId} redressee (rotation {Rotation} du coeur).", c.ReplayId, c.Rotation);
                else
                    _logger.LogInformation("Capture : {ReplayId}, rotation non inscrite dans le film.", c.ReplayId);
            }
            var entete = MatroskaEntete.Lire(c.Fichier);
            if (entete is null || entete.Couchee || entete.Largeur < LargeurMinimale)
            {
                Effacer(c.Fichier);
                _logger.LogInformation("Capture : {ReplayId} non gardee ({Raison}).", c.ReplayId,
                    entete is null ? "film illisible" : entete.Couchee ? "jeu vertical, film couche" : "image trop petite");
                return;
            }

            var destination = Path.Combine(_dossier, c.ReplayId + ".mkv");
            Effacer(destination);
            File.Move(c.Fichier, destination);
            var gardee = new CaptureGardee(c.ReplayId, destination, c.FrameDebut,
                Math.Max(c.FrameDebut, derniereFrame ?? 0), DateTime.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(_dossier, c.ReplayId + ".json"),
                JsonSerializer.Serialize(gardee, Json), Encoding.UTF8, _arret).ConfigureAwait(false);
            _logger.LogInformation("Capture : {ReplayId} gardee ({Taille} octets, {Pourquoi}), en attente de la decision du site.",
                c.ReplayId, new FileInfo(destination).Length, pourquoi);
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

    /// <summary>Pendant la capture : l'en-tete (jeu vertical ?) puis la cadence du jeu.</summary>
    private async Task SurveillerAsync(CancellationToken ct)
    {
        var c = _enCours;
        if (c is null) return;

        if (!c.EnteteVerifiee && MatroskaEntete.Lire(c.Fichier) is { } entete)
        {
            c.EnteteVerifiee = true;
            // Un film couche se redresse a la fin, si l'on sait dans quel sens.
            var redressable = entete.Couchee && c.Rotation is 1 or 3;
            if ((entete.Couchee && !redressable) || entete.Largeur < LargeurMinimale)
            {
                await ArreterAsync(c.ReplayId, null, abandon: true,
                    entete.Couchee ? "jeu vertical, sens de rotation inconnu" : "image de " + entete.Largeur + " pixels, echelle x4 absente").ConfigureAwait(false);
                return;
            }
        }

        if (TailleDe(c.Fichier) > _tailleMax)
        {
            await ArreterAsync(c.ReplayId, null, abandon: true, "film plus gros que " + _tailleMax / 1_000_000 + " Mo").ConfigureAwait(false);
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
            if (TailleDe(g.Fichier) > _tailleMax)
            {
                Oublier(g, fiche, "plus grosse que " + _tailleMax / 1_000_000 + " Mo");
                continue;
            }

            using var envoi = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_verrou) _envoiEnCours = envoi;
            try
            {
                var courante = g;
                var resultat = await _publication.EnvoyerCaptureAsync(courante.ReplayId, courante.Fichier, courante.DebutFilm, fin, fps,
                    courante.Session, async session =>
                    {
                        courante = courante with { Session = session };
                        await SauverAsync(courante, fiche, stoppingToken).ConfigureAwait(false);
                    }, envoi.Token).ConfigureAwait(false);
                switch (resultat.Issue)
                {
                    case ReplayVideoPublisher.IssueDeCapture.Envoyee:
                        await SauverAsync(courante with { Session = null, VideoId = resultat.VideoId, EnvoyeeUtc = DateTime.UtcNow },
                            fiche, stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation("Capture : {ReplayId} envoyee ({Detail}), film garde jusqu'a la video prete.",
                            courante.ReplayId, resultat.Detail);
                        break;
                    case ReplayVideoPublisher.IssueDeCapture.Inutile:
                        Oublier(courante, fiche, resultat.Detail);
                        break;
                    default:
                        if (resultat.Detail is "pas_encore" or "replay.not_owned" && age > PasEncoreAuPlus)
                            Oublier(courante, fiche, "toujours pas reliee a un score classe");
                        else _logger.LogInformation("Capture : {ReplayId} attend ({Detail}).", courante.ReplayId, resultat.Detail);
                        break;
                }
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

    private static async Task SauverAsync(CaptureGardee g, string fiche, CancellationToken ct)
    {
        var temporaire = fiche + ".tmp";
        await File.WriteAllTextAsync(temporaire, JsonSerializer.Serialize(g, Json), Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temporaire, fiche, overwrite: true);
    }

    private void Oublier(CaptureGardee g, string fiche, string pourquoi)
    {
        Effacer(g.Fichier);
        Effacer(fiche);
        _logger.LogInformation("Capture : {ReplayId} {Pourquoi}, film efface de la borne.", g.ReplayId, pourquoi);
    }

    /// <summary>
    /// L'echelle x4 des films de RetroArch : une image de jeu de 256 pixels de large n'aurait sur YouTube que du
    /// 240p flou. Posee au demarrage, hors partie, comme les autres reglages qu'APIExpose impose a RetroArch.
    /// </summary>
    private void ImposerLEchelle()
    {
        try
        {
            var chemin = RetroBatPaths.RetroArchConfigPath;
            if (!File.Exists(chemin) || Process.GetProcessesByName("retroarch").Length > 0) return;
            var contenu = File.ReadAllText(chemin);
            var nouveau = AvecReglage(contenu, "video_record_scale_factor", EchelleVoulue);
            if (nouveau is null) return;
            File.WriteAllText(chemin, nouveau, new UTF8Encoding(false));
            _logger.LogInformation("Capture : echelle des films de RetroArch posee a x{Echelle}.", EchelleVoulue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : echelle des films non posee.");
        }
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
