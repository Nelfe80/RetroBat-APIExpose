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
    private readonly ReplayPlaybackService _playback;
    private readonly ILogger<ReplayVideoRenderer> _logger;
    private readonly SemaphoreSlim _unSeulRendu = new(1, 1);

    public ReplayVideoRenderer(IReplayManifestStore manifests, IReplayObjectStore objects, IReplayMetadataStore meta,
        IReplayRuntimeResolver resolver, ReplayPlaybackService playback, ILogger<ReplayVideoRenderer> logger)
    {
        _manifests = manifests; _objects = objects; _meta = meta; _resolver = resolver; _playback = playback;
        _logger = logger;
    }

    /// <param name="Ok">Le film couvre toute la performance demandee.</param>
    /// <param name="Raison">Pourquoi il n'y a pas de film complet, sinon null.</param>
    /// <param name="Fichier">Le film, s'il en existe un (meme incomplet).</param>
    public sealed record RenduVideo(bool Ok, string? Raison, string? Fichier, long Debut, long Fin, long FrameAtteinte,
        double Secondes);

    /// <summary>
    /// Rend la performance d'un replay. <paramref name="debut"/> et <paramref name="fin"/> sont en frames du
    /// replay (les bornes du score, CDC video V2 §4.4) ; sans elles, tout le replay.
    /// </summary>
    public async Task<RenduVideo> RendreAsync(string replayId, long? debut, long? fin, CancellationToken ct)
    {
        if (!await _unSeulRendu.WaitAsync(0, ct).ConfigureAwait(false))
            return Echec("rendu_en_cours");
        try
        {
            return await RendreSeulAsync(replayId, debut, fin, ct).ConfigureAwait(false);
        }
        finally
        {
            _unSeulRendu.Release();
        }
    }

    private async Task<RenduVideo> RendreSeulAsync(string replayId, long? debutDemande, long? finDemandee, CancellationToken ct)
    {
        // Jamais pendant une partie ni une lecture : on lance notre propre RetroArch.
        if (_playback.GetState().State != "idle") return Echec("lecture_en_cours");
        if (Process.GetProcessesByName("retroarch").Length > 0) return Echec("emulateur_en_cours");

        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return Echec("replay_inconnu");
        if (!_objects.HasObject(manifest.Object.Sha256)) return Echec("objet_absent");
        if (!await _objects.VerifyObjectAsync(manifest.Object, ct).ConfigureAwait(false)) return Echec("objet_corrompu");
        var objet = _objects.ObjectPath(manifest.Object.Sha256);

        var resolution = _resolver.Resolve(manifest, _meta.GetMeta(replayId)?.Launch);
        if (resolution.Runtime is null) return Echec("runtime_" + resolution.Failure.ToString().ToLowerInvariant());

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
        await File.WriteAllTextAsync(session, ProfilDeRendu(sauvegardes, films), ct).ConfigureAwait(false);

        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(resolution.Runtime.CoreDll);
        psi.ArgumentList.Add(resolution.Runtime.RomPath);
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(objet);
        psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(RetroBatPaths.RetroArchConfigPath);
        psi.ArgumentList.Add("--appendconfig"); psi.ArgumentList.Add(session);

        var chrono = Stopwatch.StartNew();
        // Le temps accorde : la capture de l'image affichee tourne entre une et une fois et demie le temps reel
        // de la performance (mesure du 14 octobre) ; ce plafond ne sert qu'a ne jamais rester bloque.
        var fps = manifest.Frames.NominalFps > 1 ? manifest.Frames.NominalFps : 60;
        var plafond = TimeSpan.FromSeconds((fin - debut + debut / 2.0) / fps * 2.5 + 120);
        var pilote = new PiloteDuRendu(debut, fin);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video : RetroArch ne demarre pas pour {ReplayId}.", replayId);
            return Echec("retroarch_indisponible", debut, fin);
        }
        _logger.LogInformation("Video : rendu de {ReplayId}, frames {Debut} a {Fin}.", replayId, debut, fin);

        try
        {
            while (!process.HasExited && !pilote.Termine)
            {
                ct.ThrowIfCancellationRequested();
                RetroBat.Api.Infrastructure.EmulatorForeground.GarderEnRetrait(process);

                var action = chrono.Elapsed > plafond
                    ? pilote.Abandonner()
                    : pilote.Decider(await ReleverAsync(udp, chrono.Elapsed.TotalSeconds, ct).ConfigureAwait(false));
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
                await Task.Delay(200, ct).ConfigureAwait(false);
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
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch { /* deja parti */ } }
            try { Directory.Delete(sauvegardes, recursive: true); } catch { /* zone de travail */ }
        }

        var film = new DirectoryInfo(films).GetFiles("*.mkv").Where(f => f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        var ok = pilote.Complet && film is not null;
        var raison = pilote.Raison ?? (film is null ? "aucun_film" : null);
        _logger.LogInformation("Video : {ReplayId} {Issue} en {Secondes:F1} s (frame {Frame} sur {Fin}).",
            replayId, ok ? "rendu" : "incomplet (" + raison + ")", chrono.Elapsed.TotalSeconds, pilote.FrameAtteinte, fin);
        return new RenduVideo(ok, ok ? null : raison, film, debut, fin, pilote.FrameAtteinte, chrono.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Le profil de l'instance de rendu, applique par-dessus la configuration de RetroArch sans la modifier
    /// (<c>config_save_on_exit</c> faux). Repris du lecteur de replays (sorties de menu neutralisees, sauvegardes
    /// jetables, presentation neutre), plus ce que le lot 0 a mesure pour filmer.
    /// </summary>
    internal static string ProfilDeRendu(string sauvegardes, string films)
    {
        string Ligne(string cle, string valeur) => cle + " = \"" + valeur.Replace("\"", "") + "\"";
        return string.Join('\n', new[]
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
            Ligne("video_scale", "4"),
            Ligne("video_scale_integer", "true"),
            Ligne("video_smooth", "false"),
            Ligne("recording_output_directory", films),
            // Discretion : fenetre reduite (l'enregistrement continue), son coupe en sortie (le film le garde).
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
        }) + "\n";
    }

    private static async Task<PiloteDuRendu.Releve> ReleverAsync(UdpClient udp, double secondes, CancellationToken ct)
    {
        var reponse = await DemanderAsync(udp, "GET_CONFIG_PARAM active_replay", ct).ConfigureAwait(false);
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

    private static async Task<string?> DemanderAsync(UdpClient udp, string commande, CancellationToken ct)
    {
        try
        {
            await EnvoyerAsync(udp, commande, ct).ConfigureAwait(false);
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(TimeSpan.FromMilliseconds(300));
            var recu = await udp.ReceiveAsync(delai.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(recu.Buffer).Trim();
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
