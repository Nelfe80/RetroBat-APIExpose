using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NelfePlay.VocalRoom.Protocol;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.VocalRoom;

/// <summary>
/// LE PROCESSUS DU VOCAL (CDC NelfeVoice v1.3 §4, §6, §9.3 ; lot B du 12/11/2026) : <c>NelfePlay-VocalRoom.exe</c>, lance
/// par l'API seulement quand une session vocale est autorisee (jamais en simple navigation dans ES), pilote sur un named
/// pipe, arrete a la fin de la session.
///
/// Le lancement : pipe reserve au compte, secret sur l'entree standard, preuve de l'exe verifiee, protocole et capacites
/// verifies, <c>health</c> lu. Un exe incompatible est refuse SANS jamais bloquer le jeu : le vocal est alors
/// « indisponible », rien d'autre. Apres un plantage, l'exe est relance un nombre limite de fois tant qu'une session le
/// veut, puis abandonne. Les evenements <c>voice.state</c> de l'exe remontent a l'interface (bus de l'API, panneau).
///
/// Rien ici n'attend le reseau de Discord ; aucun appel ne leve vers l'appelant.
/// </summary>
public sealed class VocalRoomProcessService : IHostedService, IAsyncDisposable
{
    /// <summary>L'evenement publie sur le bus (WebSocket, UDP) a chaque changement d'etat du vocal.</summary>
    public const string EvenementEtat = "voice.state";

    internal static readonly TimeSpan DelaiDeHello = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DelaiDeCommande = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DelaiDArret = TimeSpan.FromSeconds(3);

    private readonly IOptionsMonitor<ApiExposeOptions>? _options;
    private readonly IVocalRoomLanceur _lanceur;
    private readonly IEventBus? _bus;
    private readonly ILogger<VocalRoomProcessService>? _journal;
    private readonly string _racine;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private readonly object _verrou = new();
    private IVocalRoomProcessus? _processus;
    private VocalRoomEtat _etat = VocalRoomEtat.Arrete;
    private int _numero;
    private bool _voulu;
    private int _relances;
    private bool _arretDeLApi;
    private int _lancements;

    public VocalRoomProcessService(
        IOptionsMonitor<ApiExposeOptions> options,
        IVocalRoomLanceur lanceur,
        IEventBus? bus = null,
        ILogger<VocalRoomProcessService>? journal = null)
        : this(lanceur, RetroBatPaths.PluginRoot, options, bus, journal)
    {
    }

    internal VocalRoomProcessService(
        IVocalRoomLanceur lanceur,
        string racine,
        IOptionsMonitor<ApiExposeOptions>? options = null,
        IEventBus? bus = null,
        ILogger<VocalRoomProcessService>? journal = null)
    {
        _lanceur = lanceur;
        _racine = racine;
        _options = options;
        _bus = bus;
        _journal = journal;
    }

    /// <summary>Les pauses avant chaque relance apres un plantage (1 s, 3 s, 9 s...). Reglable par les tests.</summary>
    internal Func<int, TimeSpan> PauseAvantRelance { get; set; } = n => TimeSpan.FromSeconds(Math.Pow(3, Math.Max(0, n - 1)));

    /// <summary>L'etat courant, sans secret.</summary>
    public VocalRoomEtat Etat
    {
        get { lock (_verrou) return _etat; }
    }

    /// <summary>Un changement d'etat (processus ou voix), pour le coordinateur et le panneau.</summary>
    public event Action<VocalRoomEtat>? Change;

    /// <summary>L'exe vient d'etre relance apres un plantage : la session doit lui redonner jeton et salon.</summary>
    public event Action? Relance;

    /// <summary>L'exe tourne-t-il, appaire ?</summary>
    public bool Tourne
    {
        get
        {
            lock (_verrou) return _processus is not null && _etat.Processus == VocalRoomEtat.EnMarche;
        }
    }

    private int MaxRelances => Math.Max(0, _options?.CurrentValue.NelfePlay.Voice.MaxRestarts ?? 3);

    /// <summary>
    /// Le numero du lancement en cours (augmente a chaque exe lance et appaire). Un jeton donne a un lancement ne vaut pas
    /// pour le suivant : l'exe relance a perdu son compte.
    /// </summary>
    public int Lancement
    {
        get { lock (_verrou) return _lancements; }
    }

    /// <summary>Une nouvelle session vocale commence : le compte des relances repart de zero.</summary>
    public void NouvelleSession()
    {
        lock (_verrou) _relances = 0;
    }

