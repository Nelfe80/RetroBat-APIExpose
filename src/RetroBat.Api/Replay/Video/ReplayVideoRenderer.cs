using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE RENDU VIDEO D'UN REPLAY (CDC video V2, §4). Rejoue le replay avec le vrai coeur, sans wrapper donc sans
/// scoring, en avance rapide, et n'enregistre que la performance : de son debut a sa fin, en frames du replay.
/// L'image est celle qu'affiche RetroArch (redressee pour un jeu vertical), agrandie sans lissage ; le son du jeu
/// est garde dans le film et coupe en sortie, la fenetre reste reduite : la borne peut etre devant quelqu'un.
///
/// Mesure du 14 octobre 2026 : environ le temps reel de la performance, de 1 a 1,5 fois selon le jeu.
/// Aucune ROM, aucun coeur ne quittent la machine ; le film reste dans la zone de travail du magasin des replays,
/// a la charge de l'appelant.
/// </summary>
public sealed class ReplayVideoRenderer
{
    /// <summary>Le port de commande de l'instance de rendu : jamais celui des parties (55355).</summary>
    public const int PortDeCommande = 55400;

    /// <summary>On s'arrete un peu avant la derniere frame : a la fin du replay, RetroArch ne repond plus.</summary>
    private const long MargeAvantLaFin = 30;

    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly IReplayRuntimeResolver _resolver;
    private readonly Func<string> _etatDeLaLecture;
    private readonly RetroBat.Api.Speedrun.CatalogueDesDefis? _defis;
    private readonly ILogger<ReplayVideoRenderer> _logger;
    private readonly SemaphoreSlim _unSeulRendu = new(1, 1);

    public ReplayVideoRenderer(IReplayManifestStore manifests, IReplayObjectStore objects, IReplayMetadataStore meta,
        IReplayRuntimeResolver resolver, ReplayPlaybackService playback, ILogger<ReplayVideoRenderer> logger,
        RetroBat.Api.Speedrun.CatalogueDesDefis? defis = null)
        : this(manifests, objects, meta, resolver, () => playback.GetState().State, logger, defis)
    {
    }

    /// <summary>Sans le lecteur de replays : <paramref name="etatDeLaLecture"/> dit « idle » quand aucune lecture ne tourne (essais).</summary>
    internal ReplayVideoRenderer(IReplayManifestStore manifests, IReplayObjectStore objects, IReplayMetadataStore meta,
        IReplayRuntimeResolver resolver, Func<string> etatDeLaLecture, ILogger<ReplayVideoRenderer> logger,
        RetroBat.Api.Speedrun.CatalogueDesDefis? defis = null)
    {
        _manifests = manifests; _objects = objects; _meta = meta; _resolver = resolver; _etatDeLaLecture = etatDeLaLecture;
        _logger = logger; _defis = defis;
    }

    /// <param name="Ok">Le film couvre toute la performance demandee.</param>
    /// <param name="Raison">Pourquoi il n'y a pas de film complet, sinon null.</param>
    /// <param name="Fichier">Le film, s'il en existe un (meme incomplet).</param>
    /// <param name="Fidelite">Le controle de fidelite (<see cref="ControleDeFidelite"/>) : fidele, desync ou non_verifie.</param>
    public sealed record RenduVideo(bool Ok, string? Raison, string? Fichier, long Debut, long Fin, long FrameAtteinte,
        double Secondes, string Fidelite = ControleDeFidelite.NonVerifie, string? DetailFidelite = null,
        IReadOnlyList<(long Frame, long Score)>? Releves = null, string? TraceDEpreuve = null);

