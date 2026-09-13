using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Ce qui ouvre le panneau de classement et le pilote : les boutons de la borne.
///
/// L'appui LONG sur « valider » ouvre le menu de jeu d'EmulationStation ; on l'observe en
/// meme temps que lui, et on affiche notre panneau a cote. ES ne sait rien de nous, et nous ne
/// lui prenons rien : le canal panel lit la manette en direct, sans dependre du focus.
///
/// LES BOUTONS SE DESIGNENT PAR LEUR SLOT, jamais par une lettre. Le slot vient de la
/// cartographie mesuree par le wizard LedManagerSetup sur CETTE borne, et c'est aussi celui
/// qu'eclairent les LED : sur la borne de reference, le bouton « valider » remonte sous
/// l'identite `b` et le slot 1. Coder « A » en dur donnerait un panneau qui s'ouvre sur le
/// mauvais bouton d'une borne a l'autre.
///
/// Le panneau ne s'ouvre QUE dans le menu (`state == browsing`), jamais en jeu ni pendant une
/// lecture de replay : devant un score en cours, rien ne doit passer devant l'ecran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LeaderboardInputService : IHostedService, IDisposable
{
    /// <summary>Les slots de la cartographie de la borne, pas des lettres de manette.</summary>
    private const int SlotValider = 1;
    private const int SlotAnnuler = 2;
    private const int SlotAgir = 3;
    private const int SlotPageHaut = 5;
    private const int SlotPageBas = 6;

    private readonly IEventBus _bus;
    private readonly ApiContext _context;
    private readonly LeaderboardOverlayService _overlay;
    private readonly LeaderboardClient _client;
    private readonly NelfePlayAgentService _agent;
    private readonly NelfePlayScoringSessionService _session;
    private readonly Replay.Playback.ReplayPlaybackService _playback;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<LeaderboardInputService> _logger;

    private readonly LeaderboardPanelModel _modele = new();
    private readonly object _gate = new();
    private IDisposable? _abonnement;
    private CancellationTokenSource? _arret;
    private DateTime? _validerDepuis;
    private IReadOnlyList<LeaderboardClient.Ligne> _monde = Array.Empty<LeaderboardClient.Ligne>();
    private string _etatDuMonde = LeaderboardClient.EtatAucunScore;
    private string _jeuAffiche = "";
    private string _romGroup = "";
    private string _maVille = "";
    private string _monPays = "";
    private string _maSalle = "";

    public LeaderboardInputService(
        IEventBus bus,
        ApiContext context,
        LeaderboardOverlayService overlay,
        LeaderboardClient client,
        NelfePlayAgentService agent,
        NelfePlayScoringSessionService session,
        Replay.Playback.ReplayPlaybackService playback,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<LeaderboardInputService> logger)
    {
        _bus = bus;
        _context = context;
        _overlay = overlay;
        _client = client;
        _agent = agent;
        _session = session;
        _playback = playback;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Leaderboard.Enabled) return Task.CompletedTask;
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        _arret = new CancellationTokenSource();
        _ = Task.Run(() => ChienDeGardeAsync(_arret.Token), CancellationToken.None);
        _logger.LogInformation("Classement : panneau arme (appui long sur le slot {Slot}).", SlotValider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret?.Cancel();
        _abonnement?.Dispose();
        _overlay.Fermer();
        return Task.CompletedTask;
    }

    // ── Les entrees ──────────────────────────────────────────────────────────

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            // Un jeu demarre : le panneau n'a plus rien a faire la, et rien ne doit passer
            // devant l'ecran de jeu.
            if (string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal))
            {
                Fermer();
                return;
            }

            var appuye = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
            var relache = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
            if (!appuye && !relache) return;

            var (identite, systeme, slot) = LireBouton(e.Payload);

            // Les directions arrivent par le canal DPAD, sans slot.
            if (string.Equals(systeme, "DPAD", StringComparison.OrdinalIgnoreCase))
            {
                if (appuye) Direction(identite);
                return;
            }
            if (slot is null) return;

            if (slot == SlotValider)
            {
                if (appuye)
                {
                    _validerDepuis = DateTime.UtcNow;
                    return;
                }
                _validerDepuis = null;
                return;
            }

            if (!appuye) return;
            var entree = slot switch
            {
                SlotAnnuler => EntreePanneau.Annuler,
                SlotAgir => EntreePanneau.Agir,
                SlotPageHaut => EntreePanneau.PageHaut,
                SlotPageBas => EntreePanneau.PageBas,
                _ => (EntreePanneau?) null,
            };
            if (entree is not null) Appliquer(_modele.Entree(entree.Value));
        }
        catch (Exception ex)
        {
            // Une erreur ici ne doit jamais empecher la borne de repondre a sa manette.
            _logger.LogDebug(ex, "Classement : entree ignoree.");
        }
    }

    private void Direction(string? identite)
    {
        var entree = (identite ?? "").ToLowerInvariant() switch
        {
            "left" => EntreePanneau.Gauche,
            "right" => EntreePanneau.Droite,
            "up" => EntreePanneau.Haut,
            "down" => EntreePanneau.Bas,
            _ => (EntreePanneau?) null,
        };
        if (entree is null) return;
        Appliquer(_modele.Entree(entree.Value));
    }

    /// <summary>
    /// Le seuil d'ouverture, surveille a part : `pressed` arrive des l'appui, donc on peut ouvrir
    /// PENDANT que le bouton est encore tenu, au moment meme ou ES ouvre son menu. Attendre le
    /// relache ferait apparaitre le panneau apres coup.
    /// </summary>
    private async Task ChienDeGardeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(80, ct).ConfigureAwait(false);

                if (_validerDepuis is { } depuis
                    && _modele.Etat == LeaderboardPanelModel.Foyer.Ferme
                    && (DateTime.UtcNow - depuis).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs))
                {
                    _validerDepuis = null;
                    await OuvrirAsync(ct).ConfigureAwait(false);
                }

                // Le filet : si nous tenons la manette et que plus rien n'arrive, on la rend.
                if (_overlay.TropLongSilence())
                {
                    Appliquer(LeaderboardPanelModel.Effet.Fermer);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Classement : cycle de surveillance en echec.");
            }
        }
    }

    // ── Ouvrir, charger, agir ────────────────────────────────────────────────

    private async Task OuvrirAsync(CancellationToken ct)
    {
        if (!PeutSOuvrir(out var systeme, out var nomDuJeu)) return;

        _romGroup = GamelistIdentity.Slugifier(nomDuJeu);
        _jeuAffiche = nomDuJeu;
        if (_romGroup.Length == 0) return;

        var session = _session.Get();
        _maSalle = session?.VenueName ?? "";
        _maVille = session?.VenueCity ?? "";
        _monPays = "";

        var style = EsThemeStyle.Lire(_logger);
        lock (_gate)
        {
            _monde = Array.Empty<LeaderboardClient.Ligne>();
            _etatDuMonde = "";
        }
        _modele.Ouvrir(salleConnue: _maSalle.Length > 0, villeConnue: _maVille.Length > 0, paysConnu: false, aDesRecords: true);
        _overlay.Ouvrir(style, Composer());
        _logger.LogInformation("Classement : panneau ouvert sur {Jeu} ({Systeme}).", _romGroup, systeme);

        await ChargerAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Les conditions d'ouverture. Toutes doivent tenir : un panneau qui s'ouvre au mauvais moment se ferme mal.</summary>
    private bool PeutSOuvrir(out string systeme, out string nomDuJeu)
    {
        systeme = "";
        nomDuJeu = "";
        if (_playback.IsBusy) return false;                                   // un replay se lit
        if (EmulatorForeground.EmulateurTourne()) return false;               // un jeu tourne
        if (!EsEstDevant()) return false;                                     // ES n'est pas devant

        var ui = _context.Ui;
        if (!string.Equals(ui.State, "browsing", StringComparison.OrdinalIgnoreCase)) return false;
        var jeu = ui.Selected;
        if (jeu is null) return false;
        systeme = jeu.SystemId ?? "";
        nomDuJeu = jeu.GameName ?? "";
        return nomDuJeu.Length > 0;
    }

    /// <summary>
    /// EmulationStation est-il la fenetre active ? Le panneau ne s'invite pas par-dessus autre
    /// chose : si le joueur est dans une autre application, l'appui long ne nous regarde pas.
    /// </summary>
    private static bool EsEstDevant()
    {
        try
        {
            var devant = GetForegroundWindow();
            foreach (var p in Process.GetProcessesByName("emulationstation"))
            {
                using (p)
                {
                    if (p.MainWindowHandle == devant) return true;
                }
            }
        }
        catch (Exception)
        {
            // Pas de fenetre lisible : on s'abstient plutot que d'ouvrir au mauvais moment.
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private async Task ChargerAsync(CancellationToken ct)
    {
        var pseudo = _agent.Status.Pseudo ?? "";
        var resultat = await _client.MondeAsync(_romGroup, pseudo, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _monde = resultat.Lignes;
            _etatDuMonde = resultat.Etat;
            // On apprend sa ville et son pays de SA PROPRE ligne : la borne ne les connait pas
            // autrement, et un onglet qu'on ne peut pas remplir ne doit pas exister.
            var mienne = _monde.FirstOrDefault(l => l.CestMoi);
            if (mienne is not null)
            {
                if (_maVille.Length == 0) _maVille = mienne.Ville;
                _monPays = mienne.Pays;
            }
        }
        if (_modele.Etat == LeaderboardPanelModel.Foyer.Ferme) return;
        Rafraichir();
    }

    private void Appliquer(LeaderboardPanelModel.Effet effet)
    {
        switch (effet)
        {
            case LeaderboardPanelModel.Effet.PrendreLeFocus:
                if (!_overlay.PrendreLeFocus())
                {
                    // Refuse : on retourne a l'etat ou ES navigue, sinon le panneau aurait l'air
                    // actif sans repondre.
                    _modele.Entree(EntreePanneau.Droite);
                }
                Rafraichir();
                break;

            case LeaderboardPanelModel.Effet.RendreLeFocus:
                _overlay.RendreLeFocus();
                Rafraichir();
                break;

            case LeaderboardPanelModel.Effet.Fermer:
                Fermer();
                break;

            case LeaderboardPanelModel.Effet.ChargerLaVue:
                Rafraichir();
                break;

            case LeaderboardPanelModel.Effet.AgirSurLaLigne:
                Agir();
                break;

            default:
                Rafraichir();
                break;
        }
    }

    private void Fermer()
    {
        if (_modele.Etat == LeaderboardPanelModel.Foyer.Ferme && !_overlay.Affiche) return;
        _modele.Fermer();
        _overlay.Fermer();
    }

    private void Agir()
    {
        var ligne = LignesDeLaVue().ElementAtOrDefault(_modele.Ligne);
        if (ligne?.ReplayId is not { Length: > 0 } replay)
        {
            return;
        }
        // Le replay se lance par le moteur, jamais par une ligne de commande recomposee : c'est
        // lui qui verifie l'identite de la ROM et qui va chercher l'objet s'il manque.
        _logger.LogInformation("Classement : lecture du replay {Replay} demandee.", replay);
        _overlay.Fermer();
        _modele.Fermer();
        _ = Task.Run(async () =>
        {
            try { await _playback.PlayAsync(replay, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Classement : le replay {Replay} n'a pas demarre.", replay); }
        });
    }

    private IReadOnlyList<LeaderboardClient.Ligne> LignesDeLaVue()
    {
        lock (_gate)
        {
            return LeaderboardClient.Tailler(_monde, _modele.VueCourante, _maVille, _monPays, _maSalle);
        }
    }

    private void Rafraichir() => _overlay.Montrer(Composer());

    private LeaderboardOverlayService.Contenu Composer()
    {
        var lignes = LignesDeLaVue();
        _modele.PoserLesLignes(lignes.Count);
        string etat;
        lock (_gate)
        {
            etat = _etatDuMonde.Length == 0
                ? ""
                : lignes.Count > 0 ? LeaderboardClient.EtatOk : _etatDuMonde;
        }
        return new LeaderboardOverlayService.Contenu(
            _jeuAffiche,
            _modele.Onglets.Select(Nom).ToList(),
            _modele.IndexOnglet,
            lignes,
            _modele.Ligne,
            etat,
            _modele.SurLaPorte,
            _modele.Etat == LeaderboardPanelModel.Foyer.Panneau);
    }

    /// <summary>Le nom d'une vue, tel qu'il s'affiche sur son onglet.</summary>
    public static string Nom(LeaderboardPanelModel.Vue vue) => vue switch
    {
        LeaderboardPanelModel.Vue.MesRecords => "Mes records",
        LeaderboardPanelModel.Vue.CetteBorne => "Cette borne",
        LeaderboardPanelModel.Vue.MaSalle => "Ma salle",
        LeaderboardPanelModel.Vue.MaVille => "Ma ville",
        LeaderboardPanelModel.Vue.MonPays => "Mon pays",
        _ => "Monde",
    };

    private static (string? Identite, string? Systeme, int? Slot) LireBouton(object? payload)
    {
        if (payload is null) return (null, null, null);
        try
        {
            var el = JsonSerializer.SerializeToElement(payload);
            string? Texte(string nom) => el.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Nombre(string nom) => el.TryGetProperty(nom, out var v) && v.TryGetInt32(out var n) ? n : null;
            return (Texte("Identity"), Texte("System"), Nombre("Slot"));
        }
        catch
        {
            return (null, null, null);
        }
    }

    public void Dispose()
    {
        _arret?.Dispose();
        _abonnement?.Dispose();
    }
}
