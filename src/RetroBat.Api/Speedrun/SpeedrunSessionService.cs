using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Speedrun;

/// <summary>Le refus d'un lancement de defi, avec sa raison (unknown_challenge, game_running, not_installed...).</summary>
public sealed record ReponseLancement(bool Lance, string? Raison, string? Detail = null);

/// <summary>
/// LA SESSION DE SPEEDRUN SUR LA BORNE (lot S3 du prototype Sonic, §10 et §11 bis).
///
/// - Lancement type « speedrun » : le jeu part par emulatorLauncher avec le coeur du reglement et
///   RetroAchievements coupe pour la seance (D15). La partie n'envoie aucun score au World Scoring
///   (PartieNelfePlayService.EstSpeedrun), et une partie World Scoring n'ouvre jamais de session.
/// - L'evaluateur lit le flux du wrapper (retroarch.memory.changed), comme le rapporteur de scoring, sans rien
///   demander de plus au wrapper. Les evenements d'une meme frame sont juges ensemble (§9.5) ; l'horloge de
///   chaque evenement est celle de son emission par le wrapper, pas celle de sa reception ici.
/// - Les valeurs qu'une ligne « change » n'a pas encore emises (l'acte de Green Hill 1, les minutes a 0) sont
///   relevees par READ_CORE_RAM, aux seules adresses du .MEM citees par le reglement.
/// - Relancer : RESET de RetroArch, tentative neuve. Abandonner : tentative close, rien ne change dans le jeu.
///   A la manette (D29) : Hotkey + A relance (maintenu une seconde pendant un run), Hotkey + Start quitte.
/// - Le contrat du HUD : snapshot « speedrun.session » (retenu par le WebSocket, flux « speedrun »),
///   evenements « speedrun.* » avec la sequence de la session, commandes par SpeedrunController.
///
/// Rien n'est envoye : chaque tentative partie s'ecrit au journal local (state/speedrun/tentatives.jsonl),
/// qui donne aussi le meilleur temps du joueur.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SpeedrunSessionService : IHostedService, IDisposable
{
    /// <summary>Une annonce de lancement vaut pour le jeu qui demarre dans ce delai.</summary>
    private static readonly TimeSpan ValiditeDuLancement = TimeSpan.FromMinutes(2);

    /// <summary>Les evenements d'une frame arrivent ensemble ; sans suite apres ce delai, l'image est jugee.</summary>
    private const int AttenteDeLImageMs = 40;

    /// <summary>Hotkey + A pendant un run : maintenu une seconde, pour eviter la relance par accident (D29).</summary>
    private static readonly TimeSpan MaintienDeLaRelance = TimeSpan.FromSeconds(1);

    /// <summary>La touche Hotkey de RetroBat (Select sur la borne de reference, releve le 29/10).</summary>
    private const string Hotkey = "select";

    /// <summary>Avance rapide : les frames vont 1,5 fois plus vite que l'horloge, sur au moins 1,5 seconde.</summary>
    private const double RapportDAvanceRapide = 1.5;
    private const long FenetreDAvanceRapideMs = 1500;

    private static readonly Regex HorlogeDuWrapper = new(@"^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly ICommandesRetroArch _commandes;
    private readonly ILogger<SpeedrunSessionService> _journal;
    private readonly PartieNelfePlayService? _partie;
    private readonly CatalogueDesDefis _catalogue;
    private readonly InterfaceTextService? _textes;
    private readonly EmulationStationSettingsService? _reglages;
    private readonly string _fichierDesTentatives;
    private readonly object _verrou = new();
    private readonly List<Signal> _image = new();
    private readonly HashSet<string> _tenus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(long Frame, long HorlogeMs)> _cadence = new();

    private IDisposable? _abonnement;
    private System.Threading.Timer? _vidage;
    private SessionSpeedrun? _session;
    private SessionSpeedrun? _sessionDuLancement;
    private CancellationTokenSource? _releves;
    private (Reglement Reglement, string Rom, DateTime At)? _lancement;
    private long? _frameDeLImage;
    private long? _horlogeDeLImage;
    private CancellationTokenSource? _maintien;
    private int _requetesDeLaManette;

    public SpeedrunSessionService(IEventBus bus, ApiContext contexte, ILogger<SpeedrunSessionService> journal,
        PartieNelfePlayService? partie = null, ICommandesRetroArch? commandes = null, string? fichierDesTentatives = null,
        InterfaceTextService? textes = null, EmulationStationSettingsService? reglages = null, CatalogueDesDefis? catalogue = null)
    {
        _catalogue = catalogue ?? new CatalogueDesDefis();
        _bus = bus;
        _contexte = contexte;
        _journal = journal;
        _partie = partie;
        _textes = textes;
        _reglages = reglages;
        _commandes = commandes ?? new CommandesRetroArchUdp();
        _fichierDesTentatives = fichierDesTentatives ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "tentatives.jsonl");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        _vidage = new System.Threading.Timer(_ => ViderLImage(), null, Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _releves?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _vidage?.Dispose();
        _releves?.Cancel();
    }

    /// <summary>Le snapshot de la session en cours, ou null sans session.</summary>
    public InstantaneSpeedrun? Instantane()
    {
        lock (_verrou) return _session?.Instantane();
    }

    /// <summary>
    /// LE LANCEMENT TYPE D'UN DEFI (§10). Controles avant lancement : defi connu, aucun jeu en cours, jeu installe,
    /// .MEM en vigueur identique a celui que cite le reglement. Puis lancement direct, coeur du reglement impose,
    /// RetroAchievements coupe pour la seance.
    /// </summary>
    public ReponseLancement Lancer(string? defiId, Func<string, string, string?> trouverLaRom)
    {
        var reglement = _catalogue.Trouver(defiId);
        if (reglement is null) return new(false, "unknown_challenge");
        if (_contexte.Ui.Running is not null) return new(false, "game_running");

        var mem = RamDefinitionLayers.PickDefinitionFile(reglement.SystemId, reglement.RomGroup);
        if (mem.Length == 0 || !File.Exists(mem)) return new(false, "mem_missing", mem);
        var ecarts = reglement.VerifierContre(File.ReadAllBytes(mem));
        if (ecarts.Count > 0) return new(false, "mem_mismatch", string.Join(" ; ", ecarts));

        var rom = trouverLaRom(reglement.SystemId, reglement.RomGroup);
        if (rom is null) return new(false, "not_installed");

        var coeur = reglement.Coeurs.FirstOrDefault() is { Length: > 0 } c ? c : null;
        var arguments = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur(
            RetroBat.Api.Netplay.EsManettes.PourLancer(_journal).Arguments, reglement.SystemId, "libretro", coeur, rom, retroAchievements: false);
        if (arguments is null) return new(false, "bad_rules", "systeme ou coeur illisible");

        AttendreLeLancement(reglement, rom);
        _partie?.AnnoncerLancement(PartieNelfePlayService.OrigineSpeedrun);
        if (LancementDirect.Lancer(arguments, reglement.SystemId, rom, _contexte, _bus, _journal) is null)
        {
            lock (_verrou) _lancement = null;
            return new(false, "launcher_failed");
        }

        _journal.LogInformation("Speedrun : lancement de {Defi} ({Rom}, coeur {Coeur}, sans RetroAchievements).", reglement.ChallengeId, rom, coeur ?? "auto");
        return new(true, null);
    }

    /// <summary>Le prochain jeu qui demarre sur cette ROM ouvre une session de ce defi.</summary>
    internal void AttendreLeLancement(Reglement reglement, string rom)
    {
        lock (_verrou) _lancement = (reglement, rom, DateTime.UtcNow);
    }

    /// <summary>Une commande du HUD (ou de la manette) ; la relance demandee s'execute ici, la reponse dit « pending » jusque-la.</summary>
    public async Task<ReponseCommande> CommanderAsync(CommandeSpeedrun commande)
    {
        ReponseCommande reponse;
        EffetDemande effet;
        lock (_verrou)
        {
            if (_session is null) return new ReponseCommande(commande.RequestId, "rejected", "no_session", "NONE", 0);
            reponse = _session.Commander(commande, out effet);
            Publier();
        }

        _journal.LogInformation("Speedrun : commande {Action} ({Requete}) : {Statut}{Raison}.", commande.Action, commande.RequestId,
            reponse.Statut, reponse.Raison is null ? "" : " " + reponse.Raison);
        if (effet == EffetDemande.Relancer) await RelancerAsync(_sondeDeRetroArch).ConfigureAwait(false);
        return reponse;
    }

    /// <summary>Une adresse lue par le reglement, pour verifier que RetroArch ecoute avant RESET.</summary>
    private long _sondeDeRetroArch;

    /// <summary>
    /// RELANCER UN DEPART NORMAL : commande RESET de RetroArch (§10). RetroArch ne repond pas a RESET ; une
    /// lecture juste avant prouve qu'il ecoute. Sans reponse, la relance est refusee et le jeu reste ou il est.
    /// </summary>
    private async Task RelancerAsync(long sonde)
    {
        string? raison = null;
        try
        {
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (await _commandes.DemanderAsync($"READ_CORE_RAM {sonde:x} 1", delai.Token).ConfigureAwait(false) is null) raison = "retroarch_unreachable";
            else await _commandes.EnvoyerAsync("RESET", delai.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            raison = "retroarch_unreachable";
            _journal.LogWarning(ex, "Speedrun : relance impossible.");
        }

        lock (_verrou)
        {
            if (_session is null) return;
            ViderLImageVerrouille();
            if (raison is null) _session.RelanceFaite();
            else _session.RelanceEchouee(raison);
            _cadence.Clear();
            Publier();
        }

        _journal.LogInformation(raison is null ? "Speedrun : jeu replace au depart (RESET)." : "Speedrun : relance refusee ({Raison}).", raison);
    }

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    Ouvrir();
                    break;
                case "ui.game.ended":
                    Fermer();
                    break;
                case "retroarch.memory.changed":
                    RecevoirSignal(e.Payload);
                    break;
                case "wrapper.watch.muted":
                    SurCoupure(e.Payload);
                    break;
                case "panel.input.pressed":
                case "panel.input.released":
                    SurBouton(e.Type == "panel.input.pressed", e.Payload);
                    break;
                case "scoring.listener.session":
                    SurBilan(e.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Speedrun : evenement {Type} ignore.", e.Type);
        }
    }

    private void Ouvrir()
    {
        lock (_verrou)
        {
            var lancement = _lancement;
            _lancement = null;
            if (lancement is not { } l || DateTime.UtcNow - l.At > ValiditeDuLancement) return;
            var enCours = _contexte.Ui.Running?.GamePath;
            if (enCours is { Length: > 0 } && !string.Equals(Path.GetFullPath(enCours), Path.GetFullPath(l.Rom), StringComparison.OrdinalIgnoreCase)) return;

            _session = new SessionSpeedrun(l.Reglement.ChallengeId, l.Reglement) { MeilleurTemps = MeilleurTempsDuJournal(l.Reglement.ChallengeId) };
            _sessionDuLancement = _session;
            _sondeDeRetroArch = l.Reglement.AdressesCitees().FirstOrDefault();
            _image.Clear();
            _cadence.Clear();
            _tenus.Clear();
            _frameDeLImage = _horlogeDeLImage = null;
            _releves?.Cancel();
            _releves = new CancellationTokenSource();
            _ = ReleverEnBoucleAsync(_session, _releves.Token);
            _journal.LogInformation("Speedrun : session {Session} ouverte sur {Defi}.", _session.SessionId, _session.DefiId);
            Publier();
        }
    }

    private void Fermer()
    {
        lock (_verrou)
        {
            if (_session is null) return;
            ViderLImageVerrouille();
            _session.JeuFerme();
            Publier();
            _releves?.Cancel();
            _journal.LogInformation("Speedrun : session {Session} close (jeu ferme).", _session.SessionId);
            _ = _bus.PublishAsync(new EventEnvelope { Type = "speedrun.session.closed", Payload = new { sessionId = _session.SessionId } });
            _session = null;
        }
    }

    private void RecevoirSignal(object? payload)
    {
        lock (_verrou)
        {
            if (_session is null) return;
        }

        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (!racine.TryGetProperty("signal", out var s) || s.ValueKind != JsonValueKind.Object) return;
        if (!s.TryGetProperty("Frame", out var f) || f.ValueKind != JsonValueKind.Number) return;
        if (!s.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number) return;
        var nom = s.TryGetProperty("Name", out var n) ? n.GetString() : null;
        var adresse = s.TryGetProperty("Address", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(nom) || string.IsNullOrEmpty(adresse)) return;
        var horloge = HorlogeDeLaLigne(s.TryGetProperty("RawLine", out var r) ? r.GetString() : null, DateTimeOffset.Now);
        var signal = Signal.Depuis(f.GetInt64(), nom, adresse, v.GetInt32(), horloge);

        lock (_verrou)
        {
            if (_session is null) return;
            if (_frameDeLImage is { } frame && frame != signal.Frame) ViderLImageVerrouille();
            _frameDeLImage = signal.Frame;
            _horlogeDeLImage ??= horloge;
            _image.Add(signal);
            _vidage?.Change(AttenteDeLImageMs, Timeout.Infinite);
        }
    }

    private void ViderLImage()
    {
        lock (_verrou) ViderLImageVerrouille();
    }

    private void ViderLImageVerrouille()
    {
        if (_image.Count == 0 || _session is null) { _image.Clear(); return; }
        var image = _image.ToList();
        var horloge = _horlogeDeLImage;
        _image.Clear();
        _frameDeLImage = _horlogeDeLImage = null;
        _session.RecevoirImage(image, horloge);
        GuetterLAvanceRapide(image[0].Frame, horloge);
        Publier();
    }

    /// <summary>
    /// L'AVANCE RAPIDE : les frames filent plus vite que l'horloge. Jugee sur une fenetre d'au moins une seconde
    /// et demie, jamais d'un evenement a l'autre : ils sont espaces d'un quart de seconde en avance rapide.
    /// Un PC qui rame ralentit les frames, il ne les accelere jamais : aucun faux positif de ce cote.
    /// </summary>
    private void GuetterLAvanceRapide(long frame, long? horloge)
    {
        if (_session is null || horloge is not { } h || _session.Etat != SessionSpeedrun.Running) { _cadence.Clear(); return; }
        _cadence.Enqueue((frame, h));
        while (_cadence.Count > 2 && h - _cadence.Peek().HorlogeMs > 3 * FenetreDAvanceRapideMs) _cadence.Dequeue();
        var (f0, h0) = _cadence.Peek();
        var duree = h - h0;
        if (duree < FenetreDAvanceRapideMs) return;
        var tempsDesFrames = (frame - f0) * 1000.0 / _session.Reglement.Cadence;
        if (tempsDesFrames / duree > RapportDAvanceRapide) _session.Controle("fast_forward", frame);
    }

    /// <summary>Une ligne coupee par l'auto-mute du wrapper, nommee par son action : si le reglement la cite, plus rien n'est mesurable.</summary>
    private void SurCoupure(object? payload)
    {
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (!racine.TryGetProperty("Evenement", out var e) || e.GetString() is not { Length: > 0 } action) return;
        lock (_verrou)
        {
            if (_session is null) return;
            foreach (var adresse in _session.Reglement.AdressesDeLAction(action)) _session.CapteurCoupe(adresse);
            Publier();
        }
    }

    /// <summary>
    /// LES COMBINAISONS DE LA MANETTE (D29), lues par le lecteur d'entrees de l'API sur la manette du joueur 1 :
    /// Hotkey + A relance (aussitot avant le depart, maintenu une seconde pendant un run) ; Hotkey + Start quitte
    /// le jeu dans RetroArch, la tentative en cours est close comme abandonnee avant que le jeu ne se ferme.
    /// </summary>
    private void SurBouton(bool appui, object? payload)
    {
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (racine.TryGetProperty("Player", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() != 1) return;
        var identite = racine.TryGetProperty("Identity", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(identite)) return;

        string? action = null;
        bool enCourse;
        string sessionId;
        lock (_verrou)
        {
            if (_session is null) return;
            if (!appui)
            {
                _tenus.Remove(identite);
                _maintien?.Cancel();
                return;
            }

            _tenus.Add(identite);
            if (!_tenus.Contains(Hotkey)) return;
            if (identite.Equals("a", StringComparison.OrdinalIgnoreCase)) action = SessionSpeedrun.ActionRelancer;
            else if (identite.Equals("start", StringComparison.OrdinalIgnoreCase)) action = SessionSpeedrun.ActionAbandonner;
            enCourse = _session.Etat == SessionSpeedrun.Running;
            sessionId = _session.SessionId;
        }

        if (action is null) return;
        if (action == SessionSpeedrun.ActionAbandonner)
        {
            if (enCourse) _ = CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, action, Confirme: true));
            return;
        }

        if (!enCourse)
        {
            _ = CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, action));
            return;
        }

        CancellationTokenSource maintien;
        lock (_verrou)
        {
            _maintien?.Cancel();
            _maintien = maintien = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(MaintienDeLaRelance, maintien.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            bool tenu;
            lock (_verrou) tenu = _tenus.Contains(Hotkey) && _tenus.Contains("a");
            if (tenu) await CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, SessionSpeedrun.ActionRelancer, Confirme: true)).ConfigureAwait(false);
        });
    }

    private string IdDeLaManette() => "pad-" + Interlocked.Increment(ref _requetesDeLaManette).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// LE BILAN DU WRAPPER, a la fin de la partie : chargements d'etat, retour arriere, avance rapide, triche sur
    /// toute la seance. Il ne dit pas QUAND : il s'ecrit au journal a cote des tentatives de la session, pour le
    /// passeport (S4). Sur Sonic, un chargement en pleine course se voit deja au controle du temps du jeu.
    /// </summary>
    private void SurBilan(object? payload)
    {
        string? sessionId;
        lock (_verrou) sessionId = _session?.SessionId ?? _derniereSession;
        if (sessionId is null) return;
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        Ecrire(new JsonObject
        {
            ["kind"] = "wrapper_summary",
            ["sessionId"] = sessionId,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["summary"] = JsonNode.Parse(racine.GetRawText()),
        });
    }

    private string? _derniereSession;

    /// <summary>
    /// RELEVER CE QUE LE WRAPPER NE DIT PAS ENCORE : quatre fois par seconde, les adresses inconnues de la tentative
    /// courante et de celle qui attend, par READ_CORE_RAM. Une adresse sans reponse reste inconnue, jamais 0.
    /// </summary>
    private async Task ReleverEnBoucleAsync(SessionSpeedrun session, CancellationToken ct)
    {
        var silencieux = false;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            IReadOnlyList<long> adresses;
            lock (_verrou)
            {
                if (!ReferenceEquals(_session, session)) return;
                adresses = session.AdressesInconnues;
            }

            foreach (var adresse in adresses)
            {
                var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {adresse:x} 1", ct).ConfigureAwait(false);
                var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (morceaux is not { Length: >= 3 } || morceaux[0] != "READ_CORE_RAM"
                    || !int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur))
                {
                    if (!silencieux && reponse is not null)
                    {
                        silencieux = true;
                        _journal.LogWarning("Speedrun : READ_CORE_RAM {Adresse} refuse ({Reponse}) : le depart ne peut pas s'armer.", Hex.Ecrire(adresse), reponse.Trim());
                    }

                    continue;
                }

                lock (_verrou)
                {
                    if (!ReferenceEquals(_session, session)) return;
                    session.Relever(adresse, valeur);
                    Publier();
                }
            }
        }
    }

    /// <summary>Publie les evenements de la session, puis son snapshot s'il a change. Sous le verrou.</summary>
    private void Publier()
    {
        if (_session is null) return;
        var evenements = _session.PrendreLesEvenements();
        foreach (var e in evenements)
        {
            var donnees = JsonSerializer.SerializeToNode(e.Donnees, Json) as JsonObject ?? new JsonObject();
            donnees["sequence"] = e.Sequence;
            _ = _bus.PublishAsync(new EventEnvelope { Type = e.Type, Payload = donnees });
        }

        foreach (var (attemptId, resultat) in _session.PrendreLesCloses()) EcrireTentative(_session, attemptId, resultat);
        foreach (var message in _session.PrendreLesMessages()) _ = AfficherAsync(message);
        _derniereSession = _session.SessionId;
        if (evenements.Count > 0 || _snapshotPublie != _session.Sequence)
        {
            _snapshotPublie = _session.Sequence;
            _ = _bus.PublishAsync(new EventEnvelope { Type = "speedrun.session", Payload = JsonSerializer.SerializeToNode(_session.Instantane(), Json) });
        }
    }

    private long _snapshotPublie = -1;

    /// <summary>
    /// Le meilleur temps du lancement, une seule fois, meme apres la fermeture de la session : le bilan du wrapper
    /// peut arriver avant ou apres la fin du jeu vue par ES.
    /// </summary>
    public (Reglement Reglement, TentativeFinie Tentative)? PrendreLeMeilleurTemps()
    {
        lock (_verrou)
        {
            return _sessionDuLancement is { } s && s.PrendreLaMeilleure() is { } t ? (s.Reglement, t) : null;
        }
    }

    /// <summary>Un message au joueur dans RetroArch, dans la langue d'ES. Tant que le SpeedrunHUD n'est pas la, c'est son seul retour a l'ecran.</summary>
    private async Task AfficherAsync(MessageJoueur message)
    {
        try
        {
            var texte = TexteDe(message);
            foreach (var (nom, valeur) in message.Valeurs) texte = texte.Replace("{" + nom + "}", valeur, StringComparison.Ordinal);
            await _commandes.EnvoyerAsync("SHOW_MSG " + texte.ReplaceLineEndings(" ").Trim(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Speedrun : message {Cle} non affiche.", message.Cle);
        }
    }

    private string TexteDe(MessageJoueur message)
    {
        var texte = _textes?.Text(message.Cle, _reglages?.GetScrapingSettings().Language);
        return string.IsNullOrEmpty(texte) || texte == message.Cle ? message.ParDefaut : texte;
    }

    private void EcrireTentative(SessionSpeedrun session, string attemptId, ResultatTentative r)
    {
        var ligne = new JsonObject
        {
            ["kind"] = "attempt",
            ["sessionId"] = session.SessionId,
            ["attemptId"] = attemptId,
            ["challengeId"] = session.DefiId,
            ["rulesVersion"] = session.Reglement.Version,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["state"] = SessionSpeedrun.EtatHud(r.Etat),
            ["cause"] = r.Cause,
            ["officialFrames"] = r.FramesOfficielles,
            ["official"] = r.FramesOfficielles is { } f ? Chronometrage.Afficher(f, r.Cadence) : null,
            ["aligned"] = Mesure(r.Cale),
            ["independent"] = Mesure(r.Independant),
            ["gameTimeSeconds"] = r.TempsDuJeuSecondes,
            ["gameTimeCheck"] = r.ControleTempsDuJeu,
            ["rate"] = r.Cadence,
            ["events"] = new JsonArray(r.Journal.Select(c => (JsonNode)new JsonObject
            {
                ["role"] = c.Role, ["action"] = c.Action, ["address"] = Hex.Ecrire(c.Adresse), ["value"] = c.Valeur, ["frame"] = c.Frame,
            }).ToArray()),
        };
        Ecrire(ligne);
        _journal.LogInformation("Speedrun : tentative {Tentative} {Etat}{Cause}{Temps}.", attemptId, SessionSpeedrun.EtatHud(r.Etat),
            r.Cause is null ? "" : " (" + r.Cause + ")", r.FramesOfficielles is { } t ? " en " + Chronometrage.Afficher(t, r.Cadence) : "");

        static JsonNode? Mesure(MesureChrono? m) => m is null ? null : new JsonObject { ["start"] = m.Depart, ["end"] = m.Arrivee, ["frames"] = m.Frames };
    }

    private void Ecrire(JsonObject ligne)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fichierDesTentatives)!);
            File.AppendAllText(_fichierDesTentatives, ligne.ToJsonString() + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "Speedrun : journal des tentatives non ecrit.");
        }
    }

    /// <summary>Le meilleur temps fini de ce defi dans le journal local, en frames.</summary>
    internal long? MeilleurTempsDuJournal(string defiId)
    {
        try
        {
            if (!File.Exists(_fichierDesTentatives)) return null;
            long? meilleur = null;
            foreach (var ligne in File.ReadLines(_fichierDesTentatives))
            {
                if (ligne.Length == 0) continue;
                using var doc = JsonDocument.Parse(ligne);
                var r = doc.RootElement;
                if (r.TryGetProperty("kind", out var k) && k.GetString() == "attempt"
                    && r.TryGetProperty("challengeId", out var c) && string.Equals(c.GetString(), defiId, StringComparison.OrdinalIgnoreCase)
                    && r.TryGetProperty("state", out var s) && s.GetString() == SessionSpeedrun.Finished
                    && r.TryGetProperty("officialFrames", out var f) && f.ValueKind == JsonValueKind.Number
                    && !(r.TryGetProperty("gameTimeCheck", out var g) && g.GetString() == "ecart")
                    && (meilleur is not { } m || f.GetInt64() < m))
                    meilleur = f.GetInt64();
            }

            return meilleur;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// L'HORLOGE D'EMISSION d'une ligne du wrapper, « [hh:mm:ss.fff] » en heure locale, en millisecondes Unix. Une
    /// heure plus tard que maintenant est celle de la veille (minuit passe entre l'emission et la lecture). Sans
    /// horodatage lisible : l'heure de reception.
    /// </summary>
    internal static long HorlogeDeLaLigne(string? ligne, DateTimeOffset maintenant)
    {
        var m = ligne is null ? null : HorlogeDuWrapper.Match(ligne);
        if (m is not { Success: true }) return maintenant.ToUnixTimeMilliseconds();
        var heure = new TimeSpan(0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
        var emission = new DateTimeOffset(maintenant.Date + heure, maintenant.Offset);
        if (emission - maintenant > TimeSpan.FromHours(1)) emission = emission.AddDays(-1);
        return emission.ToUnixTimeMilliseconds();
    }
}
