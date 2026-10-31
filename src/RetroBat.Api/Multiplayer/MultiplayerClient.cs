using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Les gamerooms MULTIPLAYER, demandees a la plateforme EN TANT QUE MACHINE (lot M2 du CDC) :
/// <c>/api/v1/agent/multiplayer/…</c>, avec l'identifiant de la borne. Le lien machine vers compte
/// dit au nom de qui elle parle ; le jeu, son nombre de joueurs et son niveau, la plateforme les lit
/// dans le meme catalogue du Data Pack.
///
/// Les mots de passe netplay qui arrivent ici ne sont jamais journalises.
/// </summary>
public sealed class MultiplayerClient
{
    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _fabrique;
    private readonly NelfePlayDeviceStore _machine;
    private readonly ILogger<MultiplayerClient>? _journal;
    private readonly Func<HttpClient?>? _clientDeTest;

    public MultiplayerClient(
        IHttpClientFactory fabrique,
        NelfePlayDeviceStore machine,
        ILogger<MultiplayerClient>? journal = null,
        Func<HttpClient?>? clientDeTest = null)
    {
        _fabrique = fabrique;
        _machine = machine;
        _journal = journal;
        _clientDeTest = clientDeTest;
    }

    /// <summary>
    /// Une gameroom pour ce jeu et ce contenu. <paramref name="sorte"/> (28/10) : null pour l'appui long (sa gameroom de ce
    /// jeu, sinon celle d'un ami qui l'attend en FOR FRIENDS, sinon une publique), <see cref="MultiplayerSortes.Publique"/>
    /// en passant sur FOR PLAYERS…, <see cref="MultiplayerSortes.Amis"/> en passant sur FOR FRIENDS.
    /// </summary>
    public Task<MultiplayerReponse?> RejoindreAsync(string jeu, string contenu, int? rttMs, bool panneauOuvert, CancellationToken ct,
        IReadOnlyDictionary<string, int>? choix = null, string? sorte = null)
        => sorte is null
            ? EnvoyerAsync("join", new { game = jeu, content = contenu, rtt_ms = rttMs, panel = panneauOuvert, choices = choix }, ct)
            : EnvoyerAsync("join", new { game = jeu, content = contenu, rtt_ms = rttMs, panel = panneauOuvert, choices = choix, kind = sorte }, ct);

    /// <summary>FOR FRIENDS, touche X (28/10) : l'ouvreur regle le nombre de joueurs qu'il attend.</summary>
    public Task<MultiplayerReponse?> ReglerAsync(string room, int joueurs, CancellationToken ct)
        => EnvoyerAsync("size", new { room, players = joueurs }, ct);

    /// <summary>NIVEAU NELFEPLAY : ▲▼ dans le panneau, le joueur change de personnage (permis jusqu'au lancement).</summary>
    public Task<MultiplayerReponse?> ChoisirAsync(string room, IReadOnlyDictionary<string, int> choix, CancellationToken ct)
        => EnvoyerAsync("choice", new { room, choices = choix }, ct);

    /// <summary>B maintenu : quitter cette gameroom.</summary>
    /// <param name="raison"><c>host_gone</c> : le jeu de cet invite s'est ferme parce que l'hote est parti, jamais un abandon (26/10).</param>
    public Task<MultiplayerReponse?> QuitterAsync(string room, CancellationToken ct, string? raison = null)
        => raison is null ? EnvoyerAsync("leave", new { room }, ct) : EnvoyerAsync("leave", new { room, reason = raison }, ct);

    /// <summary>Le sondage : presence, decompte, lancement, resultat, invitations.</summary>
    public Task<MultiplayerReponse?> SonderAsync(bool appui, bool panneauOuvert, int? rttMs, CancellationToken ct, bool dansLaCollection = false,
        bool? enPartie = null)
        => EnvoyerAsync("poll", new { input = appui, panel = panneauOuvert, rtt_ms = rttMs, collection = dansLaCollection, playing = enPartie }, ct);

