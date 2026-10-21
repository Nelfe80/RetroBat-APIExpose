using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Netplay;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// La vie MULTIPLAYER de cette borne : ses gamerooms, ses invitations, et le sondage qui les tient a
/// jour (CDC MULTIPLAYER v2.5, lot M3).
///
/// Le sondage part toutes les 2 s tant que le joueur attend, decompte ou joue (l'hote qui se tait
/// deux minutes perd sa partie), toutes les 10 s sinon, pour recevoir une invitation. Il dit a la
/// plateforme si le joueur a appuye sur un bouton depuis le dernier sondage : une minute sans appui
/// et il ne compte plus dans ses gamerooms (V24-10).
///
/// L'etat publie sur le bus (<c>multiplayer.state</c>) ne porte JAMAIS les mots de passe netplay :
/// il part vers le WebSocket et l'UDP de l'API. Seul le lancement (lot M4) lit la session complete.
/// </summary>
public sealed class MultiplayerSessionService : BackgroundService
{
    public const string EvenementEtat = "multiplayer.state";

    private static readonly TimeSpan PeriodeActive = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeriodeAuRepos = TimeSpan.FromSeconds(10);

    private readonly MultiplayerClient _client;
    private readonly MultiplayerCollectionSyncService _collection;
    private readonly NetplayRelayPicker? _relais;
    private readonly IEventBus? _bus;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MultiplayerSessionService>? _journal;
    private readonly SemaphoreSlim _sondage = new(1, 1);
    private readonly object _verrou = new();
    private readonly IDisposable? _abonnement;
    private volatile bool _appui;
    private MultiplayerEtat _etat = MultiplayerEtat.Vide;
    private CancellationTokenSource _reveil = new();

