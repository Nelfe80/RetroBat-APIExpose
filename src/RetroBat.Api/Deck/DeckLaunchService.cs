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

    // Coutures pour les essais (sans exe, sans ecran, sans processus)
    internal Func<DeckCatalogue>? Catalogue { get; set; }
    internal Func<DeckCatalogue.Entree, int, int, DeckRendu?>? Rendeur { get; set; }
    internal Func<(int Largeur, int Hauteur)>? Ecran { get; set; }
    internal string? RacineDuDecor { get; set; }
    internal bool DemarrerLeHost { get; set; } = true;

    public static string DossierEtat => Path.Combine(RetroBatPaths.PluginRoot, "state", "deck");

    public DeckLaunchService(IEventBus bus, ApiContext contexte, DeckStateService? etat = null, IOptionsMonitor<ApiExposeOptions>? options = null,
        RomCanonicalResolver? canonique = null, ICommandesRetroArch? commandes = null, ILogger<DeckLaunchService>? journal = null,
        string? fichierDesChoix = null)
    {
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

    private string DossierDuDeck => Reglages.Directory is { Length: > 0 } d ? d : DeckCatalogue.DossierParDefaut;

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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        DeckDecor.Effacer(RacineDuDecor);
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    // ─────────────────────────────── preparation du lancement ───────────────────────────────

    /// <summary>
    /// Avant le lanceur : le Deck de ce lancement, son decor pose et les arguments a ajouter. Jamais bloquant : sans Deck,
    /// sans exe, sur un echec, le lancement part comme avant (arguments vides). <paramref name="romGroup"/> : le rom_group
    /// s'il est deja connu (reglement d'un defi), sinon resolu par le referentiel.
    /// </summary>
    public DeckLancement Preparer(string mode, string systeme, string cheminRom, string? romGroup = null)
    {
        var rom = Path.GetFileNameWithoutExtension(cheminRom ?? string.Empty).Trim().ToLowerInvariant();
        var joueur = Joueur();
        try
        {
            if (!Reglages.Enabled || mode == DeckModes.Adventure || !DeckModes.Connu(mode) || rom.Length == 0)
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
            var lancement = new DeckLancement(mode, systeme, rom, groupe, jeu, joueur, cheminRom!, "off", null, null, null, decision.Source, choix, null, null, DateTime.UtcNow);
            if (decision.DeckId is null || catalogue.Trouver(decision.DeckId) is not { } entree)
            {
                _journal?.LogInformation("PlayerDeck : {Jeu} ({Mode}) lance sans Deck ({Source}).", jeu, mode, decision.Source);
                return Retenir(lancement with { Status = decision.Source == "none" ? "none" : "off" }, publier: true);
            }

            lancement = lancement with { DeckId = entree.Id, DeckName = entree.Name, Official = entree.Official };
            var (largeur, hauteur) = Ecran?.Invoke() ?? EcranPrincipal();
            var rendu = (Rendeur ?? RendreParLeDeck).Invoke(entree, largeur, hauteur);
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
            return Retenir(lancement with { Status = "posed", Decor = pose, Arguments = DeckDecor.ArgumentsDuLanceur }, publier: true);
        }
        catch (Exception ex)
        {
            _journal?.LogWarning(ex, "PlayerDeck : lancement de {Rom} prepare sans Deck.", rom);
            return Retenir(Aucun(mode, systeme, rom, "", joueur, cheminRom ?? "", "error"), publier: false);
        }
    }

    private static DeckLancement Aucun(string mode, string systeme, string rom, string jeu, string joueur, string chemin, string raison)
        => new(mode, systeme, rom, null, jeu, joueur, chemin, "none", null, null, null, "none", [], null, raison, DateTime.UtcNow);

    private DeckLancement Retenir(DeckLancement lancement, bool publier)
    {
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

    /// <summary>Le joueur : celui qui est enregistre en salle, sinon le compte de la borne, sinon « local ». Jamais reecrit.</summary>
    private string Joueur()
        => _contexte.SessionPseudo is { Length: > 0 } s ? s : _contexte.PlayerPseudo is { Length: > 0 } p ? p : "local";

    /// <summary>Le jeu = systeme + jeu (« arcade/19xx »), par le referentiel ; sinon le systeme du lanceur et la ROM.</summary>
    private (string Jeu, string? Groupe) CleDuJeu(string systeme, string chemin, string? romGroup)
    {
        try
        {
            if (_canonique?.ResolveByRomName(systeme, chemin) is { GameKey: { Length: > 0 } cle } fiche)
            {
                var barre = cle.IndexOf('/');
                return (cle.ToLowerInvariant(), barre >= 0 ? cle[(barre + 1)..] : romGroup);
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "PlayerDeck : rom_group non resolu pour {Chemin}.", chemin);
        }

        var groupe = romGroup is { Length: > 0 } g ? g : GamelistIdentity.Slugifier(Path.GetFileNameWithoutExtension(chemin));
        return ((RomCanonicalResolver.CanonicalScoringSystem(systeme) + "/" + groupe).ToLowerInvariant(), groupe);
    }

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
    private DeckRendu? RendreParLeDeck(DeckCatalogue.Entree entree, int largeur, int hauteur)
    {
        var exe = Path.Combine(DossierDuDeck, NomDuHost + ".exe");
        if (!File.Exists(exe))
        {
            _journal?.LogInformation("PlayerDeck : exe introuvable ({Exe}), pas de decor.", exe);
            return null;
        }

        var infoExe = new FileInfo(exe);
        var cle = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{entree.Sha256}|{infoExe.Length}|{infoExe.LastWriteTimeUtc.Ticks}|{largeur}x{hauteur}")))[..16].ToLowerInvariant();
        var cache = Path.Combine(DossierEtat, "decors");
        var png = Path.Combine(cache, entree.Id + "-" + cle + ".png");
        var json = Path.ChangeExtension(png, ".json");
        if (File.Exists(png) && File.Exists(json) && LireLeRendu(File.ReadAllText(json), largeur, hauteur) is { } garde)
        {
            return new DeckRendu(png, garde, largeur, hauteur, true, 0);
        }

        Directory.CreateDirectory(cache);
        var chrono = Stopwatch.StartNew();
        using var processus = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--render-decor \"{png}\" --theme {entree.Id} --render-size {largeur}x{hauteur}",
            WorkingDirectory = DossierDuDeck,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        if (processus is null) return null;
        var sortie = processus.StandardOutput.ReadToEndAsync();
        if (!processus.WaitForExit((int)AttenteDuRendu.TotalMilliseconds))
        {
            try { processus.Kill(); } catch (InvalidOperationException) { }
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
        return new DeckRendu(png, trou, largeur, hauteur, false, chrono.ElapsedMilliseconds);
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

    private sealed record SortieDuRendu(string? Deck, int Version, DeckEcran? Screen, DeckDecor.Trou? Hole);

    /// <summary>
    /// Le PlayerDeck tourne-t-il ? Sinon on le demarre, cache (<c>--follow-launch</c>) : il se montre pour ce lancement et
    /// tient le raccourci Ctrl+Alt+D. Une seule instance : un Deck deja lance par le joueur n'est pas double.
    /// </summary>
    private void DemarrerLeDeck()
    {
        try
        {
            if (Process.GetProcessesByName(NomDuHost).Length > 0) return;
            var exe = Path.Combine(DossierDuDeck, NomDuHost + ".exe");
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
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "PlayerDeck : le Deck n'a pas demarre.");
        }
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
        if (lancement is null || lancement.Choices.Count == 0) return null;

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
        var maintenant = off ? "hide"
            : pose is null ? "switch"
            : pose.Equals(suivant, StringComparison.OrdinalIgnoreCase) ? "show"
            : "next-launch";
        var texte = off
            ? pose is null ? "PlayerDeck off" : "PlayerDeck off (frame stays until next launch)"
            : maintenant == "next-launch" ? $"PlayerDeck: {entree!.Name} from next launch" : $"PlayerDeck: {entree!.Name}";
        var publie = new DeckChoixPublie(lancement.Mode, lancement.Game, lancement.Player, off ? null : entree!.Id, off ? null : entree!.Name, off,
            maintenant, pose, texte, source, lancement.Choices);
        _journal?.LogInformation("PlayerDeck : choix {Deck} pour {Jeu} ({Mode}, {Joueur}) par {Source}, maintenant : {Maintenant}.",
            suivant, lancement.Game, lancement.Mode, lancement.Player, source, maintenant);
        _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.choice", Payload = publie });
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
}

/// <summary>
/// Le decor pose : la taille de l'ecran (celle du PNG et du .info), le trou, et le custom_viewport que le lanceur en tire
/// (<see cref="DeckDecor.ViewportDuLanceur"/>, egal au trou quand l'image est a la taille de l'ecran).
/// </summary>
public sealed record DeckDecorPose(string Name, int Version, DeckEcran Screen, DeckDecor.Trou Hole, DeckDecor.Trou? LauncherViewport, string Png, bool Cached, long RenderMs);

public sealed record DeckEcran(int Width, int Height);

/// <summary>Un decor rendu : le PNG (cache), son trou, la taille d'ecran, et le temps du rendu.</summary>
public sealed record DeckRendu(string Png, DeckDecor.Trou Trou, int Largeur, int Hauteur, bool Cache, long Ms);

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
