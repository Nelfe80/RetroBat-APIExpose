namespace RetroBat.Providers.EmulationStation;

public class EmulationStationWatcherOptions
{
    public string GameSelectedDataDepth { get; set; } = "CachedDetails";

    /// <summary>Publish ui.game.selected(.raw) from the in-memory context as soon
    /// as events.ini is read, then enrich Details asynchronously (a second raw
    /// event follows). false restores the historical fetch-then-publish flow,
    /// where marquee/panel wait for the ES API calls (seconds under reload).</summary>
    public bool PublishSelectionBeforeDetails { get; set; } = true;

    /// <summary>ES's script queue drains one process per hovered game AFTER the
    /// user stops: events.ini then replays every intermediate selection, spaced
    /// wider than any debounce, and marquee/panel faithfully replay the journal.
    /// The burst gate publishes the first selection immediately (leading edge),
    /// coalesces selections arriving closer than SelectionBurstQuietMs to the
    /// most recent one (published after the stream goes quiet), and samples a
    /// long burst every SelectionBurstProgressMs so the display still follows.
    /// false restores one full pipeline pass per events.ini write.</summary>
    public bool CoalesceSelectionBursts { get; set; } = true;

    public int SelectionBurstQuietMs { get; set; } = 600;

    public int SelectionBurstProgressMs { get; set; } = 800;

    public bool LocalProjectionOnGameSelected { get; set; } = false;
    public bool PrefetchOnGameSelected { get; set; } = false;
    public bool CreateUserVariantGuidesOnGameSelected { get; set; } = false;
    public bool QueueRemoteScrapeOnGameSelected { get; set; } = true;
    public bool EsCacheEnabled { get; set; } = true;
    public int GamesCacheTtlSeconds { get; set; } = 600;
    public int GameSelectedPerfLogThresholdMs { get; set; } = 250;
    public int GameSelectedLocalProjectionDebounceMs { get; set; } = 1000;
}
