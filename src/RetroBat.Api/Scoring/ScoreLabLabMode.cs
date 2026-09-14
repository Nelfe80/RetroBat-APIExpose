using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Scoring;

/// <summary>
/// Le mode laboratoire de NelfeScoreLab, vu d'APIExpose.
///
/// D'ordinaire, une partie d'un jeu sans profil ouvert ne soumet rien : le reporter s'arrete avant
/// de demander un ticket. C'est juste pour un joueur, et c'est ce qui empeche un jeu d'etre ouvert
/// automatiquement : l'ouverture d'un profil part d'une tentative SIGNEE, et aucune n'existe.
///
/// Pendant un passage du labo, NelfeScoreLab pose un drapeau a duree limitee. Tant qu'il est la,
/// le reporter soumet quand meme. La plateforme refuse le score (le profil n'est pas ouvert, rien
/// n'entre dans un classement) mais garde la tentative signee et ses cinq empreintes, qui sont
/// exactement ce que le deploiement en un clic attend.
///
/// Le drapeau expire toujours : six heures au plus, quoi qu'il dise. Un labo qui plante ne laisse
/// pas une borne soumettre indefiniment.
/// </summary>
public static class ScoreLabLabMode
{
    public static readonly TimeSpan MaxValidity = TimeSpan.FromHours(6);

    public static string FlagPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "scorelab-lab.json");

    /// <summary>Vrai si le drapeau existe, demande la soumission et n'a pas expire.</summary>
    public static bool IsActive(DateTime utcNow, out string reason) => IsActive(FlagPath, utcNow, out reason);

    internal static bool IsActive(string path, DateTime utcNow, out string reason)
    {
        reason = "pas de drapeau";
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("submit_unopened", out var submit) || submit.ValueKind != JsonValueKind.True)
            {
                reason = "drapeau sans submit_unopened";
                return false;
            }

            if (!root.TryGetProperty("expires_utc", out var expires)
                || !DateTime.TryParse(expires.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var until))
            {
                reason = "drapeau sans echeance lisible";
                return false;
            }

            until = until.ToUniversalTime();
            if (until <= utcNow)
            {
                reason = "drapeau expire";
                return false;
            }

            if (until - utcNow > MaxValidity)
            {
                reason = "echeance au-dela de six heures, refusee";
                return false;
            }

            reason = $"labo actif jusqu'a {until:HH:mm} UTC";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException)
        {
            reason = "drapeau illisible";
            return false;
        }
    }

    /// <summary>
    /// Le profil de substitution qui permet d'assembler le passeport. La plateforme ne le lit pas :
    /// le jeu n'ayant pas de profil ouvert, la soumission est refusee avant toute verification, et
    /// seules les empreintes mesurees de la partie sont gardees.
    /// </summary>
    public static JsonElement PlaceholderProfile(string engine = "libretro")
    {
        var node = new JsonObject
        {
            ["ruleset"] = "1cc",
            ["profile_version"] = 1,
            ["profile_document_sha256"] = string.Empty,
            ["engine"] = engine,
            ["metric"] = new JsonObject
            {
                ["type"] = "score",
                ["unit"] = "points",
                ["ranking_direction"] = "higher_better",
                ["result_source"] = "final"
            }
        };
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
