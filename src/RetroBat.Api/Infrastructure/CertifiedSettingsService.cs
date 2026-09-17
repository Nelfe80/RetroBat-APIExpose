using System.Text;
using System.Text.Json;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Pose les réglages certifiés d'un jeu AVANT que l'émulateur ne le charge.
///
/// Un score n'est comparable qu'à réglages égaux. Jusqu'ici la borne le découvrait à la fin de
/// la partie : le profil n'épinglait que des EMPREINTES, et une empreinte ne se remonte pas, donc
/// la plateforme ne pouvait ni nommer le réglage fautif ni le corriger. Le profil publie
/// désormais ses réglages en clair (<c>core_options_expected</c>) ; ce service les dépose dans
/// <c>plugins\APIExpose\wrapper\certified.txt</c>, et le listener répond ces valeurs au cœur au
/// chargement. Aucun fichier de configuration n'est réécrit : RetroBat réécrit les siens à chaque
/// lancement, la course serait perdue d'avance.
///
/// Le fichier nomme la ROM à laquelle il s'applique et disparaît à la fin de la partie : rien ne
/// se force sur un jeu que la borne n'a pas préparé.
/// </summary>
public sealed class CertifiedSettingsService : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ApiContext _context;
    private readonly RomCanonicalResolver _canonical;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<CertifiedSettingsService>? _logger;
    private IDisposable? _abonnement;

    public CertifiedSettingsService(
        IEventBus bus,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ApiContext context,
        RomCanonicalResolver canonical,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<CertifiedSettingsService>? logger = null)
    {
        _bus = bus;
        _httpFactory = httpFactory;
        _devices = devices;
        _context = context;
        _canonical = canonical;
        _options = options;
        _logger = logger;
    }

    /// <summary>Le fichier lu par le listener au chargement.</summary>
    public static string FilePath => Path.Combine(RetroBatPaths.RetroBatRoot, "plugins", "APIExpose", "wrapper", "certified.txt");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(e =>
        {
            var type = e.Type?.ToLowerInvariant();
            if (type == "ui.game.started.raw" || type == "ui.game.started")
            {
                _ = PreparerAsync(CancellationToken.None);
            }
            else if (type == "ui.game.ended" || type == "ui.game.ended.raw")
            {
                Effacer();
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Effacer();
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    private async Task PreparerAsync(CancellationToken ct)
    {
        try
        {
            if (!_options.CurrentValue.NelfePlay.ForceCertifiedSettings)
            {
                Effacer();
                return;
            }

            var jeu = _context.Ui.Running ?? _context.Ui.Selected;
            var chemin = jeu?.GamePath;
            if (string.IsNullOrWhiteSpace(chemin)) return;

            var systeme = jeu?.SystemId ?? string.Empty;
            var fichier = Path.GetFileName(chemin);
            var romGroup = _canonical.ResolveScoreSlug(systeme, fichier, null, null);
            if (string.IsNullOrWhiteSpace(romGroup)) return;

            var attendus = await ValeursAttenduesAsync(systeme, romGroup!, ct).ConfigureAwait(false);
            if (attendus is null || attendus.Count == 0)
            {
                Effacer();
                return;
            }

            Ecrire(Path.GetFileNameWithoutExtension(chemin), attendus);
            _logger?.LogInformation("Reglages certifies poses pour {Rom} : {Nombre} valeur(s).", romGroup, attendus.Count);
        }
        catch (Exception ex)
        {
            // Jamais au prix de la partie : sans fichier, le listener ne force rien.
            _logger?.LogWarning(ex, "Reglages certifies : preparation impossible.");
            Effacer();
        }
    }

    /// <summary>Les valeurs du profil ouvert, ou null s'il n'y en a pas (jeu non ouvert, borne sans compte).</summary>
    private async Task<Dictionary<string, string>?> ValeursAttenduesAsync(string systemId, string romGroup, CancellationToken ct)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return null;

        using var client = _httpFactory.CreateClient(nameof(CertifiedSettingsService));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

        var url = $"/api/v1/agent/scores/profile?system_id={Uri.EscapeDataString(systemId)}&rom_group={Uri.EscapeDataString(romGroup)}";
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("profile", out var profil)
            || !profil.TryGetProperty("core_options_expected", out var attendus)
            || attendus.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Plusieurs entrées quand un profil sert plusieurs moteurs. Le cœur chargé n'est pas encore
        // connu à ce moment (il lira ses options au chargement), mais chaque moteur a ses propres
        // clés : l'union convient. Une clé donnée par deux entrées avec des valeurs différentes est
        // ambiguë, donc écartée plutôt que devinée.
        var valeurs = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambigues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entree in attendus.EnumerateArray())
        {
            if (!entree.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object) continue;
            foreach (var paire in v.EnumerateObject())
            {
                if (paire.Value.ValueKind != JsonValueKind.String) continue;
                var valeur = paire.Value.GetString() ?? string.Empty;
                if (valeurs.TryGetValue(paire.Name, out var deja) && deja != valeur) ambigues.Add(paire.Name);
                else valeurs[paire.Name] = valeur;
            }
        }
        foreach (var cle in ambigues) valeurs.Remove(cle);
        return valeurs;
    }

    private static void Ecrire(string romBasename, Dictionary<string, string> valeurs)
    {
        var texte = new StringBuilder();
        texte.Append("rom=").Append(romBasename).Append('\n');
        foreach (var paire in valeurs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // Une valeur multiligne n'existe pas côté libretro ; on s'en assure quand même.
            if (paire.Key.Contains('=') || paire.Value.Contains('\n') || paire.Value.Contains('\r')) continue;
            texte.Append("opt.").Append(paire.Key).Append('=').Append(paire.Value).Append('\n');
        }
        var chemin = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, texte.ToString(), new UTF8Encoding(false));
    }

    private static void Effacer()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // Un fichier verrouillé ne vaut pas d'interrompre quoi que ce soit : il nomme sa ROM,
            // donc il ne peut rien forcer sur un autre jeu.
        }
    }

    private string? ResolveCredential()
    {
        var paired = _devices.GetCredential();
        if (!string.IsNullOrEmpty(paired)) return paired;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }
        }
        catch { }
        return null;
    }
}
