namespace RetroBat.Domain.Models;

public class GameState
{
    public string Frontend { get; set; } = "emulationstation";
    public string State { get; set; } = "browsing";
    public SystemDetails? SelectedSystem { get; set; }
    public GameReference? Selected { get; set; }
    public GameReference? Running { get; set; }
}

public class GameReference
{
    public string SystemId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public GameDetails? Details { get; set; }
    public LaunchDetails? Launch { get; set; }
}

public class LaunchDetails
{
    public string SourceLog { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }
    public string StartupCommand { get; set; } = string.Empty;
    public string RunningCommand { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Emulator { get; set; } = string.Empty;
    public string Core { get; set; } = string.Empty;
    public string RomPath { get; set; } = string.Empty;
}

public class SystemDetails
{
    public string Name { get; set; } = string.Empty;
    public string Fullname { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Logo { get; set; } = string.Empty;
    public string Release { get; set; } = string.Empty;
    public string Hardware { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    
    // For specific settings extracted from es_settings.cfg (e.g. mame.emulator)
    public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
}

public class GameDetails
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Video { get; set; } = string.Empty;
    public string Marquee { get; set; } = string.Empty;
    public string Thumbnail { get; set; } = string.Empty;
    public string Developer { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
    
    // Nouveaux champs demandés
    public string SystemName { get; set; } = string.Empty;
    public string Emulator { get; set; } = string.Empty;
    public string Fanart { get; set; } = string.Empty;
    public string Bezel { get; set; } = string.Empty;
    public string Boxback { get; set; } = string.Empty;
    public string Map { get; set; } = string.Empty;
    public string Manual { get; set; } = string.Empty;
    public string Releasedate { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Genres { get; set; } = string.Empty;
    public string Arcadesystemname { get; set; } = string.Empty;
    public string Players { get; set; } = string.Empty;
    public string Favorite { get; set; } = string.Empty;
    public string Hidden { get; set; } = string.Empty;
    public string Kidgame { get; set; } = string.Empty;
    public string Playcount { get; set; } = string.Empty;
    public string Lastplayed { get; set; } = string.Empty;
    public string Gametime { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ScraperId { get; set; } = string.Empty;
    public string ScrapName { get; set; } = string.Empty;
    public string ScrapDate { get; set; } = string.Empty;
    
    // Support pour les balises personnalisées ou les nouveautés du gamelist
    public Dictionary<string, string> Extras { get; set; } = new Dictionary<string, string>();
}

public class NodeState
{
    public string NodeId { get; set; } = "cab-01";
    public string Mode { get; set; } = "standalone";
}

public class ApiContext
{
    public string SchemaVersion { get; set; } = "1.0";
    public NodeState Node { get; set; } = new NodeState();
    public GameState Ui { get; set; } = new GameState();
    public DateTime UtcTime => DateTime.UtcNow;
}
