using System.Text.Json.Serialization;

namespace RetroBat.Api.Adventure;

/// <summary>
/// NELFEPLAY ADVENTURE (CDC v1.0, 26/11/2026) : le modele que l'API echange avec le site. Le SITE est proprietaire des routes
/// et des donnees (CONTRAT.md du dossier temp\adventure) ; ces types suivent ce contrat, et tout ecart se regle la-bas avant
/// d'etre code ici. Les noms JSON sont ceux du contrat.
/// </summary>
public static class AdventureModes
{
    /// <summary>Ses participants seulement.</summary>
    public const string Exclusive = "exclusive";

    /// <summary>Tout le monde, en roulement.</summary>
    public const string Open = "open";
}

/// <summary>Pourquoi une version a ete prise.</summary>
public static class AdventureRaisons
{
    /// <summary>Fin de seance : l'etat pris avant de fermer le jeu.</summary>
    public const string Fin = "end";

    /// <summary>Toutes les 5 minutes, hors chargement.</summary>
    public const string Reguliere = "regular";

    /// <summary>Le jeu vient d'ecrire sa sauvegarde : le point le plus sur a reprendre.</summary>
    public const string SauvegardeDuJeu = "game_save";
}

/// <summary>Une aventure : une partie a plusieurs qui se reprend. La sauvegarde est celle de l'aventure, jamais d'un joueur.</summary>
public sealed record AdventureInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Le jeu du catalogue Adventure (<c>adventure/gba/kirby-amazing-mirror</c>).</summary>
    [JsonPropertyName("game")]
    public string Game { get; init; } = string.Empty;

    /// <summary>La cle de contenu (md5 de la ROM) : une aventure ne se rejoue que sur le meme contenu.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Le coeur de l'aventure et la version (empreinte de sa dll) : un etat ne se recharge que dans le meme coeur.</summary>
    [JsonPropertyName("core")]
    public string? Core { get; init; }

    [JsonPropertyName("core_sha256")]
    public string? CoreSha256 { get; init; }

    /// <summary>Les places de l'aventure (1..N).</summary>
    [JsonPropertyName("seats")]
    public int Seats { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = AdventureModes.Exclusive;

    [JsonPropertyName("creator")]
    public string? Creator { get; init; }

    [JsonPropertyName("participants")]
    public IReadOnlyList<string> Participants { get; init; } = [];

    /// <summary>La gameroom MULTIPLAYER courante, quand une seance est ouverte.</summary>
    [JsonPropertyName("room")]
    public string? Room { get; init; }

    /// <summary>Le salon vocal DE L'AVENTURE (fourni par le site) : on reprend l'aventure, on retrouve le salon.</summary>
    [JsonPropertyName("lobby_id")]
    public string? LobbyId { get; init; }

    [JsonPropertyName("latest")]
    public AdventureVersion? Latest { get; init; }
}

public sealed record AdventurePlace
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    /// <summary>Le joueur qui tenait la place : sa poignee (celle de la gameroom), jamais un identifiant de compte ni un nom saisi.</summary>
    [JsonPropertyName("handle")]
    public string? Handle { get; init; }
}