    /// <summary>L'hote publie sa session netplay.</summary>
    public Task<MultiplayerReponse?> PublierNetplayAsync(string room, MultiplayerSessionNetplay session, CancellationToken ct)
        => EnvoyerAsync("netplay", new
        {
            room,
            relay = session.Relay,
            port = session.Port,
            session = session.Session,
            spectate_password = session.SpectatePassword,
            player_password = session.PlayerPassword,
            core = session.Core,
            core_version = session.CoreVersion,
            crc = session.Crc,
        }, ct);

    /// <summary>L'hote annonce la fin de la partie.</summary>
    public Task<MultiplayerReponse?> TerminerAsync(string room, string raison, int? dureeSecondes, CancellationToken ct, int? placeGagnante = null,
        IReadOnlyDictionary<int, StatsDeLaPlace>? stats = null, object? mesures = null, IReadOnlyList<MultiplayerRang>? rangs = null)
        => EnvoyerAsync("ended", new
        {
            room, reason = raison, duration_s = dureeSecondes, winner_seat = placeGagnante,
            stats = stats?.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                p => new { hits = p.Value.Coups, kos = p.Value.KO, perfects = p.Value.Perfects, damage = p.Value.Degats }),
            // Le format generique du lot 2 (profil, puis valeur et etat de chaque statistique) ; un site plus ancien l'ignore.
            metrics = mesures,
            // Le rang de chaque humain d'une fin a plusieurs (CDC Classements v1.0 §6) ; un site plus ancien l'ignore.
            ranks = Rangs(rangs),
        }, ct);

    /// <summary>NIVEAU NELFEPLAY : un invite rapporte la place gagnante qu'il a vue ; le resultat est valide s'il concorde avec l'hote.</summary>
    public Task<MultiplayerReponse?> RapporterAsync(string room, int placeGagnante, CancellationToken ct, object? mesures = null,
        IReadOnlyList<MultiplayerRang>? rangs = null)
        => EnvoyerAsync("report", new { room, winner_seat = placeGagnante, metrics = mesures, ranks = Rangs(rangs) }, ct);

    /// <summary>Les rangs tels que le site les lit, ou null sans classement a plusieurs (un duel lu par end ou match_end).</summary>
    private static object? Rangs(IReadOnlyList<MultiplayerRang>? rangs)
        => rangs is { Count: > 0 } ? rangs.Select(r => new { seat = r.Seat, rank = r.Rank, camp = r.Camp }).ToList() : null;

    /// <summary>Un invite annule la rencontre : son bilan du wrapper montre une triche (22/10). La cote ne bouge pas.</summary>
    public Task<MultiplayerReponse?> AnnulerAsync(string room, string motif, CancellationToken ct)
        => EnvoyerAsync("report", new { room, voided = true, reason = motif }, ct);

    public Task<MultiplayerReponse?> InviterAsync(string room, string poignee, CancellationToken ct)
        => EnvoyerAsync("invite", new { room, handle = poignee }, ct);

    public Task<MultiplayerReponse?> RepondreAsync(string invitation, bool accepte, int? rttMs, CancellationToken ct)
        => EnvoyerAsync("invitation", new { invitation, accept = accepte, rtt_ms = rttMs }, ct);

    /// <summary>Les amis en ligne (INVITE FRIENDS).</summary>
    public async Task<IReadOnlyList<MultiplayerAmi>> AmisAsync(CancellationToken ct)
    {
        var client = Client();
        if (client is null)
        {
            return [];
        }

        try
        {
            using var reponse = await client.GetAsync("/api/v1/agent/multiplayer/friends", ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                return [];
            }

            var corps = await reponse.Content.ReadFromJsonAsync<MultiplayerReponse>(Lecture, ct).ConfigureAwait(false);
            return corps?.Friends ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : amis injoignables");
            return [];
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Un appel, et sa reponse lue meme quand le statut est une erreur : la plateforme y met sa
    /// raison (<c>selection_full</c>, <c>content_refused</c>…). Null quand la plateforme est
    /// injoignable, ou que la borne n'est liee a aucun compte.
    /// </summary>
    private async Task<MultiplayerReponse?> EnvoyerAsync(string action, object corps, CancellationToken ct)
    {
        var client = Client();
        if (client is null)
        {
            return null;
        }

        try
        {
            using var reponse = await client.PostAsJsonAsync("/api/v1/agent/multiplayer/" + action, corps, ct).ConfigureAwait(false);
            var texte = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)reponse.StatusCode == 401)
            {
                return new MultiplayerReponse { Ok = false, Reason = "unauthorized" };
            }

            var lu = texte.Length == 0 ? null : JsonSerializer.Deserialize<MultiplayerReponse>(texte, Lecture);
            if (!reponse.IsSuccessStatusCode)
            {
                _journal?.LogInformation("MULTIPLAYER : {Action} refuse ({Statut}, {Raison})", action, (int)reponse.StatusCode, lu?.Reason ?? "-");
            }

            return lu ?? new MultiplayerReponse { Ok = false, Reason = "empty" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : {Action} injoignable", action);
            return null;
        }
        finally
        {
            client.Dispose();
        }
    }

    private HttpClient? Client()
    {
        if (_clientDeTest is not null)
        {
            return _clientDeTest();
        }

        var identifiant = _machine.GetCredential();
        if (string.IsNullOrEmpty(identifiant))
        {
            return null;
        }

        var client = _fabrique.CreateClient();
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(6);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", identifiant);
        return client;
    }
}

