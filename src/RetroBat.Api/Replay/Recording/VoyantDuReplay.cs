using System.Globalization;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Recording;

/// <summary>
/// LE VOYANT DU REPLAY (16/11, Old AF Gamer : « je ne sais plus si ça enregistre et quand »). L'API n'avait aucun voyant
/// d'enregistrement : le joueur ne le savait que par les messages de RetroArch, coupes pour toute partie NelfePlay depuis la
/// 1.9.170 (<see cref="NotificationsRetroArch"/>). Trois annonces, par la voie des bandeaux du scoring
/// (<see cref="LiveContestOverlayService.ShowTop"/> : la plaque du PlayerDeck s'il est pose, sinon le bandeau du haut) :
/// <list type="bullet">
/// <item>au DEBUT de l'enregistrement, un bandeau court (le moment du depart, comme le bandeau du prevol) ;</item>
/// <item>s'il S'ARRETE AVANT LA FIN DE LA PARTIE (le jeu tourne, RetroArch n'enregistre plus), un bandeau au premier temps
/// mort que le .MEM signale (continue, game over, titre), jamais pendant le jeu engage ; sans temps mort avant la fin du jeu,
/// une notification d'EmulationStation au retour au menu ;</item>
/// <item>UNE FOIS par jeu et par demarrage de l'API, au depart, quand le jeu ne s'enregistre pas sur cette borne (liste
/// « ne s'enregistre plus » apres un plantage de RetroArch, ou coeur que RetroArch 1.22.2 ne sait pas enregistrer), avec le
/// motif.</item>
/// </list>
/// Rien pour les epreuves de speedrun et les challenges (leurs relances ouvrent un replay a chaque tentative, leur HUD dit
/// deja la tentative), ni pour la suite a plusieurs (1CC MULTI). Textes : <see cref="CabinetAnnounceText"/>, six langues.
/// </summary>
public sealed class VoyantDuReplay : IHostedService, IDisposable
{
    // Les raisons d'arret que l'enregistreur publie avec « replay.recording.stopped ».
    public const string RaisonFin = "fin";
    public const string RaisonContinue = "continue";
    public const string RaisonNouvellePartie = "nouvelle_partie";
    public const string RaisonRelance = "relance";
    public const string RaisonInterrompu = "interrompu";

    // Les motifs d'un jeu qui ne s'enregistre pas (« replay.recording.skipped »).
    public const string MotifPlantage = "plantage";
    public const string MotifRetroArch = "retroarch";

    /// <summary>La marque des bandeaux du replay.</summary>
    public const string Marque = "REPLAY";

    /// <summary>Un etat de jeu plus vieux ne dit plus si le joueur est engage (meme regle que NelfePlayMessageRouter).</summary>
    private static readonly TimeSpan EtatPerime = TimeSpan.FromMinutes(2);

    /// <summary>Une annonce, telle que le bandeau la montre.</summary>
    public sealed record Annonce(string Cle, string Titre, string Detail, int DureeMs, bool Alerte, RetroBat.Api.Deck.DeckNotice AuDeck);

    private readonly IEventBus _bus;
    private readonly ILogger<VoyantDuReplay>? _logger;
    private readonly Action<Annonce> _afficher;
    private readonly Func<string, Task> _notifierEs;
    private readonly Func<bool?> _gameplayActif;
    private readonly Func<string> _langue;
    private readonly object _verrou = new();
    private readonly HashSet<string> _jeuxDitsSansReplay = new(StringComparer.OrdinalIgnoreCase);
    private Annonce? _arretADire;
    private IDisposable? _abonnement;
    private System.Threading.Timer? _releve;

    public VoyantDuReplay(IEventBus bus, ILogger<VoyantDuReplay>? logger = null, LiveContestOverlayService? bandeau = null,
        IngameGameplayStateService? jeu = null, IEmulationStationNotificationService? es = null, IEsSettingsStore? reglagesEs = null,
        NelfePlayScoringSessionService? session = null)
        : this(bus, logger,
            a => bandeau?.ShowTop(Marque, a.Titre, a.Detail, a.DureeMs, a.Alerte, a.AuDeck),
            texte => es?.NotifyAsync(texte) ?? Task.CompletedTask,
            () => GameplayActif(jeu, DateTime.UtcNow),
            () => Langue(reglagesEs, session))
    {
    }

