using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LA SYNCHRO POST-APOCALYPSE (regle user 2026-10-05 : « les parties ne devraient jamais etre
/// perdues »).
///
/// Quand le site restaure sa base, il perd ce qui est arrive depuis la sauvegarde. La borne, elle,
/// garde le passeport signe de chacune de ses parties (state/nelfeplay/certified). Le site arme un
/// EPISODE de recuperation : une epoque, et la fenetre des parties perdues (de l'heure de la
/// sauvegarde, moins une marge, a l'heure de l'armement). A chaque episode nouveau, la borne renvoie
/// ses parties de la fenetre, et seulement elles ; un renvoi ne vaut que sur un vrai verdict.
///
/// Une borne inscrite apres la sauvegarde n'existe plus pour le site : elle se fait reconnaitre en
/// signant une preuve avec la cle de son appareil (voir le site, RecoveryEpisode).
/// </summary>
public static class RecuperationDesParties
{
    /// <summary>Ce que la borne signe pour reprendre son identite. Identique cote site.</summary>
    public const string ButDeLaPreuve = "nelfeplay.recovery.reidentify";

    /// <summary>La marge apres l'armement : l'horloge d'une borne peut avancer.</summary>
    public static readonly TimeSpan MargeApresArmement = TimeSpan.FromHours(1);

    /// <summary>
    /// La fenetre des parties a renvoyer. Sans bornes (ancien armement, sans heure de sauvegarde),
    /// tout repart, comme avant.
    /// </summary>
    public sealed record Fenetre(DateTime? Depuis, DateTime? Jusqua)
    {
        public static readonly Fenetre Tout = new(null, null);

        public bool Contient(DateTime? quand)
        {
            if (quand is not { } q) return Depuis is null && Jusqua is null;
            return (Depuis is not { } d || q >= d) && (Jusqua is not { } j || q <= j);
        }
    }

    /// <summary>La fenetre que donne le statut de recuperation du site.</summary>
    public static Fenetre LireLaFenetre(JsonObject? statut)
    {
        var depuis = Heure((string?)statut?["since"]);
        var arme = Heure((string?)statut?["armed_at"]);
        if (depuis is null) return Fenetre.Tout;
        return new Fenetre(depuis, arme is { } a ? a + MargeApresArmement : null);
    }

    /// <summary>
    /// L'heure ou le site a recu la partie : celle de son verdict, notee par la borne. A defaut,
    /// l'heure de fin de la partie dans le passeport.
    /// </summary>
    public static DateTime? HeureDuRecord(JsonObject record)
        => Heure((string?)record["submitted_at"]) ?? Heure((string?)record["passport"]?["timing"]?["ended_at"]);

    /// <summary>Ce que vaut la reponse du site a un renvoi.</summary>
    public enum Issue
    {
        /// <summary>Un verdict : le site a la partie, ou la refuse pour de bon.</summary>
        Fait,
        /// <summary>Le site ne repond pas, ou n'est pas en etat : on reessaiera.</summary>
        ARetenter,
        /// <summary>Le site ne connait pas la cle de l'appareil : la reinscrire, puis renvoyer.</summary>
        CleInconnue,
        /// <summary>Le site ne connait plus ce secret (401) : la borne doit se faire reconnaitre.</summary>
        BorneInconnue,
        /// <summary>La partie est d'une autre identite de la borne : essayer un autre secret.</summary>
        AutreIdentite,
    }

    public static Issue Classer(int statutHttp, string? corps)
    {
        if (statutHttp == 401) return Issue.BorneInconnue;
        if (statutHttp is 400 or 409 or 422) return Issue.Fait;
        if (statutHttp is < 200 or >= 300) return Issue.ARetenter;
        JsonObject? json;
        try { json = JsonNode.Parse(corps ?? "") as JsonObject; }
        catch (JsonException) { return Issue.ARetenter; }
        if (json is null) return Issue.ARetenter;
        if ((string?)json["status"] != "refused") return Issue.Fait;
        return (string?)json["reason"] switch
        {
            "session.device_unknown" => Issue.CleInconnue,
            "session.device_mismatch" => Issue.AutreIdentite,
            "server.unavailable" or "submission.write_failed" => Issue.ARetenter,
            _ => Issue.Fait,
        };
    }

    /// <summary>
    /// Le passeport qui prouve qui etait la borne pour cet appareil : le plus recent qui porte un
    /// ticket de la plateforme nommant cet appareil.
    /// </summary>
    public static JsonObject? PasseportDeLAppareil(IEnumerable<JsonObject> records, string deviceId)
        => records
            .Select(r => (Record: r, Passeport: r["passport"] as JsonObject))
            .Where(x => x.Passeport?["ticket"]?["signature"] is not null
                && (string?)x.Passeport["ticket"]?["device_id"] == deviceId
                && (string?)x.Passeport["device"]?["device_id"] == deviceId)
            .OrderByDescending(x => HeureDuRecord(x.Record) ?? DateTime.MinValue)
            .Select(x => x.Passeport)
            .FirstOrDefault();

    /// <summary>Le message signe par la cle de l'appareil. Doit rester identique a celui du site.</summary>
    public static byte[] MessageDePreuve(string deviceId, string credentialSha256, string epoch)
        => Jcs.CanonicalBytes(new JsonObject
        {
            ["purpose"] = ButDeLaPreuve,
            ["device_id"] = deviceId,
            ["credential_sha256"] = credentialSha256,
            ["epoch"] = epoch,
        });

    public static string Sha256Hex(string texte)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(texte))).ToLowerInvariant();

    private static DateTime? Heure(string? texte)
        => DateTime.TryParse(texte, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var h) ? h : null;
}
