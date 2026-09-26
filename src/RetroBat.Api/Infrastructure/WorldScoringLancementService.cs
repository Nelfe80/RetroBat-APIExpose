using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// DANS WORLD SCORING, NOTRE COEUR ; AILLEURS, CELUI DU JOUEUR (regle user 2026-09-27).
///
/// Pour EmulationStation, un jeu de la collection EST l'entree de son systeme : meme fiche, memes
/// metadonnees &lt;emulator&gt;/&lt;core&gt;. Et c'est de la fiche en memoire qu'ES tire le coeur qu'il
/// passe au lanceur, lequel le prefere a tout reglage par jeu (vu dans emulatorLauncher : les
/// arguments sont importes en dernier ; FindBestMameCore n'agit que sans coeur designe). Imposer un
/// coeur a la collection seule n'est donc possible qu'en changeant la fiche pendant qu'on y est :
/// - en entrant dans World Scoring, chaque jeu dont le lancement ne mesurerait pas recoit notre
///   coeur fonctionnel par /addgames, qui fusionne sans toucher au reste de la fiche ;
/// - en sortant, le choix de confort du joueur lui est rendu, sauf s'il l'a change entre-temps.
/// /addgames ne sait pas VIDER un champ : un jeu qui etait en automatique garde notre coeur, ce qui
/// ne retire aucun choix au joueur. Les choix a rendre sont notes sur disque, et rendus au
/// demarrage suivant si l'API s'est arretee entre les deux.
/// </summary>
public sealed class WorldScoringLancementService : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly EmulationStationSystemConfigService _systemes;
    private readonly EmulationStationSettingsService _reglages;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<WorldScoringLancementService>? _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private readonly HttpClient _es = new() { BaseAddress = new Uri("http://127.0.0.1:1234"), Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _etatPath;
    private IDisposable? _abonnement;
    private CancellationTokenSource? _arret;

    public WorldScoringLancementService(
        IEventBus bus,
        EmulationStationSystemConfigService systemes,
        EmulationStationSettingsService reglages,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<WorldScoringLancementService>? logger = null)
    {
        _bus = bus;
        _systemes = systemes;
        _reglages = reglages;
        _options = options;
        _logger = logger;
        _etatPath = Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "world-scoring-lancements.json");
    }

    /// <summary>Le choix de confort d'un jeu, et ce que World Scoring lui a impose.</summary>
    internal sealed record Confort(string Emulator, string Core, string ImposeEmulator, string ImposeCore);

    internal enum Remise
    {
        /// <summary>Remettre le choix du joueur.</summary>
        RemettreConfort,
        /// <summary>Le jeu etait en automatique : rien a rendre, notre coeur reste.</summary>
        GarderLeNotre,
        /// <summary>Le joueur a change la fiche entre-temps : son nouveau choix fait foi.</summary>
        ChoixDuJoueur,
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _arret = new CancellationTokenSource();
        _abonnement = _bus.Subscribe<EventEnvelope>(OnEvenement);
        // Un choix reste a rendre si l'API s'est arretee pendant qu'on etait dans World Scoring.
        // ES n'est peut-etre pas encore la : on reessaie quelques minutes.
        _ = Task.Run(() => RendreAuDemarrageAsync(_arret.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret?.Cancel();
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _arret?.Dispose();
        _es.Dispose();
        _porte.Dispose();
    }

    /// <summary>
    /// Le lancement d'un jeu de World Scoring mesure-t-il ? Sinon, le coeur a lui imposer ; null
    /// quand il mesure deja ou que son systeme ne declare rien qui mesure.
    /// </summary>
    internal static (string Emulator, string Core)? AImposer(
        EmulationStationLaunchConfig lancement,
        bool coeurChoisi,
        IReadOnlyList<EmulationStationSystemEmulatorCore> coeursDuSysteme)
    {
        if (CoeursObservables.Juger(lancement, coeurChoisi, coeursDuSysteme).Observable)
        {
            return null;
        }

        return CoeursObservables.MeilleurLancement(coeursDuSysteme);
    }

    /// <summary>Que faire d'un choix mis de cote, au vu de la fiche actuelle dans ES ?</summary>
    internal static Remise QueRendre(Confort confort, string emulatorActuel, string coreActuel)
    {
        var toujoursLeNotre =
            string.Equals(emulatorActuel, confort.ImposeEmulator, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(coreActuel, confort.ImposeCore, StringComparison.OrdinalIgnoreCase);
        if (!toujoursLeNotre)
        {
            return Remise.ChoixDuJoueur;
        }

        return confort.Emulator.Length == 0 && confort.Core.Length == 0
            ? Remise.GarderLeNotre
            : Remise.RemettreConfort;
    }

    private void OnEvenement(EventEnvelope evenement)
    {
        if (!string.Equals(evenement.Type, "ui.system.selected.raw", StringComparison.Ordinal))
        {
            return;
        }

        string systeme;
        try
        {
            var charge = JsonSerializer.SerializeToElement(evenement.Payload);
            systeme = charge.TryGetProperty("Selection", out var selection) &&
                      selection.ValueKind == JsonValueKind.Object &&
                      selection.TryGetProperty("SystemId", out var id)
                ? id.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return;
        }

        var dans = string.Equals(systeme, NelfePlayScoringCollectionSyncService.CollectionName, StringComparison.OrdinalIgnoreCase);
        var jeton = _arret?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (dans)
                {
                    await ImposerAsync(jeton).ConfigureAwait(false);
                }
                else
                {
                    await RendreAsync(jeton).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "World Scoring : coeur de lancement non ajuste.");
            }
        }, jeton);
    }

    private async Task ImposerAsync(CancellationToken cancellationToken)
    {
        var nelfeplay = _options.CurrentValue.NelfePlay;
        if (!nelfeplay.Enabled || !nelfeplay.ShowScoringCollection)
        {
            return;
        }

        await _porte.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collection = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "collections",
                "custom-" + NelfePlayScoringCollectionSyncService.CollectionName + ".cfg");
            var jeux = CertifiedSettingsService.JeuxDeLaCollection(collection, RetroBatPaths.RomsRoot,
                Path.GetDirectoryName(RetroBatPaths.EmulationStationConfigRoot) ?? RetroBatPaths.EmulationStationConfigRoot);
            if (jeux.Count == 0)
            {
                return;
            }

            var reglages = _reglages.GetAllSettings();
            var etat = LireEtat();
            var imposes = new List<string>();
            foreach (var groupe in jeux.GroupBy(jeu => jeu.Systeme, StringComparer.OrdinalIgnoreCase))
            {
                var fiches = await LireFichesAsync(groupe.Key, cancellationToken).ConfigureAwait(false);
                if (fiches == null)
                {
                    continue;
                }

                var coeurs = _systemes.GetEmulatorCores(groupe.Key);
                var fragments = new List<XElement>();
                var cles = new List<(string Cle, Confort Confort)>();
                foreach (var (systeme, fichier) in groupe)
                {
                    fiches.TryGetValue(fichier, out var actuel);
                    var (lancement, coeurChoisi) = _systemes.ResolveGameLaunchConfig(systeme, actuel.Emulator, actuel.Core, reglages);
                    if (AImposer(lancement, coeurChoisi, coeurs) is not { } impose)
                    {
                        continue;
                    }

                    var cle = systeme + "/" + fichier;
                    cles.Add((cle, etat.TryGetValue(cle, out var deja)
                        ? deja
                        : new Confort(actuel.Emulator ?? string.Empty, actuel.Core ?? string.Empty, impose.Emulator, impose.Core)));
                    fragments.Add(Fragment(fichier, impose.Emulator, impose.Core));
                }

                if (fragments.Count > 0 && await EnvoyerAsync(groupe.Key, fragments, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var (cle, confort) in cles)
                    {
                        etat[cle] = confort;
                        imposes.Add($"{cle} → {confort.ImposeCore}");
                    }
                }
            }

            if (imposes.Count > 0)
            {
                EcrireEtat(etat);
                _logger?.LogInformation("World Scoring : coeur fonctionnel impose pour {Nombre} jeu(x) : {Detail}",
                    imposes.Count, string.Join(", ", imposes));
            }
        }
        finally
        {
            _porte.Release();
        }
    }

    private async Task<bool> RendreAsync(CancellationToken cancellationToken)
    {
        await _porte.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var etat = LireEtat();
            if (etat.Count == 0)
            {
                return true;
            }

            var rendus = new List<string>();
            var toutFait = true;
            foreach (var groupe in etat.GroupBy(entree => entree.Key.Split('/')[0], StringComparer.OrdinalIgnoreCase).ToList())
            {
                var fiches = await LireFichesAsync(groupe.Key, cancellationToken).ConfigureAwait(false);
                if (fiches == null)
                {
                    toutFait = false;
                    continue;
                }

                var fragments = new List<XElement>();
                var aRetirer = new List<string>();
                foreach (var (cle, confort) in groupe)
                {
                    var fichier = cle[(cle.IndexOf('/') + 1)..];
                    fiches.TryGetValue(fichier, out var actuel);
                    switch (QueRendre(confort, actuel.Emulator ?? string.Empty, actuel.Core ?? string.Empty))
                    {
                        case Remise.RemettreConfort:
                            fragments.Add(Fragment(fichier, confort.Emulator, confort.Core));
                            rendus.Add($"{cle} → {(confort.Core.Length > 0 ? confort.Core : confort.Emulator)}");
                            break;
                        default:
                            aRetirer.Add(cle);
                            break;
                    }
                }

                if (fragments.Count > 0)
                {
                    if (await EnvoyerAsync(groupe.Key, fragments, cancellationToken).ConfigureAwait(false))
                    {
                        aRetirer.AddRange(groupe.Select(entree => entree.Key));
                    }
                    else
                    {
                        toutFait = false;
                    }
                }

                foreach (var cle in aRetirer)
                {
                    etat.Remove(cle);
                }
            }

            EcrireEtat(etat);
            if (rendus.Count > 0)
            {
                _logger?.LogInformation("World Scoring : choix de confort rendu pour {Nombre} jeu(x) : {Detail}",
                    rendus.Count, string.Join(", ", rendus));
            }

            return toutFait;
        }
        finally
        {
            _porte.Release();
        }
    }

    private async Task RendreAuDemarrageAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var essai = 0; essai < 10 && LireEtat().Count > 0; essai++)
            {
                await Task.Delay(TimeSpan.FromSeconds(essai == 0 ? 20 : 30), cancellationToken).ConfigureAwait(false);
                if (await RendreAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "World Scoring : choix de confort non rendus au demarrage.");
        }
    }

    private static XElement Fragment(string fichier, string emulator, string core)
    {
        var jeu = new XElement("game", new XElement("path", "./" + fichier));
        if (emulator.Length > 0)
        {
            jeu.Add(new XElement("emulator", emulator));
        }

        if (core.Length > 0)
        {
            jeu.Add(new XElement("core", core));
        }

        return jeu;
    }

    /// <summary>La fiche de chaque jeu du systeme dans ES : fichier → (emulator, core). Null si ES ne repond pas.</summary>
    private async Task<Dictionary<string, (string? Emulator, string? Core)>?> LireFichesAsync(string systeme, CancellationToken cancellationToken)
    {
        try
        {
            using var reponse = await _es.GetAsync($"/systems/{Uri.EscapeDataString(systeme)}/games", cancellationToken).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var fiches = new Dictionary<string, (string? Emulator, string? Core)>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return fiches;
            }

            foreach (var jeu in doc.RootElement.EnumerateArray())
            {
                var chemin = jeu.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(chemin))
                {
                    continue;
                }

                fiches[Path.GetFileName(chemin.Replace('/', Path.DirectorySeparatorChar))] = (
                    jeu.TryGetProperty("emulator", out var e) ? e.GetString() : null,
                    jeu.TryGetProperty("core", out var c) ? c.GetString() : null);
            }

            return fiches;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<bool> EnvoyerAsync(string systeme, IReadOnlyList<XElement> fragments, CancellationToken cancellationToken)
    {
        try
        {
            var xml = new XElement("gameList", fragments).ToString(SaveOptions.DisableFormatting);
            using var contenu = new StringContent(xml, Encoding.UTF8, "application/xml");
            using var reponse = await _es.PostAsync($"/addgames/{Uri.EscapeDataString(systeme)}", contenu, cancellationToken).ConfigureAwait(false);
            return reponse.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private Dictionary<string, Confort> LireEtat()
    {
        try
        {
            if (!File.Exists(_etatPath))
            {
                return new Dictionary<string, Confort>(StringComparer.OrdinalIgnoreCase);
            }

            var lu = JsonSerializer.Deserialize<Dictionary<string, Confort>>(File.ReadAllText(_etatPath));
            return new Dictionary<string, Confort>(lu ?? new Dictionary<string, Confort>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, Confort>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EcrireEtat(Dictionary<string, Confort> etat)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_etatPath)!);
            if (etat.Count == 0)
            {
                if (File.Exists(_etatPath))
                {
                    File.Delete(_etatPath);
                }

                return;
            }

            File.WriteAllText(_etatPath, JsonSerializer.Serialize(etat));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "World Scoring : choix de confort non notes sur disque.");
        }
    }
}
