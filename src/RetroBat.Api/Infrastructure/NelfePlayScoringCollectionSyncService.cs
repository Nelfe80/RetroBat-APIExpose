using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Tient a jour la collection EmulationStation « NELFEPLAY WORLD SCORING » : les jeux que
/// cette borne possede ET sur lesquels un score mondial peut etre enregistre maintenant.
///
/// Trois conditions, trois autorites : NelfePlay dit quels profils sont ouverts, la borne dit
/// ce qu'elle possede et quelle definition de score elle detient, EmulationStation se contente
/// d'afficher. Un jeu dont la definition locale n'a pas l'empreinte homologuee n'entre pas :
/// il promettrait un score qui serait refuse a la fin de la partie.
///
/// Rien ne remonte : la borne telecharge un index public et fait l'intersection chez elle.
/// </summary>
public sealed class NelfePlayScoringCollectionSyncService : BackgroundService
{
    public const string CollectionName = "nelfeplay-scoring";
    public const string HttpClientName = "nelfeplay-open-games";

    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(300);

    /// <summary>
    /// L'ecart minimal entre deux rattrapages du Data Pack declenches par la collection : en pratique
    /// un par passe (toutes les 5 min), tant qu'un .MEM est en retard.
    /// Quinze minutes laissaient une promotion (profil depose, puis .MEM publie) sortir des jeux de
    /// la collection pendant un quart d'heure (2026-09-30). Une synchronisation dont le depot n'a
    /// pas bouge ne coute qu'un appel a GitHub.
    /// </summary>
    internal static readonly TimeSpan RattrapageDataPackMinimum = TimeSpan.FromMinutes(4);

    /// <summary>
    /// LE DELAI DE GRACE D'UN .MEM EN RETARD. Quand le profil d'un jeu epingle un .MEM que la borne
    /// n'a pas encore, c'est presque toujours que le Data Pack le publie dans la minute : le jeu reste
    /// dans la collection ce temps-la, au lieu d'en sortir puis d'y revenir sous les yeux du joueur.
    /// Au-dela, il sort : son score serait refuse.
    /// </summary>
    internal static readonly TimeSpan GraceDefinitionEnRetard = TimeSpan.FromMinutes(30);

    /// <summary>Depuis quand chaque jeu a un .MEM en retard sur son profil (cle systeme/jeu).</summary>
    private readonly Dictionary<string, DateTime> _enRetardDepuis = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _horloge;
    private static readonly JsonSerializerOptions JsonLecture = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions JsonEcriture = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly InstalledGameCatalog _catalog;
    private readonly GamelistUpdateService? _gamelists;
    /// <summary>Les jeux retenus que leur gamelist ne liste pas : ES ne les connait peut-etre pas.</summary>
    private List<(InstalledGame Jeu, string RomGroup)> _aDeclarer = [];
    private readonly EsCustomCollectionWriter _writer;
    private readonly EsCollectionThemeAssets _assets;
    private readonly MediaRuntimeState _runtimeState;
    private readonly EmulationStationSettingsService? _reglages;
    private readonly EmulationStationSystemConfigService? _systemes;
    private readonly IEsSettingsChangeBus? _settingsChangeBus;
    /// <summary>Le bus d'evenements, pour rattraper la synchronisation a la fin d'une partie.</summary>
    private readonly IEventBus? _bus;
    private IDisposable? _abonnementBus;
    private readonly ILogger<NelfePlayScoringCollectionSyncService>? _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private readonly string _stateRoot;
    private readonly TimeSpan _minimumEntreDeuxAppels;
    /// <summary>
    /// « Un emulateur tourne-t-il ? », injectable pour que le test n'interroge pas la machine.
    ///
    /// Cette question se posait directement a Windows, si bien que TOUTE la suite de ce service
    /// echouait des qu'un emulateur tournait sur le poste : la synchro sortait par son statut
    /// initial, qui vaut « error », et douze tests attendaient « empty » (vecu le 2026-09-23,
    /// une partie ouverte pendant le banc). Un test qui depend de ce qui tourne a cote ne garde
    /// plus rien.
    /// </summary>
    private readonly Func<bool> _emulateurTourne;
    private readonly Func<InstalledGame, OpenGame, bool> _contenuConfirme;
    /// <summary>La regle des ROMs admises (11/11) sur un candidat confirme ; le booleen dit « jeu ouvert aux seuls speedruns ».</summary>
    private readonly Func<InstalledGame, OpenGame, bool, RomAdmission.Decision> _admission;
    private readonly DataPackSyncService? _dataPack;
    private DateTime _dernierRattrapageUtc = DateTime.MinValue;
    private int _definitionsPerimees;

    private DateTime _dernierAppelUtc = DateTime.MinValue;
    /// <summary>Les chemins retenus au dernier passage, pour repondre sans relire le disque.</summary>
    private HashSet<string>? _ouverts;
    /// <summary>Le coeur que la borne chargera pour chaque jeu retenu : « double-dragon » → « fbneo ».</summary>
    private readonly Dictionary<string, string> _coeursParJeu = new(StringComparer.OrdinalIgnoreCase);
    private ScoringCollectionStatus _statut = ScoringCollectionStatus.Initial;
    private IDisposable? _abonnementReglages;
    private bool _derniereVisibilite = true;

    /// <summary>La langue de la borne, pour le message d'ES qui suit la mise a jour de la liste.</summary>
    private readonly CabinetLocale? _locale;

    public NelfePlayScoringCollectionSyncService(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ApiExposeOptions> options,
        InstalledGameCatalog catalog,
        EsCustomCollectionWriter writer,
        EsCollectionThemeAssets assets,
        MediaRuntimeState runtimeState,
        EmulationStationSettingsService? reglages = null,
        EmulationStationSystemConfigService? systemes = null,
        IEsSettingsChangeBus? settingsChangeBus = null,
        IEventBus? bus = null,
        ILogger<NelfePlayScoringCollectionSyncService>? logger = null,
        string? stateRoot = null,
        TimeSpan? minimumEntreDeuxAppels = null,
        Func<bool>? emulateurTourne = null,
        Func<InstalledGame, OpenGame, bool>? contenuConfirme = null,
        GamelistUpdateService? gamelists = null,
        DataPackSyncService? dataPack = null,
        Func<DateTime>? horloge = null,
        CabinetLocale? locale = null,
        Func<InstalledGame, OpenGame, bool, RomAdmission.Decision>? admission = null)
    {
        _locale = locale;
        _admission = admission ?? ((candidat, jeu, speedrunSeul) => JugerLeDump(candidat, jeu, speedrunSeul, RomAdmission.ReferentielSurDisque.Borne));
        _horloge = horloge ?? (() => DateTime.UtcNow);
        _gamelists = gamelists;
        _dataPack = dataPack;
        _httpFactory = httpFactory;
        _options = options;
        _catalog = catalog;
        _writer = writer;
        _assets = assets;
        _runtimeState = runtimeState;
        _reglages = reglages;
        _systemes = systemes;
        _settingsChangeBus = settingsChangeBus;
        _bus = bus;
        _logger = logger;
        _stateRoot = stateRoot ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay");
        // Garde-fou du CDC : aucun appel a moins de 30 s d'intervalle.
        _minimumEntreDeuxAppels = minimumEntreDeuxAppels ?? TimeSpan.FromSeconds(30);
        _emulateurTourne = emulateurTourne ?? EmulatorForeground.EmulateurTourne;
        _contenuConfirme = contenuConfirme
            ?? ((candidat, jeu) => ContenuConfirme(candidat, jeu, GamelistIdentity.DeclaredSha1, Md5DuContenu,
                GamelistIdentity.DeclaredBootlegOrHack, c => SansEnTeteDuContenu(c.AbsolutePath, c.FrontendSystemId)));
    }

    /// <summary>Le dernier etat connu, tel que l'endpoint de statut le publie.</summary>
    public ScoringCollectionStatus Status => _statut;

    /// <summary>
    /// Le fichier retenu pour chaque jeu de la derniere passe, par « systeme/jeu » ; null avant la premiere passe. La
    /// collection des defis part de lui : un jeu n'y entre que s'il a passe les controles de World Scoring.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CheminsParJeu { get; private set; }

    private string EtatPath => Path.Combine(_stateRoot, "scoring-collection.json");

