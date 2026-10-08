using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Reseau;

/// <summary>Un noeud de la carte : son adresse, sa cle, ses roles (« front », « relay », « replay ») et son poids.</summary>
public sealed record NoeudDuReseau(
    int Id, string Nom, string Genre, string Url, ClePublique? Cle, IReadOnlyList<string> Roles,
    string Region, string Pays, string Hote, int Poids)
{
    public bool A(string role) => Roles.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// LA CARTE DU RESEAU (CDC infra §3.5 et §15.4, 2026-10-08). Ce que la borne sait du reseau : le central, la
/// cle qui scelle ses requetes quand elles passent par un noeud, les cles qui signent les verdicts, et les noeuds
/// actifs avec leurs roles. Elle est signee par la cle du site statique, que le central garde hors de PHP et que
/// la borne epingle : une cle de service se change en publiant une carte, sans nouvelle version d'APIExpose.
///
/// Une carte ne remplace que plus ancienne qu'elle ; elle n'expire pas (si le central tombe, la borne continue
/// avec la derniere). La carte par defaut, livree avec la borne : le central seul, avec ses cles du moment.
/// </summary>
public sealed class CarteDuReseau
{
    public const string Genre = "nelfeplay-map";
    public const string Chemin = "/.well-known/nelfeplay-carte.json";
    public static readonly string[] RolesConnus = ["front", "relay", "replay"];

    /// <summary>
    /// La cle du site statique (key_id ac42342a…0930bacd), epinglee : elle signe le manifeste des miroirs et la
    /// carte. Root seul la tient sur le serveur ; la meme est epinglee dans NelfeNode.
    /// </summary>
    public const string CleDuSiteStatiquePem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERcp8Inm7X8xEQtPMu02Z+5oP7GEt\n" +
        "v3LShT3Q0vp12DZdQk+WOQeJySMKz1OSfR5A/VoWraaiH89dL4uQFPpTLA==\n" +
        "-----END PUBLIC KEY-----\n";

    /// <summary>
    /// La cle de l'emetteur du central (key_id bcd5ab3c…a184) : tickets, index public, evenements sociaux et
    /// verdicts. Celle de la carte par defaut ; la carte publiee peut en ajouter ou la remplacer.
    /// </summary>
    public const string CleDeLEmetteurPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGN5kF94xFnb/9HwS4RBA23LlDvht\n" +
        "kfkp9ZnyY8XOOVrYiA0VYmCMSGSvQYSE2QCmTmMpvxVvmoLFHdUnzXyHlg==\n" +
        "-----END PUBLIC KEY-----\n";

    /// <summary>
    /// La cle de scellement du central (config/relay-seal.key.pem, key_id df7a93aa…49ccb, nee le 2026-10-08) : elle
    /// ouvre les requetes que la borne fait passer par un relais. La carte publiee peut la remplacer.
    /// </summary>
    public const string CleDeScellementPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEAhMpyl1T0Cpwnh20miyE8YvF7T/H\n" +
        "nB0C1O4C2r+SLKuTetqERJbN8x+rABPxEjHGdyTR4jwERZRiXGIN2dPJ/g==\n" +
        "-----END PUBLIC KEY-----\n";

    private static readonly Regex AdresseValide = new(@"\Ahttps?://[A-Za-z0-9.-]+(?::[0-9]{1,5})?\z", RegexOptions.CultureInvariant);

    private CarteDuReseau(long version, string emiseLe, string urlDuCentral, ClePublique? cleDeScellement,
        IReadOnlyList<ClePublique> clesDeVerdict, IReadOnlyList<ClePublique> clesSociales, int copies,
        IReadOnlyList<NoeudDuReseau> noeuds, string? enveloppe)
    {
        Version = version;
        EmiseLe = emiseLe;
        UrlDuCentral = urlDuCentral;
        CleDeScellement = cleDeScellement;
        ClesDeVerdict = clesDeVerdict;
        ClesSociales = clesSociales;
        Copies = copies;
        Noeuds = noeuds;
        Enveloppe = enveloppe;
    }

    public long Version { get; }
    public string EmiseLe { get; }
    public string UrlDuCentral { get; }
    public ClePublique? CleDeScellement { get; }
    public IReadOnlyList<ClePublique> ClesDeVerdict { get; }

    /// <summary>
    /// Les cles qui relisent les evenements sociaux (CDC infra §15.10) : celles des verdicts, plus les cles
    /// d'emetteur retirees. Un evenement garde la signature de son jour ; une cle retiree ne signe plus rien de
    /// neuf, elle ne sert qu'a relire.
    /// </summary>
    public IReadOnlyList<ClePublique> ClesSociales { get; }
    public int Copies { get; }
    public IReadOnlyList<NoeudDuReseau> Noeuds { get; }

    /// <summary>L'enveloppe signee telle que recue (null pour la carte par defaut) : c'est elle qu'on garde sur disque.</summary>
    public string? Enveloppe { get; }

    public IEnumerable<NoeudDuReseau> AvecLeRole(string role) => Noeuds.Where(n => n.A(role));

    public static ClePublique CleDuSiteStatique { get; } = ClePublique.DepuisPem(CleDuSiteStatiquePem)
        ?? throw new InvalidOperationException("Cle du site statique illisible.");

    /// <summary>La carte livree avec la borne : le central seul, avec ses cles du moment.</summary>
    public static CarteDuReseau ParDefaut { get; } = new(
        0, "", "https://nelfeplay.com", ClePublique.DepuisPem(CleDeScellementPem),
        [ClePublique.DepuisPem(CleDeLEmetteurPem) ?? throw new InvalidOperationException("Cle de l'emetteur illisible.")],
        [ClePublique.DepuisPem(CleDeLEmetteurPem) ?? throw new InvalidOperationException("Cle de l'emetteur illisible.")],
        2, [], null);

    /// <summary>Une carte signee par cette cle (celle du site statique), ou null.</summary>
    public static CarteDuReseau? Ouvrir(string? enveloppe, ClePublique cleDuSite)
    {
        var document = EnveloppeSignee.Document(EnveloppeSignee.Ouvrir(enveloppe, [cleDuSite]), Genre);
        return document is null ? null : Lire(document, enveloppe!.Trim());
    }

    /// <summary>
    /// Lit une carte deja verifiee. Un noeud mal decrit est ignore ; une carte sans central, sans version ou sans
    /// cle de verdict ne vaut rien (null).
    /// </summary>
    internal static CarteDuReseau? Lire(JsonObject carte, string? enveloppe)
    {
        try
        {
            if ((int?)carte["format"] != 1 || carte["version"] is not JsonValue v || !v.TryGetValue<long>(out var version) || version <= 0)
                return null;
            if (carte["central"] is not JsonObject central) return null;
            var url = ((string?)central["url"] ?? "").TrimEnd('/');
            if (!AdresseValide.IsMatch(url)) return null;
            var verdicts = (central["verdict_keys"] as JsonArray ?? [])
                .Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var pem) ? ClePublique.DepuisPem(pem) : null)
                .OfType<ClePublique>().ToList();
            if (verdicts.Count == 0) return null;
            // Les cles sociales : la liste de la carte, sinon celles des verdicts ; celles des verdicts y sont toujours.
            var sociales = (central["social_keys"] as JsonArray ?? [])
                .Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var pem) ? ClePublique.DepuisPem(pem) : null)
                .OfType<ClePublique>().Concat(verdicts).GroupBy(c => c.KeyId).Select(g => g.First()).ToList();
            var scellement = ClePublique.DepuisPem((string?)central["seal_key"]);
            if (scellement is not null && scellement.KeyId != (string?)central["seal_key_id"]) scellement = null;
            var copies = (carte["replay"] as JsonObject)?["copies"] is JsonValue c && c.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 5) : 2;

            var noeuds = new List<NoeudDuReseau>();
            foreach (var element in carte["nodes"] as JsonArray ?? [])
            {
                if (element is not JsonObject noeud) continue;
                var adresse = ((string?)noeud["url"] ?? "").TrimEnd('/');
                if (!AdresseValide.IsMatch(adresse) || noeud["id"] is not JsonValue idv || !idv.TryGetValue<int>(out var id)) continue;
                var genre = (string?)noeud["kind"] ?? "";
                var cle = ClePublique.DepuisPem((string?)noeud["public_key"]);
                if (genre == "node" && (cle is null || cle.KeyId != (string?)noeud["key_id"])) continue;
                var roles = (noeud["roles"] as JsonArray ?? [])
                    .Select(r => (string?)r).Where(r => r is not null && RolesConnus.Contains(r)).Select(r => r!).Distinct().ToList();
                // Un miroir statique (GitHub Pages) sert le site ; il ne relaie ni ne garde rien.
                if (genre != "node") roles = roles.Where(r => r == "front").ToList();
                if (roles.Count == 0) continue;
                var poids = noeud["weight"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? Math.Clamp(p, 1, 100) : 1;
                noeuds.Add(new NoeudDuReseau(id, (string?)noeud["name"] ?? "", genre, adresse, genre == "node" ? cle : null, roles,
                    (string?)noeud["region"] ?? "", (string?)noeud["country"] ?? "", (string?)noeud["host"] ?? "", poids));
            }

            return new CarteDuReseau(version, (string?)carte["issued_at"] ?? "", url, scellement, verdicts, sociales, copies, noeuds, enveloppe);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Le resume d'une ligne, pour les journaux.</summary>
    public override string ToString()
    {
        var texte = new StringBuilder($"carte {Version}");
        foreach (var role in RolesConnus)
        {
            var nombre = Noeuds.Count(n => n.A(role));
            if (nombre > 0) texte.Append($", {role} {nombre}");
        }
        return texte.ToString();
    }
}
