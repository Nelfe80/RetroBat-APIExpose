namespace RetroBat.Providers.EmulationStation;

public class EmulationStationWatcherOptions
{
    public string GameSelectedDataDepth { get; set; } = "CachedDetails";

    /// <summary>Publish ui.game.selected(.raw) from the in-memory context as soon
    /// as events.ini is read, then enrich Details asynchronously (a second raw
    /// event follows). false restores the historical fetch-then-publish flow,
    /// where marquee/panel wait for the ES API calls (seconds under reload).</summary>
    public bool PublishSelectionBeforeDetails { get; set; } = true;

    public bool LocalProjectionOnGameSelected { get; set; } = false;
    public bool PrefetchOnGameSelected { get; set; } = false;
    public bool CreateUserVariantGuidesOnGameSelected { get; set; } = false;
    public bool QueueRemoteScrapeOnGameSelected { get; set; } = true;
    public bool EsCacheEnabled { get; set; } = true;
    public int GamesCacheTtlSeconds { get; set; } = 600;
    public int GameSelectedPerfLogThresholdMs { get; set; } = 250;
    public int GameSelectedLocalProjectionDebounceMs { get; set; } = 1000;
}
