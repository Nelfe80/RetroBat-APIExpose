using System.Text.Json;
using RetroBat.Api.Infrastructure;

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
        bool CestMoi);

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

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LeaderboardClient> _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);

    private string _jeuEnCache = "";
    private DateTime _cacheJusqua = DateTime.MinValue;
    private IReadOnlyList<Ligne> _cache = Array.Empty<Ligne>();
    private string _etatEnCache = EtatAucunScore;

    public LeaderboardClient(IHttpClientFactory httpFactory, ILogger<LeaderboardClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Combien de temps un classement deja lu reste bon. Un score ne tombe pas a la seconde.</summary>
    public TimeSpan Fraicheur { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Le classement complet d'un jeu (le mondial), depuis le cache s'il est frais. Le pseudo de
    /// la borne sert a reconnaitre SA ligne, celle qu'on met en avant.
    /// </summary>
    public async Task<Resultat> MondeAsync(string romGroup, string monPseudo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(romGroup)) return Resultat.Vide(EtatAucunScore);

        await _porte.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(_jeuEnCache, romGroup, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < _cacheJusqua)
            {
                return new Resultat(_cache, _etatEnCache);
            }

            var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/scores/board"
                + $"?game={Uri.EscapeDataString(romGroup)}&limit={Profondeur}";
            try
            {
                using var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(6);
                var corps = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                var lignes = Lire(corps, monPseudo);
                _jeuEnCache = romGroup;
                _cache = lignes;
                _etatEnCache = lignes.Count == 0 ? EtatAucunScore : EtatOk;
                _cacheJusqua = DateTime.UtcNow + Fraicheur;
                return new Resultat(_cache, _etatEnCache);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Hors ligne : on le DIT, on ne montre pas un classement vide qui ferait croire
                // que personne n'a jamais joue.
                _logger.LogDebug(ex, "Classement : {Jeu} injoignable.", romGroup);
                return Resultat.Vide(EtatHorsLigne);
            }
        }
        finally
        {
            _porte.Release();
        }
    }

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
                var valeur = r.TryGetProperty("value", out var va) && va.TryGetInt64(out var n) ? n : 0;
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
                    monPseudo.Length > 0 && string.Equals(joueur, monPseudo, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (JsonException)
        {
            // Un corps illisible vaut un classement vide : l'appelant dira « hors ligne ».
        }
        return lignes;
    }
}
