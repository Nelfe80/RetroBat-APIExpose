using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Retrouver NOTRE session dans la liste du lobby libretro.
///
/// Pourquoi ce detour : quand RetroArch heberge derriere un relais, c'est le RELAIS qui attribue
/// l'identifiant de session, et RetroArch ne le transmet qu'au lobby. Il n'existe aucune cle de
/// configuration pour l'imposer (`--mitm-session=ID` est documente « to join »), et aucune ligne
/// de log ne l'ecrit — verifie dans le binaire. Le lobby est donc le SEUL endroit ou le lire.
///
/// D'ou le pseudo : on heberge sous « NELFEPLAY_&lt;pseudo&gt;_&lt;jeton&gt; », et le jeton nous
/// sert a reconnaitre notre propre entree parmi celles du monde entier.
///
/// L'entree porte aussi `core_name`, `core_version` et `game_crc` : exactement le triplet que la
/// poignee de main netplay exige d'identique des deux cotes. La verification « meme coeur, meme
/// dump » se fait donc AVANT de lancer l'invite, au lieu de le laisser echouer sans explication.
/// </summary>
public sealed class NetplayLobbyClient
{
    /// <summary>
    /// Le lobby ne repond QU'EN CLAIR : le port 443 refuse la connexion (mesure 2026-09-09).
    ///
    /// L'identifiant de session voyage donc a decouvert. Ce n'est pas un trou, parce que ce
    /// n'est pas lui qui protege : les deux mots de passe ne passent JAMAIS par le lobby, qui
    /// ne publie que des booleens « il y en a un ». Connaitre l'identifiant ne donne pas
    /// l'entree ; c'est NelfePlay qui distribue les mots de passe, et en TLS.
    /// </summary>
    private const string ListeUrl = "http://lobby.libretro.com/list/";

    /// <summary>Le lobby met quelques secondes a enregistrer une annonce ; on ne le harcele pas.</summary>
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NetplayLobbyClient> _logger;

    public NetplayLobbyClient(IHttpClientFactory httpFactory, ILogger<NetplayLobbyClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Une session telle que le lobby la decrit. Seuls les champs qui nous servent sont retenus :
    /// de quoi rejoindre, et de quoi verifier qu'on le peut.
    /// </summary>
    public sealed record Session(
        string Id,
        string RelayHote,
        int RelayPort,
        string Jeu,
        string Crc,
        string Coeur,
        string VersionCoeur,
        bool MotDePasseJoueur,
        bool MotDePasseSpectateur,
        string Pseudo);

    /// <summary>
    /// Attend que NOTRE annonce apparaisse au lobby et rend sa session, ou null au bout du temps
    /// imparti. L'attente est normale : RetroArch annonce apres avoir monte son tunnel.
    /// </summary>
    public async Task<Session?> AttendreAsync(string jeton, TimeSpan patience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jeton))
        {
            return null;
        }

        var limite = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < limite && !ct.IsCancellationRequested)
        {
            var trouvee = await ChercherAsync(jeton, ct).ConfigureAwait(false);
            if (trouvee is not null)
            {
                return trouvee;
            }
            try
            {
                await Task.Delay(Intervalle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        _logger.LogWarning("Netplay : notre annonce n'est pas apparue au lobby en {Secondes} s.",
            (int)patience.TotalSeconds);
        return null;
    }

    /// <summary>Un seul passage : la session portant ce jeton, ou null.</summary>
    public async Task<Session?> ChercherAsync(string jeton, CancellationToken ct = default)
    {
        string charge;
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            charge = await client.GetStringAsync(ListeUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Le lobby est un service TIERS : qu'il soit lent ou absent ne doit jamais faire
            // echouer autre chose que la decouverte de la session.
            _logger.LogWarning(ex, "Netplay : liste du lobby injoignable.");
            return null;
        }

        foreach (var session in Analyser(charge))
        {
            if (session.Pseudo.Contains(jeton, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }
        return null;
    }

    /// <summary>
    /// Lit la liste du lobby. Publique et statique pour etre verifiable sur une capture reelle,
    /// sans reseau.
    /// </summary>
    public static IReadOnlyList<Session> Analyser(string json)
    {
        List<Entree>? brutes;
        try
        {
            brutes = JsonSerializer.Deserialize<List<Entree>>(json);
        }
        catch (JsonException)
        {
            return [];
        }
        if (brutes is null)
        {
            return [];
        }

        var sortie = new List<Session>(brutes.Count);
        foreach (var e in brutes)
        {
            // Le lobby a servi deux formes au fil du temps : les champs a plat, ou enveloppes
            // dans « fields ». On accepte les deux plutot que de casser le jour ou ca rebascule.
            var f = e.Fields ?? e;
            if (string.IsNullOrEmpty(f.MitmSession))
            {
                continue;   // sans relais, cette entree ne nous sert a rien
            }
            sortie.Add(new Session(
                f.MitmSession ?? "",
                f.MitmIp ?? "",
                f.MitmPort,
                f.GameName ?? "",
                f.GameCrc ?? "",
                f.CoreName ?? "",
                f.CoreVersion ?? "",
                f.HasPassword,
                f.HasSpectatePassword,
                f.Username ?? ""));
        }
        return sortie;
    }

    /// <summary>La forme brute d'une entree du lobby. Seuls les champs utiles sont nommes.</summary>
    private sealed class Entree
    {
        [JsonPropertyName("fields")] public Entree? Fields { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("game_name")] public string? GameName { get; set; }
        [JsonPropertyName("game_crc")] public string? GameCrc { get; set; }
        [JsonPropertyName("core_name")] public string? CoreName { get; set; }
        [JsonPropertyName("core_version")] public string? CoreVersion { get; set; }
        [JsonPropertyName("mitm_ip")] public string? MitmIp { get; set; }
        [JsonPropertyName("mitm_port")] public int MitmPort { get; set; }
        [JsonPropertyName("mitm_session")] public string? MitmSession { get; set; }
        [JsonPropertyName("has_password")] public bool HasPassword { get; set; }
        [JsonPropertyName("has_spectate_password")] public bool HasSpectatePassword { get; set; }
    }
}
