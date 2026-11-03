using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>Un jeu de la collection des defis : son fichier et ses defis ouverts.</summary>
public sealed record JeuAvecDefis(string Chemin, IReadOnlyList<Reglement> Defis);

/// <summary>
/// LA COLLECTION « NELFEPLAY CHALLENGES &amp; SPEEDRUNS » (lot P4 du CDC du panneau) : les jeux de la collection World Scoring
/// qui ont un defi ouvert dont le .MEM est celui de cette borne. World Scoring a deja fait le tri : contenu confirme,
/// bootlegs et hacks ecartes, .MEM officiel, rien de la ludotheque envoye. Le site refuse d'ailleurs un defi sans profil
/// de scoring ouvert du meme jeu.
///
/// Un lancement depuis cette collection par ES reste une partie du joueur : seul le panneau lance une epreuve.
/// </summary>
public sealed class DefisCollectionSyncService : BackgroundService
{
    public const string CollectionName = "nelfeplay-challenges";

    /// <summary>Le logo livre par le Data Pack (<c>theme/images</c>).</summary>
    public const string LogoFile = "nelfeplay-challenges.png";

    /// <summary>Le fond de World Scoring : meme univers visuel.</summary>
    public const string FondFile = "nelfeplay-worldscoring-bg.jpg";

    /// <summary>Apres la premiere passe de World Scoring (45 s) et la lecture de l'index des defis (30 s).</summary>
    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(5);

    /// <summary>World Scoring n'a pas encore fini sa premiere passe (un index lent, des gamelists a relire) : on repasse vite.</summary>
    private static readonly TimeSpan AttenteDeWorldScoring = TimeSpan.FromSeconds(15);

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly CatalogueDesDefis _catalogue;
    private readonly NelfePlayScoringCollectionSyncService _scoring;
    private readonly EsCustomCollectionWriter _writer;
    private readonly EsCollectionThemeAssets _assets;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEsSettingsChangeBus? _settingsChangeBus;
    private readonly IEventBus? _bus;
    private readonly ILogger<DefisCollectionSyncService>? _logger;
    private readonly Func<bool> _emulateurTourne;
    private readonly Func<Reglement, bool> _memConforme;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private IDisposable? _abonnementReglages;
    private IDisposable? _abonnementBus;
    private bool _derniereVisibilite = true;

    public DefisCollectionSyncService(
        IOptionsMonitor<ApiExposeOptions> options,
        CatalogueDesDefis catalogue,
        NelfePlayScoringCollectionSyncService scoring,
        EsCustomCollectionWriter writer,
        EsCollectionThemeAssets assets,
        MediaRuntimeState runtimeState,
        IEsSettingsChangeBus? settingsChangeBus = null,
        IEventBus? bus = null,
        ILogger<DefisCollectionSyncService>? logger = null,
        Func<bool>? emulateurTourne = null,
        Func<Reglement, bool>? memConforme = null)
    {
        _options = options;
        _catalogue = catalogue;
        _scoring = scoring;
        _writer = writer;
        _assets = assets;
        _runtimeState = runtimeState;
        _settingsChangeBus = settingsChangeBus;
        _bus = bus;
        _logger = logger;
        _emulateurTourne = emulateurTourne ?? EmulatorForeground.EmulateurTourne;
        _memConforme = memConforme ?? MemConforme;
    }

    /// <summary>Les jeux de la derniere passe : le panneau les lit pour savoir quels defis un jeu propose.</summary>
    public IReadOnlyList<JeuAvecDefis> Jeux { get; private set; } = [];

    /// <summary>Les defis du jeu de ce fichier ; vide s'il n'est pas dans la collection.</summary>
    public IReadOnlyList<Reglement> DefisDuJeu(string? chemin)
    {
        // Juste apres un demarrage, la premiere passe n'a pas encore eu lieu : le panneau ne doit pas se dire vide pour autant.
        var jeux = Jeux.Count > 0 || _scoring.CheminsParJeu is not { } chemins ? Jeux : Croiser(_catalogue.Tous, chemins, _memConforme);
        return DefisDe(jeux, chemin);
    }