    /// <summary>
    /// Rend la performance d'un replay. <paramref name="debut"/> et <paramref name="fin"/> sont en frames du
    /// replay (les bornes du score, CDC video V2 §4.4) ; sans elles, tout le replay.
    /// </summary>
    /// <param name="controles">Les scores certifies et la fin de leur run, que le score lu pendant le rendu doit retrouver
    /// (controle de fidelite, 09/11) ; sans eux, le film est « non verifie ».</param>
    /// <param name="epreuves">L'epreuve certifiee du replay (speedrun, challenge ; 11/11) : son arrivee doit tomber a la bonne
    /// image (<see cref="ControleDEpreuve"/>) ; sans elle, un film d'epreuve est « non verifie ».</param>
    public async Task<RenduVideo> RendreAsync(string replayId, long? debut, long? fin, CancellationToken ct,
        IReadOnlyList<PointDuScore>? controles = null, IReadOnlyList<EpreuveCertifiee>? epreuves = null)
    {
        if (!await _unSeulRendu.WaitAsync(0, ct).ConfigureAwait(false))
            return Echec("rendu_en_cours");
        try
        {
            return await RendreSeulAsync(replayId, debut, fin, controles, epreuves, ct).ConfigureAwait(false);
        }
        finally
        {
            _unSeulRendu.Release();
        }
    }

