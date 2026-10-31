namespace RetroBat.Api.Multiplayer;

/// <summary>Un choix ecrit au depart pret : la place, la cle du catalogue (machine, character...) et sa valeur.</summary>
public sealed record ChoixDuDepart(int Place, string Cle, int Valeur);

/// <summary>
/// Une partie MULTIPLAYER qui concourt : la gameroom, la place de cette borne et le joueur du jeu qu'elle tient (ses lignes
/// du .MEM, son port), le nombre de joueurs, le depart (l'empreinte de l'etat du catalogue et les choix ecrits, null pour
/// un demarrage a froid) et les chargements d'etat que ce depart explique.
/// </summary>
public sealed record PartieMultiplayerCertifiee(
    string Gameroom, int Place, int JoueurDuJeu, int Joueurs, string Chemin, string? EtatDeDepart, IReadOnlyList<ChoixDuDepart> Choix,
    int ChargementsPermis);

/// <summary>
/// UNE COOP DE MULTIPLAYER CONCOURT DE BASE (decision user du 2026-10-31) : chaque place de la partie a son 1CC MULTI et son
/// 1LC MULTI, sans mode a choisir. Le lancement pose la partie juste avant de demarrer le jeu, le rapporteur la prend au
/// debut de la partie (ui.game.started) et s'en sert a la fin. Elle n'en fait pas une partie NelfePlay complete : ni
/// enregistreur de replay en netplay, ni bandeau en partie.
///
/// Une partie posee qui n'a pas demarre se perd au bout de <see cref="Validite"/>, ou a la fin du lancement : jamais un
/// jeu lance ensuite hors MULTIPLAYER ne la prend.
/// </summary>
public static class MultiplayerScoring
{
    /// <summary>Le temps qu'a le jeu pour demarrer apres la pose.</summary>
    public static readonly TimeSpan Validite = TimeSpan.FromMinutes(3);

    private static readonly object Verrou = new();
    private static (PartieMultiplayerCertifiee Partie, DateTime Quand)? _posee;

    /// <summary>
    /// Une coop que le catalogue fait concourir (sa sortie au World Scoring l'a dit) ; son classement s'ouvre ou non au site
    /// (profils 1cc-multi, 1lc-multi).
    /// </summary>
    public static bool Concourt(MultiplayerGame jeu)
        => string.Equals(jeu.Family, "coop", StringComparison.Ordinal) && jeu.Scoring is { RomGroup.Length: > 0 };

    /// <summary>Le nom du classement, jamais traduit, comme le sigle que la borne et le site affichent.</summary>
    public const string Etiquette = "1CC MULTI";

    public static void Poser(PartieMultiplayerCertifiee partie, DateTime maintenant)
    {
        lock (Verrou)
        {
            _posee = (partie, maintenant);
        }
    }

    public static void Oublier()
    {
        lock (Verrou)
        {
            _posee = null;
        }
    }

    /// <summary>La partie posee pour ce lancement, une seule fois ; null si rien n'est pose ou si la pose est trop ancienne.</summary>
    public static PartieMultiplayerCertifiee? Prendre(DateTime maintenant)
    {
        lock (Verrou)
        {
            if (_posee is not { } posee)
            {
                return null;
            }

            _posee = null;
            return maintenant - posee.Quand <= Validite ? posee.Partie : null;
        }
    }

    /// <summary>
    /// Les choix que le depart pret ecrit : ceux des joueurs assis, pour les cles que le catalogue sait ecrire. Un joueur
    /// sans choix garde la valeur de l'etat, qui n'est pas un choix ecrit.
    /// </summary>
    public static IReadOnlyList<ChoixDuDepart> ChoixEcrits(MultiplayerGame jeu, IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace)
    {
        var cles = new HashSet<string>((jeu.Choices ?? []).Select(c => c.Key), StringComparer.Ordinal);
        return choixParPlace.OrderBy(p => p.Key)
            .SelectMany(p => (p.Value ?? new Dictionary<string, int>())
                .Where(c => cles.Contains(c.Key))
                .OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => new ChoixDuDepart(p.Key, c.Key, c.Value)))
            .ToList();
    }
}
