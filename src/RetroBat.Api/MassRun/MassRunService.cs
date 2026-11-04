using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.MassRun;

/// <summary>
/// UNE SALLE MASS RUN LANCEE PAR L'API (CDC MASS RUN, lot L2). L'API pose la salle dans le canal (<see cref="MassRunCanal"/>) :
/// niveau, heure du GO!, pseudo et couleur d'avatar du joueur, etat de depart ; elle lance le jeu sur le coeur hote, puis, tant
/// que la partie tourne, ecrit les coureurs de chaque image et lit la partie du joueur.
///
/// En L2 les coureurs viennent des pistes .npiste enregistrees sur la borne (coureurs simules) ; au lot L3 ils viendront du
/// noeud de la salle, par le meme canal. Une partie sur le coeur hote n'est jamais classee (voir NelfePlayScoringReporter).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MassRunService : IDisposable
{
    /// <summary>Super Mario Bros. (World), le seul jeu du lot L2 : son contenu, sa ROM, ses etats d'epreuve.</summary>
    private const uint CrcSmb = 0x393a432f;
    private const string CoeurHote = "fceumm_ghosts";

    private readonly ApiContext _contexte;
    private readonly IEventBus _bus;
    private readonly NelfePlayAgentService? _agent;
    private readonly ILogger<MassRunService> _journal;
    private readonly object _verrou = new();
    private MassRunCanal? _canal;
    private Thread? _boucle;
    private volatile bool _arret;
    private SalleLocale? _salle;

    private readonly RetroBat.Api.Reseau.ServiceDeCarte? _carte;
    private readonly IPrioritesDuJeu? _priorites;
    private readonly InterfaceTextService? _textes;
    private readonly EmulationStationSettingsService? _reglages;

    /// <summary>Le classement fige reste a l'ecran ce temps apres l'arrivee, puis le jeu se ferme : le bilan prend la suite.</summary>
    private static readonly TimeSpan TenueDeLArrivee = TimeSpan.FromSeconds(10);

    /// <summary>
    /// LA COURSE S'ARRETE A L'HEURE DE LA SALLE (essai du 04/11, salle 140076) : le coeur compte sa limite en images, et une borne
    /// a 35 images/s a couru 5 min 17 pour 3 min. Passe ce temps apres la limite, la borne ferme le jeu comme les autres.
    /// </summary>
    private static readonly TimeSpan GraceDeLaLimite = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Les textes dessines par le coeur, dans l'ordre de ses emplacements. Le coeur est en anglais par defaut (decision du user du
    /// 04/11) : une cle sans traduction laisse son emplacement vide.
    /// </summary>
    internal static readonly string[] ClesDesTextes =
    [
        "massrun.core.time_up", "massrun.core.eliminated", "massrun.core.finish", "massrun.core.start", "massrun.core.ranking",
        "massrun.core.you", "massrun.core.finished", "massrun.core.waiting_room", "massrun.core.explore", "massrun.core.select_back",
    ];

    private IReadOnlyList<string> TextesDuCoeur()
    {
        string? langue = null;
        try { langue = _reglages?.GetScrapingSettings().Language; }
        catch (Exception e) when (e is IOException or InvalidOperationException) { _journal.LogDebug(e, "MASS RUN : langue illisible."); }
        return ClesDesTextes.Select(cle => _textes?.Text(cle, langue) is { Length: > 0 } t && t != cle ? t.ToUpperInvariant() : "").ToList();
    }

    /// <summary>La fin de la course a-t-elle sonne ? Pur.</summary>
    internal static bool LimiteDepassee(DateTime? go, int limite, DateTime maintenant)
        => go is { } depart && limite > 0 && maintenant > depart.AddSeconds(limite) + GraceDeLaLimite;

    public MassRunService(ApiContext contexte, IEventBus bus, ILogger<MassRunService> journal, NelfePlayAgentService? agent = null,
        RetroBat.Api.Reseau.ServiceDeCarte? carte = null, IPrioritesDuJeu? priorites = null, InterfaceTextService? textes = null,
        EmulationStationSettingsService? reglages = null)
    {
        _priorites = priorites;
        _textes = textes;
        _reglages = reglages;
        _contexte = contexte;
        _bus = bus;
        _journal = journal;
        _agent = agent;
        _carte = carte;
    }

    /// <summary>
    /// LE NOEUD DES SALLES : NELFEPLAY_MASSRUN_NOEUD (essais), sinon le premier noeud que la carte signee du reseau arme du
    /// role « massrun » (MASS RUN public, 04/11), sinon le noeud d'essai local.
    /// </summary>
    public string NoeudCourant
    {
        get
        {
            if (Environment.GetEnvironmentVariable("NELFEPLAY_MASSRUN_NOEUD") is { Length: > 0 } impose) return impose;
            try
            {
                if (_carte?.Actuelle.AvecLeRole("massrun").FirstOrDefault() is { } noeud) return noeud.Url.TrimEnd('/');
            }
            catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
            {
            }
            return "http://127.0.0.1:8082";
        }
    }

    public sealed record SalleLocale(string Niveau, int Attente, string Pseudo, string Couleur, int Coureurs, int Pistes, string Rom)
    {
        public DateTime? GoUtc { get; set; }
        public string? Noeud { get; init; }
        public uint Salle { get; init; }
        public ushort Place { get; init; }
        public int Limite { get; init; }
        public bool Survie { get; init; }
        public long Instantanes { get; set; }
        public long EcartHorlogeMs { get; set; }
        public int CoureursVus { get; set; }
        public uint Mode { get; set; }
        public uint ImageCourse { get; set; }
        public uint Arrivee { get; set; }
        public bool Finie { get; set; }

        /// <summary>Le mode le plus avance que le coeur a atteint (2 salle d'attente, 4 course, 5 arrivee ou temps ecoule).</summary>
        public uint ModeMax { get; set; }

        /// <summary>MASS SURVIVAL : la premiere mort a elimine le joueur.</summary>
        public bool Elimine { get; set; }

        /// <summary>L'heure de la salle est passee, course en cours : la borne a ferme le jeu.</summary>
        public bool TempsEcoule { get; set; }
    }

    /// <summary>
    /// La salle du reseau est quittee (RetroArch ferme) : le panneau des defis ouvre la page de bilan de la course. Seulement
    /// pour une salle jouee (pas l'essai du reseau sans jeu).
    /// </summary>
    public event Action<SalleLocale>? SalleQuittee;

    public SalleLocale? Salle => _salle;


    /// <summary>
    /// Une salle ouverte d'un noeud : les heures sont ramenees a l'horloge de la borne. <c>InscriptionsUtc</c> : la fin des
    /// inscriptions, que l'onglet d'ES decompte ; le GO! part une minute apres pour les salles du programme (noeud 0.5.1),
    /// 6 s avant le GO! pour un noeud plus ancien.
    /// </summary>
    public sealed record SalleOuverte(uint Id, string Jeu, string Niveau, string Mort, DateTime GoUtc, int Limite, int Inscrits, int Arrives,
        DateTime InscriptionsUtc)
    {
        public bool Inscriptions(DateTime maintenant) => maintenant < InscriptionsUtc;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };

    /// <summary>Les salles ouvertes du noeud ; vide si le noeud ne repond pas (l'onglet MASS RUN n'existe alors pas).</summary>
    public async Task<IReadOnlyList<SalleOuverte>> LireLesSallesAsync(CancellationToken ct)
    {
        try
        {
            var json = JsonDocument.Parse(await Http.GetStringAsync(NoeudCourant.TrimEnd('/') + "/massrun/v1/salles", ct).ConfigureAwait(false)).RootElement;
            var ecart = DateTime.UtcNow - DateTime.UnixEpoch.AddMilliseconds(json.GetProperty("maintenant_unix_ms").GetInt64());
            return json.GetProperty("salles").EnumerateArray().Select(s =>
            {
                var go = DateTime.UnixEpoch.AddMilliseconds(s.GetProperty("go_unix_ms").GetInt64()) + ecart;
                var inscriptions = s.TryGetProperty("inscriptions_unix_ms", out var fin) && fin.TryGetInt64(out var ms)
                    ? DateTime.UnixEpoch.AddMilliseconds(ms) + ecart
                    : go.AddSeconds(-6);
                return new SalleOuverte(
                    s.GetProperty("salle").GetUInt32(),
                    s.GetProperty("jeu").GetString() ?? "",
                    s.GetProperty("niveau").GetString() ?? "",
                    s.GetProperty("mort").GetString() ?? "debut",
                    go,
                    s.GetProperty("limite").GetInt32(),
                    s.GetProperty("inscrits").GetInt32(),
                    s.GetProperty("arrives").GetInt32(),
                    inscriptions);
            }).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Lance une salle d'essai sur la borne. Leve <see cref="InvalidOperationException"/> si elle ne peut pas partir.</summary>
    public SalleLocale Lancer(string niveau, int attenteSecondes, int coureurs, int limiteSecondes = 300)
    {
        lock (_verrou)
        {
            // Un rendu de film n'est pas un jeu (essai du 04/11) : le lancement l'annule en annoncant la partie.
            if (EmulatorForeground.EmulateurDeJeuTourne())
            {
                throw new InvalidOperationException("un jeu tourne deja");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(niveau, "^[1-8]-[1-4]$"))
            {
                throw new InvalidOperationException("niveau inconnu : " + niveau);
            }
            var rom = Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "nes", "Super Mario Bros. (World).zip");
            if (!File.Exists(rom))
            {
                throw new InvalidOperationException("ROM absente : " + rom);
            }
            var cheminEtat = Path.Combine(EtatsDesDefis.Racine, "nes", "super-mario-bros", niveau + "-panneau.state");
            if (!File.Exists(cheminEtat))
            {
                throw new InvalidOperationException("etat de depart absent : " + cheminEtat);
            }
            var etat = EtatRetroArch.Brut(File.ReadAllBytes(cheminEtat));

            var statut = _agent?.Status;
            var pseudo = (statut?.Pseudo is { Length: > 0 } p ? p : "JOUEUR").ToUpperInvariant();
            var poignee = statut?.PlayerCode is { Length: > 0 } code ? code : pseudo;
            var couleur = MultiplayerPanelService.CouleurDe(poignee);

            var pistes = LirePistes(niveau);
            var simules = FormerCoureurs(pistes, coureurs);

            Arreter();
            _canal ??= new MassRunCanal();
            // Le GO! n'est fixe qu'une fois le jeu charge (le coeur passe en salle d'attente) : la salle d'attente dure alors ce
            // qui a ete demande, quel que soit le temps de chargement (12 s mesurees sur la borne le 04/11).
            _canal.PoserSalle(CrcSmb, null, limiteSecondes, Convert.ToUInt32(couleur, 16), pseudo.Length > 10 ? pseudo[..10] : pseudo, niveau, etat, textes: TextesDuCoeur());

            var arguments = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur(
                RetroBat.Api.Netplay.EsManettes.PourLancer(_journal).Arguments, "nes", "libretro", CoeurHote, rom, null);
            if (arguments is null)
            {
                _canal.Fermer();
                throw new InvalidOperationException("arguments du lanceur refuses");
            }
            RetroBat.Api.Speedrun.FantomesDuSpeedrun.ViderLeDossier();
            var salle = new SalleLocale(niveau, attenteSecondes, pseudo, couleur, simules.Count, pistes.Count, rom);
            _salle = salle;
            _arret = false;
            var canal = _canal;
            // La boucle d'abord : le lancement direct rend la main tard (30 s mesurees), le jeu tourne deja.
            _boucle = new Thread(() => Boucle(canal, salle, simules)) { IsBackground = true, Name = "MassRun", Priority = ThreadPriority.AboveNormal };
            _boucle.Start();
            _ = Task.Run(() =>
            {
                if (LancementDirect.Lancer(arguments, "nes", rom, _contexte, _bus, _journal) is null)
                {
                    _journal.LogWarning("MASS RUN : emulatorLauncher n'a pas demarre");
                    _arret = true;
                }
            });
            _journal.LogInformation("MASS RUN : salle {Niveau} lancee, {Attente} s d'attente, {Coureurs} coureurs simules ({Pistes} pistes), {Pseudo} #{Couleur}",
                niveau, attenteSecondes, simules.Count, pistes.Count, pseudo, couleur);
            return salle;
        }
    }

    /// <summary>
    /// REJOINDRE UNE SALLE D'UN NOEUD (lot L3) : la borne prend sa place, cale son horloge, pose la salle dans le canal avec le
    /// GO! du noeud, lance le jeu (sauf <paramref name="lancerLeJeu"/> = false, pour verifier le reseau sans ecran), envoie ses
    /// positions et ecrit les coureurs recus.
    /// </summary>
    public async Task<SalleLocale> RejoindreAsync(string noeud, uint id, bool lancerLeJeu, CancellationToken ct)
    {
        var statut = _agent?.Status;
        var pseudo = (statut?.Pseudo is { Length: > 0 } p ? p : "JOUEUR").ToUpperInvariant();
        if (pseudo.Length > 10) pseudo = pseudo[..10];
        var poignee = statut?.PlayerCode is { Length: > 0 } code ? code : pseudo;
        var couleur = MultiplayerPanelService.CouleurDe(poignee);
        if (lancerLeJeu && EmulatorForeground.EmulateurDeJeuTourne())
        {
            throw new InvalidOperationException("un jeu tourne deja");
        }
        var client = await ClientDeSalle.RejoindreAsync(noeud, id, pseudo, couleur, ct).ConfigureAwait(false);
        lock (_verrou)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(client.Niveau, "^[1-8]-[1-4]$"))
            {
                client.Dispose();
                throw new InvalidOperationException("niveau inconnu : " + client.Niveau);
            }
            var rom = Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "nes", "Super Mario Bros. (World).zip");
            var etat = EtatRetroArch.Brut(File.ReadAllBytes(Path.Combine(EtatsDesDefis.Racine, "nes", "super-mario-bros", client.Niveau + "-panneau.state")));
            RetroBat.Api.Speedrun.FantomesDuSpeedrun.ViderLeDossier();
            Arreter();
            _canal ??= new MassRunCanal();
            _canal.PoserSalle(CrcSmb, client.GoLocalUtc, client.Limite, Convert.ToUInt32(couleur, 16), pseudo, client.Niveau, etat, client.RegleMort, TextesDuCoeur());
            var salle = new SalleLocale(client.Niveau, 0, pseudo, couleur, 0, 0, rom)
            {
                GoUtc = client.GoLocalUtc, Noeud = noeud, Salle = client.Salle, Place = client.Place, Limite = client.Limite,
                Survie = client.RegleMort == 2,
            };
            _salle = salle;
            _arret = false;
            var canal = _canal;
            _boucle = new Thread(() => BoucleReseau(canal, salle, client, lancerLeJeu)) { IsBackground = true, Name = "MassRun", Priority = ThreadPriority.AboveNormal };
            _boucle.Start();
            if (lancerLeJeu)
            {
                var arguments = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur(
                    RetroBat.Api.Netplay.EsManettes.PourLancer(_journal).Arguments, "nes", "libretro", CoeurHote, rom, null);
                if (arguments is not null)
                {
                    _ = Task.Run(() => LancementDirect.Lancer(arguments, "nes", rom, _contexte, _bus, _journal));
                }
            }
            _journal.LogInformation("MASS RUN : salle {Salle} du noeud {Noeud} rejointe, place {Place}, {Niveau}, GO! a {Go:HH:mm:ss.fff} (ecart d'horloge {Ecart} ms)",
                client.Salle, noeud, client.Place, client.Niveau, client.GoLocalUtc.ToLocalTime(), client.EcartHorlogeMs);
            return salle;
        }
    }

    /// <summary>
    /// La boucle d'une salle du reseau : a chaque image publiee par le coeur, la fenetre des coureurs (leur derniere position
    /// recue, tenue sur les 4 images) ; 15 fois par seconde au plus, la position du joueur ; l'arrivee, trois fois.
    /// Sans jeu (essai du reseau), la boucle tourne 60 s et compte les instantanes recus.
    /// </summary>
    private void BoucleReseau(MassRunCanal canal, SalleLocale salle, ClientDeSalle client, bool avecJeu)
    {
        uint derniere = uint.MaxValue, dernierMode = 0;
        var arriveeEnvoyee = 0;
        var dernierEnvoi = DateTime.MinValue;
        var debut = DateTime.UtcNow;
        var vu = false;
        var controle = DateTime.MinValue;
        var pings = DateTime.UtcNow;
        var lanceA = DateTime.Now.AddSeconds(-2);   // seul le jeu de cette salle compte
        var dernierClassement = DateTime.MinValue;
        var classementEnCours = false;
        DateTime? fermerA = null;
        var dernierQuit = DateTime.MinValue;
        var mesure = false;
        using var priorite = new CancellationTokenSource();
        // ES dessine derriere le jeu : comme un speedrun, RetroArch passe devant (essai du 04/11 : 35 images/s sans cela).
        if (avecJeu && _priorites is { } priorites) _ = Task.Run(() => priorites.DonnerLaMainAuJeuAsync(priorite.Token), CancellationToken.None);
        try
        {
            while (!_arret)
            {
                if (DateTime.UtcNow - controle > TimeSpan.FromSeconds(1))
                {
                    controle = DateTime.UtcNow;
                    salle.Instantanes = client.InstantanesRecus;
                    salle.EcartHorlogeMs = client.EcartHorlogeMs;
                    if (avecJeu && salle.Mode == 4 && fermerA is null && LimiteDepassee(salle.GoUtc, salle.Limite, DateTime.UtcNow))
                    {
                        _journal.LogInformation("MASS RUN : temps de la salle ecoule, image {Image}.", salle.ImageCourse);
                        salle.TempsEcoule = true;
                        fermerA = DateTime.UtcNow;
                    }
                    if (avecJeu && salle.Mode == 5 && fermerA is null) fermerA = DateTime.UtcNow + TenueDeLArrivee;
                    if (fermerA is { } heure && DateTime.UtcNow >= heure && DateTime.UtcNow - dernierQuit > TimeSpan.FromSeconds(2))
                    {
                        // RetroArch demande parfois deux QUIT (essai du 04/11) : on insiste jusqu'a ce qu'il parte.
                        dernierQuit = DateTime.UtcNow;
                        _ = RetroBat.Api.Netplay.RetroArchInvite.FermerAsync();
                    }
                    if (avecJeu && !mesure && (salle.Mode == 5 || salle.TempsEcoule) && salle.GoUtc is { } go)
                    {
                        mesure = true;
                        var duree = (DateTime.UtcNow - go).TotalSeconds;
                        _journal.LogInformation("MASS RUN : cadence de la course {Cadence:0.0} images/s ({Images} images en {Duree:0.0} s).",
                            duree > 0 ? salle.ImageCourse / duree : 0, salle.ImageCourse, duree);
                    }
                    if (avecJeu)
                    {
                        var tourne = EmulatorForeground.EmulateurDeJeuTourne(lanceA);
                        if (tourne) vu = true;
                        else if (vu || DateTime.UtcNow - debut > TimeSpan.FromMinutes(3)) break;
                    }
                    else if (DateTime.UtcNow - debut > TimeSpan.FromSeconds(60)) break;
                }
                if (avecJeu && salle.Mode >= 4 && DateTime.UtcNow - dernierClassement > TimeSpan.FromSeconds(1) && !classementEnCours)
                {
                    // le classement en direct, relu chaque seconde pendant la course et apres l'arrivee
                    dernierClassement = DateTime.UtcNow;
                    classementEnCours = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (await client.ClassementAsync().ConfigureAwait(false) is { } c)
                            {
                                canal.EcrireClassement(c.Lignes, c.MonRang, c.Arrives, c.Inscrits);
                            }
                        }
                        finally
                        {
                            classementEnCours = false;
                        }
                    });
                }
                if (DateTime.UtcNow - pings > TimeSpan.FromSeconds(30))
                {
                    pings = DateTime.UtcNow;
                    client.EnvoyerPing();   // l'horloge se recale, la place reste ouverte dans le NAT
                }
                if (!avecJeu)
                {
                    // Essai du reseau sans jeu : une borne immobile au depart, a l'image de course de l'horloge.
                    if (DateTime.UtcNow - dernierEnvoi > TimeSpan.FromMilliseconds(64))
                    {
                        dernierEnvoi = DateTime.UtcNow;
                        var depuisGo = (DateTime.UtcNow - client.GoLocalUtc).TotalSeconds;
                        client.EnvoyerPosition(depuisGo > 0 ? (uint)(depuisGo * 60.0988) : 0, depuisGo > 0 ? 4u : 2u, new byte[MassRunProtocole.ImageDePiste]);
                        salle.CoureursVus = client.CoureursFrais().Count;
                    }
                    Thread.Sleep(1);
                    continue;
                }
                var joueur = canal.LireJoueur();
                if (joueur is { } j)
                {
                    salle.Mode = j.Mode;
                    salle.ImageCourse = j.ImageCourse;
                    if (j.Mode > salle.ModeMax) salle.ModeMax = j.Mode;
                    if (j.Mode != dernierMode)
                    {
                        _journal.LogInformation("MASS RUN : mode {Mode}", j.Mode);
                        dernierMode = j.Mode;
                    }
                    var attente = j.Mode == 2;
                    var image = attente ? j.ImageMode : j.ImageCourse;
                    if (image != derniere)
                    {
                        derniere = image;
                        var coureurs = client.CoureursFrais();
                        var liste = new List<MassRunCanal.Coureur>(coureurs.Count);
                        // l'instant de la premiere image de la fenetre, moins le retard d'affichage (voir ClientDeSalle)
                        var instant = (attente ? client.HorlogeEnImages() : image) - ClientDeSalle.RetardDAffichage;
                        foreach (var (_, c) in coureurs.Take(MassRunCanal.MaxCoureurs))
                        {
                            var fenetre = new byte[]?[MassRunCanal.ImagesParFenetre];
                            for (var k = 0; k < fenetre.Length; k++)
                            {
                                var piste = new byte[MassRunProtocole.ImageDePiste];
                                fenetre[k] = client.Composer(c, instant + k, piste) ? piste : null;
                            }
                            liste.Add(new MassRunCanal.Coureur(c.Couleur, c.Pseudo.Length > 10 ? c.Pseudo[..10] : c.Pseudo, false, fenetre));
                        }
                        canal.EcrireCoureurs(image, liste);
                        salle.CoureursVus = liste.Count;
                    }
                    if ((j.Mode == 2 || j.Mode == 4 || j.Mode == 5) && DateTime.UtcNow - dernierEnvoi > TimeSpan.FromMilliseconds(64))
                    {
                        dernierEnvoi = DateTime.UtcNow;
                        client.EnvoyerPosition(j.ImageCourse, j.Mode, j.Piste);
                    }
                    if (j.Arrivee > 0 && arriveeEnvoyee < 3)
                    {
                        arriveeEnvoyee++;
                        client.EnvoyerArrivee(j.Arrivee, j.Elimine);
                        if (arriveeEnvoyee == 1)
                        {
                            salle.Arrivee = j.Arrivee;
                            salle.Elimine = j.Elimine;
                            NoterLArrivee(salle);
                        }
                    }
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            _journal.LogWarning(e, "MASS RUN : la boucle de la salle du reseau s'arrete");
        }
        finally
        {
            salle.Finie = true;
            priorite.Cancel();
            if (avecJeu) _priorites?.RendreLaMain();
            canal.Fermer();
            client.Dispose();
            _journal.LogInformation("MASS RUN : salle {Salle} quittee, {Instantanes} instantanes recus", salle.Salle, client.InstantanesRecus);
            if (avecJeu)
            {
                try { SalleQuittee?.Invoke(salle); }
                catch (Exception e) { _journal.LogWarning(e, "MASS RUN : le bilan de la salle n'a pas pu s'ouvrir"); }
            }
        }
    }

    private sealed record Simule(PisteMassRun Piste, int Ecart, uint Couleur, string Pseudo);

    private static List<PisteMassRun> LirePistes(string niveau)
    {
        var dossier = Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "nelfeplay", "massrun", CrcSmb.ToString("x8"), "pistes", niveau);
        if (!Directory.Exists(dossier))
        {
            return [];
        }
        return Directory.EnumerateFiles(dossier, "*.npiste")
            .Select(f => PisteMassRun.Lire(f, CrcSmb))
            .OfType<PisteMassRun>()
            .Take(32)
            .ToList();
    }

    /// <summary>Les coureurs simules : chaque piste repetee avec un ecart, a la couleur d'avatar de son pseudo.</summary>
    private static List<Simule> FormerCoureurs(List<PisteMassRun> pistes, int voulus)
    {
        var liste = new List<Simule>();
        if (pistes.Count == 0)
        {
            return liste;
        }
        for (var i = 0; i < Math.Min(voulus, MassRunCanal.MaxCoureurs); i++)
        {
            var piste = pistes[i % pistes.Count];
            var pseudo = i < pistes.Count && piste.Pseudo.Length > 0 ? piste.Pseudo : $"BOT {i + 1:00}";
            liste.Add(new Simule(piste, (i / pistes.Count) * 19, Convert.ToUInt32(MultiplayerPanelService.CouleurDe(pseudo + i), 16),
                pseudo.Length > 10 ? pseudo[..10] : pseudo));
        }
        return liste;
    }

    /// <summary>
    /// A chaque image publiee par le coeur : les coureurs de la fenetre qui commence a cette image. Pendant la salle d'attente,
    /// chaque coureur boucle sur sa piste ; en course, il est a son image de course.
    /// </summary>
    private void Boucle(MassRunCanal canal, SalleLocale salle, List<Simule> simules)
    {
        uint dernierMode = 0, derniereImage = uint.MaxValue, derniereImageMode = uint.MaxValue;
        var lanceA = DateTime.Now.AddSeconds(-2);   // seul le jeu de cette salle compte
        var arriveeNotee = false;
        var debut = DateTime.UtcNow;
        var vu = false;
        var controle = DateTime.MinValue;
        try
        {
            while (!_arret)
            {
                // La fin : le jeu a tourne puis RetroArch a disparu, ou il n'a jamais demarre en 3 minutes.
                if (DateTime.UtcNow - controle > TimeSpan.FromSeconds(1))
                {
                    controle = DateTime.UtcNow;
                    var tourne = EmulatorForeground.EmulateurDeJeuTourne(lanceA);
                    if (tourne) vu = true;
                    else if (vu || DateTime.UtcNow - debut > TimeSpan.FromMinutes(3)) break;
                }
                var joueur = canal.LireJoueur();
                if (joueur is { } j)
                {
                    salle.Mode = j.Mode;
                    salle.ImageCourse = j.ImageCourse;
                    if (j.Mode != dernierMode)
                    {
                        _journal.LogInformation("MASS RUN : mode {Mode}", j.Mode);
                        dernierMode = j.Mode;
                        if (j.Mode == 2 && salle.GoUtc is null)
                        {
                            salle.GoUtc = DateTime.UtcNow.AddSeconds(salle.Attente + 5);
                            canal.PoserGo(salle.GoUtc.Value);
                            _journal.LogInformation("MASS RUN : jeu charge, GO! a {Go:HH:mm:ss}", salle.GoUtc.Value.ToLocalTime());
                        }
                    }
                    var attente = j.Mode == 2;
                    if (attente ? j.ImageMode != derniereImageMode : j.ImageCourse != derniereImage)
                    {
                        derniereImage = j.ImageCourse;
                        derniereImageMode = j.ImageMode;
                        var base_ = attente ? j.ImageMode : j.ImageCourse;
                        var coureurs = new List<MassRunCanal.Coureur>(simules.Count);
                        for (var i = 0; i < simules.Count; i++)
                        {
                            var s = simules[i];
                            var fenetre = new byte[]?[MassRunCanal.ImagesParFenetre];
                            for (var k = 0; k < fenetre.Length; k++)
                            {
                                fenetre[k] = attente
                                    ? s.Piste.Images[(int)((base_ + k + (uint)s.Ecart * 7u + (uint)i * 53u) % (uint)s.Piste.Images.Length)]
                                    : s.Piste.A((long)base_ + k - s.Ecart);
                            }
                            coureurs.Add(new MassRunCanal.Coureur(s.Couleur, s.Pseudo, false, fenetre));
                        }
                        canal.EcrireCoureurs(base_, coureurs);
                    }
                    if (j.Arrivee > 0 && !arriveeNotee)
                    {
                        arriveeNotee = true;
                        salle.Arrivee = j.Arrivee;
                        NoterLArrivee(salle);
                    }
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            _journal.LogWarning(e, "MASS RUN : la boucle de la salle s'arrete");
        }
        finally
        {
            salle.Finie = true;
            canal.Fermer();
            _journal.LogInformation("MASS RUN : salle {Niveau} terminee", salle.Niveau);
        }
    }

    private void NoterLArrivee(SalleLocale salle)
    {
        var secondes = salle.Arrivee / 60.0988;
        _journal.LogInformation("MASS RUN : arrivee en {Images} images ({Secondes:0.00} s)", salle.Arrivee, secondes);
        try
        {
            var dossier = Path.Combine(RetroBatPaths.PluginRoot, "state", "massrun");
            Directory.CreateDirectory(dossier);
            var ligne = JsonSerializer.Serialize(new
            {
                at = DateTime.UtcNow,
                niveau = salle.Niveau,
                pseudo = salle.Pseudo,
                images = salle.Arrivee,
                secondes = Math.Round(secondes, 2),
                coureurs = salle.Coureurs,
            });
            File.AppendAllText(Path.Combine(dossier, "arrivees.jsonl"), ligne + Environment.NewLine);
        }
        catch (IOException e)
        {
            _journal.LogWarning(e, "MASS RUN : arrivee non notee");
        }
    }

    private void Arreter()
    {
        _arret = true;
        _boucle?.Join(500);
        _boucle = null;
    }

    public void Dispose()
    {
        Arreter();
        _canal?.Fermer();
        _canal?.Dispose();
    }
}
