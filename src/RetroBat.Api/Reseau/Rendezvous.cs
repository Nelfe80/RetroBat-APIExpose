using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RetroBat.Api.Reseau;

/// <summary>
/// LE PLACEMENT DES REPLAYS SUR LES NOEUDS (CDC infra §3.6 et §15.7) : hachage de rendez-vous pondere. Chaque
/// replay revient aux « copies » noeuds « replay » de la carte qui ont le meilleur score pour lui :
///
///   H = SHA-256(« nelfeplay-replay/1| » + sha + « | » + key_id du noeud), x = ses 8 premiers octets (gros-boutiste)
///   u = (x + 0,5) / 2^64, score = poids / -ln(u) ; ex aequo : key_id dans l'ordre ordinal.
///
/// Le noeud fait le meme calcul (NelfeNode, Rendezvous.cs, vecteur commun dans les tests) : la borne sait ou
/// deposer un replay et ou le chercher sans demander a personne.
/// </summary>
public static class Rendezvous
{
    public const string Etiquette = "nelfeplay-replay/1|";

    public static double Score(string sha, string cleDuNoeud, int poids)
    {
        var empreinte = SHA256.HashData(Encoding.UTF8.GetBytes(Etiquette + sha + "|" + cleDuNoeud));
        var x = BinaryPrimitives.ReadUInt64BigEndian(empreinte);
        var u = ((double)x + 0.5) / 18446744073709551616.0;
        return Math.Max(1, poids) / -Math.Log(u);
    }

    /// <summary>Les noeuds a qui revient ce replay, du premier au dernier (« copies » au plus).</summary>
    public static IReadOnlyList<NoeudDuReseau> Proprietaires(string sha, IEnumerable<NoeudDuReseau> noeuds, int copies)
        => noeuds
            .Where(n => n.A("replay") && n.Cle is not null)
            .Select(n => (Noeud: n, Score: Score(sha, n.Cle!.KeyId, n.Poids)))
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Noeud.Cle!.KeyId, StringComparer.Ordinal)
            .Take(Math.Max(1, copies))
            .Select(p => p.Noeud)
            .ToList();
}