/// <summary>
/// Une version : l'etat lie (tous les joueurs), la sauvegarde du jeu de la cartouche de l'aventure, une capture, qui tenait
/// chaque place. Les trois fichiers sont des objets par empreinte (comme les replays) : la version n'en porte que l'empreinte.
/// </summary>
public sealed record AdventureVersion
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("adventure")]
    public string Adventure { get; init; } = string.Empty;

    [JsonPropertyName("state_sha256")]
    public string? StateSha256 { get; init; }

    /// <summary>La sauvegarde du jeu de la cartouche de l'aventure : sert a repartir avec un autre nombre de joueurs.</summary>
    [JsonPropertyName("save_sha256")]
    public string? SaveSha256 { get; init; }

    [JsonPropertyName("shot_sha256")]
    public string? CaptureSha256 { get; init; }

    /// <summary>Le rang de la version dans l'aventure (1, 2, 3...), pose par le site.</summary>
    [JsonPropertyName("seq")]
    public int Seq { get; init; }

    /// <summary>La version dont la seance est partie : le site la demande a chaque depot (CONTRAT.md §5).</summary>
    [JsonPropertyName("base_seq")]
    public int? BaseSeq { get; init; }

    /// <summary>Le nombre de joueurs de l'etat : il ne se recharge qu'avec autant de consoles.</summary>
    [JsonPropertyName("players")]
    public int Players { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<AdventurePlace> Seats { get; init; } = [];

    /// <summary>La duree jouee cumulee de l'aventure, en secondes.</summary>
    [JsonPropertyName("duration_s")]
    public long DurationS { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = AdventureRaisons.Reguliere;

    /// <summary>L'empreinte de la dll du coeur qui a pris l'etat.</summary>
    [JsonPropertyName("core_sha256")]
    public string? CoreSha256 { get; init; }

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; init; }

    /// <summary>Les empreintes des objets que cette version demande au site : etat, sauvegarde, capture.</summary>
    public IEnumerable<string> Objets() => ObjetsParGenre().Select(o => o.Sha);

    /// <summary>Chaque objet avec son genre (<see cref="AdventureObjets"/>) : le site borne leur taille selon lui.</summary>
    public IEnumerable<(string Sha, string Genre)> ObjetsParGenre()
    {
        if (!string.IsNullOrEmpty(StateSha256)) yield return (StateSha256!, AdventureObjets.Etat);
        if (!string.IsNullOrEmpty(SaveSha256)) yield return (SaveSha256!, AdventureObjets.Sauvegarde);
        if (!string.IsNullOrEmpty(CaptureSha256)) yield return (CaptureSha256!, AdventureObjets.Capture);
    }
}

/// <summary>
/// Ce que le site rend a l'ouverture d'une seance (CONTRAT.md §8) : la gameroom MULTIPLAYER (ou l'ouvreur est place), la derniere
/// version avec ses objets, le salon vocal de l'aventure.
/// </summary>
public sealed record AdventureSeance
{
    public bool Ok { get; init; }

    public string? Reason { get; init; }

    /// <summary>La seance etait deja ouverte : l'appelant la rejoint.</summary>
    public bool Attached { get; init; }

    public string? Session { get; init; }

    public string? Room { get; init; }

    /// <summary><c>state</c>, <c>fresh_save</c> ou <c>fresh</c>, pour le nombre de joueurs demande.</summary>
    public string? Resume { get; init; }

    public int? BaseSeq { get; init; }

    public AdventureVersion? Latest { get; init; }

    public bool VoiceSupported { get; init; }

    public string? LobbyId { get; init; }
}

/// <summary>La derniere version, avec ce que le site conseille pour ce nombre de joueurs (CONTRAT.md §6).</summary>
public sealed record AdventureDerniere(AdventureVersion? Version, string Resume);

/// <summary>Mes aventures et les invitations en attente (CONTRAT.md §2).</summary>
public sealed record AdventureMesAventures(IReadOnlyList<AdventureInfo> Aventures, IReadOnlyList<AdventureInvitation> Invitations);

public sealed record AdventureInvitation(string Invitation, string Adventure, string? Game, string? Title, string? From);

/// <summary>Le sort d'un depot de version : parti, a refaire plus tard, ou refuse pour toujours (la borne n'insiste pas).</summary>
public sealed record AdventureDepot(bool Ok, bool Definitif, string? Raison)
{
    public static AdventureDepot Parti { get; } = new(true, false, null);

    public static AdventureDepot APlusTard(string? raison = null) => new(false, false, raison);

    /// <summary>
    /// Refus qui ne change pas en reessayant : la version ne part plus (elle reste dans la copie locale). <c>core_mismatch</c> :
    /// le coeur de la borne n'est pas celui de l'aventure ; <c>not_host</c> : ce compte n'etait pas l'hote (ou plus depuis 15 min).
    /// </summary>
    public static bool RaisonDefinitive(string? raison) => raison is "core_mismatch" or "not_host" or "not_member" or "not_found" or "bad_request" or "quota";
}
