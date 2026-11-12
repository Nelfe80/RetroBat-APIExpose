using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.VocalRoom;

/// <summary>
/// Les routes machine du vocal sur NelfePlay-Site (contrat lots B/C, <c>CONTRAT_SITE_API.md</c>, reconcilie le 12/11) :
/// <c>/api/v1/agent/voice/{profile,session,token,stats,leave}</c>, avec l'identifiant de la borne comme MULTIPLAYER.
///
/// Le site decide de tout (liaison, consentement, politique, lobby). La borne lit <c>reason</c>, pas le code HTTP. Le
/// jeton du joueur ne vit qu'en memoire, n'est jamais journalise et ne part que vers l'exe (auth.apply_token).
/// </summary>
public sealed class VocalRoomSiteClient
{
    private static readonly JsonSerializerOptions Lecture = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory? _fabrique;
    private readonly NelfePlayDeviceStore? _machine;
    private readonly ILogger<VocalRoomSiteClient>? _journal;
    private readonly Func<HttpClient?>? _clientDeTest;

    public VocalRoomSiteClient(IHttpClientFactory fabrique, NelfePlayDeviceStore machine, ILogger<VocalRoomSiteClient>? journal = null)
    {
        _fabrique = fabrique;
        _machine = machine;
        _journal = journal;
    }

    /// <summary>Pour les tests : un client HTTP deja pret (adresse et en-tete poses par le test).</summary>
    internal VocalRoomSiteClient(Func<HttpClient?> clientDeTest, ILogger<VocalRoomSiteClient>? journal = null)
    {
        _clientDeTest = clientDeTest;
        _journal = journal;
    }

    public async Task<VocalProfil> ProfilAsync(CancellationToken ct)
    {
        var (lu, raison) = await AppelerAsync<VocalProfil>(HttpMethod.Get, "profile", null, ct).ConfigureAwait(false);
        return lu ?? new VocalProfil { Ok = false, Reason = raison };
    }

    public async Task<VocalReponseSession> SessionAsync(string room, CancellationToken ct)
    {
        var (lu, raison) = await AppelerAsync<VocalReponseSession>(HttpMethod.Post, "session", new { room }, ct).ConfigureAwait(false);
        return lu ?? new VocalReponseSession { Ok = false, Reason = raison };
    }

    public async Task<VocalJeton> JetonAsync(string room, long generation, CancellationToken ct)
    {
        var (lu, raison) = await AppelerAsync<VocalJeton>(HttpMethod.Post, "token", new { room, generation }, ct).ConfigureAwait(false);
        return lu ?? new VocalJeton { Ok = false, Reason = raison };
    }

    /// <summary>
    /// L'ecran de fin : <c>statut</c> = <c>shown</c>, <c>heartbeat</c> ou <c>closed</c> ; avec <c>closed</c>, <c>raison</c> =
    /// <c>ok</c>, <c>cancel</c>, <c>replay</c>, <c>lost</c>, <c>process_exit</c> ou <c>shutdown</c>.
    /// </summary>
    public async Task<VocalStats> StatsAsync(string room, long generation, string statut, string? raison, CancellationToken ct)
    {
        var (lu, r) = await AppelerAsync<VocalStats>(HttpMethod.Post, "stats", new { room, generation, status = statut, reason = raison }, ct)
            .ConfigureAwait(false);
        return lu ?? new VocalStats { Ok = false, Reason = r };
    }

    /// <summary>La borne quitte le vocal sans quitter la room : <c>raison</c> = <c>user</c>, <c>crash</c> ou <c>shutdown</c>.</summary>
    public async Task<bool> QuitterAsync(string room, long generation, string raison, CancellationToken ct)
    {
        var (lu, _) = await AppelerAsync<VocalReponse>(HttpMethod.Post, "leave", new { room, generation, reason = raison }, ct).ConfigureAwait(false);
        return lu?.Ok == true;
    }

