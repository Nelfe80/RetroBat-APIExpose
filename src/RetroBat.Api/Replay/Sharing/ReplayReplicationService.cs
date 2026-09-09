using System.Text.Json;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// L'agent de réplication : c'est lui qui fait l'ESSAIM.
///
/// Sans lui, un record n'existe qu'à deux endroits, la borne qui l'a produit et l'amorce. Le §54
/// veut cinq copies dans deux régions pour un Top 50, dix dans trois pour un record du monde. Ces
/// copies ne viennent de nulle part : il faut des bornes qui acceptent d'en garder une.
///
/// Le mécanisme est volontairement bête. On lit la collection d'un classement suivi, elle donne
/// des hashes, et pour chacun qu'on n'a pas encore on récupère d'abord le MANIFESTE, qui rend le
/// replay connu de cette borne, puis on demande à la seam de rendre l'objet disponible. Tout le
/// chemin existant est ainsi réutilisé, vérification de taille et de hash comprise.
///
/// Trois garde-fous, parce que ça se passe sur le PC de quelqu'un.
///
/// C'est un CHOIX : rien n'est répliqué tant que le propriétaire n'a pas suivi un classement et
/// activé la réplication. On ne télécharge pas les parties d'inconnus sur une machine sans son
/// accord.
///
/// Il y a un BUDGET, en nombre d'objets et en méga-octets. Un disque plein est une panne, et une
/// panne causée par une fonction que l'utilisateur avait à peine remarquée est la pire espèce.
///
/// Et l'agent se TAIT pendant une partie ou une lecture, comme la file de semis.
/// </summary>
public sealed class ReplayReplicationService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PremierEssai = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ReplayFollowStore _follows;
    private readonly ReplayPlayedGamesStore _joues;
    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly IReplaySourceResolver _source;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplayReplicationService> _logger;

    private volatile bool _gameActive;
    private volatile bool _replayActive;

    public ReplayReplicationService(ReplayFollowStore follows, ReplayPlayedGamesStore joues,
        IReplayManifestStore manifests,
        IReplayObjectStore objects, IReplayMetadataStore meta, IReplaySourceResolver source,
        IHttpClientFactory httpFactory, IConfiguration config, IEventBus bus,
        ILogger<ReplayReplicationService> logger)
    {
        _follows = follows; _joues = joues; _manifests = manifests; _objects = objects; _meta = meta;
        _source = source; _httpFactory = httpFactory; _config = config; _bus = bus; _logger = logger;
    }

    private bool Enabled => _config.GetValue("Replay:Replication:Enabled", false);
    private int MaxObjects => Math.Max(1, _config.GetValue("Replay:Replication:MaxObjects", 50));
    private long MaxBytes => Math.Max(1, _config.GetValue("Replay:Replication:MaxMegabytes", 500L)) * 1024L * 1024L;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _bus.Subscribe<EventEnvelope>(OnBusEvent); } catch (Exception ex) { _logger.LogDebug(ex, "Replay : abonnement au bus impossible."); }

        try { await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : passage de réplication en erreur."); }

            try { await Task.Delay(Cadence, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Combien de replays on précharge par jeu joué. On regarde la tête d'un
    /// classement, pas son milieu.</summary>
    private const int TeteDeClassement = 3;

    /// <summary>Le ruleset par défaut de la plateforme. Un jeu ouvert au scoring n'en a qu'un
    /// dans les faits ; le jour où il y en aura plusieurs, c'est le suivi explicite qui
    /// tranchera, pas le préchargement.</summary>
    private const string RulesetParDefaut = "1cc";

    /// <summary>
    /// Ce que l'agent va chercher, et pourquoi.
    ///
    /// Deux sources, deux intentions. Les classements SUIVIS relèvent de la préservation : le
    /// propriétaire a accepté d'héberger des replays pour que d'autres puissent les regarder, et
    /// on les réplique en entier jusqu'au budget. Les jeux JOUÉS sur cette borne relèvent de la
    /// latence : quelqu'un qui vient de finir une partie ira voir le record, et l'objet doit
    /// déjà être là. On n'en prend que la tête.
    ///
    /// Le préchargement ne contourne aucun consentement : il vit sous le même interrupteur et le
    /// même budget que la réplication. Il change QUELS objets sont choisis, jamais SI la machine
    /// télécharge.
    /// </summary>
    private List<(ReplayFollow Follow, int Plafond, string Origine)> Cibles()
    {
        var sortie = new List<(ReplayFollow, int, string)>();
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var follow in _follows.Follows)
        {
            if (vus.Add(follow.RomGroup + "|" + follow.Ruleset))
                sortie.Add((follow, int.MaxValue, "répliqué"));
        }

        foreach (var jeu in _joues.Games)
        {
            var follow = new ReplayFollow(jeu.RomGroup, RulesetParDefaut);
            if (vus.Add(follow.RomGroup + "|" + follow.Ruleset))
                sortie.Add((follow, TeteDeClassement, "préchargé"));
        }

        return sortie;
    }

    /// <summary>Un passage, déclenchable à la demande pour le diagnostic.</summary>
    public async Task<ReplicationReport> TickAsync(CancellationToken ct)
    {
        if (!Enabled) return new ReplicationReport(false, 0, 0, "replication_disabled");
        var cibles = Cibles();
        if (cibles.Count == 0) return new ReplicationReport(true, 0, 0, "no_followed_leaderboard");
        if (_gameActive || _replayActive) return new ReplicationReport(true, 0, 0, "busy");

        var (heldCount, heldBytes) = Budget();
        var recupere = 0;
        var examines = 0;

        foreach (var (follow, plafond, origine) in cibles)
        {
            if (ct.IsCancellationRequested) break;
            var collection = await FetchCollectionAsync(follow, ct).ConfigureAwait(false);
            if (collection is null) continue;

            // Un classement SUIVI se replique en entier : c'est de la preservation, et le
            // budget dit ou s'arreter. Un jeu qu'on JOUE se precharge par la tete : personne ne
            // regarde le quarante-septieme, et prendre plus mangerait le budget du reste.
            foreach (var entry in collection.Entries.Take(plafond))
            {
                if (ct.IsCancellationRequested) break;
                examines++;

                // Déjà là : rien à faire, et surtout rien à retélécharger.
                if (File.Exists(_objects.ObjectPath(entry.ObjectSha256))) continue;

                if (heldCount + 1 > MaxObjects)
                {
                    _logger.LogInformation("Replay : budget de réplication atteint ({Count} objets), on s'arrête là.", heldCount);
                    return new ReplicationReport(true, recupere, examines, "budget_objects_reached");
                }

                // Le MANIFESTE d'abord : sans lui, l'objet serait illisible et la borne ne
                // saurait ni quel core ni quelle ROM employer.
                var manifest = await FetchManifestAsync(entry, ct).ConfigureAwait(false);
                if (manifest is null) continue;

                if (heldBytes + manifest.Object.Size > MaxBytes)
                {
                    _logger.LogInformation("Replay : budget de réplication atteint ({Mo} Mo), on s'arrête là.", heldBytes / (1024 * 1024));
                    return new ReplicationReport(true, recupere, examines, "budget_bytes_reached");
                }

                _manifests.SaveManifest(manifest);
                if (_meta.GetMeta(manifest.ReplayId) is null)
                {
                    // Reçu du réseau : cette borne n'en est pas l'auteur, et elle ne le republie pas.
                    _meta.SaveMeta(ReplayLocalMetadata.Fresh(manifest.ReplayId) with { CreatedByThisDevice = false });
                }

                // La seam fait le reste, vérification de taille et de hash comprise.
                if (await _source.EnsureObjectAvailableAsync(manifest, ct).ConfigureAwait(false))
                {
                    recupere++;
                    heldCount++;
                    heldBytes += manifest.Object.Size;
                    _logger.LogInformation("Replay : {ReplayId} {Origine} depuis le classement {Board} (rang {Rank}).",
                        manifest.ReplayId, origine, collection.LeaderboardId, entry.Rank);
                }
            }
        }

        return new ReplicationReport(true, recupere, examines, null);
    }

    public sealed record ReplicationReport(bool Ran, int Fetched, int Examined, string? Reason);

    private (int Count, long Bytes) Budget()
    {
        var count = 0;
        long bytes = 0;
        foreach (var m in _manifests.ListManifests())
        {
            var path = _objects.ObjectPath(m.Object.Sha256);
            if (!File.Exists(path)) continue;
            count++;
            bytes += m.Object.Size;
        }
        return (count, bytes);
    }

    private async Task<CollectionDoc?> FetchCollectionAsync(ReplayFollow follow, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var url = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl.TrimEnd('/')
                      + "/api/v1/replay/collection?rom_group=" + Uri.EscapeDataString(follow.RomGroup)
                      + "&ruleset=" + Uri.EscapeDataString(follow.Ruleset);
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var res = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CollectionDoc>(body, Json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : collection {Board} injoignable.", follow.RomGroup + "|" + follow.Ruleset);
            return null;
        }
    }

    private async Task<ReplayManifest?> FetchManifestAsync(CollectionEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.ManifestUrl)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var body = await client.GetStringAsync(entry.ManifestUrl, cts.Token).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<ReplayManifest>(body, Json);

            // On ne croit pas le manifeste sur parole : il doit désigner l'objet annoncé par la
            // collection, sinon on rangerait l'identité d'un replay avec le contenu d'un autre.
            if (manifest is null
                || !string.Equals(manifest.ReplayId, entry.ReplayId, StringComparison.Ordinal)
                || !string.Equals(manifest.Object.Sha256, entry.ObjectSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Replay : manifeste incohérent avec la collection pour {ReplayId} — ignoré.", entry.ReplayId);
                return null;
            }
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : manifeste {ReplayId} non récupéré.", entry.ReplayId);
            return null;
        }
    }

    private void OnBusEvent(EventEnvelope e)
    {
        switch (e.Type)
        {
            case "ui.game.started": _gameActive = true; break;
            case "ui.game.ended": _gameActive = false; break;
            case "replay.launching":
            case "replay.started": _replayActive = true; break;
            case "replay.finished": _replayActive = false; break;
            case "scoring.listener.attestation": RetenirJeuJoue(e); break;
        }
    }

    /// <summary>
    /// L'attestation dit a quel jeu on vient de jouer. C'est la seule source fiable du
    /// rom_group cote borne : le chemin de la ROM ne le donne pas, et le resoudre depuis un nom
    /// de fichier serait deviner.
    /// </summary>
    private void RetenirJeuJoue(EventEnvelope e)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(e.Payload));
            var racine = doc.RootElement;
            string? Lire(string nom) => racine.TryGetProperty(nom, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            _joues.Remember(Lire("Rom"), Lire("SystemId"));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : attestation illisible pour le prechargement."); }
    }

    private sealed record CollectionEntry(int Rank, string ReplayId, string ObjectSha256,
        string? ObjectUrl, string? ManifestUrl);

    private sealed record CollectionDoc(string Schema, string LeaderboardId, long Generation,
        int Count, IReadOnlyList<CollectionEntry> Entries);
}
