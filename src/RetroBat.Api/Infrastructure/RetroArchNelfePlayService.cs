using System.Diagnostics;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>Les chemins et les empreintes de RetroArch NelfePlay, relatifs au dossier d'APIExpose ou de RetroArch.</summary>
public static class RetroArchNelfePlay
{
    /// <summary>L'exe du lot 1, livre par outils.json (SEUL fichier : les DLL sont celles de RetroBat).</summary>
    public const string CheminDeLExe = "tools/retroarch-nelfeplay/retroarch-1.22.2-nelfeplay.1.exe";

    /// <summary>L'empreinte de cet exe, celle qui a ete essayee au banc (aucune signature : l'empreinte epinglee fait foi).</summary>
    public const string ExeNelfePlay = "aff5d5747bbaa0e533036d025b757667ee1866417b8cf8e46e803f37ca72d0cf";

    /// <summary>Les exes NelfePlay reconnus : seuls leur marque ET leur empreinte levent les portes du replay.</summary>
    public static readonly string[] ExesConnus = [ExeNelfePlay];

    /// <summary>La seule base sur laquelle on pose : le retroarch.exe 1.22.2 du buildbot (RetroBat). Jamais de retrogradation.</summary>
    public const string Stable = "81c11b6f24932bf7918f05eee8928035bff3887335fd2a081507c75e9d94d06a";

    /// <summary>Les sources correspondantes (GPLv3), a fournir avec l'exe : fork et tag.</summary>
    public const string Sources = "https://github.com/Nelfe80/RetroArch/tree/v1.22.2-nelfeplay.1";

    public const string Installeur = "install-retroarch-nelfeplay.bat";

    public const string Desinstalleur = "uninstall-retroarch-nelfeplay.bat";

    /// <summary>Retire la nocturne SDL3 de la 1.9.60 (son ancienne marque), la ou elle serait restee.</summary>
    public const string RetraitDeLaNocturne = "retirer-nocturne-retroarch.bat";

    /// <summary>LA MARQUE NEUVE, dans le dossier de RetroArch : build=, exe=, stable=, sources=. Jamais installe.txt.</summary>
    public const string Marque = ".nelfeplay/build.txt";

    /// <summary>L'ancienne marque de la nocturne du 16/10 : le service la retire, il ne la pose plus.</summary>
    public const string MarqueDeLaNocturne = ".nelfeplay/installe.txt";

    /// <summary>Pose par le desinstalleur lance a la main : le joueur est revenu a la stable, on ne reinstalle pas.</summary>
    public const string Refus = "state/retroarch-nelfeplay-refus.txt";

    /// <summary>L'empreinte du retroarch.exe de la nocturne : tant qu'il est en place, ses bibliotheques lui servent.</summary>
    public const string ExeDeLaNocturne = "cbc50e13d6a097ad7276dc40698516bf99f74d4a619a30dfbf9e849ee58d3d7b";

