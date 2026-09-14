using System.Net.Http.Json;
using System.Text.Json;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Ce que le panneau demande a la plateforme EN TANT QUE MACHINE : qui le joueur suit, les
/// directs en cours sur le jeu affiche, et les directs des joueurs suivis.
///
/// Le site sert ces memes donnees a un navigateur, authentifie par sa session. Une borne n'a pas
/// de session : elle s'authentifie avec son identifiant de machine, et le lien machine -> compte
/// dit au nom de qui elle parle. C'est la mecanique deja eprouvee pour rejoindre un direct et
/// pour le jeton de spectateur d'un replay.
///
/// Rien ici n'est vital : une borne sans compte lie, ou hors ligne, garde un classement qui
/// fonctionne. Chaque appel echoue donc en silence et rend une valeur vide.
/// </summary>
public sealed class LeaderboardSocialClient
{
    /// <summary>Un direct en cours : qui joue, a quoi, et sur quelle machine.</summary>
    public sealed record Direct(
        string Session,
        string Poignee,
        string Pseudo,
        string Jeu,
        string NomDuJeu,
        string Systeme,
        string Genre,
        string Type,
        string Depuis);

    /// <summary>Un contest en cours ou ouvert : son titre, qui l'organise, et son etat.</summary>
    public sealed record Contest(
        string Id,
        string Titre,
        string Jeu,
        string Statut,
        string Organisateur,
        int Joueurs,
        string Genre = "",          // stream | venue
        string Hote = "",           // la chaine, ou l'identifiant de salle
        string CleCertifiee = "");  // contest_id des scores certifies, quand il existe

    private readonly IHttpClientFactory _fabrique;
    private readonly NelfePlayDeviceStore _machine;
    private readonly ILogger<LeaderboardSocialClient> _journal;

    private readonly object _verrou = new();
    private HashSet<string> _suivis = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _suivisLus = DateTime.MinValue;

    public LeaderboardSocialClient(
        IHttpClientFactory fabrique,
        NelfePlayDeviceStore machine,
        ILogger<LeaderboardSocialClient> journal)
    {
        _fabrique = fabrique;
        _machine = machine;
        _journal = journal;
    }

    /// <summary>Les poignees que ce compte suit. En memoire : le panneau les demande a chaque ligne.</summary>
    public IReadOnlySet<string> Suivis
    {
        get { lock (_verrou) return _suivis; }
    }

    public bool Suit(string? poignee)
    {
        if (string.IsNullOrWhiteSpace(poignee)) return false;
        lock (_verrou) return _suivis.Contains(poignee.Trim());
    }

