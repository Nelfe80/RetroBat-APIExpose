namespace RetroBat.Api.Infrastructure;

/// <summary>
/// CE QUE LA BORNE DIT D'ELLE-MEME, en deux informations : la version d'APIExpose qui tourne,
/// et l'etat du wrapper qui mesure les scores.
///
/// Pourquoi ca existe. Un joueur a joue toute une soiree sans qu'un seul score n'arrive
/// (2026-09-22) : ses replays etaient la, donc RetroArch tournait, mais rien n'etait mesure -
/// le wrapper n'enveloppait aucun coeur. Vu de la plateforme, cette borne etait « appairee, vue
/// il y a dix minutes », et rien ne distinguait une borne qui mesure d'une borne qui ne mesure
/// pas. Il a fallu trois allers-retours avec le joueur pour le decouvrir. Ces deux champs
/// partent avec chaque releve de l'agent et avec chaque passeport : la plateforme sait alors
/// quelle version tourne et si la mesure est en place, sans rien demander a personne.
/// </summary>
public static class CabinetState
{
    private static readonly object Gate = new();
    private static string _wrapper = "unknown";

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