    /// <summary>Une valeur « cle=valeur » de la marque, ou "".</summary>
    internal static string ValeurDeLaMarque(string[] lignes, string cle)
        => lignes.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(cle + "=", StringComparison.OrdinalIgnoreCase))?[(cle.Length + 1)..].Trim() ?? "";

    internal static string Sha256De(string chemin)
    {
        using var flux = File.Open(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(flux)).ToLowerInvariant();
    }

    /// <summary>
    /// LES RESTES DE LA NOCTURNE (2026-10-17) : les fichiers que la nocturne ajoute a la racine de RetroArch 1.22.2, a leur
    /// taille et a leur empreinte dans l'archive du 16 octobre. Ils restaient quand RetroBat avait remis son RetroArch entre
    /// l'installation et le retrait (le desinstalleur laisse alors tout en place) : la mise a jour de RetroBat remettait
    /// l'ancienne libwinpthread-1.dll a cote de l'avutil-61.dll de la nocturne, et le direct, qui prenait la FFmpeg la plus
    /// recente du dossier, ouvrait chez un joueur une fenetre « clock_gettime64 introuvable » a chaque partie.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (long Taille, string Sha256)> Restes =
        new Dictionary<string, (long Taille, string Sha256)>(StringComparer.OrdinalIgnoreCase)
    {
        ["avcodec-63.dll"] = (21414912, "0d39d62b6bee1dbefe3736053f1c22c265b2e149be17cfe889050adb7d448afc"),
        ["avdevice-63.dll"] = (160256, "9d91fe01d55436fd4221a3ff33afe82a9e30088354dedae2f5ad9a201f2a3f7f"),
        ["avfilter-12.dll"] = (6281728, "f271399e9c16ae84e252934f454fc7c0debb6756a91610161cf03aefb0b19838"),
        ["avformat-63.dll"] = (2869248, "3c93892ebc9ef45295c75d553de3759f9e5dda83a33331cadb61b9fe3d1ffe87"),
        ["avutil-61.dll"] = (1102336, "83f6a4899b7d7faaf64e6cc42bda259a4e0f8e53cf4af99f8b2eb6b8c55c0079"),
        ["icudt78.dll"] = (33206351, "e441284efaf677e5a6651f656857fa50cf6dd63d28c754671d7b3450538acff3"),
        ["icuin78.dll"] = (5084935, "0af4dcbc4f51da0004107f8cdbb4d8bb7c0938efc2ec109683975163650eff01"),
        ["icuuc78.dll"] = (2867944, "2ec3d5b5b15478eef2ca195c8ecebac711d0f2f442d9cf56fa91c8dd4d48d2cb"),
        ["libbluray-2.dll"] = (2080033, "7f511900bcda94ff4e0914e51fe98f8749faee73909a570a344f6e6c9614b531"),
        ["libbrotlicommon.dll"] = (231763, "ce68162241d9b14fe362b208c1d2c96186462237e0273000899757cd9a5366e2"),
        ["libbrotlidec.dll"] = (153242, "c7b1b8755c3915504ebf79964db1d2bb9329bd97aec3dfe9abc8a9d55fff99c9"),
        ["libbrotlienc.dll"] = (893820, "a6d8aac256554803d7237f30f625d669ab73e283d2b76c85017f17e07056267c"),
        ["libgallium_wgl.dll"] = (29774213, "0513b6bbc1cd72564bb1122fa225e9d1e236785f2fc68f3be5a13558bc0887e2"),
        ["libgnurx-0.dll"] = (191756, "8289ae92449ba1402e0b7a5e11ddc74adbb515a5199b42692ba859f13fb94199"),
        ["libhogweed-7.dll"] = (8771268, "83356a2b939a75636f30fc4bffb027c98830be2e434fff0daab1476a18168c79"),
        ["libmpg123-0.dll"] = (873218, "4dbdc12b91bf81531d32a754fbeab721245dec4bc576f5396c53cd8244226c73"),
        ["libnettle-9.dll"] = (10376573, "e91cd78b66ca8c13213a192fc978ffbf04764d8fdb81c90e46069342e116679d"),
        ["libpcre2-8-0.dll"] = (557482, "b57a1c717450b0d6f6856576295000e9379f4a2aa777967bc80c62004704f735"),
        ["libtheoradec-2.dll"] = (473435, "81b620c1ef5916c419db82d43cfd6115835a707255ad229a077c89d8dbf89326"),
        ["libtheoraenc-2.dll"] = (970675, "3ee9115745187af164bcbb6e6374e89015bc42965afd46f84c75b0f32af3abe5"),
        ["libunistring-5.dll"] = (5677100, "94c35b358fa319e22be1109ce89b369862df581570d49dca76d2e5996ffbe16b"),
        ["libx264-165.dll"] = (3165982, "f0014f85580b03040679e816a6ce63d7333bc1a3e620e5fcd92fb0c3ec80c5bf"),
        ["libx265.dll"] = (26493445, "f63c39ea9912e8adf03cdd386450e0d8fa80ece4b4796822f487babce6f7d5a2"),
        ["libxml2-16.dll"] = (5615527, "5c3e6752bce0638d2d0972b5a5fdbccc8523daf00eb817d56ca99b863cc88ae9"),
        ["SDL3.dll"] = (23392677, "a3fbf83adae70db678a15f32792989a10960f002f470b45989b188418182d4f3"),
        ["swresample-7.dll"] = (180224, "1cbd46392b2ba54d097f354ec81ed09b2c4c49772568e2ed2cc01306dd516f8a"),
        ["swscale-10.dll"] = (2120192, "4083a3f263924d7acfc2dcd63d4f168c4d7a28782cc28aa8d945903251514bb1"),
    };

    /// <summary>
    /// Les restes de la nocturne dans ce dossier : un fichier de la liste a sa taille et a son empreinte exactes, jamais un
    /// fichier de RetroBat qui porterait le meme nom. Aucun tant que l'exe de la nocturne est en place.
    /// </summary>
    internal static List<string> RestesDans(string dossier, IReadOnlyDictionary<string, (long Taille, string Sha256)> liste, string exeDeLaNocturne)
    {
        var restes = new List<string>();
        foreach (var (nom, (taille, sha)) in liste)
        {
            var chemin = Path.Combine(dossier, nom);
            var info = new FileInfo(chemin);
            if (info.Exists && info.Length == taille && string.Equals(Sha256De(chemin), sha, StringComparison.OrdinalIgnoreCase))
            {
                restes.Add(chemin);
            }
        }
        var exe = Path.Combine(dossier, "retroarch.exe");
        if (restes.Count > 0 && File.Exists(exe) && string.Equals(Sha256De(exe), exeDeLaNocturne, StringComparison.OrdinalIgnoreCase))
        {
            restes.Clear();
        }
        return restes;
    }

}

