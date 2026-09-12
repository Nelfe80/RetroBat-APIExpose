namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Reglages du Data Pack officiel (voir <see cref="DataPackSyncService"/>).
/// Section de configuration : <c>ApiExpose:DataPack</c>.
/// </summary>
public class DataPackOptions
{
    /// <summary>Tirer le Data Pack officiel en tache de fond.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Depot public GitHub (owner/name) du Data Pack : un dossier par partie de resources/.</summary>
    public string Repository { get; set; } = "Nelfe80/RetroBat-DataPack";

    /// <summary>Branche suivie.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// La release du meme depot qui porte les bases par systeme (gamelist/systems, jusqu'a
    /// 161 Mo le fichier, trop gros pour l'historique git) : un actif par systeme et un
    /// manifeste d'empreintes.
    /// </summary>
    public string GamelistReleaseTag { get; set; } = "gamelist";

    /// <summary>Intervalle entre deux synchronisations, en heures.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Attente apres le demarrage avant la premiere synchro : le boot d'abord.</summary>
    public int StartupDelaySeconds { get; set; } = 120;

    /// <summary>Telechargements menes de front.</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>
    /// Les dossiers de resources/ que le Data Pack a le droit d'ecrire. Tout chemin du depot
    /// hors de ces dossiers est ignore : ni le README, ni les outils, ni un dossier qu'on
    /// n'attendait pas.
    /// </summary>
    public List<string> Folders { get; set; } = new()
    {
        "ram", "dynpanels", "gamelist", "controls", "config-ESmenus", "locales",
        "scraping", "startup-overlay", "theme",
    };
}
