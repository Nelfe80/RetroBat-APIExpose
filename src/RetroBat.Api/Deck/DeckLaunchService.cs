using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE LANCEMENT AVEC LE PLAYERDECK (CDC PlayerDeck H-D15 a H-D20).
///
/// QUAND : seulement pour les lancements faits par l'API depuis les boutons speciaux des panneaux (CHALLENGE du panneau
/// World Scoring, LAUNCH THE SPEEDRUN / LAUNCH THE CHALLENGE du panneau Challenges &amp; Speedruns, la presence dans une room
/// MULTIPLAYER), et seulement si le jeu a au moins un Deck installe. Aucune ligne dans le panneau d'ES. Actif par defaut
/// quand le jeu a un Deck officiel.
///
/// QUOI : avant le lanceur, l'API resout le Deck (choix du joueur pour ce jeu et ce mode, sinon le Deck officiel), fait
/// rendre son fond par le PlayerDeck lui-meme (<c>--render-decor</c>, a la taille de l'ecran, garde en cache), l'ecrit
/// comme decor de RetroBat <c>decorations/nelfeplay-deck</c> avec son <c>.info</c> de marges, et rend les arguments
/// <c>-bezel nelfeplay-deck -ratio custom -integerscale 0</c> a ajouter a la ligne du lanceur. RetroArch dessine le fond,
/// cale le jeu dans le trou ; la fenetre du Deck n'ecrit que les valeurs, alignees sur le meme trou (H-D8).
///
/// LE CHOIX EN JEU : Select + A (appui court, <see cref="DeckGeste"/>) ou Ctrl+Alt+D dans le Deck (message
/// <c>deck.choice.next</c>) : Deck suivant, puis Off. Retenu par joueur, jeu et mode (<see cref="DeckChoix"/>). RetroArch ne
/// change pas de decor en pleine partie (pas de commande reseau pour cela : seule OVERLAY_NEXT existe, et le lanceur ne pose
/// qu'une image) : le decor change au lancement suivant ; tout de suite, Off cache la fenetre du Deck, et un Deck choisi
/// alors qu'aucun decor n'est pose se dessine en entier dans la fenetre. Un message de RetroArch le dit au joueur.
///
/// LA CERTIFICATION NE CHANGE PAS : le decor et le cadre de l'image sont des reglages d'affichage, hors de l'empreinte
/// (ni coeur, ni contenu, ni .MEM, ni options de coeur filtrees, ni listener). Rien de ce qui est ici n'ecrit dans
/// es_settings.cfg ni dans retroarch.cfg.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeckLaunchService : IHostedService, IDisposable
{
    public const string NomDuHost = "NelfePlay.PlayerDeck";

    /// <summary>Un rendu du decor ne doit pas retarder le lancement au-dela (mesure : 40 a 60 ms de rendu, 120 a 280 ms de processus).</summary>
    private static readonly TimeSpan AttenteDuRendu = TimeSpan.FromSeconds(5);

    /// <summary>Une fin de partie qui arrive juste apres la preparation du lancement suivant n'efface pas le decor neuf.</summary>
    private static readonly TimeSpan GardeDuDecor = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly DeckStateService? _etat;
    private readonly IOptionsMonitor<ApiExposeOptions>? _options;
    private readonly RomCanonicalResolver? _canonique;
    private readonly ICommandesRetroArch _commandes;
    private readonly ILogger<DeckLaunchService>? _journal;
    private readonly object _verrou = new();
    private readonly DeckGeste _geste = new();
    private IDisposable? _abonnement;
    private DeckLancement? _enCours;
    private DateTime _preparation = DateTime.MinValue;
    private bool? _gesteLibre;

    public DeckChoix Choix { get; }

    /// <summary>Le rang en direct (H-D21), arme a chaque lancement avec Deck ; pose par Program.cs.</summary>
    public DeckRankService? Rang { get; set; }

    /// <summary>L'epreuve en cours (H-D23), armee a chaque lancement d'epreuve avec Deck ; pose par Program.cs.</summary>
    public DeckTrialService? Epreuve { get; set; }

    /// <summary>Ce qui se tait quand le Deck est pose (demande du user du 08/11) ; pose par Program.cs.</summary>
    public DeckSilence? Silence { get; set; }

    // Coutures pour les essais (sans exe, sans ecran, sans processus)
    internal Func<DeckCatalogue>? Catalogue { get; set; }
    internal Func<DeckCatalogue.Entree, int, int, DeckTrialPlan?, DeckRendu?>? Rendeur { get; set; }
    internal Func<(int Largeur, int Hauteur)>? Ecran { get; set; }
    internal string? RacineDuDecor { get; set; }
    internal bool DemarrerLeHost { get; set; } = true;
    private readonly IHostApplicationLifetime? _vie;

    public static string DossierEtat => Path.Combine(RetroBatPaths.PluginRoot, "state", "deck");

    public DeckLaunchService(IEventBus bus, ApiContext contexte, DeckStateService? etat = null, IOptionsMonitor<ApiExposeOptions>? options = null,
        RomCanonicalResolver? canonique = null, ICommandesRetroArch? commandes = null, ILogger<DeckLaunchService>? journal = null,
        string? fichierDesChoix = null, IHostApplicationLifetime? vie = null)
    {
        _vie = vie;
        _bus = bus;
        _contexte = contexte;
        _etat = etat;
        _options = options;
        _canonique = canonique;
        _commandes = commandes ?? new CommandesRetroArchUdp();
        _journal = journal;
        Choix = new DeckChoix(fichierDesChoix ?? Path.Combine(DossierEtat, "choix.json"));
    }

    private ApiExposeOptions.PlayerDeckOptions Reglages => _options?.CurrentValue.PlayerDeck ?? new ApiExposeOptions.PlayerDeckOptions();

    /// <summary>Le dossier configure, puis le Deck livre (tools/playerdeck), puis celui de developpement (<see cref="DeckCatalogue.Dossier"/>).</summary>
    private string DossierDuDeck => DossierImpose ?? DeckCatalogue.Dossier(Reglages.Directory);

    /// <summary>Couture des essais : le dossier du Deck, sans options.</summary>
    internal string? DossierImpose { get; set; }

    /// <summary>Couture des essais : l'attente du rendu du decor (<see cref="AttenteDuRendu"/> en jeu).</summary>
    internal TimeSpan Attente { get; set; } = AttenteDuRendu;

    /// <summary>Le lancement en cours (ou le dernier prepare), avec son Deck ; null hors d'un lancement des boutons speciaux.</summary>
    public DeckLancement? EnCours
    {
        get { lock (_verrou) return _enCours; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Un decor laisse par un arret brutal ne doit pas habiller le prochain jeu lance par ES
        DeckDecor.Effacer(RacineDuDecor);
        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        if (_etat is not null) _etat.CommandesDuChoix = Commande;
        // (ECR-03) le Deck d'avant une mise a jour est remplace au demarrage, pas au premier lancement
        if (_vie is not null && DemarrerLeHost && Reglages.StartHost)
        {
            ApresLeDemarrage.Lancer(_vie, _journal, "le remplacement du Deck d'avant la mise a jour",
                _ => { RemplacerLeDeckPerime("au demarrage de l'API"); return Task.CompletedTask; });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        DeckDecor.Effacer(RacineDuDecor);
        ArreterLeDeckDuDirect("api stopping");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        ArreterLeDeckDuDirect("disposed");
    }

    // ─────────────────────────────── preparation du lancement ───────────────────────────────

    /// <summary>
    /// Avant le lanceur : le Deck de ce lancement, son decor pose et les arguments a ajouter. Jamais bloquant : sans Deck,
    /// sans exe, sur un echec, le lancement part comme avant (arguments vides). <paramref name="romGroup"/> : le rom_group
    /// s'il est deja connu (reglement d'un defi), sinon resolu par le referentiel. <paramref name="classement"/> : le
    /// classement du rang en direct (regle de l'onglet, epreuve) ; null : celui du mode par defaut. <paramref name="place"/> :
    /// la place du joueur local (MULTIPLAYER), dont le score se classe.
    /// </summary>
    public DeckLancement Preparer(string mode, string systeme, string cheminRom, string? romGroup = null, DeckRankBoard? classement = null, int place = 1,
        DeckTrialPlan? epreuve = null, string cartes = DeckCartes.Deck)
    {
        // Le Deck du direct d'une partie precedente ne survit pas a un nouveau lancement (H-D28)
        ArreterLeDeckDuDirect("new launch");
        DeckLancement lancement;
        try
        {
            _cartes = DeckCartes.Connu(cartes) ? cartes : DeckCartes.Deck;
            lancement = PreparerLeDeck(mode, systeme, cheminRom, romGroup, classement, epreuve);
        }
        catch (Exception ex)
        {
            // JAMAIS D'ERREUR VISIBLE (1.9.163) : quoi qu'il arrive au Deck, le jeu part comme avant, sans decor ni argument.
            _journal?.LogWarning(ex, "PlayerDeck : lancement prepare sans Deck.");
            lancement = Aucun(mode, systeme, Path.GetFileNameWithoutExtension(cheminRom ?? string.Empty), "", "local", cheminRom ?? "", "error");
        }

        if (place != 1) lancement = lancement with { Seat = place };
        // Le rang en direct se prepare avec le lancement (lecture du classement en fond, jamais bloquante)
        try
        {
            Rang?.Armer(lancement);
        }
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "PlayerDeck : rang en direct non prepare.");
        }

        // L'epreuve en cours (H-D23) : le plan du reglement, pour la couche epreuve du Deck
        try
        {
            Epreuve?.Armer(lancement, epreuve);
        }
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "PlayerDeck : epreuve non preparee.");
        }

        // LES SPEEDRUNS ET LES CHALLENGES (decision du user du 18/11 nuit) : leur direct (partage, annonce par le site) montre
        // aussi le Deck du joueur ; il part avec le lancement, comme celui du defi diffuse. Jamais en MULTIPLAYER (pas de Deck).
        if (lancement is { Status: "posed", Mode: DeckModes.Speedrun }) DemarrerLeDeckDuDirect("speedrun or challenge launch");
        return lancement;
    }

    private DeckLancement PreparerLeDeck(string mode, string systeme, string cheminRom, string? romGroup, DeckRankBoard? classement, DeckTrialPlan? epreuve)
    {
        var rom = Path.GetFileNameWithoutExtension(cheminRom ?? string.Empty).Trim().ToLowerInvariant();
        var joueur = Joueur();
        try
        {
            if (!Reglages.Enabled || !DeckModes.PoseUnDeck(mode) || rom.Length == 0)
            {
                return Retenir(Aucun(mode, systeme, rom, "", joueur, cheminRom ?? "", Reglages.Enabled ? "mode_not_wired" : "disabled"), publier: false);
            }

            var (jeu, groupe) = CleDuJeu(systeme, cheminRom!, romGroup);
            var catalogue = Catalogue?.Invoke() ?? DeckCatalogue.Lire(DossierDuDeck);
            var installes = catalogue.PourLeJeu(rom, groupe);
            if (installes.Count == 0)
            {
                return Retenir(Aucun(mode, systeme, rom, jeu, joueur, cheminRom!, "no_deck_installed"), publier: false);
            }

            var choix = installes.Select(e => e.Id).Append(DeckChoix.Off).ToList();
            var decision = Choix.Resoudre(joueur, jeu, mode, installes);
            var lancement = new DeckLancement(mode, systeme, rom, groupe, jeu, joueur, cheminRom!, "off", null, null, null, decision.Source, choix, null, null, DateTime.UtcNow)
            {
                Board = classement,
            };
            if (decision.DeckId is null || catalogue.Trouver(decision.DeckId) is not { } entree)
            {
                _journal?.LogInformation("PlayerDeck : {Jeu} ({Mode}) lance sans Deck ({Source}).", jeu, mode, decision.Source);
                return Retenir(lancement with { Status = decision.Source == "none" ? "none" : "off" }, publier: true);
            }

            lancement = lancement with { DeckId = entree.Id, DeckName = entree.Name, Official = entree.Official };
            var (largeur, hauteur) = Ecran?.Invoke() ?? EcranPrincipal();
            var rendu = (Rendeur ?? RendreParLeDeck).Invoke(entree, largeur, hauteur, epreuve);
            if (rendu is null)
            {
                return Retenir(lancement with { Status = "window", Reason = "decor_not_rendered" }, publier: true);
            }

            var info = DeckDecor.Info(largeur, hauteur, rendu.Trou);
            var t = rendu.Trou;
            var viewport = DeckDecor.ViewportDuLanceur(largeur, hauteur, t.X, t.Y, largeur - t.X - t.Width, hauteur - t.Y - t.Height,
                largeur, hauteur, largeur, hauteur);
            if (!DeckDecor.Ecrire(systeme, cheminRom!, rendu.Png, info, RacineDuDecor))
            {
                return Retenir(lancement with { Status = "window", Reason = "decor_not_written" }, publier: true);
            }

            var pose = new DeckDecorPose(DeckDecor.Nom, entree.Version, new DeckEcran(largeur, hauteur), t, viewport, rendu.Png, rendu.Cache, rendu.Ms);
            _journal?.LogInformation(
                "PlayerDeck : decor {Deck} pose pour {Jeu} ({Mode}, {Source}) : ecran {L} x {H}, trou {X},{Y} {W} x {Ht}, rendu {Ms} ms{Cache}.",
                entree.Id, jeu, mode, decision.Source, largeur, hauteur, t.X, t.Y, t.Width, t.Height, rendu.Ms, rendu.Cache ? " (cache)" : "");
            // LES NOTIFICATIONS DE RETROARCH (demande du user du 08/11) : un Deck qui sait montrer les messages dans ses rails
            // (deck.notice, annonce par son rendu) les fait couper pour ce lancement, par l'argument du lanceur « -OnScreenMsg 0 »
            // (video_font_enable, qui eteint aussi les widgets de RetroArch 1.22). Un Deck plus ancien les laisse a RetroArch.
            var arguments = rendu.Notices ? DeckDecor.ArgumentsDuLanceur.Concat(DeckDecor.ArgumentsSansNotifications).ToList() : DeckDecor.ArgumentsDuLanceur;
            return Retenir(lancement with { Status = "posed", Decor = pose, Arguments = arguments, Notices = rendu.Notices, RetroArchMuet = rendu.Notices },
                publier: true);
        }
        catch (Exception ex)
        {
            _journal?.LogWarning(ex, "PlayerDeck : lancement de {Rom} prepare sans Deck.", rom);
            return Retenir(Aucun(mode, systeme, rom, "", joueur, cheminRom ?? "", "error"), publier: false);
        }
    }

    private static DeckLancement Aucun(string mode, string systeme, string rom, string jeu, string joueur, string chemin, string raison)
        => new(mode, systeme, rom, null, jeu, joueur, chemin, "none", null, null, null, "none", [], null, raison, DateTime.UtcNow);

    /// <summary>Qui montre les ecrans de depart et de bilan du lancement en preparation (<see cref="DeckCartes"/>).</summary>
    private string _cartes = DeckCartes.Deck;

    private DeckLancement Retenir(DeckLancement lancement, bool publier)
    {
        lancement = lancement with { Cards = _cartes };
        lock (_verrou)
        {
            // un jeu qui a des Decks garde son lancement, meme sans Deck retenu : le choix en jeu reste possible
            _enCours = lancement.Choices.Count == 0 ? null : lancement;
            _preparation = DateTime.UtcNow;
        }

        _etat?.PoserLeLancement(lancement.Choices.Count == 0 ? null : lancement);
        if (publier)
        {
            _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.launch", Payload = lancement });
            if (DemarrerLeHost && Reglages.StartHost) DemarrerLeDeck();
        }

        return lancement;
    }

    /// <summary>
    /// LE LANCEMENT PART SANS LE DECOR (lancement direct refuse, repli par ES) : le lancement prepare perd son decor. Sans cela,
    /// la partie lancee par ES recevait un Deck « pose » alors que RetroArch n'avait aucun decor (charte du 08/11). Le choix en
    /// jeu reste possible (il vaudra au lancement suivant).
    /// </summary>
    public void Abandonner(string raison)
    {
        DeckLancement? l;
        lock (_verrou) l = _enCours;
        if (l is null || l.Status != "posed") return;
        var sans = l with { Status = "none", Decor = null, Reason = raison, Arguments = [], Notices = false, RetroArchMuet = false };
        _journal?.LogInformation("PlayerDeck : lancement de {Jeu} sans decor ({Raison}) : Deck cache.", l.Game, raison);
        Retenir(sans, publier: true);
        DeckDecor.Effacer(RacineDuDecor);
    }

    /// <summary>
    /// UN LANCEMENT SANS DECK (room MULTIPLAYER, decision du user du 09/11) : le lancement prepare plus tot (un defi de World
    /// Scoring du meme jeu) ne se rattache pas a cette partie, et le choix en jeu (Select + A) ne propose rien.
    /// </summary>
    public void Retirer(string raison)
    {
        bool avait;
        lock (_verrou)
        {
            avait = _enCours is not null;
            _enCours = null;
            _preparation = DateTime.UtcNow;
        }

        _etat?.PoserLeLancement(null);
        if (avait) _journal?.LogInformation("PlayerDeck : lancement sans Deck ({Raison}).", raison);
    }

    /// <summary>
    /// LE CHALLENGE DIFFUSE EN LIVE (H-D28) : le lancement en cours, Deck pose, part en hote netplay ; le direct YouTube de cette
    /// partie montrera le Deck. Rien ne change pour le joueur ni pour le Deck de la borne.
    /// </summary>
    public void MarquerLeDirect()
    {
        // L'option du menu d'ES (global.apiexpose.challenge.share_live_deck, allumee par defaut) : eteinte, le direct reste comme avant
        if (!DeckDansLeDirect)
        {
            _journal?.LogInformation("PlayerDeck : option « Deck dans le direct » eteinte, direct sans Deck.");
            return;
        }

        lock (_verrou)
        {
            if (_enCours is { Status: "posed" } l) _enCours = l with { Diffuse = true };
        }

        // LE DECK DU DIRECT PART AVEC LA PARTIE, pas avec le passage a l'antenne : ses instruments qui s'accumulent (NO MISS,
        // FIRE MIX, scores par stage, graphe) sont alors ceux du Deck de la borne quand la partie passe a l'antenne. Il ne
        // part que si l'exe du direct sait le poser (0.7.3).
        DemarrerLeDeckDuDirect("new live challenge");
    }

    private void DemarrerLeDeckDuDirect(string pourquoi)
    {
        if (!DeckDansLeDirect) return;
        ArreterLeDeckDuDirect(pourquoi);
        var nouveau = DemarrerDeckDuDirect?.Invoke(this)
                      ?? DeckDuDirect.Demarrer(this, Replay.Video.LanceurDuDirect.Chemin(AppContext.BaseDirectory), _journal);
        lock (_verrou) _duDirect = nouveau;
    }

    private DeckDuDirect? _duDirect;

    /// <summary>Couture des essais : la valeur de l'option « Deck dans le direct » (sinon les options de l'API).</summary>
    internal Func<bool>? OptionDeckDansLeDirect { get; set; }

    /// <summary>L'option « Deck dans le direct » (<c>global.apiexpose.challenge.share_live_deck</c>) : allumee par defaut.</summary>
    public bool DeckDansLeDirect
        => OptionDeckDansLeDirect?.Invoke() ?? _options?.CurrentValue.Leaderboard.ChallengeShareLiveDeck ?? true;

    /// <summary>Couture des essais : le demarrage du Deck du direct.</summary>
    internal Func<DeckLaunchService, DeckDuDirect?>? DemarrerDeckDuDirect { get; set; }

    /// <summary>
    /// Les options de l'exe du direct pour montrer le Deck (H-D28) : la memoire du Deck du direct s'il tourne pour la partie en
    /// cours (CHALLENGE diffuse, Deck pose) et si cet exe sait le poser ; vide sinon (le direct reste comme avant).
    /// </summary>
    public IReadOnlyList<string> OptionsDuDirect(string? exeDuDirect) => OptionsDuDirect(Replay.Video.LanceurDuDirect.SaitLeDeck(exeDuDirect));

    internal IReadOnlyList<string> OptionsDuDirect(bool exeSaitLeDeck)
    {
        DeckDuDirect? d;
        lock (_verrou) d = _duDirect;
        return exeSaitLeDeck && DeckDansLeDirect && d is { Vivant: true, FinDemandee: false } && DeckDuDirect.PourCeLancement(EnCours) ? d.OptionsDuDirect : [];
    }

    /// <summary>
    /// Le bail d'un direct sur le Deck du direct (19/11) : ses options (vides sans Deck), rendu par le direct a la fin de son exe.
    /// Tant qu'un bail court, la fin de la partie n'arrete pas le Deck du direct (<see cref="FinirLeDeckDuDirect"/>).
    /// </summary>
    public BailDuDirect TenirPourLeDirect(string? exeDuDirect) => TenirPourLeDirect(Replay.Video.LanceurDuDirect.SaitLeDeck(exeDuDirect));

    internal BailDuDirect TenirPourLeDirect(bool exeSaitLeDeck)
    {
        DeckDuDirect? d;
        IReadOnlyList<string> options;
        lock (_verrou)
        {
            d = _duDirect;
            options = OptionsDuDirect(exeSaitLeDeck);
            if (options.Count == 0 || d is null) return BailDuDirect.Aucun;
            d.Lecteurs++;
        }

        return new BailDuDirect(options, () => RendreLeBail(d));
    }

    private void RendreLeBail(DeckDuDirect d)
    {
        bool arreter;
        lock (_verrou)
        {
            d.Lecteurs--;
            arreter = d.FinDemandee && d.Lecteurs <= 0 && ReferenceEquals(_duDirect, d);
        }

        if (arreter) ArreterCeDeckDuDirect(d, "live ended");
    }

    /// <summary>Au plus ce temps apres la fin de la partie, le Deck du direct s'arrete meme si un direct le tient encore.</summary>
    internal TimeSpan GardeApresLaPartie { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// LA FIN DE LA PARTIE (19/11) : le Deck du direct s'arrete tout de suite si aucun direct ne le montre ; sinon il sert la fin du
    /// direct (le film arrive 5 a 19 s apres le jeu : la fin de la course garde le Deck) et s'arrete quand le dernier direct le
    /// rend, au plus tard <see cref="GardeApresLaPartie"/> apres la partie. Aucun nouveau direct ne le prend alors.
    /// </summary>
    private void FinirLeDeckDuDirect()
    {
        DeckDuDirect? d;
        bool garder;
        lock (_verrou)
        {
            d = _duDirect;
            if (d is null) return;
            garder = d.Lecteurs > 0;
            if (garder) d.FinDemandee = true;
        }

        if (!garder)
        {
            ArreterCeDeckDuDirect(d, "game ended");
            return;
        }

        _journal?.LogInformation("PlayerDeck : partie finie, Deck du direct garde pour la fin du direct ({S:F0} s au plus).", GardeApresLaPartie.TotalSeconds);
        var garde = GardeApresLaPartie;
        _ = Task.Run(async () =>
        {
            await Task.Delay(garde).ConfigureAwait(false);
            ArreterCeDeckDuDirect(d, "game ended " + garde.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s ago");
        });
    }

    /// <summary>Arrete ce Deck du direct s'il est encore celui de la partie (un nouveau lancement l'a peut-etre deja remplace).</summary>
    private void ArreterCeDeckDuDirect(DeckDuDirect d, string pourquoi)
    {
        lock (_verrou)
        {
            if (!ReferenceEquals(_duDirect, d)) return;
            _duDirect = null;
        }

        d.Dispose();
        _journal?.LogInformation("PlayerDeck : Deck du direct arrete ({Pourquoi}).", pourquoi);
    }

    private void ArreterLeDeckDuDirect(string pourquoi)
    {
        DeckDuDirect? d;
        lock (_verrou)
        {
            d = _duDirect;
            _duDirect = null;
        }

        if (d is null) return;
        d.Dispose();
        _journal?.LogInformation("PlayerDeck : Deck du direct arrete ({Pourquoi}).", pourquoi);
    }

    /// <summary>L'exe du PlayerDeck (le meme que celui de la borne), ou null s'il n'est pas installe.</summary>
    public string? ExeDuDeckInstalle()
    {
        var exe = Path.Combine(DossierDuDeck, NomDuHost + ".exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Le joueur : celui qui est enregistre en salle, sinon le compte de la borne, sinon « local ». Jamais reecrit.</summary>
    private string Joueur()
        => _contexte.SessionPseudo is { Length: > 0 } s ? s : _contexte.PlayerPseudo is { Length: > 0 } p ? p : "local";

    /// <summary>
    /// Le jeu = systeme + rom_group des classements (« arcade/metal-slug-3 ») : le .MEM qui connait ce dump d'abord, le nom du
    /// fichier en repli (<see cref="DeckIdentite"/>). Couture des essais : <see cref="Identite"/>.
    /// </summary>
    private (string Jeu, string? Groupe) CleDuJeu(string systeme, string chemin, string? romGroup)
    {
        var cle = Identite?.Invoke(systeme, chemin, romGroup) ?? DeckIdentite.Resoudre(_canonique, systeme, chemin, romGroup);
        _journal?.LogDebug("PlayerDeck : {Chemin} = {Jeu} ({Source}).", chemin, cle.Jeu, cle.Source);
        return (cle.Jeu, cle.Groupe);
    }

    /// <summary>Couture des essais : l'identite du jeu sans referentiel.</summary>
    internal Func<string, string, string?, DeckIdentite.Cle>? Identite { get; set; }

    private static (int, int) EcranPrincipal()
    {
        var e = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return (e.Width, e.Height);
    }

    // ─────────────────────────────── rendu par le PlayerDeck ───────────────────────────────

    /// <summary>
    /// LE PNG DU DECOR EST RENDU PAR LE PLAYERDECK LUI-MEME (<c>--render-decor</c>) : le meme code dessine le fond et les
    /// valeurs, sur la meme grille de pixels du jeu, donc ils tombent au pixel pres. Le rendu a lieu au lancement, avant le
    /// jeu, jamais pendant une partie (budgets du §0.4 : rien a la charge du Host en jeu). Garde en cache par Deck (empreinte
    /// du fichier), exe et taille d'ecran : un relancement du meme jeu ne coute qu'une copie de fichier.
    /// </summary>
    private DeckRendu? RendreParLeDeck(DeckCatalogue.Entree entree, int largeur, int hauteur, DeckTrialPlan? epreuve)
    {
        var exe = Path.Combine(DossierDuDeck, NomDuHost + ".exe");
        if (!File.Exists(exe))
        {
            _journal?.LogInformation("PlayerDeck : exe introuvable ({Exe}), pas de decor.", exe);
            return null;
        }

        var infoExe = new FileInfo(exe);
        var cle = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{entree.Sha256}|{infoExe.Length}|{infoExe.LastWriteTimeUtc.Ticks}|{largeur}x{hauteur}|{epreuve?.ToJson()}")))[..16].ToLowerInvariant();
        var cache = Path.Combine(DossierEtat, "decors");
        var png = Path.Combine(cache, entree.Id + "-" + cle + ".png");
        // La couche epreuve (H-D23) est dans le decor : ses plaques font partie du fond pose par RetroArch
        var plan = epreuve is null ? null : Path.ChangeExtension(png, ".trial.json");
        var json = Path.ChangeExtension(png, ".json");
        if (File.Exists(png) && File.Exists(json) && File.ReadAllText(json) is var lu && LireLeRendu(lu, largeur, hauteur) is { } garde)
        {
            return new DeckRendu(png, garde, largeur, hauteur, true, 0) { Notices = PorteLesNotices(lu) };
        }

        Directory.CreateDirectory(cache);
        if (plan is not null) File.WriteAllText(plan, epreuve!.ToJson());
        var chrono = Stopwatch.StartNew();
        using var processus = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--render-decor \"{png}\" --theme {entree.Id} --render-size {largeur}x{hauteur}" + (plan is null ? "" : $" --trial \"{plan}\""),
            WorkingDirectory = DossierDuDeck,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        if (processus is null) return null;
        var sortie = processus.StandardOutput.ReadToEndAsync();
        if (!processus.WaitForExit((int)Attente.TotalMilliseconds))
        {
            try { processus.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _journal?.LogWarning("PlayerDeck : rendu du decor {Deck} trop long, abandonne.", entree.Id);
            return null;
        }

        var texte = sortie.Result;
        if (processus.ExitCode != 0 || LireLeRendu(texte, largeur, hauteur) is not { } trou || !File.Exists(png))
        {
            _journal?.LogWarning("PlayerDeck : rendu du decor {Deck} refuse (code {Code}) : {Sortie}", entree.Id, processus.ExitCode, texte);
            return null;
        }

        File.WriteAllText(json, texte);
        return new DeckRendu(png, trou, largeur, hauteur, false, chrono.ElapsedMilliseconds) { Notices = PorteLesNotices(texte) };
    }

    /// <summary>La ligne JSON de <c>--render-decor</c> : le trou, s'il est rendu pour cet ecran et tient dedans. Pur.</summary>
    internal static DeckDecor.Trou? LireLeRendu(string texte, int largeur, int hauteur)
    {
        try
        {
            var r = JsonSerializer.Deserialize<SortieDuRendu>(texte.Trim(), Json);
            if (r?.Hole is not { } h || r.Screen is not { } s || s.Width != largeur || s.Height != hauteur) return null;
            if (h.Width < 16 || h.Height < 16 || h.X < 0 || h.Y < 0 || h.X + h.Width > largeur || h.Y + h.Height > hauteur) return null;
            return h;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SortieDuRendu(string? Deck, int Version, DeckEcran? Screen, DeckDecor.Trou? Hole, string[]? Features = null);

    /// <summary>Le Deck sait-il montrer les messages dans ses rails (« Features » de --render-decor contient « notice ») ? Pur.</summary>
    internal static bool PorteLesNotices(string texte)
    {
        try
        {
            return JsonSerializer.Deserialize<SortieDuRendu>(texte.Trim(), Json)?.Features is { } f
                && f.Any(x => string.Equals(x, "notice", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Un Deck en cours : l'heure de son demarrage et de quoi l'arreter (couture des essais : aucun processus).</summary>
    internal sealed record DeckResident(DateTime DemarreUtc, Action Arreter);

    /// <summary>Couture des essais : les Decks residents (par defaut, les processus <see cref="NomDuHost"/>).</summary>
    internal Func<IReadOnlyList<DeckResident>>? DecksResidents { get; set; }

    /// <summary>Couture des essais : demarrer le Deck (par defaut, le processus, cache et a la suite des lancements).</summary>
    internal Action<string>? LancerLeDeck { get; set; }

    /// <summary>Couture des essais : la date d'ecriture de l'exe du Deck.</summary>
    internal Func<string, DateTime>? DateDeLExe { get; set; }

    /// <summary>Apres la fin d'une partie, le Deck d'avant la mise a jour est remplace ce temps-la plus tard : il a envoye son bilan.</summary>
    internal TimeSpan AttenteApresLaPartie { get; set; } = TimeSpan.FromSeconds(10);

    private int _remplacementPrevu;

    private IReadOnlyList<DeckResident> Residents()
    {
        if (DecksResidents is { } couture) return couture();
        var liste = new List<DeckResident>();
        foreach (var p in Process.GetProcessesByName(NomDuHost))
        {
            // le Deck du direct (meme exe, sans fenetre) n'est jamais le Deck de la borne
            if (DeckDuDirect.EstUnDeckDuDirect(p.Id))
            {
                p.Dispose();
                continue;
            }

            DateTime debut;
            try { debut = p.StartTime.ToUniversalTime(); }
            catch (Exception) { debut = DateTime.MaxValue; }   // illisible : jamais tenu pour perime
            var processus = p;
            liste.Add(new DeckResident(debut, () =>
            {
                try { processus.Kill(); processus.WaitForExit(3000); } catch { /* deja parti */ }
                processus.Dispose();
            }));
        }

        return liste;
    }

    private string ExeDuDeck => Path.Combine(DossierDuDeck, NomDuHost + ".exe");

    private DateTime EcritureDeLExe(string exe)
        => DateDeLExe?.Invoke(exe) ?? (File.Exists(exe) ? File.GetLastWriteTimeUtc(exe) : DateTime.MinValue);

    private void Lancer(string exe)
    {
        if (LancerLeDeck is { } couture)
        {
            couture(exe);
            return;
        }

        if (!File.Exists(exe)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--deck --follow-launch",
            WorkingDirectory = DossierDuDeck,
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
        _journal?.LogInformation("PlayerDeck : Deck demarre ({Exe}).", exe);
    }

    /// <summary>
    /// Le PlayerDeck tourne-t-il ? Sinon on le demarre, cache (<c>--follow-launch</c>) : il se montre pour ce lancement et
    /// tient le raccourci Ctrl+Alt+D. Une seule instance : un Deck deja lance par le joueur n'est pas double.
    ///
    /// (brief 13/11, ECR-03) Un Deck d'avant la mise a jour n'est plus arrete ICI : on etait au milieu d'un lancement, la
    /// page de depart ouverte depuis 3 s, et sa fenetre disparaissait puis revenait (journal du 13/11, 20:41:44). Il sert
    /// ce lancement ; il est remplace au demarrage de l'API, et sinon apres la partie (<see cref="RemplacerLeDeckPerime"/>).
    /// </summary>
    internal void DemarrerLeDeck()
    {
        try
        {
            var exe = ExeDuDeck;
            var enCours = Residents();
            if (enCours.Count > 0)
            {
                if (enCours.Any(d => d.DemarreUtc < EcritureDeLExe(exe)) && Interlocked.Exchange(ref _remplacementPrevu, 1) == 0)
                {
                    _journal?.LogInformation("PlayerDeck : Deck d'avant la mise a jour garde pour ce lancement, remplace apres la partie ({Exe}).", exe);
                }

                return;
            }

            Lancer(exe);
        }
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "PlayerDeck : le Deck n'a pas demarre.");
        }
    }

    /// <summary>
    /// UN DECK D'AVANT LA MISE A JOUR (10/11) : il reste resident apres son lancement, et une borne mise a jour gardait l'ancien
    /// Deck jusqu'au redemarrage du PC (19XX du 09/11 encore en 0.1.6 le 10/11 apres la 1.9.167). Un Deck lance avant que son
    /// exe ne change sur le disque est arrete, et le nouveau demarre, cache. Hors d'un lancement seulement : au demarrage de
    /// l'API, ou apres la partie qui l'a vu. Rend vrai si un Deck a ete remplace.
    /// </summary>
    internal bool RemplacerLeDeckPerime(string quand)
    {
        try
        {
            var exe = ExeDuDeck;
            var enCours = Residents();
            if (enCours.Count == 0) return false;
            var ecrit = EcritureDeLExe(exe);
            var perimes = enCours.Where(d => d.DemarreUtc < ecrit).ToList();
            if (perimes.Count == 0 || perimes.Count < enCours.Count) return false;   // un Deck a jour tourne deja : on n'en double pas
            foreach (var d in perimes) d.Arreter();
            _journal?.LogInformation("PlayerDeck : Deck d'avant la mise a jour arrete {Quand} ({Exe}).", quand, exe);
            Lancer(exe);
            return true;
        }
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "PlayerDeck : remplacement du Deck d'avant la mise a jour impossible.");
            return false;
        }
    }

    /// <summary>Apres la partie, le remplacement prevu pendant le lancement, si aucun jeu n'a repris entre-temps.</summary>
    private void RemplacerApresLaPartie()
    {
        if (Volatile.Read(ref _remplacementPrevu) == 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(AttenteApresLaPartie).ConfigureAwait(false);
            if (_contexte.Ui.Running is not null) return;   // une nouvelle partie : ce sera apres elle
            if (Interlocked.Exchange(ref _remplacementPrevu, 0) == 0) return;
            RemplacerLeDeckPerime("apres la partie");
        });
    }

    // ─────────────────────────────── choix en jeu ───────────────────────────────

    private void Recevoir(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "panel.input.pressed":
                case "panel.input.released":
                {
                    if (!Reglages.Gesture) return;
                    var p = e.Payload;
                    var accompli = _geste.Entree(DeckStateService.Texte(p, "Identity"), (int)(DeckStateService.Entier(DeckStateService.Champ(p, "Player")) ?? 1),
                        e.Type.EndsWith("pressed", StringComparison.Ordinal), DateTime.UtcNow);
                    if (accompli && _contexte.Ui.Running is not null && GesteLibre()) Choisir(null, "gamepad");
                    break;
                }
                case "panel.input.devices":
                    _geste.Oublier();
                    break;
                case "ui.game.ended":
                {
                    bool effacer;
                    lock (_verrou)
                    {
                        effacer = DateTime.UtcNow - _preparation > GardeDuDecor;
                        if (effacer) _enCours = null;
                    }

                    // Le decor s'efface une fois la partie finie : il n'apparait pas parmi les decors d'ES
                    if (effacer) DeckDecor.Effacer(RacineDuDecor);
                    RemplacerApresLaPartie();
                    // Le Deck du direct (H-D28) s'arrete avec la partie, ou avec le direct qui la montre encore (pas celui d'un
                    // lancement qui vient d'etre prepare)
                    if (effacer) FinirLeDeckDuDirect();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "PlayerDeck : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>La combinaison est verifiee une fois dans le retroarch.cfg reel (E.6) ; illisible : on la croit libre.</summary>
    private bool GesteLibre()
    {
        if (_gesteLibre is { } connu) return connu;
        try
        {
            var libre = DeckGeste.CombinaisonLibre(File.ReadLines(RetroBatPaths.RetroArchConfigPath), out var raison);
            if (!libre) _journal?.LogWarning("PlayerDeck : le geste Select + A est pris dans retroarch.cfg ({Raison}) : seul Ctrl+Alt+D choisit le Deck.", raison);
            _gesteLibre = libre;
            return libre;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Les messages du Deck sur le WebSocket : <c>deck.choice.next</c> (Deck suivant ou Off) et <c>deck.choice.set</c>
    /// (<c>{"DeckId":"19xx"}</c> ou <c>{"DeckId":"off"}</c>). Rend l'accuse de reception.
    /// </summary>
    public string? Commande(string type, JsonElement corps)
    {
        string? voulu = null;
        if (type.Equals("deck.choice.set", StringComparison.OrdinalIgnoreCase))
        {
            voulu = corps.ValueKind == JsonValueKind.Object && (corps.TryGetProperty("DeckId", out var d) || corps.TryGetProperty("deckId", out d))
                && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(voulu)) return Accuse(type, "deck_required", null);
        }
        else if (!type.Equals("deck.choice.next", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var choix = Choisir(voulu, "keyboard");
        return Accuse(type, choix is null ? "no_deck_launch" : null, choix);
    }

    private static string Accuse(string type, string? refus, DeckChoixPublie? choix)
        => JsonSerializer.Serialize(new EventEnvelope { Type = "deck.ack", Payload = new { Request = type, Accepted = refus is null, Reason = refus, Choice = choix } });

    /// <summary>
    /// Le choix du joueur en jeu : <paramref name="voulu"/> (un Deck installe pour ce jeu, ou off), sinon le suivant. Retenu
    /// pour ce joueur, ce jeu et ce mode ; publie (<c>deck.choice</c>) et dit dans RetroArch. Null hors d'un lancement avec Deck.
    /// </summary>
    internal DeckChoixPublie? Choisir(string? voulu, string source)
    {
        DeckLancement? lancement;
        lock (_verrou) lancement = _enCours;
        // MULTIPLAYER (decision du user du 09/11, remplace H-D17) : aucun Deck en room, rien a choisir
        if (lancement is null || lancement.Choices.Count == 0 || !DeckModes.PoseUnDeck(lancement.Mode)) return null;

        var catalogue = Catalogue?.Invoke() ?? DeckCatalogue.Lire(DossierDuDeck);
        var installes = catalogue.PourLeJeu(lancement.Rom, lancement.RomGroup);
        if (installes.Count == 0) return null;

        string suivant;
        if (voulu is not null)
        {
            if (!voulu.Equals(DeckChoix.Off, StringComparison.OrdinalIgnoreCase) && !installes.Any(e => e.Id.Equals(voulu, StringComparison.OrdinalIgnoreCase))) return null;
            suivant = voulu.ToLowerInvariant();
        }
        else
        {
            var courant = Choix.Resoudre(lancement.Player, lancement.Game, lancement.Mode, installes).DeckId ?? DeckChoix.Off;
            suivant = DeckChoix.Suivant(courant, installes);
        }

        Choix.Poser(lancement.Player, lancement.Game, lancement.Mode, suivant);
        var off = suivant == DeckChoix.Off;
        var entree = off ? null : installes.First(e => e.Id.Equals(suivant, StringComparison.OrdinalIgnoreCase));
        var pose = lancement.Status == "posed" ? lancement.DeckId : null;
        // LA CHARTE (08/11) : sans decor pose, le Deck ne se dessine pas en entier par-dessus le cadre du jeu ; le Deck choisi
        // vaut au lancement suivant (« switch » n'est plus publie).
        var maintenant = off ? "hide"
            : pose is not null && pose.Equals(suivant, StringComparison.OrdinalIgnoreCase) ? "show"
            : "next-launch";
        var texte = off
            ? pose is null ? "PlayerDeck off" : "PlayerDeck off (frame stays until next launch)"
            : maintenant == "next-launch" ? $"PlayerDeck: {entree!.Name} from next launch" : $"PlayerDeck: {entree!.Name}";
        // La route se decide AVANT de publier le choix : Off retire le Deck, mais le message « PlayerDeck off » doit encore s'y voir
        // (le Deck montre la plaque seule, puis se cache), puisque RetroArch, muet pour ce lancement, ne le montrerait pas.
        var auDeck = Silence?.AuDeck ?? false;
        var publie = new DeckChoixPublie(lancement.Mode, lancement.Game, lancement.Player, off ? null : entree!.Id, off ? null : entree!.Name, off,
            maintenant, pose, texte, source, lancement.Choices);
        _journal?.LogInformation("PlayerDeck : choix {Deck} pour {Jeu} ({Mode}, {Joueur}) par {Source}, maintenant : {Maintenant}.",
            suivant, lancement.Game, lancement.Mode, lancement.Player, source, maintenant);
        _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.choice", Payload = publie });
        if (Silence?.Dire(DeckNotices.DuChoix(off, pose is not null, entree?.Name, maintenant), auDeck) == true) return publie;
        // RetroArch muet pour ce lancement, sans plaque du Deck : le bandeau de l'API (11/11)
        if (Infrastructure.NotificationsRetroArch.AuBandeau("PLAYERDECK", texte, 4000)) return publie;
        _ = Task.Run(async () =>
        {
            try { await _commandes.EnvoyerAsync("SHOW_MSG " + texte, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _journal?.LogDebug(ex, "PlayerDeck : message du choix non affiche."); }
        });
        return publie;
    }
}

// ─────────────────────────────── messages (contrat additif) ───────────────────────────────

/// <summary>
/// deck.launch, et le champ <c>Deck</c> de deck.game : le Deck d'un lancement des boutons speciaux. <c>Status</c> : posed
/// (decor pose, arguments ajoutes au lanceur), window (Deck choisi mais decor impossible : la fenetre dessine tout), off
/// (le joueur l'a retire pour ce jeu et ce mode), none (aucun Deck installe pour le jeu, ou lancement non concerne).
/// <c>Source</c> : choice, choice-other-mode, official-default, none. <c>Choices</c> : l'ordre du choix en jeu.
/// </summary>
public sealed record DeckLancement(
    string Mode,
    string SystemId,
    string Rom,
    string? RomGroup,
    string Game,
    string Player,
    string GamePath,
    string Status,
    string? DeckId,
    string? DeckName,
    bool? Official,
    string Source,
    IReadOnlyList<string> Choices,
    DeckDecorPose? Decor,
    string? Reason,
    DateTime PreparedAtUtc)
{
    /// <summary>Les arguments a ajouter a la ligne du lanceur (vides sans decor pose).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Le classement du rang en direct demande par le bouton (null : celui du mode, choisi par le rang).</summary>
    public DeckRankBoard? Board { get; init; }

    /// <summary>La place du joueur local (1 ; en MULTIPLAYER, sa place dans la room) : son score se classe.</summary>
    public int Seat { get; init; } = 1;

    /// <summary>Le Deck de ce lancement montre les messages du joueur dans ses rails (deck.notice).</summary>
    public bool Notices { get; init; }

    /// <summary>Les notifications de RetroArch sont coupees pour ce lancement (-OnScreenMsg 0) : les messages vont au Deck.</summary>
    public bool RetroArchMuet { get; init; }

    /// <summary>Qui montre les ecrans de depart et de bilan : deck | api (<see cref="DeckCartes"/>).</summary>
    public string Cards { get; init; } = DeckCartes.Deck;

    /// <summary>
    /// Le CHALLENGE est diffuse en LIVE (hote netplay, direct annonce) : seul ce lancement montre le Deck dans le direct YouTube
    /// (H-D28). Interne a l'API, hors des messages.
    /// </summary>
    [JsonIgnore]
    public bool Diffuse { get; init; }
}

/// <summary>
/// Le decor pose : la taille de l'ecran (celle du PNG et du .info), le trou, et le custom_viewport que le lanceur en tire
/// (<see cref="DeckDecor.ViewportDuLanceur"/>, egal au trou quand l'image est a la taille de l'ecran).
/// </summary>
public sealed record DeckDecorPose(string Name, int Version, DeckEcran Screen, DeckDecor.Trou Hole, DeckDecor.Trou? LauncherViewport, string Png, bool Cached, long RenderMs);

public sealed record DeckEcran(int Width, int Height);

/// <summary>Un decor rendu : le PNG (cache), son trou, la taille d'ecran, et le temps du rendu.</summary>
public sealed record DeckRendu(string Png, DeckDecor.Trou Trou, int Largeur, int Hauteur, bool Cache, long Ms)
{
    /// <summary>Le Deck qui l'a rendu sait montrer les messages dans ses rails (deck.notice).</summary>
    public bool Notices { get; init; }
}

/// <summary>
/// QUI MONTRE LES ECRANS DE DEPART ET DE BILAN D'UN LANCEMENT AVEC DECK (demande du user du 09/11 : « l'ecran start et recap
/// stats au debut et a la fin du world scoring comme pour les challenges/speedruns »). Un seul systeme, jamais deux ecrans :
/// <list type="bullet">
/// <item><see cref="Deck"/> : le PlayerDeck pose son ecran de depart plein ecran, aux couleurs du jeu, de la decision du
/// lancement jusqu'a l'image du jeu, puis son ecran de bilan a la fin (CHALLENGE de World Scoring, prive ou diffuse ; epreuve
/// lancee sans panneau, par la route REST) ;</item>
/// <item><see cref="Api"/> : les pages plein ecran de l'API (preparation et bilan des epreuves du panneau Challenges &amp;
/// Speedruns, interactives : quitter, rejouer) restent ; le Deck ne montre aucune valeur tant que la page est la
/// (<c>deck.page</c>).</item>
/// </list>
/// </summary>
public static class DeckCartes
{
    public const string Deck = "deck";
    public const string Api = "api";

    public static bool Connu(string? c) => c is Deck or Api;
}

/// <summary>
/// deck.choice : le choix du joueur en jeu. <c>Now</c> : hide (Off : la fenetre se cache, le decor reste jusqu'au lancement
/// suivant), show (le Deck dont le decor est pose), switch (aucun decor pose : la fenetre dessine ce Deck en entier),
/// next-launch (un autre Deck que celui du decor). <c>Label</c> : le message montre dans RetroArch.
/// </summary>
public sealed record DeckChoixPublie(
    string Mode,
    string Game,
    string Player,
    string? DeckId,
    string? DeckName,
    bool Off,
    string Now,
    string? PosedDeckId,
    string Label,
    string Source,
    IReadOnlyList<string> Choices);
