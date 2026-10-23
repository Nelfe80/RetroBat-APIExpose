using System.Drawing;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Controllers;

/// <summary>
/// NELFEPLAY MULTIPLAYER sur cette borne : la collection, les gamerooms du joueur, et les gestes du
/// panneau (rejoindre, quitter, inviter, repondre), pour le panneau d'ES comme pour un essai depuis
/// Swagger. Les mots de passe netplay ne sortent jamais par ici.
/// </summary>
[ApiController]
[Tags("Multiplayer")]
[Route("api/v1/multiplayer")]
public sealed class MultiplayerController : ControllerBase
{
    private readonly MultiplayerCollectionSyncService _collection;
    private readonly MultiplayerSessionService _session;

    public MultiplayerController(MultiplayerCollectionSyncService collection, MultiplayerSessionService session)
    {
        _collection = collection;
        _session = session;
    }

    /// <summary>La collection (catalogue, jeux trouves, manquants) et l'etat des gamerooms.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        collection = _collection.Status,
        games = _collection.Games.Select(jeu => new
        {
            id = jeu.Game.Id,
            name = jeu.Game.Name,
            level = jeu.Game.Level,
            players = jeu.Game.Players,
            core = jeu.Core?.Core,
            content = jeu.ContentKey,
            path = jeu.Path,
        }),
        session = _session.Etat.SansSecrets(),
    });

    public sealed record RejoindreDemande(string Path);

    /// <summary>L'appui long sur un jeu de la collection.</summary>
    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] RejoindreDemande demande, CancellationToken ct)
    {
        var refus = await _session.RejoindreAsync(demande.Path, ct);
        return refus is null ? Ok(new { ok = true }) : Conflict(new { ok = false, reason = refus });
    }

    public sealed record RoomDemande(string Room);

    [HttpPost("leave")]
    public async Task<IActionResult> Leave([FromBody] RoomDemande demande, CancellationToken ct)
        => await _session.QuitterAsync(demande.Room, ct) ? Ok(new { ok = true }) : Conflict(new { ok = false });

    public sealed record InviterDemande(string Room, string Handle);

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviterDemande demande, CancellationToken ct)
    {
        var refus = await _session.InviterAsync(demande.Room, demande.Handle, ct);
        return refus is null ? Ok(new { ok = true }) : Conflict(new { ok = false, reason = refus });
    }

    public sealed record ReponseDemande(string Invitation, bool Accept);

    [HttpPost("invitation")]
    public async Task<IActionResult> Answer([FromBody] ReponseDemande demande, CancellationToken ct)
    {
        var refus = await _session.RepondreAsync(demande.Invitation, demande.Accept, ct);
        return refus is null ? Ok(new { ok = true }) : Conflict(new { ok = false, reason = refus });
    }

    /// <summary>Les amis en ligne (INVITE FRIENDS).</summary>
    [HttpGet("friends")]
    public async Task<IActionResult> Friends(CancellationToken ct) => Ok(await _session.AmisAsync(ct));

    /// <summary>
    /// L'APERCU d'un ecran MULTIPLAYER en image, dessine hors ecran avec le theme de la borne (outil de mise au
    /// point) : <c>panel</c>, <c>panel-focus</c>, <c>invite</c>, <c>presentation</c>, <c>result</c>, <c>cards</c>, et au niveau
    /// NelfePlay <c>np-panel</c>, <c>np-presentation</c>, <c>np-result</c>.
    /// Joueurs fictifs ; rien n'est montre a l'ecran ni envoye a la plateforme.
    /// </summary>
    [HttpGet("preview/{vue}")]
    [SupportedOSPlatform("windows")]
    public IActionResult Preview(string vue, [FromServices] InterfaceTextService textes, [FromServices] EmulationStationSettingsService reglages,
        [FromServices] ILogger<LeaderboardOverlayService> journal, [FromServices] RetroBat.Api.Avatar.AvatarSheetStore planches,
        [FromServices] RetroAchievementsService retroAchievements, [FromServices] MultiplayerCatalogueStore catalogue,
        [FromQuery] string? jeu = null)
    {
        // Une vraie planche de la borne pour l'avatar, et les portraits des personnages de SF2CE (badges en cache) ; avec
        // ?jeu=arcade/magdrop3, la bande du choix de ce jeu, images du Data Pack d'abord, comme le panneau.
        var planche = planches.Objets().FirstOrDefault() is { } sha ? planches.ObjectPath(sha) : null;
        string? Portrait(int badge) => retroAchievements.CheminDuBadge(badge.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var noms = new[] { "RYU", "E. HONDA", "BLANKA", "GUILE", "KEN", "CHUN-LI", "ZANGIEF", "DHALSIM", "M. BISON", "SAGAT", "BALROG", "VEGA" };
        var bande = noms.Select((nom, i) => new LeaderboardOverlayService.PortraitDeChoix(Portrait(307536 + i), nom)).ToList();
        if (jeu is not null && catalogue.Lire(out _)?.Games.FirstOrDefault(g => g.Id == jeu)?.Choices is { Count: > 0 } choix)
        {
            var dossier = Path.GetDirectoryName(catalogue.Path)!;
            bande = choix[0].Values.Select(v => new LeaderboardOverlayService.PortraitDeChoix(
                v.CheminDeLImage(dossier) is { } image && System.IO.File.Exists(image) ? image : retroAchievements.CheminDuBadge(v.RaBadge), v.Name)).ToList();
        }
        string langue;
        try { langue = reglages.GetScrapingSettings().Language; } catch (Exception) { langue = "fr"; }
        string T(string cle) => textes.Text(cle, langue);
        var style = EsMenuStyle.Lire(journal);
        var onglets = MultiplayerPanelModel.Onglets.Select(MultiplayerPanelModel.Titre).ToList();
        var aides = new List<LeaderboardOverlayService.Aide>
        {
            new("dpad_leftright", T("multiplayer.help.tab")), new("button_b", T("multiplayer.help.back")), new("button_b", T("multiplayer.help.leave")),
        };
        LeaderboardOverlayService.JoueurMultiplayer Joueur(string nom, string pays, string couleur, bool hote, bool moi, bool present, int? barres, string etat)
            => new(nom, pays, couleur, hote, moi, present, barres, etat, T("multiplayer.host"));
        List<LeaderboardOverlayService.StatDuJoueur> Stats(int coups, int ko, int perfects, int degats)
            => [new(T("multiplayer.stat.hits"), coups.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(T("multiplayer.stat.kos"), ko.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(T("multiplayer.stat.perfects"), perfects.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(T("multiplayer.stat.damage"), degats.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        var deux = new List<LeaderboardOverlayService.JoueurMultiplayer>
        {
            Joueur("PLAYER 1", "FR", "A98BFF", false, true, true, 3, T("multiplayer.ready")),
            Joueur("PLAYER 2", "JP", "FF9F6B", true, false, true, 4, T("multiplayer.ready")),
        };
        var socle = new LeaderboardOverlayService.ContenuMultiplayer(onglets, 2, false, T("multiplayer.badge.classic"), T("multiplayer.info.classic"),
            "1/2", null, deux.Take(1).ToList(), 0, string.Empty, string.Empty,
            [new("dpad_left", T("multiplayer.help.open")), new("button_b", T("multiplayer.help.close"))]);
        try
        {
            byte[] png = vue switch
            {
                "panel" => LeaderboardOverlayService.ApercuMultiplayer(style, socle, "", "", "", journal),
                "panel-focus" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    NousAvonsLaMain = true, Compte = "2/2", Lignes = deux.Select((j, i) => j with { Place = i + 1 }).ToList(), DecompteFinUtc = DateTime.UtcNow.AddSeconds(4), Aides = aides,
                    Disposition = LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
                }, "", "", "", journal),
                "invite" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    OngletCourant = 0, NousAvonsLaMain = true, Lignes =
                    [
                        Joueur("PLAYER 3", "BE", "78E5A3", false, false, true, null, T("multiplayer.friend.in_es")),
                        Joueur("PLAYER 4", "DE", "FFD37A", false, false, true, null, T("multiplayer.friend.invited")),
                    ],
                    Aides = [new("dpad_leftright", T("multiplayer.help.tab")), new("dpad_updown", T("multiplayer.help.choose")), new("button_a", T("multiplayer.help.invite")), new("button_b", T("multiplayer.help.back"))],
                }, "", "", "", journal),
                "presentation" => LeaderboardOverlayService.ApercuMultiplayer(style, socle, T("multiplayer.syncing"), "Metal Slug 3", T("multiplayer.classic.message"), journal),
                "result" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    Onglets = [T("multiplayer.result.classic")], OngletCourant = 0, Compte = string.Empty,
                    Info = T("multiplayer.result.playtime") + " 7:42", DecompteFinUtc = DateTime.UtcNow.AddSeconds(12),
                    Lignes = [Joueur("PLAYER 1", "FR", "A98BFF", false, true, true, 3, "") with { Place = 1 }, Joueur("PLAYER 2", "JP", "FF9F6B", true, false, true, 4, "") with { Place = 2 }],
                    LigneCourante = -1, Bandeau = T("multiplayer.result.classic_note"), Disposition = LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
                    Aides = [new("button_a", T("multiplayer.help.replay")), new("button_b", T("multiplayer.help.close"))],
                }, "", "", "", journal),
                "np-panel" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    Badge = string.Empty, Info = "1 VS 1", NousAvonsLaMain = true, Compte = "1/2", DecompteFinUtc = null,
                    Lignes =
                    [
                        deux[0] with { Personnage = "KEN", Avatar = planche, Portrait = Portrait(307540), Place = 1, Cote = "ELO 1516", Provisoire = T("multiplayer.rating.provisional") },
                        new(T("multiplayer.seat.free"), "", "", false, false, false, null, T("multiplayer.seat.waiting"), Place: 2, Libre: true),
                    ],
                    Selection = bande, SelectionCourante = 4, LigneCourante = -1, Disposition = LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
                    Aides = [new("dpad_leftright", T("multiplayer.help.tab")), new("dpad_updown", T("multiplayer.help.character")), new("button_b", T("multiplayer.help.back"))],
                }, "", "", "", journal),
                "np-panel-full" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    Badge = string.Empty, Info = "1 VS 1", NousAvonsLaMain = true, Compte = "2/2", DecompteFinUtc = DateTime.UtcNow.AddSeconds(4),
                    Lignes =
                    [
                        deux[0] with { Personnage = "KEN", Avatar = planche, Portrait = Portrait(307540), Place = 1, Cote = "ELO 1516", Provisoire = T("multiplayer.rating.provisional") },
                        deux[1] with { Personnage = "CHUN-LI", Portrait = Portrait(307541), Place = 2, Cote = "ELO 1484", Provisoire = T("multiplayer.rating.provisional") },
                    ],
                    Selection = bande, SelectionCourante = 4, LigneCourante = -1, Disposition = LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
                    Aides = [new("dpad_leftright", T("multiplayer.help.tab")), new("dpad_updown", T("multiplayer.help.character")), new("button_b", T("multiplayer.help.back"))],
                }, "", "", "", journal),
                "panel-seats" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    NousAvonsLaMain = true, Compte = "2/4",
                    Lignes =
                    [
                        deux[0] with { Avatar = planche, Place = 1 },
                        deux[1] with { Place = 2 },
                        new(T("multiplayer.seat.free"), "", "", false, false, false, null, T("multiplayer.seat.waiting"), Place: 3, Libre: true),
                        new(T("multiplayer.seat.free"), "", "", false, false, false, null, T("multiplayer.seat.waiting"), Place: 4, Libre: true),
                    ],
                    Aides = aides, LigneCourante = -1, Disposition = LeaderboardOverlayService.DispositionDesJoueurs.Grille,
                }, "", "", "", journal),
                "np-presentation" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with { Badge = string.Empty },
                    T("multiplayer.syncing"), "Street Fighter II' Champion Edition", "P1 KEN  ·  P2 CHUN-LI", journal),
                "np-result" => LeaderboardOverlayService.ApercuMultiplayer(style, socle with
                {
                    Onglets = [T("multiplayer.result.victory")], OngletCourant = 0, Compte = string.Empty, Badge = string.Empty,
                    Info = T("multiplayer.result.playtime") + " 1:01", DecompteFinUtc = DateTime.UtcNow.AddSeconds(12),
                    Lignes =
                    [
                        Joueur("PLAYER 1", "FR", "A98BFF", false, true, true, 3, "") with
                        {
                            Personnage = "KEN", Avatar = planche, Portrait = Portrait(307540), Place = 1, Issue = T("multiplayer.result.victory"), Gagnant = true,
                            Cote = "ELO 1516", Evolution = "+16", Provisoire = T("multiplayer.rating.provisional"), Stats = Stats(12, 2, 1, 288),
                        },
                        Joueur("PLAYER 2", "JP", "FF9F6B", true, false, true, 4, "") with
                        {
                            Personnage = "CHUN-LI", Portrait = Portrait(307541), Place = 2, Issue = T("multiplayer.result.defeat"),
                            Cote = "ELO 1484", Evolution = "-16", Provisoire = T("multiplayer.rating.provisional"), Stats = Stats(7, 0, 0, 90),
                        },
                    ],
                    LigneCourante = -1, Bandeau = string.Empty, Disposition = LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
                    Aides = [new("button_a", T("multiplayer.help.replay")), new("button_b", T("multiplayer.help.close"))],
                    PartageDesDegats = new LeaderboardOverlayService.Partage(288 / 378d, "A98BFF", "FF9F6B", T("multiplayer.result.damage")),
                }, "", "", "", journal),
                "cards" => MultiplayerToastOverlay.Apercu(style,
                [
                    new(T("multiplayer.invitation.title"), textes.Format("multiplayer.invitation", langue, ("player", "PLAYER 2"), ("game", "Metal Slug 3")), T("multiplayer.invitation.help"), DateTime.UtcNow.AddSeconds(17), Alerte: true),
                    new(T("multiplayer.selection"), string.Empty, string.Empty, Lignes:
                    [
                        new("Street Fighter II' Champion Edition", "1/2   " + T("multiplayer.selection.help")),
                        new("Street Fighter III: 3rd Strike", "1/2   " + T("multiplayer.selection.help")),
                        new("Bomber Man World", "3/4   " + T("multiplayer.selection.help"), DateTime.UtcNow.AddSeconds(4)),
                    ]),
                ], new Rectangle(0, 0, 1920, 1200)),
                _ => [],
            };
            return png.Length == 0
                ? NotFound(new { vues = new[] { "panel", "panel-focus", "panel-seats", "invite", "presentation", "result", "cards", "np-panel", "np-panel-full", "np-presentation", "np-result" } })
                : File(png, "image/png");
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(500, new { erreur = ex.InnerException?.Message ?? ex.Message });
        }
    }
}
