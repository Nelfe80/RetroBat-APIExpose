using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Ce que MULTIPLAYER montre dans ES (CDC MULTIPLAYER v2.5, lots M3 et M5), et ce que la manette y fait :
///
///   - le PANNEAU, ouvert par l'appui long sur un jeu de la collection (V24-01, V24-03) : le joueur est
///     place dans une gameroom tout de suite, deja pret, et voit qui l'attend ;
///   - la PRESENTATION, « Les joueurs se synchronisent… », du decompte jusqu'a l'arrivee du jeu ;
///   - le RESULTAT, apres la partie : les joueurs, la duree, « Rejouer » (A maintenu), retour a ES en 12 s ;
///   - en haut d'ES, le rappel de « Votre selection » (panneau ferme, le joueur attend toujours) et le
///     toast d'une invitation (A maintenu pour rejoindre, B pour refuser).
///
/// La fenetre, le focus et l'habillage sont ceux du panneau de classement
/// (<see cref="LeaderboardOverlayService"/>) ; l'appui long et la lecture de la manette restent a
/// <see cref="LeaderboardInputService"/>, qui transmet ici les gestes tant que MULTIPLAYER est a l'ecran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerPanelService
{
    /// <summary>Les couleurs d'avatar, tirees de la poignee : la meme pour un joueur sur toutes les bornes.</summary>
    private static readonly string[] Couleurs = ["A98BFF", "78E5A3", "FF9F6B", "49E3CB", "FFD37A", "FF8C9A"];

    /// <summary>V24-17 : « Vous serez de retour dans 12 s ».</summary>
    private static readonly TimeSpan DureeDuResultat = TimeSpan.FromSeconds(12);

    private enum Mode
    {
        Ferme,
        Panneau,
        Presentation,
        Resultat,
    }

    private readonly MultiplayerSessionService _session;
    private readonly LeaderboardOverlayService _overlay;
    private readonly InterfaceTextService _textes;
    private readonly EmulationStationSettingsService _reglages;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MultiplayerPanelService> _journal;
    private readonly MultiplayerToastOverlay? _cartes;
    private string? _message;
    private DateTime _messageJusqua;
    private readonly ApiContext? _contexte;
    private readonly MediaRuntimeState? _etatDesMedias;
    private readonly MultiplayerPanelModel _modele = new();
    private readonly object _verrou = new();
    private readonly Dictionary<string, MultiplayerRoom> _connues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invitationsVues = new(StringComparer.Ordinal);
    private Mode _mode = Mode.Ferme;
    private MultiplayerInstalledGame? _jeu;
    private readonly RetroBat.Api.Avatar.AvatarSheetStore? _planches;
    private readonly RetroBat.Api.Replay.Sharing.ReplayRelayService? _relaisDesPlanches;
    private readonly RetroAchievementsService? _retroAchievements;

    /// <summary>Les planches et les portraits deja demandes : une demande par fichier manquant, pas une par dessin.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _demandes = new(StringComparer.Ordinal);
    private IReadOnlyList<MultiplayerAmi> _amis = [];
    private readonly SemaphoreSlim _telechargementsDePortraits = new(2, 2);
    private string? _refus;
    private DateTime? _validerDepuis;
    private DateTime? _annulerDepuis;
    private string _glypheValider = "button_a";
    private string _glypheAnnuler = "button_b";
    private string _langue = "";
    private string _roomDuResultat = "";
    private int? _dureeDuResultat;
    private DateTime _finDuResultat;
    private CancellationTokenSource? _retour;
    private string _texteDuBandeau = "";
    private MultiplayerInvitation? _invitation;
    private DateTime _presenteeDepuis;

    /// <summary>La presentation ne dure pas plus : au-dela, la partie ne s'est pas ouverte, le joueur retrouve ES.</summary>
    private static readonly TimeSpan PresentationMax = TimeSpan.FromMinutes(2);

    public MultiplayerPanelService(
        MultiplayerSessionService session,
        LeaderboardOverlayService overlay,
        InterfaceTextService textes,
        EmulationStationSettingsService reglages,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<MultiplayerPanelService> journal,
        MultiplayerToastOverlay? cartes = null,
        ApiContext? contexte = null,
        MediaRuntimeState? etatDesMedias = null,
        RetroBat.Api.Avatar.AvatarSheetStore? planches = null,
        RetroBat.Api.Replay.Sharing.ReplayRelayService? relais = null,
        RetroAchievementsService? retroAchievements = null)
    {
        _planches = planches;
        _relaisDesPlanches = relais;
        _retroAchievements = retroAchievements;
        _session = session;
        _overlay = overlay;
        _textes = textes;
        _reglages = reglages;
        _options = options;
        _journal = journal;
        _cartes = cartes;
        _contexte = contexte;
        _etatDesMedias = etatDesMedias;
        _session.Change += SurChangement;
    }

    /// <summary>MULTIPLAYER est-il a l'ecran ? Tant qu'il l'est, les gestes viennent ici.</summary>
    public bool Ouvert
    {
        get { lock (_verrou) return _mode != Mode.Ferme; }
    }

    /// <summary>Le panneau des gamerooms est-il a l'ecran ? Ni la presentation d'un lancement, ni un resultat.</summary>
    public bool PanneauAffiche
    {
        get { lock (_verrou) return _mode == Mode.Panneau; }
    }

    /// <summary>MULTIPLAYER est-il en service sur cette borne ?</summary>
    public bool Actif => _options.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true };

    /// <summary>
    /// Le carrousel montre-t-il la collection MULTIPLAYER ? C'est le carrousel qui compte, pas le systeme du
    /// jeu : a la selection d'un jeu, l'API prend le systeme du jeu (« fbneo »), meme dans une collection
    /// (vu le 22/10 : l'appui long ouvrait le classement).
    /// </summary>
    public bool DansLaCollection
        => string.Equals(_etatDesMedias?.CarouselSystemId, MultiplayerCollectionSyncService.CollectionName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Une invitation attend sa reponse : l'appui long la prend avant tout panneau.</summary>
    public bool InvitationEnAttente
    {
        get { lock (_verrou) return _invitation is not null; }
    }

    // ── le panneau ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// L'APPUI LONG sur un jeu de la collection : le joueur rejoint une gameroom et le panneau s'ouvre.
    /// Rend faux quand le jeu n'est pas MULTIPLAYER sur cette borne.
    /// </summary>
    public async Task<bool> OuvrirAsync(string cheminDuJeu, string glypheValider, string glypheAnnuler, CancellationToken ct, bool rejoindre = true)
    {
        var jeu = _session.JeuDe(cheminDuJeu);
        if (jeu is null)
        {
            _journal.LogInformation("MULTIPLAYER : {Chemin} n'est pas un jeu de la collection", cheminDuJeu);
            return false;
        }

        lock (_verrou)
        {
            _jeu = jeu;
            _refus = null;
            _amis = [];
            _glypheValider = glypheValider.Length > 0 ? glypheValider : _glypheValider;
            _glypheAnnuler = glypheAnnuler.Length > 0 ? glypheAnnuler : _glypheAnnuler;
            _langue = "";
            _mode = Mode.Panneau;
            _retour?.Cancel();
            _modele.Ouvrir();
        }

        _session.PanneauOuvert = true;
        MettreAJourLeBandeau();
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
        _journal.LogInformation("MULTIPLAYER : panneau ouvert sur {Jeu}", jeu.Game.Id);

        if (rejoindre)
        {
            var refus = await _session.RejoindreAsync(cheminDuJeu, ct).ConfigureAwait(false);
            lock (_verrou)
            {
                _refus = refus;
            }
        }

        Rafraichir();
        _ = ChargerLesAmisAsync(ct);
        return true;
    }

    /// <summary>Fermer sans quitter : le jeu reste dans « Votre selection » (V24-09).</summary>
    public void Fermer()
    {
        lock (_verrou)
        {
            _modele.Fermer();
            _mode = Mode.Ferme;
            _validerDepuis = null;
            _annulerDepuis = null;
            _retour?.Cancel();
        }

        _session.PanneauOuvert = false;
        _overlay.Fermer();
        MettreAJourLeBandeau();
    }

    /// <summary>
    /// Une autre fenetre est passee devant ES (un rendu de replay, un editeur sur un autre ecran), ou le
    /// joueur ne touche plus a rien : le panneau reste a l'ecran, on y attend un joueur. Seul le foyer revient a ES.
    /// </summary>
    public void RendreLaMain()
    {
        bool change;
        lock (_verrou)
        {
            change = _mode == Mode.Panneau && _modele.RendreLaMain();
        }

        if (change)
        {
            _journal.LogInformation("MULTIPLAYER : le panneau reste ouvert et rend la main a ES.");
            Rafraichir();
        }
    }

    /// <summary>Un jeu demarre : la fenetre s'efface sans reprendre le premier plan a l'emulateur.</summary>
    public void FermerPourLeJeu()
    {
        lock (_verrou)
        {
            _modele.Fermer();
            _mode = Mode.Ferme;
        }

        _session.PanneauOuvert = false;
        _overlay.FermerPourLeJeu();
        _cartes?.Cacher();
        _texteDuBandeau = "";
    }

    /// <summary>L'emulateur est la : la presentation s'efface.</summary>
    public void EffacerPourLeJeu() => FermerPourLeJeu();

    // ── la presentation et le resultat ────────────────────────────────────────────────────────────

    /// <summary>
    /// Le decompte est fini : « Les joueurs se synchronisent… » jusqu'a l'arrivee du jeu (ecrans 9 et 14 de
    /// la toile). Au niveau Classique, le message de la borne d'origine s'ajoute.
    /// </summary>
    public void AnnoncerLeLancement(MultiplayerRoom room, MultiplayerInstalledGame? jeu)
    {
        var langue = Langue();
        bool deja;
        lock (_verrou)
        {
            deja = _mode == Mode.Presentation;
            _mode = Mode.Presentation;
            _modele.Fermer();
            _jeu = jeu ?? _jeu;
            _retour?.Cancel();
            if (!deja)
            {
                _presenteeDepuis = DateTime.UtcNow;
            }
        }

        _session.PanneauOuvert = false;
        _cartes?.Cacher();
        _texteDuBandeau = "";
        if (deja)
        {
            return;
        }

        var classique = room.Level != MultiplayerLevels.NelfePlay;
        // NIVEAU NELFEPLAY : les personnages dans l'ordre des places (00quater.10), a la place du message du Classique.
        var personnages = string.Join("  ·  ", room.Players
            .Where(joueur => joueur.Seat is not null)
            .OrderBy(joueur => joueur.Seat)
            .Select(joueur => (Place: joueur.Seat!.Value, Nom: PersonnageDe(joueur)))
            .Where(paire => paire.Nom.Length > 0)
            .Select(paire => "P" + paire.Place.ToString(CultureInfo.InvariantCulture) + " " + paire.Nom));
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
        _overlay.Attendre(
            _textes.Text("multiplayer.syncing", langue),
            jeu?.Game.Name ?? string.Empty,
            classique ? _textes.Text("multiplayer.classic.message", langue) : personnages);
    }

    /// <summary>
    /// Apres la partie, l'ecran de resultat (V24-17, 00quater.10) : au niveau Classique, les joueurs et la
    /// duree. Il se ferme de lui-meme au bout de 12 s ; A maintenu relance le meme jeu.
    /// </summary>
    public void MontrerLeResultat(string room, int? dureeSecondes)
    {
        _ = Task.Run(async () =>
        {
            // L'hote annonce la fin : un ou deux sondages plus tard, la gameroom est « finished ».
            for (var essai = 0; essai < 6 && !_session.Etat.Rooms.Any(r => r.Room == room && r.State == "finished"); essai++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            var retour = new CancellationTokenSource();
            lock (_verrou)
            {
                _retour?.Cancel();
                _retour = retour;
                _mode = Mode.Resultat;
                _roomDuResultat = room;
                _dureeDuResultat = dureeSecondes;
                _finDuResultat = DateTime.UtcNow + DureeDuResultat;
                _modele.Fermer();
            }

            _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
            try
            {
                await Task.Delay(DureeDuResultat, retour.Token).ConfigureAwait(false);
                bool encore;
                lock (_verrou)
                {
                    encore = _mode == Mode.Resultat && ReferenceEquals(_retour, retour);
                }

                if (encore)
                {
                    Fermer();
                }
            }
            catch (OperationCanceledException)
            {
                // Rejouer, ou fermer a la main : rien a faire de plus.
            }
        });
    }

    // ── les gestes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Une direction du DPAD : <c>left</c>, <c>right</c>, <c>up</c>, <c>down</c>.</summary>
    public void Direction(string? identite)
    {
        MultiplayerPanelModel.Effet effet;
        var sens = (identite ?? string.Empty).ToLowerInvariant();
        lock (_verrou)
        {
            if (_mode != Mode.Panneau)
            {
                return;
            }

            // NIVEAU NELFEPLAY : ▲▼ change le personnage dans WAITING FOR PLAYERS (V24-04), quand nous avons la main.
            if (sens is "up" or "down"
                && _modele.Etat == MultiplayerPanelModel.Foyer.Panneau
                && _modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForPlayers
                && _jeu?.Game is { EstNelfePlay: true, Choices.Count: > 0 } jeuNelfePlay
                && RoomDuJeu() is { State: "waiting" or "countdown" } salle)
            {
                _ = ChangerDePersonnageAsync(salle.Room, jeuNelfePlay, sens == "down" ? 1 : -1);
                return;
            }

            effet = sens switch
            {
                "left" => _modele.Gauche(),
                "right" => _modele.Droite(),
                "up" => _modele.Haut(),
                "down" => _modele.Bas(),
                _ => MultiplayerPanelModel.Effet.Rien,
            };
        }

        Appliquer(effet);
    }

    /// <summary>Le bouton « valider » : relache, il agit sur la ligne ; au resultat, maintenu, il relance le jeu.</summary>
    public void Valider(bool appuye)
    {
        MultiplayerPanelModel.Effet effet;
        bool rejouer;
        lock (_verrou)
        {
            if (appuye)
            {
                _validerDepuis = DateTime.UtcNow;
                return;
            }

            var maintenu = Maintenu(_validerDepuis);
            _validerDepuis = null;
            rejouer = _mode == Mode.Resultat && maintenu;
            effet = _mode == Mode.Panneau ? _modele.Valider() : MultiplayerPanelModel.Effet.Rien;
        }

        if (rejouer)
        {
            _ = RejouerAsync();
            return;
        }

        Appliquer(effet);
    }

    /// <summary>Le bouton « annuler » : relache court, il rend la main ou ferme ; maintenu, il quitte la gameroom.</summary>
    public void Annuler(bool appuye)
    {
        MultiplayerPanelModel.Effet effet;
        lock (_verrou)
        {
            if (appuye)
            {
                _annulerDepuis = DateTime.UtcNow;
                return;
            }

            var maintenu = Maintenu(_annulerDepuis);
            _annulerDepuis = null;
            if (_mode == Mode.Resultat)
            {
                effet = MultiplayerPanelModel.Effet.Fermer;
            }
            else if (_mode == Mode.Panneau)
            {
                effet = maintenu ? _modele.AnnulerMaintenu() : _modele.Annuler();
            }
            else
            {
                return;   // la presentation ne s'interrompt pas
            }
        }

        Appliquer(effet);
    }

    /// <summary>L'appui long pendant une invitation : le joueur rejoint la gameroom de son ami.</summary>
    public async Task AccepterInvitationAsync(CancellationToken ct = default)
    {
        MultiplayerInvitation? invitation;
        lock (_verrou)
        {
            invitation = _invitation;
            _invitation = null;
        }

        if (invitation is null)
        {
            return;
        }

        _cartes?.Cacher();
        _texteDuBandeau = "";
        var jeu = _session.JeuDeLaRoom(new MultiplayerRoom { Game = invitation.Game, Content = invitation.Content });
        if (jeu is null)
        {
            // Sans le meme contenu, la partie se desynchroniserait : on refuse plutot que de rejoindre.
            await _session.RepondreAsync(invitation.Invitation, false, ct).ConfigureAwait(false);
            Signaler(_textes.Text("multiplayer.refused.content_refused", Langue()));
            return;
        }

        var refus = await _session.RepondreAsync(invitation.Invitation, true, ct).ConfigureAwait(false);
        if (refus is not null)
        {
            Signaler(_textes.Text("multiplayer.refused." + refus, Langue()));
            return;
        }

        await OuvrirAsync(jeu.Path, string.Empty, string.Empty, ct, rejoindre: false).ConfigureAwait(false);
    }

    /// <summary>B pendant une invitation : elle est refusee.</summary>
    public void RefuserInvitation()
    {
        MultiplayerInvitation? invitation;
        lock (_verrou)
        {
            invitation = _invitation;
            _invitation = null;
        }

        if (invitation is null)
        {
            return;
        }

        _cartes?.Cacher();
        _texteDuBandeau = "";
        _ = _session.RepondreAsync(invitation.Invitation, false);
        MettreAJourLeBandeau();
    }

    private bool Maintenu(DateTime? depuis)
        => depuis is { } debut && (DateTime.UtcNow - debut).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs);

    private void Appliquer(MultiplayerPanelModel.Effet effet)
    {
        switch (effet)
        {
            case MultiplayerPanelModel.Effet.PrendreLeFocus:
                _overlay.PrendreLeFocus();
                break;
            case MultiplayerPanelModel.Effet.RendreLeFocus:
                _overlay.RendreLeFocus();
                break;
            case MultiplayerPanelModel.Effet.Fermer:
                Fermer();
                return;
            case MultiplayerPanelModel.Effet.QuitterLaGameroom:
                _ = QuitterAsync();
                return;
            case MultiplayerPanelModel.Effet.AgirSurLaLigne:
                _ = InviterAsync();
                break;
            case MultiplayerPanelModel.Effet.ChargerLOnglet:
                if (_modele.OngletCourant != MultiplayerPanelModel.Onglet.WaitingForPlayers)
                {
                    _ = ChargerLesAmisAsync(CancellationToken.None);
                }

                break;
        }

        Rafraichir();
    }

    private async Task RejouerAsync()
    {
        var jeu = _jeu;
        if (jeu is null)
        {
            Fermer();
            return;
        }

        _journal.LogInformation("MULTIPLAYER : rejouer a {Jeu}", jeu.Game.Id);
        await OuvrirAsync(jeu.Path, string.Empty, string.Empty, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task QuitterAsync()
    {
        var room = RoomDuJeu();
        Fermer();
        if (room is not null)
        {
            await _session.QuitterAsync(room.Room).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : gameroom {Room} quittee (B maintenu)", room.Room);
        }
    }

    private async Task InviterAsync()
    {
        var room = RoomDuJeu();
        MultiplayerAmi? ami;
        lock (_verrou)
        {
            var invitables = Invitables();
            ami = _modele.Ligne >= 0 && _modele.Ligne < invitables.Count ? invitables[_modele.Ligne] : null;
        }

        if (room is null || ami is null)
        {
            return;
        }

        var refus = await _session.InviterAsync(room.Room, ami.Handle).ConfigureAwait(false);
        lock (_verrou)
        {
            _refus = refus;
        }

        await ChargerLesAmisAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ChargerLesAmisAsync(CancellationToken ct)
    {
        var amis = await _session.AmisAsync(ct).ConfigureAwait(false);
        lock (_verrou)
        {
            _amis = amis;
        }

        Rafraichir();
    }

    // ── l'etat qui change ────────────────────────────────────────────────────────────────────────

    private void SurChangement(MultiplayerEtat etat)
    {
        MultiplayerRoom? aPresenter = null;
        lock (_verrou)
        {
            foreach (var room in etat.Rooms)
            {
                _connues[room.Room] = room;
                if (room.State == "launching" && room.Launch is not null && _mode != Mode.Presentation)
                {
                    aPresenter = room;
                }
            }

            // Une invitation nouvelle s'affiche une fois ; celle qui disparait (repondue ou expiree) s'oublie.
            var nouvelle = etat.Invitations.FirstOrDefault(i => !_invitationsVues.Contains(i.Invitation));
            if (nouvelle is not null)
            {
                _invitationsVues.Add(nouvelle.Invitation);
                _invitation = nouvelle;
            }
            else if (_invitation is { } courante && etat.Invitations.All(i => i.Invitation != courante.Invitation))
            {
                _invitation = null;
            }
        }

        // La presentation s'efface quand la partie ne part plus : la gameroom est revenue a l'attente (l'hote
        // n'a pas publie sa session), ou rien n'arrive depuis deux minutes. Sinon le joueur restait devant
        // « Les joueurs se synchronisent… » pour toujours.
        bool presentationPerimee;
        lock (_verrou)
        {
            presentationPerimee = _mode == Mode.Presentation && !EnJeu()
                && (etat.Rooms.All(r => r.State is not ("launching" or "playing")) || DateTime.UtcNow - _presenteeDepuis > PresentationMax);
        }

        if (presentationPerimee)
        {
            _journal.LogInformation("MULTIPLAYER : la partie ne s'est pas ouverte, la presentation s'efface.");
            Fermer();
            Signaler(_textes.Text("multiplayer.launch.failed", Langue()));
            return;
        }

        if (aPresenter is not null && !EnJeu())
        {
            // Le decompte est fini : l'hote lance, les invites attendent sa session. Tout le monde voit la presentation.
            AnnoncerLeLancement(aPresenter, _session.JeuDeLaRoom(aPresenter));
            return;
        }

        Rafraichir();
        MettreAJourLeBandeau();
    }

    private void Rafraichir()
    {
        lock (_verrou)
        {
            if (_mode is not (Mode.Panneau or Mode.Resultat))
            {
                return;
            }
        }

        _overlay.MontrerMultiplayer(Composer());
    }

    /// <summary>
    /// Les cartes du haut d'ES, quand le panneau est ferme (retour du user du 22/10 : petites, discretes, en
    /// haut a droite, comme le cartouche d'un defi) : l'invitation d'abord, puis un message passager, puis une
    /// carte par jeu de « Votre selection » (V24-09), avec son decompte. Jamais pendant une partie.
    /// </summary>
    private void MettreAJourLeBandeau()
    {
        if (_cartes is null)
        {
            return;
        }

        var langue = Langue();
        string T(string cle) => _textes.Text(cle, langue);
        var cartes = new List<MultiplayerToastOverlay.Carte>();
        lock (_verrou)
        {
            if (_mode != Mode.Ferme || EnJeu())
            {
                cartes.Clear();
            }
            else
            {
                if (_invitation is { } invitation)
                {
                    cartes.Add(new MultiplayerToastOverlay.Carte(
                        T("multiplayer.invitation.title"),
                        _textes.Format("multiplayer.invitation", langue,
                            ("player", invitation.From?.Pseudo ?? string.Empty), ("game", NomDuJeu(invitation.Game))),
                        T("multiplayer.invitation.help"),
                        DateTime.UtcNow.AddMilliseconds(Math.Max(1000, invitation.ExpiresMs)),
                        Alerte: true));
                }

                if (_message is { } message && DateTime.UtcNow < _messageJusqua)
                {
                    cartes.Add(new MultiplayerToastOverlay.Carte("MULTIPLAYER", message, string.Empty, Alerte: true));
                }

                // Un seul bloc « Votre selection », un jeu par ligne (retour du user du 22/10).
                var lignes = _session.Etat.Rooms.Where(r => r.State is "waiting" or "countdown").Take(3).Select(room =>
                {
                    var absent = room.Players.Any(j => j.You && !j.Present);
                    return new MultiplayerToastOverlay.Ligne(
                        NomDuJeu(room.Game),
                        room.Players.Count(j => j.Present) + "/" + room.Max + "   " + T(absent ? "multiplayer.selection.away" : "multiplayer.selection.help"),
                        room is { State: "countdown", CountdownMs: { } ms } ? DateTime.UtcNow.AddMilliseconds(ms) : null);
                }).ToList();
                if (lignes.Count > 0)
                {
                    cartes.Add(new MultiplayerToastOverlay.Carte(T("multiplayer.selection"), string.Empty, string.Empty, Lignes: lignes));
                }
            }
        }

        // La pile ne change que si son contenu change ; le decompte, lui, se recalcule dans la carte.
        var cle = string.Join("|", cartes.Select(c => c.Surtitre + "/" + c.Titre + "/" + c.Detail + "/" + (c.FinDuDecompteUtc is null ? "" : "d")
            + string.Concat((c.Lignes ?? []).Select(l => "/" + l.Titre + "/" + l.Detail + "/" + (l.FinDuDecompteUtc is null ? "" : "d")))));
        if (cle == _texteDuBandeau && !cartes.Any(c => c.Decompte))
        {
            return;
        }

        _texteDuBandeau = cle;
        _cartes.Montrer(cartes);
    }

    /// <summary>Un message passager dans les cartes du haut (un refus), 5 s.</summary>
    private void Signaler(string message)
    {
        lock (_verrou)
        {
            _message = message;
            _messageJusqua = DateTime.UtcNow.AddSeconds(5);
        }

        MettreAJourLeBandeau();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5.5)).ConfigureAwait(false);
            MettreAJourLeBandeau();
        });
    }

    private bool EnJeu()
        => EmulatorForeground.EmulateurDeJeuTourne() || string.Equals(_contexte?.Ui.State, "playing", StringComparison.OrdinalIgnoreCase);

    private MultiplayerRoom? RoomDuJeu()
    {
        var jeu = _jeu;
        return jeu is null ? null : _session.Etat.Rooms.FirstOrDefault(room => room.Game == jeu.Game.Id && room.State != "finished");
    }

    /// <summary>Les amis qu'on peut inviter : en ligne, et pas deja avec vous.</summary>
    private List<MultiplayerAmi> Invitables() => AmisDuJeu().Where(ami => ami.State != "with_you").ToList();

    /// <summary>
    /// Les amis vus depuis le jeu du panneau. La plateforme dit « deja avec vous » des qu'un ami partage UNE de vos
    /// gamerooms ; ici, cela ne vaut que pour celle de ce jeu : un ami avec vous sur un autre jeu est seulement en ligne,
    /// et on peut l'inviter (retour du user du 22/10).
    /// </summary>
    private IEnumerable<MultiplayerAmi> AmisDuJeu()
    {
        var avecMoi = RoomDuJeu()?.Players.Select(j => j.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return _amis.Select(ami => ami.State == "with_you" && !avecMoi.Contains(ami.Handle) ? ami with { State = "in_es" } : ami);
    }

    private string NomDuJeu(string id) => _session.NomDuJeu(id);

    // ── la composition ─────────────────────────────────────────────────────────────────────────

    /// <summary>Ce que le panneau montre, compose en bloc depuis l'etat courant.</summary>
    internal LeaderboardOverlayService.ContenuMultiplayer Composer()
    {
        var langue = Langue();
        string T(string cle) => _textes.Text(cle, langue);
        lock (_verrou)
        {
            return _mode == Mode.Resultat ? ComposerLeResultat(T) : ComposerLePanneau(T);
        }
    }

    private LeaderboardOverlayService.ContenuMultiplayer ComposerLePanneau(Func<string, string> T)
    {
        var jeu = _jeu;
        var room = RoomDuJeu();
        var classique = (room?.Level ?? jeu?.Game.Level) != MultiplayerLevels.NelfePlay;
        var max = room?.Max ?? jeu?.Game.Players ?? 2;
        var presents = room?.Players.Count(joueur => joueur.Present) ?? 0;

        IReadOnlyList<LeaderboardOverlayService.JoueurMultiplayer> lignes = _modele.OngletCourant switch
        {
            MultiplayerPanelModel.Onglet.WaitingForPlayers => LesPlaces(room, max, T),
            MultiplayerPanelModel.Onglet.WaitingForFriends => AmisDuJeu()
                .Where(ami => ami.State is "with_you" or "in_es")
                .Select(ami => Ami(ami, T)).ToList(),
            _ => Invitables().Select(ami => Ami(ami, T)).ToList(),
        };
        _modele.PoserLesLignes(lignes.Count(ligne => !ligne.Libre));
        var (selection, selectionCourante) = _modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForPlayers
            ? LaBandeDesPersonnages()
            : (null, -1);

        var message = _refus is { Length: > 0 } refus ? T("multiplayer.refused." + refus) : string.Empty;
        if (message.StartsWith("multiplayer.refused.", StringComparison.Ordinal))
        {
            message = T("multiplayer.refused.other");
        }

        return new LeaderboardOverlayService.ContenuMultiplayer(
            MultiplayerPanelModel.Onglets.Select(MultiplayerPanelModel.Titre).ToList(),
            _modele.IndexOnglet,
            _modele.Etat == MultiplayerPanelModel.Foyer.Panneau,
            classique ? T("multiplayer.badge.classic") : string.Empty,
            classique ? T("multiplayer.info.classic") : string.Empty,
            presents + "/" + max,
            room is { State: "countdown", CountdownMs: { } ms } ? DateTime.UtcNow.AddMilliseconds(ms) : null,
            lignes,
            _modele.Ligne,
            message,
            string.Empty,
            AidesDuPanneau(T),
            Selection: selection,
            SelectionCourante: selectionCourante,
            Disposition: _modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForPlayers
                ? DispositionPour(max)
                : LeaderboardOverlayService.DispositionDesJoueurs.Liste);
    }

    /// <summary>
    /// Deux places : deux cartes cote a cote ; trois ou quatre : une grille 2 x 2 ; au-dela, une ligne par joueur
    /// (retour du user du 22/10).
    /// </summary>
    internal static LeaderboardOverlayService.DispositionDesJoueurs DispositionPour(int places) => places switch
    {
        2 => LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
        3 or 4 => LeaderboardOverlayService.DispositionDesJoueurs.Grille,
        _ => LeaderboardOverlayService.DispositionDesJoueurs.Liste,
    };

    /// <summary>
    /// Une carte par place, numerotee comme la manette du netplay : les joueurs dans l'ordre d'arrivee, puis les places
    /// libres jusqu'au nombre de joueurs du jeu (retour du user du 22/10).
    /// </summary>
    private List<LeaderboardOverlayService.JoueurMultiplayer> LesPlaces(MultiplayerRoom? room, int max, Func<string, string> T)
    {
        var lignes = (room?.Players ?? [])
            .Select((joueur, i) => Joueur(joueur, joueur.Present ? T("multiplayer.ready") : T("multiplayer.away"), T) with { Place = joueur.Seat ?? i + 1 })
            .ToList();
        if (room is null)
        {
            return lignes;
        }

        for (var place = lignes.Count + 1; place <= max; place++)
        {
            lignes.Add(new LeaderboardOverlayService.JoueurMultiplayer(T("multiplayer.seat.free"), string.Empty, string.Empty, false, false, false,
                null, T("multiplayer.seat.waiting"), Place: place, Libre: true));
        }

        return lignes;
    }

    /// <summary>NIVEAU NELFEPLAY : tous les personnages du jeu en portraits, le choix du joueur en avant (▲▼).</summary>
    private (IReadOnlyList<LeaderboardOverlayService.PortraitDeChoix>?, int) LaBandeDesPersonnages()
    {
        if (_jeu?.Game is not { EstNelfePlay: true, Choices: { Count: > 0 } choix } jeu)
        {
            return (null, -1);
        }

        var premier = choix[0];
        var mien = _session.MesChoix(jeu).GetValueOrDefault(premier.Key, premier.Default);
        var bande = premier.Values.Select(valeur => new LeaderboardOverlayService.PortraitDeChoix(PortraitDu(valeur.RaBadge), valeur.Name)).ToList();
        return (bande, premier.Values.ToList().FindIndex(valeur => valeur.Value == mien));
    }

    /// <summary>
    /// Le portrait d'un personnage : son badge RetroAchievements en cache, telecharge a la premiere demande (une image
    /// publique, sans cle). Le chemin est rendu tout de suite : le panneau dessine le portrait des qu'il est la.
    /// </summary>
    private string? PortraitDu(string? badge)
    {
        if (_retroAchievements?.CheminDuBadge(badge) is not { } chemin)
        {
            return null;
        }

        if (!File.Exists(chemin) && _demandes.TryAdd("badge:" + badge, true))
        {
            _ = Task.Run(async () =>
            {
                // Deux a la fois : une bande de vingt portraits demandee d'un coup se faisait limiter par le serveur, et
                // un portrait manque ne revenait plus avant un redemarrage de l'API (retour du user du 22/10). Un echec
                // se reessaie une minute plus tard, au dessin suivant.
                await _telechargementsDePortraits.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _retroAchievements.TelechargerLeBadgeAsync(badge!, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    _journal?.LogDebug(ex, "MULTIPLAYER : portrait {Badge} en echec", badge);
                }
                finally
                {
                    _telechargementsDePortraits.Release();
                }

                if (!File.Exists(chemin))
                {
                    _journal?.LogInformation("MULTIPLAYER : portrait {Badge} non telecharge (media.retroachievements.org), nouvel essai dans une minute", badge);
                    await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                    _demandes.TryRemove("badge:" + badge, out _);
                }

                Rafraichir();
            });
        }

        return chemin;
    }

    /// <summary>
    /// La planche d'avatar d'un joueur : la sienne si la borne la detient, sinon demandee au relais (comme pour la foule
    /// d'un direct) ; le panneau se redessine quand elle arrive.
    /// </summary>
    private string? PlancheDe(MultiplayerAvatar? avatar)
    {
        if (_planches is null || avatar?.Sheet is not { } sha || !RetroBat.Api.Avatar.AvatarSheetStore.EstSha(sha))
        {
            return null;
        }

        if (!_planches.Has(sha) && _relaisDesPlanches is not null && _demandes.TryAdd("planche:" + sha, true))
        {
            _ = Task.Run(async () =>
            {
                await _relaisDesPlanches.RequestAsync(sha, CancellationToken.None, "avatar").ConfigureAwait(false);
                for (var essai = 0; essai < 15 && !_planches.Has(sha); essai++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                Rafraichir();
            });
        }

        return _planches.ObjectPath(sha);
    }

    private LeaderboardOverlayService.ContenuMultiplayer ComposerLeResultat(Func<string, string> T)
    {
        _connues.TryGetValue(_roomDuResultat, out var connue);
        var finie = _session.Etat.Rooms.FirstOrDefault(r => r.Room == _roomDuResultat) ?? connue;
        var classique = (finie?.Level ?? _jeu?.Game.Level) != MultiplayerLevels.NelfePlay;
        // NIVEAU NELFEPLAY : la place gagnante lue chez l'hote (V24-16) ; le gagnant d'abord, une defaite presentee avec
        // le meme soin (00quater.3).
        var gagnante = classique ? null : finie?.Ended?.WinnerSeat;
        var elo = finie?.Ended?.Elo;
        MultiplayerEvolution? EloDe(int? place)
            => place is not null && elo is not null && elo.TryGetValue(place.Value.ToString(CultureInfo.InvariantCulture), out var e) ? e : null;
        // RANK-03 : la cote ne s'affiche qu'une fois le resultat valide, avec « Verification en cours » avant.
        var bandeauDuResultat = finie?.Ended?.Result switch
        {
            "pending" => T("multiplayer.result.pending"),
            "disputed" => T("multiplayer.result.disputed"),
            _ => string.Empty,
        };
        // En cartes, P1 reste a gauche ; en liste, le gagnant d'abord.
        var joueursDuResultat = (finie?.Players ?? []).Where(j => j.Seat is not null || finie?.State != "finished").ToList();
        var disposition = DispositionPour(finie?.Max ?? joueursDuResultat.Count);
        var joueurs = joueursDuResultat
            .OrderBy(j => disposition == LeaderboardOverlayService.DispositionDesJoueurs.Liste && gagnante is not null && j.Seat == gagnante ? 0 : 1)
            .ThenBy(j => j.Seat ?? 99)
            .Select(j =>
            {
                // Le numero de la place est dans sa pastille : l'etat n'a plus a dire « P1 ».
                var ligne = Joueur(j with { Present = true }, string.Empty, T) with { Place = j.Seat };
                if (gagnante is null || j.Seat is null)
                {
                    return ligne;
                }

                // La cote d'apres la rencontre, et son ecart (RANK-03 : seulement un resultat valide en porte une).
                var evolution = EloDe(j.Seat);
                var delta = evolution is null ? 0 : (int)Math.Round(evolution.After - evolution.Before);
                return ligne with
                {
                    Issue = T(j.Seat == gagnante ? "multiplayer.result.victory" : "multiplayer.result.defeat"),
                    Gagnant = j.Seat == gagnante,
                    Cote = evolution is null ? ligne.Cote : "ELO " + Math.Round(evolution.After).ToString(CultureInfo.InvariantCulture),
                    Evolution = evolution is null ? string.Empty : (delta >= 0 ? "+" : "") + delta.ToString(CultureInfo.InvariantCulture),
                    Stats = StatsDe(finie, j.Seat, T),
                };
            })
            .ToList();
        var maPlace = finie?.Players.FirstOrDefault(j => j.You)?.Seat;
        var titre = classique ? T("multiplayer.result.classic")
            : gagnante is not null && maPlace is not null ? T(maPlace == gagnante ? "multiplayer.result.victory" : "multiplayer.result.defeat")
            : T("multiplayer.result.title");
        var duree = finie?.Ended?.DurationS ?? _dureeDuResultat;
        var info = duree is { } secondes
            ? T("multiplayer.result.playtime") + " " + TimeSpan.FromSeconds(secondes).ToString(secondes >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
            : string.Empty;

        return new LeaderboardOverlayService.ContenuMultiplayer(
            [titre],
            0,
            false,
            classique ? T("multiplayer.badge.classic") : string.Empty,
            info,
            string.Empty,
            _finDuResultat,
            joueurs,
            -1,
            string.Empty,
            classique ? T("multiplayer.result.classic_note") : bandeauDuResultat,
            [new(_glypheValider, T("multiplayer.help.replay")), new(_glypheAnnuler, T("multiplayer.help.close"))],
            PartageDesDegats: classique ? null : LePartage(finie, T),
            Disposition: disposition);
    }

    /// <summary>La part des degats infliges par chaque place d'un duel, a leurs couleurs (P1 a gauche).</summary>
    private LeaderboardOverlayService.Partage? LePartage(MultiplayerRoom? room, Func<string, string> T)
    {
        if (room?.Ended?.Stats is not { } stats || !stats.TryGetValue("1", out var p1) || !stats.TryGetValue("2", out var p2)
            || p1.Damage + p2.Damage <= 0)
        {
            return null;
        }

        var couleur = (int place) => room.Players.FirstOrDefault(j => j.Seat == place) is { } j ? CouleurDe(j.Handle) : "8A93A6";
        return new LeaderboardOverlayService.Partage(p1.Damage / (double)(p1.Damage + p2.Damage), couleur(1), couleur(2), T("multiplayer.result.damage"));
    }

    private LeaderboardOverlayService.JoueurMultiplayer Joueur(MultiplayerJoueur joueur, string etat, Func<string, string> T)
        => new(
            joueur.Pseudo.Length > 0 ? joueur.Pseudo : joueur.Handle,
            joueur.Country,
            CouleurDe(joueur.Handle),
            joueur.Host,
            joueur.You,
            joueur.Present,
            Barres(joueur.RttMs),
            etat,
            T("multiplayer.host"),
            PersonnageDe(joueur),
            Avatar: PlancheDe(joueur.Avatar),
            Portrait: PortraitDeJoueur(joueur),
            // Versus classe : la cote du joueur sur ce jeu, a la suite de son pseudo ; « provisoire » pendant ses 10
            // premieres rencontres et tant qu'il n'a pas rencontre 3 adversaires differents (RANK-03).
            Cote: joueur.Rating is { } cote ? "ELO " + Math.Round(cote.Value).ToString(CultureInfo.InvariantCulture) : string.Empty,
            Provisoire: joueur.Rating is { Provisional: true } ? T("multiplayer.rating.provisional") : string.Empty);

    /// <summary>NIVEAU NELFEPLAY : le portrait du personnage de ce joueur.</summary>
    private string? PortraitDeJoueur(MultiplayerJoueur joueur)
    {
        if (_jeu?.Game is not { EstNelfePlay: true, Choices: { Count: > 0 } choix } jeu)
        {
            return null;
        }

        var premier = choix[0];
        var valeur = joueur.You ? _session.MesChoix(jeu).GetValueOrDefault(premier.Key, premier.Default)
            : joueur.Choices is not null && joueur.Choices.TryGetValue(premier.Key, out var v) ? v
            : premier.Default;
        return PortraitDu(premier.Values.FirstOrDefault(x => x.Value == valeur)?.RaBadge);
    }

    private async Task ChangerDePersonnageAsync(string room, MultiplayerGame jeu, int pas)
    {
        // Le nom change tout de suite a l'ecran ; la gameroom le recoit dans la foulee.
        var envoi = _session.ChangerDeChoixAsync(room, jeu, pas);
        Rafraichir();
        var refus = await envoi.ConfigureAwait(false);
        if (refus is not null)
        {
            _journal?.LogInformation("MULTIPLAYER : choix de personnage refuse ({Raison})", refus);
        }

        Rafraichir();
    }

    /// <summary>Les statistiques d'une place au resultat (ecran 10) : coups reussis, K.O., perfects, degats infliges.</summary>
    private IReadOnlyList<LeaderboardOverlayService.StatDuJoueur>? StatsDe(MultiplayerRoom? room, int? place, Func<string, string> T)
    {
        if (place is null || room?.Ended?.Stats is not { } stats
            || !stats.TryGetValue(place.Value.ToString(CultureInfo.InvariantCulture), out var s))
        {
            return null;
        }

        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        return
        [
            new(T("multiplayer.stat.hits"), N(s.Hits)), new(T("multiplayer.stat.kos"), N(s.Kos)),
            new(T("multiplayer.stat.perfects"), N(s.Perfects)), new(T("multiplayer.stat.damage"), N(s.Damage)),
        ];
    }

    /// <summary>NIVEAU NELFEPLAY : le nom du personnage de ce joueur (le premier choix du catalogue), ou rien.</summary>
    private string PersonnageDe(MultiplayerJoueur joueur)
    {
        var jeu = _jeu?.Game;
        if (jeu is not { EstNelfePlay: true, Choices: { Count: > 0 } choix })
        {
            return string.Empty;
        }

        var premier = choix[0];
        // Le sien d'abord : ▲▼ le change ici avant que la gameroom le renvoie.
        var valeur = joueur.You ? _session.MesChoix(jeu).GetValueOrDefault(premier.Key, premier.Default)
            : joueur.Choices is not null && joueur.Choices.TryGetValue(premier.Key, out var v) ? v
            : premier.Default;
        return premier.NomDe(valeur);
    }

    private LeaderboardOverlayService.JoueurMultiplayer Ami(MultiplayerAmi ami, Func<string, string> T)
        => new(ami.Pseudo.Length > 0 ? ami.Pseudo : ami.Handle, ami.Country, CouleurDe(ami.Handle), false, false,
            true, null, T("multiplayer.friend." + ami.State));

    private IReadOnlyList<LeaderboardOverlayService.Aide> AidesDuPanneau(Func<string, string> T)
    {
        if (_modele.Etat != MultiplayerPanelModel.Foyer.Panneau)
        {
            return [new("dpad_left", T("multiplayer.help.open")), new(_glypheAnnuler, T("multiplayer.help.close"))];
        }

        var aides = new List<LeaderboardOverlayService.Aide> { new("dpad_leftright", T("multiplayer.help.tab")) };
        if (_modele.OngletCourant == MultiplayerPanelModel.Onglet.InviteFriends && Invitables().Count > 0)
        {
            aides.Add(new("dpad_updown", T("multiplayer.help.choose")));
            aides.Add(new(_glypheValider, T("multiplayer.help.invite")));
        }
        else if (_modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForPlayers && _jeu?.Game is { EstNelfePlay: true, Choices.Count: > 0 })
        {
            aides.Add(new("dpad_updown", T("multiplayer.help.character")));
        }

        aides.Add(new(_glypheAnnuler, T("multiplayer.help.back")));
        aides.Add(new(_glypheAnnuler, T("multiplayer.help.leave")));
        return aides;
    }

    /// <summary>La latence vers le relais, en quatre barres.</summary>
    internal static int? Barres(int? rttMs) => rttMs switch
    {
        null or <= 0 => null,
        < 40 => 4,
        < 80 => 3,
        < 140 => 2,
        _ => 1,
    };

    internal static string CouleurDe(string poignee)
    {
        var somme = 0;
        foreach (var caractere in poignee)
        {
            somme = unchecked(somme * 31 + caractere);
        }

        return Couleurs[(int)((uint)somme % (uint)Couleurs.Length)];
    }

    private string Langue()
    {
        if (_langue.Length > 0)
        {
            return _langue;
        }

        try
        {
            _langue = _reglages.GetScrapingSettings().Language;
        }
        catch (Exception)
        {
            _langue = "en";
        }

        return _langue;
    }
}
