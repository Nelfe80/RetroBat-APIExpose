using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Reseau;

/// <summary>
/// Une cle publique ECDSA P-256 du reseau NelfePlay : son SPKI DER et son identifiant (SHA-256 du SPKI, en
/// hexadecimal minuscule), comme les cles des bornes, des noeuds et du central.
/// </summary>
public sealed record ClePublique(string KeyId, byte[] SpkiDer, string Pem)
{
    /// <summary>La cle d'un PEM (« BEGIN PUBLIC KEY »), ou null si ce n'est pas une cle P-256.</summary>
    public static ClePublique? DepuisPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            var parametres = ec.ExportParameters(false);
            if (parametres.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value) return null;
            var spki = ec.ExportSubjectPublicKeyInfo();
            return new ClePublique(Crypto.KeyId(spki), spki, pem.Replace("\r\n", "\n"));
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    public bool Verifie(byte[] message, string signatureB64Url) => Crypto.Verify(SpkiDer, message, signatureB64Url);
}

/// <summary>
/// L'ENVELOPPE SIGNEE DU RESEAU (CDC infra §15.1) : les octets d'un document tels quels (en base64url), leur
/// signature ECDSA P-256 DER et l'identifiant de la cle. Rien a remettre en forme entre langages : on decode,
/// on verifie, puis on lit. C'est la forme des verdicts et de la carte du central, et des messages des noeuds.
/// </summary>
public static class EnveloppeSignee
{
    public const string Algorithme = "ECDSA_P256_SHA256_DER";

    /// <summary>
    /// Les octets d'une enveloppe si l'une de ces cles l'a signee (celle que nomme son key_id), sinon null.
    /// </summary>
    public static byte[]? Ouvrir(JsonObject? enveloppe, IEnumerable<ClePublique> cles)
    {
        if (enveloppe is null) return null;
        try
        {
            if ((int?)enveloppe["format"] != 1 || (string?)enveloppe["alg"] != Algorithme) return null;
            var keyId = (string?)enveloppe["key_id"];
            var charge = (string?)enveloppe["payload"];
            var signature = (string?)enveloppe["signature"];
            if (string.IsNullOrEmpty(keyId) || charge is null || string.IsNullOrEmpty(signature)) return null;
            var cle = cles.FirstOrDefault(c => string.Equals(c.KeyId, keyId, StringComparison.Ordinal));
            if (cle is null) return null;
            var octets = Crypto.FromB64Url(charge);
            return cle.Verifie(octets, signature) ? octets : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    /// <summary>La meme chose depuis le texte JSON de l'enveloppe.</summary>
    public static byte[]? Ouvrir(string? enveloppe, IEnumerable<ClePublique> cles)
    {
        if (string.IsNullOrWhiteSpace(enveloppe)) return null;
        try
        {
            return Ouvrir(JsonNode.Parse(enveloppe) as JsonObject, cles);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Le document JSON d'une enveloppe ouverte, s'il dit etre de ce genre (« kind »).</summary>
    public static JsonObject? Document(byte[]? octets, string genre)
    {
        if (octets is null) return null;
        try
        {
            return JsonNode.Parse(octets) is JsonObject document && (string?)document["kind"] == genre ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Une enveloppe signee par cette cle : pour les essais (le central et les noeuds signent eux-memes).</summary>
    public static JsonObject Signer(byte[] octets, ECDsa cle)
    {
        var spki = cle.ExportSubjectPublicKeyInfo();
        return new JsonObject
        {
            ["format"] = 1,
            ["alg"] = Algorithme,
            ["key_id"] = Crypto.KeyId(spki),
            ["payload"] = Crypto.B64Url(octets),
            ["signature"] = Crypto.SignB64Url(cle, octets),
        };
    }
}
