namespace RetroBat.Api.Deck;

/// <summary>
/// UNE NOUVELLE PARTIE DANS LE MEME LANCEMENT (11/11, essai du user sur Bubble Bobble : apres un game over, la partie
/// suivante gardait les vies perdues, le meilleur round et les salles secretes de la precedente). Le Deck ne remettait
/// ses cumuls a zero qu'a un nouveau lancement ; en arcade, on rejoue sans quitter le jeu.
///
/// La regle, tenue par l'API pour le lancement en cours, sans rien inventer :
/// - LE SCORE DU JOUEUR 1 RETOMBE A ZERO apres avoir compte des points : c'est le depart d'une nouvelle partie (le jeu
///   remet son score a zero au START). Un continue garde le score (Capcom, Cave, Toaplan, Taito) : il ne remet rien a
///   zero, la partie continue et le 1CC est coupe par la regle du scoring, pas par le Deck. Le zero se confirme : il
///   tient <see cref="FenetreDuZero"/>, ou la lecture suivante est plus basse que le score d'avant (une nouvelle partie
///   qui marque deja) ; une lecture qui revient au moins au score d'avant est un passage (un score en plusieurs mots
///   ecrit en deux temps) et ne compte pas.
/// - UN DEPART LU DANS LA MEMOIRE DU JEU (credit consomme, GAME_START du .MEM) alors que la partie en cours n'a encore
///   rien marque : la demo d'attraction ou une partie finie sans un point ne laissent rien au Deck.
/// - UNE TENTATIVE D'EPREUVE QUI S'OUVRE (speedrun.attempt.opened) : l'etat de depart recharge, une partie neuve.
///
/// Le Deck remet ses cumuls a zero au message <c>deck.run</c> : au DEPART de la nouvelle partie, jamais au game over (le
/// bilan reste lisible pendant l'ecran de fin et la demo). Limite connue : un jeu qui remet son score a zero au continue
/// (Double Dragon, Altered Beast, Sonic) verrait son Deck repartir au continue ; aucun Deck n'est dans ce cas le 11/11.
/// Pur : l'heure est donnee par l'appelant.
/// </summary>
public sealed class DeckNouvellePartie
{
    /// <summary>Le temps qu'un zero doit tenir avant de valoir une nouvelle partie.</summary>
    public static readonly TimeSpan FenetreDuZero = TimeSpan.FromMilliseconds(700);

    /// <summary>Un depart lu juste apres une nouvelle partie est le meme depart (credit puis START, deux lectures).</summary>
    public static readonly TimeSpan DepartDoublon = TimeSpan.FromSeconds(3);

    public const string RaisonScore = "score-zero", RaisonDepart = "start", RaisonTentative = "attempt";

    private long _pic;                                  // plus haut score du joueur 1 dans la partie en cours
    private (long Pic, DateTime Depuis)? _zero;          // un zero en attente de confirmation

    public DeckNouvellePartie(DateTime debut) => DebutUtc = debut;

    /// <summary>La fenetre du zero de cette regle (<see cref="FenetreDuZero"/> ; reduite par les tests).</summary>
    public TimeSpan Fenetre { get; init; } = FenetreDuZero;

    /// <summary>Numero de la partie dans le lancement : 0 pour la premiere, +1 a chaque nouvelle partie.</summary>
    public int Run { get; private set; }

    /// <summary>Le depart de la partie en cours.</summary>
    public DateTime DebutUtc { get; private set; }

    /// <summary>Pourquoi la partie en cours a commence (vide : la premiere, au lancement).</summary>
    public string Raison { get; private set; } = "";

    /// <summary>Le plus haut score de la partie d'avant (0 si elle n'en avait pas).</summary>
    public long PicPrecedent { get; private set; }

    /// <summary>Un zero attend sa confirmation (l'appelant rappelle <see cref="Echeance"/> apres la fenetre).</summary>
    public bool ZeroEnAttente => _zero is not null;

    /// <summary>
    /// Une lecture du score (score.live.changed). Rend vrai si elle ouvre une nouvelle partie. Le joueur 2 et les scores
    /// de RetroAchievements (une autre mesure, qui peut valoir 0 a cote du score du jeu) ne comptent pas.
    /// </summary>
    public bool Score(int joueur, long score, string? source, DateTime maintenant)
    {
        if (joueur != 1 || score < 0) return false;
        if (source is not null && source.StartsWith("retroachievements", StringComparison.OrdinalIgnoreCase)) return false;
        if (_zero is { } z)
        {
            if (score == 0) return false;
            if (score >= z.Pic)
            {
                // un passage : le score revient la ou il en etait
                _zero = null;
                _pic = Math.Max(_pic, score);
                return false;
            }

            Nouvelle(RaisonScore, z.Pic, z.Depuis);
            _pic = score;
            return true;
        }

        if (score == 0)
        {
            if (_pic > 0) _zero = (_pic, maintenant);
            return false;
        }

        _pic = Math.Max(_pic, score);
        return false;
    }

    /// <summary>La fenetre du zero est passee : rend vrai si le zero a tenu (nouvelle partie).</summary>
    public bool Echeance(DateTime maintenant)
    {
        if (_zero is not { } z || maintenant - z.Depuis < Fenetre) return false;
        Nouvelle(RaisonScore, z.Pic, z.Depuis);
        return true;
    }

    /// <summary>
    /// Un depart lu dans la memoire du jeu (scoring.partie.depart). Seuls le credit consomme et GAME_START comptent (le
    /// « score qui monte » arrive apres les points : il n'ouvre rien) ; seulement si la partie en cours n'a rien marque.
    /// </summary>
    public bool Depart(string? source, DateTime maintenant)
    {
        if (source is not ("credit consomme" or "GAME_START")) return false;
        if (_pic > 0 || _zero is not null) return false;
        if (maintenant - DebutUtc < DepartDoublon) return false;
        Nouvelle(RaisonDepart, 0, maintenant);
        return true;
    }

    /// <summary>Une tentative d'epreuve s'ouvre : toujours une nouvelle partie.</summary>
    public bool Tentative(DateTime maintenant)
    {
        Nouvelle(RaisonTentative, _pic, maintenant);
        return true;
    }

    private void Nouvelle(string raison, long picPrecedent, DateTime depuis)
    {
        Run++;
        Raison = raison;
        PicPrecedent = picPrecedent;
        DebutUtc = depuis;
        _pic = 0;
        _zero = null;
    }
}
