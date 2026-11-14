using System.Net;
using System.Text.Json;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Speedrun;

/// <summary>Le meilleur temps d'un joueur sur une epreuve, a sa place au classement.</summary>
public sealed record TempsClasse(int Rang, string Joueur, long Valeur);

public sealed record ClassementDEpreuve(int Total, IReadOnlyList<TempsClasse> Lignes);

/// <summary>
/// LES CLASSEMENTS DES EPREUVES, lus sur le site. Le panneau ne fait jamais de requete au rendu : il montre ce qui est connu
/// aussitot ouvert, puis se recompose quand le frais arrive. Hors ligne, le dernier classement connu reste.
/// </summary>
public sealed class ClassementsDesDefis
{
    private const int Profondeur = 100;

    /// <summary>Le Cache-Control de la plateforme sur ces classements.</summary>
    private static readonly TimeSpan Fraicheur = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<ClassementsDesDefis>? _journal;
    private readonly Dictionary<string, (DateTime Lu, ClassementDEpreuve Classement)> _connus = new(StringComparer.Ordinal);

    public ClassementsDesDefis(IHttpClientFactory http, ILogger<ClassementsDesDefis>? journal = null)
    {
        _http = http;
        _journal = journal;
    }

    public IReadOnlyDictionary<string, ClassementDEpreuve> Connus(IEnumerable<string> regles)
    {
        lock (_connus) return regles.Where(_connus.ContainsKey).Distinct().ToDictionary(r => r, r => _connus[r].Classement);
    }

    /// <summary>Le dernier classement connu d'une epreuve et l'heure de sa lecture (null : jamais lu).</summary>
    public (ClassementDEpreuve? Classement, DateTime? Lu) Dernier(string regle)
    {
        lock (_connus) return _connus.TryGetValue(regle, out var c) ? (c.Classement, c.Lu) : (null, null);
    }

    /// <summary>
    /// LE PANNEAU DES DEFIS (lot 4 bis C-10, 14/11) : il lit d'un coup le classement de CHAQUE epreuve du jeu (34 pour Super
    /// Mario Bros.) a chaque ouverture ; avec 60 s de fraicheur, rouvrir le panneau relisait tout. Pour lui, 5 min : un
    /// verdict force la relecture (<c>forcer</c>), le Deck et le direct gardent 60 s.
    /// </summary>
    public static readonly TimeSpan FraicheurDuPanneau = TimeSpan.FromMinutes(5);

    /// <summary>Les empreintes deja recues (C-11) : un site qui en donne repond 304 sans corps.</summary>
    public LecturesConditionnelles Lectures { get; init; } = LecturesConditionnelles.Partagees;

