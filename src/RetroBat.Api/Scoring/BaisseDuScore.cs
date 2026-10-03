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
/// </summary>
public sealed class ArretALaBaisse
{
    private long? _avantLaBaisse;

    /// <summary>Une baisse attend sa confirmation.</summary>
    public bool EnAttente => _avantLaBaisse is not null;

    /// <summary>
    /// Vrai quand une baisse est confirmee par cette lecture : il faut arreter le replay.
    /// <paramref name="enregistrement"/> : un replay s'enregistre ; <paramref name="monteeVue"/> :
    /// le score y a monte.
    /// </summary>
    public bool Lire(long total, long? reference, bool nouvelleSalve, bool enregistrement, bool monteeVue)
    {
        if (_avantLaBaisse is { } avant)
        {
            // Dans la meme salve, rien n'est encore confirme ni dementi.
            if (!nouvelleSalve) return false;
            _avantLaBaisse = null;
            if (total < avant) return enregistrement;
        }

        if (enregistrement && monteeVue && reference is { } r && total < r)
        {
            _avantLaBaisse = r;
        }

        return false;
    }

    /// <summary>Oublie une baisse en attente (nouvel enregistrement, nouvelle partie).</summary>
    public void Oublier() => _avantLaBaisse = null;
}