    private async Task<RenduVideo> RendreSeulAsync(string replayId, long? debutDemande, long? finDemandee,
        IReadOnlyList<PointDuScore>? controles, IReadOnlyList<EpreuveCertifiee>? epreuves, CancellationToken ct)
    {
        // Jamais pendant une partie ni une lecture : on lance notre propre RetroArch.
        if (_etatDeLaLecture() != "idle") return Echec("lecture_en_cours");
        if (Process.GetProcessesByName("retroarch").Length > 0) return Echec("emulateur_en_cours");

        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return Echec("replay_inconnu");
        if (!_objects.HasObject(manifest.Object.Sha256)) return Echec("objet_absent");
        if (!await _objects.VerifyObjectAsync(manifest.Object, ct).ConfigureAwait(false)) return Echec("objet_corrompu");
        var objet = _objects.ObjectPath(manifest.Object.Sha256);

        var resolution = _resolver.Resolve(manifest, _meta.GetMeta(replayId)?.Launch);
        if (resolution.Runtime is null) return Echec("runtime_" + resolution.Failure.ToString().ToLowerInvariant());
        // UN FILM SE REND SUR LA ROM ET LE COEUR EXACTS (09/11) : le 1-1 de SMB d'un autre joueur, rendu sur la version
        // Europe au lieu de la World, ne montrait pas sa partie. La lecture humaine garde son « au plus pres ».
        if (RefusDuRuntime(resolution.Runtime) is { } refus) return Echec(refus);

        var finDuReplay = manifest.Frames.ReplayEnd;
        var debut = Math.Clamp(debutDemande ?? 0, 0, Math.Max(0, finDuReplay));
        var fin = Math.Min(finDemandee ?? finDuReplay, finDuReplay - MargeAvantLaFin);
        if (fin <= debut) return Echec("bornes", debut, fin);

        var dossier = Path.Combine(_objects.TempRoot, "video", replayId);
        var sauvegardes = Path.Combine(dossier, "sauvegardes");
        var films = Path.Combine(dossier, "films");
        try { if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true); } catch { /* repris plus bas */ }
        Directory.CreateDirectory(sauvegardes);
        Directory.CreateDirectory(films);
        var session = Path.Combine(dossier, "rendu.cfg");
        // Les options des coeurs de cette machine, filtres d'image coupes : le film d'un record se fait sans eux.
        string? optionsDesCoeurs = null;
        try
        {
            var source = FiltreDImage.FichierDesOptions(RetroBatPaths.RetroArchConfigPath, ValeurDeRetroArch("core_options_path"));
            if (File.Exists(source))
            {
                optionsDesCoeurs = Path.Combine(dossier, "options-des-coeurs.cfg");
                await File.WriteAllLinesAsync(optionsDesCoeurs, FiltreDImage.SansFiltres(File.ReadLines(source)), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            optionsDesCoeurs = null;
        }
        await File.WriteAllTextAsync(session, ProfilDeRendu(sauvegardes, films, optionsDesCoeurs) + (ReglagesDEssai ?? ""), ct).ConfigureAwait(false);

        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        // Un replay de defi a verrous se rend par le wrapper, qui remet les memes verrous (D18).
        // Un replay enregistre touche par touche se rend par le wrapper, masque refuse (11/11) : sinon le film derive.
        var modeDesEntrees = RetroBat.Api.Replay.Playback.ModeDesEntrees.Lire(objet);
        var (coeurDuRendu, refusDesVerrous, romDuRendu) = RetroBat.Api.Replay.Playback.LectureAvecVerrous.PreparerLaLecture(manifest, resolution.Runtime.CoreDll,
            resolution.Runtime.RomPath, psi, mode: modeDesEntrees);
        _logger.LogInformation("Video : {ReplayId}, entrees enregistrees en mode {Mode}, coeur du rendu {Coeur}.", replayId, modeDesEntrees,
            Path.GetFileName(Path.GetDirectoryName(coeurDuRendu)) + "\\" + Path.GetFileName(coeurDuRendu));
        if (refusDesVerrous != RetroBat.Api.Replay.Models.ReplayErrorCode.None) return Echec("verrous_non_tenus");
        psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(coeurDuRendu);
        psi.ArgumentList.Add(romDuRendu);
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(objet);
        psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(RetroBatPaths.RetroArchConfigPath);
        psi.ArgumentList.Add("--appendconfig"); psi.ArgumentList.Add(session);

        var chrono = Stopwatch.StartNew();
        // Le temps accorde : la capture de l'image affichee tourne entre une et une fois et demie le temps reel
        // de la performance (mesure du 14 octobre) ; ce plafond ne sert qu'a ne jamais rester bloque.
        var fps = manifest.Frames.NominalFps > 1 ? manifest.Frames.NominalFps : 60;
        // LE PLAFOND SUIT LA BORNE (11/11) : le meme rendu va de 4 a 47 images/s selon la charge de la machine (Alex Kidd sous
        // Genesis Plus GX, FBNeo et fceumm de meme ; 576 Mo libres sur 7,8 Go). A 2,5 fois le temps reel, Garegga, Streets of
        // Rage 2 et Alex Kidd s'arretaient en route. Le plafond ne sert qu'a ne jamais rester bloque : c'est l'immobilite de la
        // lecture (PiloteDuRendu) qui dit qu'un RetroArch ne progresse plus.
        var plafond = PlafondDuRendu(fin - debut + debut / 2.0, fps);
        // L'arrivee d'une epreuve (11/11) : toutes les adresses du .MEM lues a chaque releve, du debut du replay, et les
        // lectures continuent 10 s apres le film (une arrivee se confirme parfois apres ses 3 s de marge).
        var epreuve = epreuves is { Count: > 0 } ? epreuves[0] : null;
        var (controleDEpreuve, raisonEpreuve) = epreuve is null ? (null, null)
            : ControleDEpreuve.Pour(epreuve, _defis, coeur: manifest.Runtime.CoreName);
        var finDesLectures = controleDEpreuve is null ? fin : Math.Min(finDuReplay - MargeAvantLaFin, fin + (long)Math.Round(10 * fps));
        var pilote = new PiloteDuRendu(debut, fin, finDesLectures: finDesLectures);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        // Le score lu dans la memoire du coeur pendant la portion filmee (controle de fidelite).
        using var lecteur = controles is { Count: > 0 } ? LecteurDuScore.Pour(manifest) : null;
        var releves = new List<(long Frame, long Score)>();

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video : RetroArch ne demarre pas pour {ReplayId}.", replayId);
            return Echec("retroarch_indisponible", debut, fin);
        }
        RetroBat.Api.Replay.Playback.LectureAvecVerrous.EffacerALaFin(manifest, process);
        _logger.LogInformation("Video : rendu de {ReplayId}, frames {Debut} a {Fin}.", replayId, debut, fin);
        // Ce RetroArch n'est pas une partie : les panneaux d'ES restent ouvrables pendant le rendu (22/10).
        RetroBat.Api.Infrastructure.EmulatorForeground.NoterLeRendu(process.Id);

        try
        {
            while (!process.HasExited && !pilote.Termine)
            {
                ct.ThrowIfCancellationRequested();
                if (RetroBat.Api.Infrastructure.EmulatorForeground.GarderHorsEcran(process))
                {
                    // Lance « reduit », il s'active quand meme : ES perdait le premier plan et la manette.
                    RetroBat.Api.Infrastructure.EmulatorForeground.RendreAEsSiLeRenduEstDevant(process);
                }

                PiloteDuRendu.Action action;
                if (chrono.Elapsed > plafond) action = pilote.Abandonner();
                else if (controleDEpreuve is not null)
                {
                    // Une epreuve : la frame et toute la memoire du .MEM dans un seul datagramme, sans pause entre deux releves
                    // (RetroArch y repond une fois par image : on lit a chaque image ou presque).
                    var (releve, blocs) = await ReleverAvecMemoireAsync(udp, controleDEpreuve, chrono.Elapsed.TotalSeconds, ct).ConfigureAwait(false);
                    action = pilote.Decider(releve);
                    if (releve.Drapeaux > 0 && blocs is { Count: > 0 }) controleDEpreuve.Noter(releve.Frame, blocs);
                }
                else
                {
                    var releve = await ReleverAsync(udp, chrono.Elapsed.TotalSeconds, ct).ConfigureAwait(false);
                    action = pilote.Decider(releve);
                    if (lecteur is not null && releve.Drapeaux > 0 && releve.Frame >= debut && releve.Frame <= fin
                        && await LireLeScoreAsync(udp, lecteur, ct).ConfigureAwait(false) is long score)
                        releves.Add((releve.Frame, score));
                }
                switch (action)
                {
                    case PiloteDuRendu.Action.AvanceRapide: await EnvoyerAsync(udp, "FAST_FORWARD", ct).ConfigureAwait(false); break;
                    case PiloteDuRendu.Action.DebutEnregistrement:
                    case PiloteDuRendu.Action.FinEnregistrement: await EnvoyerAsync(udp, "RECORDING_TOGGLE", ct).ConfigureAwait(false); break;
                    case PiloteDuRendu.Action.Quitter:
                        // Le fichier se ferme avec l'arret de l'enregistrement : on lui laisse le temps.
                        await Task.Delay(800, ct).ConfigureAwait(false);
                        await EnvoyerAsync(udp, "QUIT", ct).ConfigureAwait(false);
                        break;
                }
                if (controleDEpreuve is null || action != PiloteDuRendu.Action.Rien) await Task.Delay(200, ct).ConfigureAwait(false);
            }

            // RetroArch se ferme seul apres QUIT (et plante parfois en sortant, sans abimer le film).
            if (!process.WaitForExit(20_000))
            {
                _logger.LogWarning("Video : RetroArch ne se ferme pas apres le rendu de {ReplayId}, arret force.", replayId);
                try { process.Kill(entireProcessTree: true); } catch { /* deja parti */ }
            }
        }
        finally
        {
            RetroBat.Api.Infrastructure.EmulatorForeground.OublierLeRendu();
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch { /* deja parti */ } }
            try { Directory.Delete(sauvegardes, recursive: true); } catch { /* zone de travail */ }
        }

