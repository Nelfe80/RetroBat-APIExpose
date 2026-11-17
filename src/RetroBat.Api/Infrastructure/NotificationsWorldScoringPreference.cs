using RetroBat.Domain.Paths;
using System.Xml.Linq;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Option « NOTIFICATIONS EN WORLD SCORING » du menu APIExpose d'ES (demande du user du 14/11), cle
/// <c>global.apiexpose.world_scoring.notifications</c>, liste de choix :
/// <list type="bullet">
///   <item>AUTO (valeur vide), <c>ra_only</c> ou toute valeur inconnue : le comportement de la 1.9.182. Un joueur qui a
///   RetroAchievements garde ses succes en World Scoring (<see cref="NotificationsRetroArch.AutresNotifications"/> coupent le
///   reste) ;</item>
///   <item><c>none</c> (« TOUT MASQUER ») : meme avec RetroAchievements, la partie du World Scoring revient a
///   <c>OnScreenMsg = 0</c>. Plus aucun widget de RetroArch : ni succes, ni « Starting movie record », qu'aucune cle
///   notification_show_* ne cache.</item>
/// </list>
/// Un joueur sans RetroAchievements n'est pas concerne : tout est deja coupe. Les deux chemins obeissent : lancement par
/// l'API (<see cref="RetroBat.Api.Leaderboard.LeaderboardInputService"/>) et lancement par ES au crochet game-start
/// (<see cref="NotificationsDesLancementsEs"/>). Lecture seule : l'API n'ecrit jamais cette cle.
/// </summary>
public static class NotificationsWorldScoringPreference
{
    /// <summary>Cle du reglage dans es_settings.cfg (seule une cle global.apiexpose.* est permise).</summary>
    public const string EsKey = "global.apiexpose.world_scoring.notifications";

    /// <summary>La valeur du choix « RetroAchievements seulement » (le defaut, comme la valeur vide).</summary>
    public const string SuccesSeulement = "ra_only";

    /// <summary>La valeur du choix « Tout masquer ».</summary>
    public const string Aucune = "none";

    /// <summary>
    /// Option « FLASH AU RECORD BATTU » (demande du user du 17/11), interrupteur, ETEINT par defaut. Allumee : la partie du
    /// World Scoring garde les widgets de RetroArch (comme avec RetroAchievements) pour son seul flash de capture, rapide,
    /// a l'instant ou le record tombe ; tout autre message reste coupe. « TOUT MASQUER » l'emporte.
    /// </summary>
    public const string FlashKey = "global.apiexpose.world_scoring.record_flash";

    /// <summary>L'option du flash est-elle allumee ? « 1 », « true », « on » ; vide ou absente = eteinte. Pur.</summary>
    public static bool FlashDuRecord(XDocument es)
    {
        foreach (var e in es.Root?.Elements() ?? [])
        {
            if (!string.Equals((string?)e.Attribute("name"), FlashKey, StringComparison.OrdinalIgnoreCase)) continue;
            var v = ((string?)e.Attribute("value") ?? "").Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Comme <see cref="FlashDuRecord(XDocument)"/>, lu sur le disque. Faux si illisible.</summary>
    public static bool FlashDuRecord(string? esSettings = null)
    {
        try
        {
            return FlashDuRecord(XDocument.Load(esSettings ?? RetroBatPaths.EmulationStationSettingsPath));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>La valeur brute veut-elle tout masquer ? Seul <c>none</c> le veut ; vide, ra_only ou inconnu : non. Pur.</summary>
    public static bool ToutMasquer(string? brute)
        => string.Equals((brute ?? string.Empty).Trim(), Aucune, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lu dans es_settings.cfg (la cle globale seule ; absente = defaut). Pur.</summary>
    public static bool ToutMasquer(XDocument es)
    {
        foreach (var e in es.Root?.Elements() ?? [])
        {
            if (string.Equals((string?)e.Attribute("name"), EsKey, StringComparison.OrdinalIgnoreCase))
                return ToutMasquer((string?)e.Attribute("value"));
        }

        return false;
    }
}
