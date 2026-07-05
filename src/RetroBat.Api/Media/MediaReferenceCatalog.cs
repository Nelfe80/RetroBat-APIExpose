using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public class MediaReferenceCatalog
{
    public string SystemesScreenScraperJsonPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "systems_screenscraper.json");
    public string SystemesScreenScraperLegacyPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "systems_screenscraper.txt");
    public string CrcNoCalculPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "crc_no_calcul.txt");
    public string ArcadeSystemsPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "arcade_systems_list.txt");
    public string FolderSystemsPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "systems_as_folder.txt");
    public string FolderSearchDepthPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "folder_search_depth_per_system.txt");
    public string RemoveArcadeBiosPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "remove_list_arcade_bios.txt");
    public string CustomGameNamesPath => Path.Combine(RetroBatPaths.ScrapingReferenceRoot, "custom_games_names.txt");
}