        var film = new DirectoryInfo(films).GetFiles("*.mkv").Where(f => f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        var ok = pilote.Complet && film is not null;
        var raison = pilote.Raison ?? (film is null ? "aucun_film" : null);
        var verdict = epreuve is not null
            ? controleDEpreuve is null
                ? new ControleDeFidelite.Verdict(ControleDeFidelite.NonVerifie, raisonEpreuve ?? "epreuve non controlable")
                : controleDEpreuve.Juger(epreuve, debut, fin)
            : controles is { Count: > 0 } && lecteur is null
                ? new ControleDeFidelite.Verdict(ControleDeFidelite.NonVerifie, "score non lisible sous ce coeur ou sans .MEM")
                : ControleDeFidelite.Juger(controles, releves, fps);
        _logger.LogInformation("Video : {ReplayId} {Issue} en {Secondes:F1} s (frame {Frame} sur {Fin}, {Cadence:F1} images/s) ; fidelite {Fidelite} ({Detail}, {Lectures} lectures).",
            replayId, ok ? "rendu" : "incomplet (" + raison + ")", chrono.Elapsed.TotalSeconds, pilote.FrameAtteinte, fin,
            chrono.Elapsed.TotalSeconds > 0 ? Math.Max(0, pilote.FrameAtteinte) / chrono.Elapsed.TotalSeconds : 0,
            verdict.Issue, verdict.Detail, releves.Count);
        return new RenduVideo(ok, ok ? null : raison, film, debut, fin, pilote.FrameAtteinte, chrono.Elapsed.TotalSeconds,
            verdict.Issue, verdict.Detail, releves, controleDEpreuve?.Trace);
    }

