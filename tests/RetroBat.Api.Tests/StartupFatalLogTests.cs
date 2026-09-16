using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le journal fatal du demarrage : ce qu'il ecrit doit suffire a comprendre une API morte avant son
/// premier log, et le manifeste des executables doit rester lisible par l'outil de diagnostic.
/// </summary>
public class StartupFatalLogTests
{
    [Fact]
    public void Le_journal_porte_le_type_le_message_et_l_exception_interne()
    {
        StartupFatalLog.Configure(true, null, new[] { "--diagnostic-mode" });
        Exception erreur;
        try { throw new InvalidOperationException("Kestrel n'ecoute pas", new IOException("port deja pris")); }
        catch (Exception ex) { erreur = ex; }

        var texte = StartupFatalLog.Format("Exception non geree (fatale)", erreur, null);

        Assert.Contains("Exception non geree (fatale)", texte);
        Assert.Contains("System.InvalidOperationException", texte);
        Assert.Contains("Kestrel n'ecoute pas", texte);
        Assert.Contains("Interne     : System.IO.IOException : port deja pris", texte);
        Assert.Contains("--diagnostic-mode", texte);
        Assert.Contains($"PID         : {Environment.ProcessId}", texte);
        Assert.Contains("Le_journal_porte", texte);   // la pile d'appels
    }

    [Fact]
    public void La_session_de_diagnostic_passe_avant_le_dossier_du_plugin()
    {
        var session = Path.Combine(Path.GetTempPath(), "diag-session-x");
        StartupFatalLog.Configure(true, Path.Combine(session, "appsettings.diagnostic.json"), Array.Empty<string>());
        var candidats = StartupFatalLog.Candidates().ToList();
        Assert.Equal(Path.Combine(session, StartupFatalLog.FileName), candidats[0]);
        Assert.EndsWith(Path.Combine("APIExpose-Diagnostic", StartupFatalLog.FileName), candidats[^1]);
        StartupFatalLog.Configure(false, null, Array.Empty<string>());
    }

    [Fact]
    public void Le_manifeste_des_executables_declare_les_trois_exe_de_la_racine()
    {
        var dossier = AppContext.BaseDirectory;
        string? manifeste = null;
        while (dossier is not null && manifeste is null)
        {
            var candidat = Path.Combine(dossier, "executables.manifest.json");
            if (File.Exists(candidat)) manifeste = candidat;
            dossier = Path.GetDirectoryName(dossier);
        }
        Assert.NotNull(manifeste);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifeste!));
        var fichiers = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("file").GetString()).ToArray();
        Assert.Equal(new[] { "RetroBat.Api.exe", "RetroBat.Api.Update.exe", "RetroBat.Api.Diagnostic.exe" }, fichiers);
        Assert.All(doc.RootElement.EnumerateArray(), e => Assert.InRange(e.GetProperty("timeoutSeconds").GetInt32(), 1, 600));
    }
}
