using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// (brief du 13/11, MULTI-05) LA FILE DE REPRISE DES RESULTATS MULTIPLAYER.
///
/// La fin de partie de l'hote (<c>ended</c>), le rapport d'un invite et son annulation (<c>report</c>) partaient une seule
/// fois, avec un delai de 6 s : un site en maintenance deux minutes a la fin d'un match, et la rencontre etait perdue
/// (aucun resultat valide, aucune cote). Comme la file des brouillons du scoring, l'envoi qui n'a pas eu de reponse du site
/// (injoignable, 5xx, 408, 429) est pose sur le disque, un fichier par envoi, et renvoye tel quel, de 5 s a 5 min, pendant
/// 24 h au plus. Le meme corps est renvoye : le site garde un rapport par compte et par salle (ON DUPLICATE KEY), et refuse
/// une seconde fin d'une salle deja finie, ce qui en fait une reponse definitive.
///
/// L'espacement des essais vit en memoire : un redemarrage de l'API reessaie tout de suite.
/// </summary>
public sealed class FileDesResultatsMultiplayer
{
    public const string Schema = "nelfe.multiplayer.resultat.v1";
    public static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DernierDelai = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DureeMax = TimeSpan.FromHours(24);

    private readonly Func<DateTime> _maintenant;
    private readonly Random _alea;
    private readonly object _verrou = new();
    private readonly Dictionary<string, (int Essais, DateTime Prochain)> _essais = new(StringComparer.Ordinal);

