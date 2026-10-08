using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Reseau;

/// <summary>
/// LE VERDICT SIGNE DU CENTRAL (CDC infra §15.3, 2026-10-08).
///
/// La borne garde chaque partie jusqu'a un verdict signe. La reponse a un passeport porte, a cote de ses champs,
/// le meme verdict dans l'enveloppe signee du reseau, par une cle de verdict que la carte nomme. Le verdict dit
/// la session, l'appareil et l'empreinte des octets du passeport recus : la borne y reconnait la reponse a CE
/// qu'elle a envoye. Un verdict qui ne se verifie pas ne vaut rien : la partie reste en file.
/// </summary>
public static class VerdictSigne
{
    public const string Genre = "nelfeplay-verdict";

    public sealed record Lecture(bool Valide, JsonObject? Contenu, string Raison);

    /// <summary>Verifie le verdict signe que porte une reponse au passeport <paramref name="corpsEnvoye"/>.</summary>
    public static Lecture Verifier(string? corpsDeReponse, IReadOnlyCollection<ClePublique> cles, string sessionId, string deviceId, byte[] corpsEnvoye)
    {
        JsonObject? reponse;
        try
        {
            reponse = string.IsNullOrWhiteSpace(corpsDeReponse) ? null : JsonNode.Parse(corpsDeReponse) as JsonObject;
        }
        catch (JsonException)
        {
            reponse = null;
        }
        if (reponse?["verdict"] is not JsonObject enveloppe) return new(false, null, "verdict_absent");
        var keyId = (string?)enveloppe["key_id"] ?? "";
        if (!cles.Any(c => c.KeyId == keyId)) return new(false, null, "cle_inconnue:" + (keyId.Length > 12 ? keyId[..12] : keyId));
        var contenu = EnveloppeSignee.Document(EnveloppeSignee.Ouvrir(enveloppe, cles), Genre);
        if (contenu is null) return new(false, null, "signature_invalide");
        if ((string?)contenu["session_id"] != sessionId) return new(false, null, "autre_session");
        if ((string?)contenu["device_id"] != deviceId) return new(false, null, "autre_appareil");
        if ((string?)contenu["body_sha256"] != Crypto.Sha256Hex(corpsEnvoye)) return new(false, null, "autre_passeport");
        return new(true, contenu, "");
    }

    /// <summary>
    /// La reponse avec les champs du verdict signe a la place des champs en clair (statut, raison, rang, verdict
    /// d'origine) : c'est le verdict signe qui fait foi. Les autres champs (code de reclamation) restent.
    /// </summary>
    public static string AvecLesChampsSignes(string corpsDeReponse, JsonObject contenu)
    {
        if (JsonNode.Parse(corpsDeReponse) is not JsonObject reponse) return corpsDeReponse;
        foreach (var champ in new[] { "status", "reason", "rank", "original_status", "original_reason" })
        {
            reponse[champ] = contenu[champ]?.DeepClone();
        }
        reponse["verdict_signe"] = true;
        return reponse.ToJsonString();
    }
}
