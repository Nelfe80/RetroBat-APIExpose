using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LA SANTE DE L'EMISSION PENDANT UN DIRECT (demande du user, 14/11 : « des fois on voit que YouTube patine, comme chez
/// NeoGeoff ; il faut couper la transmission et passer la main »). L'exe du direct (NelfePlay.LiveStreaming) se regule deja
/// seul (Regulateur : debit en trois paliers, 30 images/s, saut a l'image cle au-dela de 6 s de retard) et ecrit ce qu'il
/// fait sur sa sortie. On lit ces lignes, sans rien mesurer de plus :
/// <list type="bullet">
/// <item>« regulation : N s de retard, saut jusqu'a l'image cle suivante » : le spectateur perd des images ;</item>
/// <item>« regulation : debit N kbit/s (l'envoi n'ecoule pas ...) » : la connexion retient, l'exe baisse son debit ;
/// « (une minute sans retard ...) » : il le remonte ;</item>
/// <item>le point toutes les 10 s (« N s, ..., X Mo envoyes, ... ; debit N kbit/s, ... ») : le debit reellement envoye
/// contre le debit vise.</item>
/// </list>
/// L'emission patine DURABLEMENT, et la borne rend l'antenne, quand :
/// <list type="number">
/// <item>trois sauts en <see cref="FenetreS"/> secondes (le regulateur en fait un toutes les 10 s au plus) ;</item>
/// <item>ou un saut alors que l'exe est deja a son plus bas palier de debit (deux baisses « n'ecoule pas ») : il n'a plus
/// de marge ;</item>
/// <item>ou trois points de suite (30 s) sous <see cref="RatioMinDuDebit"/> du debit vise, avec un signe d'engorgement
/// (saut ou baisse « n'ecoule pas ») dans la fenetre : un debit bas sans engorgement est une image fixe, pas une panne.</item>
/// </list>
/// Rien ne dure au-dela du passage : une instance par lancement de l'exe. Pur.
/// </summary>
public sealed class SanteDeLEmission
{
    /// <summary>La fenetre d'observation des signes d'engorgement, en secondes.</summary>
    public const double FenetreS = 90;

    /// <summary>Sauts a l'image cle dans la fenetre avant la coupure.</summary>
    public const int SautsAvantCoupure = 3;

    /// <summary>Baisses « l'envoi n'ecoule pas » qui mettent l'exe a son plus bas palier (trois paliers).</summary>
    public const int BaissesJusquAuPlancher = 2;

    /// <summary>Sous cette part du debit vise, un point est bas.</summary>
    public const double RatioMinDuDebit = 0.5;

    /// <summary>Points bas de suite avant la coupure (un point toutes les 10 s).</summary>
    public const int PointsBasAvantCoupure = 3;

    private static readonly Regex Point = new(
        @"(?<s>\d+(?:\.\d+)?) s, images lues .*?, (?<mo>\d+(?:\.\d+)?) Mo envoyes, .*; debit (?<kbps>\d+) kbit/s",
        RegexOptions.CultureInvariant);

    private readonly Queue<double> _sauts = new();
    private readonly Queue<double> _engorgements = new();
    private int _baissesNettes;
    private (double S, double Mo)? _dernierPoint;
    private int _pointsBas;

    /// <summary>La raison de couper, des qu'elle est connue ; elle reste (le passage est perdu).</summary>
    public string? RaisonDeCouper { get; private set; }

    /// <summary>
    /// Une ligne de l'exe, lue a <paramref name="t"/> secondes (horloge de l'appelant). Rend la raison de couper quand
    /// l'emission vient d'etre jugee patinante, null sinon.
    /// </summary>
    public string? Observer(double t, string ligne)
    {
        if (RaisonDeCouper is not null || string.IsNullOrEmpty(ligne)) return null;
        Oublier(t);
        if (ligne.Contains("saut jusqu'a l'image cle suivante", StringComparison.Ordinal))
        {
            _sauts.Enqueue(t);
            _engorgements.Enqueue(t);
            if (_sauts.Count >= SautsAvantCoupure)
                return Couper(_sauts.Count + " sauts d'images en " + Texte(FenetreS) + " s");
            if (_baissesNettes >= BaissesJusquAuPlancher)
                return Couper("saut d'images au plus bas debit");
            return null;
        }
        if (ligne.Contains("regulation : debit", StringComparison.Ordinal))
        {
            if (ligne.Contains("n'ecoule pas", StringComparison.Ordinal))
            {
                _baissesNettes++;
                _engorgements.Enqueue(t);
            }
            else if (_baissesNettes > 0)
            {
                _baissesNettes--;
            }
            return null;
        }
        if (Point.Match(ligne) is { Success: true } m
            && double.TryParse(m.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && double.TryParse(m.Groups["mo"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mo)
            && int.TryParse(m.Groups["kbps"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vise))
        {
            var precedent = _dernierPoint;
            _dernierPoint = (s, mo);
            if (precedent is not { } p || s - p.S < 1 || vise <= 0) return null;
            var reel = (mo - p.Mo) * 8000 / (s - p.S);
            _pointsBas = reel < vise * RatioMinDuDebit ? _pointsBas + 1 : 0;
            if (_pointsBas >= PointsBasAvantCoupure && _engorgements.Count > 0)
                return Couper("debit envoye " + Texte(reel) + " kbit/s pour " + vise + " vises, " + _pointsBas + " points de suite");
        }
        return null;
    }

    private string Couper(string raison) => RaisonDeCouper = raison;

    private void Oublier(double t)
    {
        while (_sauts.Count > 0 && t - _sauts.Peek() > FenetreS) _sauts.Dequeue();
        while (_engorgements.Count > 0 && t - _engorgements.Peek() > FenetreS) _engorgements.Dequeue();
    }

    private static string Texte(double v) => v.ToString("0", CultureInfo.InvariantCulture);
}
