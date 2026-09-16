using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using RetroBat.Api.Hubs;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// RetroBat.Api.exe --self-test : ce qu'il faut pour demarrer, sans demarrer.
///
/// Le conteneur a deja ete construit avec la validation de TOUS les services (ValidateOnBuild) :
/// un service impossible a construire a donc deja fait echouer Build. Reste ici : charger les
/// assemblies, lire la configuration, resoudre les quelques services sans lesquels l'API ne sert
/// rien, lire les ressources, ecrire dans un dossier temporaire. Aucun port ouvert, aucun service
/// d'arriere-plan lance, aucune donnee de la borne modifiee.
/// </summary>
public static class ApiSelfTest
{
    public static int Run(WebApplication app)
    {
        var echecs = 0;
        void Ok(string quoi, string detail) => Console.WriteLine($"PASS {quoi} : {detail}");
        void Ko(string quoi, string detail) { Console.WriteLine($"FAIL {quoi} : {detail}"); echecs++; }

        try
        {
            var entree = Assembly.GetEntryAssembly() ?? typeof(ApiSelfTest).Assembly;
            var chargees = 0;
            foreach (var nom in entree.GetReferencedAssemblies())
            {
                try { Assembly.Load(nom); chargees++; }
                catch (Exception ex) { Ko("assembly " + nom.Name, ex.Message); }
            }
            Ok("assemblies", $"{chargees} reference(s) chargee(s), version {ApiExposeVersion.Current}");
        }
        catch (Exception ex) { Ko("assemblies", ex.Message); }

        try
        {
            var section = app.Configuration.GetSection("ApiExpose");
            var options = app.Services.GetRequiredService<IOptions<ApiExposeOptions>>().Value;
            Ok("configuration", $"section ApiExpose {(section.Exists() ? "presente" : "ABSENTE (valeurs par defaut)")}, ecoute {app.Configuration["Urls"] ?? "http://127.0.0.1:12345"}, console {options.Logging.ConsoleEnabled}");
        }
        catch (Exception ex) { Ko("configuration", ex.GetType().Name + " : " + ex.Message); }

        foreach (var type in new[] { typeof(WebSocketConnectionManager), typeof(ApiExposeRuntimeOptionsService), typeof(StartupReadinessState) })
        {
            try
            {
                _ = app.Services.GetRequiredService(type);
                Ok("service " + type.Name, "resolu");
            }
            catch (Exception ex) { Ko("service " + type.Name, ex.GetType().Name + " : " + ex.Message); }
        }

        try
        {
            var ressources = Path.Combine(RetroBatPaths.PluginRoot, "resources");
            if (!Directory.Exists(ressources))
            {
                Ko("ressources", "dossier absent : " + ressources);
            }
            else
            {
                var textes = Path.Combine(ressources, "locales", "interface-texts.json");
                if (File.Exists(textes))
                {
                    using var flux = File.OpenRead(textes);
                    using var _ = JsonDocument.Parse(flux);
                    Ok("ressources", $"{ressources} lisible, interface-texts.json valide");
                }
                else
                {
                    Ok("ressources", $"{ressources} lisible (interface-texts.json absent : textes par defaut)");
                }
            }
        }
        catch (Exception ex) { Ko("ressources", ex.GetType().Name + " : " + ex.Message); }

        try
        {
            var dossier = Path.Combine(Path.GetTempPath(), "APIExpose-Diagnostic");
            Directory.CreateDirectory(dossier);
            var fichier = Path.Combine(dossier, $"api-self-test-{Environment.ProcessId}.tmp");
            File.WriteAllText(fichier, "self-test");
            var relu = File.ReadAllText(fichier);
            File.Delete(fichier);
            if (relu == "self-test") Ok("ecriture temporaire", dossier);
            else Ko("ecriture temporaire", "relecture differente");
        }
        catch (Exception ex) { Ko("ecriture temporaire", ex.GetType().Name + " : " + ex.Message); }

        Console.WriteLine(echecs == 0 ? "self-test ok" : $"self-test : {echecs} echec(s)");
        return echecs == 0 ? 0 : 1;
    }
}
