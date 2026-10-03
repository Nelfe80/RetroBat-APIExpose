using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Le classement d'un jeu, tel que la plateforme le sert, range en VUES.
///
/// La plateforme rend un classement mondial ordonne
/// (<c>GET /api/v1/scores/board?game=&amp;limit=</c>), et chaque ligne porte deja ce qu'il faut
/// pour les autres vues : la ville, le pays, la salle, le sceau de scellement, le pseudo, et le
/// replay quand il existe. On appelle donc UNE fois, assez profond, et on taille les vues dans
/// ce qui revient : quatre appels reseau pour le meme jeu seraient quatre fois la meme attente.
///
/// LIMITE ASSUMEE : « Ma ville » et « Mon pays » se taillent dans les N premiers mondiaux. Si le
/// meilleur joueur de Paris n'y est pas, il manque. C'est acceptable pour regarder un classement
/// depuis une borne ; le jour ou ca compte, c'est un filtre cote plateforme qu'il faudra, pas un
/// N plus grand.
///
/// Rien n'est jamais bloquant : sans reseau, la vue se dit hors ligne et le menu d'ES n'a pas
/// attendu une milliseconde.
/// </summary>
public sealed class LeaderboardClient
{
    /// <summary>Une ligne de classement, telle qu'on la dessine.</summary>
    public sealed record Ligne(
        int Rang,
        string Joueur,
        long Valeur,
        string Ville,
        string Pays,
        string Salle,
        bool Scelle,
        string? ReplayId,
        string Quand,
        bool CestMoi,
        string Monde = "",
        string Poignee = "",    // la poignee publique du joueur : ce qui permet de le suivre
        bool PlusBasEstMieux = false);  // le sens du PROFIL du jeu : vrai pour un contre-la-montre

    /// <summary>Ce qu'une vue a a montrer, y compris son echec.</summary>
    public sealed record Resultat(IReadOnlyList<Ligne> Lignes, string Etat)
    {
        public static Resultat Vide(string etat) => new(Array.Empty<Ligne>(), etat);
    }

    public const string EtatOk = "ok";
    public const string EtatAucunScore = "aucun_score";
    public const string EtatHorsLigne = "hors_ligne";

    /// <summary>Profondeur du classement demande : de quoi tailler les vues locales.</summary>
    private const int Profondeur = 200;

