namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Les seules routes que la découverte a le droit d'appeler, et celles qu'elle n'appellera
/// jamais.
///
/// Il existe sur la plateforme une route qui reçoit des images : celle des captures de
/// records, servie par <c>ScoreShotService</c>. Elle est légitime, la personne sait que son
/// record est photographié, et elle n'a rien à voir avec la découverte, qui ne remonte que
/// des nombres. Les deux chemins doivent donc rester étrangers l'un à l'autre, et pas
/// seulement par habitude : ici, une URL de dépôt d'image est refusée avant d'être appelée.
/// </summary>
public static class DiscoveryRoutes
{
    public const string Ticket = "/api/v1/agent/scoring-discovery/ticket";
    public const string Sessions = "/api/v1/agent/scoring-discovery/sessions";

    private static readonly string[] Allowed = { Ticket, Sessions };

    /// <summary>Fragments qui trahissent une route de dépôt d'image.</summary>
    private static readonly string[] Forbidden = { "/scores/shot", "/shot", "/screenshot", "/record-shots", "/images" };

    /// <summary>
    /// Vrai si la découverte peut appeler cette adresse. <paramref name="reason"/> dit
    /// pourquoi non.
    /// </summary>
    public static bool IsAllowed(string? url, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "adresse vide";
            return false;
        }

        // On raisonne sur le CHEMIN, pas sur la chaîne entière : une requête dont le corps ou
        // la chaîne de requête mentionne « shot » n'est pas un dépôt d'image, et un chemin
        // maquillé en paramètre n'en devient pas licite.
        var path = url;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var afterHost = path.IndexOf('/', scheme + 3);
            path = afterHost >= 0 ? path[afterHost..] : "/";
        }

        var query = path.IndexOfAny(new[] { '?', '#' });
        if (query >= 0)
        {
            path = path[..query];
        }

        path = path.TrimEnd('/').ToLowerInvariant();
        if (path.Length == 0)
        {
            path = "/";
        }

        foreach (var forbidden in Forbidden)
        {
            if (path.EndsWith(forbidden, StringComparison.Ordinal) || path.Contains(forbidden + "/", StringComparison.Ordinal))
            {
                reason = $"la découverte ne dépose pas d'image : {forbidden}";
                return false;
            }
        }

        foreach (var allowed in Allowed)
        {
            if (string.Equals(path, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        reason = "route hors de la découverte";
        return false;
    }
}
