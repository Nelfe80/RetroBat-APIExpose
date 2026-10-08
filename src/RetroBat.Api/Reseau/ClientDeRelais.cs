using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Reseau;

/// <summary>
/// LE RELAIS DES PARTIES, COTE BORNE (CDC infra §15.5 et §15.6, 2026-10-08).
///
/// Quand la borne ne joint pas le central, elle passe par les noeuds « relay » de sa carte : deux au hasard, le
/// plus rapide d'abord (moyenne glissante), un noeud en echec ecarte quelques minutes. La requete part scellee
/// pour le central (Scellement) : le noeud ne lit ni le code de la borne ni la partie, et la reponse revient
/// scellee pour la borne seule.
///
/// Un passeport complet peut etre GARDE par le noeud si le central ne repond pas : le noeud rend un recu signe par
/// sa cle et le fera suivre au retour du central. La borne vise deux hebergeurs, donc deux recus, et garde son
/// brouillon jusqu'au verdict signe quoi qu'il arrive.
/// </summary>
public sealed class ClientDeRelais
{
    public const string Route = "/relais/v1/envoi";
    public const string GenreDuRecu = "nelfeplay-relay-receipt";
    private static readonly TimeSpan Ecart = TimeSpan.FromMinutes(5);
    private const int EssaisAuPlus = 4;

    /// <summary>La reponse du central, ouverte : statut HTTP, corps, delai d'un 503.</summary>
    public sealed record Reponse(int Statut, string Corps, int? Delai, string Noeud);

    /// <summary>Le recu d'un noeud qui garde la partie, verifie avec sa cle.</summary>
    public sealed record Recu(string Noeud, string Hote, string Id, string Sha256, string RecuLe, string Enveloppe);

    /// <summary>Ce qu'a donne un envoi par les relais : une reponse du central, ou des recus de garde, ou rien.</summary>
    public sealed record Issue(Reponse? Reponse, IReadOnlyList<Recu> Recus, string Detail);

    private readonly IHttpClientFactory _http;
    private readonly ServiceDeCarte _cartes;
    private readonly ILogger<ClientDeRelais>? _logger;
    private readonly Func<DateTime> _maintenant;
    private readonly ConcurrentDictionary<string, DateTime> _ecartes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _latences = new(StringComparer.Ordinal);

    public ClientDeRelais(IHttpClientFactory http, ServiceDeCarte cartes, ILogger<ClientDeRelais>? logger = null)
        : this(http, cartes, logger, () => DateTime.UtcNow)
    {
    }

    internal ClientDeRelais(IHttpClientFactory http, ServiceDeCarte cartes, ILogger<ClientDeRelais>? logger, Func<DateTime> maintenant)
    {
        _http = http;
        _cartes = cartes;
        _logger = logger;
        _maintenant = maintenant;
    }

    /// <summary>Vrai si la carte permet de relayer : une cle de scellement et au moins un noeud « relay ».</summary>
    public bool Disponible => _cartes.Actuelle.CleDeScellement is not null && _cartes.Actuelle.AvecLeRole("relay").Any();

    /// <summary>
    /// L'ordre des essais : deux noeuds tires au hasard (selon leur poids), le plus rapide des deux d'abord, puis
    /// les autres au hasard. Un noeud ecarte (echec recent) passe en dernier.
    /// </summary>
    internal IReadOnlyList<NoeudDuReseau> Ordre(IReadOnlyList<NoeudDuReseau> relais)
    {
        var maintenant = _maintenant();
        var dispo = relais.Where(n => !_ecartes.TryGetValue(n.Url, out var jusqua) || jusqua <= maintenant).ToList();
        var ecartes = relais.Except(dispo).ToList();
        var tires = new List<NoeudDuReseau>();
        while (dispo.Count > 0)
        {
            var total = dispo.Sum(n => n.Poids);
            var tirage = RandomNumberGenerator.GetInt32(total);
            var choisi = dispo.First(n => (tirage -= n.Poids) < 0);
            tires.Add(choisi);
            dispo.Remove(choisi);
        }
        var tete = tires.Take(2).OrderBy(n => _latences.TryGetValue(n.Url, out var ms) ? ms : 0).ToList();
        return [.. tete, .. tires.Skip(2), .. ecartes];
    }