/// <summary>Ce que la plateforme repond a un appel MULTIPLAYER.</summary>
public sealed record MultiplayerReponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("room")]
    public string? Room { get; init; }

    /// <summary>La sorte de la gameroom rejointe (28/10) : <c>public</c> ou <c>friends</c> ; null d'un site plus ancien.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("rooms")]
    public IReadOnlyList<MultiplayerRoom> Rooms { get; init; } = [];

    [JsonPropertyName("invitations")]
    public IReadOnlyList<MultiplayerInvitation> Invitations { get; init; } = [];

    [JsonPropertyName("friends")]
    public IReadOnlyList<MultiplayerAmi> Friends { get; init; } = [];

    /// <summary>Les joueurs suivis qui attendent dans une gameroom, quand les deux bornes montrent la collection.</summary>
    [JsonPropertyName("proposals")]
    public IReadOnlyList<MultiplayerProposition> Proposals { get; init; } = [];

    /// <summary>Le compteur d'abandons du joueur et sa suspension, rendus par le sondage.</summary>
    [JsonPropertyName("conduct")]
    public MultiplayerConduite? Conduct { get; init; }

    /// <summary>Un refus « suspended » : le temps qui reste, en millisecondes.</summary>
    [JsonPropertyName("retry_in_ms")]
    public long? RetryInMs { get; init; }
}

/// <summary>
/// L'ABANDON : combien de rencontres le joueur a quittees avant leur fin (une rencontre jouee jusqu'au bout en efface
/// une), et ce qu'il reste de sa suspension (0 : libre). <c>Limit</c> : le nombre d'abandons qui suspend.
/// </summary>
public sealed record MultiplayerConduite
{
    [JsonPropertyName("abandons")]
    public int Abandons { get; init; }

    [JsonPropertyName("suspended_ms")]
    public long SuspendedMs { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 3;
}

/// <summary>Une gameroom, vue par ce joueur.</summary>
public sealed record MultiplayerRoom
{
    [JsonPropertyName("room")]
    public string Room { get; init; } = string.Empty;

    [JsonPropertyName("game")]
    public string Game { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; init; } = MultiplayerLevels.Classique;

    /// <summary>Le nombre de places : celui du jeu, ou, dans une gameroom d'amis, celui que son ouvreur attend.</summary>
    [JsonPropertyName("max")]
    public int Max { get; init; }

    /// <summary>LES GAMEROOMS D'AMIS (28/10) : <c>public</c> (FOR PLAYERS…) ou <c>friends</c> (FOR FRIENDS).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = MultiplayerSortes.Publique;

    /// <summary>Le nombre de joueurs du jeu, borne de la touche X d'une gameroom d'amis (0 d'un site plus ancien).</summary>
    [JsonPropertyName("game_max")]
    public int GameMax { get; init; }

    /// <summary>Ce joueur regle la gameroom d'amis (il l'a ouverte, ou elle lui est passee).</summary>
    [JsonPropertyName("owner")]
    public bool Owner { get; init; }

