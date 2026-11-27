using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Adventure;

/// <summary>Le choix de l'aventure d'un jeu, pur : c'est ce qui se teste.</summary>
public static class AdventureChoix
{
    /// <summary>
    /// L'aventure que l'appui long reprend parmi les miennes : celle de CE jeu et de CE contenu (une aventure ne se rejoue que sur
    /// le meme contenu), de preference celle dont une seance est deja ouverte (on la rejoint), sinon la derniere jouee (le site rend
    /// les aventures par derniere activite). Aucune : null, l'appui long en cree une.
    /// </summary>
    public static AdventureInfo? Choisir(IEnumerable<AdventureInfo> miennes, string jeu, string contenu, Func<string, string, bool>? possede = null)
        => miennes.Where(a => string.Equals(a.Game, jeu, StringComparison.Ordinal)
                              && (a.Content.Length == 0 || string.Equals(a.Content, contenu, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.Room is { Length: > 0 })
            .FirstOrDefault()
        // 1.9.223 : la collection d'ES ne montre que les versions de la langue de la borne. Une aventure de ce jeu creee sur une AUTRE version
        // que la borne possede (cachee par le filtre de langue) se reprend quand meme, plutot que d'en creer une neuve sur la version montree.
        ?? (possede is null ? null : miennes.Where(a => string.Equals(a.Game, jeu, StringComparison.Ordinal) && a.Content.Length > 0 && possede(jeu, a.Content))
            .OrderByDescending(a => a.Room is { Length: > 0 })
            .FirstOrDefault());

    /// <summary>Le nombre de joueurs a demander : celui de la derniere version, sinon toutes les places ; de 2 a ses places.</summary>
    public static int Joueurs(AdventureInfo aventure, int placesDuJeu)
    {
        var places = Math.Max(2, aventure.Seats > 0 ? aventure.Seats : placesDuJeu);
        return Math.Clamp(aventure.Latest?.Players is { } n and >= 2 ? n : places, 2, places);
    }
}

/// <summary>Une aventure de ce jeu, pour l'onglet NEW ADVENTURE : sa version, ses joueurs, la date de sa derniere version, et si une seance est ouverte.</summary>
public sealed record AventureLigne(string Id, string Version, int Joueurs, DateTime? Date, bool EnCours, string Titre = "", IReadOnlyList<string>? Pseudos = null);

/// <summary>Ce que montre l'onglet NEW ADVENTURE : la version et les places de la prochaine aventure, et les aventures qui existent (null : site injoignable).</summary>
public sealed record AventuresDuJeu(string Version, int Places, IReadOnlyList<AventureLigne>? Lignes);

/// <summary>
/// L'APPUI LONG SUR UN JEU DE LA COLLECTION NELFEPLAY ADVENTURE : reprendre l'aventure de ce jeu (celle dont une seance est ouverte,
/// sinon la derniere jouee), ou en commencer une, puis ouvrir la seance chez le site. Le site place le joueur dans la gameroom
/// MULTIPLAYER de la seance (de sorte « friends », l'ouvreur y est) et invite les autres participants : le panneau MULTIPLAYER
/// prend alors le relais comme pour une salle d'amis (FOR FRIENDS : attente, invitations, nombre de joueurs attendu).
/// Choisir une autre aventure ou en creer une de plus : routes locales <c>/api/v1/adventure/*</c>.
/// </summary>
public sealed class AdventureLancement
{
    private readonly IAdventureSite _site;
    private readonly Func<string?, string?> _empreinteDuCoeur;
    private readonly ILogger<AdventureLancement>? _journal;
    private readonly AdventureCatalogueStore? _catalogue;

    /// <summary>Cette borne possede-t-elle ce jeu sur ce contenu (toutes versions, montrees ou non par la collection) ?</summary>
    public Func<string, string, bool>? PossedeLeContenu { get; set; }

    public AdventureLancement(IAdventureSite site, Func<string?, string?>? empreinteDuCoeur = null, ILogger<AdventureLancement>? journal = null,
        AdventureCatalogueStore? catalogue = null)
    {
        _catalogue = catalogue;
        _site = site;
        _empreinteDuCoeur = empreinteDuCoeur ?? (coeur => EtatsParCoeur.EmpreinteDuCoeurInstalle(coeur));
        _journal = journal;
    }

    /// <summary>Rend la raison d'un refus (celle du site, ou <c>offline</c>), ou null quand le joueur est dans la gameroom de la seance.</summary>
    public async Task<string?> RejoindreAsync(MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        var miennes = await _site.MesAventuresAsync(ct).ConfigureAwait(false);
        if (miennes is null)
        {
            return "offline";
        }

        var aventure = AdventureChoix.Choisir(miennes.Aventures, jeu.Game.Id, jeu.ContentKey, PossedeLeContenu);
        if (aventure is null)
        {
            (aventure, var refus) = await CreerAsync(jeu, ct).ConfigureAwait(false);
            if (aventure is null)
            {
                return refus ?? "unknown_game";
            }
        }

        return await OuvrirAsync(aventure, jeu, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// L'ONGLET NEW ADVENTURE (27/11) : ce que A maintenu va faire (une aventure de plus, de CETTE version, a toutes les places du jeu) et les aventures
    /// de CE jeu qui existent deja, les plus recentes d'abord. Rend <c>Lignes</c> null quand le site ne repond pas.
    /// </summary>
    public async Task<AventuresDuJeu> AventuresDuJeuAsync(MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        var spec = _catalogue?.Entrees(out _).FirstOrDefault(e => e.Jeu.Id == jeu.Game.Id)?.Spec;
        string Version(string? contenu) => spec?.VersionDuContenu(contenu) is { } v ? v.Label ?? v.Key : string.Empty;
        var miennes = await _site.MesAventuresAsync(ct).ConfigureAwait(false);
        var lignes = miennes is null ? null : LesLignes(miennes.Aventures, jeu.Game.Id, Version, jeu.ContentKey);
        return new AventuresDuJeu(Version(jeu.ContentKey), Math.Max(2, jeu.Game.Players), lignes);
    }

    /// <summary>
    /// Pur : les aventures ACTIVES de ce jeu (un brouillon n'est jamais liste : il n'a pas encore ete lance avec les autres), la derniere partie
    /// d'abord, avec leur nom, leur date et les pseudos des joueurs.
    /// </summary>
    internal static List<AventureLigne> LesLignes(IEnumerable<AdventureInfo> aventures, string jeu, Func<string?, string> version, string contenuDuJeu)
        => aventures.Where(a => string.Equals(a.Game, jeu, StringComparison.Ordinal) && !a.EstUnBrouillon)
            .OrderByDescending(a => DateDe(a) ?? DateTime.MinValue)
            .Select(a => new AventureLigne(a.Id, version(a.Content.Length > 0 ? a.Content : contenuDuJeu),
                a.Latest?.Players is { } n and > 0 ? n : Math.Max(2, a.Seats), DateDe(a), a.Room is { Length: > 0 },
                a.Name ?? string.Empty, a.Pseudos.Count > 0 ? a.Pseudos : a.Participants))
            .ToList();

    private static DateTime? DateDe(AdventureInfo a)
        => a.LastPlayedUtc is { } d && d > DateTime.MinValue ? d : a.Latest?.CreatedUtc is { } c && c > DateTime.MinValue ? c : null;

    /// <summary>
    /// CHOISIR UNE AVENTURE DE LA LISTE (27/11) : elle devient l'aventure courante, sa seance s'ouvre (ou se rejoint) et sa gameroom est celle de
    /// l'onglet ADVENTURE. Rend la raison d'un refus, ou null.
    /// </summary>
    public async Task<string?> ChoisirAsync(MultiplayerInstalledGame jeu, string aventure, CancellationToken ct)
    {
        var miennes = await _site.MesAventuresAsync(ct).ConfigureAwait(false);
        if (miennes is null)
        {
            return "offline";
        }

        var choisie = miennes.Aventures.FirstOrDefault(a => string.Equals(a.Id, aventure, StringComparison.Ordinal) && string.Equals(a.Game, jeu.Game.Id, StringComparison.Ordinal));
        return choisie is null ? "not_found" : await OuvrirAsync(choisie, jeu, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// NOUVELLE AVENTURE (panneau, 26/11) : une aventure de plus pour ce jeu et CETTE version, meme quand une autre existe : sinon le
    /// joueur ne pourrait jamais en recommencer une. Rend la raison d'un refus, ou null.
    /// </summary>
    public async Task<string?> NouvelleAsync(MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        var (aventure, refus) = await CreerAsync(jeu, ct).ConfigureAwait(false);
        return aventure is null ? refus ?? "unknown_game" : await OuvrirAsync(aventure, jeu, ct).ConfigureAwait(false);
    }

    private async Task<(AdventureInfo? Aventure, string? Raison)> CreerAsync(MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        var coeur = jeu.Core?.Core;
        var version = _catalogue?.Entrees(out _).FirstOrDefault(e => e.Jeu.Id == jeu.Game.Id)?.Spec.VersionDuContenu(jeu.ContentKey)?.Key;
        var (aventure, raison) = await _site.CreerAvecRaisonAsync(jeu.Game.Id, jeu.ContentKey, Math.Max(2, jeu.Game.Players), AdventureModes.Exclusive, null, coeur,
            _empreinteDuCoeur(coeur), ct, version).ConfigureAwait(false);
        if (aventure is not null)
        {
            // Le site cree un BROUILLON (un seul par compte et par jeu) : il ne devient une aventure qu'au premier lancement a deux joueurs.
            _journal?.LogInformation("ADVENTURE : brouillon {Aventure} pour {Jeu} (version {Version}, etat {Etat})", aventure.Id, jeu.Game.Id, version ?? "unique", aventure.State ?? "-");
        }
        else
        {
            _journal?.LogInformation("ADVENTURE : creation refusee pour {Jeu} ({Raison})", jeu.Game.Id, raison ?? "-");
        }

        return (aventure, aventure is null ? raison ?? "unknown_game" : null);
    }

    private async Task<string?> OuvrirAsync(AdventureInfo aventure, MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        var seance = await _site.OuvrirSeanceAsync(aventure.Id, AdventureChoix.Joueurs(aventure, jeu.Game.Players), ct).ConfigureAwait(false);
        if (seance is null)
        {
            return "offline";
        }

        if (!seance.Ok)
        {
            _journal?.LogInformation("ADVENTURE : seance de {Aventure} refusee ({Raison})", aventure.Id, seance.Reason);
            return seance.Reason ?? "other";
        }

        _journal?.LogInformation("ADVENTURE : seance de {Aventure} {Etat}, gameroom {Room}", aventure.Id, seance.Attached ? "rejointe" : "ouverte", seance.Room);
        return null;
    }
}
