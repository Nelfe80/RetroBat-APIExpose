using System.Net;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES LECTURES CONDITIONNELLES DU SITE (lot 4 bis C-11, 14/11). Depuis le 14/11, le site repond a <c>scores/board</c>,
/// <c>scores/leaderboard</c> et <c>scores/index</c> avec une empreinte (<c>ETag</c>), puis 304 sans corps a qui la
/// renvoie (<c>If-None-Match</c>). Ici, par adresse, la derniere empreinte et le dernier corps recus : la lecture suivante
/// renvoie l'empreinte et, sur 304, resert le corps garde. Meme modele que <c>open-games</c> et <c>challenges/open</c>
/// (NelfePlayScoringCollectionSyncService, IndexDesDefisService), en memoire seulement.
/// </summary>
public sealed class LecturesConditionnelles
{
    /// <summary>Le partage de toute l'API : les classements du panneau et ceux du composant « records ».</summary>
    public static LecturesConditionnelles Partagees { get; } = new();

    /// <summary>Au-dela, la plus ancienne adresse est oubliee : quelques Mo au plus.</summary>
    private readonly int _plafond;

    private readonly Dictionary<string, (string Empreinte, string Corps, long Rang)> _gardees = new(StringComparer.Ordinal);
    private long _rang;

    public LecturesConditionnelles(int plafond = 128)
    {
        _plafond = Math.Max(1, plafond);
    }

    /// <summary>Nombre d'adresses gardees (tests).</summary>
    public int Nombre
    {
        get { lock (_gardees) return _gardees.Count; }
    }

    /// <summary>
    /// Le corps de <paramref name="url"/>, comme <see cref="HttpClient.GetStringAsync(string?, CancellationToken)"/> :
    /// une reponse en erreur leve <see cref="HttpRequestException"/>. Un 304 rend le corps garde ; un 304 sans corps
    /// garde (ne devrait pas arriver) leve aussi, l'appelant se rabat sur sa copie.
    /// </summary>
    public async Task<string> LireAsync(HttpClient client, string url, CancellationToken ct)
    {
        (string Empreinte, string Corps, long Rang) gardee;
        bool connue;
        lock (_gardees) connue = _gardees.TryGetValue(url, out gardee);

        using var requete = new HttpRequestMessage(HttpMethod.Get, url);
        if (connue) requete.Headers.TryAddWithoutValidation("If-None-Match", gardee.Empreinte);

        using var reponse = await client.SendAsync(requete, ct).ConfigureAwait(false);
        if (reponse.StatusCode == HttpStatusCode.NotModified)
        {
            if (!connue) throw new HttpRequestException("304 sans corps garde", null, HttpStatusCode.NotModified);
            lock (_gardees)
            {
                if (_gardees.ContainsKey(url)) _gardees[url] = gardee with { Rang = ++_rang };
            }
            return gardee.Corps;
        }

        reponse.EnsureSuccessStatusCode();
        var corps = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var empreinte = reponse.Headers.ETag?.ToString();
        lock (_gardees)
        {
            if (string.IsNullOrEmpty(empreinte))
            {
                _gardees.Remove(url);
            }
            else
            {
                _gardees[url] = (empreinte, corps, ++_rang);
                while (_gardees.Count > _plafond)
                {
                    var plusAncienne = _gardees.MinBy(p => p.Value.Rang).Key;
                    _gardees.Remove(plusAncienne);
                }
            }
        }

        return corps;
    }

    /// <summary>Oublie tout (tests).</summary>
    public void Vider()
    {
        lock (_gardees) _gardees.Clear();
    }
}