    /// <summary>
    /// Les defis de ce fichier. World Scoring garde les chemins de Windows et ES donne des barres obliques : on compare le
    /// fichier, jamais son ecriture.
    /// </summary>
    internal static IReadOnlyList<Reglement> DefisDe(IEnumerable<JeuAvecDefis> jeux, string? chemin)
    {
        var cherche = (chemin ?? "").Replace('\\', '/');
        return jeux.FirstOrDefault(j => string.Equals(j.Chemin.Replace('\\', '/'), cherche, StringComparison.OrdinalIgnoreCase))?.Defis ?? [];
    }

    private bool Visible => _options.CurrentValue.NelfePlay is { Enabled: true, ShowChallengesCollection: true };

    /// <summary>
    /// Le croisement, sans disque ni reseau : un jeu par fichier, ses defis dans l'ordre du catalogue. Un jeu absent de World
    /// Scoring n'entre pas, et un defi dont le .MEM n'est pas celui de la borne n'est pas propose.
    /// </summary>
    internal static IReadOnlyList<JeuAvecDefis> Croiser(IEnumerable<Reglement> defis, IReadOnlyDictionary<string, string> cheminsParJeu, Func<Reglement, bool> memConforme)
    {
        var parChemin = new Dictionary<string, List<Reglement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var defi in defis)
        {
            if (!cheminsParJeu.TryGetValue(defi.SystemId + "/" + defi.RomGroup, out var chemin) || !memConforme(defi)) continue;
            if (!parChemin.TryGetValue(chemin, out var liste)) parChemin[chemin] = liste = new List<Reglement>();
            liste.Add(defi);
        }

        return parChemin.Select(p => new JeuAvecDefis(p.Key, p.Value)).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _derniereVisibilite = Visible;
        // Couper l'option fait disparaitre la collection tout de suite, pas au tic suivant.
        _abonnementReglages = _settingsChangeBus?.Subscribe(async (_, token) =>
        {
            if (Visible == _derniereVisibilite) return;
            _derniereVisibilite = Visible;
            await SynchroniserAsync(token).ConfigureAwait(false);
        });

        // Rien ne bouge pendant une partie : la passe suit donc la fin de partie.
        _abonnementBus = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    await SynchroniserAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }, stoppingToken);
        });

        try
        {
            await Task.Delay(PremierDelai, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SynchroniserAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_scoring.CheminsParJeu is null ? AttenteDeWorldScoring : Periode, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task SynchroniserAsync(CancellationToken ct)
    {
        if (!await _porte.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) return;
        try
        {
            if (!Visible)
            {
                Retirer();
                return;
            }

            // World Scoring n'a pas encore tourne : ne pas savoir n'est pas une liste vide, la collection reste telle quelle.
            if (_emulateurTourne() || _scoring.CheminsParJeu is not { } chemins) return;

            var jeux = Croiser(_catalogue.Tous, chemins, _memConforme);
            var fichiers = jeux.Select(j => j.Chemin).ToList();
            var resultat = _writer.Apply(CollectionName, fichiers);
            var visuel = fichiers.Count > 0 ? _assets.Install(CollectionName, LogoFile, FondFile) : _assets.Remove(CollectionName);
            if (visuel.Changed) PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
            Jeux = jeux;
            if (resultat.Changed || visuel.Changed)
            {
                _logger?.LogInformation("Collection des defis : {Jeux} jeu(x), {Defis} defi(s).", jeux.Count, jeux.Sum(j => j.Defis.Count));
                _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), silencieux: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Collection des defis : passe interrompue.");
        }
        finally
        {
            _porte.Release();
        }
    }

    private void Retirer()
    {
        Jeux = [];
        var resultat = _writer.Remove(CollectionName);
        if (_assets.Remove(CollectionName).Changed) PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
        if (!resultat.Changed) return;
        _logger?.LogInformation("Collection des defis retiree (option coupee).");
        _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), silencieux: true);
    }

    /// <summary>Le meme controle qu'au lancement (<see cref="SpeedrunSessionService.Lancer"/>) : .MEM en vigueur, empreinte et lignes.</summary>
    private static bool MemConforme(Reglement defi)
    {
        var mem = defi.DefinitionAuLancement();
        return mem.Length > 0 && File.Exists(mem) && defi.VerifierContre(File.ReadAllBytes(mem)).Count == 0;
    }

    public override void Dispose()
    {
        _abonnementReglages?.Dispose();
        _abonnementBus?.Dispose();
        base.Dispose();
    }
}
