using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Social;

/// <summary>
/// L'ancre de confiance des événements sociaux (LOT R9).
///
/// Un événement signé ne vaut que si l'on sait par QUELLE clé il doit l'être. Cette clé s'apprend
/// une seule fois, chez la plateforme, en TLS, et s'épingle ici. Ensuite la borne accepte des
/// événements de n'importe quel pair, puisqu'elle sait à quelle empreinte ils doivent répondre :
/// c'est exactement ce qui rend la distribution possible sans arbitre.
///
/// La clé ne s'apprend JAMAIS d'un pair. Un pair qui pourrait fournir la clé de référence
/// signerait ce qu'il veut avec la sienne, et toute la chaîne ne prouverait plus rien.
///
/// Une clé déjà épinglée ne change pas toute seule. Si la plateforme en présente une autre, on
/// garde l'ancienne et on le dit dans le journal : une rotation est un acte délibéré, pas un
/// effet de bord d'une requête HTTP. Elle se fait en effaçant le fichier d'épinglage.
/// </summary>
public sealed class SocialIssuerPin
{
    public sealed record Pin(string KeyId, byte[] Spki, string Pem);

    private const string Chemin = "/api/v1/social/issuer";

    private readonly ReplayStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SocialIssuerPin> _logger;

    private readonly object _gate = new();
    private Pin? _pin;
    private bool _lu;
    private DateTime _dernierEssai = DateTime.MinValue;

    public SocialIssuerPin(ReplayStore store, IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<SocialIssuerPin> logger)
    {
        _store = store; _httpFactory = httpFactory; _config = config; _logger = logger;
    }

    private string Fichier => Path.Combine(_store.SocialRoot, "issuer.json");

    /// <summary>La clé épinglée, sans aller sur le réseau. Null tant qu'on n'en a pas appris.</summary>
    public Pin? Current
    {
        get
        {
            lock (_gate)
            {
                if (_lu) return _pin;
                _lu = true;
                try
                {
                    if (File.Exists(Fichier))
                    {
                        var doc = JsonNode.Parse(File.ReadAllText(Fichier)) as JsonObject;
                        var pem = doc?["public_key"]?.GetValue<string>() ?? string.Empty;
                        var keyId = doc?["key_id"]?.GetValue<string>() ?? string.Empty;
                        var (spki, mesure) = SocialEventVerifier.FromPem(pem);
                        // L'empreinte écrite dans le fichier n'est pas crue : on la RECALCULE
                        // depuis la clé. Un fichier retouché à la main ne doit pas pouvoir faire
                        // accepter une clé sous une autre identité.
                        if (spki.Length > 0 && mesure.Length > 0
                            && string.Equals(mesure, keyId, StringComparison.Ordinal))
                            _pin = new Pin(mesure, spki, pem);
                        else if (spki.Length > 0)
                            _logger.LogWarning("Replay social : épinglage incohérent, ignoré ({Fichier}).", Fichier);
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Replay social : épinglage illisible."); }
                return _pin;
            }
        }
    }

    /// <summary>
    /// Rend la clé épinglée, en allant l'apprendre chez la plateforme si on n'en a pas encore.
    /// Une plateforme injoignable n'est pas une erreur : on réessaiera, et d'ici là aucun
    /// événement distant n'est accepté — ce qui est le comportement voulu.
    /// </summary>
    public async Task<Pin?> EnsureAsync(CancellationToken ct)
    {
        var deja = Current;
        if (deja is not null) return deja;

        lock (_gate)
        {
            // Une borne hors ligne ne doit pas marteler la plateforme à chaque lecture.
            if (DateTime.UtcNow - _dernierEssai < TimeSpan.FromMinutes(10)) return null;
            _dernierEssai = DateTime.UtcNow;
        }

        var baseUrl = _config["Replay:Share:TransitUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await client.GetAsync(baseUrl.TrimEnd('/') + Chemin, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var corps = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var doc = JsonNode.Parse(corps) as JsonObject;
            var pem = doc?["public_key"]?.GetValue<string>() ?? string.Empty;
            var (spki, keyId) = SocialEventVerifier.FromPem(pem);
            if (spki.Length == 0 || keyId.Length == 0) return null;

            return Epingler(new Pin(keyId, spki, pem));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay social : émetteur injoignable.");
            return null;
        }
    }

    private Pin? Epingler(Pin nouvelle)
    {
        lock (_gate)
        {
            if (_pin is not null)
            {
                if (!string.Equals(_pin.KeyId, nouvelle.KeyId, StringComparison.Ordinal))
                    _logger.LogWarning(
                        "Replay social : la plateforme présente une AUTRE clé d'émetteur ({Neuve} au lieu de {Epinglee}). " +
                        "L'épinglage est conservé. Une rotation se fait en effaçant {Fichier}.",
                        Court(nouvelle.KeyId), Court(_pin.KeyId), Fichier);
                return _pin;
            }

            try
            {
                var doc = new JsonObject
                {
                    ["key_id"] = nouvelle.KeyId,
                    ["public_key"] = nouvelle.Pem,
                    ["learned_at"] = DateTime.UtcNow.ToString("O"),
                };
                Directory.CreateDirectory(_store.SocialRoot);
                var tmp = Fichier + ".tmp";
                File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, Fichier, true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Replay social : épinglage non enregistré."); }

            _pin = nouvelle;
            _lu = true;
            _logger.LogInformation("Replay social : clé d'émetteur épinglée ({KeyId}).", Court(nouvelle.KeyId));
            return _pin;
        }
    }

    private static string Court(string s) => s.Length > 12 ? s[..12] : s;
}
