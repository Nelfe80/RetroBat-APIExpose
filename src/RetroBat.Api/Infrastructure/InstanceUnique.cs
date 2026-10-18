namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UNE SEULE API PAR SESSION WINDOWS (2026-10-18, theJim).
///
/// Une seconde RetroBat.Api.exe lancee pendant que la premiere tourne (un double-clic dans le dossier d'APIExpose)
/// demarrait ses services, remettait a zero le journal de la premiere, puis tombait sur le port 12345 deja pris :
/// exception non geree et fenetre « RetroBat.Api a cesse de fonctionner ». Le verrou se prend avant tout le reste ; s'il
/// est tenu, la seconde attend un peu (une mise a jour remplace la premiere, qui sort a l'instant), puis se retire sans
/// rien toucher. L'auto-test du diagnostic tourne a cote de l'API et ne le prend pas.
/// </summary>
public static class InstanceUnique
{
    internal const string Nom = @"Local\NelfePlay.RetroBat.Api";

    /// <summary>L'attente d'une API qui sort (le programme de mise a jour l'arrete puis lance la nouvelle).</summary>
    public static readonly TimeSpan AttenteDeLaPrecedente = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Le verrou de l'API, pris tout de suite ou dans le delai ; null si une autre API le garde. Une API morte sans le
    /// rendre le laisse « abandonne » : il est alors a nous.
    /// </summary>
    public static Mutex? Prendre(TimeSpan attente, Action? quandOccupe = null, string nom = Nom)
    {
        var verrou = new Mutex(false, nom);
        try
        {
            if (verrou.WaitOne(TimeSpan.Zero)) return verrou;
            quandOccupe?.Invoke();
            if (verrou.WaitOne(attente)) return verrou;
        }
        catch (AbandonedMutexException)
        {
            return verrou;
        }
        verrou.Dispose();
        return null;
    }

    /// <summary>Ce que voit le joueur dans la fenetre de la seconde API, juste avant qu'elle se ferme.</summary>
    public static void Annoncer()
    {
        int? autre = null;
        try
        {
            autre = System.Diagnostics.Process.GetProcessesByName("RetroBat.Api")
                .Select(p => p.Id).FirstOrDefault(id => id != Environment.ProcessId) is var id and > 0 ? id : null;
        }
        catch (Exception)
        {
            // Sans la liste des processus, le message reste juste.
        }
        Console.WriteLine("APIExpose est deja lancee" + (autre is null ? "" : " (PID " + autre + ")")
            + " : cette fenetre se ferme, l'API en service continue.");
        Console.WriteLine("APIExpose is already running: this window closes, the running API carries on.");
        if (!Console.IsOutputRedirected) Thread.Sleep(TimeSpan.FromSeconds(5));
    }
}
