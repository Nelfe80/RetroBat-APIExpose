using System.Net;
using System.Text.Json;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// QUOI DE NEUF (charge du 14/11, point 6 du site, C-8). Au repos, une borne appairee demandait au site, chacun pour soi :
/// <c>agent/work</c>, <c>scores/open-games</c> et <c>challenges/open</c> toutes les 5 min, <c>live/following</c> chaque minute,
/// <c>relay/inbox</c> toutes les 5 min, <c>avatar/pending</c> toutes les 10 min et <c>scores/recovery-status</c> chaque heure :
/// environ 115 appels par heure pour, le plus souvent, rien de neuf.
///
/// Ici, un seul appel toutes les <c>poll_after</c> secondes (300), avec l'empreinte de la reponse precedente (304 sans
/// corps quand rien n'a bouge). La reponse donne la revision du releve, de l'index des jeux ouverts et de celui des
/// defis : seul ce dont la revision a change se relit. Elle porte aussi, en entier, les directs suivis, les avatars en
/// attente, l'etat d'une reconstruction et la boite du relais, que <see cref="SuiviDuSite"/> distribue. Le releve
/// <c>agent/work</c> garde son passage au demarrage et une fois par heure (<see cref="NelfePlayAgentService.ReleveMinimal"/>).
///
/// Un site d'avant (404) : rien ne change, les minuteurs tournent comme avant ; le quoi de neuf se redemande une fois par
/// heure. Une panne : recul (<see cref="CadenceDeFond"/>), et sans reponse pendant deux passages et demi, les minuteurs
/// reprennent d'eux-memes.
/// </summary>
public sealed class QuoiDeNeufService : BackgroundService
{
    internal const string Chemin = "/api/v1/agent/whats-new";

    /// <summary>Apres le releve du demarrage de l'agent.</summary>
    internal static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(45);

    internal static readonly TimeSpan CadenceParDefaut = TimeSpan.FromSeconds(300);
    internal static readonly TimeSpan PlancherDeLaCadence = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PlafondDeLaCadence = TimeSpan.FromHours(1);

    /// <summary>Un site qui ne connait pas le quoi de neuf se redemande une fois par heure : il a peut-etre ete mis a jour.</summary>
    internal static readonly TimeSpan SiteAncienRedemande = TimeSpan.FromHours(1);

    internal static readonly TimeSpan PlafondDuRecul = TimeSpan.FromMinutes(30);

    public enum Issue
    {
        /// <summary>Ni appairage ni installation anonyme : rien a demander.</summary>
        SansIdentite,
        /// <summary>Reponse lue (200 ou 304).</summary>
        Lu,
        /// <summary>Le site ne connait pas <c>agent/whats-new</c> (404, 405) : les minuteurs d'avant.</summary>
        SiteAncien,
        /// <summary>Reseau, erreur du site, reponse illisible.</summary>
        Echec,
    }

    private readonly SuiviDuSite _suivi;
    private readonly Func<string?> _credential;
    private readonly Func<string, HttpClient> _client;
    private readonly Func<CancellationToken, Task<bool>> _releve;
    private readonly Func<bool> _appairee;
    private readonly ILogger? _journal;
    private readonly LecturesConditionnelles _lectures = new(8);

    /// <summary>La revision du releve au dernier <c>agent/work</c> demande ici et abouti.</summary>
    private string? _revisionDuTravailVue;

    public QuoiDeNeufService(NelfePlayAgentService agent, SuiviDuSite suivi, ILogger<QuoiDeNeufService>? journal = null)
        : this(suivi, agent.CredentialDeLaBorne, agent.ClientDuSite, ReleveDe(agent), () => agent.Appairee, journal)
    {
    }

    internal QuoiDeNeufService(SuiviDuSite suivi, Func<string?> credential, Func<string, HttpClient> client,
        Func<CancellationToken, Task<bool>> releve, Func<bool> appairee, ILogger? journal = null)
    {
        _suivi = suivi;
        _credential = credential;
        _client = client;
        _releve = releve;
        _appairee = appairee;
        _journal = journal;
    }

    /// <summary>Un releve de l'agent, abouti ou non.</summary>
    private static Func<CancellationToken, Task<bool>> ReleveDe(NelfePlayAgentService agent) => async ct =>
    {
        var avant = DateTime.UtcNow;
        await agent.PollAsync(ct).ConfigureAwait(false);
        return agent.DernierReleveReussiUtc is { } fait && fait >= avant;
    };

    /// <summary>La cadence demandee par le site a la derniere reponse (300 s par defaut).</summary>
    public TimeSpan Cadence { get; private set; } = CadenceParDefaut;

