using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE DIRECT PARTAGE (CDC video V2.1, §5.2 ; decisions du 15 octobre 2026). Une seule borne a l'antenne a la fois,
/// choisie par le site : la borne qui joue une partie NelfePlay se porte candidate et redemande toutes les 10 secondes.
/// <list type="bullet">
/// <item>A l'antenne, elle envoie la partie que RetroArch enregistre deja, par l'exe du direct, a 3 Mbit/s, avec la
/// bande sous le jeu (joueur, jeu, systeme, mode, record a battre) ; d'office (decision du user : debit reduit).</item>
/// <item>A la fin de la partie, l'exe joue sa fin de passage (clip de transition verifie par son empreinte, carte
/// « UP NEXT » avec la borne suivante que le site a elue) et s'arrete quand le site le dit : la suivante est alors a
/// l'antenne, le flux ne coupe jamais sur les lecteurs du site.</item>
/// <item>Une alert de record sur cette borne termine son passage : elle ne peut pas alimenter deux directs.</item>
/// </list>
/// L'adresse d'entree ne se journalise jamais. Un essai (route locale <c>shared-test</c>, jusqu'au redemarrage) passe
/// en directs d'essai non repertories.
/// </summary>
public sealed class DirectPartageService : IHostedService
{
    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly LeaderboardClient _classement;
    private readonly NelfePlayAgentService _agent;
    private readonly RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider _wrapper;
    private readonly NelfePlayScoringCollectionSyncService? _collection;
    private readonly PartieNelfePlayService? _partie;
    private readonly ReplayCaptureService _capture;
    private readonly ReplayVideoPublisher _site;
    private readonly ILogger<DirectPartageService> _journal;
    private readonly bool _actif;
    private volatile bool _essai;
    private IDisposable? _abonnement;
    private readonly NelfePlayScoringReporter? _rapporteur;

    /// <summary>La partie en cours, et ce que son passage en sait.</summary>
    private sealed class Partie
    {
        public volatile bool Finie;
        public volatile bool Alert;
        // La partie certifiee s'est fermee (un continue, l'arrivee d'un joueur) : elle cede l'antenne a qui la demande.
        public volatile bool NonCertifiable;
        public long MeilleurScore;
        // Le record a battre (rang 1 du classement du mode joue), quand plus haut est mieux : rien sinon, ni quand le mode
        // d'un jeu a modes n'est pas connu.
        public long? Record;
        // LE MODE JOUE (2026-10-18) : le classement dont la bande montre le record, et ce texte. Null : mode inconnu.
        public string? RegleDuRecord;
        public string RecordTexte = "";
        // La bande sous le jeu, telle que le site l'a donnee au passage : relue pour la reecrire quand le mode change.
        public string? BandeJoueur, BandeJeu, BandeSysteme, BandeRegleParDefaut;
        // LA GREFFE (18 octobre 2026) : le classement du mode joue (la carte de fin d'un record tombe sur la chaine), et la
        // bande « RECORD ALERT » pendant que la partie approche le record.
        public IReadOnlyList<LeaderboardClient.Ligne> Lignes = Array.Empty<LeaderboardClient.Ligne>();
        public volatile bool AlerteRecord;
        public bool CarteEcrite;
        public readonly DateTime Debut = DateTime.UtcNow;
        // LE RELAIS (2026-10-18) : la cle de la partie, pour que le site distingue deux parties de la borne (l'ancienne
        // finit sa fin de passage pendant que la nouvelle se presente pour reprendre le meme direct).
        public readonly string Cle = Guid.NewGuid().ToString("N");
    }

    private Partie? _enCours;
    private readonly object _verrou = new();
    // L'exe qui envoie, et la partie a qui il appartient : elle seule ecrit la commande qu'il lit.
    private Partie? _aLAntenne;
    private Task<LanceurDuDirect.Rapport>? _exeCourant;
    private CancellationTokenSource? _secoursCourant;