    public MultiplayerSessionService(
        MultiplayerClient client,
        MultiplayerCollectionSyncService collection,
        IOptionsMonitor<ApiExposeOptions> options,
        NetplayRelayPicker? relais = null,
        IEventBus? bus = null,
        ILogger<MultiplayerSessionService>? journal = null)
    {
        _client = client;
        _collection = collection;
        _options = options;
        _relais = relais;
        _bus = bus;
        _journal = journal;
        // Tout appui sur la borne compte comme presence : le joueur est la.
        _abonnement = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal))
            {
                _appui = true;
            }
        });
    }

    /// <summary>L'etat courant, mots de passe compris : pour le panneau et le lancement, jamais pour le bus.</summary>
    public MultiplayerEtat Etat
    {
        get { lock (_verrou) return _etat; }
    }

    /// <summary>Le panneau MULTIPLAYER est-il ouvert ? Fermer le panneau veut dire attendre depuis « Votre selection ».</summary>
    public bool PanneauOuvert { get; set; }

    /// <summary>Un changement d'etat, pour le panneau.</summary>
    public event Action<MultiplayerEtat>? Change;

    private bool Active => _options.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Active)
                {
                    await SonderAsync(stoppingToken).ConfigureAwait(false);
                }

                var attente = Active && Etat.Occupe ? PeriodeActive : PeriodeAuRepos;
                using var lie = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _reveil.Token);
                try
                {
                    await Task.Delay(attente, lie.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Reveille par une action du joueur : on sonde tout de suite.
                    lock (_verrou)
                    {
                        _reveil.Dispose();
                        _reveil = new CancellationTokenSource();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// L'APPUI LONG sur un jeu de la collection : « je veux jouer a ce jeu ». Rend la raison d'un
    /// refus (<c>not_multiplayer</c>, <c>selection_full</c>, <c>offline</c>…), ou null si le joueur
    /// est dans une gameroom.
    /// </summary>
    public async Task<string?> RejoindreAsync(string cheminDuJeu, CancellationToken ct = default)
    {
        var jeu = JeuDe(cheminDuJeu);
        if (jeu is null)
        {
            return "not_multiplayer";
        }

        if (_relais is not null && _relais.MeilleurTempsMs is null)
        {
            // La premiere mesure du relais : l'election de l'hote en a besoin (V24-12).
            await _relais.ChoisirAsync(ct).ConfigureAwait(false);
        }

        _appui = true;
        var reponse = await _client.RejoindreAsync(jeu.Game.Id, jeu.ContentKey, _relais?.MeilleurTempsMs, PanneauOuvert, ct).ConfigureAwait(false);
        if (reponse is null)
        {
            return "offline";
        }

        if (!reponse.Ok)
        {
            return reponse.Reason ?? "refused";
        }

        _journal?.LogInformation("MULTIPLAYER : {Jeu} rejoint ({Room})", jeu.Game.Id, reponse.Room);
        await SonderAsync(ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>B maintenu : le joueur quitte cette gameroom.</summary>
    public async Task<bool> QuitterAsync(string room, CancellationToken ct = default)
    {
        var reponse = await _client.QuitterAsync(room, ct).ConfigureAwait(false);
        Reveiller();
        return reponse?.Ok == true;
    }

    public async Task<string?> InviterAsync(string room, string poignee, CancellationToken ct = default)
    {
        var reponse = await _client.InviterAsync(room, poignee, ct).ConfigureAwait(false);
        return reponse is null ? "offline" : reponse.Ok ? null : reponse.Reason ?? "refused";
    }

    public async Task<string?> RepondreAsync(string invitation, bool accepte, CancellationToken ct = default)
    {
        _appui = true;
        var reponse = await _client.RepondreAsync(invitation, accepte, _relais?.MeilleurTempsMs, ct).ConfigureAwait(false);
        Reveiller();
        return reponse is null ? "offline" : reponse.Ok ? null : reponse.Reason ?? "refused";
    }

    public Task<IReadOnlyList<MultiplayerAmi>> AmisAsync(CancellationToken ct = default) => _client.AmisAsync(ct);

    /// <summary>Le jeu MULTIPLAYER d'un fichier, tel que la collection l'a reconnu, ou null.</summary>
    public MultiplayerInstalledGame? JeuDe(string cheminDuJeu)
    {
        if (string.IsNullOrWhiteSpace(cheminDuJeu))
        {
            return null;
        }

        string cible;
        try
        {
            cible = Path.GetFullPath(cheminDuJeu.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return _collection.Games.FirstOrDefault(jeu => string.Equals(Path.GetFullPath(jeu.Path), cible, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Un sondage, tout de suite. Un seul a la fois.</summary>
    internal async Task SonderAsync(CancellationToken ct)
    {
        if (!await _sondage.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var appui = _appui;
            _appui = false;
            var reponse = await _client.SonderAsync(appui, PanneauOuvert, _relais?.MeilleurTempsMs, ct).ConfigureAwait(false);
            if (reponse is null || !reponse.Ok)
            {
                // L'appui n'est pas perdu : il partira au sondage suivant.
                _appui = _appui || appui;
                Poser(Etat with { Erreur = reponse?.Reason ?? "offline" });
                return;
            }

            Poser(new MultiplayerEtat(reponse.Rooms, reponse.Invitations, null, DateTime.UtcNow));
        }
        finally
        {
            _sondage.Release();
        }
    }

    private void Poser(MultiplayerEtat etat)
    {
        bool change;
        lock (_verrou)
        {
            change = !etat.MemeAffichage(_etat);
            _etat = etat;
        }

        if (!change)
        {
            return;
        }

        Change?.Invoke(etat);
        _ = _bus?.PublishAsync(new EventEnvelope { Type = EvenementEtat, Payload = etat.SansSecrets() });
    }

    private void Reveiller()
    {
        lock (_verrou)
        {
            _reveil.Cancel();
        }
    }

    public override void Dispose()
    {
        _abonnement?.Dispose();
        _reveil.Dispose();
        _sondage.Dispose();
        base.Dispose();
    }
}

/// <summary>Ce que la borne sait de ses gamerooms et de ses invitations.</summary>
public sealed record MultiplayerEtat(
    IReadOnlyList<MultiplayerRoom> Rooms,
    IReadOnlyList<MultiplayerInvitation> Invitations,
    string? Erreur,
    DateTime? MisAJourUtc)
{
    public static MultiplayerEtat Vide => new([], [], null, null);

    /// <summary>Le joueur attend, decompte, joue, ou a une invitation : le sondage va vite.</summary>
    public bool Occupe => Rooms.Count > 0 || Invitations.Count > 0;

    /// <summary>
    /// La forme publiable : la session netplay de l'hote est remplacee par le seul fait qu'elle
    /// existe. Le bus part vers le WebSocket et l'UDP : aucun mot de passe n'y passe.
    /// </summary>
    public object SansSecrets() => new
    {
        rooms = Rooms.Select(room => new
        {
            room = room.Room,
            game = room.Game,
            level = room.Level,
            max = room.Max,
            state = room.State,
            countdown_ms = room.CountdownMs,
            players = room.Players,
            launch = room.Launch is null ? null : new
            {
                role = room.Launch.Role,
                seat = room.Launch.Seat,
                players = room.Launch.Players,
                netplay_ready = room.Launch.Netplay is not null,
            },
            ended = room.Ended,
        }).ToList(),
        invitations = Invitations,
        error = Erreur,
    };

    /// <summary>Meme chose a l'ecran ? Le decompte compte : il change a chaque sondage.</summary>
    public bool MemeAffichage(MultiplayerEtat autre)
        => Erreur == autre.Erreur
           && System.Text.Json.JsonSerializer.Serialize(Rooms) == System.Text.Json.JsonSerializer.Serialize(autre.Rooms)
           && System.Text.Json.JsonSerializer.Serialize(Invitations) == System.Text.Json.JsonSerializer.Serialize(autre.Invitations);
}