    public bool EntreAmis => Kind == MultiplayerSortes.Amis;

    /// <summary><c>waiting</c>, <c>countdown</c>, <c>launching</c>, <c>playing</c>, <c>finished</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("countdown_ms")]
    public int? CountdownMs { get; init; }

    [JsonPropertyName("players")]
    public IReadOnlyList<MultiplayerJoueur> Players { get; init; } = [];

    [JsonPropertyName("launch")]
    public MultiplayerLancement? Launch { get; init; }

    [JsonPropertyName("ended")]
    public MultiplayerFin? Ended { get; init; }
}

public sealed record MultiplayerJoueur
{
    [JsonPropertyName("handle")]
    public string Handle { get; init; } = string.Empty;

    [JsonPropertyName("pseudo")]
    public string Pseudo { get; init; } = string.Empty;

    /// <summary>Le pays, pour le drapeau sous le nom. Jamais la ville (V24-18).</summary>
    [JsonPropertyName("country")]
    public string Country { get; init; } = string.Empty;

    [JsonPropertyName("avatar_seed")]
    public string AvatarSeed { get; init; } = string.Empty;

    [JsonPropertyName("you")]
    public bool You { get; init; }

    [JsonPropertyName("host")]
    public bool Host { get; init; }

    [JsonPropertyName("present")]
    public bool Present { get; init; }

    [JsonPropertyName("seat")]
    public int? Seat { get; init; }

    [JsonPropertyName("rtt_ms")]
    public int? RttMs { get; init; }

    /// <summary>NIVEAU NELFEPLAY : les choix de ce joueur (<c>character</c> =&gt; valeur), permis par le catalogue.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyDictionary<string, int>? Choices { get; init; }

    /// <summary>Son avatar : sa planche NelfeNet (que la borne demande au relais si elle lui manque), comme dans un direct.</summary>
    [JsonPropertyName("avatar")]
    public MultiplayerAvatar? Avatar { get; init; }

    /// <summary>NIVEAU NELFEPLAY, versus 1 contre 1 : sa cote Elo sur ce jeu.</summary>
    [JsonPropertyName("rating")]
    public MultiplayerCote? Rating { get; init; }

    /// <summary>Une coop qui concourt (31/10/2026) : son meilleur score publie au 1CC MULTI et sa place. Null sans score.</summary>
    [JsonPropertyName("onecc_multi")]
    public MultiplayerUnCreditMulti? UnCreditMulti { get; init; }

    /// <summary>Il regle la gameroom d'amis (28/10).</summary>
    [JsonPropertyName("owner")]
    public bool Owner { get; init; }
}

/// <summary>Le meilleur 1CC MULTI d'un joueur sur le jeu de la gameroom, et sa place au classement (tous mondes).</summary>
public sealed record MultiplayerUnCreditMulti
{
    [JsonPropertyName("score")]
    public long Score { get; init; }

    [JsonPropertyName("rank")]
    public int Rank { get; init; }
}

public sealed record MultiplayerCote
{
    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("matches")]
    public int Matches { get; init; }

    /// <summary>Moins de 10 rencontres (conventions de Fightcade, 23/10).</summary>
    [JsonPropertyName("provisional")]
    public bool Provisional { get; init; }

    /// <summary>La lettre de Fightcade (E a S), « ? » tant que la cote est provisoire.</summary>
    [JsonPropertyName("rank")]
    public string? Rank { get; init; }

    /// <summary>Ses victoires sur ce jeu (26/10) ; null si la plateforme ne les envoie pas.</summary>
    [JsonPropertyName("wins")]
    public int? Wins { get; init; }

    /// <summary>Sa serie de victoires en cours sur ce jeu (26/10).</summary>
    [JsonPropertyName("streak")]
    public int? Streak { get; init; }

    /// <summary>La cote du type de ce jeu (combat, puzzle...).</summary>
    [JsonPropertyName("type")]
    public MultiplayerCote? Type { get; init; }

    /// <summary>L'ELOF : la moyenne des cotes par type, ponderee par les rencontres.</summary>
    [JsonPropertyName("elof")]
    public MultiplayerCote? Elof { get; init; }

