using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RetroBat.Domain.Paths;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE SPECTATEUR CACHE DE L'HOTE (demande du user, 28/10) : sur une partie de consoles reliees (GBA, Game Boy Color, Neo Geo
/// Pocket), chaque borne n'affiche que sa console (« focus ») ; l'hote ne voyait que la sienne, son direct aussi. Apres le
/// depart, l'hote lance un second RetroArch, cache, qui rejoint sa propre partie en SPECTATEUR par le relais, avec la vue de
/// toutes les consoles (bloc <c>spectator</c> du coeur au catalogue). C'est lui que la capture filme pour le direct ; la
/// partie des joueurs ne change pas.
///
/// Mesures au banc (28/10, i3-N305) : 1,4 coeur a deux consoles, 1,8 a quatre, autant que l'hote ; d'ou la priorite basse :
/// le jeu de l'hote passe toujours devant. Au relais, il ajoute DEUX connexions (la sienne, et le tunnel que l'hote ouvre pour
/// lui) : <see cref="Corriger"/> les retire du compte des invites. Le mot de passe des spectateurs se tape dans sa fenetre
/// (RetroArch le demande au client, comme a l'invite). L'exe est une copie de retroarch.exe sous un autre nom : les services
/// qui cherchent « retroarch » (premier plan, partie en cours) ne le confondent jamais avec le jeu.
///
/// Retour du premier direct a deux bornes (28/10, hote Falken) : (1) son journal suit la langue de la borne (« Connecte a »
/// en francais) : l'API ne le voyait jamais rejoindre, l'arretait au bout de 60 s et filmait l'hote ; il journalise donc en
/// anglais. (2) Windows demandait l'autorisation du pare-feu a chaque direct : son port de commandes (UDP sur toutes les
/// interfaces) etait la seule ecoute d'un exe neuf. Il n'en a plus : il filme des son lancement (--record) et l'API ferme sa
/// fenetre a la fin, RetroArch termine alors le film (le RetroArch de Windows n'a pas l'interface de commandes par l'entree
/// standard, vu au banc). Seule reste sa connexion sortante au relais.
/// </summary>
public static class MultiplayerSpectateur
{
    public const string Exe = "nelfeplay-spectateur.exe";

    private static readonly object Verrou = new();
    private static Process? _processus;
    private static volatile bool _prevu;

    /// <summary>Le dossier de ses films.</summary>
    public static string DossierDesFilms => Path.Combine(Dossier, "films");

