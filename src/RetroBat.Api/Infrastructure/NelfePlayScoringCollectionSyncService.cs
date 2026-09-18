using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

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
    private readonly EsCustomCollectionWriter _writer;
    private readonly EsCollectionThemeAssets _assets;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEsSettingsChangeBus? _settingsChangeBus;
    private readonly ILogger<NelfePlayScoringCollectionSyncService>? _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private readonly string _stateRoot;
    private readonly TimeSpan _minimumEntreDeuxAppels;

    private DateTime _dernierAppelUtc = DateTime.MinValue;
    private ScoringCollectionStatus _statut = ScoringCollectionStatus.Initial;
    private IDisposable? _abonnementReglages;
    private bool _derniereVisibilite = true;

    public NelfePlayScoringCollectionSyncService(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ApiExposeOptions> options,
        InstalledGameCatalog catalog,
        EsCustomCollectionWriter writer,
        EsCollectionThemeAssets assets,
        MediaRuntimeState runtimeState,
        IEsSettingsChangeBus? settingsChangeBus = null,
        ILogger<NelfePlayScoringCollectionSyncService>? logger = null,
        string? stateRoot = null,
        TimeSpan? minimumEntreDeuxAppels = null)
    {
        _httpFactory = httpFactory;
        _options = options;
        _catalog = catalog;
        _writer = writer;
        _assets = assets;
        _runtimeState = runtimeState;
        _settingsChangeBus = settingsChangeBus;
        _logger = logger;
        _stateRoot = stateRoot ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay");
        // Garde-fou du CDC : aucun appel a moins de 30 s d'intervalle.
        _minimumEntreDeuxAppels = minimumEntreDeuxAppels ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Le dernier etat connu, tel que l'endpoint de statut le publie.</summary>
    public ScoringCollectionStatus Status => _statut;

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
            if (EmulatorForeground.EmulateurTourne())
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
            var resultat = _writer.Apply(CollectionName, chemins);
            // L'identite visuelle suit la collection : la declarer dans le theme est ce qui lui
            // donne sa propre tuile au lieu de la ranger dans le fourre-tout « collections ».
            var visuel = chemins.Count > 0 ? _assets.Install(CollectionName) : _assets.Remove(CollectionName);
            if (visuel.Changed)
            {
                PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
            }

            if (resultat.Changed || visuel.Changed)
            {
                _logger?.LogInformation(
                    "Collection World Scoring : {Entrees} entrees sur {Distants} jeux ouverts et {Candidats} candidats locaux, revision {Revision}",
                    chemins.Count, manifeste.Games.Count, candidats, manifeste.Revision);
                _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
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

    private ScoringCollectionStatus Desactiver(bool enabled, bool visible)
    {
        var resultat = _writer.Remove(CollectionName);
        if (_assets.Remove(CollectionName).Changed)
        {
            PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
        }

        if (resultat.Changed)
        {
            _logger?.LogInformation("Collection World Scoring retiree (option coupee)");
            _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
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
        var systemes = manifeste.Games
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
        var precedents = LireCollectionPrecedente();
        var retenus = new List<string>();

        foreach (var jeu in manifeste.Games)
        {
            var cle = jeu.SystemId + "/" + jeu.RomGroup;
            if (!parGroupe.TryGetValue(cle, out var candidats) || candidats.Count == 0)
            {
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
                continue;
            }

            if (!string.Equals(locale, jeu.MemSha256, StringComparison.OrdinalIgnoreCase))
            {
                // La borne a bien un .MEM, mais pas celui qu'exige le profil : le score
                // serait refuse. Le jeu revient des que le Data Pack est a jour.
                _logger?.LogDebug("World Scoring : {Cle} en definition non homologuee", cle);
                continue;
            }

            var choisi = Choisir(candidats, jeu, precedents);
            if (choisi is { Length: > 0 })
            {
                retenus.Add(choisi);
            }
        }

        return retenus;
    }

    /// <summary>
    /// Plusieurs dumps du meme jeu peuvent etre installes : on garde le chemin deja choisi, ou
    /// celui dont le hash est reconnu par le profil, sinon le plus proche du groupe.
    /// </summary>
    internal static string? Choisir(List<InstalledGame> candidats, OpenGame jeu, HashSet<string> precedents)
    {
        var dejaChoisi = candidats.FirstOrDefault(candidat =>
            precedents.Contains(candidat.AbsolutePath.Replace('\\', '/')));
        if (dejaChoisi != null)
        {
            return dejaChoisi.AbsolutePath;
        }

        var reconnus = jeu.ContentHashes ?? new ContentHashes();
        var connus = new HashSet<string>(
            (reconnus.Md5 ?? []).Concat(reconnus.Sha1 ?? []).Concat(reconnus.Sha256 ?? []),
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
            etat.LastError);
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
    string? LastError)
{
    public static ScoringCollectionStatus Initial => new(true, true, "error", null, 0, 0, null, false, null);
}

/// <summary>Reprend le statut depuis l'etat garde sur disque au demarrage.</summary>
internal static class ScoringCollectionStateExtensions
{
    public static ScoringCollectionStatus EnStale(this ScoringCollectionState etat)
        => new(true, true, etat.LastSuccessUtc == null ? "error" : "stale", etat.RemoteRevision,
            etat.RemoteGames, etat.LocalReadyGames, etat.LastSuccessUtc, true, etat.LastError);
}
