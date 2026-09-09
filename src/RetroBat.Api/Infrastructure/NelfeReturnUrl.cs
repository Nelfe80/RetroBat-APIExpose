namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Les adresses de RETOUR acceptées par le funnel web.
///
/// Le funnel fonctionne par NAVIGATION : le site envoie le navigateur sur une URL loopback
/// d'APIExpose, qui agit puis REDIRIGE vers le site. Cette redirection est le point sensible :
/// sans contrôle, n'importe qui pourrait faire d'APIExpose un tremplin vers l'adresse de son
/// choix (open redirect). D'où l'exigence d'un hôte connu ET du HTTPS.
///
/// Cette liste vivait en double, dans deux contrôleurs. Une liste d'autorisation en double
/// finit toujours par n'être allongée que d'un côté.
/// </summary>
public static class NelfeReturnUrl
{
    private const string Fallback = "https://nelfeplay.com";

    public static bool IsAllowedHost(string host) =>
        host.Equals("nelfeplay.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".nelfeplay.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("nelfetech.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".nelfetech.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>L'ORIGINE seule (schéma + hôte), pour composer une adresse connue d'avance.</summary>
    public static string SafeOrigin(string? url)
    {
        var u = Accepter(url);
        return u is null ? Fallback : u.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// L'adresse de retour SANS sa query : on repart propre, les paramètres de résultat sont
    /// ajoutés par l'appelant et jamais accumulés d'un aller-retour à l'autre.
    /// </summary>
    public static string SafeBase(string? url)
    {
        var u = Accepter(url);
        return u is null ? Fallback + "/" : u.GetLeftPart(UriPartial.Path);
    }

    private static Uri? Accepter(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        return u.Scheme == Uri.UriSchemeHttps && IsAllowedHost(u.Host) ? u : null;
    }
}