    /// <summary>
    /// Son film, le plus recent de son dossier ; null s'il ne tourne pas. RetroArch le recommence (nom date) quand le coeur
    /// change sa cadence ou sa geometrie : Mario Kart change la sienne au demarrage, juste apres le premier (banc du 28/10).
    /// </summary>
    public static string? Film
    {
        get
        {
            lock (Verrou)
            {
                if (_processus is not { HasExited: false }) return null;
            }
            try
            {
                return Directory.GetFiles(DossierDesFilms, "*.mkv").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private static string Dossier => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "spectateur");

    /// <summary>Une partie de l'hote attend son spectateur : la capture l'attend avant de filmer.</summary>
    public static bool Prevu => _prevu;

    public static void Prevoir() => _prevu = true;

    /// <summary>Le spectateur tourne et a rejoint la partie (vu une fois dans son journal, au lancement).</summary>
    public static bool Connecte
    {
        get
        {
            lock (Verrou)
            {
                return _connecte && _processus is { HasExited: false };
            }
        }
    }

    private static volatile bool _connecte;

    /// <summary>Son journal dit-il qu'il a rejoint la partie ?</summary>
    private static bool AJoint(string journal)
    {
        try
        {
            using var flux = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lecteur = new StreamReader(flux, Encoding.UTF8);
            return lecteur.ReadToEnd().Contains("[Netplay] Connected to", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Les liens des invites au relais, sans les deux du spectateur quand il y est.</summary>
    public static Func<int?> Corriger(Func<int?> liens)
        => () => liens() is int n ? Math.Max(0, n - (Connecte ? 2 : 0)) : null;

    /// <summary>
    /// La grille de son image : <c>auto</c> (le coeur GBA : cote a cote a deux, 2 x 2 a trois ou quatre) ou « colonnes x
    /// lignes ». Pur.
    /// </summary>
    public static (int Colonnes, int Lignes) Grille(string? grille, int joueurs)
    {
        if (string.Equals(grille?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) return joueurs <= 2 ? (2, 1) : (2, 2);
        var morceaux = (grille ?? "").Trim().ToLowerInvariant().Split('x');
        return morceaux.Length == 2 && int.TryParse(morceaux[0], out var c) && int.TryParse(morceaux[1], out var l)
               && c is >= 1 and <= 4 && l is >= 1 and <= 4 ? (c, l) : (Math.Max(1, joueurs), 1);
    }

    /// <summary>
    /// Ses options de coeur : celles de la partie (communes et de la place 1 : le netplay exige les memes), puis celles de sa
    /// vue (toutes les consoles). <c>{players}</c> devient le nombre de joueurs. Pur.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OptionsDuSpectateur(MultiplayerCore coeur, MultiplayerSpectatorView vue, int joueurs)
    {
        var options = new Dictionary<string, string>(MultiplayerLaunchPlan.OptionsDuCoeur(coeur, joueurs, 1), StringComparer.Ordinal);
        foreach (var (cle, valeur) in vue.Options ?? new Dictionary<string, string>())
        {
            options[cle] = valeur.Replace("{players}", joueurs.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        return options;
    }

    /// <summary>
    /// Ses reglages, ajoutes a retroarch.cfg : spectateur, cache, muet (le film garde le son), ni menu ni sortie a la manette,
    /// ni decor ni shader de la borne, ses propres sauvegardes, ses options de coeur, ses films ; journal en anglais (lu par
    /// <see cref="AJoint"/>), aucun port de commandes (aucune demande du pare-feu). Pur.
    /// </summary>
    internal static IReadOnlyList<string> Reglages(string optionsDuCoeur, string sauvegardes, string films, string motDePasse)
    {
        static string Ligne(string cle, string valeur) => cle + " = \"" + valeur.Replace("\"", "", StringComparison.Ordinal) + "\"";
        return
        [
            Ligne("netplay_start_as_spectator", "true"),
            Ligne("netplay_password", motDePasse),
            Ligne("netplay_spectate_password", motDePasse),
            Ligne("netplay_nickname", "NelfePlayTV"),
            Ligne("netplay_public_announce", "false"),
            Ligne("netplay_use_mitm_server", "false"),
            Ligne("user_language", "0"),
            Ligne("network_cmd_enable", "false"),
            Ligne("network_remote_enable", "false"),
            Ligne("core_options_path", optionsDuCoeur),
            Ligne("global_core_options", "true"),
            Ligne("game_specific_options", "false"),
            Ligne("savefile_directory", sauvegardes),
            Ligne("savestate_directory", sauvegardes),
            Ligne("savefiles_in_content_dir", "false"),
            Ligne("savestates_in_content_dir", "false"),
            Ligne("savestate_auto_load", "false"),
            Ligne("savestate_auto_save", "false"),
            Ligne("recording_output_directory", films),
            Ligne("video_gpu_record", "false"),
            Ligne("config_save_on_exit", "false"),
            Ligne("history_list_enable", "false"),
            Ligne("video_fullscreen", "false"),
            Ligne("video_windowed_fullscreen", "false"),
            Ligne("video_window_save_positions", "false"),
            Ligne("video_shader_enable", "false"),
            Ligne("input_overlay_enable", "false"),
            Ligne("video_font_enable", "false"),
            Ligne("audio_mute_enable", "true"),
            Ligne("pause_nonactive", "false"),
            Ligne("menu_pause_libretro", "false"),
            Ligne("input_menu_toggle_btn", "nul"),
            Ligne("input_exit_emulator_btn", "nul"),
            Ligne("input_menu_toggle_gamepad_combo", "0"),
            Ligne("input_quit_gamepad_combo", "0"),
            Ligne("quit_press_twice", "false"),
            Ligne("cheevos_enable", "false"),
            Ligne("rewind_enable", "false"),
            Ligne("run_ahead_enabled", "false"),
        ];
    }

    /// <summary>
    /// Lance le spectateur sur la session publiee de l'hote. <paramref name="optionsImposees"/> : les options de la partie (les
    /// memes que l'hote, le netplay l'exige), puis celles du spectateur (la vue de toutes les consoles). Rend vrai s'il a
    /// rejoint la partie.
    /// </summary>
    public static async Task<bool> LancerAsync(MultiplayerSessionNetplay session, string coeur, string rom,
        IReadOnlyDictionary<string, string> optionsImposees, ILogger journal, CancellationToken ct)
    {
        Arreter(garderLaPrevision: true);
        // Un spectateur reste d'une API arretee en pleine partie ne survit pas au suivant.
        foreach (var orphelin in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)))
        {
            try { orphelin.Kill(entireProcessTree: true); } catch (Exception) { /* deja parti */ }
            orphelin.Dispose();
        }
        try
        {
            var retroarch = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");
            var source = Path.Combine(retroarch, "retroarch.exe");
            var copie = Path.Combine(retroarch, Exe);
            var dll = Path.Combine(retroarch, "cores", coeur + "_libretro.dll");
            if (!File.Exists(source) || !File.Exists(dll) || !File.Exists(rom))
            {
                journal.LogInformation("Spectateur : RetroArch, le coeur {Coeur} ou le jeu manque, pas de spectateur.", coeur);
                return false;
            }
            var infoSource = new FileInfo(source);
            var infoCopie = new FileInfo(copie);
            if (!infoCopie.Exists || infoCopie.Length != infoSource.Length || infoCopie.LastWriteTimeUtc < infoSource.LastWriteTimeUtc)
            {
                File.Copy(source, copie, overwrite: true);
            }

            try { if (Directory.Exists(Dossier)) Directory.Delete(Dossier, recursive: true); } catch (IOException) { /* repris plus bas */ }
            var sauvegardes = Path.Combine(Dossier, "sauvegardes");
            Directory.CreateDirectory(sauvegardes);
            Directory.CreateDirectory(DossierDesFilms);
            // Ses options : celles de la borne a cet instant (la partie y a pose les siennes), puis la vue de toutes les consoles.
            var options = Path.Combine(Dossier, "options-des-coeurs.cfg");
            var global = Path.Combine(retroarch, "retroarch-core-options.cfg");
            if (File.Exists(global)) File.Copy(global, options, overwrite: true);
            else await File.WriteAllTextAsync(options, "", ct).ConfigureAwait(false);
            RetroArchConfigEdit.Poser(options, optionsImposees);
            var reglages = Path.Combine(Dossier, "spectateur.cfg");
            await File.WriteAllLinesAsync(reglages, Reglages(options, sauvegardes, DossierDesFilms, session.SpectatePassword), ct)
                .ConfigureAwait(false);
            var journalDuSpectateur = Path.Combine(Dossier, "spectateur.log");
            var film = Path.Combine(DossierDesFilms,
                Path.GetFileNameWithoutExtension(rom) + "-" + DateTime.Now.ToString("yyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".mkv");

            var psi = new ProcessStartInfo
            {
                FileName = copie,
                WorkingDirectory = retroarch,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Minimized,
            };
            foreach (var a in Arguments(dll, rom, RetroBatPaths.RetroArchConfigPath, reglages, journalDuSpectateur, film, session))
            {
                psi.ArgumentList.Add(a);
            }
            var processus = Process.Start(psi);
            if (processus is null) return false;
            try { processus.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* il tourne quand meme */ }
            lock (Verrou)
            {
                _processus = processus;
            }
            journal.LogInformation("Spectateur : lance sur la session {Session} ({Coeur}).", session.Session, coeur);

            // Cache, sans jamais prendre la main au jeu ; le mot de passe tape dans sa fenetre jusqu'a ce qu'il soit entre.
            var limite = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < limite && !processus.HasExited && !ct.IsCancellationRequested)
            {
                Cacher(processus);
                if (AJoint(journalDuSpectateur))
                {
                    _connecte = true;
                    journal.LogInformation("Spectateur : il a rejoint la partie.");
                    _ = Task.Run(() => GarderCacheAsync(processus), CancellationToken.None);
                    return true;
                }
                Taper(processus, session.SpectatePassword);
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            journal.LogInformation("Spectateur : il n'a pas rejoint la partie, il s'arrete.");
            Arreter();
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            journal.LogInformation("Spectateur : lancement impossible ({Message}).", ex.Message);
            Arreter();
            return false;
        }
    }

    /// <summary>
    /// Ses arguments : le coeur, le jeu, la config de RetroBat et la sienne, son film (commence au chargement du jeu), la
    /// session du relais. Pur.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string dll, string rom, string config, string reglages, string journal,
        string film, MultiplayerSessionNetplay session)
    {
        var arguments = new List<string>
        {
            "-L", dll, rom, "--config", config, "--appendconfig", reglages, "--verbose", "--log-file", journal,
            "--record=" + film,
            "--connect=" + session.Relay, "--port=" + (session.Port > 0 ? session.Port : 55435).ToString(CultureInfo.InvariantCulture),
            "--nick=NelfePlayTV",
        };
        if (!string.IsNullOrWhiteSpace(session.Session)) arguments.Add("--mitm-session=" + session.Session);
        return arguments;
    }

    /// <summary>
    /// Ferme sa fenetre, comme la croix : RetroArch quitte proprement et termine son film. Attend sa sortie (10 s au plus).
    /// Rend vrai s'il est sorti.
    /// </summary>
    public static async Task<bool> FermerAsync(CancellationToken ct)
    {
        Process? p;
        lock (Verrou)
        {
            p = _processus;
        }
        if (p is null) return true;
        try
        {
            if (p.HasExited) return true;
            p.Refresh();
            if (p.MainWindowHandle == IntPtr.Zero || !PostMessage(p.MainWindowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero)) return false;
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(delai.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Arrete le spectateur (fin de la partie de l'hote), son film d'abord ferme par la capture.</summary>
    public static void Arreter() => Arreter(garderLaPrevision: false);

    private static void Arreter(bool garderLaPrevision)
    {
        Process? p;
        lock (Verrou)
        {
            p = _processus;
            _processus = null;
            if (!garderLaPrevision) _prevu = false;
            _connecte = false;
        }
        if (p is null) return;
        try
        {
            // Sa fenetre fermee d'abord : son film se termine proprement s'il tourne encore.
            if (!p.HasExited)
            {
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero && PostMessage(p.MainWindowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero))
                    p.WaitForExit(5000);
            }
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Deja parti.
        }
        finally
        {
            p.Dispose();
        }
    }

    private static async Task GarderCacheAsync(Process processus)
    {
        try
        {
            while (!processus.HasExited)
            {
                Cacher(processus);
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Arrete entre-temps.
        }
    }

    /// <summary>Hors de tous les ecrans ; s'il a pris le premier plan en demarrant, le jeu le reprend.</summary>
    private static void Cacher(Process processus)
    {
        if (!RetroBat.Api.Infrastructure.EmulatorForeground.GarderHorsEcran(processus)) return;
        try
        {
            if (processus.MainWindowHandle != IntPtr.Zero && GetForegroundWindow() == processus.MainWindowHandle)
            {
                RetroBat.Api.Infrastructure.EmulatorForeground.Focus("retroarch");
            }
        }
        catch (Exception)
        {
            // Sa fenetre s'est fermee.
        }
    }

    /// <summary>Le mot de passe des spectateurs, tape dans SA fenetre (WM_CHAR), puis Entree.</summary>
    private static void Taper(Process processus, string motDePasse)
    {
        try
        {
            processus.Refresh();
            var fenetre = processus.MainWindowHandle;
            if (fenetre == IntPtr.Zero) return;
            foreach (var c in motDePasse + "\r")
            {
                PostMessage(fenetre, 0x0102, (IntPtr)c, (IntPtr)1);
            }
        }
        catch (Exception)
        {
            // Pas encore de fenetre.
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr fenetre, uint message, IntPtr wParam, IntPtr lParam);
}
