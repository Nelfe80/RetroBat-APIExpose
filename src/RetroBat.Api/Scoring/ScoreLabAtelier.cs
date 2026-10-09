using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Scoring;

/// <summary>
/// L'atelier de NelfeScoreLab, vu d'APIExpose (ticket APX-LAB-001, 2026-10-09).
///
/// Le Lab fabrique des etats de depart de defis : il ecrit dans la memoire du jeu, sauve un etat,
/// le recharge. Une partie ainsi preparee ne doit RIEN produire : ni mesure, ni brouillon (rien ne
/// doit partir plus tard), ni prevol annonce, ni bandeau, ni replay, ni partie comptee. Ne pas etre
/// appairee n'isole pas une borne : elle soumet alors en anonyme.
///
/// Le Lab pose, avant de lancer le jeu, un drapeau a duree limitee :
/// <c>state/scorelab-atelier.json</c> = <c>{ "suppress_scoring": true, "expires_utc": "...",
/// "by": "NelfeScoreLab", "run_id": "..." }</c>. Tant qu'il vaut, la partie n'est pas une partie
/// NelfePlay (<see cref="RetroBat.Api.Infrastructure.PartieNelfePlayService"/>), et tout ce qui
/// respecte ce verdict se tait. Vu une fois pendant une partie, il la couvre jusqu'au bout : son
/// retrait ne rend le scoring qu'a la partie suivante.
///
/// LE DOUTE FAIT TAIRE LA BORNE, l'inverse du drapeau de labo : un drapeau illisible (en cours
/// d'ecriture par le Lab, abime) ou sans echeance lisible vaut silence. Mais rien ne dure plus de
/// six heures apres l'ecriture du fichier, quoi qu'il dise : un Lab qui plante ne laisse pas une
/// borne muette.
///
/// C'est l'inverse de <c>scorelab-lab.json</c> (<see cref="ScoreLabLabMode"/>), qui fait soumettre
/// une partie hors profil : les deux ne sont jamais valides ensemble, et s'ils le sont, l'atelier
/// l'emporte.
/// </summary>
public static class ScoreLabAtelier
{
    public static readonly TimeSpan MaxValidity = TimeSpan.FromHours(6);

    public static string FlagPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "scorelab-atelier.json");

    /// <summary>Ce que dit un drapeau valide : son echeance, qui l'a pose, pour quel passage.</summary>
    public sealed record Etat(DateTime ExpiresUtc, string? By, string? RunId);

    /// <summary>Vrai si l'atelier tient : la partie en cours ne produit rien.</summary>
    public static bool IsActive(DateTime utcNow, out string reason) => Lire(FlagPath, utcNow, out reason) is not null;

    /// <summary>L'atelier en cours, ou null : ce que /api/v1/status en montre.</summary>
    public static Etat? Statut(DateTime utcNow) => Lire(FlagPath, utcNow, out _);

    internal static Etat? Lire(string path, DateTime utcNow, out string reason)
    {
        reason = "pas d'atelier";
        if (!File.Exists(path))
        {
            return null;
        }

        DateTime plafond;
        try
        {
            plafond = File.GetLastWriteTimeUtc(path) + MaxValidity;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plafond = utcNow + MaxValidity;
        }

        if (plafond <= utcNow)
        {
            reason = "drapeau pose il y a plus de six heures, ignore";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("suppress_scoring", out var silence) || silence.ValueKind != JsonValueKind.True)
            {
                reason = "drapeau sans suppress_scoring";
                return null;
            }

            var until = plafond;
            if (root.TryGetProperty("expires_utc", out var expires)
                && expires.ValueKind == JsonValueKind.String
                && DateTime.TryParse(expires.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var annonce))
            {
                annonce = annonce.ToUniversalTime();
                if (annonce < until) until = annonce;
            }
            else
            {
                reason = $"echeance illisible : atelier tenu jusqu'a {plafond:HH:mm} UTC";
            }

            if (until <= utcNow)
            {
                reason = "drapeau expire";
                return null;
            }

            if (reason.StartsWith("pas d'", StringComparison.Ordinal)) reason = $"atelier actif jusqu'a {until:HH:mm} UTC";
            return new Etat(until, Texte(root, "by"), Texte(root, "run_id"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            // En cours d'ecriture, ou abime : dans le doute, la borne se tait.
            reason = $"drapeau illisible : atelier tenu jusqu'a {plafond:HH:mm} UTC";
            return new Etat(plafond, null, null);
        }
    }

    private static string? Texte(JsonElement root, string nom)
        => root.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
