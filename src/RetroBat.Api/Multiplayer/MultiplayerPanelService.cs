using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Le panneau MULTIPLAYER d'ES (CDC MULTIPLAYER v2.5, lot M3) : ce qu'il montre et ce que la manette y
/// fait. Il s'ouvre par l'appui long sur un jeu de la collection MULTIPLAYER (V24-01, V24-03) : le
/// joueur est place dans une gameroom tout de suite, deja pret, et le panneau montre qui l'attend.
///
/// La fenetre, le focus et l'habillage sont ceux du panneau de classement
/// (<see cref="LeaderboardOverlayService"/>) ; l'appui long et la lecture de la manette restent a
/// <see cref="LeaderboardInputService"/>, qui transmet ici les gestes tant que ce panneau est ouvert.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerPanelService
{
    /// <summary>Les couleurs d'avatar, tirees de la poignee : la meme pour un joueur sur toutes les bornes.</summary>
    private static readonly string[] Couleurs = ["A98BFF", "78E5A3", "FF9F6B", "49E3CB", "FFD37A", "FF8C9A"];

    private readonly MultiplayerSessionService _session;
    private readonly LeaderboardOverlayService _overlay;
    private readonly InterfaceTextService _textes;
    private readonly EmulationStationSettingsService _reglages;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MultiplayerPanelService> _journal;
    private readonly MultiplayerPanelModel _modele = new();
    private readonly object _verrou = new();
    private MultiplayerInstalledGame? _jeu;
    private IReadOnlyList<MultiplayerAmi> _amis = [];
    private string? _refus;
    private DateTime? _annulerDepuis;
    private string _glypheValider = "button_a";
    private string _glypheAnnuler = "button_b";
    private string _langue = "";

    public MultiplayerPanelService(
        MultiplayerSessionService session,
        LeaderboardOverlayService overlay,
        InterfaceTextService textes,
        EmulationStationSettingsService reglages,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<MultiplayerPanelService> journal)
    {
        _session = session;
        _overlay = overlay;
        _textes = textes;
        _reglages = reglages;
        _options = options;
        _journal = journal;
        _session.Change += _ => Rafraichir();
    }

    /// <summary>Le panneau est-il ouvert ? Tant qu'il l'est, les gestes viennent ici.</summary>
    public bool Ouvert => _modele.Etat != MultiplayerPanelModel.Foyer.Ferme;

    /// <summary>MULTIPLAYER est-il en service sur cette borne ?</summary>
    public bool Actif => _options.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true };

    /// <summary>
    /// L'APPUI LONG sur un jeu de la collection : le joueur rejoint une gameroom et le panneau s'ouvre.
    /// Rend faux quand le jeu n'est pas MULTIPLAYER sur cette borne : le classement garde la main.
    /// </summary>
    public async Task<bool> OuvrirAsync(string cheminDuJeu, string glypheValider, string glypheAnnuler, CancellationToken ct)
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
            _glypheValider = glypheValider.Length > 0 ? glypheValider : "button_a";
            _glypheAnnuler = glypheAnnuler.Length > 0 ? glypheAnnuler : "button_b";
            _langue = "";
            _modele.Ouvrir();
        }

        _session.PanneauOuvert = true;
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
        _journal.LogInformation("MULTIPLAYER : panneau ouvert sur {Jeu}", jeu.Game.Id);

        var refus = await _session.RejoindreAsync(cheminDuJeu, ct).ConfigureAwait(false);
        lock (_verrou)
        {
            _refus = refus;
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
            _annulerDepuis = null;
        }

        _session.PanneauOuvert = false;
        _overlay.Fermer();
    }

    /// <summary>Un jeu demarre : la fenetre s'efface sans reprendre le premier plan a l'emulateur.</summary>
    public void FermerPourLeJeu()
    {
        lock (_verrou)
        {
            _modele.Fermer();
        }

        _session.PanneauOuvert = false;
        _overlay.FermerPourLeJeu();
    }

    /// <summary>Une direction du DPAD : <c>left</c>, <c>right</c>, <c>up</c>, <c>down</c>.</summary>
    public void Direction(string? identite)
    {
        MultiplayerPanelModel.Effet effet;
        lock (_verrou)
        {
            effet = (identite ?? string.Empty).ToLowerInvariant() switch
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

    /// <summary>Le bouton « valider » relache : agir sur la ligne.</summary>
    public void Valider()
    {
        MultiplayerPanelModel.Effet effet;
        lock (_verrou)
        {
            effet = _modele.Valider();
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

            var maintenu = _annulerDepuis is { } depuis
                && (DateTime.UtcNow - depuis).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs);
            _annulerDepuis = null;
            effet = maintenu ? _modele.AnnulerMaintenu() : _modele.Annuler();
        }

        Appliquer(effet);
    }

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
            ami = _modele.Ligne < invitables.Count ? invitables[_modele.Ligne] : null;
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

    private void Rafraichir()
    {
        if (!Ouvert)
        {
            return;
        }

        _overlay.MontrerMultiplayer(Composer());
    }

    private MultiplayerRoom? RoomDuJeu()
    {
        var jeu = _jeu;
        return jeu is null ? null : _session.Etat.Rooms.FirstOrDefault(room => room.Game == jeu.Game.Id && room.State != "finished");
    }

    /// <summary>Les amis qu'on peut inviter : en ligne, et pas deja avec vous.</summary>
    private List<MultiplayerAmi> Invitables() => _amis.Where(ami => ami.State != "with_you").ToList();

    /// <summary>Ce que le panneau montre, compose en bloc depuis l'etat courant.</summary>
    internal LeaderboardOverlayService.ContenuMultiplayer Composer()
    {
        var langue = Langue();
        string T(string cle) => _textes.Text(cle, langue);
        lock (_verrou)
        {
            var jeu = _jeu;
            var room = RoomDuJeu();
            var classique = (room?.Level ?? jeu?.Game.Level) != MultiplayerLevels.NelfePlay;
            var max = room?.Max ?? jeu?.Game.Players ?? 2;
            var presents = room?.Players.Count(joueur => joueur.Present) ?? 0;

            IReadOnlyList<LeaderboardOverlayService.JoueurMultiplayer> lignes = _modele.OngletCourant switch
            {
                MultiplayerPanelModel.Onglet.WaitingForPlayers => (room?.Players ?? []).Select(joueur => new LeaderboardOverlayService.JoueurMultiplayer(
                    joueur.Pseudo.Length > 0 ? joueur.Pseudo : joueur.Handle,
                    joueur.Country,
                    CouleurDe(joueur.Handle),
                    joueur.Host,
                    joueur.You,
                    joueur.Present,
                    Barres(joueur.RttMs),
                    joueur.Present ? T("multiplayer.ready") : T("multiplayer.away"),
                    T("multiplayer.host"))).ToList(),
                MultiplayerPanelModel.Onglet.WaitingForFriends => _amis
                    .Where(ami => ami.State is "with_you" or "in_es")
                    .Select(ami => Ami(ami, T)).ToList(),
                _ => Invitables().Select(ami => Ami(ami, T)).ToList(),
            };
            _modele.PoserLesLignes(lignes.Count);

            var bandeau = room?.State is "launching" or "playing"
                ? (classique ? T("multiplayer.classic.message") + " " : string.Empty) + T("multiplayer.syncing")
                : string.Empty;
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
                bandeau,
                Aides(T));
        }
    }

    private LeaderboardOverlayService.JoueurMultiplayer Ami(MultiplayerAmi ami, Func<string, string> T)
        => new(ami.Pseudo.Length > 0 ? ami.Pseudo : ami.Handle, ami.Country, CouleurDe(ami.Handle), false, false,
            true, null, T("multiplayer.friend." + ami.State));

    private IReadOnlyList<LeaderboardOverlayService.Aide> Aides(Func<string, string> T)
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