    /// <summary>La lettre a montrer : celle de la plateforme, sinon calculee (une plateforme plus ancienne n'en envoie pas).</summary>
    public string Lettre => Provisional ? "?" : Rank is { Length: > 0 } r ? r : LettreDe(Value);

    /// <summary>Les paliers de Fightcade : E jusqu'a 699, D de 700, C de 1000, B de 1300, A de 1600, S de 1900.</summary>
    public static string LettreDe(double cote) => cote switch
    {
        >= 1900 => "S",
        >= 1600 => "A",
        >= 1300 => "B",
        >= 1000 => "C",
        >= 700 => "D",
        _ => "E",
    };
}

public sealed record MultiplayerEvolution
{
    [JsonPropertyName("before")]
    public double Before { get; init; }

    [JsonPropertyName("after")]
    public double After { get; init; }
}

public sealed record MultiplayerAvatar
{
    [JsonPropertyName("pseudo")]
    public string Pseudo { get; init; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; init; } = string.Empty;

    [JsonPropertyName("variation")]
    public int Variation { get; init; }

    /// <summary>L'empreinte de la planche (PNG de 320 x 192), ou null tant qu'aucune n'est declaree.</summary>
    [JsonPropertyName("sheet")]
    public string? Sheet { get; init; }

    [JsonPropertyName("generator")]
    public string? Generator { get; init; }
}

/// <summary>Ce que cette borne doit faire : heberger, ou rejoindre a sa place.</summary>
public sealed record MultiplayerLancement
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    [JsonPropertyName("players")]
    public int Players { get; init; }

    /// <summary>La session de l'hote, pour un invite place, une fois la partie publiee.</summary>
    [JsonPropertyName("netplay")]
    public MultiplayerSessionNetplay? Netplay { get; init; }
}

public sealed record MultiplayerSessionNetplay
{
    [JsonPropertyName("relay")]
    public string Relay { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("session")]
    public string Session { get; init; } = string.Empty;

    [JsonPropertyName("spectate_password")]
    public string SpectatePassword { get; init; } = string.Empty;

    [JsonPropertyName("player_password")]
    public string PlayerPassword { get; init; } = string.Empty;

    [JsonPropertyName("core")]
    public string Core { get; init; } = string.Empty;

    [JsonPropertyName("core_version")]
    public string CoreVersion { get; init; } = string.Empty;

    [JsonPropertyName("crc")]
    public string Crc { get; init; } = string.Empty;

    /// <summary>Jamais les mots de passe dans un journal ou un etat publie.</summary>
    public override string ToString() => $"{Relay}:{Port} {Session} {Core} {CoreVersion}";
}

/// <summary>Un score soumis par une place d'une coop qui concourt : sa valeur, son verdict, et sa place une fois publie.</summary>
public sealed record MultiplayerScoreDeFin
{
    [JsonPropertyName("score")]
    public long Score { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("rank")]
    public int? Rank { get; init; }
}

public sealed record MultiplayerFin
{
    /// <summary>
    /// Une coop qui concourt (31/10/2026) : par place (« 1 »), puis par classement (« 1cc-multi », « 1lc-multi »), le dernier
    /// score soumis depuis le lancement. Chaque borne ne connait que le sien : les autres arrivent au fil des sondages.
    /// </summary>
    [JsonPropertyName("scores")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MultiplayerScoreDeFin>>? Scores { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("duration_s")]
    public int? DurationS { get; init; }

    /// <summary>NIVEAU NELFEPLAY : la place gagnante de la rencontre, lue chez l'hote.</summary>
    [JsonPropertyName("winner_seat")]
    public int? WinnerSeat { get; init; }

    /// <summary><c>pending</c> (en attente d'un invite), <c>validated</c>, <c>disputed</c>, ou null hors versus classe.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>Les cotes avant et apres, par place, une fois le resultat valide.</summary>
    [JsonPropertyName("elo")]
    public IReadOnlyDictionary<string, MultiplayerEvolution>? Elo { get; init; }

    /// <summary>Les statistiques du duel, par place, mesurees chez l'hote.</summary>
    [JsonPropertyName("stats")]
    public IReadOnlyDictionary<string, MultiplayerStatsPlace>? Stats { get; init; }

    /// <summary>Les trois statistiques de chaque place selon le profil du jeu (lot 2), mesurees chez l'hote.</summary>
    [JsonPropertyName("metrics")]
    public MultiplayerMesuresDuSite? Metrics { get; init; }

    /// <summary>La place qui a quitte la rencontre avant sa fin (<c>result</c> vaut alors <c>abandoned</c>).</summary>
    [JsonPropertyName("abandoned_seat")]
    public int? AbandonedSeat { get; init; }

    /// <summary>Le rang de chaque humain d'une fin a plusieurs (CDC Classements v1.0 §6), tel que l'hote l'a rapporte.</summary>
    [JsonPropertyName("ranks")]
    public IReadOnlyList<MultiplayerRangDuSite>? Ranks { get; init; }

    /// <summary>Les places parties avant le resultat d'une rencontre a plusieurs : chacune a pris sa defaite, seule.</summary>
    [JsonPropertyName("abandoned_seats")]
    public IReadOnlyList<int>? AbandonedSeats { get; init; }
}

public sealed record MultiplayerRangDuSite
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    [JsonPropertyName("rank")]
    public int Rank { get; init; }