    /// <summary>
    /// Le profil de l'instance de rendu, applique par-dessus la configuration de RetroArch sans la modifier
    /// (<c>config_save_on_exit</c> faux). Repris du lecteur de replays (sorties de menu neutralisees, sauvegardes
    /// jetables, presentation neutre), plus ce que le lot 0 a mesure pour filmer.
    /// </summary>
    internal static string ProfilDeRendu(string sauvegardes, string films, string? optionsDesCoeurs = null)
    {
        string Ligne(string cle, string valeur) => cle + " = \"" + valeur.Replace("\"", "") + "\"";
        // Sans filtre d'image (25/10) : les options des coeurs de la machine, filtres coupes.
        var options = optionsDesCoeurs is null
            ? Array.Empty<string>()
            : new[] { Ligne("global_core_options", "true"), Ligne("core_options_path", optionsDesCoeurs) };
        return string.Join('\n', options.Concat(new[]
        {
            Ligne("config_save_on_exit", "false"),
            Ligne("savefile_directory", sauvegardes),
            Ligne("savestate_directory", sauvegardes),
            Ligne("sort_savefiles_enable", "false"),
            Ligne("sort_savefiles_by_content_enable", "false"),
            Ligne("savefiles_in_content_dir", "false"),
            Ligne("sort_savestates_enable", "false"),
            Ligne("sort_savestates_by_content_enable", "false"),
            Ligne("savestates_in_content_dir", "false"),
            Ligne("history_list_enable", "false"),
            Ligne("input_menu_toggle_btn", "nul"),
            Ligne("input_exit_emulator_btn", "nul"),
            Ligne("input_menu_toggle_gamepad_combo", "0"),
            Ligne("input_quit_gamepad_combo", "0"),
            Ligne("quit_press_twice", "false"),
            Ligne("cheevos_enable", "false"),
            Ligne("rewind_enable", "false"),
            // SANS RECALAGE (09/11) : avec replay_checkpoint_deserialize, RetroArch recharge l'etat de chaque point de controle
            // (toutes les 5 s) ; un replay qui derive se recale alors en sautant, et le score de fin reste juste alors que la
            // partie filmee est fausse. Sans recalage, une derive se voit au score, et le film ne part pas.
            Ligne("replay_checkpoint_deserialize", "false"),
            Ligne("input_overlay_enable", "false"),
            Ligne("video_shader_enable", "false"),
            Ligne("aspect_ratio_index", "22"),
            Ligne("video_aspect_ratio_auto", "true"),
            Ligne("video_rotation", "0"),
            Ligne("screen_orientation", "0"),
            Ligne("video_allow_rotate", "true"),
            Ligne("network_cmd_enable", "true"),
            Ligne("network_cmd_port", PortDeCommande.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            // Le film : l'image AFFICHEE, deja redressee par RetroArch (decision du 14 octobre). L'image du
            // coeur, elle, est couchee pour un jeu vertical (Ms. Pac-Man, 19XX) et ses proportions etaient
            // fausses dans le fichier. La lecture de l'image affichee n'existe qu'avec OpenGL (D3D11 refuse :
            // « Failed to get viewport information ») ; agrandissement entier sans lissage, fenetre de 4 fois
            // la taille du jeu, pixels carres. Mesure : 1280x960 pour Double Dragon, 648x864 pour Ms. Pac-Man.
            Ligne("video_driver", "gl"),
            Ligne("video_gpu_record", "true"),
            Ligne("video_post_filter_record", "false"),
            Ligne("video_record_scale_factor", "1"),
            // La qualite moyenne de RetroArch, comme avant : retroarch.cfg designe depuis le 15 octobre 2026 les
            // reglages du film a la taille d'origine (4:4:4), faits pour la capture pendant la partie.
            Ligne("video_record_quality", "2"),
            Ligne("video_record_config", ""),
            Ligne("video_scale", "4"),
            Ligne("video_scale_integer", "true"),
            Ligne("video_smooth", "false"),
            Ligne("recording_output_directory", films),
            // Discretion : la fenetre demarre reduite puis vit hors de tous les ecrans (EmulatorForeground.
            // GarderHorsEcran) ; reduite, OpenGL ne dessine plus rien (film noir, 14 octobre). Son coupe en
            // sortie, garde dans le film.
            Ligne("video_fullscreen", "false"),
            Ligne("video_windowed_fullscreen", "false"),
            Ligne("video_window_save_positions", "false"),
            Ligne("audio_mute_enable", "true"),
            Ligne("pause_nonactive", "false"),
            Ligne("menu_pause_libretro", "false"),
            Ligne("video_font_enable", "false"),
            // L'avance rapide sans limite.
            Ligne("fastforward_ratio", "0.0"),
            Ligne("fastforward_frameskip", "false"),
        })) + "\n";
    }

    /// <summary>
    /// Le temps accorde a un rendu de <paramref name="images"/> images : 15 fois leur duree a la cadence du jeu, soit 4 images/s
    /// tenues pour un jeu a 60 (la borne la plus chargee mesuree le 11/11), plus 5 minutes pour lancer et fermer RetroArch. Pur.
    /// </summary>
    internal static TimeSpan PlafondDuRendu(double images, double fps)
        => TimeSpan.FromSeconds(Math.Max(0, images) / (fps > 1 ? fps : 60) * FacteurDuPlafond + 300);

    /// <summary>Le plafond en multiple de la duree du film : 60 / 4, pour tenir 4 images/s d'un jeu a 60.</summary>
    internal const double FacteurDuPlafond = 15;

    /// <summary>Pour les essais de mesure seulement : des lignes ajoutees au profil de rendu (null en service).</summary>
    internal static string? ReglagesDEssai { get; set; }

    /// <summary>
    /// Pourquoi ce runtime ne sert pas a un film : « rom_inexacte » ou « coeur_inexact », sinon null (09/11). Pur.
    /// </summary>
    internal static string? RefusDuRuntime(ResolvedRuntime runtime)
        => !runtime.ExactRom ? "rom_inexacte" : !runtime.ExactCore ? "coeur_inexact" : null;

    /// <summary>Le score compose a partir des lignes du .MEM, lues par READ_CORE_RAM ; null s'il ne se compose pas.</summary>
    private static async Task<long?> LireLeScoreAsync(UdpClient udp, LecteurDuScore lecteur, CancellationToken ct)
    {
        var valeurs = new List<long?>(lecteur.Lignes.Count);
        foreach (var l in lecteur.Lignes)
        {
            // Les index de la ligne (mots inverses de FBNeo compris), lus d'un bloc puis remis dans l'ordre de l'adresse.
            var index = l.Index;
            var bas = index.Min();
            var n = (int)(index.Max() - bas + 1);
            var commande = "READ_CORE_RAM " + bas.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + " " + n;
            var bloc = LecteurDuScore.OctetsDeLaReponse(await DemanderAsync(udp, commande, ct, "READ_CORE_RAM").ConfigureAwait(false), n);
            valeurs.Add(bloc is null ? null : LecteurDuScore.Valeur(l, index.Select(i => bloc[(int)(i - bas)]).ToList()));
        }
        return valeurs.Any(v => v is not null) ? lecteur.Composer(valeurs) : null;
    }

    /// <summary>
    /// Un releve d'epreuve : la frame (active_replay) et les octets de chaque bloc, d'un seul datagramme. Les reponses arrivent
    /// une par commande ; une reponse en retard d'un releve precedent est jetee avant la demande.
    /// </summary>
    private static async Task<(PiloteDuRendu.Releve Releve, IReadOnlyDictionary<long, IReadOnlyList<int>>? Blocs)> ReleverAvecMemoireAsync(
        UdpClient udp, ControleDEpreuve controle, double secondes, CancellationToken ct)
    {
        var sansReponse = new PiloteDuRendu.Releve(secondes, -1, 0);
        PiloteDuRendu.Releve? releve = null;
        var blocs = new Dictionary<long, IReadOnlyList<int>>();
        try
        {
            while (udp.Available > 0) await udp.ReceiveAsync(ct).ConfigureAwait(false);
            await EnvoyerAsync(udp, controle.Datagramme, ct).ConfigureAwait(false);
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(TimeSpan.FromMilliseconds(300));
            var attendus = controle.Blocs.ToDictionary(b => b.Adresse, b => b.Octets);
            // Une reponse par commande, lisible ou non (« -1 » : adresse hors de la memoire exposee) : on s'arrete quand toutes
            // sont la, sans attendre le delai pour une adresse illisible.
            var reponsesMemoire = 0;
            for (var i = 0; i < controle.Blocs.Count + 4 && (releve is null || reponsesMemoire < attendus.Count); i++)
            {
                var texte = Encoding.ASCII.GetString((await udp.ReceiveAsync(delai.Token).ConfigureAwait(false)).Buffer).Trim();
                var champs = texte.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (texte.StartsWith("GET_CONFIG_PARAM", StringComparison.Ordinal))
                {
                    releve = champs.Length >= 5 && int.TryParse(champs[^2], out var d) && long.TryParse(champs[^1], out var f)
                        ? new PiloteDuRendu.Releve(secondes, d, f) : sansReponse;
                }
                else if (champs.Length >= 2 && champs[0] == "READ_CORE_RAM"
                    && long.TryParse(champs[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var adresse)
                    && attendus.TryGetValue(adresse, out var n))
                {
                    reponsesMemoire++;
                    if (LecteurDuScore.OctetsDeLaReponse(texte, n) is { } octets) blocs[adresse] = octets;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Le delai : on garde ce qui est arrive (la frame surtout, sans laquelle le pilote croirait la lecture arretee).
        }
        catch (SocketException)
        {
            return (sansReponse, null);
        }
        return (releve ?? sansReponse, blocs);
    }

    /// <summary>Un reglage de retroarch.cfg, ou null.</summary>
    private static string? ValeurDeRetroArch(string cle)
    {
        foreach (var ligne in File.ReadLines(RetroBatPaths.RetroArchConfigPath))
        {
            var egal = ligne.IndexOf('=');
            if (egal > 0 && ligne[..egal].Trim() == cle) return ligne[(egal + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static async Task<PiloteDuRendu.Releve> ReleverAsync(UdpClient udp, double secondes, CancellationToken ct)
    {
        var reponse = await DemanderAsync(udp, "GET_CONFIG_PARAM active_replay", ct, "GET_CONFIG_PARAM").ConfigureAwait(false);
        // « GET_CONFIG_PARAM active_replay <id> <drapeaux> <frame> »
        var champs = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (champs is { Length: >= 5 }
            && int.TryParse(champs[^2], out var drapeaux)
            && long.TryParse(champs[^1], out var frame))
        {
            return new PiloteDuRendu.Releve(secondes, drapeaux, frame);
        }
        return new PiloteDuRendu.Releve(secondes, -1, 0);
    }

    /// <summary>
    /// Une commande et sa reponse. <paramref name="prefixe"/> : une reponse en retard d'une commande precedente (vu a la
    /// sonde du 09/11, la reponse d'active_replay arrivait a la lecture suivante) est ecartee.
    /// </summary>
    private static async Task<string?> DemanderAsync(UdpClient udp, string commande, CancellationToken ct, string? prefixe = null)
    {
        try
        {
            await EnvoyerAsync(udp, commande, ct).ConfigureAwait(false);
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(TimeSpan.FromMilliseconds(300));
            for (var i = 0; i < 8; i++)
            {
                var recu = await udp.ReceiveAsync(delai.Token).ConfigureAwait(false);
                var texte = Encoding.ASCII.GetString(recu.Buffer).Trim();
                if (prefixe is null || texte.StartsWith(prefixe, StringComparison.Ordinal)) return texte;
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static Task EnvoyerAsync(UdpClient udp, string commande, CancellationToken ct)
    {
        var octets = Encoding.ASCII.GetBytes(commande);
        return udp.SendAsync(octets, new IPEndPoint(IPAddress.Loopback, PortDeCommande), ct).AsTask();
    }

    private static RenduVideo Echec(string raison, long debut = 0, long fin = 0)
        => new(false, raison, null, debut, fin, -1, 0);
}