    /// <summary>La derniere issue (statut, tests).</summary>
    public Issue? DerniereIssue { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(PremierDelai, stoppingToken).ConfigureAwait(false);
            var echecs = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                Issue issue;
                try
                {
                    issue = await TourAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _journal?.LogDebug(ex, "Quoi de neuf : passage en erreur.");
                    issue = Issue.Echec;
                }

                echecs = issue == Issue.Echec ? echecs + 1 : 0;
                await Task.Delay(Attente(issue, Cadence, echecs, Random.Shared.NextDouble()), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// L'attente avant le prochain passage. Pur. Lu : la cadence du site ; echec : la meme, doublee a chaque echec (30 min
    /// au plus) ; site ancien : une heure ; sans identite : 5 min. Toujours dispersee de plus ou moins 10 %.
    /// </summary>
    internal static TimeSpan Attente(Issue issue, TimeSpan cadence, int echecs, double alea) => issue switch
    {
        Issue.SiteAncien => CadenceDeFond.Prochaine(SiteAncienRedemande, 0, SiteAncienRedemande, alea),
        Issue.SansIdentite => CadenceDeFond.Prochaine(CadenceParDefaut, 0, CadenceParDefaut, alea),
        Issue.Echec => CadenceDeFond.Prochaine(cadence, echecs, PlafondDuRecul, alea),
        _ => CadenceDeFond.Prochaine(cadence, 0, cadence, alea),
    };

    /// <summary><c>poll_after</c> borne de 60 s a 1 h ; absent ou illisible : 300 s. Pur.</summary>
    internal static TimeSpan CadenceDemandee(JsonElement racine)
    {
        if (!racine.TryGetProperty("poll_after", out var p) || !p.TryGetInt32(out var secondes) || secondes <= 0) return CadenceParDefaut;
        var demandee = TimeSpan.FromSeconds(secondes);
        return demandee < PlancherDeLaCadence ? PlancherDeLaCadence : demandee > PlafondDeLaCadence ? PlafondDeLaCadence : demandee;
    }

    /// <summary>L'adresse du quoi de neuf, avec le curseur de la boite du relais s'il y en a un. Pur.</summary>
    internal static string Adresse(long? curseurDeLaBoite)
        => curseurDeLaBoite is { } curseur ? Chemin + "?inbox_since=" + Math.Max(0, curseur) : Chemin;

    /// <summary>La revision <c>{"revision": "..."}</c> d'un champ, ou null. Pur.</summary>
    internal static string? RevisionDe(JsonElement racine, string champ)
        => racine.TryGetProperty(champ, out var objet) && objet.ValueKind == JsonValueKind.Object
           && objet.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } texte
            ? texte
            : null;

    private Issue Noter(Issue issue)
    {
        DerniereIssue = issue;
        return issue;
    }

    /// <summary>Un passage : demander, puis relire ce qui a change et distribuer le reste.</summary>
    internal async Task<Issue> TourAsync(CancellationToken ct)
    {
        var credential = _credential();
        if (string.IsNullOrEmpty(credential)) return Noter(Issue.SansIdentite);

        var appairee = _appairee();
        string corps;
        try
        {
            using var client = _client(credential);
            client.Timeout = TimeSpan.FromSeconds(30);
            // La boite du relais ne se lit qu'avec l'appairage (le relais n'a pas de role pour une installation anonyme).
            corps = await _lectures.LireAsync(client, Adresse(appairee ? _suivi.Curseur() : null), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            _suivi.OublierLeQuoiDeNeuf();
            if (DerniereIssue != Issue.SiteAncien) _journal?.LogInformation("Quoi de neuf : le site ne le connait pas encore, minuteurs d'avant.");
            return Noter(Issue.SiteAncien);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _journal?.LogDebug(ex, "Quoi de neuf : site injoignable.");
            return Noter(Issue.Echec);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(corps);
        }
        catch (JsonException ex)
        {
            _journal?.LogDebug(ex, "Quoi de neuf : reponse illisible.");
            return Noter(Issue.Echec);
        }

        using (document)
        {
            var racine = document.RootElement;
            if (racine.ValueKind != JsonValueKind.Object || !racine.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                return Noter(Issue.Echec);
            }

            Cadence = CadenceDemandee(racine);
            var validite = SuiviDuSite.Validite(Cadence);

            // Le releve d'abord : licences, installations et messages ne doivent pas attendre l'heure.
            if (appairee && RevisionDe(racine, SuiviDuSite.Travail) is { } travail
                && !string.Equals(travail, _revisionDuTravailVue, StringComparison.Ordinal))
            {
                if (await _releve(ct).ConfigureAwait(false)) _revisionDuTravailVue = travail;
            }

            // Le releve n'est suivi (et ne se tait toutes les 5 min) que si sa revision est donnee et qu'il a abouti.
            var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (appairee && _revisionDuTravailVue is { } vue && RevisionDe(racine, SuiviDuSite.Travail) == vue) revisions[SuiviDuSite.Travail] = vue;
            foreach (var cle in new[] { SuiviDuSite.JeuxOuverts, SuiviDuSite.DefisOuverts })
            {
                if (RevisionDe(racine, cle) is { } revision) revisions[cle] = revision;
            }

            await _suivi.NoterLesRevisionsAsync(revisions, validite, ct).ConfigureAwait(false);
            await _suivi.DistribuerAsync(racine, validite, ct).ConfigureAwait(false);
        }

        return Noter(Issue.Lu);
    }
}