    /// <summary>Couture des essais : l'affichage, la notification d'ES, l'etat du jeu et la langue sont donnes.</summary>
    internal VoyantDuReplay(IEventBus bus, ILogger<VoyantDuReplay>? logger, Action<Annonce> afficher, Func<string, Task> notifierEs,
        Func<bool?> gameplayActif, Func<string> langue)
    {
        _bus = bus; _logger = logger; _afficher = afficher; _notifierEs = notifierEs; _gameplayActif = gameplayActif; _langue = langue;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Voyant du replay : abonnement au bus impossible."); }
        _releve = new System.Threading.Timer(_ => Relever(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _releve?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _releve?.Dispose();
    }

    /// <summary>
    /// Le replay s'arrete-t-il AVANT la fin de la partie ? Oui quand le jeu enregistre est toujours charge et que RetroArch dit
    /// ne plus l'enregistrer ; un jeu ferme ou change, ou une reponse absente, est une fin. Pur.
    /// </summary>
    internal static bool Interrompu(bool? contenuCharge, string? jeuCharge, string jeuEnregistre, bool? enregistre)
        => contenuCharge == true && string.Equals(jeuCharge, jeuEnregistre, StringComparison.Ordinal) && enregistre == false;

    /// <summary>
    /// Le joueur est-il engage dans sa partie ? Null quand on n'en sait rien (aucun marqueur de cycle de vie, ou un etat trop
    /// vieux) : on se tait alors, comme <see cref="NelfePlayMessageRouter"/>.
    /// </summary>
    internal static bool? GameplayActif(IngameGameplayStateService? jeu, DateTime maintenant)
    {
        var etat = jeu?.GetSnapshot();
        if (etat is null || etat.StateName.Length == 0 || etat.LastUpdatedUtc == DateTime.MinValue) return null;
        return maintenant - etat.LastUpdatedUtc > EtatPerime ? null : etat.IsGameplayActive;
    }

    private static string Langue(IEsSettingsStore? reglagesEs, NelfePlayScoringSessionService? session)
    {
        string? es = null;
        try
        {
            if (reglagesEs is not null && reglagesEs.ReadAllSettings().TryGetValue("Language", out var langue)) es = langue;
        }
        catch (Exception)
        {
            // Des reglages illisibles : la langue du joueur, sinon l'anglais.
        }
        string? joueur = null;
        try { joueur = session?.Get()?.Locale; }
        catch (Exception) { /* sans session, la borne */ }
        return CabinetAnnounceText.Resolve(joueur, es);
    }

    /// <summary>Le bandeau du debut de l'enregistrement.</summary>
    internal static Annonce Debut(string langue)
        => new("replay_rec", CabinetAnnounceText.Get("replay_rec_title", langue), CabinetAnnounceText.Get("replay_rec_sub", langue),
            RetroBat.Api.Deck.DeckNotices.Court, false,
            RetroBat.Api.Deck.DeckNotices.Message("replay.rec", RetroBat.Api.Deck.DeckSeverity.Info, "REC", "REPLAY RECORDING", "replay",
                RetroBat.Api.Deck.DeckNotices.Court));

    /// <summary>Le bandeau d'un replay arrete avant la fin de la partie.</summary>
    internal static Annonce Arret(string langue)
        => new("replay_stopped", CabinetAnnounceText.Get("replay_stopped_title", langue), CabinetAnnounceText.Get("replay_stopped_sub", langue),
            RetroBat.Api.Deck.DeckNotices.Long, true,
            RetroBat.Api.Deck.DeckNotices.Message("replay.stopped", RetroBat.Api.Deck.DeckSeverity.Warn, "REPLAY STOPPED",
                "REST OF GAME NOT RECORDED", "replay", RetroBat.Api.Deck.DeckNotices.Long));

    /// <summary>
    /// Le bandeau d'un jeu qui ne s'enregistre pas sur cette borne, avec son motif. Apres un plantage, la date du prochain
    /// essai (16/11 : l'inscription expire, <see cref="GardeDesPlantages"/>) quand l'enregistreur la donne.
    /// </summary>
    internal static Annonce SansReplay(string langue, string? motif, DateTime? plantage, DateTime? prochainEssai = null)
    {
        static string Jour(DateTime d) => d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var detail = motif == MotifPlantage && plantage is { } le
            ? prochainEssai is { } essai
                ? string.Format(CultureInfo.InvariantCulture, CabinetAnnounceText.Get("replay_off_crash_retry_sub", langue), Jour(le), Jour(essai))
                : string.Format(CultureInfo.InvariantCulture, CabinetAnnounceText.Get("replay_off_crash_sub", langue), Jour(le))
            : CabinetAnnounceText.Get("replay_off_retroarch_sub", langue);
        return new("replay_off", CabinetAnnounceText.Get("replay_off_title", langue), detail, RetroBat.Api.Deck.DeckNotices.Long, true,
            RetroBat.Api.Deck.DeckNotices.Message("replay.off", RetroBat.Api.Deck.DeckSeverity.Warn, "NO REPLAY",
                motif == MotifPlantage ? "RETROARCH CRASHED RECORDING IT" : "RETROARCH CANNOT RECORD IT", "replay",
                RetroBat.Api.Deck.DeckNotices.Long));
    }

    internal void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    lock (_verrou) _arretADire = null;
                    break;
                case "replay.recording.started":
                {
                    var p = JsonSerializer.SerializeToElement(e.Payload);
                    if (Booleen(p, "Speedrun") == true || Texte(p, "Categorie") is { Length: > 0 }) return;
                    lock (_verrou) _arretADire = null;
                    Dire(Debut(_langue()));
                    break;
                }
                case "replay.recording.stopped":
                {
                    var p = JsonSerializer.SerializeToElement(e.Payload);
                    if (Texte(p, "Raison") != RaisonInterrompu) return;
                    lock (_verrou) _arretADire = Arret(_langue());
                    _logger?.LogInformation("Voyant du replay : replay interrompu avant la fin de la partie, annonce au prochain temps mort.");
                    Relever();
                    break;
                }
                case "replay.recording.skipped":
                {
                    var p = JsonSerializer.SerializeToElement(e.Payload);
                    var jeu = (Texte(p, "System") ?? "") + "/" + (Texte(p, "Game") ?? "");
                    lock (_verrou)
                    {
                        if (!_jeuxDitsSansReplay.Add(jeu)) return;
                    }
                    Dire(SansReplay(_langue(), Texte(p, "Motif"), Date(p, "Le"), Date(p, "Jusqua")));
                    break;
                }
                case "ui.game.ended":
                {
                    Annonce? reste;
                    lock (_verrou) { reste = _arretADire; _arretADire = null; }
                    // Pas de temps mort avant la fin du jeu : le joueur l'apprend au menu, par EmulationStation.
                    if (reste is not null) _ = _notifierEs(reste.Titre + " : " + reste.Detail);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Voyant du replay : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>Dit l'arret en attente si le joueur est dans un temps mort (le .MEM le dit, et l'etat est frais).</summary>
    internal void Relever()
    {
        Annonce? aDire;
        lock (_verrou) aDire = _arretADire;
        if (aDire is null || _gameplayActif() != false) return;
        lock (_verrou)
        {
            if (!ReferenceEquals(_arretADire, aDire)) return;
            _arretADire = null;
        }
        Dire(aDire);
    }

    private void Dire(Annonce a)
    {
        try { _afficher(a); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Voyant du replay : bandeau {Cle} non montre.", a.Cle); }
    }

    private static string? Texte(JsonElement p, string nom)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Booleen(JsonElement p, string nom)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nom, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static DateTime? Date(JsonElement p, string nom)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
           && v.TryGetDateTime(out var d) ? d : null;
}