/// <summary>Ce que le service doit faire a ce releve.</summary>
public enum ActionNelfePlay { Rien, Installer, Retirer, RetirerLaNocturne }

/// <summary>Ce que le disque dit, ce que le joueur a choisi : l'entree de la decision. Tout est lu avant, rien ici ne touche au disque.</summary>
public readonly record struct EtatNelfePlay(
    bool AutoInstall, bool Refus, bool MarqueDeLaNocturne, bool Marque, string ExeEnPlace, string ExeDeLaMarque,
    bool NotreExePret, bool InstalleurPresent, bool DesinstalleurPresent, bool UnJeuTourne);

/// <summary>
/// RETROARCH NELFEPLAY (1.9.216, lot 1) : le retroarch.exe 1.22.2 de RetroBat plus trois correctifs de l'amont (replay des gros
/// etats, GET_STATUS sans systemid, hote netplay contre un pair sans compression). emulatorLauncher ne lance que
/// <c>retroarch.exe</c> : notre exe en prend le nom, la stable est sauvegardee dans <c>.nelfeplay\sauvegarde</c> et revient en un
/// geste. Il ne se pose QUE par-dessus la stable connue (jamais de retrogradation), hors partie, et la copie
/// <c>retroarch.patched.RETROBAT.exe</c> d'emulatorLauncher est effacee pour qu'il la refasse depuis notre exe.
///
/// La mise a jour de RetroBat ecrase retroarch.exe : on ne restaure RIEN par-dessus, on se retire (marque et sauvegarde), puis on
/// se repose si la nouvelle base est encore la stable 1.22.2. Les scripts font le travail sur les fichiers, ce service decide
/// (<see cref="Decider"/>, pur et teste). Installation automatique allumee par defaut (<c>ApiExpose:RetroArchNelfePlay:AutoInstall</c>,
/// option d'ES <c>global.apiexpose.retroarch_nelfeplay.enabled</c>) ; eteinte, la stable est remise.
/// La nocturne SDL3 de la 1.9.60 (ancienne marque installe.txt) est toujours retiree.
/// </summary>
public sealed class RetroArchNelfePlayService : BackgroundService
{
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Releve = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApresUnEchec = TimeSpan.FromMinutes(30);
    private static readonly string[] AutresProcessus = ["emulatorLauncher", "mame", "mame64"];

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RetroArchNelfePlayService> _journal;
    private IDisposable? _abonnement;
    private int _enCours;
    private DateTime _pasAvant = DateTime.MinValue;
    private bool _restesRetires;

