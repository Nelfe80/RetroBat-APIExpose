using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck;

/// <summary>
/// L'ETAT DU JEU POUR LE PLAYERDECK, tenu par l'API pour qu'un Deck qui se connecte en pleine partie sache tout de suite
/// ou on en est. Le flux global ne rejoue rien : sans cet etat, le Deck montrait « -- » partout jusqu'au prochain
/// changement de chaque valeur.
///
/// Ce que le service tient, sans rien inventer :
/// - le jeu lance, des le crochet game-start (systeme, rom, rom_group, nom, coeur), puis sa geometrie quand RetroArch
///   l'a annoncee (taille du tampon, forme de l'image, rotation, cadence) : <c>deck.game</c> ;
/// - la derniere valeur de chaque signal du .MEM suivi (<c>retroarch.memory.changed</c>, <c>ingame.memory.changed</c>),
///   le dernier score de chaque joueur (<c>score.live.changed</c>) et les entrees tenues (<c>panel.input.*</c>) ;
/// - la correspondance effective des boutons du jeu lance : <c>deck.controls</c> ;
/// - le cadre reel ou RetroArch dessine le jeu, en pixels de l'ecran : <c>deck.frame</c> ;
/// - les cadres demandes par un theme du Deck (pour information : le cadre s'impose par le decor du lancement,
///   <see cref="DeckLaunchService"/>, H-D15 ; la voie A, un fichier ajoute par le lanceur, est abandonnee) ;
/// - le Deck du lancement en cours (champ <c>Deck</c> de <c>deck.game</c>) ;
/// - le sens des signaux du .MEM, lu dans son compagnon .SMEM (<c>deck.semantics</c>, voir <see cref="Smem"/>) ;
/// - le rang en direct de la partie (<c>deck.rank</c>, tenu par <see cref="DeckRankService"/>).
///
/// <c>deck.snapshot</c> reunit tout, envoye a la connexion (<c>/ws</c> et <c>/ws/deck</c>) et sur demande
/// (message <c>deck.snapshot.request</c>, ou <c>GET /api/v1/deck/snapshot</c>). L'API lit la manette ; le Deck jamais
/// (CDC PlayerDeck 0.3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeckStateService : IHostedService, IDisposable
{
    public const string Schema = "nelfeplay.deck.snapshot/1";

    /// <summary>Au-dela, un signal de plus remplace le plus ancien : un .MEM n'en suit jamais autant.</summary>
    private const int SignauxMax = 512;

    private static readonly TimeSpan AttenteDuJournal = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new();

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly RomCanonicalResolver? _canonique;
    private readonly ILogger<DeckStateService>? _journal;
    private readonly object _verrou = new();
    private readonly Dictionary<string, DeckSignal> _signaux = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DeckScore> _scores = new();
    private readonly Dictionary<string, DeckHeldInput> _tenues = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _abonnement;
    private Partie? _partie;
    private long _revision;

    public DeckFramePlanStore Cadres { get; }

    /// <summary>Les messages du choix du Deck en jeu (deck.choice.*), traites par <see cref="DeckLaunchService"/>.</summary>
    public Func<string, JsonElement, string?>? CommandesDuChoix { get; set; }

    /// <summary>Le rang en direct de la partie (<see cref="DeckRankService"/>, H-D21), repris dans l'instantane.</summary>
    public Func<DeckRank?>? Rang { get; set; }

    /// <summary>L'identifiant du lancement en cours ; null hors d'une partie.</summary>
    public string? LaunchIdCourant
    {
        get { lock (_verrou) return _partie is { } p && p.Etat != "ended" ? p.LaunchId : null; }
    }

    /// <summary>Le Deck du prochain lancement, prepare avant le lanceur ; rattache a la partie a son depart.</summary>
    private DeckLancement? _lancementPrepare;

    /// <summary>
    /// Le Deck du lancement que l'API va faire (null : lancement sans Deck). Rattache a la partie au crochet de depart si
    /// c'est le meme jeu et que la preparation date de moins de deux minutes.
    /// </summary>
    public void PoserLeLancement(DeckLancement? lancement)
    {
        lock (_verrou)
        {
            _lancementPrepare = lancement;
            _revision++;
        }
    }

    private static string DossierDeck => Path.Combine(RetroBatPaths.PluginRoot, "state", "deck");

    public DeckStateService(IEventBus bus, ApiContext contexte, RomCanonicalResolver? canonique = null, ILogger<DeckStateService>? journal = null)
    {
        _bus = bus;
        _contexte = contexte;
        _canonique = canonique;
        _journal = journal;
        Cadres = new DeckFramePlanStore(Path.Combine(DossierDeck, "cadres.json"));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // La voie A (cadre-session.cfg ajoute par le lanceur) est abandonnee (H-D15) : un reste d'une version d'essai part.
        try
        {
            var ancien = Path.Combine(DossierDeck, "cadre-session.cfg");
            if (File.Exists(ancien)) File.Delete(ancien);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        lock (_verrou)
        {
            _partie?.Arret.Cancel();
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    // ─────────────────────────────── evenements du bus ───────────────────────────────

    private void Recevoir(EventEnvelope e)
    {
        try
        {
            var type = e.Type ?? string.Empty;
            if (type.StartsWith("deck.", StringComparison.OrdinalIgnoreCase)) return; // les notres

            switch (type)
            {
                case "retroarch.memory.changed":
                case "ingame.memory.changed":
                    NoterSignal(e);
                    break;
                case "score.live.changed":
                    NoterScore(e);
                    break;
                case "panel.input.pressed":
                    NoterEntree(e, tenue: true);
                    break;
                case "panel.input.released":
                    NoterEntree(e, tenue: false);
                    break;
                case "panel.input.devices":
                    lock (_verrou)
                    {
                        if (_tenues.Count > 0) { _tenues.Clear(); _revision++; }
                    }
                    break;
                case "ui.game.started":
                    Demarrer(e);
                    break;
                case "ui.game.ended":
                    Terminer();
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Deck : evenement {Type} ignore.", e.Type);
        }
    }

    private void NoterSignal(EventEnvelope e)
    {
        var p = e.Payload;
        var s = Champ(p, "signal") ?? Champ(p, "Signal");
        if (s is null) return;

        var adresse = Texte(s, "Address");
        var canal = Texte(s, "Channel");
        var nom = Texte(s, "Name");
        if (string.IsNullOrEmpty(nom) && string.IsNullOrEmpty(adresse)) return;

        var signal = new DeckSignal(
            Key: Texte(s, "Key") ?? (canal + "." + nom),
            Channel: canal,
            Name: nom,
            Address: adresse,
            Value: Entier(Champ(s, "Value")),
            RawValueHex: Texte(s, "RawValueHex"),
            Player: (int?)Entier(Champ(s, "Player")) ?? (int?)Entier(Champ(p, "player")),
            Frame: Entier(Champ(s, "Frame")),
            Source: Texte(p, "Source"),
            SystemId: Texte(p, "SystemId"),
            Rom: Texte(p, "Rom"),
            DefinitionFile: Texte(p, "DefinitionFile"),
            Ts: e.Ts);

        Partie? aChercher = null;
        lock (_verrou)
        {
            var cle = (canal ?? "") + "." + (nom ?? "") + "@" + (adresse ?? "") + "#" + (signal.Player?.ToString() ?? "");
            if (!_signaux.ContainsKey(cle) && _signaux.Count >= SignauxMax)
            {
                var ancien = _signaux.MinBy(kv => kv.Value.Ts).Key;
                _signaux.Remove(ancien);
            }
            _signaux[cle] = signal;
            if (_partie is { } partie && partie.MemRom is null && !string.IsNullOrEmpty(signal.Rom))
            {
                partie.MemRom = signal.Rom;
                partie.DefinitionFile = signal.DefinitionFile;
                if (!partie.SemantiqueCherchee && !string.IsNullOrEmpty(partie.DefinitionFile))
                {
                    partie.SemantiqueCherchee = true;
                    aChercher = partie;
                }
            }
            _revision++;
        }

        if (aChercher is not null) ChargerLaSemantique(aChercher);
    }

    /// <summary>
    /// Le sens des signaux (.SMEM) du .MEM que le wrapper a charge, publie en deck.semantics une fois par lancement,
    /// des que le premier signal nomme son fichier de definition. Lecture seule : rien ici ne touche au score.
    /// </summary>
    private void ChargerLaSemantique(Partie partie)
    {
        var semantique = Smem.Semantique(partie.LaunchId, partie.SystemId, partie.Rom, partie.MemRom, partie.DefinitionFile,
            RetroBatPaths.RamResourcesRoot, 0);
        lock (_verrou)
        {
            if (!ReferenceEquals(_partie, partie) || partie.Etat == "ended") return;
            _revision++;
            semantique = semantique with { Revision = _revision };
            partie.Semantique = semantique;
        }
        if (semantique.Status == "invalid")
        {
            _journal?.LogWarning("Deck : .SMEM {Fichier} invalide : {Erreurs}", semantique.SmemFile, string.Join(" ; ", semantique.Errors));
        }
        Publier("deck.semantics", semantique);
    }

    private void NoterScore(EventEnvelope e)
    {
        var p = e.Payload;
        var score = p is ScoreLiveEvent s
            ? new DeckScore(s.Player, s.Score, s.ScoreKind, s.Source, s.SystemId, s.Rom, s.Confidence, s.Ts)
            : new DeckScore(
                (int)(Entier(Champ(p, "Player")) ?? 1),
                Entier(Champ(p, "Score")) ?? 0,
                Texte(p, "ScoreKind"),
                Texte(p, "Source"),
                Texte(p, "SystemId"),
                Texte(p, "Rom"),
                Texte(p, "Confidence"),
                e.Ts);
        lock (_verrou)
        {
            _scores[score.Player] = score;
            _revision++;
        }
    }

    private void NoterEntree(EventEnvelope e, bool tenue)
    {
        var p = e.Payload;
        var joueur = (int)(Entier(Champ(p, "Player")) ?? 1);
        var identite = Texte(p, "Identity") ?? "";
        var appareil = (int)(Entier(Champ(p, "Device")) ?? -1);
        var cle = joueur + "|" + appareil + "|" + identite;
        lock (_verrou)
        {
            if (tenue)
            {
                _tenues[cle] = new DeckHeldInput(joueur, identite, (int?)Entier(Champ(p, "Slot")), Texte(p, "System"), appareil, e.Ts);
            }
            else
            {
                _tenues.Remove(cle);
            }
            _revision++;
        }
    }

    // ─────────────────────────────── partie ───────────────────────────────

    private void Demarrer(EventEnvelope e)
    {
        var jeu = _contexte.Ui.Running;
        var systeme = jeu?.SystemId ?? Texte(e.Payload, "SystemId") ?? "";
        var chemin = jeu?.GamePath ?? Texte(e.Payload, "GamePath") ?? "";
        if (string.IsNullOrWhiteSpace(chemin)) return;

        Partie partie;
        lock (_verrou)
        {
            // ES annonce parfois deux fois le meme depart : on ne repart pas de zero
            if (_partie is { } courante && courante.Etat != "ended"
                && string.Equals(courante.GamePath, chemin, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - courante.StartedUtc < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _partie?.Arret.Cancel();
            _signaux.Clear();
            _scores.Clear();
            var rom = NomDeRom(chemin);
            // Le Deck prepare pour ce jeu par les boutons speciaux ; un autre lancement (ES) n'en a pas (H-D20)
            var deck = _lancementPrepare is { } l && DateTime.UtcNow - l.PreparedAtUtc < TimeSpan.FromMinutes(2) && MemeChemin(l.GamePath, chemin) ? l : null;
            _lancementPrepare = null;
            partie = new Partie
            {
                LaunchId = Guid.NewGuid().ToString("N"),
                SystemId = systeme,
                GamePath = chemin,
                Rom = rom,
                Name = FirstNonEmpty(jeu?.Details?.Name, jeu?.GameName, Path.GetFileNameWithoutExtension(chemin)),
                Emulator = jeu?.Launch?.Emulator,
                Core = jeu?.Launch?.Core,
                CanonicalSystem = RomCanonicalResolver.CanonicalScoringSystem(systeme),
                RomGroup = GamelistIdentity.Slugifier(rom),
                RomGroupSource = "file",
                StartedUtc = DateTime.UtcNow,
                Etat = "launching",
                Deck = deck,
            };
            _partie = partie;
            _revision++;
        }

        Publier("deck.game", JeuDe(partie));
        _ = Task.Run(() => SuivreAsync(partie, partie.Arret.Token));
    }

    private void Terminer()
    {
        Partie? partie;
        lock (_verrou)
        {
            partie = _partie;
            if (partie is null || partie.Etat == "ended") return;
            partie.Arret.Cancel();
            partie.Etat = "ended";
            partie.EndedUtc = DateTime.UtcNow;
            partie.Cadre = null;
            _signaux.Clear();
            _scores.Clear();
            _revision++;
        }

        Publier("deck.game", JeuDe(partie));
    }

    /// <summary>
    /// Suit un lancement : identite canonique, puis le journal de RetroArch jusqu'a la geometrie (coeur, remappage),
    /// puis le cadre reel de l'image tant que la partie dure (publie a chaque changement seulement).
    /// </summary>
    private async Task SuivreAsync(Partie partie, CancellationToken ct)
    {
        try
        {
            ResoudreLeGroupe(partie);

            var debut = DateTime.UtcNow;
            DeckLaunchLog.Lecture? lecture = null;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - debut < AttenteDuJournal)
            {
                lecture = LireLeJournal(partie.StartedUtc);
                if (lecture?.GeometrieConnue == true) break;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            lock (_verrou)
            {
                if (ct.IsCancellationRequested) return;
                partie.Journal = lecture is { GeometrieConnue: true } ? lecture : null;
                partie.Etat = "running";
                if (partie.Journal?.Core is { Length: > 0 } coeur) partie.Core = coeur;
                _revision++;
            }

            Publier("deck.game", JeuDe(partie));
            var commandes = ConstruireLesCommandes(partie, lecture);
            lock (_verrou)
            {
                partie.Controls = commandes;
                _revision++;
            }
            Publier("deck.controls", commandes);

            DeckVideoSettings? reglages = null;
            var depuis = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                reglages ??= LireLesReglages(lecture);
                var cadre = MesurerLeCadre(partie, reglages);
                var change = false;
                lock (_verrou)
                {
                    if (!Equals(cadre?.Cle, partie.Cadre?.Cle))
                    {
                        partie.Cadre = cadre;
                        _revision++;
                        change = cadre is not null;
                    }
                }
                if (change) Publier("deck.frame", cadre!);

                // serre au lancement (la fenetre apparait, se pose), puis lache : une fenetre ne bouge guere en partie
                var pas = DateTime.UtcNow - depuis < TimeSpan.FromSeconds(15) ? 1000 : 3000;
                await Task.Delay(pas, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "Deck : suivi du lancement interrompu.");
        }
    }

    private void ResoudreLeGroupe(Partie partie)
    {
        try
        {
            // le referentiel arcade pese 55 000 lignes : lu ici, hors du fil du bus
            var orientation = GamelistIdentity.DeclaredOrientation(partie.SystemId, partie.Name, partie.Rom);
            lock (_verrou) partie.OrientationDeclaree = orientation;
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Deck : orientation declaree illisible pour {Rom}.", partie.Rom);
        }

        if (_canonique is null) return;
        try
        {
            var fiche = _canonique.ResolveByRomName(partie.SystemId, partie.GamePath);
            if (fiche?.GameKey is { Length: > 0 } cle)
            {
                var barre = cle.IndexOf('/');
                lock (_verrou)
                {
                    partie.RomGroup = barre >= 0 ? cle[(barre + 1)..] : cle;
                    partie.RomGroupSource = "catalog";
                    if (!string.IsNullOrWhiteSpace(fiche.CanonicalSystem)) partie.CanonicalSystem = fiche.CanonicalSystem;
                    _revision++;
                }
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Deck : rom_group non resolu pour {Rom}.", partie.Rom);
        }
    }

    private static DeckLaunchLog.Lecture? LireLeJournal(DateTime demarreUtc)
    {
        try
        {
            var fichier = new FileInfo(RetroBatPaths.EsLaunchStdoutLogPath);
            // le journal d'avant le lancement parle du jeu precedent : RetroArch le reecrit en demarrant
            if (!fichier.Exists || fichier.LastWriteTimeUtc < demarreUtc - TimeSpan.FromSeconds(2)) return null;
            return DeckLaunchLog.Lire(LireLignes(fichier.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> LireLignes(string chemin)
    {
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lecteur = new StreamReader(flux);
        var lignes = new List<string>();
        string? ligne;
        while ((ligne = lecteur.ReadLine()) is not null) lignes.Add(ligne);
        return lignes;
    }

    // ─────────────────────────────── boutons ───────────────────────────────

    private DeckControlsPayload ConstruireLesCommandes(Partie partie, DeckLaunchLog.Lecture? lecture)
    {
        Dictionary<(int Player, string Identity), int>? remap = null;
        var remapSource = "core-default";
        var suppose = true;
        string? remapChemin = null, remapPortee = null;
        if (lecture is not null)
        {
            suppose = false;
            remapSource = "none";
            if (lecture.RemapPath is { Length: > 0 } chemin)
            {
                remapChemin = chemin;
                remapPortee = lecture.RemapScope;
                try
                {
                    remap = DeckControls.LireRemap(LireLignes(chemin));
                    remapSource = "retroarch.log";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // le fichier a disparu depuis le lancement : on ne sait plus ce qu'il disait
                    remapSource = "core-default";
                    suppose = true;
                }
            }
        }

        var coeur = lecture?.Core ?? partie.Core;
        var bibliotheque = remapChemin is not null ? Path.GetFileName(Path.GetDirectoryName(remapChemin)) : null;
        if (string.IsNullOrWhiteSpace(bibliotheque) && !string.IsNullOrWhiteSpace(coeur))
        {
            var info = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "info", coeur + "_libretro.info");
            try
            {
                if (File.Exists(info)) bibliotheque = DeckControls.NomDeBibliotheque(File.ReadLines(info));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Dictionary<int, Dictionary<int, DeckControls.FonctionDuCoeur>> sonde = new();
        Dictionary<string, (string? Label, string? Color)> systeme = new();
        string? fichierDynpanel = null;
        foreach (var candidat in new[]
                 {
                     Path.Combine(RetroBatPaths.DynPanelsGamesRoot, partie.Rom.Replace(' ', '_') + ".json"),
                     Path.Combine(RetroBatPaths.DynPanelsSystemsRoot, partie.SystemId.ToLowerInvariant() + ".json"),
                 })
        {
            try
            {
                if (!File.Exists(candidat)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(candidat));
                sonde = DeckControls.LireSonde(doc.RootElement, bibliotheque);
                systeme = DeckControls.LireBoutonsSysteme(doc.RootElement);
                fichierDynpanel = Path.GetFileName(candidat);
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        var joueurs = new SortedSet<int> { 1, 2 };
        foreach (var j in sonde.Keys) joueurs.Add(j);
        if (remap is not null) foreach (var (j, _) in remap.Keys) joueurs.Add(j);

        var parJoueur = joueurs
            .Select(j => new DeckPlayerControls(j, DeckControls.Composer(j, remap, sonde.TryGetValue(j, out var s) ? s : null, systeme)))
            .ToList();

        return new DeckControlsPayload(
            LaunchId: partie.LaunchId,
            SystemId: partie.SystemId,
            Rom: partie.Rom,
            Core: coeur,
            CoreLibrary: bibliotheque,
            Remap: new DeckRemapInfo(remapSource, remapPortee, remapChemin, suppose),
            Functions: new DeckFunctionsInfo(sonde.Count > 0 ? "dynpanel.probe" : "none", fichierDynpanel),
            Players: parJoueur,
            Revision: Interlocked.Read(ref _revision));
    }

    // ─────────────────────────────── cadre ───────────────────────────────

    private static DeckVideoSettings? LireLesReglages(DeckLaunchLog.Lecture? lecture)
    {
        try
        {
            var cfg = DeckViewport.LireCfg(LireLignes(RetroBatPaths.RetroArchConfigPath));
            foreach (var ajout in lecture?.AppendedConfigs ?? [])
            {
                if (File.Exists(ajout)) DeckViewport.LireCfg(LireLignes(ajout), cfg);
            }
            return DeckVideoSettings.Depuis(cfg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private DeckFramePayload? MesurerLeCadre(Partie partie, DeckVideoSettings? reglages)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var mesure = DeckRetroArchWindow.Mesurer();
        if (mesure is not { } m) return null;

        DeckLaunchLog.Lecture? lecture;
        lock (_verrou) lecture = partie.Journal;
        int largeur = 0, hauteur = 0;
        if (lecture is { BaseWidth: { } bw, BaseHeight: { } bh })
        {
            (largeur, hauteur, _) = DeckLaunchLog.Affichage(bw, bh, lecture.Rotation, lecture.Aspect);
        }

        var plan = PlanDuDecor(partie) ?? Cadres.Pour(partie.SystemId, partie.Rom);
        if (reglages is null)
        {
            return new DeckFramePayload(partie.LaunchId, partie.SystemId, partie.Rom, m.Client, null, null, "unknown", null, null,
                false, false, null, "window", PlanDe(plan, null, m.Client, lecture), Interlocked.Read(ref _revision));
        }

        var (dansLaFenetre, approche) = DeckViewport.Calculer(reglages, m.Client.Width, m.Client.Height, largeur, hauteur, lecture?.Aspect);
        var image = dansLaFenetre.Decale(m.Client.X, m.Client.Y);
        DeckPixelScale? echelle = largeur > 0 && hauteur > 0
            ? new DeckPixelScale(Math.Round((double)dansLaFenetre.Width / largeur, 4), Math.Round((double)dansLaFenetre.Height / hauteur, 4))
            : null;
        return new DeckFramePayload(
            LaunchId: partie.LaunchId,
            SystemId: partie.SystemId,
            Rom: partie.Rom,
            Window: m.Client,
            Viewport: image,
            ViewportInWindow: dansLaFenetre,
            Mode: DeckViewport.Mode(reglages),
            AspectRatioIndex: reglages.AspectRatioIndex,
            Aspect: DeckViewport.FormatVoulu(reglages, largeur, hauteur, lecture?.Aspect) is { } f ? Math.Round(f, 4) : null,
            ScaleInteger: reglages.ScaleInteger,
            Approximate: approche,
            PixelScale: echelle,
            Source: "window+config",
            Plan: PlanDe(plan, dansLaFenetre, m.Client, lecture),
            Revision: Interlocked.Read(ref _revision));
    }

    /// <summary>Le cadre impose par le decor du lancement (son trou), s'il est pose.</summary>
    private static DeckFramePlan? PlanDuDecor(Partie partie)
        => partie.Deck is { Status: "posed", Decor: { } d } l
            ? new DeckFramePlan { SystemId = partie.SystemId, Rom = partie.Rom, X = d.Hole.X, Y = d.Hole.Y, Width = d.Hole.Width, Height = d.Hole.Height,
                ScreenWidth = d.Screen.Width, ScreenHeight = d.Screen.Height, Theme = l.DeckId }
            : null;

    /// <summary>
    /// Le sort du cadre demande : applied (le viewport recalcule tombe sur le cadre a un pixel pres), sinon screen_mismatch
    /// (zone de RetroArch d'une autre taille que l'ecran du decor) ou decor_not_posed (aucun decor de ce lancement ne
    /// l'impose : un cadre demande par le Deck hors d'un lancement des boutons speciaux reste une information). Pur.
    /// </summary>
    internal static DeckFramePlanStatus? PlanDe(DeckFramePlan? plan, DeckRect? obtenu, DeckRect fenetre, DeckLaunchLog.Lecture? lecture)
    {
        if (plan is null) return null;
        string etat, raison;
        if (obtenu is { } o && Math.Abs(o.X - plan.X) <= 1 && Math.Abs(o.Y - plan.Y) <= 1
            && Math.Abs(o.Width - plan.Width) <= 1 && Math.Abs(o.Height - plan.Height) <= 1)
        {
            (etat, raison) = ("applied", "");
        }
        else if ((plan.ScreenWidth is { } sw && sw != fenetre.Width) || (plan.ScreenHeight is { } sh && sh != fenetre.Height))
        {
            (etat, raison) = ("not-applied", "screen_mismatch");
        }
        else
        {
            (etat, raison) = ("not-applied", "decor_not_posed");
        }
        return new DeckFramePlanStatus(plan.Rect, plan.Theme, etat, raison);
    }

    private static bool MemeChemin(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b.Trim('"')), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─────────────────────────────── (voie A abandonnee) ───────────────────────────────

    // ─────────────────────────────── commandes du Deck ───────────────────────────────

    /// <summary>
    /// Un message envoye par un client sur le WebSocket. Rend le JSON a lui repondre, ou null. <paramref name="connexion"/>
    /// identifie la connexion : un cadre demande ainsi vit avec elle.
    /// </summary>
    public string? Commande(object connexion, string type, JsonElement corps)
    {
        switch (type.ToLowerInvariant())
        {
            case "deck.snapshot.request":
                return SnapshotJson("request");

            case "deck.choice.next":
            case "deck.choice.set":
                return CommandesDuChoix?.Invoke(type, corps);

            case "deck.frame.request":
            {
                DeckFramePlan? plan = null;
                try
                {
                    var source = corps.TryGetProperty("Frame", out var f) || corps.TryGetProperty("frame", out f) ? f : corps;
                    plan = JsonSerializer.Deserialize<DeckFramePlan>(source.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                }

                var refus = plan is null ? "invalid_frame" : Cadres.Poser(plan with { RequestedAtUtc = DateTime.UtcNow, Persist = false }, connexion);
                return Accuse("deck.frame.request", refus, plan);
            }

            case "deck.frame.clear":
            {
                var systeme = corps.TryGetProperty("SystemId", out var s) || corps.TryGetProperty("systemId", out s) ? s.GetString() : null;
                var rom = corps.TryGetProperty("Rom", out var r) || corps.TryGetProperty("rom", out r) ? r.GetString() : null;
                Cadres.Retirer(systeme, rom, connexion);
                return Accuse("deck.frame.clear", null, null);
            }
        }

        return null;
    }

    /// <summary>La connexion d'un Deck s'est fermee : ses cadres (non gardes) partent avec elle.</summary>
    public void ConnexionFermee(object connexion) => Cadres.Oublier(connexion);

    private static string Accuse(string type, string? refus, DeckFramePlan? plan)
        => JsonSerializer.Serialize(new EventEnvelope
        {
            Type = "deck.ack",
            Payload = new { Request = type, Accepted = refus is null, Reason = refus, Plan = plan }
        });

    // ─────────────────────────────── instantane ───────────────────────────────

    public DeckSnapshot Snapshot(string raison)
    {
        lock (_verrou)
        {
            var partie = _partie;
            return new DeckSnapshot(
                Schema: Schema,
                Revision: _revision,
                Reason: raison,
                GeneratedAtUtc: DateTime.UtcNow,
                Game: partie is null ? null : JeuDeVerrouille(partie),
                Scores: _scores.Values.OrderBy(s => s.Player).ToList(),
                Signals: _signaux.Values.OrderBy(s => s.Address, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                HeldInputs: _tenues.Values.OrderBy(t => t.Player).ThenBy(t => t.Identity, StringComparer.Ordinal).ToList(),
                Controls: partie?.Etat == "ended" ? null : partie?.Controls,
                Frame: partie?.Etat == "ended" ? null : partie?.Cadre,
                FramePlans: Cadres.Tous(),
                Semantics: partie?.Etat == "ended" ? null : partie?.Semantique,
                Rank: partie?.Etat == "ended" ? null : Rang?.Invoke());
        }
    }

    /// <summary>L'instantane en enveloppe JSON, pret a partir sur un WebSocket.</summary>
    public string SnapshotJson(string raison)
        => JsonSerializer.Serialize(new EventEnvelope { Type = "deck.snapshot", Payload = Snapshot(raison) }, Json);

    // ─────────────────────────────── aides ───────────────────────────────

    private void Publier(string type, object payload)
    {
        _ = _bus.PublishAsync(new EventEnvelope { Type = type, Payload = payload });
    }

    private DeckGame JeuDe(Partie partie)
    {
        lock (_verrou) return JeuDeVerrouille(partie);
    }

    private DeckGame JeuDeVerrouille(Partie partie)
    {
        DeckVideo? video = null;
        if (partie.Journal is { BaseWidth: { } bw, BaseHeight: { } bh } j)
        {
            var (w, h, orientation) = DeckLaunchLog.Affichage(bw, bh, j.Rotation, j.Aspect);
            video = new DeckVideo(bw, bh, j.Rotation ?? 0, (j.Rotation ?? 0) * 90, w, h, j.Aspect, j.Fps, orientation, "retroarch.log", j.Crc32);
        }

        var orientationDeclaree = video is null ? partie.OrientationDeclaree : null;

        return new DeckGame(
            LaunchId: partie.LaunchId,
            State: partie.Etat,
            SystemId: partie.SystemId,
            CanonicalSystem: partie.CanonicalSystem,
            Rom: partie.Rom,
            RomGroup: partie.RomGroup,
            RomGroupSource: partie.RomGroupSource,
            Name: partie.Name,
            GamePath: partie.GamePath,
            Emulator: partie.Emulator,
            Core: partie.Core,
            MemRom: partie.MemRom,
            DefinitionFile: partie.DefinitionFile,
            StartedAtUtc: partie.StartedUtc,
            EndedAtUtc: partie.EndedUtc,
            Video: video,
            Orientation: video?.Orientation ?? orientationDeclaree,
            OrientationSource: video is not null ? "core" : orientationDeclaree is not null ? "catalog" : null,
            Revision: _revision,
            Deck: partie.Deck);
    }

    private static string NomDeRom(string chemin) => Path.GetFileNameWithoutExtension(chemin).Trim().ToLowerInvariant();

    private static string FirstNonEmpty(params string?[] valeurs) => valeurs.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Proprietes = new();

    /// <summary>Un champ d'une charge du bus (objet anonyme, type nomme ou JsonElement), sans casse.</summary>
    internal static object? Champ(object? source, string nom)
    {
        if (source is null) return null;
        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name.Equals(nom, StringComparison.OrdinalIgnoreCase)) return p.Value;
            }
            return null;
        }

        var info = Proprietes.GetOrAdd((source.GetType(), nom),
            cle => cle.Item1.GetProperty(cle.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        return info?.GetValue(source);
    }

    internal static string? Texte(object? source, string nom)
    {
        var v = Champ(source, nom);
        var t = v switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
            JsonElement => null,
            _ => v.ToString()
        };
        return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
    }

    internal static long? Entier(object? valeur) => valeur switch
    {
        null => null,
        long n => n,
        int n => n,
        short n => n,
        byte n => n,
        uint n => n,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var n) => n,
        JsonElement { ValueKind: JsonValueKind.String } e when long.TryParse(e.GetString(), out var n) => n,
        string s when long.TryParse(s, out var n) => n,
        _ => null
    };

    private sealed class Partie
    {
        public required string LaunchId;
        public required string SystemId;
        public required string GamePath;
        public required string Rom;
        public required string Name;
        public string? Emulator;
        public string? Core;
        public string CanonicalSystem = "";
        public string RomGroup = "";
        public string RomGroupSource = "file";
        public string? MemRom;
        public string? DefinitionFile;
        public string? OrientationDeclaree;
        public DateTime StartedUtc;
        public DateTime? EndedUtc;
        public string Etat = "launching";
        public DeckLaunchLog.Lecture? Journal;
        public DeckControlsPayload? Controls;
        public DeckFramePayload? Cadre;
        public DeckSemantics? Semantique;
        public bool SemantiqueCherchee;
        public DeckLancement? Deck;
        public readonly CancellationTokenSource Arret = new();
    }
}

// ─────────────────────────────── messages (contrat additif : on ajoute, on ne renomme jamais) ───────────────────────────────

/// <summary>
/// deck.game : le jeu lance. <c>State</c> = launching (crochet game-start), running (RetroArch a annonce sa geometrie, ou
/// 60 s sans journal), ended. <c>Deck</c> : le Deck de ce lancement (decor pose, Off...), null pour un lancement sans Deck.
/// </summary>
public sealed record DeckGame(
    string LaunchId,
    string State,
    string SystemId,
    string CanonicalSystem,
    string Rom,
    string RomGroup,
    string RomGroupSource,
    string Name,
    string GamePath,
    string? Emulator,
    string? Core,
    string? MemRom,
    string? DefinitionFile,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    DeckVideo? Video,
    string? Orientation,
    string? OrientationSource,
    long Revision,
    DeckLancement? Deck = null);

/// <summary>
/// La geometrie annoncee par le coeur. <c>BaseWidth</c>/<c>BaseHeight</c> : le tampon tel que le coeur le rend (non
/// tourne) ; <c>Width</c>/<c>Height</c> : l'image une fois tournee ; <c>Aspect</c> : la forme de l'image affichee
/// donnee par le coeur (0,75 pour 19XX).
/// </summary>
public sealed record DeckVideo(
    int BaseWidth,
    int BaseHeight,
    int Rotation,
    int RotationDegrees,
    int Width,
    int Height,
    double? Aspect,
    double? Fps,
    string Orientation,
    string Source,
    string? Crc32);

public sealed record DeckSignal(
    string Key,
    string? Channel,
    string? Name,
    string? Address,
    long? Value,
    string? RawValueHex,
    int? Player,
    long? Frame,
    string? Source,
    string? SystemId,
    string? Rom,
    string? DefinitionFile,
    DateTime Ts);

public sealed record DeckScore(
    int Player,
    long Score,
    string? ScoreKind,
    string? Source,
    string? SystemId,
    string? Rom,
    string? Confidence,
    DateTime Ts);

public sealed record DeckHeldInput(int Player, string Identity, int? Slot, string? System, int Device, DateTime SinceUtc);

public sealed record DeckRemapInfo(string Source, string? Scope, string? Path, bool Assumed);

public sealed record DeckFunctionsInfo(string Source, string? File);

public sealed record DeckPlayerControls(int Player, IReadOnlyList<DeckControls.Bouton> Buttons);

/// <summary>deck.controls : chaque bouton du panel et ce qu'il fait reellement dans le jeu lance.</summary>
public sealed record DeckControlsPayload(
    string LaunchId,
    string SystemId,
    string Rom,
    string? Core,
    string? CoreLibrary,
    DeckRemapInfo Remap,
    DeckFunctionsInfo Functions,
    IReadOnlyList<DeckPlayerControls> Players,
    long Revision);

public sealed record DeckPixelScale(double X, double Y);

public sealed record DeckFramePlanStatus(DeckRect Requested, string? Theme, string Status, string Reason);

/// <summary>
/// deck.frame : ou RetroArch dessine le jeu. <c>Window</c> est mesuree (zone cliente, pixels physiques de l'ecran) ;
/// <c>Viewport</c> en est deduit avec les reglages que RetroArch a lus (retroarch.cfg et fichiers ajoutes),
/// <c>ViewportInWindow</c> est le meme rectangle relatif a la zone cliente. <c>PixelScale</c> : pixels d'ecran par
/// pixel du jeu.
/// </summary>
public sealed record DeckFramePayload(
    string LaunchId,
    string SystemId,
    string Rom,
    DeckRect Window,
    DeckRect? Viewport,
    DeckRect? ViewportInWindow,
    string Mode,
    int? AspectRatioIndex,
    double? Aspect,
    bool ScaleInteger,
    bool Approximate,
    DeckPixelScale? PixelScale,
    string Source,
    DeckFramePlanStatus? Plan,
    long Revision)
{
    /// <summary>Ce qui fait qu'un cadre a change (la revision n'en fait pas partie).</summary>
    internal string Cle => $"{Window}|{Viewport}|{Mode}|{Aspect}|{Plan?.Status}|{Plan?.Requested}";
}

/// <summary>deck.snapshot : tout l'etat du Deck a un instant, avec sa revision.</summary>
public sealed record DeckSnapshot(
    string Schema,
    long Revision,
    string Reason,
    DateTime GeneratedAtUtc,
    DeckGame? Game,
    IReadOnlyList<DeckScore> Scores,
    IReadOnlyList<DeckSignal> Signals,
    IReadOnlyList<DeckHeldInput> HeldInputs,
    DeckControlsPayload? Controls,
    DeckFramePayload? Frame,
    IReadOnlyList<DeckFramePlan> FramePlans,
    DeckSemantics? Semantics = null,
    DeckRank? Rank = null);