    /// <summary>
    /// Envoie une requete de l'agent par les relais. <paramref name="garder"/> : un passeport complet, que le noeud
    /// peut garder si le central ne repond pas (deux recus d'hebergeurs differents sont cherches).
    /// </summary>
    public async Task<Issue> EnvoyerAsync(string methode, string chemin, string? query, string? corps, string credential,
        string? label, bool garder, JsonObject? resume, CancellationToken ct)
    {
        var carte = _cartes.Actuelle;
        var scellement = carte.CleDeScellement;
        var relais = carte.AvecLeRole("relay").ToList();
        if (scellement is null || relais.Count == 0) return new Issue(null, [], "aucun relais dans la carte");

        var requete = new JsonObject
        {
            ["method"] = methode,
            ["path"] = chemin,
            ["query"] = query ?? "",
            ["device"] = credential,
            ["label"] = label ?? "",
            ["body"] = corps,
            ["sent_at"] = _maintenant().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        var scellee = Scellement.Sceller(requete, scellement, garder, garder ? resume : null);
        var octets = Encoding.UTF8.GetBytes(scellee.Enveloppe.ToJsonString());
        var empreinte = Crypto.Sha256Hex(octets);

        var recus = new List<Recu>();
        var echecs = new List<string>();
        var client = _http.CreateClient(nameof(ClientDeRelais));
        client.Timeout = TimeSpan.FromSeconds(60);
        foreach (var noeud in Ordre(relais).Take(EssaisAuPlus))
        {
            // Deux recus d'un meme hebergeur n'en valent qu'un.
            if (recus.Any(r => r.Hote == noeud.Hote && noeud.Hote.Length > 0)) continue;
            var chrono = Stopwatch.StartNew();
            try
            {
                using var contenu = new ByteArrayContent(octets);
                contenu.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var reponse = await client.PostAsync(noeud.Url + Route, contenu, ct).ConfigureAwait(false);
                var texte = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Noter(noeud, chrono.Elapsed);
                var json = Lire(texte);
                if (reponse.StatusCode == HttpStatusCode.OK && json?["reply"] is JsonObject scelleeRetour)
                {
                    var ouverte = Scellement.OuvrirReponse(scelleeRetour, scellee.Id, scellee.CleReponse);
                    if (ouverte is null)
                    {
                        // Une reponse qui ne s'ouvre pas : le noeud l'a touchee, ou ce n'est pas le central.
                        Ecarter(noeud, TimeSpan.FromHours(1));
                        echecs.Add($"{noeud.Nom} : reponse qui ne s'ouvre pas");
                        continue;
                    }
                    var statut = (int?)ouverte["status"] ?? 0;
                    var delai = ouverte["retry_after"] is JsonValue d && d.TryGetValue<int>(out var s) ? s : (int?)null;
                    return new Issue(new Reponse(statut, (string?)ouverte["body"] ?? "", delai, noeud.Nom), recus, $"par {noeud.Nom}");
                }
                if (reponse.StatusCode == HttpStatusCode.Accepted && garder && LireLeRecu(json?["receipt"] as JsonObject, noeud, scellee.Id, empreinte) is { } recu)
                {
                    recus.Add(recu);
                    if (recus.Select(r => r.Hote).Distinct().Count() >= 2) break;
                    continue;
                }
                var retry = reponse.Headers.RetryAfter?.Delta;
                Ecarter(noeud, retry is { } r && r > Ecart ? r : Ecart);
                echecs.Add($"{noeud.Nom} : HTTP {(int)reponse.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Ecarter(noeud, Ecart);
                echecs.Add($"{noeud.Nom} : {ex.GetType().Name}");
            }
        }
        var detail = recus.Count > 0
            ? $"partie gardee par {string.Join(", ", recus.Select(r => r.Noeud))}"
            : (echecs.Count == 0 ? "aucun relais joignable" : string.Join(" ; ", echecs));
        _logger?.LogInformation("Relais : {Chemin} sans reponse du central ({Detail}).", chemin, detail);
        return new Issue(null, recus, detail);
    }

    /// <summary>Le recu signe d'un noeud qui garde la partie : sa cle, cette enveloppe, ces octets.</summary>
    internal static Recu? LireLeRecu(JsonObject? enveloppe, NoeudDuReseau noeud, string id, string empreinte)
    {
        if (noeud.Cle is null) return null;
        var recu = EnveloppeSignee.Document(EnveloppeSignee.Ouvrir(enveloppe, [noeud.Cle]), GenreDuRecu);
        if (recu is null || (string?)recu["id"] != id || (string?)recu["sha256"] != empreinte) return null;
        return new Recu(noeud.Nom, noeud.Hote, id, empreinte, (string?)recu["received_at"] ?? "", enveloppe!.ToJsonString());
    }

    private static JsonObject? Lire(string texte)
    {
        try
        {
            return JsonNode.Parse(texte) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Noter(NoeudDuReseau noeud, TimeSpan duree)
        => _latences.AddOrUpdate(noeud.Url, duree.TotalMilliseconds, (_, avant) => 0.7 * avant + 0.3 * duree.TotalMilliseconds);

    private void Ecarter(NoeudDuReseau noeud, TimeSpan duree) => _ecartes[noeud.Url] = _maintenant() + duree;
}