    /// <summary>Le client HTTP du classement, qui demande des reponses compressees (Program.cs).</summary>
    public const string HttpClientName = "nelfeplay-classement";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LeaderboardClient> _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);

    /// <summary>
    /// Ou se gardent les derniers classements recus, un fichier par jeu et par regle : le panneau
    /// les montre des l'ouverture, meme juste apres un redemarrage de l'API.
    /// </summary>
    public string DossierDisque { get; init; } = Path.Combine(RetroBatPaths.PluginRoot, "state", "leaderboard", "derniers");

    /// <summary>Un classement deja lu par jeu ET par regle : les onglets d'un jeu en lisent plusieurs.</summary>
    private readonly Dictionary<string, (DateTime Jusqua, IReadOnlyList<Ligne> Lignes, string Etat)> _caches = new(StringComparer.OrdinalIgnoreCase);

    public LeaderboardClient(IHttpClientFactory httpFactory, ILogger<LeaderboardClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Combien de temps un classement deja lu reste bon. Un score ne tombe pas a la seconde.</summary>
    /// <summary>
    /// 30 s, le Cache-Control de la plateforme. Deux minutes cachaient le replay d'un record tout
    /// juste publie : il arrive sur la plateforme une minute ou deux APRES le score.
    /// </summary>
    public TimeSpan Fraicheur { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Oublie le classement en memoire : apres une partie, il a pu changer.</summary>
    public void Oublier()
    {
        lock (_caches) _caches.Clear();
    }

    /// <summary>
    /// La regle des onglets ordinaires d'un jeu : le 1CC s'il est ouvert, sinon sa premiere regle.
    /// Vide quand la borne ne connait pas ses regles : on demande alors tout, comme avant.
    /// </summary>
    public static string ReglePrincipale(IReadOnlyList<string> regles)
        => regles.Contains("1cc", StringComparer.OrdinalIgnoreCase) ? "1cc" : regles.FirstOrDefault() ?? "";

    /// <summary>
    /// Le classement complet d'un jeu (le mondial), depuis le cache s'il est frais. Le pseudo de
    /// la borne sert a reconnaitre SA ligne, celle qu'on met en avant.
    /// </summary>
    public async Task<Resultat> MondeAsync(string romGroup, string monPseudo, CancellationToken ct, string regle = "")
    {
        if (string.IsNullOrWhiteSpace(romGroup)) return Resultat.Vide(EtatAucunScore);

        await _porte.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cle = romGroup + "|" + regle;
            lock (_caches)
            {
                if (_caches.TryGetValue(cle, out var deja) && DateTime.UtcNow < deja.Jusqua)
                {
                    return new Resultat(deja.Lignes, deja.Etat);
                }
            }

            // LA REGLE EST DEMANDEE (2026-10-02). Sans elle, la plateforme rend le meilleur score
            // de chaque joueur dans CHAQUE regle : un 1CC MULTI se classait parmi les scores solo,
            // et le meme joueur apparaissait deux fois.
            var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/scores/board"
                + $"?game={Uri.EscapeDataString(romGroup)}&limit={Profondeur}"
                + (regle.Length > 0 ? $"&ruleset={Uri.EscapeDataString(regle)}" : "");
            try
            {
                using var client = _httpFactory.CreateClient(HttpClientName);
                client.Timeout = TimeSpan.FromSeconds(6);
                var corps = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                var lignes = Lire(corps, monPseudo);
                var etat = lignes.Count == 0 ? EtatAucunScore : EtatOk;
                lock (_caches) _caches[cle] = (DateTime.UtcNow + Fraicheur, lignes, etat);
                Garder(cle, lignes, etat);
                return new Resultat(lignes, etat);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Hors ligne : on le DIT, on ne montre pas un classement vide qui ferait croire
                // que personne n'a jamais joue. Le dernier classement connu vaut mieux qu'un
                // panneau vide : il reste affiche (2026-10-03).
                _logger.LogDebug(ex, "Classement : {Jeu} injoignable.", romGroup);
                var connu = DernierConnu(romGroup, regle);
                return connu is { Lignes.Count: > 0 }
                    ? new Resultat(connu.Lignes, EtatHorsLigne)
                    : Resultat.Vide(EtatHorsLigne);
            }
        }
        finally
        {
            _porte.Release();
        }
    }

    /// <summary>
    /// LE DERNIER CLASSEMENT CONNU, meme perime (2026-10-03). Le panneau le montre aussitot ouvert
    /// et le remplace quand le classement frais arrive : il n'est plus jamais vide pendant un
    /// chargement. La memoire d'abord, puis le disque, qui survit a un redemarrage de l'API.
    /// Null s'il n'y en a jamais eu.
    /// </summary>
    public Resultat? DernierConnu(string romGroup, string regle = "")
    {
        if (string.IsNullOrWhiteSpace(romGroup)) return null;
        var cle = romGroup + "|" + regle;
        lock (_caches)
        {
            if (_caches.TryGetValue(cle, out var deja)) return new Resultat(deja.Lignes, deja.Etat);
        }
        try
        {
            var fichier = Path.Combine(DossierDisque, NomDeFichier(cle));
            if (!File.Exists(fichier)) return null;
            var copie = JsonSerializer.Deserialize<CopieDisque>(File.ReadAllText(fichier));
            return copie?.Lignes is null ? null : new Resultat(copie.Lignes, copie.Etat ?? EtatOk);
        }
        catch (Exception)
        {
            return null;   // une copie illisible vaut l'absence de copie
        }
    }

    private sealed record CopieDisque(DateTime Le, string? Etat, List<Ligne>? Lignes);

    private void Garder(string cle, IReadOnlyList<Ligne> lignes, string etat)
    {
        try
        {
            Directory.CreateDirectory(DossierDisque);
            var fichier = Path.Combine(DossierDisque, NomDeFichier(cle));
            var temporaire = fichier + ".tmp";
            File.WriteAllText(temporaire, JsonSerializer.Serialize(new CopieDisque(DateTime.UtcNow, etat, lignes.ToList())));
            File.Move(temporaire, fichier, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : copie sur disque impossible.");
        }
    }

    private static string NomDeFichier(string cle)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cle.ToLowerInvariant())))[..32].ToLowerInvariant() + ".json";

    /// <summary>
    /// La vue demandee, taillee dans le classement mondial. Pure : elle se teste sans reseau.
    /// </summary>
    public static IReadOnlyList<Ligne> Tailler(
        IReadOnlyList<Ligne> monde,
        LeaderboardPanelModel.Vue vue,
        string maVille,
        string monPays,
        string maSalle)
    {
        IEnumerable<Ligne> choisies = vue switch
        {
            LeaderboardPanelModel.Vue.Monde => monde,
            LeaderboardPanelModel.Vue.MonPays => monde.Where(l => Meme(l.Pays, monPays)),
            LeaderboardPanelModel.Vue.MaVille => monde.Where(l => Meme(l.Ville, maVille)),
            LeaderboardPanelModel.Vue.MaSalle => monde.Where(l => Meme(l.Salle, maSalle)),
            // « Cette borne » et « Mes records » : ce que le joueur de cette borne a fait.
            LeaderboardPanelModel.Vue.CetteBorne or LeaderboardPanelModel.Vue.MesRecords => monde.Where(l => l.CestMoi),
            _ => monde,
        };

        // Les rangs sont ceux du MONDE : dans « Ma ville », etre 4e mondial veut dire quelque
        // chose, alors qu'un rang 1 recalcule ne dirait rien de plus que « le premier de la
        // liste que je viens de filtrer ».
        return choisies.ToList();
    }

    private static bool Meme(string a, string b)
        => a.Length > 0 && b.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Le corps JSON du board vers nos lignes. Tolerant : un champ absent n'est pas une panne.</summary>
    public static IReadOnlyList<Ligne> Lire(string json, string monPseudo)
    {
        var lignes = new List<Ligne>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return lignes;
            }
            var rang = 0;
            foreach (var r in rows.EnumerateArray())
            {
                string Texte(string nom) => r.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var joueur = Texte("player");
                var anonyme = r.TryGetProperty("anonymous", out var an) && an.ValueKind == JsonValueKind.True;
                var valeur = r.TryGetProperty("value", out var va) && va.ValueKind == JsonValueKind.Number && va.TryGetInt64(out var n) ? n : 0;
                var replay = r.TryGetProperty("replay", out var rp) && rp.ValueKind == JsonValueKind.Object
                    && rp.TryGetProperty("id", out var ri) && ri.ValueKind == JsonValueKind.String
                        ? ri.GetString()
                        : null;
                lignes.Add(new Ligne(
                    ++rang,
                    anonyme || joueur.Length == 0 ? "" : joueur,
                    valeur,
                    Texte("city"),
                    Texte("country"),
                    Texte("venue"),
                    r.TryGetProperty("sealed", out var sc) && sc.ValueKind == JsonValueKind.True,
                    replay,
                    Texte("at"),
                    monPseudo.Length > 0 && string.Equals(joueur, monPseudo, StringComparison.OrdinalIgnoreCase),
                    Texte("world"),     // home | station | stream : le monde du record, comme sur le site
                    Texte("handle"),
                    string.Equals(Texte("better"), "lower", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (JsonException)
        {
            // Un corps illisible vaut un classement vide : l'appelant dira « hors ligne ».
        }
        return lignes;
    }
}
