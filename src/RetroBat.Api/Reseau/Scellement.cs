using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Reseau;

/// <summary>
/// L'ENVELOPPE SCELLEE (CDC infra §15.5, 2026-10-08).
///
/// Quand la borne ne joint pas le central, un noeud fait suivre ses requetes. Il ne doit lire ni le code de la
/// borne (il ouvre son compte) ni le code joueur du passeport : la borne chiffre pour le central seul. Une cle
/// ephemere par envoi, accord ECDH P-256 avec la cle de scellement que la carte publie, HKDF-SHA256, AES-256-GCM.
/// La reponse revient chiffree par une seconde cle tiree du meme accord : seule la borne l'ouvre, et le noeud ne
/// peut ni la lire ni la changer. Meme forme que app/Reseau/Scellement.php du central, verifiee par des vecteurs
/// communs.
/// </summary>
public static class Scellement
{
    public const string Genre = "nelfeplay-sealed";
    public const string GenreReponse = "nelfeplay-sealed-reply";
    public const string Algorithme = "ECDH_P256_HKDF_SHA256_A256GCM";
    private const string Etiquette = "nelfeplay-sealed/1";

    /// <summary>Une requete scellee : l'enveloppe a envoyer, son identifiant et la cle qui ouvrira la reponse.</summary>
    public sealed record Scellee(JsonObject Enveloppe, string Id, byte[] CleReponse);

    /// <summary>
    /// Scelle une requete pour le central. <paramref name="garder"/> : le noeud peut la garder et la faire suivre
    /// plus tard si le central ne repond pas (un passeport complet). <paramref name="resume"/> : ce qui peut se
    /// lire en route (jeu, score), authentifie avec le reste.
    /// </summary>
    public static Scellee Sceller(JsonObject requete, ClePublique cleDeScellement, bool garder = false, JsonObject? resume = null)
    {
        using var ephemere = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var central = ECDiffieHellman.Create();
        central.ImportSubjectPublicKeyInfo(cleDeScellement.SpkiDer, out _);
        var epk = ephemere.ExportSubjectPublicKeyInfo();
        var secret = ephemere.DeriveRawSecretAgreement(central.PublicKey);
        var (cleRequete, cleReponse) = Cles(secret, epk);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var pub = resume is null ? "" : Crypto.B64Url(Encoding.UTF8.GetBytes(resume.ToJsonString()));
        var iv = RandomNumberGenerator.GetBytes(12);
        var chiffre = Chiffrer(cleRequete, iv, Encoding.UTF8.GetBytes(requete.ToJsonString()),
            DonneesAssociees(id, cleDeScellement.KeyId, garder, pub));
        var enveloppe = new JsonObject
        {
            ["format"] = 1,
            ["kind"] = Genre,
            ["alg"] = Algorithme,
            ["to"] = cleDeScellement.KeyId,
            ["id"] = id,
            ["epk"] = Crypto.B64Url(epk),
            ["iv"] = Crypto.B64Url(iv),
            ["ct"] = Crypto.B64Url(chiffre),
            ["keep"] = garder,
            ["pub"] = pub,
        };
        return new Scellee(enveloppe, id, cleReponse);
    }

