namespace RetroBat.Providers.EmulationStation;

public class EmulationStationWatcherOptions
{
    public string GameSelectedDataDepth { get; set; } = "CachedDetails";
    public bool LocalProjectionOnGameSelected { get; set; } = false;
    public bool PrefetchOnGameSelected { get; set; } = false;
    public bool CreateUserVariantGuidesOnGameSelected { get; set; } = false;
    public bool QueueRemoteScrapeOnGameSelected { get; set; } = true;
    public bool EsCacheEnabled { get; set; } = true;
    public int GamesCacheTtlSeconds { get; set; } = 600;
    public int GameSelectedPerfLogThresholdMs { get; set; } = 250;
    public int GameSelectedLocalProjectionDebounceMs { get; set; } = 1000;
}
