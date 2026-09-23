namespace RetroBat.Api.Infrastructure;

/// <summary>
/// CE QUE LA BORNE DIT D'ELLE-MEME : la version d'APIExpose qui tourne, l'etat du wrapper qui
/// mesure les scores, et avec quel coeur elle lancera chaque jeu ouvert au scoring.
///
/// Pourquoi ca existe. Un joueur a joue toute une soiree sans qu'un seul score n'arrive
/// (2026-09-22) : ses replays etaient la, donc RetroArch tournait, mais rien n'etait mesure -
/// le wrapper n'enveloppait aucun coeur. Vu de la plateforme, cette borne etait « appairee, vue
/// il y a dix minutes », et rien ne distinguait une borne qui mesure d'une borne qui ne mesure
/// pas. Il a fallu trois allers-retours avec le joueur pour le decouvrir. Ces champs
/// partent avec chaque releve de l'agent et avec chaque passeport : la plateforme sait alors
/// quelle version tourne et si la mesure est en place, sans rien demander a personne.
/// </summary>
public static class CabinetState
{
    private static readonly object Gate = new();
    private static string _wrapper = "unknown";
    private static string _coeurs = "";

    /// <summary>La version d'APIExpose, telle que l'assemblage la porte (« 1.8.23+... »).</summary>
    public static string Version { get; } =
        typeof(CabinetState).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(CabinetState).Assembly.GetName().Version?.ToString()
        ?? "";

    /// <summary>
    /// L'etat du wrapper en un mot lisible : « ok:12 » (douze coeurs enveloppes), « none:0 »
    /// (aucun : rien ne sera mesure), « skipped » (deploiement saute, RetroArch tournait),
    /// « missing » (la DLL du wrapper n'est pas la), « unknown » (pas encore audite).
    /// </summary>
    public static string Wrapper
    {
        get { lock (Gate) { return _wrapper; } }
    }

    /// <summary>
    /// AVEC QUOI CETTE BORNE LANCERA CHAQUE JEU OUVERT, sous la forme
    /// « double-dragon=fbneo,ms-pac-man=fbneo ». Chaine vide tant que la collection World
    /// Scoring n'a pas tourne.
    ///
    /// Le coeur ne se devine pas depuis la plateforme : RetroBat le resout chez le joueur, a
    /// partir de ses propres reglages puis de son es_systems.cfg. Une borne peut donc lancer
    /// FBNeo la ou une autre lance MAME, pour le meme jeu. La fiche du jeu peut alors dire au
    /// lecteur ce que SA borne fera, au lieu d'une moyenne qui ne serait vraie nulle part.
    /// </summary>
    public static string Coeurs
    {
        get { lock (Gate) { return _coeurs; } }
    }

    /// <summary>
    /// Note ce que la borne lancera, a partir de ce que la collection vient de retenir. Le
    /// format tient dans un en-tete HTTP : ASCII, coupe a 480 caracteres, les jeux au-dela
    /// sont simplement tus - une liste tronquee vaut mieux qu'un appel refuse.
    /// </summary>
    public static void NoterCoeurs(IEnumerable<KeyValuePair<string, string>> parJeu)
    {
        var morceaux = new List<string>();
        var taille = 0;
        foreach (var (jeu, coeur) in parJeu.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!Propre(jeu) || !Propre(coeur))
            {
                continue;
            }

            var morceau = jeu + "=" + coeur;
            if (taille + morceau.Length + 1 > 480)
            {
                break;
            }

            morceaux.Add(morceau);
            taille += morceau.Length + 1;
        }

        var valeur = string.Join(",", morceaux);
        lock (Gate) { _coeurs = valeur; }
    }

    /// <summary>Un identifiant transportable tel quel dans un en-tete.</summary>
    private static bool Propre(string valeur) =>
        valeur.Length is > 0 and <= 120 && valeur.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>Note l'etat a partir du dernier audit du deploiement.</summary>
    public static void NoterWrapper(RetroArchWrapperDeploymentResult r)
    {
        var etat = !r.WrapperExists ? "missing"
            : r.SkippedBecauseRetroArchRunning ? "skipped:" + r.WrappedCores
            : r.WrappedCores > 0 ? "ok:" + r.WrappedCores
            : "none:0";
        lock (Gate) { _wrapper = etat; }
    }
}
