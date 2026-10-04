using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LE PROFIL DE MEDIAS CHOISI A L'INSTALLATION (demande user 2026-09-28).
///
/// L'installeur demande si le RetroBat est neuf ou deja configure, en suggerant la reponse d'apres
/// ses gamelists, et laisse le joueur choisir :
///   - neuf : APIExpose s'occupe des medias : LOCAL MEDIA MANAGER et auto-scrap actifs (l'auto-scrap
///     depend du gestionnaire, coupe par defaut depuis le 2026-10-05), PRESERVE CUSTOM MEDIA coupe ;
///   - configure : les medias du joueur sont a lui : LOCAL MEDIA MANAGER et auto-scrap coupes,
///     PRESERVE CUSTOM MEDIA actif ;
///   - garder : rien ne change (mise a jour d'une installation existante).
///
/// L'installeur ne touche pas lui-meme aux reglages : ES reecrit es_settings.cfg de memoire en
/// quittant. Il depose state\install-profile.json, applique ICI une seule fois, avant le service
/// des valeurs par defaut, qui recopie ensuite appsettings.json vers ES. Le fichier est renomme une
/// fois applique : un joueur qui change ensuite ses reglages garde son choix.
/// </summary>
public sealed class InstallProfileHostedService : IHostedService
{
    public const string FileName = "install-profile.json";
    private const string CleAutoScrap = "global.apiexpose.scraping.auto_enabled";
    private const string ClePreserve = "global.apiexpose.media_allocation.write_policy_enabled";
    private const string CleGestionnaire = "global.apiexpose.local_media_manager.enabled";

    private readonly ApiExposeAppsettingsSyncService _appsettings;
    private readonly IEsSettingsStore _esSettings;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<InstallProfileHostedService>? _logger;
    private readonly string _dossierEtat;

    public InstallProfileHostedService(
        ApiExposeAppsettingsSyncService appsettings,
        IEsSettingsStore esSettings,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<InstallProfileHostedService>? logger = null,
        string? dossierEtat = null)
    {
        _appsettings = appsettings;
        _esSettings = esSettings;
        _options = options;
        _logger = logger;
        _dossierEtat = dossierEtat ?? Path.Combine(RetroBatPaths.PluginRoot, "state");
    }

    /// <summary>
    /// Les reglages d'un profil : (auto-scrap, preserve custom media, local media manager), ou null
    /// pour « garder ».
    /// </summary>
    internal static (bool AutoScrap, bool Preserve, bool Gestionnaire)? Reglages(string? profil) => (profil ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "neuf" or "new" => (true, false, true),
        "configure" or "configured" => (false, true, false),
        _ => null,
    };

    /// <summary>Le profil depose par l'installeur, ou null sans fichier lisible.</summary>
    internal static string? LireProfil(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(chemin));
            return doc.RootElement.TryGetProperty("media_profile", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var chemin = Path.Combine(_dossierEtat, FileName);
        if (!File.Exists(chemin)) return;

        var profil = LireProfil(chemin);
        var reglages = Reglages(profil);
        if (reglages is { } r)
        {
            var auto = r.AutoScrap ? "1" : "0";
            var preserve = r.Preserve ? "1" : "0";
            var gestionnaire = r.Gestionnaire ? "1" : "0";
            // appsettings.json fait foi au demarrage (le service des valeurs par defaut le recopie
            // vers ES) ; ES est ecrit aussi, pour que le menu le montre sans attendre.
            _appsettings.ApplyEsSettingsChanges(new[]
            {
                new ApiExposeSettingChange(CleAutoScrap, string.Empty, auto),
                new ApiExposeSettingChange(ClePreserve, string.Empty, preserve),
                new ApiExposeSettingChange(CleGestionnaire, string.Empty, gestionnaire),
            });
            try
            {
                _esSettings.Update(document =>
                {
                    var root = document.Root;
                    if (root is null) return false;
                    return Ecrire(root, CleAutoScrap, auto) | Ecrire(root, ClePreserve, preserve) | Ecrire(root, CleGestionnaire, gestionnaire);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Profil d'installation : es_settings.cfg non ecrit, appsettings.json fera foi.");
            }

            // Le service des valeurs par defaut lit les options : on attend qu'elles aient relu le fichier.
            for (var i = 0; i < 20 && !cancellationToken.IsCancellationRequested; i++)
            {
                var o = _options.CurrentValue;
                if (o.Scraping.AutoScrapingEnabled == r.AutoScrap && o.MediaAllocation.WritePolicyEnabled == r.Preserve
                    && o.LocalMediaManager.Enabled == r.Gestionnaire) break;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            _logger?.LogInformation("Profil d'installation « {Profil} » applique : LOCAL MEDIA MANAGER {Gestionnaire}, auto-scrap {Auto}, PRESERVE CUSTOM MEDIA {Preserve}.",
                profil, r.Gestionnaire ? "actif" : "coupe", r.AutoScrap ? "actif" : "coupe", r.Preserve ? "actif" : "coupe");
        }
        else
        {
            _logger?.LogInformation("Profil d'installation « {Profil} » : reglages gardes tels quels.", profil ?? "?");
        }

        try
        {
            File.Move(chemin, Path.Combine(_dossierEtat, $"install-profile.applied-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Profil d'installation applique mais non archive : il sera reapplique au prochain demarrage.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool Ecrire(XElement root, string cle, string valeur)
    {
        var existant = root.Elements().FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase));
        if (existant is null)
        {
            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(new XElement("string", new XAttribute("name", cle), new XAttribute("value", valeur)));
            return true;
        }

        if (string.Equals(existant.Attribute("value")?.Value, valeur, StringComparison.Ordinal)) return false;
        existant.SetAttributeValue("value", valeur);
        return true;
    }
}