    public FileDesResultatsMultiplayer() : this(Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "multiplayer-resultats"))
    {
    }

    public FileDesResultatsMultiplayer(string dossier, Func<DateTime>? maintenant = null, Random? alea = null)
    {
        Dossier = dossier;
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
        _alea = alea ?? new Random();
    }

    public string Dossier { get; }

    /// <summary>Un envoi en attente.</summary>
    public sealed record Envoi(string Id, string Action, string Room, JsonNode Corps, DateTime PoseLe);

    /// <summary>
    /// Vrai si la reponse demande un nouvel essai : pas de reponse lisible du site (null), une erreur du serveur, un delai
    /// depasse ou trop de demandes. Une reponse du site, meme un refus, est definitive.
    /// </summary>
    public static bool ARetenter(MultiplayerReponse? reponse)
        => reponse is null || reponse.Statut >= 500 || reponse.Statut is 408 or 429;

    /// <summary>Le delai avant le prochain essai : 5 s, double a chaque echec, 5 min au plus, a 20 % pres.</summary>
    public static TimeSpan Delai(int essais, double alea)
    {
        var base_ = PremierDelai.TotalSeconds * Math.Pow(2, Math.Max(0, essais - 1));
        var secondes = Math.Min(base_, DernierDelai.TotalSeconds);
        return TimeSpan.FromSeconds(secondes * (0.8 + 0.4 * Math.Clamp(alea, 0, 1)));
    }

    private string Chemin(string id) => Path.Combine(Dossier, id + ".json");

    /// <summary>Pose l'envoi. Rend son identifiant, ou null si le disque refuse.</summary>
    public string? Poser(string action, string room, JsonNode corps)
    {
        try
        {
            // Un fichier par envoi : l'hote envoie une fin, un invite un rapport puis peut-etre une annulation.
            var id = (room.Length > 0 ? new string(room.Where(char.IsLetterOrDigit).Take(32).ToArray()) : "salle") + "-" + action
                     + "-" + _maintenant().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            var fichier = new JsonObject
            {
                ["schema"] = Schema,
                ["action"] = action,
                ["room"] = room,
                ["pose_le"] = _maintenant().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["corps"] = corps.DeepClone(),
            };
            Directory.CreateDirectory(Dossier);
            var temporaire = Chemin(id) + ".tmp";
            File.WriteAllText(temporaire, fichier.ToJsonString(), new System.Text.UTF8Encoding(false));
            File.Move(temporaire, Chemin(id), overwrite: true);
            lock (_verrou) _essais[id] = (1, _maintenant() + Delai(1, _alea.NextDouble()));
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Les envois en attente, du plus ancien au plus recent. Un fichier illisible est retire.</summary>
    public IReadOnlyList<Envoi> EnAttente()
    {
        if (!Directory.Exists(Dossier)) return [];
        var liste = new List<Envoi>();
        foreach (var fichier in Directory.EnumerateFiles(Dossier, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(fichier);
            try
            {
                if (JsonNode.Parse(File.ReadAllText(fichier)) is JsonObject o && (string?)o["schema"] == Schema
                    && (string?)o["action"] is { Length: > 0 } action && o["corps"] is JsonNode corps
                    && DateTime.TryParse((string?)o["pose_le"], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var pose))
                {
                    liste.Add(new Envoi(id, action, (string?)o["room"] ?? "", corps.DeepClone(), pose.ToUniversalTime()));
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (ex is not JsonException) continue;   // verrouille un instant : il sera relu au tour suivant
            }

            Retirer(id);
        }

        return liste.OrderBy(e => e.PoseLe).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Vrai si l'envoi a depasse sa duree de vie : il part de la file sans avoir ete recu.</summary>
    public bool Perime(Envoi envoi) => _maintenant() - envoi.PoseLe > DureeMax;

    public void Retirer(string id)
    {
        try { File.Delete(Chemin(id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        lock (_verrou) _essais.Remove(id);
    }

    public bool EstDu(string id)
    {
        lock (_verrou) return !_essais.TryGetValue(id, out var e) || _maintenant() >= e.Prochain;
    }

    /// <summary>L'essai a echoue : le prochain attendra. Rend le delai choisi.</summary>
    public TimeSpan Reporter(string id)
    {
        lock (_verrou)
        {
            var essais = (_essais.TryGetValue(id, out var e) ? e.Essais : 0) + 1;
            var delai = Delai(essais, _alea.NextDouble());
            _essais[id] = (essais, _maintenant() + delai);
            return delai;
        }
    }

    public int Essais(string id)
    {
        lock (_verrou) return _essais.TryGetValue(id, out var e) ? e.Essais : 0;
    }
}

/// <summary>Renvoie les resultats MULTIPLAYER gardes, des que le site repond (et au demarrage de l'API).</summary>
public sealed class RepriseDesResultatsMultiplayer : BackgroundService
{
    private static readonly TimeSpan Tour = TimeSpan.FromSeconds(5);
    private readonly FileDesResultatsMultiplayer _file;
    private readonly MultiplayerClient _client;
    private readonly ILogger<RepriseDesResultatsMultiplayer>? _journal;

    public RepriseDesResultatsMultiplayer(FileDesResultatsMultiplayer file, MultiplayerClient client,
        ILogger<RepriseDesResultatsMultiplayer>? journal = null)
    {
        _file = file;
        _client = client;
        _journal = journal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UnTourAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _journal?.LogWarning(ex, "MULTIPLAYER : reprise des resultats en echec, nouvel essai au tour suivant.");
            }

            try { await Task.Delay(Tour, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Un tour : chaque envoi du est renvoye une fois. Rend le nombre d'envois sortis de la file.</summary>
    public async Task<int> UnTourAsync(CancellationToken ct)
    {
        var sortis = 0;
        foreach (var envoi in _file.EnAttente())
        {
            if (_file.Perime(envoi))
            {
                _journal?.LogWarning("MULTIPLAYER : {Action} de {Room} abandonne apres {Heures} h sans reponse du site.",
                    envoi.Action, envoi.Room, (int)FileDesResultatsMultiplayer.DureeMax.TotalHours);
                _file.Retirer(envoi.Id);
                sortis++;
                continue;
            }

            if (!_file.EstDu(envoi.Id)) continue;
            var reponse = await _client.RenvoyerAsync(envoi.Action, envoi.Corps, ct).ConfigureAwait(false);
            if (FileDesResultatsMultiplayer.ARetenter(reponse))
            {
                var delai = _file.Reporter(envoi.Id);
                _journal?.LogDebug("MULTIPLAYER : {Action} de {Room} toujours sans reponse, essai {Essai} dans {Delai:F0} s.",
                    envoi.Action, envoi.Room, _file.Essais(envoi.Id), delai.TotalSeconds);
                continue;
            }

            _file.Retirer(envoi.Id);
            sortis++;
            _journal?.LogInformation("MULTIPLAYER : {Action} de {Room} recu par le site apres coup ({Statut}).",
                envoi.Action, envoi.Room, reponse!.Ok ? "ok" : reponse.Reason ?? "refus");
        }

        return sortis;
    }
}