    /// <summary>
    /// <paramref name="forcer"/> : apres un verdict, le classement d'il y a moins d'une minute ne compte plus.
    /// <paramref name="fraicheur"/> : l'age au-dela duquel un classement se relit (60 s par defaut).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ClassementDEpreuve>> ChargerAsync(IReadOnlyCollection<string> regles, CancellationToken ct,
        bool forcer = false, TimeSpan? fraicheur = null)
    {
        var age = fraicheur ?? Fraicheur;
        var aLire = regles.Distinct(StringComparer.Ordinal).Where(r => forcer || !AJour(r, age, DateTime.UtcNow)).ToList();
        if (aLire.Count == 0) return Connus(regles);

        // UNE LECTURE GROUPEE (charge du 14/11, point 4 du site) : le panneau de Super Mario Bros. faisait 34 appels. Une
        // seule epreuve, ou un site qui ne connait pas `rulesets` : un appel par epreuve, comme avant.
        var restantes = aLire;
        if (aLire.Count > 1 && LectureGroupeePossible(DateTime.UtcNow))
        {
            restantes = [];
            foreach (var paquet in aLire.Chunk(ParLectureGroupee))
            {
                var nonLues = await ChargerGroupeAsync(paquet, ct).ConfigureAwait(false);
                if (nonLues is null)
                {
                    // Site d'avant la lecture groupee : ce paquet et les suivants, un par un.
                    restantes = aLire.Skip(aLire.IndexOf(paquet[0])).ToList();
                    break;
                }
            }
        }

        await Task.WhenAll(restantes.Select(r => ChargerUneAsync(r, ct))).ConfigureAwait(false);
        return Connus(regles);
    }

    /// <summary>Au plus 64 epreuves par lecture groupee (le site refuse au-dela).</summary>
    internal const int ParLectureGroupee = 64;

    /// <summary>Un site qui ne connait pas `rulesets` n'est pas redemande avant une heure : il est peut-etre mis a jour depuis.</summary>
    internal static readonly TimeSpan OubliDuSiteAncien = TimeSpan.FromHours(1);

    private DateTime _siteSansLectureGroupee = DateTime.MinValue;

    /// <summary>La lecture groupee vaut-elle d'etre tentee ? Non pendant une heure apres un site qui ne la connaissait pas.</summary>
    internal bool LectureGroupeePossible(DateTime maintenantUtc)
    {
        lock (_connus) return maintenantUtc - _siteSansLectureGroupee >= OubliDuSiteAncien;
    }

    /// <summary>
    /// <c>challenges/board?rulesets=a,b,c</c> : chaque classement de <c>boards</c> se lit comme la lecture seule ; <c>missing</c>
    /// (epreuve inconnue) ne donne rien, comme un 404. Null : le site ne connait pas la lecture groupee (400, 404, 405, ou
    /// une reponse sans <c>boards</c>) ; l'appelant lit alors une epreuve par appel. Une panne (reseau, 5xx) n'est pas un
    /// site ancien : le dernier classement connu reste, sans 64 appels de plus vers un site qui va mal.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ChargerGroupeAsync(IReadOnlyList<string> regles, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
            var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/challenges/board?rulesets="
                + string.Join(",", regles.Select(Uri.EscapeDataString)) + $"&limit={Profondeur}";
            var corps = await Lectures.LireAsync(client, url, ct).ConfigureAwait(false);
            var lus = LireGroupe(corps);
            if (lus is null)
            {
                SiteSansLectureGroupee();
                return null;
            }

            var maintenant = DateTime.UtcNow;
            lock (_connus)
            {
                foreach (var (regle, classement) in lus) _connus[regle] = (maintenant, classement);
            }

            return regles.Where(r => !lus.ContainsKey(r)).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            SiteSansLectureGroupee();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(ex, "Classements groupes des epreuves injoignables.");
            return [];
        }
    }

    private void SiteSansLectureGroupee()
    {
        lock (_connus) _siteSansLectureGroupee = DateTime.UtcNow;
        _journal?.LogDebug("Classements des epreuves : le site ne connait pas la lecture groupee, un appel par epreuve.");
    }

    /// <summary>
    /// Le corps d'une lecture groupee : chaque classement de <c>boards</c>, lu par <see cref="Lire(string)"/>. Null sans
    /// <c>boards</c> (site d'avant la lecture groupee). Pur.
    /// </summary>
    internal static IReadOnlyDictionary<string, ClassementDEpreuve>? LireGroupe(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Object) return null;
        var lus = new Dictionary<string, ClassementDEpreuve>(StringComparer.Ordinal);
        foreach (var tableau in boards.EnumerateObject())
        {
            if (tableau.Value.ValueKind != JsonValueKind.Object) continue;
            lus[tableau.Name] = Lire(tableau.Value.GetRawText());
        }

        return lus;
    }

    /// <summary>Le classement connu de cette epreuve est-il plus jeune que <paramref name="fraicheur"/> ? Pur pour les tests.</summary>
    internal bool AJour(string regle, TimeSpan fraicheur, DateTime maintenantUtc)
    {
        lock (_connus) return _connus.TryGetValue(regle, out var deja) && maintenantUtc - deja.Lu < fraicheur;
    }

    private async Task ChargerUneAsync(string regle, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
            var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/challenges/board?ruleset={Uri.EscapeDataString(regle)}&limit={Profondeur}";
            var classement = Lire(await Lectures.LireAsync(client, url, ct).ConfigureAwait(false));
            lock (_connus) _connus[regle] = (DateTime.UtcNow, classement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(ex, "Classement de l'epreuve {Regle} injoignable.", regle);
        }
    }

    /// <summary>Le corps de <c>/api/v1/challenges/board</c>. Le rang est la place dans la liste : le site la trie deja.</summary>
    internal static ClassementDEpreuve Lire(string json)
    {
        using var document = JsonDocument.Parse(json);
        var racine = document.RootElement;
        var lignes = new List<TempsClasse>();
        if (racine.TryGetProperty("rows", out var rangees) && rangees.ValueKind == JsonValueKind.Array)
        {
            foreach (var rangee in rangees.EnumerateArray())
            {
                if (!rangee.TryGetProperty("value", out var valeur) || !valeur.TryGetInt64(out var v)) continue;
                var joueur = rangee.TryGetProperty("player", out var p) ? p.GetString() ?? "" : "";
                lignes.Add(new TempsClasse(lignes.Count + 1, joueur, v));
            }
        }

        var total = racine.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : lignes.Count;
        return new ClassementDEpreuve(total, lignes);
    }
}