    /// <summary>
    /// Lance l'exe s'il ne tourne pas (une session vocale autorisee le veut). Rend vrai s'il tourne, appaire et
    /// compatible ; faux sinon, l'etat dit pourquoi (<see cref="VocalRoomEtat.Raison"/>).
    /// </summary>
    public async Task<bool> DemarrerAsync(CancellationToken ct)
    {
        try
        {
            await _porte.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (_arretDeLApi) return false;
            _voulu = true;
            if (Tourne) return true;
            return await LancerAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _porte.Release();
        }
    }

    /// <summary>
    /// Envoie une commande a l'exe et attend sa reponse. Null si l'exe ne tourne pas, ne repond pas dans le delai ou que
    /// le pipe se ferme : jamais d'exception vers l'appelant.
    /// </summary>
    public async Task<JsonObject?> CommanderAsync(string commande, JsonObject? charge = null, TimeSpan? delai = null)
    {
        IVocalRoomProcessus? p;
        lock (_verrou) p = _etat.Processus == VocalRoomEtat.EnMarche ? _processus : null;
        if (p is null) return null;
        try
        {
            return await p.Connexion.SendAsync(commande, charge, delai ?? DelaiDeCommande).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException
                                       or OperationCanceledException or FramingException)
        {
            _journal?.LogInformation("VOCAL : {Commande} sans reponse ({Type})", commande, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Fin de la session vocale : <c>shutdown</c>, attente de la sortie, puis arret force en dernier recours (§5). Plus
    /// de relance ensuite.
    /// </summary>
    public async Task ArreterAsync(string raison)
    {
        await _porte.WaitAsync().ConfigureAwait(false);
        try
        {
            _voulu = false;
            await ArreterSansPorteAsync(raison).ConfigureAwait(false);
        }
        finally
        {
            _porte.Release();
        }
    }

    private async Task ArreterSansPorteAsync(string raison)
    {
        IVocalRoomProcessus? p;
        lock (_verrou)
        {
            p = _processus;
            _processus = null;
            _numero++;
        }

        if (p is null)
        {
            if (Etat.Processus is VocalRoomEtat.EnMarche or VocalRoomEtat.Demarrage) Poser(VocalRoomEtat.Arrete with { Raison = raison });
            return;
        }

        try
        {
            await p.Connexion.SendAsync(Commands.Shutdown, null, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // L'exe ne repond plus : la sortie forcee suit.
        }

        var code = await p.AttendreLaSortieAsync(DelaiDArret).ConfigureAwait(false);
        if (code is null)
        {
            p.Tuer();
            _journal?.LogWarning("VOCAL : NelfePlay-VocalRoom ne s'arretait pas, arret force (pid {Pid})", p.Pid);
        }

        await LibererAsync(p).ConfigureAwait(false);
        _journal?.LogInformation("VOCAL : NelfePlay-VocalRoom arrete ({Raison}, code {Code})", raison, code?.ToString() ?? "force");
        Poser(VocalRoomEtat.Arrete with { Raison = raison, Relances = Etat.Relances });
    }

    private async Task<bool> LancerAsync(CancellationToken ct)
    {
        var exe = VocalRoomFichiers.CheminDeLExe(_racine);
        if (exe is null || !VocalRoomFichiers.LotComplet(_racine))
        {
            Poser(Indisponible("exe_missing"));
            return false;
        }

        Poser(Etat with { Processus = VocalRoomEtat.Demarrage, Voix = "STARTING", Raison = null });
        IVocalRoomProcessus? p = null;
        try
        {
            p = await _lanceur.LancerAsync(exe, DossierDuJournal(), ct).ConfigureAwait(false);
            var hello = await p.Connexion.HelloAsync(DelaiDeHello).ConfigureAwait(false);
            var refus = Verifier(hello, p.Connexion.ClientProofValid);
            if (refus is not null)
            {
                _journal?.LogWarning("VOCAL : NelfePlay-VocalRoom refuse ({Raison}) : le vocal est indisponible, la partie continue", refus);
                await AbandonnerAsync(p).ConfigureAwait(false);
                Poser(Indisponible(refus));
                return false;
            }

            var sante = await p.Connexion.SendAsync(Commands.Health, null, DelaiDeCommande).ConfigureAwait(false);
            if (Messages.Bool(sante, "ok") != true || Messages.Bool(Messages.Obj(sante, "result"), "alive") != true)
            {
                await AbandonnerAsync(p).ConfigureAwait(false);
                Poser(Indisponible("health_failed"));
                return false;
            }

            var resultat = Messages.Obj(hello, "result");
            int numero;
            lock (_verrou)
            {
                _processus = p;
                numero = ++_numero;
                _lancements++;
            }

            p.Connexion.EventReceived += e => SurEvenement(numero, e);
            _ = SurveillerAsync(numero, p);
            _journal?.LogInformation("VOCAL : NelfePlay-VocalRoom {Version} lance (pid {Pid}, SDK {Sdk}, protocole {Protocole})",
                Messages.Str(resultat, "app_version"), p.Pid, Messages.Str(resultat, "sdk_version") ?? "-", Messages.Long(resultat, "protocol"));
            Poser(Etat with
            {
                Processus = VocalRoomEtat.EnMarche,
                Voix = Messages.Str(Messages.Obj(sante, "result"), "state") ?? Messages.Str(resultat, "state") ?? "STARTING",
                Raison = null,
                DerniereErreur = null,
                VersionExe = Messages.Str(resultat, "app_version"),
                VersionSdk = Messages.Str(resultat, "sdk_version"),
            });
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _journal?.LogWarning("VOCAL : lancement de NelfePlay-VocalRoom impossible ({Type}) : le vocal est indisponible, la partie continue",
                ex.GetType().Name);
            if (p is not null) await AbandonnerAsync(p).ConfigureAwait(false);
            Poser(Indisponible(ex is OperationCanceledException ? "cancelled" : "launch_failed"));
            return false;
        }
    }

    /// <summary>
    /// La reponse a <c>hello</c> autorise-t-elle a piloter cet exe ? Null si oui, sinon la raison du refus : preuve fausse
    /// (ce n'est pas notre exe), protocole incompatible, exe trop ancien (capacites ou version), SDK absent. Pur.
    /// </summary>
    internal static string? Verifier(JsonObject? hello, bool preuveValide)
    {
        if (hello is null) return "no_hello";
        if (Messages.Bool(hello, "ok") != true)
        {
            return Messages.Str(Messages.Obj(hello, "error"), "code") == ErrorCodes.UnsupportedVersion ? "protocol_incompatible" : "pairing_refused";
        }

        if (!preuveValide) return "pairing_failed";
        var r = Messages.Obj(hello, "result");
        if (Messages.Long(r, "protocol") != ProtocolInfo.Version) return "protocol_incompatible";

        var capacites = r?["capabilities"] is JsonArray liste
            ? liste.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).Where(s => s is not null).ToHashSet(StringComparer.Ordinal)
            : [];
        if (VocalRoomProtocolSource.CapacitesRequises.Any(c => !capacites.Contains(c))) return "exe_too_old";
        if (VersionDe(Messages.Str(r, "app_version")) is not { } version || version < VocalRoomProtocolSource.VersionMinimale) return "exe_too_old";
        if (string.IsNullOrEmpty(Messages.Str(r, "sdk_version"))) return "sdk_unavailable";
        return null;
    }

    /// <summary>« 0.2.0+20261112.f3f0095 » → 0.2.0.</summary>
    internal static Version? VersionDe(string? texte)
    {
        var tete = (texte ?? string.Empty).Split('+', '-')[0];
        return Version.TryParse(tete, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    private async Task SurveillerAsync(int numero, IVocalRoomProcessus p)
    {
        try
        {
            await Task.WhenAny(p.Connexion.Closed, p.Sortie).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        TimeSpan? pause = null;
        await _porte.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_verrou)
            {
                if (numero != _numero || !ReferenceEquals(_processus, p))
                {
                    return;   // arret demande : rien a dire
                }

                _processus = null;
                _numero++;
            }

            var code = await p.AttendreLaSortieAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (code is null) p.Tuer();
            await LibererAsync(p).ConfigureAwait(false);
            _journal?.LogWarning("VOCAL : NelfePlay-VocalRoom s'est arrete seul (code {Code})", code?.ToString() ?? "?");

            int relances;
            lock (_verrou) relances = _relances;
            if (_voulu && !_arretDeLApi && relances < MaxRelances)
            {
                lock (_verrou) _relances = ++relances;
                pause = PauseAvantRelance(relances);
                Poser(Etat with { Processus = VocalRoomEtat.Demarrage, Voix = "STARTING", Raison = "restarting", Relances = relances, Connecte = false, Participants = 0 });
            }
            else
            {
                Poser(Indisponible(_voulu ? "crashed" : "stopped") with { Relances = relances });
            }
        }
        finally
        {
            _porte.Release();
        }

        if (pause is not { } attente) return;
        await Task.Delay(attente).ConfigureAwait(false);
        bool relance;
        await _porte.WaitAsync().ConfigureAwait(false);
        try
        {
            relance = _voulu && !_arretDeLApi && !Tourne && await LancerAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _porte.Release();
        }

        if (relance)
        {
            _journal?.LogInformation("VOCAL : NelfePlay-VocalRoom relance apres un plantage ({N}/{Max})", Etat.Relances, MaxRelances);
            try { Relance?.Invoke(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _journal?.LogDebug(ex, "VOCAL : reprise apres relance"); }
        }
    }

    private void SurEvenement(int numero, JsonObject e)
    {
        lock (_verrou)
        {
            if (numero != _numero) return;
        }

        var nom = Messages.Str(e, "event");
        var charge = Messages.Obj(e, "payload");
        if (nom == Events.VoiceState)
        {
            Poser(Etat with
            {
                Voix = Messages.Str(charge, "state") ?? Etat.Voix,
                Room = Messages.Str(charge, "room_id"),
                Generation = Messages.Long(charge, "generation"),
                Connecte = Messages.Bool(charge, "connected") ?? false,
                MicroCoupe = Messages.Bool(charge, "mic_muted") ?? Etat.MicroCoupe,
                CasqueCoupe = Messages.Bool(charge, "deafened") ?? Etat.CasqueCoupe,
                Participants = (int)(Messages.Long(charge, "participants") ?? 0),
                Raison = Messages.Str(charge, "reason"),
            });
        }
        else if (nom == Events.VoiceError)
        {
            var code = Messages.Str(charge, "code");
            _journal?.LogInformation("VOCAL : erreur signalee par l'exe ({Code})", code ?? "?");
            Poser(Etat with { DerniereErreur = code });
        }
    }

    private VocalRoomEtat Indisponible(string raison) => Etat with
    {
        Processus = VocalRoomEtat.Indisponible,
        Voix = "STOPPED",
        Raison = raison,
        Connecte = false,
        Participants = 0,
    };

    private void Poser(VocalRoomEtat etat)
    {
        lock (_verrou)
        {
            if (etat == _etat) return;
            _etat = etat;
        }

        try { Change?.Invoke(etat); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _journal?.LogDebug(ex, "VOCAL : abonne de l'etat"); }
        _ = _bus?.PublishAsync(new EventEnvelope { Type = EvenementEtat, Payload = etat.PourLeBus() });
    }

    private async Task AbandonnerAsync(IVocalRoomProcessus p)
    {
        p.Tuer();
        await LibererAsync(p).ConfigureAwait(false);
    }

    private static async Task LibererAsync(IVocalRoomProcessus p)
    {
        try { await p.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private string? DossierDuJournal()
    {
        try
        {
            var dossier = Path.Combine(_racine, ".log", "vocalroom");
            Directory.CreateDirectory(dossier);
            return dossier;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>L'API s'arrete : l'exe aussi (il le ferait seul en perdant le pipe, on le lui dit d'abord).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _arretDeLApi = true;
        try
        {
            await ArreterAsync("api_stopping").WaitAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        IVocalRoomProcessus? p;
        lock (_verrou)
        {
            p = _processus;
            _processus = null;
            _numero++;
        }

        if (p is not null) await AbandonnerAsync(p).ConfigureAwait(false);
    }
}

/// <summary>
/// L'etat du vocal tel que l'API le connait : le processus (<see cref="Processus"/>) et ce que l'exe a observe
/// (<see cref="Voix"/>, un etat du §11 : STOPPED, STARTING, AUTH_REQUIRED, READY, LOBBY_PENDING, CONNECTING, IN_CALL,
/// RECONNECTING, DETACHING, ERROR). Jamais de jeton ni de secret.
/// </summary>
public sealed record VocalRoomEtat(
    string Processus,
    string Voix,
    string? Raison = null,
    string? Room = null,
    long? Generation = null,
    bool Connecte = false,
    bool MicroCoupe = true,
    bool CasqueCoupe = false,
    int Participants = 0,
    string? VersionExe = null,
    string? VersionSdk = null,
    int Relances = 0,
    string? DerniereErreur = null)
{
    public const string Arret = "stopped";
    public const string Demarrage = "starting";
    public const string EnMarche = "running";
    public const string Indisponible = "unavailable";

    public static readonly VocalRoomEtat Arrete = new(Arret, "STOPPED");

    /// <summary>Ce qui part sur le bus (WebSocket et UDP de l'API) : etat et compteurs, sans lobby ni jeton.</summary>
    public object PourLeBus() => new
    {
        process = Processus,
        state = Voix,
        reason = Raison,
        room = Room,
        generation = Generation,
        connected = Connecte,
        mic_muted = MicroCoupe,
        deafened = CasqueCoupe,
        participants = Participants,
        exe_version = VersionExe,
        sdk_version = VersionSdk,
        restarts = Relances,
        last_error = DerniereErreur,
    };
}
