using RetroBat.Domain.Events;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Vide la file de semis (CDC DEV §101.5) : porte chaque replay inscrit jusqu'à l'amorce, et
/// recommence tant que ce n'est pas fait.
///
/// Trois propriétés voulues.
///
/// Le test d'achèvement est ADRESSÉ PAR CONTENU : on demande à l'amorce si elle détient déjà ce
/// hash. Comme le nom de l'objet EST son hash, la réponse est sans ambiguïté et il n'y a aucun
/// état local à conserver ni à croire. Une reprise revérifie la réalité au lieu de se fier à sa
/// mémoire, donc rien ne peut être poussé deux fois ni perdu en silence.
///
/// Le semis se TAIT pendant qu'une partie ou une lecture tourne. Envoyer un objet de plusieurs
/// méga-octets pendant que le joueur joue lui volerait de la bande passante, et c'est exactement
/// le genre de nuisance invisible qu'on ne rattrape jamais. Le record attend quelques minutes.
///
/// Enfin, un délai de garde après une poussée réussie : la plateforme relaie vers l'amorce par une
/// tâche périodique, donc l'objet n'y apparaît pas immédiatement. Sans ce délai, on repousserait
/// le même objet à chaque tour pendant toute la fenêtre de relais.
/// </summary>
public sealed class ReplaySeedService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(45);
    /// <summary>Après une poussée acceptée, on laisse à la plateforme le temps de relayer.</summary>
    private static readonly TimeSpan DelaiDeGarde = TimeSpan.FromMinutes(30);

    /// <summary>
    /// LE RECORD N'ATTEND PLUS LE TOUR SUIVANT (2026-10-10). Le replay d'un score s'inscrit à la fin de
    /// la partie, quelques secondes AVANT qu'EmulationStation annonce la fin du jeu : la tentative
    /// immédiate tombait « en partie », et le replay attendait le tour suivant, jusqu'à cinq minutes
    /// de « Replay en attente » sur les classements. La fin d'une partie ou d'une lecture relance la
    /// file trente secondes plus tard, si des replays y attendent. La cadence reste le filet.
    /// </summary>
    private static readonly TimeSpan ApresLaPartie = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _reveil = new(0, 1);

    /// <summary>
    /// Revision du miroir (27/11/2026) : un passage par heure, 5 replays au plus, chacun une fois par jour. Le premier
    /// passage a lieu 10 minutes apres le demarrage : les anciens « mirrored » se verifient au compte-gouttes, sans rafale.
    /// </summary>
    private static readonly TimeSpan CadenceRevision = TimeSpan.FromHours(1);
    private static readonly TimeSpan PremiereRevision = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PauseEntreVerifications = TimeSpan.FromSeconds(3);
    private DateTime _prochaineRevision = DateTime.MinValue;

    private readonly ReplaySeedQueue _queue;
    private readonly ReplayTransitPublisher _publisher;
    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplaySeedService> _logger;

    private volatile bool _gameActive;
    private volatile bool _replayActive;

    public ReplaySeedService(ReplaySeedQueue queue, ReplayTransitPublisher publisher,
        IReplayManifestStore manifests, IReplayObjectStore objects, IReplayMetadataStore meta,
        IEventBus bus, ILogger<ReplaySeedService> logger)
    {
        _queue = queue; _publisher = publisher; _manifests = manifests; _objects = objects; _meta = meta;
        _bus = bus; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _bus.Subscribe<EventEnvelope>(OnBusEvent); } catch (Exception ex) { _logger.LogDebug(ex, "Replay : abonnement au bus impossible."); }

        // Un premier passage peu après le démarrage : c'est lui qui rattrape une machine éteinte
        // en plein envoi. Pas immédiat, pour ne pas concurrencer le démarrage d'EmulationStation.
        try { await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _prochaineRevision = DateTime.UtcNow + PremiereRevision;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : passage de semis en erreur."); }

            try
            {
                if (DateTime.UtcNow >= _prochaineRevision)
                {
                    _prochaineRevision = DateTime.UtcNow + CadenceRevision;
                    await ReviserLeMiroirAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : revision du miroir en erreur."); }

            // La cadence, ou plus tôt : une partie (ou une lecture) qui finit avec des replays en file.
            try
            {
                if (await _reveil.WaitAsync(Cadence, stoppingToken).ConfigureAwait(false))
                    await Task.Delay(ApresLaPartie, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>La fin d'une partie ou d'une lecture relance-t-elle la file ? Seulement si des replays y attendent.</summary>
    internal static bool Relance(string? typeEvenement, int enFile)
        => enFile > 0 && typeEvenement is "ui.game.ended" or "replay.finished";

    private void Reveiller(string type)
    {
        try
        {
            if (Relance(type, _queue.Read().Count) && _reveil.CurrentCount == 0) _reveil.Release();
        }
        catch (SemaphoreFullException)
        {
            // Un réveil est déjà en attente : il suffit.
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : relance du semis impossible."); }
    }

    /// <summary>Tentative immédiate, déclenchée par le geste de publication.</summary>
    public Task NudgeAsync(CancellationToken ct) => DrainAsync(ct);

    private async Task DrainAsync(CancellationToken ct)
    {
        var intents = _queue.Read();
        if (intents.Count == 0) return;

        if (_gameActive || _replayActive)
        {
            _logger.LogDebug("Replay : semis reporté, une partie ou une lecture est en cours ({Count} en file).", intents.Count);
            return;
        }

        foreach (var intent in intents)
        {
            if (ct.IsCancellationRequested) return;

            // 1. L'amorce l'a-t-elle déjà ? Question posée par le HASH, donc sans ambiguïté.
            if (await _publisher.IsOnSeedAsync(intent.ObjectSha256, ct).ConfigureAwait(false))
            {
                // LA PREUVE : l'amorce sert l'objet. C'est ici, et seulement ici, qu'un replay devient « mirrored ».
                MarquerProuve(intent.ReplayId);
                _queue.Complete(intent.ReplayId);
                continue;
            }

            // 2. Poussée récente : la plateforme n'a peut-être pas encore relayé. On patiente
            //    plutôt que de renvoyer plusieurs méga-octets pour rien.
            if (intent.LastPushUtc is { } pushed && DateTime.UtcNow - pushed < DelaiDeGarde) continue;

            // 3. On pousse. L'intention reste en file jusqu'à ce que l'amorce confirme.
            var result = await _publisher.PublishAsync(intent.ReplayId, ct).ConfigureAwait(false);
            _queue.Note(intent.ReplayId, result.Ok, result.Error);
            if (!result.Ok)
                _logger.LogInformation("Replay : semis de {ReplayId} non abouti ({Error}), nouvelle tentative plus tard.",
                    intent.ReplayId, result.Error);
        }
    }

    private void MarquerProuve(string replayId)
    {
        try
        {
            var meta = _meta.GetMeta(replayId);
            if (meta is not null) _meta.SaveMeta(ReplayMiroirPreuve.Prouve(meta, DateTime.UtcNow));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : etat miroir non ecrit pour {ReplayId}.", replayId); }
    }

    /// <summary>
    /// Revoit au plus <see cref="ReplayMiroirPreuve.BudgetParPassage"/> replays dits « mirrored » (HEAD, jamais de GET), les moins
    /// recemment verifies d'abord. Silencieuse pendant une partie ou une lecture. Un objet que l'amorce ne sert plus
    /// repasse en file ("to_resend") seulement si le site le veut encore (<see cref="ReplayMiroirPreuve.LeSiteLeVeut"/>) ;
    /// sinon "off_mirror". Une reponse inexploitable (reseau, 5xx) ne change rien.
    /// </summary>
    internal async Task<int> ReviserLeMiroirAsync(CancellationToken ct)
    {
        if (_gameActive || _replayActive) return 0;
        var maintenant = DateTime.UtcNow;
        var candidats = _manifests.ListManifests()
            .Select(m => (m.ReplayId, Sha256: m.Object.Sha256, Meta: _meta.GetMeta(m.ReplayId)));
        var aRevoir = ReplayMiroirPreuve.ChoisirARevoir(candidats, maintenant);
        var renvoyes = 0;
        foreach (var (replayId, sha) in aRevoir)
        {
            if (ct.IsCancellationRequested || _gameActive || _replayActive) break;
            var verdict = await _publisher.VerifierSurLeMiroirAsync(sha, ct).ConfigureAwait(false);
            var meta = _meta.GetMeta(replayId);
            if (meta is null) continue;
            var (apres, renvoyer) = ReplayMiroirPreuve.Appliquer(meta, verdict, _objects.HasObject(sha), DateTime.UtcNow);
            if (!ReferenceEquals(apres, meta)) _meta.SaveMeta(apres);
            if (renvoyer)
            {
                _queue.Enqueue(replayId, sha);
                renvoyes++;
                _logger.LogInformation("Replay : {ReplayId} n'est plus sur le miroir et le site le veut encore, renvoi en file.", replayId);
            }
            else if (verdict == VerdictMiroir.Absent)
                _logger.LogInformation("Replay : {ReplayId} n'est plus sur le miroir, le site ne le garde pas ({Etat}).", replayId, apres.PublicationState);
            try { await Task.Delay(PauseEntreVerifications, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return renvoyes;
    }

    private void OnBusEvent(EventEnvelope e)
    {
        switch (e.Type)
        {
            case "ui.game.started": _gameActive = true; break;
            case "ui.game.ended": _gameActive = false; Reveiller(e.Type); break;
            case "replay.launching":
            case "replay.started": _replayActive = true; break;
            case "replay.finished": _replayActive = false; Reveiller(e.Type); break;
        }
    }
}
