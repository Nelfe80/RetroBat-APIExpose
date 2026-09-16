using System.Diagnostics;
using System.Text;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Le journal des erreurs fatales, disponible AVANT l'hote ASP.NET.
///
/// Une API qui meurt pendant son demarrage ne laisse rien : la console est souvent masquee, le
/// journal d'execution n'existe pas encore. Ce journal-ci s'ecrit de facon synchrone depuis les
/// evenements de plus bas niveau (exception non geree, tache non observee), dans le dossier de la
/// session de diagnostic quand l'outil en a ouvert une, sinon dans .log, sinon sous %TEMP%. Une
/// ecriture qui echoue ne leve jamais une seconde exception.
/// </summary>
public static class StartupFatalLog
{
    public const string FileName = "startup-fatal.log";

    private static readonly object Verrou = new();
    private static string? _dossierSession;
    private static bool _modeDiagnostic;
    private static string[] _arguments = Array.Empty<string>();
    private static int _enregistre;

    public static void Configure(bool diagnosticMode, string? diagnosticConfigPath, string[] arguments)
    {
        _modeDiagnostic = diagnosticMode;
        _arguments = arguments;
        try
        {
            _dossierSession = diagnosticConfigPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(diagnosticConfigPath));
        }
        catch { _dossierSession = null; }
    }

    public static void Register()
    {
        if (Interlocked.Exchange(ref _enregistre, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Exception non geree" + (e.IsTerminating ? " (fatale)" : ""), e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
        // Une tache non observee n'arrete pas le processus : on ne la note qu'en diagnostic, sinon
        // le journal fatal d'une borne en service se remplirait de bruit.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (_modeDiagnostic) Write("Tache non observee (non fatale)", e.Exception, null);
        };
    }

    /// <summary>Les emplacements, dans l'ordre : session de diagnostic, dossier .log du plugin, %TEMP%.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (_dossierSession is not null) yield return Path.Combine(_dossierSession, FileName);
        string? plugin = null;
        try { plugin = Path.Combine(RetroBatPaths.PluginRoot, ".log", FileName); } catch { }
        if (plugin is not null) yield return plugin;
        yield return Path.Combine(Path.GetTempPath(), "APIExpose-Diagnostic", FileName);
    }

    public static void Write(string kind, Exception? exception, string? fallbackText)
    {
        string texte;
        try { texte = Format(kind, exception, fallbackText); }
        catch { texte = $"{DateTime.Now:O} {kind} : {exception?.GetType().FullName} {exception?.Message}{Environment.NewLine}"; }

        lock (Verrou)
        {
            foreach (var chemin in Candidates())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
                    File.AppendAllText(chemin, texte, new UTF8Encoding(false));
                    return;
                }
                catch
                {
                    // emplacement suivant
                }
            }
        }
    }

    internal static string Format(string kind, Exception? exception, string? fallbackText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==================================================================");
        sb.AppendLine($"Date        : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine($"Evenement   : {kind}");
        sb.AppendLine($"Version     : {SafeVersion()}");
        sb.AppendLine($"PID         : {Environment.ProcessId}");
        sb.AppendLine($"Arguments   : {string.Join(" ", _arguments.Select(Quote))}");
        sb.AppendLine($"Exe         : {Environment.ProcessPath}");
        sb.AppendLine($"Dossier exe : {AppContext.BaseDirectory}");
        sb.AppendLine($"Repertoire  : {SafeCurrentDirectory()}");
        sb.AppendLine($"Plugin      : {SafePluginRoot()}");
        sb.AppendLine($"Code sortie : {Environment.ExitCode}");
        if (exception is null)
        {
            sb.AppendLine($"Objet       : {fallbackText ?? "(aucun)"}");
        }
        else
        {
            sb.AppendLine($"Type        : {exception.GetType().FullName}");
            sb.AppendLine($"Message     : {exception.Message}");
            var interne = exception.InnerException;
            while (interne is not null)
            {
                sb.AppendLine($"Interne     : {interne.GetType().FullName} : {interne.Message}");
                interne = interne.InnerException;
            }
            sb.AppendLine("Detail complet :");
            sb.AppendLine(exception.ToString());
        }
        return sb.ToString();
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    private static string SafeVersion()
    {
        try { return ApiExposeVersion.Current; }
        catch { try { return FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "?"; } catch { return "?"; } }
    }

    private static string SafeCurrentDirectory()
    {
        try { return Environment.CurrentDirectory; } catch { return "?"; }
    }

    private static string SafePluginRoot()
    {
        try { return RetroBatPaths.PluginRoot; } catch (Exception ex) { return "(illisible : " + ex.Message + ")"; }
    }
}