    private string ManifestePath => Path.Combine(_stateRoot, "scoring-collection-remote.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _statut = LireEtat() is { } repris ? repris.EnStale() : ScoringCollectionStatus.Initial;
        _derniereVisibilite = _options.CurrentValue.NelfePlay is { Enabled: true, ShowScoringCollection: true };
        // Couper l'option depuis le menu d'EmulationStation doit faire disparaitre la collection
        // tout de suite, pas au bout de cinq minutes.
        _abonnementReglages = _settingsChangeBus?.Subscribe(async (_, token) =>
        {
            var visible = _options.CurrentValue.NelfePlay is { Enabled: true, ShowScoringCollection: true };
            if (visible == _derniereVisibilite)
            {
                return;
            }

            _derniereVisibilite = visible;
            await SynchroniserAsync("option", token);
        });

        // RATTRAPAGE A LA FIN D'UNE PARTIE.
        //
        // La synchronisation ne fait RIEN tant qu'un emulateur tourne, et c'est voulu : recharger
        // les gamelists sous le nez d'un joueur serait pire que d'attendre. Mais si le tic de
        // cinq minutes tombe toujours pendant une partie, l'etat ne se rafraichit jamais : une
        // borne vue le 2026-09-23 etait restee perimee deux heures, parce que son joueur
        // enchainait les parties.
        //
        // On ne change donc pas la regle, on ajoute le moment qui lui manquait : juste apres la
        // partie, quand plus personne ne joue.
        _abonnementBus = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Le temps que l'emulateur soit vraiment parti : sans ce delai, la passe
                    // repartirait aussitot par la porte « un emulateur tourne ».
                    await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken).ConfigureAwait(false);
                    await SynchroniserAsync("fin de partie", stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Collection World Scoring : rattrapage de fin de partie en echec");
                }
            }, stoppingToken);
        });

        try
        {
            await Task.Delay(PremierDelai, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SynchroniserAsync("periodique", stoppingToken);
                await Task.Delay(Periode, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _abonnementReglages?.Dispose();
            _abonnementBus?.Dispose();
        }
    }

    /// <summary>
    /// Une passe complete : index distant, inventaire local, intersection, ecriture. Appelable
    /// aussi a la demande (changement d'options, installation d'un jeu).
    /// </summary>
    public async Task<ScoringCollectionStatus> SynchroniserAsync(string declencheur, CancellationToken cancellationToken)
    {
        if (!await _porte.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            return _statut;
        }

        try
        {
            var nelfeplay = _options.CurrentValue.NelfePlay;
            if (!nelfeplay.Enabled || !nelfeplay.ShowScoringCollection)
            {
                return _statut = Desactiver(nelfeplay.Enabled, nelfeplay.ShowScoringCollection);
            }

            // Pendant une partie, rien ne doit bouger devant le joueur : le reload ES attendra.
            if (_emulateurTourne())
            {
                return _statut;
            }

            var etat = LireEtat();
            var index = await TelechargerAsync(etat?.RemoteEtag, cancellationToken);
            if (index.Erreur is { Length: > 0 })
            {
                _logger?.LogWarning("Collection World Scoring : {Erreur} (declencheur {Declencheur})", index.Erreur, declencheur);
                return _statut = EcrireEtat((etat ?? ScoringCollectionState.Vide) with
                {
                    LastAttemptUtc = DateTime.UtcNow,
                    LastError = index.Erreur,
                }, stale: true);
            }

            // 304 : l'index distant n'a pas change, mais l'inventaire local, si. On rejoue
            // l'intersection sur le manifeste garde en local, sinon un redemarrage laisserait
            // la borne sans liste.
            var manifeste = index.Manifeste ?? LireManifeste();
            if (manifeste == null)
            {
                return _statut = EcrireEtat((etat ?? ScoringCollectionState.Vide) with
                {
                    LastAttemptUtc = DateTime.UtcNow,
                    LastError = "aucun index distant connu",
                }, stale: true);
            }

            if (index.Manifeste != null)
            {
                EcrireManifeste(manifeste);
            }

            var chemins = Intersecter(manifeste, out var candidats);
            if (await RattraperLeDataPackAsync(declencheur, cancellationToken))
            {
                chemins = Intersecter(manifeste, out candidats);
            }

            var inscrits = InscrireLesJeuxInconnusDEs();
            _ouverts = chemins.Select(Normaliser).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // La borne dit a la plateforme avec quoi elle lancera ces jeux : la fiche d'un jeu
            // peut alors repondre « votre borne lancera FBNeo » au lieu de rester generale.
            CabinetState.NoterCoeurs(_coeursParJeu);
            var resultat = _writer.Apply(CollectionName, chemins);
            // L'identite visuelle suit la collection : la declarer dans le theme est ce qui lui
            // donne sa propre tuile au lieu de la ranger dans le fourre-tout « collections ».
            var visuel = chemins.Count > 0 ? _assets.Install(CollectionName) : _assets.Remove(CollectionName);
            if (visuel.Changed)
            {
                PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
            }

            if (resultat.Changed || visuel.Changed || inscrits > 0)
            {
                _logger?.LogInformation(
                    "Collection World Scoring : {Entrees} entrees sur {Distants} jeux ouverts et {Candidats} candidats locaux, revision {Revision}",
                    chemins.Count, manifeste.Games.Count, candidats, manifeste.Revision);
                // Une collection qui apparait, change ou disparait est un nouveau systeme pour ES :
                // seul un rechargement des gamelists la fait exister. Silencieux : le joueur n'a
                // rien commande, et ES annonce la liste a jour une fois rechargee, par sa propre
                // notification (demande user 2026-10-10, a la place de la barre de progression).
                _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8),
                    silencieux: true, annonce: chemins.Count > 0 ? AnnonceDeLaListe(chemins.Count) : null);
            }

            var maintenant = DateTime.UtcNow;
            return _statut = EcrireEtat(new ScoringCollectionState
            {
                RemoteRevision = manifeste.Revision,
                RemoteEtag = index.Etag ?? etat?.RemoteEtag,
                ManagedFile = Path.GetFileName(_writer.ConfigPath(CollectionName)),
                CollectionSha256 = _writer.ReadState(CollectionName)?.ContentSha256,
                RemoteGames = manifeste.Games.Count,
                LocalReadyGames = chemins.Count,
                Missing = [.. _manques],
                Advice = [.. _conseils],
                LastSuccessUtc = maintenant,
                LastAttemptUtc = maintenant,
                LastError = null,
            }, stale: false);
        }
        catch (OperationCanceledException)
        {
            return _statut;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Collection World Scoring : synchronisation interrompue");
            return _statut = EcrireEtat((LireEtat() ?? ScoringCollectionState.Vide) with
            {
                LastAttemptUtc = DateTime.UtcNow,
                LastError = ex.GetType().Name,
            }, stale: true);
        }
        finally
        {
            _porte.Release();
        }
    }

    /// <summary>
    /// UNE DEFINITION PERIMEE SE RATTRAPE TOUT DE SUITE (2026-09-28).
    ///
    /// Un .MEM corrige part dans le Data Pack avec un nouveau profil qui epingle sa nouvelle
    /// empreinte. Or une borne ne relit le Data Pack qu'a son demarrage, puis toutes les 24 h :
    /// entre les deux, sa copie n'a plus l'empreinte exigee. 1942 est ainsi sorti de la collection
    /// d'un joueur en pleine soiree, avec « memoire non certifiable » au lancement, juste apres
    /// la correction de son score a huit chiffres.
    ///
    /// La passe qui constate l'ecart demande donc le Data Pack tout de suite, puis refait
    /// l'intersection AVANT d'ecrire la collection : le jeu n'en sort pas. Au plus une fois par
    /// quart d'heure, pour qu'un profil depose avant son .MEM ne fasse pas marteler le depot.
    /// </summary>
    private async Task<bool> RattraperLeDataPackAsync(string declencheur, CancellationToken cancellationToken)
    {
        if (_definitionsPerimees == 0 || _dataPack is null || !_options.CurrentValue.DataPack.Enabled)
        {
            return false;
        }

        var maintenant = DateTime.UtcNow;
        if (!RattrapageAutorise(_dernierRattrapageUtc, maintenant))
        {
            return false;
        }

        _dernierRattrapageUtc = maintenant;
        _logger?.LogInformation(
            "Collection World Scoring : {Nombre} definition(s) sans l'empreinte du profil, synchronisation du Data Pack ({Declencheur})",
            _definitionsPerimees, declencheur);
        try
        {
            var bilan = await _dataPack.SyncNowAsync(cancellationToken);
            return bilan.Added + bilan.Updated > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Collection World Scoring : rattrapage du Data Pack en echec");
            return false;
        }
    }

    /// <summary>Le message d'ES qui suit la mise a jour de la liste, dans la langue de la borne.</summary>
    internal string AnnonceDeLaListe(int jeux)
        => string.Format(
            CabinetAnnounceText.Get(jeux == 1 ? "world_scoring_updated_one" : "world_scoring_updated", _locale?.Langue ?? "en"),
            jeux);

    internal static bool RattrapageAutorise(DateTime dernierUtc, DateTime maintenantUtc)
        => maintenantUtc - dernierUtc >= RattrapageDataPackMinimum;

    /// <summary>Vrai tant que le .MEM d'un jeu est en retard depuis moins que le delai de grace.</summary>
    internal static bool EnGrace(DateTime depuisUtc, DateTime maintenantUtc)
        => maintenantUtc - depuisUtc < GraceDefinitionEnRetard;

    /// <summary>
    /// Ce jeu est-il OUVERT AU SCORING sur cette borne ? C'est-a-dire : un profil est ouvert
    /// pour lui, la borne en a le dump, et sa definition locale porte l'empreinte homologuee.
    /// La reponse est la collection elle-meme, qui est exactement cette intersection.
    ///
    /// Rend null quand la borne n'a AUCUNE liste - collection eteinte, ou index jamais
    /// telecharge. On ne sait alors pas, et ne pas savoir ne doit pas tout fermer : mieux vaut
    /// le comportement d'avant qu'un panneau qui ne s'ouvre nulle part apres un demarrage hors
    /// ligne.
    /// </summary>
    /// <summary>
    /// Les regles ouvertes d'un jeu (« 1cc », « 1cc-multi », « 1lc »...), lues dans le dernier index
    /// des jeux ouverts garde sur disque ; vide si le jeu n'y est pas. Le panneau de classement s'en
    /// sert : le libelle du defi suit la regle du jeu, et le 1CC MULTI n'est propose que la ou il a
    /// un classement.
    /// </summary>
    /// <summary>Les systemes des jeux ouverts au scoring, lus dans le dernier index garde sur disque.</summary>
    public IReadOnlyList<string> SystemesOuverts()
    {
        try
        {
            return LireManifeste()?.Games?
                .Select(jeu => jeu.SystemId)
                .Where(systeme => !string.IsNullOrWhiteSpace(systeme))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public IReadOnlyList<string> ReglesOuvertes(string romGroup)
    {
        if (string.IsNullOrWhiteSpace(romGroup)) return [];
        try
        {
            var manifeste = LireManifeste();
            var jeu = manifeste?.Games?.FirstOrDefault(g => string.Equals(g.RomGroup, romGroup, StringComparison.OrdinalIgnoreCase));
            return jeu is null ? [] : jeu.Rulesets.Select(r => r.Id).Where(id => id.Length > 0).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public bool? EstOuvertAuScoring(string cheminDuJeu)
    {
        if (string.IsNullOrWhiteSpace(cheminDuJeu))
        {
            return null;
        }

        // Le disque garde la liste entre deux demarrages : sans cette relecture, le panneau
        // s'ouvrirait partout pendant les premieres secondes qui suivent un lancement.
        var ouverts = _ouverts ??= LireCollectionPrecedente()
            .Select(Normaliser)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ouverts.Count == 0)
        {
            return null;
        }

        // UN CHEMIN RELATIF NE SE COMPARE A RIEN. EmulationStation donne d'ordinaire un chemin
        // absolu, mais s'il en donnait un relatif, le resoudre depuis le dossier de l'API
        // designerait un fichier qui n'existe pas - et le panneau ne s'ouvrirait plus nulle
        // part. On prefere ne pas savoir : meme regle qu'une borne sans liste.
        var cible = Normaliser(cheminDuJeu);
        return Path.IsPathRooted(cible) ? ouverts.Contains(cible) : null;
    }

    /// <summary>
    /// Un chemin comparable : separateurs unifies, sans espaces autour, et ramene a sa forme
    /// complete quand on le peut - « roms/fbneo/../fbneo/19xx.zip » et « roms/fbneo/19xx.zip »
    /// designent le meme fichier.
    /// </summary>
    private static string Normaliser(string chemin)
    {
        var propre = chemin.Trim().Replace('\\', '/');
        if (!Path.IsPathRooted(propre))
        {
            return propre;
        }

        try
        {
            return Path.GetFullPath(propre).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return propre;
        }
    }

    private ScoringCollectionStatus Desactiver(bool enabled, bool visible)
    {
        _ouverts = null;
        var resultat = _writer.Remove(CollectionName);
        if (_assets.Remove(CollectionName).Changed)
        {
            PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
        }

        if (resultat.Changed)
        {
            _logger?.LogInformation("Collection World Scoring retiree (option coupee)");
            // Une collection qui apparait, change ou disparait est un nouveau systeme pour ES :
                // seul un rechargement des gamelists la fait exister. Silencieux : le joueur n'a
                // rien commande.
                _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), silencieux: true);
        }

        try
        {
            if (File.Exists(EtatPath))
            {
                File.Delete(EtatPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new ScoringCollectionStatus(enabled, visible, "disabled", null, 0, 0, null, false, null);
    }

    /// <summary>
    /// Croise l'index distant avec l'inventaire local. Un jeu n'est retenu que si sa
    /// definition officielle locale porte exactement l'empreinte publiee par le profil.
    /// </summary>
    private List<string> Intersecter(OpenGamesManifest manifeste, out int candidatsLocaux)
    {
        var systemes = manifeste.Games.Concat(manifeste.SpeedrunGames)
            .Select(jeu => jeu.SystemId)
            .Where(systeme => !string.IsNullOrWhiteSpace(systeme))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var installes = _catalog.Enumerate(systemes);
        candidatsLocaux = installes.Count(jeu => jeu.ScorableLocal);

        var parGroupe = installes
            .Where(jeu => jeu.ScorableLocal)
            .GroupBy(jeu => jeu.CanonicalSystemId + "/" + jeu.RomGroup, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.ToList(), StringComparer.OrdinalIgnoreCase);

        var empreintes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perimees = 0;
        var precedents = LireCollectionPrecedente();
        var retenus = new List<string>();
        var aDeclarer = new List<(InstalledGame Jeu, string RomGroup)>();
        var cheminsParJeu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _coeursParJeu.Clear();
        // POURQUOI CHAQUE JEU MANQUANT MANQUE.
        //
        // « 2 jeux sur 6 » ne dit pas lesquels ni pourquoi, et la raison n'existait qu'en
        // journal de debogage : un exploitant voyait le chiffre sans pouvoir agir (2026-09-23).
        // Trois conditions, trois manques possibles, et ils appellent trois gestes differents.
        var manques = new List<string>();
        // (11/11, option A) Ce que l'API conseille sans bloquer : la version propre d'un jeu retenu sur une version deja
        // certifiee que le referentiel marque mauvais dump (« Super Mario Bros (JU) (PRG 1) » de Lorenzolamas).
        var conseils = new List<string>();

        var speedrunsSeuls = manifeste.SpeedrunGames.Select(j => j.SystemId + "/" + j.RomGroup).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var jeu in manifeste.Games.Concat(manifeste.SpeedrunGames.Where(j => !manifeste.Games.Any(g =>
                     string.Equals(g.SystemId + "/" + g.RomGroup, j.SystemId + "/" + j.RomGroup, StringComparison.OrdinalIgnoreCase)))))
        {
            var cle = jeu.SystemId + "/" + jeu.RomGroup;
            // Un jeu ouvert aux seuls speedruns a un .MEM sans score : l'inventaire ne lui donne pas de groupe (05/11, Super
            // Metroid « ROM absente » alors que la borne l'a). Ses dumps se reconnaissent par leur .MEM.
            if ((!parGroupe.TryGetValue(cle, out var candidats) || candidats.Count == 0) && speedrunsSeuls.Contains(cle))
                candidats = [.. _catalog.DumpsPourLesSpeedruns(jeu.SystemId, jeu.RomGroup)];
            if (candidats is null || candidats.Count == 0)
            {
                manques.Add(jeu.RomGroup + " : ROM absente");
                continue;
            }

            if (!empreintes.TryGetValue(cle, out var locale))
            {
                locale = _catalog.OfficialDefinitionSha256(jeu.SystemId, jeu.RomGroup);
                empreintes[cle] = locale;
            }

            if (locale.Length == 0)
            {
                _logger?.LogDebug("World Scoring : {Cle} sans definition officielle locale", cle);
                manques.Add(jeu.RomGroup + " : définition de score absente");
                continue;
            }

            if (!string.Equals(locale, jeu.MemSha256, StringComparison.OrdinalIgnoreCase))
            {
                // La borne a bien un .MEM, mais pas celui qu'exige le profil : le score
                // serait refuse. La passe met le Data Pack a jour elle-meme
                // (RattraperLeDataPackAsync) ; pendant le delai de grace, le jeu reste.
                _logger?.LogDebug("World Scoring : {Cle} en definition non homologuee", cle);
                perimees++;
                var depuis = _enRetardDepuis.TryGetValue(cle, out var vu) ? vu : (_enRetardDepuis[cle] = _horloge());
                if (!EnGrace(depuis, _horloge()))
                {
                    manques.Add(jeu.RomGroup + " : définition non homologuée (Data Pack à mettre à jour)");
                    continue;
                }
                manques.Add(jeu.RomGroup + " : définition en cours de mise à jour par le Data Pack (le jeu reste dans la collection)");
            }
            else
            {
                _enRetardDepuis.Remove(cle);
            }

            // LE CONTENU D'ABORD, LE CHOIX ENSUITE.
            //
            // Les candidats sont regroupes par NOM, et deux jeux peuvent porter le meme : le Double
            // Dragon Neo-Geo (doubledr, un jeu de combat de 1995) et celui de Technos (ddragon, le
            // beat'em up ouvert au scoring) donnent tous deux « double-dragon ». Le hash ne servait
            // qu'a CLASSER les candidats : sans dump reconnu, le plus proche du groupe entrait quand
            // meme, et un fichier deja choisi restait choisi. Un joueur a vu « Partie certifiable »
            // sur un Double Dragon, et rien n'est parti (2026-09-25).
            //
            // Un fichier n'est desormais candidat que si son contenu est CONFIRME. La collection se
            // reconstruit ainsi d'elle-meme a chaque passe : ce qui n'est pas confirme en sort.
            var confirmes = candidats.Where(candidat => _contenuConfirme(candidat, jeu)).ToList();
            // (10/11) un set d'arcade d'exception que le .MEM nomme, hors gamelist et hors nom du jeu (DaiOuJou Black Label)
            if (confirmes.Count == 0 && string.Equals(jeu.SystemId, "arcade", StringComparison.OrdinalIgnoreCase))
                confirmes = _catalog.DumpsNommesParLeMem(jeu.SystemId, jeu.RomGroup).Where(candidat => _contenuConfirme(candidat, jeu)).ToList();
            if (confirmes.Count == 0)
            {
                manques.Add(jeu.RomGroup + " : ROM non reconnue (ce n'est pas la version classée)"
                    + (RomAttendue(jeu, speedrunsSeuls.Contains(cle)) is { Length: > 0 } attendue ? $". ROM attendue : « {attendue} »" : ""));
                continue;
            }

            // LES ROMS ADMISES (11/11, rapport de Gogerad) : un contenu publie par le site n'est pas forcement un dump propre.
            // Les `content_hashes` d'un jeu console rangent sous lui les hacks, pirates et traductions du meme groupe du
            // referentiel (« Super Mario Bros. (Asia) (En) (Pirate) », un hack de couleurs de Streets of Rage 2) : ils sortent ici.
            var verdicts = confirmes.ToDictionary(c => c, c => _admission(c, jeu, speedrunsSeuls.Contains(cle)));
            var admis = confirmes.Where(c => verdicts[c].Admise).ToList();
            foreach (var exclu in confirmes.Where(c => !verdicts[c].Admise))
                _logger?.LogInformation("World Scoring : {Fichier} ecarte de {Cle} : {Raison}", exclu.AbsolutePath, cle, verdicts[exclu].Message);
            if (admis.Count == 0)
            {
                manques.Add(jeu.RomGroup + " : " + verdicts[confirmes[0]].Message);
                continue;
            }
            confirmes = admis;

            // CE QUI SE MESURE, ET RIEN D'AUTRE. Un dump reconnu lance par un coeur qui ne lit rien
            // (MAME 2003-Plus choisi en automatique pour 19xx, 2026-09-26) promettait une partie
            // classee et n'envoyait aucun score. Depuis, World Scoring impose son coeur fonctionnel
            // quand le lancement du joueur ne mesure pas (WorldScoringLancementService) : n'est
            // ecarte que le jeu dont le systeme ne declare aucun coeur qui mesure.
            var mesurables = confirmes.Where(candidat => Observabilite(candidat).Observable || Imposable(candidat)).ToList();
            if (mesurables.Count == 0)
            {
                manques.Add($"{jeu.RomGroup} : aucun cœur qui mesure n'est déclaré pour le système {confirmes[0].FrontendSystemId}");
                continue;
            }

            var choisi = Choisir(mesurables, jeu, precedents, Lancement, c => RomAdmission.Rang(verdicts[c]));
            if (choisi is { Length: > 0 })
            {
                // Un jeu ouvert aux seuls speedruns : sa ROM pour les epreuves, pas d'entree au World Scoring.
                if (!speedrunsSeuls.Contains(cle) || manifeste.Games.Any(g => string.Equals(g.SystemId + "/" + g.RomGroup, cle, StringComparison.OrdinalIgnoreCase)))
                    retenus.Add(choisi);
                var retenu = mesurables.FirstOrDefault(candidat =>
                    string.Equals(candidat.AbsolutePath, choisi, StringComparison.OrdinalIgnoreCase));
                if (retenu is not null && verdicts[retenu].Conseil is { Length: > 0 } conseil)
                {
                    conseils.Add(jeu.RomGroup + " : " + conseil);
                    _logger?.LogInformation("World Scoring : {Fichier} retenu pour {Cle} : {Raison} ; {Conseil}", retenu.AbsolutePath, cle,
                        verdicts[retenu].Raison, conseil);
                }
                // Les defis (collection Challenges & Speedruns) partent de ce chemin. Un jeu qui a des speedruns (coeur hote des
                // fantomes, etats de depart) n'y entre qu'avec SA ROM de reference : un etat de depart charge sur une autre
                // version (Super Mario World (Europe) (Rev 1) chez Gogerad, sur des etats faits sur la version USA) n'est pas la
                // meme epreuve.
                if (retenu is null || speedrunsSeuls.Contains(cle) || !AUnSpeedrun(jeu)
                    || _admission(retenu, jeu, true).Statut == RomAdmission.Statut.Reference)
                    cheminsParJeu[jeu.SystemId + "/" + jeu.RomGroup] = choisi;
                else
                    manques.Add(jeu.RomGroup + " : speedruns indisponibles avec ce fichier. " + _admission(retenu, jeu, true).Message);
                if (retenu is { FromGamelist: false })
                {
                    aDeclarer.Add((retenu, jeu.RomGroup));
                }
                var coeur = retenu == null ? null : Lancement(retenu.FrontendSystemId)?.Core;
                if (!string.IsNullOrWhiteSpace(coeur))
                {
                    _coeursParJeu[jeu.RomGroup] = coeur;
                }
            }
        }

        _manques = manques;
        _conseils = conseils;
        _aDeclarer = aDeclarer;
        _definitionsPerimees = perimees;
        CheminsParJeu = cheminsParJeu;

        return retenus;
    }

    /// <summary>
    /// UN JEU DE LA COLLECTION DOIT EXISTER POUR EMULATIONSTATION.
    ///
    /// ES n'affiche dans une collection que les jeux qu'il a charges dans leur systeme. Avec
    /// ParseGamelistOnly=true dans es_settings, il ne lit plus les dossiers de ROMs : un jeu que le
    /// joueur vient d'ajouter manquait a World Scoring alors que notre fichier le listait, et ne
    /// revenait qu'en remettant un es_settings par defaut (rapport testeur, 2026-09-26). La
    /// collection ne depend plus de ce reglage : un jeu retenu que sa gamelist ne nomme pas y est
    /// inscrit, et le rechargement qui suit le fait connaitre a ES.
    /// </summary>
    private int InscrireLesJeuxInconnusDEs()
    {
        if (_gamelists == null || _aDeclarer.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var systeme in _aDeclarer.GroupBy(entree => entree.Jeu.FrontendSystemId, StringComparer.OrdinalIgnoreCase))
        {
            var jeux = systeme
                .Select(entree => (entree.Jeu.AbsolutePath,
                    GamelistIdentity.DeclaredName(systeme.Key, entree.RomGroup, Path.GetFileNameWithoutExtension(entree.Jeu.AbsolutePath))
                        ?? entree.Jeu.DisplayName))
                .ToList();
            try
            {
                total += _gamelists.DeclarerJeuxAbsents(systeme.Key, jeux);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                _logger?.LogWarning(ex, "Collection World Scoring : gamelist de {Systeme} non completee", systeme.Key);
            }
        }

        if (total > 0)
        {
            _logger?.LogInformation(
                "Collection World Scoring : {Nombre} jeu(x) inscrit(s) dans leur gamelist pour qu'EmulationStation les charge",
                total);
        }

        return total;
    }

    /// <summary>Pourquoi chaque jeu ouvert n'est pas dans la collection de cette borne.</summary>
    private IReadOnlyList<string> _manques = [];
    private IReadOnlyList<string> _conseils = [];

    /// <summary>Un coeur qui mesure peut-il etre impose a ce jeu dans World Scoring ?</summary>
    private bool Imposable(InstalledGame candidat)
        => _systemes == null ||
           CoeursObservables.MeilleurLancement(_systemes.GetEmulatorCores(candidat.FrontendSystemId)) is not null;

    /// <summary>
    /// Le coeur qui lancera ce fichier mesure-t-il ? Le choix du jeu passe devant celui du systeme.
    /// Sans services (tests), on ne retire rien.
    /// </summary>
    private CoeursObservables.Verdict Observabilite(InstalledGame candidat)
    {
        if (_systemes == null || _reglages == null)
        {
            return new CoeursObservables.Verdict(true, null);
        }

        try
        {
            var (lancement, coeurChoisi) = _systemes.ResolveGameLaunchConfig(
                candidat.FrontendSystemId,
                candidat.Emulator,
                candidat.Core,
                _reglages.GetAllSettings());
            return CoeursObservables.Juger(lancement, coeurChoisi, _systemes.GetEmulatorCores(candidat.FrontendSystemId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CoeursObservables.Verdict(true, null);
        }
    }

    /// <summary>
    /// Ce que CETTE borne lancerait pour ce systeme d'EmulationStation : son reglage
    /// `&lt;systeme&gt;.emulator` / `.core` s'il en a un, sinon le premier coeur declare dans
    /// son es_systems.cfg. Null quand les services ne sont pas la (tests).
    /// </summary>
    private EmulationStationLaunchConfig? Lancement(string systemeFrontal)
    {
        if (_systemes == null || _reglages == null || string.IsNullOrWhiteSpace(systemeFrontal))
        {
            return null;
        }

        try
        {
            return _systemes.ResolveLaunchConfig(systemeFrontal, _reglages.GetAllSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Une configuration illisible ne doit pas vider la collection : sans rang, tous les
            // dumps redeviennent egaux et le tri d'avant tranche.
            return null;
        }
    }

    /// <summary>
    /// CE QUI SE REJOUE PASSE DEVANT. Un score mesure hors RetroArch est un vrai score, mais il
    /// n'aura jamais de replay : l'enregistrement se pilote par les commandes reseau de
    /// RetroArch, et MAME autonome ne les a pas. Quand la borne possede le meme jeu sous
    /// plusieurs systemes, la collection prend donc celui qui se lancera sous RetroArch, et
    /// FBNeo avant MAME pour l'arcade (regle user 2026-09-23).
    ///
    /// 0 : libretro FBNeo — 1 : un autre coeur libretro (MAME sous RetroArch, Genesis Plus GX)
    /// — 2 : un emulateur autonome, qui ne rend pas de replay.
    /// </summary>
    internal static int RangDeLancement(EmulationStationLaunchConfig? lancement)
    {
        if (lancement == null || string.IsNullOrWhiteSpace(lancement.Emulator))
        {
            return 1;   // on ne sait pas : ni favori ni penalise
        }

        if (!string.Equals(lancement.Emulator, "libretro", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var coeur = lancement.Core ?? string.Empty;
        return coeur.StartsWith("fbneo", StringComparison.OrdinalIgnoreCase)
            || coeur.StartsWith("fbalpha", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    /// <summary>
    /// Plusieurs dumps du meme jeu peuvent etre installes : on garde le chemin deja choisi, ou
    /// celui dont le hash est reconnu par le profil, sinon le plus proche du groupe. Ne recoit
    /// que des candidats au contenu CONFIRME (voir <see cref="ContenuConfirme"/>) : ce classement
    /// departage des dumps valables, il ne fait plus entrer un jeu.
    ///
    /// Avant tout cela vient le RANG DE LANCEMENT : entre un dump qui se rejouera et un dump
    /// qui ne se rejouera pas, il n'y a pas d'hesitation a avoir, et cela vaut aussi contre un
    /// choix deja pose - une borne epinglee sur MAME autonome passe a FBNeo d'elle-meme.
    /// </summary>
    internal static string? Choisir(
        List<InstalledGame> candidats,
        OpenGame jeu,
        HashSet<string> precedents,
        Func<string, EmulationStationLaunchConfig?>? lancement = null,
        Func<InstalledGame, int>? rangDAdmission = null)
    {
        if (candidats.Count == 0)
        {
            return null;
        }

        // LA REFERENCE D'ABORD (11/11) : la version de reference, puis une version admise, puis une version PAL. Avant meme le
        // fichier deja choisi, qui gardait sinon un dump de second rang (le pirate de Super Mario Bros. chez Gogerad) apres
        // l'arrivee de la reference sur le disque.
        if (rangDAdmission != null)
        {
            var rangs = candidats.ToDictionary(candidat => candidat, rangDAdmission);
            var meilleur = rangs.Values.Min();
            candidats = candidats.Where(candidat => rangs[candidat] == meilleur).ToList();
        }

        if (lancement != null)
        {
            var rangs = candidats.ToDictionary(
                candidat => candidat,
                candidat => RangDeLancement(lancement(candidat.FrontendSystemId)));
            var meilleur = rangs.Values.Min();
            candidats = candidats.Where(candidat => rangs[candidat] == meilleur).ToList();
        }

        var dejaChoisi = candidats.FirstOrDefault(candidat =>
            precedents.Contains(candidat.AbsolutePath.Replace('\\', '/')));
        if (dejaChoisi != null)
        {
            return dejaChoisi.AbsolutePath;
        }

        var reconnus = jeu.ContentHashes ?? new ContentHashes();
        var connus = new HashSet<string>(
            (reconnus.Md5 ?? []).Concat(reconnus.Sha1 ?? []).Concat(reconnus.Sha256 ?? []).Concat(reconnus.Rhash ?? []),
            StringComparer.OrdinalIgnoreCase);

        return candidats
            .OrderByDescending(candidat => connus.Count > 0 &&
                ((candidat.Md5 is { Length: > 0 } md5 && connus.Contains(md5)) ||
                 (candidat.CheevosHash is { Length: > 0 } ra && connus.Contains(ra))))
            .ThenByDescending(candidat => string.Equals(
                Path.GetFileNameWithoutExtension(candidat.AbsolutePath), jeu.RomGroup, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidat => candidat.Md5 is { Length: > 0 } || candidat.CheevosHash is { Length: > 0 })
            .ThenBy(candidat => candidat.AbsolutePath, StringComparer.OrdinalIgnoreCase)
            .First()
            .AbsolutePath;
    }

    /// <summary>
    /// Le contenu de ce fichier est-il celui que le profil a homologue ?
    ///
    /// - Un md5 ou un hash RetroAchievements egal a ceux du profil suffit (consoles, et zip
    ///   d'arcade identique a celui du profil).
    /// - ARCADE : le md5 d'un zip ne prouve rien, il change avec l'outil qui l'a fabrique (trois
    ///   md5 differents pour 1942 chez six joueurs classes). La preuve est le SET : son sha1 declare
    ///   au referentiel doit etre celui du profil, et l'emulateur verifie lui-meme le contenu du set
    ///   contre sa base au chargement. ddragon porte le sha1 de Double Dragon, doubledr celui du
    ///   BIOS Neo-Geo : le second n'entre jamais, meme sous le meme nom.
    /// - Une console sans hash dans sa gamelist (jeu jamais scrape) : la borne calcule le md5 du
    ///   fichier, ou du fichier unique d'un zip, plutot que de l'exclure a tort.
    /// - LES BOOTLEGS ET LES HACKS (2026-10-18, durci le 11/11) : un set que le referentiel marque ainsi
    ///   n'entre jamais. mspacmab, bootleg de Ms. Pac-Man, porte le meme sha1 que mspacman et entrait
    ///   dans la collection, alors que sa memoire ne suit pas la definition.
    /// - LE SET NOMME (11/11) : un set d'arcade n'entre que si le .MEM le nomme (md5 de son nom de set
    ///   publie ou ecrit au .MEM) : le parent classe, ou un clone que le .MEM a prouve.
    /// - CONSOLE : ce contenu confirme passe ENSUITE la regle des ROMs admises (<see cref="RomAdmission"/>).
    /// </summary>
    internal static bool ContenuConfirme(
        InstalledGame candidat,
        OpenGame jeu,
        Func<string?, string?, string?, string?> sha1DuSet,
        Func<string, string?> md5DuContenu,
        Func<string?, string?, bool>? bootlegOuHack = null,
        Func<InstalledGame, string?>? sansEnTeteDuContenu = null,
        Func<string, string?>? md5DuFichier = null,
        Func<string, bool>? cloneOfficiel = null)
    {
        var hashes = jeu.ContentHashes;
        if (hashes is null) return false;
        var sha1s = new HashSet<string>((hashes.Sha1 ?? []).Where(h => h.Length > 0), StringComparer.OrdinalIgnoreCase);
        var connus = new HashSet<string>(
            (hashes.Md5 ?? []).Concat(hashes.Sha1 ?? []).Concat(hashes.Sha256 ?? []).Where(h => h.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var arcade = string.Equals(candidat.CanonicalSystemId, "arcade", StringComparison.OrdinalIgnoreCase);
        var sansEnTete = arcade ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : EmpreintesSansEnTete(hashes);
        if (connus.Count == 0 && sansEnTete.Count == 0) return false;

        if (arcade)
        {
            // L'ARCADE (11/11, regle des ROMs admises) : le SET que le .MEM NOMME, jamais un bootleg ni un hack. Nomme = le md5 de
            // son nom de set publie (l'empreinte arcade des .MEM, « mspacman.zip » = md5 de « mspacman ») ou ecrit au .MEM, ou le
            // zip lui-meme nomme par le .MEM (DaiOuJou Black Label : « ddpdojblka.zip », md5 du fichier). Le sha1 declare d'un
            // set ne suffit plus : c'est souvent celui d'un fichier partage (le BIOS Neo-Geo de 285 sets, la PROM de 118 sets
            // System 16), si bien que 19xxu, mslug3h, doubledr ou pc_ddrgn (PlayChoice-10) entraient sous le jeu classe.
            var set = Path.GetFileNameWithoutExtension(candidat.AbsolutePath);
            if (set.Length == 0 || bootlegOuHack?.Invoke(candidat.CanonicalSystemId, set) == true) return false;
            var duMem = RomAdmission.DumpsDuMem(candidat.OfficialDefinitionPath);
            var nomDuSet = Md5DuNomDeSet(set);
            // (10/11) LE FICHIER LUI-MEME : le zip qui a servi a ouvrir un jeu d'exception, absent de la gamelist et du
            // referentiel. Il ne vaut que si le .MEM le nomme (avant le 11/11 : tout md5 publie, y compris un zip appris d'une
            // partie sur un clone).
            if ((md5DuFichier ?? Md5DuFichier)(candidat.AbsolutePath) is { Length: > 0 } duZip && connus.Contains(duZip)
                && (duMem.Count == 0 || duMem.ContainsKey(duZip))) return true;
            if (!connus.Contains(nomDuSet) && !duMem.ContainsKey(nomDuSet))
            {
                // (11/11, decision du user) le clone OFFICIEL d'un set nomme, du meme titre (19xxu, tokiu, mslug3h) : la famille
                // vient du referentiel, l'emulateur verifie le romset, le site juge le sha1 du set par son groupe canonique.
                return (cloneOfficiel ?? (s => RomAdmission.JugerCloneArcade(s, n => SetNommeParLeMem(n, connus, duMem),
                    RomAdmission.ReferentielArcadeSurDisque.Borne).Admise))(set);
            }
            if (candidat.Md5 is { Length: > 0 } md5Zip && connus.Contains(md5Zip)) return true;
            var sha1 = sha1DuSet(candidat.CanonicalSystemId, jeu.RomGroup, set);
            return sha1 is { Length: > 0 } && sha1s.Contains(sha1);
        }

        if (candidat.Md5 is { Length: > 0 } md5 && connus.Contains(md5)) return true;
        // Le hash RetroAchievements de la gamelist EST l'empreinte sans en-tete (rcheevos).
        if (candidat.CheevosHash is { Length: > 0 } ra && (connus.Contains(ra) || sansEnTete.Contains(ra))) return true;

        // L'empreinte sans en-tete d'abord : elle se calcule dans la meme lecture que le md5, que l'appel suivant trouve en cache.
        var mesureeSansEnTete = sansEnTete.Count > 0 ? sansEnTeteDuContenu?.Invoke(candidat) : null;
        var calcule = md5DuContenu(candidat.AbsolutePath);
        if (calcule is { Length: > 0 } && connus.Contains(calcule)) return true;
        return mesureeSansEnTete is { Length: > 0 } && sansEnTete.Contains(mesureeSansEnTete);
    }

    /// <summary>
    /// LA REGLE DES ROMS ADMISES SUR UN CANDIDAT CONFIRME (11/11). Arcade : la regle du set est deja passee dans
    /// <see cref="ContenuConfirme"/>, le set est sa propre reference. Console : <see cref="RomAdmission.JugerConsole"/> sur
    /// tout ce qui est connu du fichier (md5 de la gamelist, md5 mesure, md5 sans en-tete, hash RetroAchievements).
    /// <paramref name="speedrun"/> : seule la ROM des etats de depart passe.
    /// </summary>
    internal static RomAdmission.Decision JugerLeDump(InstalledGame candidat, OpenGame jeu, bool speedrun, RomAdmission.IReferentiel referentiel,
        Func<string, string?>? md5DuContenu = null, Func<InstalledGame, string?>? sansEnTete = null, string? racineEtats = null)
    {
        if (string.Equals(candidat.CanonicalSystemId, "arcade", StringComparison.OrdinalIgnoreCase))
            return new RomAdmission.Decision(RomAdmission.Statut.Reference, "set nommé par la définition de score",
                Path.GetFileNameWithoutExtension(candidat.AbsolutePath), null);
        var systeme = candidat.CanonicalSystemId;
        var duMem = RomAdmission.DumpsDuMem(candidat.OfficialDefinitionPath);
        var empreintes = new[]
        {
            candidat.Md5, candidat.CheevosHash,
            (md5DuContenu ?? Md5DuContenu)(candidat.AbsolutePath),
            (sansEnTete ?? (c => SansEnTeteDuContenu(c.AbsolutePath, c.FrontendSystemId)))(candidat),
        };
        var reference = RomAdmission.ReferenceDuScore(systeme, duMem, jeu.ContentHashes, referentiel);
        var refSpeedrun = speedrun ? RomAdmission.ReferenceDuSpeedrun(systeme, jeu.RomGroup, referentiel, racineEtats) ?? reference : null;
        // Un jeu ouvert aux seuls speedruns sans reference connue : la regle du score s'applique (dump propre, prouve, publie).
        return RomAdmission.JugerConsole(systeme, empreintes, duMem, jeu.ContentHashes, reference, refSpeedrun, referentiel);
    }

    /// <summary>Un coeur hote des fantomes ou des etats de depart designent-ils la ROM des speedruns de ce jeu ?</summary>
    internal static bool AUnSpeedrun(OpenGame jeu)
        => !string.Equals(jeu.SystemId, "arcade", StringComparison.OrdinalIgnoreCase)
           && RomAdmission.ReferenceDuSpeedrun(jeu.SystemId, jeu.RomGroup, RomAdmission.ReferentielSurDisque.Borne) is not null;

    /// <summary>LA ROM qu'il faut au joueur pour ce jeu, a lui dire quand la sienne n'entre pas ; null si on ne sait pas.</summary>
    internal string? RomAttendue(OpenGame jeu, bool speedrun)
    {
        try
        {
            if (string.Equals(jeu.SystemId, "arcade", StringComparison.OrdinalIgnoreCase))
                return GamelistIdentity.DeclaredName(jeu.SystemId, jeu.RomGroup) is { Length: > 0 } nom ? $"{nom}, set MAME/FBNeo nommé par la définition de score" : null;
            var referentiel = RomAdmission.ReferentielSurDisque.Borne;
            if (speedrun && RomAdmission.ReferenceDuSpeedrun(jeu.SystemId, jeu.RomGroup, referentiel) is { } r) return r.Nom;
            return RomAdmission.ReferenceDuScore(jeu.SystemId, RomAdmission.DumpsDuMem(_catalog.OfficialDefinitionPath(jeu.SystemId, jeu.RomGroup)),
                jeu.ContentHashes, referentiel)?.Nom;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// LES EMPREINTES SANS EN-TETE QU'UN JEU OUVERT ACCEPTE (08/11) : celles que le site publie (<c>content_hashes.rhash</c>)
    /// et ses md5, car le md5 d'un fichier sans en-tete EST son empreinte sans en-tete (un .sfc No-Intro, un .nes sans
    /// en-tete du referentiel). Console seulement : l'appelant ne la demande jamais pour l'arcade. Pur.
    /// </summary>
    internal static HashSet<string> EmpreintesSansEnTete(ContentHashes? hashes)
        => new((hashes?.Rhash ?? []).Concat(hashes?.Md5 ?? []).Where(h => h.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Les empreintes du profil ouvert d'un jeu, d'apres le dernier index des jeux ouverts garde sur disque (celui de la
    /// collection) ; null sans index ou sans ce jeu.
    /// </summary>
    internal static ContentHashes? EmpreintesOuvertes(string romGroup, string? stateRoot = null)
    {
        try
        {
            var chemin = Path.Combine(stateRoot ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay"), "scoring-collection-remote.json");
            if (string.IsNullOrWhiteSpace(romGroup) || !File.Exists(chemin)) return null;
            var manifeste = JsonSerializer.Deserialize<OpenGamesManifest>(File.ReadAllText(chemin), JsonLecture);
            return manifeste?.Games?.FirstOrDefault(g => string.Equals(g.RomGroup, romGroup, StringComparison.OrdinalIgnoreCase))?.ContentHashes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// LE JEU OUVERT D'UNE ROM CONSOLE, PAR SON CONTENU (24/10, demande du user : « c'est le hash compatible qui fait
    /// foi ») : le groupe dont les empreintes publiees contiennent le md5 ou le sha256 MESURE, sur le meme systeme.
    /// Le nom du fichier n'y entre pas : « Sonic The Hedgehog (Japan) (En) (Virtual Console) » etait dans la collection
    /// par son contenu et la partie cherchait le profil « sonic-the-hedgehog-japan-en-virtual-console ». Arcade : null,
    /// le set y est l'identite. Null aussi sans index ou sans empreinte qui corresponde.
    /// </summary>
    internal static string? GroupeOuvertDuContenu(string? systemId, string? md5, string? sha256, string? stateRoot = null, string? sansEnTete = null)
    {
        try
        {
            var systeme = RomCanonicalResolver.CanonicalScoringSystem(systemId ?? string.Empty);
            if (string.IsNullOrEmpty(systeme) || systeme == "arcade"
                || (string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256) && string.IsNullOrWhiteSpace(sansEnTete)))
            {
                return null;
            }

            var chemin = Path.Combine(stateRoot ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay"), "scoring-collection-remote.json");
            if (!File.Exists(chemin)) return null;
            var manifeste = JsonSerializer.Deserialize<OpenGamesManifest>(File.ReadAllText(chemin), JsonLecture);
            return manifeste?.Games?.FirstOrDefault(g =>
                    string.Equals(RomCanonicalResolver.CanonicalScoringSystem(g.SystemId), systeme, StringComparison.Ordinal)
                    && ((!string.IsNullOrWhiteSpace(md5) && (g.ContentHashes?.Md5 ?? []).Contains(md5.Trim(), StringComparer.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(sha256) && (g.ContentHashes?.Sha256 ?? []).Contains(sha256.Trim(), StringComparer.OrdinalIgnoreCase))
                        // Le meme jeu sous un autre en-tete (08/11) : son empreinte sans en-tete.
                        || (!string.IsNullOrWhiteSpace(sansEnTete) && EmpreintesSansEnTete(g.ContentHashes).Contains(sansEnTete.Trim()))))
                ?.RomGroup;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Le .MEM nomme-t-il ce set ? Le md5 de son nom de set publie par le profil ou ecrit au .MEM, ou un libelle « set.zip ».
    /// Pur.
    /// </summary>
    internal static bool SetNommeParLeMem(string set, ICollection<string> publies, IReadOnlyDictionary<string, string> duMem)
    {
        var nom = Md5DuNomDeSet(set);
        return publies.Contains(nom) || duMem.ContainsKey(nom)
            || duMem.Values.Any(l => l.StartsWith(set + ".zip", StringComparison.OrdinalIgnoreCase) || l.StartsWith(set + ".7z", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>L'empreinte arcade d'un set dans un .MEM : le md5 de son nom, en minuscules. Pur.</summary>
    internal static string Md5DuNomDeSet(string set)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(set.Trim().ToLowerInvariant())))
            .ToLowerInvariant();

    /// <summary>Au-dela, ce n'est plus une cartouche : on ne lit pas un disque entier pour une collection.</summary>
    private const long PlafondMd5 = 64L * 1024 * 1024;

    /// <summary>Les empreintes d'un contenu, par « chemin|taille|date|regle » : relues seulement quand le fichier change.</summary>
    private static readonly ConcurrentDictionary<string, (string? Md5, string? SansEnTete)> Md5Calcules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Le md5 du CONTENU : celui du fichier, ou du fichier unique d'un .zip ou d'un .7z (une cartouche en archive a le
    /// md5 de sa ROM, pas celui de son emballage). Garde en memoire tant que le fichier ne change pas. Le plafond se
    /// releve pour un appelant qui ne lit que des candidats deja reconnus (MULTIPLAYER : un disque PSP).
    /// </summary>
    internal static string? Md5DuContenu(string chemin) => Md5DuContenu(chemin, PlafondMd5);

    /// <summary>(10/11) Le md5 du fichier tel quel (un zip d'arcade entier), sous le plafond ; null s'il manque ou depasse.</summary>
    internal static string? Md5DuFichier(string chemin)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists || info.Length > PlafondMd5) return null;
            using var flux = File.Open(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(flux)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? Md5DuContenu(string chemin, long plafond) => EmpreintesDuContenu(chemin, RegleSansEnTete.Aucune, plafond).Md5;

    /// <summary>
    /// Le md5 du contenu SANS SON EN-TETE, a la RetroAchievements (<see cref="EmpreinteSansEnTete"/>), pour un fichier de ce
    /// systeme ; null pour un systeme sans regle (arcade comprise : le set y est l'identite) ou un contenu illisible.
    /// </summary>
    internal static string? SansEnTeteDuContenu(string chemin, string? systemId)
        => EmpreinteSansEnTete.RegleDu(systemId) is var regle and not RegleSansEnTete.Aucune
            ? EmpreintesDuContenu(chemin, regle, PlafondMd5).SansEnTete
            : null;

    /// <summary>
    /// Le md5 du contenu et, si la regle en a une, son md5 sans en-tete : une seule lecture pour les deux. Le md5 seul est
    /// garde aussi sous la regle « Aucune », pour que l'appelant qui ne demande que lui ne relise pas le fichier.
    /// </summary>
    internal static (string? Md5, string? SansEnTete) EmpreintesDuContenu(string chemin, RegleSansEnTete regle, long plafond)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists) return (null, null);
            // Le plafond fait partie de la cle : un contenu refuse sous 64 Mo peut etre lu par MULTIPLAYER, au plafond releve.
            var cle = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + plafond + "|";
            if (Md5Calcules.TryGetValue(cle + regle, out var connu)) return connu;

            (string? Md5, string? SansEnTete) empreintes = (null, null);
            using (var contenu = ContenuDUneRom.Ouvrir(info.FullName, plafond))
            {
                if (contenu is not null)
                {
                    empreintes = EmpreinteSansEnTete.Empreintes(regle, contenu.Flux, contenu.Taille);
                }
            }

            Md5Calcules[cle + regle] = empreintes;
            if (regle != RegleSansEnTete.Aucune) Md5Calcules.TryAdd(cle + RegleSansEnTete.Aucune, (empreintes.Md5, null));
            return empreintes;
        }
        catch (Exception ex) when (ContenuDUneRom.EstUneLectureRatee(ex))
        {
            return (null, null);
        }
    }

    private HashSet<string> LireCollectionPrecedente()
    {
        try
        {
            var chemin = _writer.ConfigPath(CollectionName);
            return File.Exists(chemin)
                ? File.ReadAllLines(chemin)
                    .Select(ligne => ligne.Trim())
                    .Where(ligne => ligne.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<TelechargementResultat> TelechargerAsync(string? etag, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _dernierAppelUtc < _minimumEntreDeuxAppels)
        {
            return new TelechargementResultat(null, etag, null);
        }

        try
        {
            using var client = _httpFactory.CreateClient(HttpClientName);
            if (client.BaseAddress == null)
            {
                client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            }

            using var requete = new HttpRequestMessage(HttpMethod.Get, "/api/v1/scores/open-games");
            requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(etag))
            {
                requete.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            _dernierAppelUtc = DateTime.UtcNow;
            using var reponse = await client.SendAsync(requete, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var etagRecu = reponse.Headers.ETag?.ToString() ?? etag;

            if (reponse.StatusCode == HttpStatusCode.NotModified)
            {
                return new TelechargementResultat(null, etagRecu, null);
            }

            if (!reponse.IsSuccessStatusCode)
            {
                return new TelechargementResultat(null, etag, "HTTP " + (int) reponse.StatusCode);
            }

            var corps = await reponse.Content.ReadAsStringAsync(cancellationToken);
            var manifeste = JsonSerializer.Deserialize<OpenGamesManifest>(corps, JsonLecture);
            var invalide = Valider(manifeste);
            return invalide is { Length: > 0 }
                ? new TelechargementResultat(null, etag, invalide)
                : new TelechargementResultat(Nettoyer(manifeste!), etagRecu, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TelechargementResultat(null, etag, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new TelechargementResultat(null, etag, "reseau " + (ex.StatusCode?.ToString() ?? "injoignable"));
        }
        catch (JsonException)
        {
            return new TelechargementResultat(null, etag, "reponse illisible");
        }
    }

    /// <summary>Une reponse qui ne respecte pas le contrat n'est jamais interpretee comme une liste vide.</summary>
    internal static string? Valider(OpenGamesManifest? manifeste)
    {
        if (manifeste == null || !manifeste.Ok)
        {
            return "reponse refusee";
        }

        if (manifeste.SchemaVersion != 1)
        {
            return "schema " + manifeste.SchemaVersion + " inconnu";
        }

        if (manifeste.Games == null)
        {
            return "liste de jeux absente";
        }

        return string.IsNullOrWhiteSpace(manifeste.Revision) ? "revision absente" : null;
    }

    /// <summary>Ne garde que des entrees exploitables : identifiants presents et empreinte .MEM valide.</summary>
    internal static OpenGamesManifest Nettoyer(OpenGamesManifest manifeste)
    {
        var jeux = (manifeste.Games ?? [])
            .Where(jeu => !string.IsNullOrWhiteSpace(jeu.SystemId)
                && !string.IsNullOrWhiteSpace(jeu.RomGroup)
                && jeu.MemSha256 is { Length: 64 }
                && jeu.MemSha256.All(Uri.IsHexDigit))
            .Select(jeu => jeu with
            {
                SystemId = jeu.SystemId.Trim().ToLowerInvariant(),
                RomGroup = jeu.RomGroup.Trim().ToLowerInvariant(),
                MemSha256 = jeu.MemSha256!.ToLowerInvariant(),
            })
            .GroupBy(jeu => jeu.SystemId + "/" + jeu.RomGroup, StringComparer.OrdinalIgnoreCase)
            .Select(groupe => groupe.First())
            .ToList();
        return manifeste with { Games = jeux };
    }

    private ScoringCollectionState? LireEtat() => LireJson<ScoringCollectionState>(EtatPath);

    private OpenGamesManifest? LireManifeste() => LireJson<OpenGamesManifest>(ManifestePath);

    private T? LireJson<T>(string chemin) where T : class
    {
        try
        {
            return File.Exists(chemin) ? JsonSerializer.Deserialize<T>(File.ReadAllText(chemin), JsonLecture) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogDebug(ex, "Etat de collection illisible : {Chemin}", chemin);
            return null;
        }
    }

    private void EcrireManifeste(OpenGamesManifest manifeste) => EcrireJson(ManifestePath, manifeste);

    private ScoringCollectionStatus EcrireEtat(ScoringCollectionState etat, bool stale)
    {
        EcrireJson(EtatPath, etat);
        var etatTexte = stale
            ? (etat.LastSuccessUtc == null ? "error" : "stale")
            : (etat.LocalReadyGames > 0 ? "ready" : "empty");
        return new ScoringCollectionStatus(
            true,
            true,
            etatTexte,
            etat.RemoteRevision,
            etat.RemoteGames,
            etat.LocalReadyGames,
            etat.LastSuccessUtc,
            stale,
            etat.LastError,
            etat.Missing,
            etat.Advice);
    }

    private void EcrireJson<T>(string chemin, T valeur)
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);
            File.WriteAllText(chemin, JsonSerializer.Serialize(valeur, JsonEcriture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Etat de collection non ecrit : {Chemin}", chemin);
        }
    }

    private sealed record TelechargementResultat(OpenGamesManifest? Manifeste, string? Etag, string? Erreur);
}

/// <summary>L'index public des jeux ouverts, tel que NelfePlay le publie.</summary>
public sealed record OpenGamesManifest
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; init; }

    [JsonPropertyName("revision")]
    public string? Revision { get; init; }

    [JsonPropertyName("games")]
    public IReadOnlyList<OpenGame> Games { get; init; } = [];

    /// <summary>
    /// Les jeux ouverts aux SEULS speedruns (05/11, Super Metroid, sans score) : leur ROM se cherche comme celle d'un jeu du
    /// World Scoring, pour « Challenges & Speedruns », mais ils n'entrent pas dans la collection World Scoring.
    /// </summary>
    [JsonPropertyName("speedrun_games")]
    public IReadOnlyList<OpenGame> SpeedrunGames { get; init; } = [];
}

public sealed record OpenGame
{
    [JsonPropertyName("system_id")]
    public string SystemId { get; init; } = string.Empty;

    [JsonPropertyName("rom_group")]
    public string RomGroup { get; init; } = string.Empty;

    /// <summary>L'empreinte de la definition de score homologuee par le profil.</summary>
    [JsonPropertyName("mem_sha256")]
    public string? MemSha256 { get; init; }

    /// <summary>Les dumps que le referentiel reconnait : sert a choisir une variante locale.</summary>
    [JsonPropertyName("content_hashes")]
    public ContentHashes? ContentHashes { get; init; }

    [JsonPropertyName("rulesets")]
    public IReadOnlyList<OpenRuleset> Rulesets { get; init; } = [];
}

public sealed record ContentHashes
{
    [JsonPropertyName("md5")]
    public IReadOnlyList<string>? Md5 { get; init; }

    [JsonPropertyName("sha1")]
    public IReadOnlyList<string>? Sha1 { get; init; }

    [JsonPropertyName("sha256")]
    public IReadOnlyList<string>? Sha256 { get; init; }

    /// <summary>
    /// Les md5 SANS EN-TETE, a la RetroAchievements (08/11) : le site les publie a part, une API plus ancienne les ignore.
    /// Absents d'un index plus ancien.
    /// </summary>
    [JsonPropertyName("rhash")]
    public IReadOnlyList<string>? Rhash { get; init; }

    /// <summary>
    /// LES VERSIONS DEJA CERTIFIEES (11/11, option A) : les md5 de ce jeu qui portent une partie publiee et que le site garde
    /// admis (une version officielle que le .MEM ne nomme pas, ou un mauvais dump deja mesure). Absents d'un index plus ancien.
    /// </summary>
    [JsonPropertyName("certified")]
    public IReadOnlyList<string>? Certified { get; init; }
}

public sealed record OpenRuleset
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("profile_version")]
    public int ProfileVersion { get; init; }
}

/// <summary>L'etat garde sur disque : il distingue une liste vide legitime d'un incident.</summary>
public sealed record ScoringCollectionState
{
    public static ScoringCollectionState Vide => new();

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("remote_revision")]
    public string? RemoteRevision { get; init; }

    [JsonPropertyName("remote_etag")]
    public string? RemoteEtag { get; init; }

    /// <summary>Pourquoi chaque jeu ouvert n'entre pas ici : « 19xx : ROM absente ».</summary>
    [JsonPropertyName("missing")]
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>
    /// (11/11) Ce qui est conseille sans bloquer, « groupe : conseil » : la version propre d'un jeu retenu sur une version deja
    /// certifiee que le referentiel marque mauvais dump.
    /// </summary>
    [JsonPropertyName("advice")]
    public IReadOnlyList<string> Advice { get; init; } = [];

    [JsonPropertyName("collection_sha256")]
    public string? CollectionSha256 { get; init; }

    [JsonPropertyName("managed_file")]
    public string? ManagedFile { get; init; }

    [JsonPropertyName("remote_games")]
    public int RemoteGames { get; init; }

    [JsonPropertyName("local_ready_games")]
    public int LocalReadyGames { get; init; }

    [JsonPropertyName("last_success_utc")]
    public DateTime? LastSuccessUtc { get; init; }

    [JsonPropertyName("last_attempt_utc")]
    public DateTime? LastAttemptUtc { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }
}

/// <summary>Ce que le support lit en un coup d'oeil.</summary>
public sealed record ScoringCollectionStatus(
    bool Enabled,
    bool Visible,
    string State,
    string? RemoteRevision,
    int RemoteGames,
    int LocalReadyGames,
    DateTime? LastSuccessUtc,
    bool Stale,
    string? LastError,
    /// <summary>Pourquoi chaque jeu ouvert n'est pas dans la collection de cette borne.</summary>
    IReadOnlyList<string>? Missing = null,
    /// <summary>Ce qui est conseille sans bloquer (la version propre d'une version deja certifiee).</summary>
    IReadOnlyList<string>? Advice = null)
{
    public static ScoringCollectionStatus Initial => new(true, true, "error", null, 0, 0, null, false, null);
}

/// <summary>Reprend le statut depuis l'etat garde sur disque au demarrage.</summary>
internal static class ScoringCollectionStateExtensions
{
    public static ScoringCollectionStatus EnStale(this ScoringCollectionState etat)
        => new(true, true, etat.LastSuccessUtc == null ? "error" : "stale", etat.RemoteRevision,
            etat.RemoteGames, etat.LocalReadyGames, etat.LastSuccessUtc, true, etat.LastError, etat.Missing, etat.Advice);
}
