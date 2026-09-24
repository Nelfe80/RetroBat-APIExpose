namespace RetroBat.Api.Infrastructure;

/// <summary>Une perte ou un gain de vie observé pendant la partie, avec sa valeur et sa frame.</summary>
public readonly record struct EvenementDeVie(string Address, bool Perte, int? Value, long Frame, int Player);

/// <summary>
/// OÙ SE TERMINE UN RUN : à la dernière vie perdue, pas à la fin de la partie.
///
/// 1CC veut dire « un crédit, sans continue ». Le découpage ne connaissait que les CHUTES de
/// score — or un continue d'arcade CONSERVE le score : la courbe ne retombe jamais, rien ne se
/// coupe, et la partie entière passe pour un seul run. Un joueur a ainsi vu certifier en 1CC un
/// score obtenu avec un continue, sur 19xx et sur Altered Beast (signalé le 24 septembre 2026).
/// Il l'a signalé lui-même et a demandé le retrait.
///
/// ON NE CHERCHE PAS À RECONNAÎTRE LE CONTINUE. C'est un événement ambigu : le crédit consommé
/// au démarrage ressemble à celui d'un continue, l'entrée d'un second joueur en consomme un
/// aussi, une borne en free play n'en consomme aucun, et un 1-up remonte les vies comme le ferait
/// un continue. Aucune de ces distinctions n'est fiable.
///
/// On borne le run par sa vraie fin : <b>les vies du joueur mesuré atteignent zéro</b>. Ce qui
/// vient après appartient à un autre run, quelle qu'en soit la raison. C'est littéralement ce que
/// 1CC désigne — le score au moment où la dernière vie a été perdue.
///
/// QUEL COMPTEUR. Le même que celui de l'audit 1LC, et pour la même raison : un bloc `lives` en
/// déclare plusieurs, et ils ne disent pas tous la même chose. Sur Ms. Pac-Man le compteur
/// AFFICHÉ tombe à zéro alors qu'il reste une vie à jouer — couper là tronquerait la dernière vie
/// et volerait son score au joueur. Le compteur INTERNE, lui, descend 3, 2, 1, 0 et n'atteint zéro
/// qu'à la mort finale. On retient donc le plus complet, ce qui ne se sait qu'à la fin de la
/// partie : d'où un calcul en fin de session plutôt qu'au fil de l'eau.
/// </summary>
public static class FinsDeRun
{
    /// <summary>
    /// Les frames où le run s'est terminé. Vide quand aucun compteur de vies crédible n'a parlé :
    /// on ne coupe alors rien, plutôt que de couper au hasard.
    /// </summary>
    public static IReadOnlyList<long> Calculer(IReadOnlyList<EvenementDeVie> evenements, int joueur = 1)
    {
        if (evenements is null || evenements.Count == 0)
        {
            return [];
        }

        // Un compteur par adresse, dans l'ordre d'arrivée. Les gains comptent dans la trajectoire
        // de l'adresse sans être des morts : c'est le gain du démarrage qui rend lisible la
        // descente d'un jeu à une seule vie (1 puis 0).
        var parAdresse = new Dictionary<string, List<EvenementDeVie>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in evenements)
        {
            if (e.Player > 0 && e.Player != joueur)
            {
                continue;   // les vies d'un autre joueur ne bornent pas CE run
            }

            (parAdresse.TryGetValue(e.Address, out var l) ? l : parAdresse[e.Address] = []).Add(e);
        }

        List<long>? meilleures = null;
        var meilleurPas = 0;
        foreach (var (_, suite) in parAdresse.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var (pas, zeros) = Descente(suite);
            if (pas > meilleurPas)
            {
                meilleurPas = pas;
                meilleures = zeros;
            }
        }

        return meilleures ?? [];
    }

    /// <summary>
    /// Ce que vaut une adresse : combien de pas de UN elle descend, et à quelles frames elle
    /// touche zéro.
    ///
    /// Un compteur de vies descend de un en un. Une jauge d'énergie descend aussi, mais de quatre
    /// en quatre ; un drapeau garde sa valeur ; un réglage n'a qu'une valeur isolée. Mesures du
    /// 24 septembre 2026, dix lignes sur cinq jeux.
    /// </summary>
    private static (int Pas, List<long> Zeros) Descente(List<EvenementDeVie> suite)
    {
        var pas = 0;
        var zeros = new List<long>();
        int? precedente = null;
        foreach (var e in suite)
        {
            if (e.Value is not { } v)
            {
                continue;   // sans valeur, rien à conclure de cette lecture
            }

            if (precedente is { } avant && e.Perte && v == avant - 1)
            {
                pas++;
                if (v == 0)
                {
                    // La dernière vie vient de tomber : le run s'arrête ICI.
                    zeros.Add(e.Frame);
                }
            }

            precedente = v;
        }

        return (pas, zeros);
    }
}
