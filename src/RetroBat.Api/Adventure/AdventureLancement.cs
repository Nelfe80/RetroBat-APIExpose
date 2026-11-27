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
/// <remarks>
/// 28/11 : <c>Pseudos</c> = les joueurs PAR PSEUDO d'affichage (jamais une poignee quand le pseudo est connu), <c>Createur</c> = le pseudo du
/// createur. <c>Jouable</c> faux = cette borne n'a pas le contenu exact (la version) de l'aventure : la ligne se grise et <c>Raison</c> dit
/// pourquoi (cle de refus <c>content_missing</c>).
/// </remarks>
public sealed record AventureLigne(string Id, string Version, int Joueurs, DateTime? Date, bool EnCours, string Titre = "", IReadOnlyList<string>? Pseudos = null,
    string Createur = "", bool Jouable = true, string? Raison = null);

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
        var aventures = miennes is null ? null : await EnrichirAsync(miennes.Aventures.Where(a => string.Equals(a.Game, jeu.Game.Id, StringComparison.Ordinal) && !a.EstUnBrouillon).ToList(), ct).ConfigureAwait(false);
        var lignes = aventures is null ? null : LesLignes(aventures, jeu.Game.Id, Version, jeu.ContentKey, PossedeLeContenu);
        return new AventuresDuJeu(Version(jeu.ContentKey), Math.Max(2, jeu.Game.Players), lignes);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Pris, AdventureInfo Detail)> _details = new(StringComparer.Ordinal);

    /// <summary>Combien de temps le detail d'une aventure se garde : la liste se relit a chaque ouverture du panneau, pas un appel par aventure a chaque fois.</summary>
    internal static readonly TimeSpan GardeDuDetail = TimeSpan.FromSeconds(90);

    /// <summary>
    /// LES LISTES DU SITE NE DISENT NI LE CONTENU NI LES PSEUDOS (28/11) : <c>mine</c> rend des poignees (parfois un code hexadecimal) et pas la cle de
    /// contenu (seul le detail la rend). Pour chaque aventure du jeu a qui il manque l'un ou l'autre, la borne lit le detail (membres : pseudos et
    /// role, cle de contenu) : une aventure de contenu inconnu montrait avant la version de la borne qui la lit (« USA » a un joueur qui avait
    /// cree une aventure europeenne). Quand le site rendra <c>content</c>, <c>creator_pseudo</c> et <c>participant_pseudos</c> dans les listes, ces
    /// appels cessent d'eux-memes.
    /// </summary>
    internal async Task<List<AdventureInfo>> EnrichirAsync(IReadOnlyList<AdventureInfo> liste, CancellationToken ct)
    {
        var resultat = await Task.WhenAll(liste.Select(async a =>
        {
            if (a.Content.Length > 0 && a.NomsDesJoueurs.Count > 0 && a.CreatorPseudo is { Length: > 0 })
            {
                return a;
            }

            var cle = a.Id + "|" + (a.LastPlayedUtc?.Ticks ?? 0);
            if (_details.TryGetValue(cle, out var garde) && DateTime.UtcNow - garde.Pris < GardeDuDetail)
            {
                return Completer(a, garde.Detail);
            }

            var detail = await _site.DetailAsync(a.Id, ct).ConfigureAwait(false);
            if (detail is null)
            {
                return a;
            }

            _details[cle] = (DateTime.UtcNow, detail);
            return Completer(a, detail);
        })).ConfigureAwait(false);
        return resultat.ToList();
    }

    /// <summary>Pur : l'aventure de la liste, completee de ce que le detail sait en plus (contenu, pseudos). Ce que la liste dit deja ne change pas.</summary>
    internal static AdventureInfo Completer(AdventureInfo liste, AdventureInfo detail)
        => liste with
        {
            Content = liste.Content.Length > 0 ? liste.Content : detail.Content,
            CreatorPseudo = liste.CreatorPseudo is { Length: > 0 } ? liste.CreatorPseudo : detail.CreatorPseudo,
            NomsDesJoueurs = liste.NomsDesJoueurs.Count > 0 ? liste.NomsDesJoueurs : detail.NomsDesJoueurs,
        };

    /// <summary>
    /// Pur : les aventures ACTIVES de ce jeu (un brouillon n'est jamais liste : il n'a pas encore ete lance avec les autres), la derniere partie
    /// d'abord, avec leur nom, leur date, leur createur et les pseudos des joueurs. Une aventure dont la borne n'a pas le contenu exact n'est pas
    /// <c>Jouable</c> (<paramref name="possede"/> : jeu, contenu).
    /// </summary>
    internal static List<AventureLigne> LesLignes(IEnumerable<AdventureInfo> aventures, string jeu, Func<string?, string> version, string contenuDuJeu,
        Func<string, string, bool>? possede = null)
        => aventures.Where(a => string.Equals(a.Game, jeu, StringComparison.Ordinal) && !a.EstUnBrouillon)
            .OrderByDescending(a => DateDe(a) ?? DateTime.MinValue)
            .Select(a =>
            {
                var jouable = a.Content.Length == 0 || possede is null || possede(jeu, a.Content);
                return new AventureLigne(a.Id, version(a.Content.Length > 0 ? a.Content : contenuDuJeu),
                    a.Latest?.Players is { } n and > 0 ? n : Math.Max(2, a.Seats), DateDe(a), a.Room is { Length: > 0 },
                    a.Name ?? string.Empty, a.NomsDesJoueurs.Count > 0 ? a.NomsDesJoueurs : a.Pseudos.Count > 0 ? a.Pseudos : a.Participants,
                    a.CreatorPseudo ?? string.Empty, jouable, jouable ? null : AdventureRefus.ContenuAbsent);
            })
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
        if (choisie is null)
        {
            return "not_found";
        }

        // Le contenu exact (la version) manque a cette borne : la seance se desynchroniserait, on n'ouvre rien (la ligne est grisee dans la liste).
        if ((await EnrichirAsync([choisie], ct).ConfigureAwait(false))[0] is { Content.Length: > 0 } complete && PossedeLeContenu is { } possede && !possede(jeu.Game.Id, complete.Content))
        {
            _journal?.LogInformation("ADVENTURE : aventure {Aventure} refusee, le contenu {Contenu} n'est pas sur cette borne", choisie.Id, complete.Content);
            return AdventureRefus.ContenuAbsent;
        }

        return await OuvrirAsync(choisie, jeu, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// UN AMI INVITE DANS UNE SALLE D'AVENTURE DOIT AUSSI ETRE INVITE A L'AVENTURE (28/11) : l'invitation de gameroom (MULTIPLAYER) assoit l'ami dans la
    /// salle mais ne le fait jamais participant ; le site refuse alors <c>activate</c> (« not_member ») a l'hote qui n'est pas le createur, et l'aventure
    /// restait un brouillon pour son createur. Rend la raison du site, ou null (« deja participant » est un succes).
    /// </summary>
    public async Task<string?> InviterALAventureAsync(string aventure, string poignee, CancellationToken ct)
    {
        var (ok, raison) = await _site.InviterAsync(aventure, poignee, ct).ConfigureAwait(false);
        var succes = ok || raison is "already_member";
        _journal?.LogInformation("ADVENTURE : {Poignee} invite a l'aventure {Aventure} ({Statut})", poignee, aventure, succes ? "ok" : raison ?? "-");
        return succes ? null : raison ?? "other";
    }

    /// <summary>
    /// L'INVITE QUI ENTRE DANS LA SALLE D'UNE AVENTURE EN DEVIENT PARTICIPANT (28/11) : l'invitation du site vers cette aventure (celle que
    /// <see cref="InviterALAventureAsync"/> a posee) est acceptee. Rend vrai si la borne est participante apres l'appel.
    /// </summary>
    public async Task<bool> DevenirParticipantAsync(string aventure, CancellationToken ct)
    {
        var miennes = await _site.MesAventuresAsync(ct).ConfigureAwait(false);
        if (miennes is null)
        {
            return false;
        }

        if (miennes.Aventures.Any(a => string.Equals(a.Id, aventure, StringComparison.Ordinal)))
        {
            return true;
        }

        var invitation = miennes.Invitations.FirstOrDefault(i => string.Equals(i.Adventure, aventure, StringComparison.Ordinal));
        if (invitation is null)
        {
            _journal?.LogInformation("ADVENTURE : aucune invitation du site vers l'aventure {Aventure} : cette borne reste hors de ses participants", aventure);
            return false;
        }

        var ok = await _site.AccepterAsync(invitation.Invitation, true, ct).ConfigureAwait(false);
        _journal?.LogInformation("ADVENTURE : participant de l'aventure {Aventure} ({Statut})", aventure, ok ? "ok" : "refuse");
        return ok;
    }

    /// <summary>Les raisons de refus d'un reglage du nombre de joueurs (touche X) : la seance et la salle, par le site.</summary>
    public Task<(bool Ok, string? Raison)> ReglerLaSeanceAsync(string aventure, int joueurs, CancellationToken ct)
        => _site.ReglerLaSeanceAsync(aventure, joueurs, ct);

    /// <summary>
    /// NOUVELLE AVENTURE (panneau, 26/11) : une aventure de plus pour ce jeu et CETTE version, meme quand une autre existe : sinon le
    /// joueur ne pourrait jamais en recommencer une. Rend la raison d'un refus, ou null.
    /// </summary>
    public async Task<string?> NouvelleAsync(MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        var (aventure, refus) = await CreerAsync(jeu, ct).ConfigureAwait(false);
        if (aventure is null)
        {
            return refus ?? "unknown_game";
        }

        // Le site REUTILISE le brouillon du compte pour ce jeu (ses places sont remises a jour) mais son ancienne seance peut rester ouverte :
        // l'ouvrir attachait a l'ancienne salle (ancien nombre de joueurs, ouvreur parfois l'autre). Une nouvelle aventure ferme l'ancienne
        // seance puis en ouvre une neuve, au nombre de places du jeu.
        var voulus = Math.Max(2, jeu.Game.Players);
        if (aventure.Room is { Length: > 0 })
        {
            var fermee = await _site.FermerSeanceAsync(aventure.Id, "abandon", null, ct).ConfigureAwait(false);
            _journal?.LogInformation("ADVENTURE : ancienne seance du brouillon {Aventure} fermee ({Statut})", aventure.Id, fermee ? "ok" : "refusee");
        }

        var refusOuverture = await OuvrirAsync(aventure, jeu, ct, voulus).ConfigureAwait(false);
        if (refusOuverture is null && aventure.Room is { Length: > 0 })
        {
            // Si l'ancienne salle a du etre gardee (fermeture refusee : l'ouvreur est l'autre joueur), le nombre voulu est redemande (ouvreur seulement).
            await _site.ReglerLaSeanceAsync(aventure.Id, voulus, ct).ConfigureAwait(false);
        }

        return refusOuverture;
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

    private async Task<string?> OuvrirAsync(AdventureInfo aventure, MultiplayerInstalledGame jeu, CancellationToken ct, int? joueurs = null)
    {
        var seance = await _site.OuvrirSeanceAsync(aventure.Id, joueurs ?? AdventureChoix.Joueurs(aventure, jeu.Game.Players), ct).ConfigureAwait(false);
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
