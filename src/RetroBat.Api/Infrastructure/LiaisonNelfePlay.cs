using System.Net;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LA LIAISON AVEC NELFEPLAY, telle que la borne la voit (2026-10-04). Chaque echange avec le site
/// la note, quel que soit le service qui l'a fait : releve du compte, classement, envoi d'un score,
/// replay. Le panneau du classement en tire la pastille devant le pseudo : verte quand le dernier
/// echange a abouti, rouge sinon (et tant qu'aucun n'a eu lieu).
///
/// Une reponse sous 500 compte comme une liaison qui tient, meme quand la demande est refusee :
/// le site a repondu. Une erreur 5xx, une connexion refusee ou une attente sans reponse la coupe.
/// </summary>
public static class LiaisonNelfePlay
{
    private static readonly object Gate = new();
    private static bool? _joint;
    private static DateTime? _le;

    /// <summary>Le dernier echange avec le site a-t-il abouti ? Faux tant qu'aucun n'a eu lieu.</summary>
    public static bool EnLigne
    {
        get { lock (Gate) return _joint == true; }
    }

    /// <summary>L'heure du dernier echange note, null avant le premier.</summary>
    public static DateTime? DernierEchangeUtc
    {
        get { lock (Gate) return _le; }
    }

    public static void Noter(bool joint)
    {
        lock (Gate)
        {
            _joint = joint;
            _le = DateTime.UtcNow;
        }
    }

    /// <summary>Le site a-t-il repondu ? Toute reponse sous 500 est une reponse.</summary>
    public static bool AReponduLeSite(HttpStatusCode statut) => (int)statut < 500;

    /// <summary>Cette adresse est-elle celle de NelfePlay (meme schema, hote et port) ?</summary>
    public static bool EstNelfePlay(Uri? adresse, string baseUrl)
    {
        if (adresse is null || !adresse.IsAbsoluteUri) return false;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var site)) return false;
        return string.Equals(
            adresse.GetLeftPart(UriPartial.Authority),
            site.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pour les tests : revient a l'etat de demarrage.</summary>
    internal static void Oublier()
    {
        lock (Gate)
        {
            _joint = null;
            _le = null;
        }
    }
}

/// <summary>
/// Le relais pose sur tous les clients HTTP de l'API : il note la liaison a chaque echange avec
/// NelfePlay, et ne touche a rien d'autre.
/// </summary>
public sealed class LiaisonNelfePlayHandler : DelegatingHandler
{
    private readonly Func<string> _baseUrl;

    public LiaisonNelfePlayHandler() : this(() => NelfePlayAgentService.BaseUrl)
    {
    }

    internal LiaisonNelfePlayHandler(Func<string> baseUrl)
    {
        _baseUrl = baseUrl;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var suivi = LiaisonNelfePlay.EstNelfePlay(request.RequestUri, _baseUrl());
        try
        {
            var reponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (suivi) LiaisonNelfePlay.Noter(LiaisonNelfePlay.AReponduLeSite(reponse.StatusCode));
            return reponse;
        }
        catch (Exception ex) when (suivi && ex is HttpRequestException or OperationCanceledException)
        {
            // Une attente depassee arrive ici en annulation, comme un abandon : les deux disent
            // qu'aucune reponse n'est venue.
            LiaisonNelfePlay.Noter(false);
            throw;
        }
    }
}