    [JsonPropertyName("camp")]
    public int? Camp { get; init; }
}

/// <summary>Les statistiques d'une rencontre selon un profil : le profil, puis par place, chaque statistique par son id.</summary>
public sealed record MultiplayerMesuresDuSite
{
    [JsonPropertyName("profile")]
    public MultiplayerProfilDuSite? Profile { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MultiplayerMesureDuSite>>? Seats { get; init; }
}

public sealed record MultiplayerProfilDuSite
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; init; }
}

public sealed record MultiplayerMesureDuSite
{
    [JsonPropertyName("value")]
    public double? Value { get; init; }

    /// <summary><c>measured</c>, <c>zero</c>, <c>unavailable</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

public sealed record MultiplayerStatsPlace
{
    [JsonPropertyName("hits")]
    public int Hits { get; init; }

    [JsonPropertyName("kos")]
    public int Kos { get; init; }

    [JsonPropertyName("perfects")]
    public int Perfects { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }
}

public sealed record MultiplayerInvitation
{
    [JsonPropertyName("invitation")]
    public string Invitation { get; init; } = string.Empty;

    [JsonPropertyName("room")]
    public string Room { get; init; } = string.Empty;

    [JsonPropertyName("game")]
    public string Game { get; init; } = string.Empty;

    /// <summary>Le contenu de la gameroom : la borne invitee doit avoir le meme pour rejoindre.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public MultiplayerJoueur? From { get; init; }

    [JsonPropertyName("expires_ms")]
    public int ExpiresMs { get; init; }

    /// <summary>Une invitation dans une gameroom d'amis vaut tant que celui qui invite y reste (28/10).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = MultiplayerSortes.Publique;
}

/// <summary>Les deux sortes de gameroom (28/10) : publique (FOR PLAYERS…) et d'amis (FOR FRIENDS).</summary>
public static class MultiplayerSortes
{
    public const string Publique = "public";
    public const string Amis = "friends";
}

/// <summary>
/// UNE PROPOSITION (demande du user du 26/10) : un joueur que ce compte suit a mis un jeu dans sa selection, et sa borne
/// montre la collection MULTIPLAYER comme celle-ci. La borne la dit si elle a le meme jeu et le meme contenu.
/// </summary>
public sealed record MultiplayerProposition
{
    [JsonPropertyName("room")]
    public string Room { get; init; } = string.Empty;

    [JsonPropertyName("game")]
    public string Game { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public MultiplayerJoueur? From { get; init; }
}

/// <summary>Un ami en ligne : <c>in_es</c>, <c>in_game</c>, <c>invited</c> ou <c>with_you</c>.</summary>
public sealed record MultiplayerAmi
{
    [JsonPropertyName("handle")]
    public string Handle { get; init; } = string.Empty;

    [JsonPropertyName("pseudo")]
    public string Pseudo { get; init; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; init; } = string.Empty;

    [JsonPropertyName("avatar_seed")]
    public string AvatarSeed { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;
}
