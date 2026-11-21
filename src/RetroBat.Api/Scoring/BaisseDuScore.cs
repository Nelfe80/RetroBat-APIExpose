namespace RetroBat.Api.Scoring;

/// <summary>
/// LES TOTAUX D'UNE MEME SALVE se jugent contre le score d'AVANT la salve (2026-10-03).
///
/// Un jeu qui range son score un chiffre par octet (1942 : huit octets, un par chiffre) change
/// plusieurs octets dans la meme image quand une retenue passe. Le wrapper signale chaque octet a
/// part, et l'agregateur publie un total apres chacun : de 90 a 100, on recoit 190 (la centaine
/// est deja ecrite, pas encore la dizaine), puis 100. Compare d'un total au suivant, 190 puis 100
/// est une BAISSE : chez un testeur, le replay de 1942 a ete coupe sept secondes apres le depart
/// (« le score a baisse (100) apres avoir monte »).
///
/// Les signaux d'une meme image arrivent ensemble, a quelques millisecondes ; une image dure
/// 16 ms. On groupe donc les totaux par salve (tout ce qui arrive moins de <see cref="Duree"/>
/// apres le premier) et chaque total se compare au dernier total de la salve precedente. Le
/// temps, et non le numero d'image : l'agregateur et le signal memoire qui porte l'image passent
/// par le bus sans ordre garanti, et le pont Lua de MAME ne transmet pas d'image du tout.
/// </summary>
public sealed class SalvesDeScore
{
    /// <summary>Les totaux arrives moins de 50 ms apres le premier d'une salve en font partie.</summary>
    public static readonly TimeSpan Duree = TimeSpan.FromMilliseconds(50);

    private DateTime _debut = DateTime.MinValue;
    private long? _avant;

    /// <summary>
    /// Le total de reference de cette lecture (le dernier de la salve precedente) et si elle ouvre
    /// une salve. <paramref name="precedent"/> est le dernier total recu avant celui-ci.
    /// </summary>
    public (long? Reference, bool NouvelleSalve) Lire(long? precedent, DateTime maintenant)
    {
        if (maintenant - _debut > Duree || maintenant < _debut)
        {
            _debut = maintenant;
            _avant = precedent;
            return (_avant, true);
        }

        return (_avant, false);
    }
}

/// <summary>
/// LA BAISSE QUI ARRETE LE REPLAY SE CONFIRME (2026-10-03). Le score qui baisse pendant un
/// enregistrement, apres y avoir monte, dit une nouvelle partie ou une remise a zero : on arrete
/// le replay. Une baisse ne vaut qu'une fois CONFIRMEE par la lecture d'une salve suivante, encore
/// sous le score d'avant la baisse ; une lecture de passage (un chiffre ecrit une image avant
/// l'autre) est rattrapee aussitot et n'arrete plus rien. Une vraie remise a zero arrete le replay
/// aux premiers points de la partie suivante, ou a la fin du jeu si personne ne rejoue.
///
/// SEULE UNE VRAIE REMISE A ZERO ARRETE LE REPLAY (16/11, GigaWing d'Old AF Gamer et de NeoGeoff). Trois « baisses » en
/// pleine partie (1 157 820, 1 208 680, 304 719 740 pour une premiere vie finie a 246 082 370) arretaient le replay et le
/// film, que le score relancait ensuite : un run en morceaux, et le score rattache a l'un d'eux. Une baisse ne compte plus
/// que si le score retombe pres de zero (<see cref="EstUneRemiseAZero"/>) a la lecture qui ouvre l'attente, ou apres un GAME_OVER du .MEM
/// (<c>finDePartieVue</c>) ; la fin d'un 1CC au credit passe, elle, par « scoring.run.ended ».
/// </summary>
public sealed class ArretALaBaisse
{
    private long? _avantLaBaisse;

    /// <summary>Une baisse attend sa confirmation.</summary>
    public bool EnAttente => _avantLaBaisse is not null;

    /// <summary>
    /// La part du score d'avant sous laquelle une baisse est une remise a zero : un millieme. Le banc du 16/11 (replay du
    /// record GigaWing d'Old AF Gamer rejoue image par image, 0 ecart) montre un score qui ne baisse JAMAIS en memoire : la
    /// baisse naissait du total recompose mot par mot (un mot de poids fort encore ancien, les autres deja neufs). Juste apres
    /// la premiere retenue dans un mot (10^8 de GigaWing), ce total faux ne garde que les chiffres du bas : un dixieme le
    /// laissait encore passer pour une remise a zero.
    /// </summary>
    public const long DiviseurDeLaRemiseAZero = 1000;

