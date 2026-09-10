using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Remonte a NelfePlay les reactions d'un spectateur pendant un DIRECT.
///
/// Charge inverse de celle des reactions d'un replay, et c'est pourquoi ce n'est pas le meme
/// chemin. Une reaction de replay est durable : elle se range sur une frise, part par lots, et
/// une borne peut renvoyer son journal entier trois jours plus tard. Une reaction de direct ne
/// vaut que quelques secondes : elle part TOUT DE SUITE, seule, sans journal, parce qu'arrivee
/// en retard elle n'anime plus rien.
///
/// La borne s'authentifie comme MACHINE et estampille le jeton de spectateur recu en
/// rejoignant. Elle ne manipule aucune identite de compte : c'est la plateforme qui resout le
/// jeton, applique le budget de cinq, et derive le pseudonyme de la foule.
///
/// Un echec ne se retente PAS. Retenter une reaction perimee la ferait paraitre au mauvais
/// moment, sur un saut qui n'a plus de raison d'etre.
/// </summary>
public sealed class LiveReactionUploader
{
    private readonly NelfePlayDeviceStore _machine;
    private readonly IHttpClientFactory _httpFactory;
    private readonly LiveSpectateState _seance;
    private readonly LiveCrowdModel _foule;
    private readonly ILogger<LiveReactionUploader> _logger;

    public LiveReactionUploader(
        NelfePlayDeviceStore machine,
        IHttpClientFactory httpFactory,
        LiveSpectateState seance,
        LiveCrowdModel foule,
        ILogger<LiveReactionUploader> logger)
    {
        _machine = machine;
        _httpFactory = httpFactory;
        _seance = seance;
        _foule = foule;
        _logger = logger;
    }

    /// <summary>
    /// Envoie une reaction pour la seance en cours. Sans seance, il n'y a rien a viser et on
    /// ne fait rien : c'est le cas normal quand personne ne regarde de direct.
    /// </summary>
    public async Task EnvoyerAsync(string famille, int niveau, CancellationToken ct = default)
    {
        var (session, jeton, _) = _seance.Courant;
        if (session.Length == 0 || jeton.Length == 0)
        {
            return;
        }

        var credential = _machine.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            // Court : une reaction qui met dix secondes a partir est deja hors sujet.
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            var corps = new JsonObject
            {
                ["viewer_token"] = jeton,
                ["reaction"] = famille,
                // L'INTENSITE du geste, un a trois crans, celle de la jauge de charge. Elle
                // choisit l'emoji et la hauteur du saut ; elle ne dit rien du budget.
                ["level"] = Math.Clamp(niveau, 1, 3),
            };

            using var contenu = new StringContent(corps.ToJsonString(), Encoding.UTF8, "application/json");
            using var reponse = await client
                .PostAsync($"/api/v1/agent/live/{Uri.EscapeDataString(session)}/react", contenu, ct)
                .ConfigureAwait(false);

            if (!reponse.IsSuccessStatusCode)
            {
                // 422 = budget epuise, ou jeton perime. Ce n'est pas une panne : c'est la regle
                // qui s'applique, et elle s'applique au CENTRE pour valoir la meme chose sur
                // toutes les bornes.
                _logger.LogDebug(
                    "Direct : reaction refusee (HTTP {Code}).", (int) reponse.StatusCode);
                return;
            }

            // ON ANIME TOUT DE SUITE. La reponse porte l'ACTEUR, que cette borne ne peut pas
            // calculer elle-meme (c'est un pseudonyme derive au centre). Attendre le battement
            // suivant ferait sauter sa propre silhouette deux secondes et demie apres le
            // geste, ce qui se lit comme un bouton qui ne marche pas.
            //
            // L'identifiant est note pour que le flux ne rejoue pas la meme reaction.
            try
            {
                using var doc = JsonDocument.Parse(
                    await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var r = doc.RootElement;
                if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    var id = r.TryGetProperty("id", out var i) && i.TryGetInt64(out var v) ? v : 0L;
                    var acteur = r.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString() ?? "" : "";
                    var nom = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    if (acteur.Length > 0 && _foule.NoterVue(id))
                    {
                        _foule.Reagir(acteur, famille, niveau, nom);
                    }
                }
            }
            catch (Exception ex)
            {
                // La reaction est PARTIE : ne pas avoir su lire la reponse n'annule rien, on
                // perd seulement l'animation immediate, que le battement suivant rattrapera.
                _logger.LogDebug(ex, "Direct : reponse de reaction illisible.");
            }

            _logger.LogInformation("Direct : reaction {Famille} niveau {Niveau} remontee.", famille, niveau);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct : remontee de reaction en echec.");
        }
    }
}
