using System.IO;

namespace RetroBat.Domain.Paths;

public static class RetroBatPaths
{
    private static readonly Lazy<string> PluginRootValue = new(ResolvePluginRoot);
    private static readonly Lazy<string> RetroBatRootValue = new(ResolveRetroBatRoot);

    public static string PluginRoot => PluginRootValue.Value;
    public static string RetroBatRoot => RetroBatRootValue.Value;
    public static string ToolsRoot => Path.Combine(PluginRoot, "tools");
    public static string ThemeResourcesRoot => Path.Combine(PluginRoot, "resources", "theme");
    public static string ScrapingResourcesRoot => Path.Combine(PluginRoot, "resources", "scraping");
    public static string ScrapingReferenceRoot => Path.Combine(ScrapingResourcesRoot, "reference");
    public static string ThemeHiscoreResourcesRoot => Path.Combine(ThemeResourcesRoot, "hiscore");
    public static string ThemeHiscoreParsingDbRoot => Path.Combine(ThemeHiscoreResourcesRoot, ".parsingdb");
    public static string ThemeGameInfosResourcesRoot => Path.Combine(ThemeResourcesRoot, "gameinfos");
    public static string MediaRoot => Path.Combine(PluginRoot, "media");
    public static string MediaAliasesRoot => Path.Combine(MediaRoot, "aliases");
    public static string MediaAliasesGamesRoot => Path.Combine(MediaAliasesRoot, "games");
    public static string MediaAliasesSystemsRoot => Path.Combine(MediaAliasesRoot, "systems");
    public static string MediaAliasesSharedRoot => Path.Combine(MediaAliasesRoot, "shared");
    public static string PendingScrapesPath => Path.Combine(MediaAliasesSharedRoot, "pending-scrapes.json");
    public static string BootstrapPlaceholderStatePath => Path.Combine(MediaAliasesSharedRoot, "bootstrap-placeholder-state.json");
    public static string GamelistSelectionNormalizationStatePath => Path.Combine(MediaAliasesSharedRoot, "gamelist-selection-normalization-state.json");
    public static string StartupGamelistPreparationStatePath => Path.Combine(MediaAliasesSharedRoot, "startup-gamelist-preparation-state.json");
    public static string RomPackInstallerStartupStatePath => Path.Combine(MediaAliasesSharedRoot, "rom-pack-installer-startup-state.json");
    public static string MediaSystemsRoot => Path.Combine(MediaRoot, "systems");
    public static string MediaUserRoot => Path.Combine(MediaRoot, "user");
    public static string MediaUserSystemsRoot => Path.Combine(MediaUserRoot, "systems");
    public static string RamResourcesRoot => Path.Combine(PluginRoot, "resources", "ram");
    public static string RamToolsRoot => Path.Combine(RamResourcesRoot, "tools");
    public static string DynPanelsRoot => Path.Combine(PluginRoot, "resources", "dynpanels");
    public static string DynPanelsGamesRoot => Path.Combine(DynPanelsRoot, "games");
    public static string DynPanelsSystemsRoot => Path.Combine(DynPanelsRoot, "systems");
    public static string DynPanelsCoresRoot => Path.Combine(DynPanelsRoot, "cores");
    public static string ThemePanelsResourcesRoot => Path.Combine(ThemeResourcesRoot, "panels");
    public static string EmulationStationPanelsThemeRoot => Path.Combine(EmulationStationThemesRoot, ".panels");
    public static string EmulationStationGameInfosThemeRoot => Path.Combine(EmulationStationThemesRoot, ".gameinfos");
    public static string EventsIniPath => Path.Combine(PluginRoot, "events.ini");
    public static string RomsRoot => Path.Combine(RetroBatRoot, "roms");
    public static string SavesRoot => Path.Combine(RetroBatRoot, "saves");
    public static string EmulationStationConfigRoot => Path.Combine(RetroBatRoot, "emulationstation", ".emulationstation");
    public static string EmulationStationFeaturesPath => Path.Combine(EmulationStationConfigRoot, "es_features.cfg");
    public static string EmulationStationSettingsPath => Path.Combine(EmulationStationConfigRoot, "es_settings.cfg");
    public static string EmulationStationScriptsRoot => Path.Combine(EmulationStationConfigRoot, "scripts");
    public static string EmulationStationThemesRoot => Path.Combine(EmulationStationConfigRoot, "themes");
    public static string EmulationStationThemeMediaRoot => Path.Combine(EmulationStationThemesRoot, ".media");
    public static string EmulationStationThemeMediasRoot => Path.Combine(EmulationStationThemesRoot, ".medias");
    public static string EmulatorLauncherLogPath => Path.Combine(RetroBatRoot, "emulationstation", "emulatorLauncher.log");
    public static string RetroArchConfigPath => Path.Combine(RetroBatRoot, "emulators", "retroarch", "retroarch.cfg");
    public static string MameIniPath => Path.Combine(RetroBatRoot, "bios", "mame", "ini", "mame.ini");
    public static string EmulatorMameIniPath => Path.Combine(RetroBatRoot, "emulators", "mame", "mame.ini");
    public static string MamePluginIniPath => Path.Combine(RetroBatRoot, "emulators", "mame", "plugin.ini");
    public static string BiosMamePluginIniPath => Path.Combine(RetroBatRoot, "bios", "mame", "ini", "plugin.ini");
    public static string BiosMamePluginsRoot => Path.Combine(RetroBatRoot, "bios", "mame", "plugins");
    public static string EmulatorMamePluginsRoot => Path.Combine(RetroBatRoot, "emulators", "mame", "plugins");
    public static string MameHiscorePluginConfigPath => Path.Combine(RetroBatRoot, "emulators", "mame", "hiscore", "plugin.cfg");
    public static string BiosMameHiscoreRoot => Path.Combine(RetroBatRoot, "bios", "mame", "hiscore");
    public static string BiosMameHiscoreDatPath => Path.Combine(RetroBatRoot, "bios", "mame", "hiscore.dat");
    public static string EmulatorMameHiscoreDatPath => Path.Combine(RetroBatRoot, "emulators", "mame", "plugins", "hiscore", "hiscore.dat");

    private static string ResolvePluginRoot()
    {
        foreach (var candidate in GetCandidateDirectories())
        {
            for (var current = new DirectoryInfo(candidate); current != null; current = current.Parent)
            {
                if (LooksLikePluginRoot(current))
                {
                    return current.FullName;
                }
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveRetroBatRoot()
    {
        var pluginRoot = new DirectoryInfo(PluginRoot);
        var pluginsDir = pluginRoot.Parent;
        if (pluginsDir?.Parent != null && pluginsDir.Name.Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            return pluginsDir.Parent.FullName;
        }

        return pluginRoot.FullName;
    }

    private static IEnumerable<string> GetCandidateDirectories()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
    }

    private static bool LooksLikePluginRoot(DirectoryInfo directory)
    {
        return Directory.Exists(Path.Combine(directory.FullName, "tools"))
            && Directory.Exists(Path.Combine(directory.FullName, "resources"));
    }
}
