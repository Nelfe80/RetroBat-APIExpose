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

    /// <summary>Dans la collection MULTIPLAYER, sans salle : une proposition d'un joueur suivi arrive vite.</summary>
    private static readonly TimeSpan PeriodeDansLaCollection = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// La gameroom dont le jeu demarre sur cette borne, du lancement jusqu'a l'arrivee de l'emulateur (le lanceur la pose,
    /// voir MultiplayerLaunchService) ; null sinon. Pendant ce temps, ni l'appui long ni B maintenu ne touchent a la salle :
    /// le 25/10, une salle quittee pendant un demarrage de 48 s a fait refuser la session (409), et l'invite n'a jamais pu
    /// se connecter.
    /// </summary>
    public string? LancementEnCours { get; set; }

    /// <summary>Un changement d'etat, pour le panneau.</summary>
    public event Action<MultiplayerEtat>? Change;

    /// <summary>Les propositions du dernier sondage (des joueurs suivis qui attendent), a trier par le panneau.</summary>
    public event Action<IReadOnlyList<MultiplayerProposition>>? Propositions;

    /// <summary>
    /// La borne montre-t-elle la collection MULTIPLAYER, hors partie ? Pose par le panneau, dit a chaque sondage : la
    /// plateforme ne propose un joueur, et ne propose a un joueur, que dans ce cas.
    /// </summary>
    public Func<bool>? DansLaCollection { get; set; }

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

                var attente = Active && Etat.Occupe ? PeriodeActive
                    : Active && DansLaCollection?.Invoke() == true ? PeriodeDansLaCollection
                    : PeriodeAuRepos;
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
        var reponse = await _client.RejoindreAsync(jeu.Game.Id, jeu.ContentKey, _relais?.MeilleurTempsMs, PanneauOuvert, ct,
            jeu.Game.Choices is { Count: > 0 } ? MesChoix(jeu.Game) : null).ConfigureAwait(false);
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

    // ── niveau NelfePlay : les choix du joueur ─────────────────────────────────────────────────

    private static string FichierDesChoix => Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "state", "nelfeplay", "multiplayer-choix.json");

    private Dictionary<string, Dictionary<string, int>>? _choix;

    /// <summary>
    /// Les choix du joueur pour ce jeu : ceux de sa derniere partie (« le joueur est pret avec son dernier
    /// personnage », 00quater.5), gardes sur la borne ; sinon le defaut du catalogue.
    /// </summary>
    public IReadOnlyDictionary<string, int> MesChoix(MultiplayerGame jeu)
    {
        lock (_verrou)
        {
            _choix ??= LireLesChoix();
            var gardes = _choix.TryGetValue(jeu.Id, out var siens) ? siens : null;
            return (jeu.Choices ?? []).ToDictionary(
                choix => choix.Key,
                choix => gardes is not null && gardes.TryGetValue(choix.Key, out var v) && choix.Values.Any(x => x.Value == v) ? v : choix.Default,
                StringComparer.Ordinal);
        }
    }

    /// <summary>▲▼ : le choix voisin, garde sur la borne et envoye a la gameroom. Rend la raison d'un refus, ou null.</summary>
    public async Task<string?> ChangerDeChoixAsync(string room, MultiplayerGame jeu, int pas, CancellationToken ct = default, IReadOnlySet<int>? prises = null)
    {
        if (jeu.Choices is not { Count: > 0 } choix)
        {
            return "no_choice";
        }

        var actuels = MesChoix(jeu);
        var premier = choix[0];
        var nouveaux = new Dictionary<string, int>(actuels, StringComparer.Ordinal)
        {
            [premier.Key] = premier.Distinct && prises is { Count: > 0 }
                ? premier.VoisineLibre(actuels.TryGetValue(premier.Key, out var v) ? v : premier.Default, pas, prises)
                : premier.Voisine(actuels.TryGetValue(premier.Key, out var w) ? w : premier.Default, pas),
        };
        lock (_verrou)
        {
            _choix ??= LireLesChoix();
            _choix[jeu.Id] = nouveaux;
            EcrireLesChoix(_choix);
        }

        _appui = true;
        var reponse = await _client.ChoisirAsync(room, nouveaux, ct).ConfigureAwait(false);
        Reveiller();
        return reponse is null ? "offline" : reponse.Ok ? null : reponse.Reason ?? "refused";
    }

    private static Dictionary<string, Dictionary<string, int>> LireLesChoix()
    {
        try
        {
            return File.Exists(FichierDesChoix)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(FichierDesChoix)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void EcrireLesChoix(Dictionary<string, Dictionary<string, int>> choix)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FichierDesChoix)!);
            File.WriteAllText(FichierDesChoix, System.Text.Json.JsonSerializer.Serialize(choix), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : choix non gardes");
        }
    }

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

        return _collection.JeuxConnus().FirstOrDefault(jeu => string.Equals(Path.GetFullPath(jeu.Path), cible, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Le jeu d'une gameroom sur cette borne : meme jeu ET meme contenu (un autre dump se desynchroniserait).
    /// Null quand la borne ne l'a pas.
    /// </summary>
    public MultiplayerInstalledGame? JeuDeLaRoom(MultiplayerRoom room)
        => _collection.JeuxConnus().FirstOrDefault(jeu => jeu.Game.Id == room.Game
            && string.Equals(jeu.ContentKey, room.Content, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le jeu installe sur cette borne avec ce jeu ET ce contenu, ou null : rejoindre se desynchroniserait.</summary>
    public MultiplayerInstalledGame? JeuCompatible(string jeu, string contenu)
        => _collection.JeuxConnus().FirstOrDefault(installe => installe.Game.Id == jeu
            && string.Equals(installe.ContentKey, contenu, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le nom d'un jeu du catalogue, jamais traduit ; son identifiant s'il n'est pas sur cette borne.</summary>
    public string NomDuJeu(string id)
        => _collection.JeuxConnus().FirstOrDefault(jeu => jeu.Game.Id == id)?.Game.Name ?? id;

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
            var reponse = await _client.SonderAsync(appui, PanneauOuvert, _relais?.MeilleurTempsMs, ct, DansLaCollection?.Invoke() == true).ConfigureAwait(false);
            if (reponse is null || !reponse.Ok)
            {
                // L'appui n'est pas perdu : il partira au sondage suivant.
                _appui = _appui || appui;
                Poser(Etat with { Erreur = reponse?.Reason ?? "offline" });
                return;
            }

            Poser(new MultiplayerEtat(reponse.Rooms, reponse.Invitations, null, DateTime.UtcNow, reponse.Conduct,
                reponse.Conduct is { SuspendedMs: > 0 } c ? DateTime.UtcNow.AddMilliseconds(c.SuspendedMs) : null));
            if (reponse.Proposals.Count > 0)
            {
                Propositions?.Invoke(reponse.Proposals);
            }
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
    DateTime? MisAJourUtc,
    MultiplayerConduite? Conduite = null,
    DateTime? SuspenduJusquaUtc = null)
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