    public DirectPartageService(IEventBus bus, ApiContext contexte, LeaderboardClient classement, NelfePlayAgentService agent,
        RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider wrapper, ReplayCaptureService capture,
        ReplayVideoPublisher site, IConfiguration configuration, ILogger<DirectPartageService> journal,
        NelfePlayScoringCollectionSyncService? collection = null, PartieNelfePlayService? partie = null,
        NelfePlayScoringReporter? rapporteur = null)
    {
        _bus = bus;
        _contexte = contexte;
        _classement = classement;
        _agent = agent;
        _wrapper = wrapper;
        _capture = capture;
        _site = site;
        _journal = journal;
        _collection = collection;
        _partie = partie;
        _rapporteur = rapporteur;
        _actif = configuration.GetValue("Video:Shared:Enabled", true);
        _essai = configuration.GetValue("Video:Shared:Test", false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_actif)
        {
            _journal.LogInformation("Direct partage : desactive (Video:Shared:Enabled).");
            return Task.CompletedTask;
        }
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        lock (_verrou) if (_enCours is not null) _enCours.Finie = true;
        return Task.CompletedTask;
    }

    /// <summary>L'essai, jusqu'au prochain demarrage : passages d'essai, non repertories, entre bornes en essai.</summary>
    public void Essayer(bool actif)
    {
        _essai = actif;
        _journal.LogInformation("Direct partage : essai {Etat}.", actif ? "arme" : "desarme");
    }