    /// <summary>
    /// Un appel ; la reponse est lue meme quand le statut est une erreur (le site y met sa raison). Rend (null, raison)
    /// quand rien n'est lisible : <c>unauthorized</c> (401, borne non liee), <c>voice_unsupported</c> (404 d'un site
    /// ancien), <c>offline</c> (injoignable ou pas de lien machine).
    /// </summary>
    private async Task<(T? Lu, string Raison)> AppelerAsync<T>(HttpMethod methode, string route, object? corps, CancellationToken ct)
        where T : VocalReponse
    {
        var client = Client();
        if (client is null)
        {
            return (null, "offline");
        }

        try
        {
            using var requete = new HttpRequestMessage(methode, "/api/v1/agent/voice/" + route);
            if (corps is not null)
            {
                requete.Content = JsonContent.Create(corps);
            }

            using var reponse = await client.SendAsync(requete, ct).ConfigureAwait(false);
            if (reponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (null, "unauthorized");
            }

            var texte = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            T? lu = null;
            try
            {
                lu = texte.Length == 0 ? null : JsonSerializer.Deserialize<T>(texte, Lecture);
            }
            catch (JsonException)
            {
                lu = null;
            }

            if (reponse.StatusCode == HttpStatusCode.NotFound && (lu is null || lu.Reason is null))
            {
                // Un site qui ne connait pas la route : il n'a pas de vocal, la borne se tait.
                return (null, "voice_unsupported");
            }

            if (lu is null)
            {
                return (null, reponse.IsSuccessStatusCode ? "empty" : "http_" + (int)reponse.StatusCode);
            }

            if (!lu.Ok)
            {
                // Jamais le corps : celui de la route token pourrait porter un jeton.
                _journal?.LogInformation("VOCAL : {Route} refuse ({Statut}, {Raison})", route, (int)reponse.StatusCode, lu.Reason ?? "-");
            }

            return (lu, lu.Reason ?? string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _journal?.LogDebug("VOCAL : {Route} injoignable ({Type})", route, ex.GetType().Name);
            return (null, "offline");
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

        var identifiant = _machine?.GetCredential();
        if (string.IsNullOrEmpty(identifiant) || _fabrique is null)
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

/// <summary>Le socle de toute reponse : <c>{"ok", "reason"}</c>.</summary>
public record VocalReponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("retry_ms")]
    public long? RetryMs { get; init; }
}

/// <summary>§1 du contrat : ce que le compte lie a cette borne permet.</summary>
public sealed record VocalProfil : VocalReponse
{
    [JsonPropertyName("voice_supported")]
    public bool VoiceSupported { get; init; }

    [JsonPropertyName("discord_linked")]
    public bool DiscordLinked { get; init; }

    [JsonPropertyName("voice_consent")]
    public bool VoiceConsent { get; init; }

    /// <summary><c>off | ask | friends_auto | eligible_auto</c>.</summary>
    [JsonPropertyName("voice_preference")]
    public string VoicePreference { get; init; } = "ask";

    [JsonPropertyName("mic_default")]
    public string MicDefault { get; init; } = "muted";

    [JsonPropertyName("consent_version")]
    public int ConsentVersion { get; init; }

    /// <summary>Le site permet-il une session vocale a ce compte ? (§8 du contrat, point 1)</summary>
    public bool Permis => Ok && VoiceSupported && DiscordLinked && VoiceConsent
        && !string.Equals(VoicePreference, "off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>§2 et §7 du contrat : l'etat vocal de cette borne pour une room (route session, ou <c>rooms[].voice</c> du sondage).</summary>
public sealed record MultiplayerVoix
{
    [JsonPropertyName("room")]
    public string? Room { get; init; }

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    /// <summary><c>pending</c>, <c>ready</c>, <c>closing</c>, <c>closed</c>, <c>unavailable</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("lobby_id")]
    public string? LobbyId { get; init; }

    /// <summary><c>auto</c>, <c>offer</c>, <c>none</c> : decision du site, jamais de la borne.</summary>
    [JsonPropertyName("join")]
    public string Join { get; init; } = "none";

    [JsonPropertyName("mic_muted")]
    public bool MicMuted { get; init; } = true;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("retry_ms")]
    public long? RetryMs { get; init; }
}

public sealed record VocalReponseSession : VocalReponse
{
    [JsonPropertyName("voice")]
    public MultiplayerVoix? Voice { get; init; }
}

/// <summary>§3 du contrat. Le jeton n'apparait jamais dans <see cref="ToString"/> (journal, exception, debogueur).</summary>
public sealed record VocalJeton : VocalReponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("expires_at")]
    public long? ExpiresAt { get; init; }

    [JsonPropertyName("scopes")]
    public string? Scopes { get; init; }

    [JsonPropertyName("discord_user_id")]
    public string? DiscordUserId { get; init; }

    public override string ToString() => $"VocalJeton {{ Ok = {Ok}, Reason = {Reason ?? "-"}, AccessToken = ***, ExpiresAt = {ExpiresAt} }}";
}

/// <summary>§4 du contrat : l'ecran de fin.</summary>
public sealed record VocalStats : VocalReponse
{
    /// <summary><c>pending | open | closing | closed</c>.</summary>
    [JsonPropertyName("lobby")]
    public string? Lobby { get; init; }

    [JsonPropertyName("remaining")]
    public int? Remaining { get; init; }

    [JsonPropertyName("detach")]
    public bool Detach { get; init; }

    /// <summary>Faut-il detacher ? Le drapeau du site, ou ce qui le vaut d'un site qui ne l'enverrait pas.</summary>
    public bool ADetacher => Detach || Lobby is "closing" or "closed" || Remaining is <= 1;
}
