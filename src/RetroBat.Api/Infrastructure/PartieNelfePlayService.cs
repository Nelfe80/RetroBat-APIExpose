using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UNE PARTIE NELFEPLAY, OU UNE PARTIE DU JOUEUR (regle user 2026-09-27).
///
/// Hors NelfePlay, un jeu garde son propre comportement : ni scoring, ni replay. Ce qui en decide
/// est l'endroit d'ou la partie est lancee, pas le jeu : 19xx lance depuis le systeme mame est une
/// partie du joueur, le meme 19xx lance depuis la collection World Scoring est une partie NelfePlay.
///
/// - Depuis EmulationStation : le carrousel. S'il montre la collection World Scoring au game-start,
///   la partie est NelfePlay ; tout autre systeme ou collection, non. Le systeme selectionne du
///   contexte ne suffit pas : il prend celui du jeu des qu'un jeu est selectionne.
/// - Depuis une fonction NelfePlay de l'API (defi du panneau de classement, bouton du site,
///   concours) : elle l'annonce juste avant de demander le lancement a ES.
/// - Par commands/launch (HubManager, tournois, LiveContest) : il n'y a pas de carrousel ; la
///   partie est NelfePlay si le jeu est dans la collection World Scoring de la borne.
///
/// Le verdict vaut jusqu'au lancement suivant : la session du listener arrive en fin de partie,
/// parfois apres le game-end.
///
/// CARROUSEL INCONNU. ES n'annonce le carrousel que lorsqu'il change, et n'a pas de route pour le
/// demander. Une API qui redemarre pendant qu'ES est deja dans la collection (mise a jour
/// automatique au lancement, ES qui rouvre sur World Scoring) ne le connait donc pas. On juge
/// alors le jeu lui-meme : s'il est dans la collection World Scoring, la partie est NelfePlay.
/// Ne pas savoir ne doit pas faire perdre un record.
/// </summary>
public sealed class PartieNelfePlayService : IHostedService, IDisposable
{
    /// <summary>
    /// Une annonce vaut pour le lancement qui la suit, s'il la suit d'assez pres : ES peut tarder a
    /// prendre la main (plus de deux minutes mesurees quand notre panneau avait le premier plan).
    /// </summary>
    private static readonly TimeSpan ValiditeAnnonce = TimeSpan.FromMinutes(5);

    private readonly IEventBus _bus;
    private readonly MediaRuntimeState _media;
    private readonly ApiContext? _contexte;
    private readonly NelfePlayScoringCollectionSyncService? _collection;
    private readonly ILogger<PartieNelfePlayService>? _logger;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private (string Origine, DateTime At)? _annonce;
    private string? _origine;

    public PartieNelfePlayService(IEventBus bus, MediaRuntimeState media, ILogger<PartieNelfePlayService>? logger = null,
        ApiContext? contexte = null, NelfePlayScoringCollectionSyncService? collection = null)
    {
        _bus = bus;
        _media = media;
        _logger = logger;
        _contexte = contexte;
        _collection = collection;
    }

    /// <summary>La partie en cours, ou la derniere, est-elle une partie NelfePlay ?</summary>
    public bool EstNelfePlay
    {
        get
        {
            lock (_verrou)
            {
                return _origine is not null;
            }
        }
    }

    /// <summary>D'ou vient la partie NelfePlay : collection, defi, site, concours, api. Null hors NelfePlay.</summary>
    public string? Origine
    {
        get
        {
            lock (_verrou)
            {
                return _origine;
            }
        }
    }

    /// <summary>Le prochain lancement est une partie NelfePlay, quel que soit le carrousel.</summary>
    public void AnnoncerLancement(string origine)
    {
        lock (_verrou)
        {
            _annonce = (origine, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// L'origine d'une partie NelfePlay, ou null pour une partie du joueur. `dansLaCollection` ne
    /// sert que si le carrousel est inconnu.
    /// </summary>
    internal static string? Juger(string? carrousel, string? annonce, bool? dansLaCollection = null)
    {
        if (!string.IsNullOrWhiteSpace(annonce))
        {
            return annonce;
        }

        if (string.IsNullOrWhiteSpace(carrousel))
        {
            return dansLaCollection == true ? "collection (carrousel inconnu)" : null;
        }

        return string.Equals(carrousel.Trim(), NelfePlayScoringCollectionSyncService.CollectionName, StringComparison.OrdinalIgnoreCase)
            ? "collection"
            : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(OnEvenement);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    private void OnEvenement(EventEnvelope evenement)
    {
        if (!string.Equals(evenement.Type, "ui.game.started", StringComparison.Ordinal))
        {
            return;
        }

        var carrousel = _media.CarouselSystemId;
        bool? dansLaCollection = null;
        if (carrousel.Length == 0 && _contexte?.Ui.Running?.GamePath is { Length: > 0 } chemin)
        {
            dansLaCollection = _collection?.EstOuvertAuScoring(chemin);
        }

        string? origine;
        lock (_verrou)
        {
            var annonce = _annonce is { } a && DateTime.UtcNow - a.At <= ValiditeAnnonce ? a.Origine : null;
            _annonce = null;
            origine = Juger(carrousel, annonce, dansLaCollection);
            _origine = origine;
        }

        if (origine is null)
        {
            _logger?.LogInformation("Partie hors NelfePlay (carrousel {Carrousel}) : ni scoring ni replay.",
                carrousel.Length > 0 ? carrousel : "inconnu");
        }
        else
        {
            _logger?.LogInformation("Partie NelfePlay ({Origine}) : scoring et replay.", origine);
        }
    }
}
