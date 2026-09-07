using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Replay.Social;

/// <summary>
/// Un événement social signé (LOT R9 ; CDC §58, §70, CDC DEV §39).
///
/// Il se transporte en trois morceaux — <c>event_id</c>, <c>body</c>, <c>signature</c> — et seul
/// le CORPS est signé. On le garde ici tel qu'il est arrivé, en <see cref="JsonObject"/> : le
/// recanonicaliser (JCS) avant de vérifier rend l'ordre des clés au transport indifférent, et
/// permet à un champ ajouté plus tard de traverser une version ancienne sans casser sa signature.
///
/// C'est ce qui change tout par rapport à la remontée d'avant : la borne n'a plus à croire celui
/// qui lui parle. Elle vérifie, donc elle peut accepter une réaction venue de n'importe quel pair.
/// </summary>
public sealed class SocialEvent
{
    public string EventId { get; }
    public JsonObject Body { get; }
    public string Signature { get; }

    private SocialEvent(string eventId, JsonObject body, string signature)
    {
        EventId = eventId; Body = body; Signature = signature;
    }

    /// <summary>Lit un événement au format de transport, sans rien vérifier encore.</summary>
    public static SocialEvent? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        if (o["body"] is not JsonObject body) return null;
        var id = o["event_id"]?.GetValue<string>() ?? string.Empty;
        var sig = o["signature"]?.GetValue<string>() ?? string.Empty;
        if (sig.Length == 0) return null;
        // Copie profonde : l'objet reçu peut appartenir à un document que l'appelant va libérer.
        return new SocialEvent(id, (JsonObject)body.DeepClone(), sig);
    }

    public JsonObject ToTransport() => new()
    {
        ["event_id"] = EventId,
        ["body"] = (JsonObject)Body.DeepClone(),
        ["signature"] = Signature,
    };

    public string Kind => Texte("kind");
    public string TargetId => Texte("target_id");
    public string Actor => Texte("actor");
    public string ObjectSha256 => Texte("object_sha256");
    public string Reaction => Texte("reaction");
    public string IssuerKeyId => Texte("issuer_key_id");
    public string ModeratedEventId => Texte("target_event_id");
    public long Frame => Entier("frame");
    public long SessionSeq => Entier("session_seq");
    public int Level => (int)Math.Clamp(Entier("level"), 1, 255);

    private string Texte(string cle)
    {
        try { return Body[cle]?.GetValue<string>() ?? string.Empty; } catch { return string.Empty; }
    }

    private long Entier(string cle)
    {
        try { return Body[cle]?.GetValue<long>() ?? 0; } catch { return 0; }
    }
}

/// <summary>
/// Vérification d'un événement social. Mêmes règles, mêmes codes de refus que l'implémentation
/// PHP de la plateforme : les deux côtés doivent trancher pareil, sinon un événement accepté ici
/// serait refusé ailleurs, et l'ensemble cesserait de converger.
/// </summary>
public static class SocialEventVerifier
{
    public const string Schema = "nelfe.social.event.v1";
    public const string KindReaction = "reaction";
    public const string KindModeration = "moderation";

    private static readonly Regex Hex32 = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex Famille = new("^[a-z][a-z0-9_]{0,23}$", RegexOptions.Compiled);

    /// <summary>
    /// Chaîne vide = l'événement est bon. Sinon le code du refus.
    ///
    /// <paramref name="expectedKeyId"/> est le cœur du dispositif : n'importe qui peut fabriquer
    /// une paire de clés et signer ce qu'il veut. Ce qui distingue un événement authentique n'est
    /// pas d'être signé, c'est d'être signé par CETTE clé-là.
    /// </summary>
    public static string Check(SocialEvent e, byte[] issuerSpkiDer, string expectedKeyId)
    {
        var forme = CheckBody(e.Body);
        if (forme.Length > 0) return forme;

        if (expectedKeyId.Length > 0
            && !string.Equals(expectedKeyId, e.IssuerKeyId, StringComparison.Ordinal))
            return "issuer_unexpected";

        var canonique = Jcs.Canonical(e.Body);
        var octets = Encoding.UTF8.GetBytes(canonique);
        var attendu = Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

        // L'identifiant annoncé n'est pas cru sur parole : il DOIT tomber sur le corps reçu, sans
        // quoi un événement pourrait se faire passer pour un autre dans un index.
        if (e.EventId.Length > 0 && !string.Equals(attendu, e.EventId, StringComparison.Ordinal))
            return "event_id_mismatch";

        if (issuerSpkiDer.Length == 0) return "issuer_key_missing";
        return Crypto.Verify(issuerSpkiDer, octets, e.Signature) ? string.Empty : "signature_invalid";
    }

    /// <summary>Forme du corps, avant toute crypto : ce qui n'a pas la bonne forme ne mérite pas
    /// qu'on dépense une vérification de signature dessus.</summary>
    public static string CheckBody(JsonObject body)
    {
        string S(string cle)
        {
            try { return body[cle]?.GetValue<string>() ?? string.Empty; } catch { return string.Empty; }
        }
        long? N(string cle)
        {
            try { return body[cle]?.GetValue<long>(); } catch { return null; }
        }

        if (S("schema") != Schema) return "schema_unknown";
        if (S("target_type") != "replay") return "target_type_unknown";
        var cible = S("target_id");
        if (cible.Length is 0 or > 64) return "target_id_invalid";
        if (!Hex64.IsMatch(S("issuer_key_id"))) return "issuer_key_id_invalid";
        var date = S("issued_at");
        if (date.Length is < 20 or > 32) return "issued_at_invalid";

        var kind = S("kind");
        if (kind == KindReaction)
        {
            if (!Hex32.IsMatch(S("actor"))) return "actor_invalid";
            if (!Famille.IsMatch(S("reaction"))) return "reaction_invalid";
            if (N("frame") is not >= 0) return "frame_invalid";
            if (N("level") is not (>= 1 and <= 255)) return "level_invalid";
            if (N("session_seq") is not >= 0) return "session_seq_invalid";
            var objet = S("object_sha256");
            if (objet.Length > 0 && !Hex64.IsMatch(objet)) return "object_sha256_invalid";
            return string.Empty;
        }

        if (kind == KindModeration)
        {
            // Un retrait ne réécrit RIEN : il produit son propre événement, qui désigne celui
            // qu'il retire (§58). L'original reste vérifiable, et la trace de son retrait aussi.
            if (!Hex64.IsMatch(S("target_event_id"))) return "target_event_id_invalid";
            var raison = S("reason");
            if (raison.Length is 0 or > 48) return "reason_invalid";
            return string.Empty;
        }

        return "kind_unknown";
    }

    /// <summary>SPKI DER d'une clé publique PEM, et son empreinte (= key_id du protocole).</summary>
    public static (byte[] Spki, string KeyId) FromPem(string pem)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            var spki = ec.ExportSubjectPublicKeyInfo();
            return (spki, Crypto.KeyId(spki));
        }
        catch { return (Array.Empty<byte>(), string.Empty); }
    }
}
