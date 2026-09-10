using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Alimente la foule pendant qu'on regarde un direct : qui est la, et ce qui vient de se
/// passer.
///
/// SONDAGE et non flux pousse. Le serveur est en PHP-FPM : un evenement pousse y tiendrait un
/// processus ouvert par spectateur, ce qui ne passe pas l'echelle. Un battement de deux
/// secondes et demie coute une requete de quelques centaines d'octets et suffit largement :
/// une reaction qui parait deux secondes apres le geste reste liee a ce qu'on regarde.
///
/// UN SEUL aller-retour porte la PRESENCE et la LECTURE. Le battement dit « je suis la » en
/// meme temps qu'il demande ce qui a change : les separer doublerait le trafic pour la meme
/// information, alors que la presence est precisement ce que la lecture rend.
///
/// Au PREMIER battement on ne rejoue rien : on prend le curseur et on part du present.
/// Rejouer dix minutes d'un coup ferait sauter deux cents silhouettes ensemble et ne
/// raconterait rien.
/// </summary>
public sealed class LiveCrowdPoller : BackgroundService
{
    private static readonly TimeSpan Battement = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan Repos = TimeSpan.FromSeconds(5);

    private readonly NelfePlayDeviceStore _machine;
    private readonly IHttpClientFactory _httpFactory;
    private readonly LiveSpectateState _seance;
    private readonly LiveCrowdModel _foule;
    private readonly ILogger<LiveCrowdPoller> _logger;

    private string _sessionSuivie = "";
    private long _curseur;

    public LiveCrowdPoller(
        NelfePlayDeviceStore machine,
        IHttpClientFactory httpFactory,
        LiveSpectateState seance,
        LiveCrowdModel foule,
        ILogger<LiveCrowdPoller> logger)
    {
        _machine = machine;
        _httpFactory = httpFactory;
        _seance = seance;
        _foule = foule;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var (session, jeton, _) = _seance.Courant;

            if (session.Length == 0 || jeton.Length == 0)
            {
                // Personne ne regarde de direct : on se repose, et on vide ce qui restait pour
                // qu'une foule d'hier ne reparaisse pas au prochain direct.
                if (_sessionSuivie.Length > 0)
                {
                    _sessionSuivie = "";
                    _curseur = 0;
                    _foule.Vider();
                }
                await Attendre(Repos, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(_sessionSuivie, session, StringComparison.Ordinal))
            {
                // Direct different : on repart du present, sans rien reprendre du precedent.
                _sessionSuivie = session;
                _curseur = 0;
                _foule.Vider();
            }

            try
            {
                await BattreAsync(session, jeton, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Un battement perdu ne casse rien : la foule garde son dernier etat et le
                // curseur n'a pas bouge, donc rien ne sera saute au suivant.
                _logger.LogDebug(ex, "Direct : battement de la foule en echec.");
            }

            await Attendre(Battement, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task BattreAsync(string session, string jeton, CancellationToken ct)
    {
        var credential = _machine.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        var client = _httpFactory.CreateClient();
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(6);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

        var corps = new JsonObject
        {
            ["viewer_token"] = jeton,
            ["since"] = _curseur,
        };

        using var contenu = new StringContent(corps.ToJsonString(), Encoding.UTF8, "application/json");
        using var reponse = await client
            .PostAsync($"/api/v1/agent/live/{Uri.EscapeDataString(session)}/crowd", contenu, ct)
            .ConfigureAwait(false);
        if (!reponse.IsSuccessStatusCode)
        {
            return;
        }

        using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;

        var acteurs = new List<string>();
        if (r.TryGetProperty("actors", out var la) && la.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in la.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String)
                {
                    acteurs.Add(a.GetString() ?? "");
                }
            }
        }

        var total = r.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : acteurs.Count;
        _foule.Poser(acteurs, total);

        // Les places d'abord, les reactions ensuite : une reaction dont l'auteur n'a pas encore
        // de place ne se dessine pas, et l'ordre inverse en perdrait a chaque battement.
        var premier = _curseur == 0;
        if (!premier && r.TryGetProperty("reactions", out var lr) && lr.ValueKind == JsonValueKind.Array)
        {
            var maintenant = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var x in lr.EnumerateArray())
            {
                _foule.Reagir(
                    Texte(x, "actor"),
                    Texte(x, "reaction"),
                    x.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var niv) ? niv : 1,
                    Texte(x, "name"),
                    maintenant);
            }
        }

        if (r.TryGetProperty("cursor", out var c) && c.TryGetInt64(out var curseur) && curseur > _curseur)
        {
            _curseur = curseur;
        }
    }

    private static string Texte(JsonElement racine, string nom)
        => racine.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static async Task Attendre(TimeSpan duree, CancellationToken ct)
    {
        try
        {
            await Task.Delay(duree, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arret demande : rien a signaler.
        }
    }
}