    /// <summary>Ouvre la reponse scellee du central ; null si elle n'est pas la reponse a cette requete ou a ete touchee.</summary>
    public static JsonObject? OuvrirReponse(JsonObject? reponse, string id, byte[] cleReponse)
    {
        try
        {
            if (reponse is null || (int?)reponse["format"] != 1 || (string?)reponse["kind"] != GenreReponse || (string?)reponse["id"] != id)
                return null;
            var iv = Crypto.FromB64Url((string?)reponse["iv"] ?? "");
            var chiffre = Crypto.FromB64Url((string?)reponse["ct"] ?? "");
            var clair = Dechiffrer(cleReponse, iv, chiffre, Encoding.UTF8.GetBytes(Etiquette + " reponse|" + id));
            return clair is null ? null : JsonNode.Parse(clair) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ce que fait le central : ouvrir une requete scellee avec la cle privee de scellement. Pour les essais et les
    /// vecteurs communs avec PHP.
    /// </summary>
    public static (JsonObject Requete, byte[] CleReponse, JsonObject? Resume)? Ouvrir(JsonObject enveloppe, ECDiffieHellman clePrivee)
    {
        try
        {
            if ((int?)enveloppe["format"] != 1 || (string?)enveloppe["kind"] != Genre || (string?)enveloppe["alg"] != Algorithme) return null;
            var id = (string?)enveloppe["id"] ?? "";
            var vers = (string?)enveloppe["to"] ?? "";
            var garder = (bool?)enveloppe["keep"] ?? false;
            var pub = (string?)enveloppe["pub"] ?? "";
            if (vers != Crypto.KeyId(clePrivee.ExportSubjectPublicKeyInfo())) return null;
            var epk = Crypto.FromB64Url((string?)enveloppe["epk"] ?? "");
            using var ephemere = ECDiffieHellman.Create();
            ephemere.ImportSubjectPublicKeyInfo(epk, out _);
            var (cleRequete, cleReponse) = Cles(clePrivee.DeriveRawSecretAgreement(ephemere.PublicKey), epk);
            var clair = Dechiffrer(cleRequete, Crypto.FromB64Url((string?)enveloppe["iv"] ?? ""),
                Crypto.FromB64Url((string?)enveloppe["ct"] ?? ""), DonneesAssociees(id, vers, garder, pub));
            if (clair is null || JsonNode.Parse(clair) is not JsonObject requete) return null;
            var resume = pub.Length == 0 ? null : JsonNode.Parse(Crypto.FromB64Url(pub)) as JsonObject;
            return (requete, cleReponse, resume);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Ce que fait le central : sceller la reponse (essais).</summary>
    public static JsonObject ScellerReponse(string id, byte[] cleReponse, JsonObject reponse)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        return new JsonObject
        {
            ["format"] = 1,
            ["kind"] = GenreReponse,
            ["id"] = id,
            ["iv"] = Crypto.B64Url(iv),
            ["ct"] = Crypto.B64Url(Chiffrer(cleReponse, iv, Encoding.UTF8.GetBytes(reponse.ToJsonString()),
                Encoding.UTF8.GetBytes(Etiquette + " reponse|" + id))),
        };
    }

    /// <summary>Les deux cles d'un accord : celle de la requete, celle de la reponse.</summary>
    public static (byte[] Requete, byte[] Reponse) Cles(byte[] secret, byte[] epkDer)
    {
        var sel = SHA256.HashData([.. Encoding.ASCII.GetBytes(Etiquette), .. epkDer]);
        return (
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, sel, Encoding.ASCII.GetBytes(Etiquette + " requete")),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, sel, Encoding.ASCII.GetBytes(Etiquette + " reponse")));
    }

    public static byte[] DonneesAssociees(string id, string vers, bool garder, string pub)
        => Encoding.UTF8.GetBytes($"{Etiquette}|{id}|{vers}|{(garder ? "1" : "0")}|{pub}");

    /// <summary>AES-256-GCM : le chiffre suivi de l'etiquette de 16 octets, comme openssl_encrypt cote PHP.</summary>
    private static byte[] Chiffrer(byte[] cle, byte[] iv, byte[] clair, byte[] donneesAssociees)
    {
        var sortie = new byte[clair.Length + 16];
        using var aes = new AesGcm(cle, 16);
        aes.Encrypt(iv, clair, sortie.AsSpan(0, clair.Length), sortie.AsSpan(clair.Length), donneesAssociees);
        return sortie;
    }

    private static byte[]? Dechiffrer(byte[] cle, byte[] iv, byte[] chiffre, byte[] donneesAssociees)
    {
        if (iv.Length != 12 || chiffre.Length < 17) return null;
        var clair = new byte[chiffre.Length - 16];
        try
        {
            using var aes = new AesGcm(cle, 16);
            aes.Decrypt(iv, chiffre.AsSpan(0, clair.Length), chiffre.AsSpan(clair.Length), clair, donneesAssociees);
            return clair;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
