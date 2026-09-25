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
/// ON COUPE AU CONTINUE, PAS À ZÉRO (2026-09-25, mesuré sur 19xx). Zéro ne dit pas la même chose
/// d'un jeu à l'autre : dernière mort sur le compteur interne de Ms. Pac-Man, mais « plus de vie en
/// RÉSERVE, la dernière se joue » sur 19xx. Couper à zéro y retirait la dernière vie : 5 300
/// certifiés pour un 1CC de 8 700, et 14 700 pour une partie sans aucun continue.
///
/// Ce qui marque le continue, dans les deux cas, c'est que les vies REMONTENT après zéro, au moins
/// au niveau du départ (sur 19xx : 2 → 1 → 0, puis 2 au continue). Un 1-up sur la dernière vie ne
/// remonte que d'un cran : il ne coupe pas. On coupe donc à la remontée, et la dernière vie jouée
/// avec le premier crédit compte. Sans continue, rien ne se coupe : la partie entière est le run.
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

        // LE COMPTEUR QUI DESCEND LE PLUS ET REMONTE LE MOINS. Un vrai compteur de vies ne remonte
        // qu'a un 1-up ou a un continue. Mesure sur Altered Beast le 2026-09-25 : « FLAG BOSS DEATH »,
        // range dans le bloc des vies, descendait 14 fois d'un cran mais remontait 20 fois ; les vies
        // (0xFFE018 : 2, 1, 0) descendaient 2 fois et ne remontaient jamais. Au seul nombre de
        // descentes, le drapeau l'emportait et aurait coupe la partie n'importe ou.
        List<long>? meilleures = null;
        var meilleurScore = 0;
        foreach (var (_, suite) in parAdresse.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var (pas, remontees, zeros) = Descente(suite);
            var score = pas - remontees;
            if (pas > 0 && score > meilleurScore)
            {
                meilleurScore = score;
                meilleures = zeros;
            }
        }

        return meilleures ?? [];
    }

    /// <summary>
    /// Ce que vaut une adresse : combien de pas de UN elle descend, et à quelles frames ses vies
    /// sont REMONTÉES après zéro au niveau du départ (un continue).
    ///
    /// Un compteur de vies descend de un en un. Une jauge d'énergie descend aussi, mais de quatre
    /// en quatre ; un drapeau garde sa valeur ; un réglage n'a qu'une valeur isolée. Mesures du
    /// 24 septembre 2026, dix lignes sur cinq jeux.
    /// </summary>
    private static (int Pas, int Remontees, List<long> Zeros) Descente(List<EvenementDeVie> suite)
    {
        var pas = 0;
        var remontees = 0;
        var zeros = new List<long>();
        int? precedente = null;
        var depart = 0;        // le plus haut niveau vu avant de toucher zéro : les vies du départ
        var aZero = false;     // le compteur a touché zéro ; on attend de voir s'il remonte
        foreach (var e in suite)
        {
            if (e.Value is not { } v)
            {
                continue;   // sans valeur, rien à conclure de cette lecture
            }

            // Une perte lue « v » vient de v + 1 : c'est ce qui révèle le départ quand la première
            // lecture est déjà une mort (19xx : 1, donc parti de 2).
            if (!aZero) depart = Math.Max(depart, e.Perte ? v + 1 : v);

            if (precedente is { } avant && e.Perte && v == avant - 1)
            {
                pas++;
                if (v == 0) aZero = true;
            }
            else if (aZero && precedente == 0 && v >= Math.Max(1, depart))
            {
                // Les vies REMONTENT au niveau du départ : un nouveau crédit. Le run du premier
                // s'arrête ICI, dernière vie comprise. C'est ce qu'un vrai compteur de vies fait au
                // continue : cette remontée-là ne le rend pas suspect.
                zeros.Add(e.Frame);
                aZero = false;
                depart = v;
            }
            else if (precedente is { } avantR && v > avantR)
            {
                remontees++;   // un 1-up, ou un compteur qui n'est pas celui des vies
            }

            precedente = v;
        }

        return (pas, remontees, zeros);
    }
}