    public async Task RafraichirLesSuivisAsync(CancellationToken ct = default)
    {
        // Deux minutes suffisent : suivre quelqu'un se fait depuis cette borne ou depuis le
        // site, et la bascule met la liste a jour sans attendre.
        if (DateTime.UtcNow - _suivisLus < TimeSpan.FromMinutes(2)) return;
        var doc = await LireAsync("follows", ct).ConfigureAwait(false);
        if (doc is null) return;
        var trouves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("handles", out var liste) && liste.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in liste.EnumerateArray())
            {
                if (h.ValueKind == JsonValueKind.String && h.GetString() is { Length: > 0 } valeur) trouves.Add(valeur);
            }
        }
        lock (_verrou)
        {
            _suivis = trouves;
            _suivisLus = DateTime.UtcNow;
        }
        doc.Dispose();
    }

    /// <summary>Suivre ou ne plus suivre. Rend l'etat OBTENU, pas l'etat demande.</summary>
    public async Task<bool> BasculerLeSuiviAsync(string poignee, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(poignee)) return false;
        var voulu = !Suit(poignee);
        var client = Client();
        if (client is null) return !voulu;
        try
        {
            using var reponse = await client
                .PostAsJsonAsync("/api/v1/agent/follows/toggle", new { handle = poignee.Trim(), follow = voulu }, ct)
                .ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                _journal.LogInformation("Classement : suivi refuse ({Statut}).", (int) reponse.StatusCode);
                return !voulu;
            }
            using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var obtenu = doc.RootElement.TryGetProperty("followed", out var f) && f.ValueKind == JsonValueKind.True;
            lock (_verrou)
            {
                if (obtenu) _suivis.Add(poignee.Trim()); else _suivis.Remove(poignee.Trim());
            }
            return obtenu;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal.LogDebug(ex, "Classement : bascule de suivi impossible.");
            return !voulu;
        }
    }

    /// <summary>Les directs en cours SUR CE JEU (liste publique : pas besoin de compte lie).</summary>
    public Task<IReadOnlyList<Direct>> DirectsDuJeuAsync(string jeu, CancellationToken ct = default)
        => DirectsAsync("live/for-game?game=" + Uri.EscapeDataString(jeu), "lives", ct);

    /// <summary>Les directs des joueurs que ce compte suit.</summary>
    public Task<IReadOnlyList<Direct>> DirectsSuivisAsync(CancellationToken ct = default)
        => DirectsAsync("live/following", "live", ct);

    private async Task<IReadOnlyList<Direct>> DirectsAsync(string chemin, string champ, CancellationToken ct)
    {
        var doc = await LireAsync(chemin, ct).ConfigureAwait(false);
        if (doc is null) return Array.Empty<Direct>();
        try
        {
            if (!doc.RootElement.TryGetProperty(champ, out var liste) || liste.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Direct>();
            }
            var directs = new List<Direct>();
            foreach (var d in liste.EnumerateArray())
            {
                string Texte(string nom) => d.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var session = Texte("session");
                if (session.Length == 0) continue;
                // La liste publique nomme le joueur « pseudo », celle des suivis « player ».
                var pseudo = Texte("pseudo");
                if (pseudo.Length == 0) pseudo = Texte("player");
                directs.Add(new Direct(
                    session, Texte("handle"), pseudo, Texte("game"), Texte("game_name"),
                    Texte("system"), Texte("kind"), Texte("type"), Texte("since")));
            }
            return directs;
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// <summary>
    /// Les contests EN COURS ou OUVERTS sur ce jeu. La liste est publique (LiveContest, relayee
    /// par la plateforme) : pas besoin de compte lie pour la lire.
    /// </summary>
    public async Task<IReadOnlyList<Contest>> ContestsDuJeuAsync(string jeu, string nomDuJeu, CancellationToken ct = default)
    {
        try
        {
            using var client = _fabrique.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(6);
            using var reponse = await client.GetAsync("/contests/data?limit=200", ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return Array.Empty<Contest>();
            using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("contests", out var liste) || liste.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Contest>();
            }
            var slugDuNom = GamelistIdentity.Slugifier(nomDuJeu);
            var trouves = new List<Contest>();
            foreach (var c in liste.EnumerateArray())
            {
                string Texte(string nom) => c.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var statut = Texte("status");
                if (statut is not ("live" or "open")) continue;
                // Le jeu d'un contest est un identifiant de jeu chez les streamers, parfois un
                // nom pour les manches de salle : on accepte l'un ou l'autre.
                var sonJeu = Texte("game");
                if (!string.Equals(sonJeu, jeu, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(GamelistIdentity.Slugifier(sonJeu), slugDuNom, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var joueurs = c.TryGetProperty("players", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : 0;
                trouves.Add(new Contest(Texte("id"), Texte("title"), sonJeu, statut, Texte("hostLabel"), joueurs,
                    Texte("kind"), Texte("hostValue"), Texte("contestId")));
            }
            return trouves;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal.LogDebug(ex, "Classement : contests indisponibles.");
            return Array.Empty<Contest>();
        }
    }

    private async Task<JsonDocument?> LireAsync(string chemin, CancellationToken ct)
    {
        var client = Client();
        if (client is null) return null;
        try
        {
            using var reponse = await client.GetAsync("/api/v1/agent/" + chemin, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal.LogDebug(ex, "Classement : {Chemin} indisponible.", chemin);
            return null;
        }
    }

    private HttpClient? Client()
    {
        var identifiant = _machine.GetCredential();
        if (string.IsNullOrEmpty(identifiant)) return null;
        var client = _fabrique.CreateClient();
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(6);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", identifiant);
        return client;
    }
}