    /// <summary>
    /// Le score retombe-t-il a zero, ou presque ? Au plus la valeur de depart du run (<paramref name="valeurDeDepart"/>, la
    /// plus basse lue au debut de l'enregistrement, avant toute montee), ou au plus un millieme du score d'avant. La valeur de
    /// depart ne compte que jusqu'a la moitie du score d'avant : un enregistrement ouvert en cours de run (adopte en retard)
    /// ne fait pas d'un creux de passage une remise a zero. Une retenue mal lue entre deux mots BCD ne retire qu'un chiffre de
    /// poids fort a un score qui reste du meme ordre : elle n'y tombe pas. Pur.
    /// </summary>
    public static bool EstUneRemiseAZero(long total, long avant, long valeurDeDepart)
        => total < avant && total <= Math.Max(Math.Min(valeurDeDepart, avant / 2), avant / DiviseurDeLaRemiseAZero);

    /// <summary>
    /// Vrai quand une baisse est confirmee par cette lecture : il faut arreter le replay.
    /// <paramref name="enregistrement"/> : un replay s'enregistre ; <paramref name="monteeVue"/> :
    /// le score y a monte ; <paramref name="valeurDeDepart"/> : la plus basse lecture de l'enregistrement ;
    /// <paramref name="finDePartieVue"/> : le .MEM a dit GAME_OVER dans la partie, toute baisse vaut alors une fin.
    /// </summary>
    public bool Lire(long total, long? reference, bool nouvelleSalve, bool enregistrement, bool monteeVue,
        long valeurDeDepart = 0, bool finDePartieVue = false)
    {
        if (_avantLaBaisse is { } avant)
        {
            // Dans la meme salve, rien n'est encore confirme ni dementi.
            if (!nouvelleSalve) return false;
            _avantLaBaisse = null;
            // La remise a zero a ete vue en ouvrant l'attente ; la salve suivante confirme seulement que le score n'est pas revenu
            // (une nouvelle partie a deja pu marquer ses premiers points).
            if (total < avant) return enregistrement;
        }

        if (enregistrement && monteeVue && reference is { } r && total < r
            && (finDePartieVue || EstUneRemiseAZero(total, r, valeurDeDepart)))
        {
            _avantLaBaisse = r;
        }

        return false;
    }

    /// <summary>Oublie une baisse en attente (nouvel enregistrement, nouvelle partie).</summary>
    public void Oublier() => _avantLaBaisse = null;
}

/// <summary>
/// LA FIN DE LA FENETRE DE JEU PAR LA BAISSE DU SCORE (21/11, DaiOuJou de la borne du user). Un jeu dont le .MEM ne dit pas
/// GAME_OVER n'avait aucune fin de partie : la fenetre ouverte au depart (credit consomme, START, GAME_START) ne se fermait
/// jamais, et la demo qui suit la partie, 35 minutes de scores, passait pour du jeu (2 554 070 envoyes, « new record » dans le
/// direct). Une vraie remise a zero (<see cref="ArretALaBaisse.EstUneRemiseAZero"/>), confirmee par la salve suivante, ferme la
/// fenetre ; seul un nouveau depart la rouvre. Independant de l'enregistrement du replay (le replay peut etre coupe).
/// </summary>
public sealed class FinDeFenetreParBaisse
{
    private (long Avant, long ImageDuHaut)? _attente;

    /// <summary>
    /// L'image du dernier point haut quand cette lecture CONFIRME la remise a zero, sinon null.
    /// <paramref name="imageDuHaut"/> : l'image de la derniere lecture avant la baisse. Pur.
    /// </summary>
    public long? Lire(long total, long? reference, bool nouvelleSalve, long imageDuHaut)
    {
        if (_attente is { } a)
        {
            if (!nouvelleSalve) return null;
            _attente = null;
            if (total < a.Avant) return a.ImageDuHaut;
        }

        if (reference is { } r && total < r && ArretALaBaisse.EstUneRemiseAZero(total, r, 0)) _attente = (r, imageDuHaut);
        return null;
    }

    public void Oublier() => _attente = null;

    /// <summary>
    /// La fenetre se ferme-t-elle ? Seulement si un depart MACHINE a ete vu (un credit consomme ou GAME_START du .MEM : un START
    /// lu au panel seul ne suffit pas, une partie lancee autrement ne rouvrirait pas la fenetre) et qu'aucun depart n'est
    /// survenu depuis le dernier point haut (un START pendant l'ecran de fin lance la partie suivante : la remise a zero est la
    /// sienne). Un continue (credit consomme) tombe lui aussi apres le point haut et laisse la fenetre ouverte. Pur.
    /// </summary>
    public static bool Ferme(bool departMachineVu, bool enJeu, long imageDuDernierDepart, long imageDuHaut)
        => departMachineVu && enJeu && imageDuDernierDepart <= imageDuHaut;
}
