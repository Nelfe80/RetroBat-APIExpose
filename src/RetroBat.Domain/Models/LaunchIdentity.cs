namespace RetroBat.Domain.Models;

/// <summary>
/// L'IDENTIFIANT D'UN LANCEMENT (ECR-08, 14/11). <c>ui.game.started</c> part de plusieurs chemins (crochet game-start d'ES,
/// lancement direct de l'API, MULTIPLAYER), a des instants differents, et rien ne disait qu'un second depart du meme jeu
/// etait le meme lancement. Chaque chemin demande ici l'identifiant de son lancement et le pose dans sa charge
/// (<c>LaunchId</c>) ; la fin (<c>ui.game.ended</c>) porte le meme.
///
/// Un second depart du meme jeu (meme fichier) avant toute fin et dans les <see cref="Fenetre"/> qui suivent garde
/// l'identifiant du premier : un lancement direct suivi du crochet d'ES ne fait qu'un lancement. Apres une fin, le meme jeu
/// relance en a un nouveau. Les abonnes ne sont pas changes : ils peuvent s'en servir pour dedoublonner.
/// </summary>
public static class LaunchIdentity
{
    /// <summary>Au-dela, un depart du meme jeu sans fin entre les deux est un nouveau lancement (une fin perdue).</summary>
    public static readonly TimeSpan Fenetre = TimeSpan.FromMinutes(2);

    private static readonly object Verrou = new();
    private static string? _courant;
    private static string _jeuCourant = string.Empty;
    private static DateTime _ouvertUtc;
    private static bool _ferme = true;

    /// <summary>L'identifiant du lancement en cours ; null avant le premier depart ou apres une fin.</summary>
    public static string? Current
    {
        get { lock (Verrou) return _ferme ? null : _courant; }
    }

    /// <summary>
    /// L'identifiant du lancement de ce jeu : celui en cours si c'est le meme jeu, ouvert depuis moins de
    /// <see cref="Fenetre"/> et pas encore ferme ; sinon un nouveau (32 caracteres hexadecimaux).
    /// </summary>
    public static string Open(string? gamePath, DateTime? nowUtc = null)
    {
        var maintenant = nowUtc ?? DateTime.UtcNow;
        var jeu = Key(gamePath);
        lock (Verrou)
        {
            if (!_ferme && _courant is not null && jeu.Length > 0 && jeu == _jeuCourant && maintenant - _ouvertUtc < Fenetre)
            {
                return _courant;
            }

            _courant = Guid.NewGuid().ToString("N");
            _jeuCourant = jeu;
            _ouvertUtc = maintenant;
            _ferme = false;
            return _courant;
        }
    }

    /// <summary>La fin du lancement : rend son identifiant (null s'il n'y en avait pas) et le ferme.</summary>
    public static string? Close()
    {
        lock (Verrou)
        {
            if (_ferme) return null;
            _ferme = true;
            return _courant;
        }
    }

    /// <summary>Le jeu, tel qu'on le compare : le nom du fichier sans extension, en minuscules.</summary>
    private static string Key(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return string.Empty;
        try
        {
            return Path.GetFileNameWithoutExtension(gamePath.Trim().TrimEnd('/', '\\')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return gamePath.Trim().ToLowerInvariant();
        }
    }

    /// <summary>Pour les tests : aucun lancement en cours.</summary>
    public static void Reset()
    {
        lock (Verrou)
        {
            _courant = null;
            _jeuCourant = string.Empty;
            _ferme = true;
        }
    }
}