    public RetroArchNelfePlayService(IEventBus bus, ApiContext contexte, IConfiguration configuration, ILogger<RetroArchNelfePlayService> journal)
    {
        _bus = bus;
        _contexte = contexte;
        _configuration = configuration;
        _journal = journal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement); }
        catch (Exception ex) { _journal.LogDebug(ex, "RetroArch NelfePlay : abonnement au bus impossible."); }
        try
        {
            await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await EssayerAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Releve, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _abonnement?.Dispose();
        }
    }

    private void SurEvenement(EventEnvelope e)
    {
        // La fin d'une partie libere RetroArch : le moment d'agir, une fois ES revenu.
        if (string.Equals(e.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                await EssayerAsync(CancellationToken.None).ConfigureAwait(false);
            });
        }
    }

    /// <summary>
    /// La decision, pure. Jamais rien pendant une partie. Nocturne SDL3 : toujours retiree. Marque neuve : si l'exe en place est bien
    /// celui qu'elle nomme, on le garde (option allumee) ou on remet la stable (option eteinte) ; sinon RetroBat est passe par la :
    /// on se retire sans rien restaurer, et le meme releve repose si la nouvelle base est la stable. Sans marque : on pose si
    /// l'option est allumee, rien de refuse, notre exe pret (empreinte relue) ET la base exactement la stable connue.
    /// </summary>
    internal static ActionNelfePlay Decider(EtatNelfePlay e)
    {
        if (e.UnJeuTourne) return ActionNelfePlay.Rien;
        if (e.MarqueDeLaNocturne) return e.DesinstalleurPresent ? ActionNelfePlay.RetirerLaNocturne : ActionNelfePlay.Rien;
        var enPlace = e.ExeDeLaMarque.Length == 64 && string.Equals(e.ExeEnPlace, e.ExeDeLaMarque, StringComparison.OrdinalIgnoreCase);
        var peutPoser = e.AutoInstall && !e.Refus && e.NotreExePret && e.InstalleurPresent
            && string.Equals(e.ExeEnPlace, RetroArchNelfePlay.Stable, StringComparison.OrdinalIgnoreCase);
        if (e.Marque)
        {
            if (enPlace) return !e.AutoInstall && e.DesinstalleurPresent ? ActionNelfePlay.Retirer : ActionNelfePlay.Rien;
            // La stable est revenue sous notre marque : l'installeur ecarte l'ancienne sauvegarde et repose tout seul.
            if (peutPoser) return ActionNelfePlay.Installer;
            return e.DesinstalleurPresent ? ActionNelfePlay.Retirer : ActionNelfePlay.Rien;
        }
        return peutPoser ? ActionNelfePlay.Installer : ActionNelfePlay.Rien;
    }

    private async Task EssayerAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _enCours, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow < _pasAvant) return;
            var racine = RetroBatPaths.PluginRoot;
            var dossierRetroArch = Path.GetDirectoryName(RetroBatPaths.RetroArchConfigPath) ?? "";
            if (dossierRetroArch.Length == 0) return;
            var etat = LireLEtat(racine, dossierRetroArch);
            if (etat.UnJeuTourne) return;
            var action = Decider(etat);
            if (action == ActionNelfePlay.RetirerLaNocturne)
            {
                _journal.LogInformation("RetroArch NelfePlay : retrait de la nocturne SDL3 de la 1.9.60.");
                await LancerEtJournaliserAsync(RetroArchNelfePlay.RetraitDeLaNocturne, "/silencieux /annulation", racine, "retrait de la nocturne", ct).ConfigureAwait(false);
                RetirerLesRestes(dossierRetroArch);
                return;
            }
            if (!etat.Marque && !etat.MarqueDeLaNocturne) RetirerLesRestes(dossierRetroArch);
            switch (action)
            {
                case ActionNelfePlay.Installer:
                    _journal.LogInformation("RetroArch NelfePlay : pose de {Exe} sur la stable 1.22.2 (aucun jeu ne tourne). Sources : {Sources}.",
                        RetroArchNelfePlay.CheminDeLExe, RetroArchNelfePlay.Sources);
                    await LancerEtJournaliserAsync(RetroArchNelfePlay.Installeur, "/silencieux", racine, "installation", ct).ConfigureAwait(false);
                    break;
                case ActionNelfePlay.Retirer:
                    _journal.LogInformation(etat.AutoInstall
                        ? "RetroArch NelfePlay : RetroBat a remplace retroarch.exe, retrait propre (rien n'est restaure par-dessus)."
                        : "RetroArch NelfePlay : option eteinte, retour a la stable de RetroBat.");
                    await LancerEtJournaliserAsync(RetroArchNelfePlay.Desinstalleur, "/silencieux /annulation", racine, "retrait", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pasAvant = DateTime.UtcNow + ApresUnEchec;
            _journal.LogWarning("RetroArch NelfePlay : operation impossible ({Erreur}).", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _enCours, 0);
        }
    }

    private async Task LancerEtJournaliserAsync(string script, string arguments, string racine, string nom, CancellationToken ct)
    {
        var (code, texte) = await LancerAsync(Path.Combine(racine, script), arguments, racine, ct).ConfigureAwait(false);
        if (code == 0) _journal.LogInformation("RetroArch NelfePlay : {Operation} faite ({Sortie}).", nom, texte);
        else
        {
            _pasAvant = DateTime.UtcNow + ApresUnEchec;
            _journal.LogWarning("RetroArch NelfePlay : {Operation} non faite (code {Code}) : {Sortie}. Nouvel essai dans 30 min.", nom, code, texte);
        }
    }

    /// <summary>Lit le disque (empreintes comprises) et les reglages ; ce qui ne se lit pas vaut « absent ».</summary>
    private EtatNelfePlay LireLEtat(string racine, string dossierRetroArch)
    {
        var unJeuTourne = _contexte.Ui.Running is not null || UnProcessusDeJeuTourne();
        var exe = Path.Combine(dossierRetroArch, "retroarch.exe");
        var marque = Path.Combine(dossierRetroArch, RetroArchNelfePlay.Marque);
        var marqueDeLaNocturne = File.Exists(Path.Combine(dossierRetroArch, RetroArchNelfePlay.MarqueDeLaNocturne));
        string exeIci = "", exeMarque = "";
        try
        {
            if (!unJeuTourne && File.Exists(exe)) exeIci = RetroArchNelfePlay.Sha256De(exe);
            if (File.Exists(marque)) exeMarque = RetroArchNelfePlay.ValeurDeLaMarque(File.ReadAllLines(marque), "exe");
        }
        catch (IOException) { }
        var notre = Path.Combine(racine, RetroArchNelfePlay.CheminDeLExe.Replace('/', Path.DirectorySeparatorChar));
        var notrePret = false;
        try { notrePret = !unJeuTourne && File.Exists(notre) && string.Equals(RetroArchNelfePlay.Sha256De(notre), RetroArchNelfePlay.ExeNelfePlay, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { }
        return new EtatNelfePlay(
            _configuration.GetValue("ApiExpose:RetroArchNelfePlay:AutoInstall", true),
            File.Exists(Path.Combine(racine, RetroArchNelfePlay.Refus)),
            marqueDeLaNocturne, File.Exists(marque), exeIci, exeMarque, notrePret,
            File.Exists(Path.Combine(racine, RetroArchNelfePlay.Installeur)),
            File.Exists(Path.Combine(racine, marqueDeLaNocturne ? RetroArchNelfePlay.RetraitDeLaNocturne : RetroArchNelfePlay.Desinstalleur)),
            unJeuTourne);
    }

    /// <summary>Un jeu tourne : tout RetroArch (par PREFIXE : l'hote netplay et la copie du spectateur ont un autre nom) ou le lanceur.</summary>
    private static bool UnProcessusDeJeuTourne()
    {
        var retroarch = RetroArchProcessus.Tous();
        var tourne = retroarch.Length > 0;
        foreach (var p in retroarch) p.Dispose();
        if (tourne) return true;
        foreach (var nom in AutresProcessus)
        {
            var ps = Process.GetProcessesByName(nom);
            foreach (var p in ps) p.Dispose();
            if (ps.Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Retire les restes de la nocturne (RetroArchNelfePlay.Restes), une fois par demarrage de l'API quand tout est propre :
    /// un fichier tenu par un programme reste, et le releve suivant le reprend.
    /// </summary>
    private void RetirerLesRestes(string dossierRetroArch)
    {
        if (_restesRetires || dossierRetroArch.Length == 0) return;
        var restes = RetroArchNelfePlay.RestesDans(dossierRetroArch, RetroArchNelfePlay.Restes, RetroArchNelfePlay.ExeDeLaNocturne);
        var retires = new List<string>();
        var tenus = new List<string>();
        foreach (var chemin in restes)
        {
            try
            {
                File.Delete(chemin);
                retires.Add(Path.GetFileName(chemin));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tenus.Add(Path.GetFileName(chemin));
            }
        }
        if (retires.Count > 0)
        {
            _journal.LogInformation("RetroArch NelfePlay : {Nombre} reste(s) de la nocturne retire(s) de RetroArch : {Fichiers}.",
                retires.Count, string.Join(", ", retires));
        }
        if (tenus.Count > 0)
        {
            _journal.LogWarning("RetroArch NelfePlay : reste(s) de la nocturne encore tenu(s), nouvel essai au prochain releve : {Fichiers}.",
                string.Join(", ", tenus));
            return;
        }
        _restesRetires = true;
    }

    /// <summary>Lance un des deux scripts, sans fenetre, sa sortie gardee pour le journal ; dix minutes au plus.</summary>
    private static async Task<(int Code, string Sortie)> LancerAsync(string bat, string arguments, string dossier, CancellationToken ct)
    {
        using var processus = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" " + arguments + "\"")
            {
                WorkingDirectory = dossier,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        processus.Start();
        var sortie = processus.StandardOutput.ReadToEndAsync(ct);
        using (var delai = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            delai.CancelAfter(TimeSpan.FromMinutes(10));
            try { await processus.WaitForExitAsync(delai.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { processus.Kill(true); } catch { }
            }
        }
        var texte = string.Join(" | ", (await sortie.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return (processus.HasExited ? processus.ExitCode : -1, texte);
    }
}
