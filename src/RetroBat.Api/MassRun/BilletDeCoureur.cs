using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.MassRun;

/// <summary>
/// LE BILLET DE COUREUR (05/11, cote MASS RUN, D33 a D36 ; CDC-MassRun-cote-protocole §1) : a la jonction d'une salle, la borne
/// signe avec sa cle de scoring (celle des passeports) qui elle est, dans quelle salle, et avec quel coeur hote. Le noeud le
/// garde tel quel et le joint au resultat signe de la salle ; le central retrouve le compte par l'appareil et ne croit pas
/// le noeud sur l'identite des coureurs. Une borne non liee (pas d'appareil) n'en envoie pas : elle court, sans cote.
/// </summary>
public static class BilletDeCoureur
{
    public const string Genre = "nelfeplay-massrun-runner";

    /// <summary>Le corps signe. Pur.</summary>
    public static JsonObject Corps(uint salle, string jeu, string niveau, string pseudo, string appareil, string cle, string? coeurSha256,
        DateTime maintenant)
        => new()
        {
            ["format"] = 1,
            ["kind"] = Genre,
            ["room"] = salle,
            ["game"] = jeu,
            ["level"] = niveau,
            ["pseudo"] = pseudo,
            ["device_id"] = appareil,
            ["key_id"] = cle,
            ["core_sha256"] = coeurSha256,
            ["signed_at"] = maintenant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>Le billet signe, ou null sans appareil lie ou sans cle.</summary>
    [SupportedOSPlatform("windows")]
    public static JsonObject? Signer(string? appareil, uint salle, string jeu, string niveau, string pseudo, string? coeurSha256)
    {
        if (string.IsNullOrWhiteSpace(appareil)) return null;
        using var cle = CngDeviceKey.OpenOrCreate(RetroBat.Api.Infrastructure.NelfePlayScoringReporter.ScoringKeyName);
        var corps = Corps(salle, jeu, niveau, pseudo, appareil, cle.KeyId, coeurSha256, DateTime.UtcNow);
        return new JsonObject { ["body"] = corps, ["signature"] = cle.SignB64Url(Jcs.CanonicalBytes(corps)) };
    }

    /// <summary>L'empreinte de la dll du coeur hote installee (celle que RetroArch chargera), ou null.</summary>
    public static string? EmpreinteDuCoeur(string coeur)
    {
        try
        {
            var chemin = Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores", coeur + "_libretro.dll");
            if (!File.Exists(chemin)) return null;
            using var flux = File.OpenRead(chemin);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(flux)).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
