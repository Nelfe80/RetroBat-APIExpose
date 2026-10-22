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

    /// <summary>L'appui long : une gameroom pour ce jeu et ce contenu.</summary>
    public Task<MultiplayerReponse?> RejoindreAsync(string jeu, string contenu, int? rttMs, bool panneauOuvert, CancellationToken ct,
        IReadOnlyDictionary<string, int>? choix = null)
        => EnvoyerAsync("join", new { game = jeu, content = contenu, rtt_ms = rttMs, panel = panneauOuvert, choices = choix }, ct);

    /// <summary>NIVEAU NELFEPLAY : ▲▼ dans le panneau, le joueur change de personnage (permis jusqu'au lancement).</summary>
    public Task<MultiplayerReponse?> ChoisirAsync(string room, IReadOnlyDictionary<string, int> choix, CancellationToken ct)
        => EnvoyerAsync("choice", new { room, choices = choix }, ct);

    /// <summary>B maintenu : quitter cette gameroom.</summary>
    public Task<MultiplayerReponse?> QuitterAsync(string room, CancellationToken ct)
        => EnvoyerAsync("leave", new { room }, ct);

    /// <summary>Le sondage : presence, decompte, lancement, resultat, invitations.</summary>
    public Task<MultiplayerReponse?> SonderAsync(bool appui, bool panneauOuvert, int? rttMs, CancellationToken ct)
        => EnvoyerAsync("poll", new { input = appui, panel = panneauOuvert, rtt_ms = rttMs }, ct);

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
    public Task<MultiplayerReponse?> TerminerAsync(string room, string raison, int? dureeSecondes, CancellationToken ct, int? placeGagnante = null)
        => EnvoyerAsync("ended", new { room, reason = raison, duration_s = dureeSecondes, winner_seat = placeGagnante }, ct);

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

    [JsonPropertyName("rooms")]
    public IReadOnlyList<MultiplayerRoom> Rooms { get; init; } = [];

    [JsonPropertyName("invitations")]
    public IReadOnlyList<MultiplayerInvitation> Invitations { get; init; } = [];

    [JsonPropertyName("friends")]
    public IReadOnlyList<MultiplayerAmi> Friends { get; init; } = [];
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

    [JsonPropertyName("max")]
    public int Max { get; init; }

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

public sealed record MultiplayerFin
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("duration_s")]
    public int? DurationS { get; init; }

    /// <summary>NIVEAU NELFEPLAY : la place gagnante de la rencontre, lue chez l'hote.</summary>
    [JsonPropertyName("winner_seat")]
    public int? WinnerSeat { get; init; }
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