    /// <summary>Les fichiers que l'API ecrit pour l'exe : la commande (fin, suivante, arret) et la bande sous le jeu.</summary>
    private static string Dossier => Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "state", "nelfenet", "replay");
    internal static string CheminDeLaCommande => Path.Combine(Dossier, "partage-commande.json");
    internal static string CheminDuBandeau => Path.Combine(Dossier, "partage-bandeau.json");

    /// <summary>
    /// Un clip livre par le Data Pack (resources/live) : seulement un nom de fichier, jamais un chemin, et seulement un
    /// MP4. Null sinon. Pur.
    /// </summary>
    internal static string? CheminDuClip(string racine, string? fichier)
    {
        if (string.IsNullOrWhiteSpace(fichier)) return null;
        var nom = Path.GetFileName(fichier.Trim());
        if (nom != fichier.Trim() || !nom.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(racine, "resources", "live", nom);
    }

    /// <summary>
    /// Les options de l'exe pour un passage : debit, commande, clip de transition et son empreinte (sans empreinte, pas de
    /// clip), bande sous le jeu. Pur.
    /// </summary>
    internal static IReadOnlyList<string> OptionsDuPassage(int debitKbps, string commande, string? clip, string? empreinte, string bandeau,
        string? applaudissements = null, string? empreinteApplaudissements = null, string? relance = null, string? empreinteRelance = null,
        string? champion = null, string? empreinteChampion = null, string? empreinteAnnonce = null)
    {
        var o = new List<string> { "--debit", Math.Clamp(debitKbps, 500, 9000).ToString(CultureInfo.InvariantCulture), "--commande", commande,
            "--attente-max", "90", "--bandeau", bandeau };
        if (clip is not null && !string.IsNullOrWhiteSpace(empreinte)) o.AddRange(["--clip", clip, "--clip-sha256", empreinte.Trim()]);
        // Les applaudissements d'un record tombe pendant le passage (18 octobre 2026), joues avant la transition.
        if (applaudissements is not null && !string.IsNullOrWhiteSpace(empreinteApplaudissements))
            o.AddRange(["--applaudissements", applaudissements, "--applaudissements-sha256", empreinteApplaudissements.Trim()]);
        // « Try again », a la place de la transition quand le joueur relance une partie ratee (18 octobre 2026).
        if (relance is not null && !string.IsNullOrWhiteSpace(empreinteRelance))
            o.AddRange(["--relance", relance, "--relance-sha256", empreinteRelance.Trim()]);
        // La greffe : le nouveau champion d'un record tombe sur la chaine, et l'empreinte de « New Record » (le chemin de
        // l'annonce part a part, par LanceurDuDirect).
        if (champion is not null && !string.IsNullOrWhiteSpace(empreinteChampion))
            o.AddRange(["--champion", champion, "--champion-sha256", empreinteChampion.Trim()]);
        if (!string.IsNullOrWhiteSpace(empreinteAnnonce)) o.AddRange(["--annonce-sha256", empreinteAnnonce.Trim()]);
        return o;
    }

    /// <summary>Une partie plus courte que ca, sans autre joueur annonce, est une partie ratee que le joueur relance.</summary>
    internal static readonly TimeSpan DureeDUneRelance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// LE JOUEUR RELANCE (demande du user, 18 octobre 2026) : AshTaste92 relancait Bomb Jack toutes les 30 a 60 s apres un
    /// depart rate, et chaque fin de partie rejouait la transition de la chaine, en boucle. Une partie courte, ou suivie
    /// d'une nouvelle partie deja lancee sur la borne, finit sur « try again », sauf si un autre joueur est annonce ou qu'un
    /// direct de record prend le relais. Pur.
    /// </summary>
    internal static bool EstUneRelance(TimeSpan duree, string? joueurAnnonce, string? joueur, bool nouvellePartieLancee, bool alert)
    {
        if (alert) return false;
        var autreJoueur = !string.IsNullOrWhiteSpace(joueurAnnonce)
            && !string.Equals(joueurAnnonce.Trim(), (joueur ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        return !autreJoueur && (nouvellePartieLancee || duree < DureeDUneRelance);
    }

    /// <summary>
    /// Le record est-il tombe pendant le passage ? Seulement une partie restee certifiable, sans direct de record pour le
    /// prendre (l'alert joue ses propres applaudissements), qui a depasse un record connu. Pur.
    /// </summary>
    internal static bool RecordBattu(long meilleurScore, long? record, bool nonCertifiable, bool alert)
        => !nonCertifiable && !alert && record is long r && r > 0 && meilleurScore > r;

    /// <summary>Le record a battre, sur la bande : « RECORD 523 400 · player ». Vide sans record. Pur.</summary>
    internal static string TexteDuRecord(long? valeur, string? joueur)
        => valeur is long v && v > 0
            ? "RECORD " + v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + (string.IsNullOrWhiteSpace(joueur) ? "" : " · " + joueur.Trim())
            : "";

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    var partie = new Partie();
                    lock (_verrou)
                    {
                        if (_enCours is not null) _enCours.Finie = true;
                        _enCours = partie;
                    }
                    _ = Task.Run(() => DeroulerAsync(partie));
                    break;
                case "score.live.changed":
                    if (Nombre(e.Payload, "Score") is not { } score || (Nombre(e.Payload, "Player") ?? 1) != 1) return;
                    lock (_verrou) if (_enCours is { } p) p.MeilleurScore = Math.Max(p.MeilleurScore, score);
                    break;
                case "video.live.record.started":
                    // L'alert de cette borne prend le relais : son passage partage se termine.
                    lock (_verrou) if (_enCours is { } a) a.Alert = true;
                    break;
                case "ui.game.ended":
                    lock (_verrou) if (_enCours is { } f) f.Finie = true;
                    break;
                case "scoring.run.ended":
                    // L'ANTENNE AUX PARTIES QUI COMPTENT (demande user, 2026-10-16) : un joueur qui remet des credits
                    // n'est plus certifiable, et la regie le fait sortir des qu'une autre borne attend.
                    lock (_verrou) if (_enCours is { } c) c.NonCertifiable = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct partage : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>Une partie : candidature, passage a l'antenne s'il vient, fin de passage, arret.</summary>
    private async Task DeroulerAsync(Partie p)
    {
        try
        {
            // La partie NelfePlay se decide au lancement, en quelques secondes.
            for (var i = 0; i < 30 && _partie?.EstNelfePlay != true && !p.Finie; i++) await Task.Delay(500).ConfigureAwait(false);
            if (_partie?.EstNelfePlay != true || p.Finie) return;
            // LA PARTIE D'UN AUTRE (2026-10-16) : une borne spectatrice d'un netplay s'etait portee candidate sous son
            // propre nom, avec la partie de l'hote. Ni spectatrice ni invitee ne se presentent.
            if (!RetroBat.Api.Netplay.NetplayGuestService.PartieALaBorne)
            {
                _journal.LogInformation("Direct partage : partie de l'hote (netplay), la borne ne se porte pas candidate.");
                return;
            }
            // PAS DE CANDIDATURE SANS L'EXE DU DIRECT (2026-10-16) : elue, la borne recevait l'adresse d'envoi,
            // constatait l'absence de l'exe et se taisait ; la diffusion YouTube restait « planifiee », sans image,
            // jusqu'a l'entretien du site. L'exe arrive par outils.json.
            if (LanceurDuDirect.Chemin(AppContext.BaseDirectory) is null)
            {
                _journal.LogInformation("Direct partage : l'exe du direct manque, la borne ne se porte pas candidate.");
                return;
            }
            if (_contexte.Ui.Running is not { } jeu) return;
            var brut = Path.GetFileNameWithoutExtension(jeu.GamePath.Length > 0 ? jeu.GamePath : jeu.GameName);
            if (brut.Length == 0 || jeu.SystemId.Length == 0) return;
            var def = _wrapper.ResolveDefinitionFor(brut, jeu.SystemId);
            if (!(def.AliasMatched || def.DefinitionExists) || string.IsNullOrWhiteSpace(def.Rom)) return;
            var romGroup = def.Rom.Trim().ToLowerInvariant();
            var regles = _collection?.ReglesOuvertes(romGroup) ?? Array.Empty<string>();
            var regle = LeaderboardClient.ReglePrincipale(regles);
            if (regle.Length == 0) regle = "1cc";
            // UN JEU A MODES (2026-10-18) : le record de la bande et des applaudissements est celui du mode joue, que le
            // rapporteur lit pendant la partie. Le 18 octobre, la bande d'AshTaste92 montrait le record du 1CC normal de
            // Bubble Bobble (1 013 700) quel que soit son mode (ORIGINAL : 1 333 090). Mode inconnu : ni record ni bravo.
            var aModes = !DirectDuRecordService.SansModes(regles);
            await MettreAJourLeRecordAsync(p, romGroup, aModes ? _rapporteur?.RegleDuModeEnCours(romGroup) : regle).ConfigureAwait(false);
            // Le systeme du classement : le dossier de la definition (arcade, megadrive, mastersystem...), jamais celui d'ES
            // (fbneo, mame). Le site le devinait depuis le 17 octobre 2026 ; le titre du direct disait « (Arcade) » pour un
            // jeu Master System quand il manquait.
            var systeme = (def.SystemId ?? "").Trim().ToLowerInvariant();
            await PasserAsync(p, romGroup, regle, systeme, aModes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _journal.LogInformation("Direct partage : passage interrompu ({Type} : {Message}).", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// Le record du classement <paramref name="regle"/>, pour la bande et les applaudissements. Rien sans classement ni
    /// quand le mode joue est inconnu (null) : la bande dit alors nelfeplay.com.
    /// </summary>
    private async Task MettreAJourLeRecordAsync(Partie p, string romGroup, string? regle)
    {
        p.RegleDuRecord = regle;
        p.Record = null;
        p.RecordTexte = "";
        p.Lignes = Array.Empty<LeaderboardClient.Ligne>();
        if (regle is null) return;
        try
        {
            var classement = await _classement.MondeAsync(romGroup, _agent.Status.Pseudo ?? "", CancellationToken.None, regle)
                .ConfigureAwait(false);
            p.Lignes = classement.Lignes;
            if (classement.Lignes.OrderBy(l => l.Rang).FirstOrDefault() is { } tete)
            {
                p.RecordTexte = TexteDuRecord(tete.Valeur, tete.Joueur);
                // Un contre-la-montre ne se compare pas au score : pas d'applaudissements a sa fin.
                if (!tete.PlusBasEstMieux) p.Record = tete.Valeur;
            }
        }
        catch (Exception)
        {
            // Sans classement, la bande dit nelfeplay.com.
        }
    }

    /// <summary>Le mode sur la bande : « 1CC POWER-UP » pour le classement 1cc-power-up. Pur.</summary>
    internal static string LibelleDuMode(string regle)
    {
        // Seul le premier tiret separe la categorie du mode : « power-up » garde le sien.
        var libelle = regle.Trim().ToUpperInvariant();
        var i = libelle.IndexOf('-');
        return i > 0 ? libelle[..i] + " " + libelle[(i + 1)..] : libelle;
    }

    private async Task PasserAsync(Partie p, string romGroup, string regleParDefaut, string systeme, bool aModes)
    {
        Task<LanceurDuDirect.Rapport>? exe = null;
        CancellationTokenSource? secours = null;
        var finAnnoncee = false;
        var essai = _essai;
        try
        {
            while (true)
            {
                // Le code d'un mode se tape a l'ecran titre : le mode joue se relit a chaque tour, la bande suit.
                if (aModes && !p.Finie && _rapporteur?.RegleDuModeEnCours(romGroup) is var duMode && duMode != p.RegleDuRecord)
                {
                    await MettreAJourLeRecordAsync(p, romGroup, duMode).ConfigureAwait(false);
                    _journal.LogInformation("Direct partage : {Jeu}, mode joue {Regle}, record a battre {Record}.", romGroup,
                        duMode ?? "inconnu", p.Record?.ToString(CultureInfo.InvariantCulture) ?? "aucun");
                    EcrireLaBande(p);
                }
                var regle = p.RegleDuRecord ?? regleParDefaut;
                // Un role de netplay repris tard (jeu demarre hors ES) retire aussi la candidature.
                var finie = p.Finie || p.Alert || !RetroBat.Api.Netplay.NetplayGuestService.PartieALaBorne;
                var demande = new JsonObject
                {
                    ["rom_group"] = romGroup, ["ruleset"] = regle, ["system_id"] = systeme, ["score"] = Interlocked.Read(ref p.MeilleurScore),
                    ["state"] = finie ? "ended" : "playing", ["elapsed_s"] = (int)(DateTime.UtcNow - p.Debut).TotalSeconds, ["test"] = essai,
                    // Sans envoi en cours, le site redonne l'adresse d'entree d'un passage ouvert : sa premiere reponse a pu
                    // se perdre.
                    ["sending"] = exe is not null,
                    ["certifiable"] = !p.NonCertifiable,
                    ["game_key"] = p.Cle,
                    // Le film de la partie tourne : elle peut reprendre tout de suite le direct de la precedente.
                    ["film"] = _capture.FilmEnCours() is not null,
                };
                var r = await _site.SonderLePartageAsync(demande, CancellationToken.None).ConfigureAwait(false);
                var role = (string?)r?["role"];
                var attente = TimeSpan.FromSeconds(Math.Clamp((int?)r?["poll_after"] ?? 10, 3, 60));
                // LA GREFFE : la partie approche le record, la bande sous le jeu le dit (l'exe la relit toutes les 2 s).
                if (role == "on_air" && ((bool?)r!["record_alert"] == true) != p.AlerteRecord)
                {
                    p.AlerteRecord = (bool?)r["record_alert"] == true;
                    EcrireLaBande(p);
                }

                if (role == "on_air" && exe is null && !finie && (string?)r!["ingest"] is { Length: > 0 } adresse)
                {
                    exe = Lancer(p, r, adresse, regleParDefaut, out secours);
                    if (exe is null)
                    {
                        // ELUE SANS POUVOIR ENVOYER (2026-10-16) : la borne se taisait, et la sortante tenait son clip de fin
                        // en attendant une suivante qui ne venait pas. Le site l'apprend tout de suite (partie finie) et
                        // abandonne le passage.
                        p.Finie = true;
                        continue;
                    }
                    _journal.LogInformation("Direct partage : {Jeu} a l'antenne (passage {Passage}).", romGroup, (string?)r["passage_id"]);
                }
                else if (role == "outro")
                {
                    if (!finAnnoncee) _journal.LogInformation("Direct partage : fin du passage de {Jeu}.", romGroup);
                    finAnnoncee = true;
                    var suivante = r!["next"] as JsonObject;
                    bool nouvellePartie;
                    lock (_verrou) nouvellePartie = _enCours is not null && _enCours != p;
                    var relance = EstUneRelance(DateTime.UtcNow - p.Debut, (string?)suivante?["player"], p.BandeJoueur, nouvellePartie, p.Alert);
                    var recordBattu = RecordBattu(Interlocked.Read(ref p.MeilleurScore), p.Record, p.NonCertifiable, p.Alert);
                    // Le classement mis a jour, que l'exe montre apres le nouveau champion : ecrit avant la commande qui
                    // ouvre la fin de passage.
                    if (recordBattu && !p.CarteEcrite)
                    {
                        p.CarteEcrite = true;
                        EcrireLaCarte(p, romGroup);
                    }
                    EcrireLaCommande(p, fin: true, arret: false, (string?)suivante?["player"], (string?)suivante?["game"],
                        recordBattu, relance, (bool?)suivante?["record"] == true);
                }
                else if (role == "stop" || (role == "none" && exe is not null))
                {
                    EcrireLaCommande(p, fin: true, arret: true, null, null);
                    _journal.LogInformation("Direct partage : {Jeu}, arret demande par le site.", romGroup);
                    return;
                }
                else if (role is "none" or "wait" && finie)
                {
                    return;
                }
                else if (r is null && finie && exe is null)
                {
                    return;
                }
                // L'exe est sorti seul (film perdu, connexion coupee) : le passage se termine aupres du site.
                if (exe is { IsCompleted: true } && !finie) p.Finie = true;
                await Task.Delay(attente).ConfigureAwait(false);
            }
        }
        finally
        {
            if (exe is not null)
            {
                // L'exe s'arrete sur l'ordre ecrit ; au-dela de deux minutes, on l'arrete.
                if (await Task.WhenAny(exe, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false) != exe) secours?.Cancel();
                try
                {
                    var rapport = await exe.ConfigureAwait(false);
                    _journal.LogInformation("Direct partage : passage fini ({Fin}, code {Code}, {Secondes:F0} s).", rapport.Fin, rapport.Code,
                        rapport.Duree.TotalSeconds);
                }
                catch (Exception ex)
                {
                    _journal.LogInformation("Direct partage : l'exe du passage a echoue ({Message}).", ex.Message);
                }
            }
            secours?.Dispose();
        }
    }

    /// <summary>
    /// Lance l'exe du passage sur le film en cours ; null s'il n'y a ni film ni exe. L'exe d'une partie precedente qui
    /// envoie encore (sa fin de passage, quand la nouvelle partie reprend le meme direct) s'arrete d'abord : une seule
    /// connexion par adresse d'entree, une seule commande.
    /// </summary>
    private Task<LanceurDuDirect.Rapport>? Lancer(Partie p, JsonObject r, string adresse, string regleParDefaut, out CancellationTokenSource? secours)
    {
        secours = null;
        var exe = LanceurDuDirect.Chemin(AppContext.BaseDirectory);
        if (_capture.FilmEnCours() is not { } film || exe is null)
        {
            _journal.LogInformation("Direct partage : a l'antenne, mais {Manque} : pas de passage.", exe is null ? "l'exe du direct manque" : "aucun film ne tourne");
            EcrireLaCommande(p, fin: true, arret: true, null, null);
            return null;
        }
        Task<LanceurDuDirect.Rapport>? precedent;
        CancellationTokenSource? secoursPrecedent;
        lock (_verrou)
        {
            precedent = _exeCourant is { IsCompleted: false } enCours && _aLAntenne != p ? enCours : null;
            secoursPrecedent = _secoursCourant;
            _aLAntenne = p;
        }
        var relais = (bool?)r["relay"] == true;
        var banniere = r["banner"] as JsonObject;
        p.BandeJoueur = (string?)banniere?["player"] ?? _agent.Status.Pseudo;
        p.BandeJeu = (string?)banniere?["game"];
        p.BandeSysteme = (string?)banniere?["system"];
        p.BandeRegleParDefaut = regleParDefaut;
        var transition = (r["clips"] as JsonObject)?["transition"] as JsonObject;
        var clip = CheminDuClip(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, (string?)transition?["file"]);
        if (clip is not null && !File.Exists(clip)) clip = null;
        var bravo = (r["clips"] as JsonObject)?["applause"] as JsonObject;
        var applaudissements = CheminDuClip(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, (string?)bravo?["file"]);
        if (applaudissements is not null && !File.Exists(applaudissements)) applaudissements = null;
        // LA GREFFE (18 octobre 2026) : une partie qui prend la chaine pour un record s'ouvre par « New Record » ; un record
        // tombe finit sur « New Champion » et le classement mis a jour.
        var clipRecord = (r["clips"] as JsonObject)?["record"] as JsonObject;
        var annonce = (bool?)r["record"] == true ? CheminDuClip(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, (string?)clipRecord?["file"]) : null;
        if (annonce is not null && (!File.Exists(annonce) || string.IsNullOrWhiteSpace((string?)clipRecord?["sha256"]))) annonce = null;
        var clipChampion = (r["clips"] as JsonObject)?["champion"] as JsonObject;
        var champion = CheminDuClip(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, (string?)clipChampion?["file"]);
        if (champion is not null && !File.Exists(champion)) champion = null;
        var essai = (r["clips"] as JsonObject)?["relaunch"] as JsonObject;
        var relanceClip = CheminDuClip(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, (string?)essai?["file"]);
        if (relanceClip is not null && !File.Exists(relanceClip)) relanceClip = null;
        var options = OptionsDuPassage((int?)r["bitrate_kbps"] ?? 3000, CheminDeLaCommande, clip, (string?)transition?["sha256"], CheminDuBandeau,
            applaudissements, (string?)bravo?["sha256"], relanceClip, (string?)essai?["sha256"], champion, (string?)clipChampion?["sha256"],
            annonce is null ? null : (string?)clipRecord?["sha256"]);
        secours = new CancellationTokenSource();
        var jeton = secours.Token;
        var tache = Task.Run(async () =>
        {
            if (precedent is not null)
            {
                // L'ordre d'arret, que l'exe precedent lit chaque seconde ; au-dela de 15 s, son lanceur l'arrete.
                Ecrire(CheminDeLaCommande, CommandeJson(fin: true, arret: true, null, null, false, false));
                if (await Task.WhenAny(precedent, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != precedent)
                {
                    try { secoursPrecedent?.Cancel(); } catch (ObjectDisposedException) { /* deja fini */ }
                    await Task.WhenAny(precedent, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(false);
                }
                _journal.LogInformation("Direct partage : l'exe de la partie precedente s'est arrete, la nouvelle partie {Mode}.",
                    relais ? "reprend le meme direct" : "prend l'antenne");
            }
            EcrireLeBandeau(p.BandeJoueur, p.BandeJeu, p.BandeSysteme, ModeDeLaBande(p, regleParDefaut), p.RecordTexte);
            // La carte d'un passage precedent ne se montre jamais : elle s'ecrit au moment ou le record tombe.
            try { File.Delete(CheminDeLaCarte); } catch (Exception) { /* absente */ }
            Ecrire(CheminDeLaCommande, CommandeJson(fin: false, arret: false, null, null, false, false));
            return await LanceurDuDirect.DiffuserAsync(exe, film.Fichier, adresse, film.Rotation ?? 0, _journal, jeton,
                annonce: annonce, classement: CheminDeLaCarte, options: options, delaiDeFin: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        });
        lock (_verrou)
        {
            _exeCourant = tache;
            _secoursCourant = secours;
        }
        return tache;
    }

    /// <summary>La commande de l'exe ; seule la partie a qui l'exe appartient l'ecrit (une ancienne partie se tait).</summary>
    private void EcrireLaCommande(Partie p, bool fin, bool arret, string? joueur, string? jeu, bool recordBattu = false, bool relance = false,
        bool suivanteRecord = false)
    {
        lock (_verrou) if (_aLAntenne != p) return;
        Ecrire(CheminDeLaCommande, CommandeJson(fin, arret, joueur, jeu, recordBattu, relance, suivanteRecord));
    }

    internal static string CommandeJson(bool fin, bool arret, string? joueur, string? jeu, bool recordBattu, bool relance,
        bool suivanteRecord = false)
    {
        var commande = new JsonObject
        {
            ["fin"] = fin, ["arret"] = arret,
            // « record » : la partie suivante approche le record, la carte de fin dit RECORD ALERT (greffe).
            ["suivant"] = joueur is null && jeu is null ? null : new JsonObject { ["joueur"] = joueur, ["jeu"] = jeu, ["record"] = suivanteRecord },
            ["recordBattu"] = recordBattu,
            ["relance"] = relance,
        };
        return commande.ToJsonString();
    }

    /// <summary>La bande d'une partie a l'antenne, reecrite (l'exe la relit toutes les deux secondes) ; une autre se tait.</summary>
    private void EcrireLaBande(Partie p)
    {
        lock (_verrou) if (_aLAntenne != p || p.BandeRegleParDefaut is null) return;
        EcrireLeBandeau(p.BandeJoueur, p.BandeJeu, p.BandeSysteme, ModeDeLaBande(p, p.BandeRegleParDefaut), p.RecordTexte);
    }

    /// <summary>Le mode sur la bande : « RECORD ALERT » pendant que la partie approche le record, sinon son classement.</summary>
    private static string ModeDeLaBande(Partie p, string regleParDefaut)
        => p.AlerteRecord ? "RECORD ALERT" : LibelleDuMode(p.RegleDuRecord ?? regleParDefaut);

    /// <summary>La carte de fin d'un passage : le classement mis a jour quand le record est tombe sur la chaine.</summary>
    internal static string CheminDeLaCarte => Path.Combine(Dossier, "partage-classement.json");

    /// <summary>Ecrit la carte du classement mis a jour, au format que lit l'exe (CarteDuClassement).</summary>
    private void EcrireLaCarte(Partie p, string romGroup)
    {
        try
        {
            var joueur = string.IsNullOrWhiteSpace(p.BandeJoueur) ? _agent.Status.Pseudo ?? "player" : p.BandeJoueur!;
            var carte = new JsonObject
            {
                ["jeu"] = string.IsNullOrWhiteSpace(p.BandeJeu) ? romGroup : p.BandeJeu,
                ["regle"] = p.RegleDuRecord ?? p.BandeRegleParDefaut ?? "1cc",
                ["lignes"] = new JsonArray(DirectDuRecordService.ClassementMisAJour(p.Lignes, joueur, Interlocked.Read(ref p.MeilleurScore))
                    .Select(l => (JsonNode)new JsonObject { ["rang"] = l.Rang, ["joueur"] = l.Joueur, ["score"] = l.Score, ["nouveau"] = l.Nouveau })
                    .ToArray()),
            };
            Ecrire(CheminDeLaCarte, carte.ToJsonString());
            _journal.LogInformation("Direct partage : {Jeu}, record tombe, carte du classement ecrite.", romGroup);
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct partage : carte du classement non ecrite.");
        }
    }

    private void EcrireLeBandeau(string? joueur, string? jeu, string? systeme, string mode, string record)
        => Ecrire(CheminDuBandeau, new JsonObject
        {
            ["joueur"] = joueur, ["jeu"] = jeu, ["systeme"] = systeme, ["mode"] = mode, ["record"] = record.Length > 0 ? record : null,
        }.ToJsonString());

    /// <summary>Ecriture atomique : l'exe ne lit jamais un fichier a moitie ecrit.</summary>
    private void Ecrire(string chemin, string texte)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin + ".tmp", texte);
            File.Move(chemin + ".tmp", chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct partage : {Fichier} non ecrit.", Path.GetFileName(chemin));
        }
    }

    private static long? Nombre(object? payload, string nom)
    {
        try
        {
            var el = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
            return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(nom, out var v) && v.TryGetInt64(out var n) ? n : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
