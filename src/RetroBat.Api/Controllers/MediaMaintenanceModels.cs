namespace RetroBat.Api.Controllers;

public class ApiErrorResponse
{
    public string Error { get; set; } = string.Empty;
}

public class ScrapingStatusResponse
{
    public bool RemoteScrapingEnabled { get; set; }
    public bool LegacyScrapingArchived { get; set; }
    public string LegacyArchivePath { get; set; } = string.Empty;
    public bool LocalProjectionEnabled { get; set; }
    public string EsGameIdStrategy { get; set; } = string.Empty;
    public string EsGameIdFormula { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Remote { get; set; }
}

public class MediaScrapeRequest
{
    /// <summary>
    /// RetroBat frontend system id, for example <c>nes</c>, <c>snes</c>, <c>mame</c> or <c>fbneo</c>.
    /// </summary>
    public string SystemId { get; set; } = string.Empty;

    /// <summary>
    /// Game path as stored in the system gamelist, usually relative to the system ROM folder.
    /// </summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional display name used only as a fallback when resolving the target game.
    /// </summary>
    public string? GameName { get; set; }
}

public class MediaMaintenanceResponse
{
    public string Action { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public int DeletedEsMediaFiles { get; set; }
    public int RemovedGamelistTags { get; set; }
    public int DeletedCanonicalFiles { get; set; }
    public int ClearedFailureEntries { get; set; }
    public int ClearedAvailabilityEntries { get; set; }
    public int ClearedMediaHashEntries { get; set; }
    public bool QueuedRemoteScrape { get; set; }
}

public class GamelistGenerationRequest
{
    /// <summary>
    /// Generation scope: <c>game</c>, <c>system</c> or <c>all</c>.
    /// </summary>
    public string Scope { get; set; } = string.Empty;
    public string? SystemId { get; set; }
    public string? GamePath { get; set; }
    public string? GameName { get; set; }
}

public class GamelistGenerationResponse
{
    public string Scope { get; set; } = string.Empty;
    public List<string> Systems { get; set; } = new();
    public int SystemsProcessed { get; set; }
    public int SystemsUpdated { get; set; }
    public int GamesProcessed { get; set; }
    public int GamesWithLocalMedia { get; set; }
    public bool ReloadGamesRequested { get; set; }
}

public class LocalGamelistUpdateRequest
{
    /// <summary>
    /// Update scope: <c>game</c>, <c>system</c> or <c>all</c>.
    /// </summary>
    public string Scope { get; set; } = "all";
    public string? SystemId { get; set; }
    public string? GamePath { get; set; }
    public string? GameName { get; set; }
    public string? GameSlug { get; set; }
    public string? PreferredLanguage { get; set; }
    public bool SuppressTaskProgress { get; set; }
}

public class LocalGamelistUpdateResponse
{
    public string Scope { get; set; } = string.Empty;
    public List<string> Systems { get; set; } = new();
    public int SystemsProcessed { get; set; }
    public int SystemsUpdated { get; set; }
    public int SystemsFailed { get; set; }
    public int GamesProcessed { get; set; }
    public int GamesUpdated { get; set; }
    public int MediaTagsUpdated { get; set; }
    public int TextTagsUpdated { get; set; }
    public int LocalMediaCandidates { get; set; }
    public bool ReloadGamesRequested { get; set; }
    public List<LocalGamelistUpdateSystemResult> SystemResults { get; set; } = new();
}

public class LocalGamelistUpdateSystemResult
{
    public string SystemId { get; set; } = string.Empty;
    public string GamelistPath { get; set; } = string.Empty;
    public bool Changed { get; set; }
    public bool Failed { get; set; }
    public int GamesProcessed { get; set; }
    public int GamesUpdated { get; set; }
    public int MediaTagsUpdated { get; set; }
    public int TextTagsUpdated { get; set; }
    public int LocalMediaCandidates { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public class MetadataNormalizationRequest
{
    /// <summary>
    /// Normalization scope: <c>game</c>, <c>system</c> or <c>all</c>.
    /// </summary>
    public string Scope { get; set; } = "all";
    public string? SystemId { get; set; }
    public string? GamePath { get; set; }
    public string? GameName { get; set; }
    public string? GameSlug { get; set; }
    public string? PreferredLanguage { get; set; }
    public bool RebuildFromScreenScraperCache { get; set; }
    public bool NormalizeExistingGamelists { get; set; }
    public bool UpdateGamelists { get; set; }
}

public class MetadataNormalizationResponse
{
    public string Scope { get; set; } = string.Empty;
    public List<string> Systems { get; set; } = new();
    public int SystemsProcessed { get; set; }
    public int MetadataFilesScanned { get; set; }
    public int MetadataFilesUpdated { get; set; }
    public int MetadataFilesRemoved { get; set; }
    public int MetadataFilesFailed { get; set; }
    public int FieldsNormalized { get; set; }
    public int RawCachePayloadsScanned { get; set; }
    public int RawCachePayloadsFailed { get; set; }
    public int RawCacheMetadataBundlesWritten { get; set; }
    public int RawCacheMetadataBundlesSkipped { get; set; }
    public bool GamelistsUpdated { get; set; }
    public GamelistTextNormalizationResponse? GamelistTextNormalization { get; set; }
    public LocalGamelistUpdateResponse? GamelistUpdate { get; set; }
    public List<MetadataNormalizationSystemResult> SystemResults { get; set; } = new();
}

public class MetadataNormalizationSystemResult
{
    public string SystemId { get; set; } = string.Empty;
    public int MetadataFilesScanned { get; set; }
    public int MetadataFilesUpdated { get; set; }
    public int MetadataFilesRemoved { get; set; }
    public int MetadataFilesFailed { get; set; }
    public int FieldsNormalized { get; set; }
    public int RawCachePayloadsScanned { get; set; }
    public int RawCachePayloadsFailed { get; set; }
    public int RawCacheMetadataBundlesWritten { get; set; }
    public int RawCacheMetadataBundlesSkipped { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class GamelistTextNormalizationResponse
{
    public string Scope { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = string.Empty;
    public List<string> Systems { get; set; } = new();
    public int SystemsProcessed { get; set; }
    public int GamelistsScanned { get; set; }
    public int GamelistsUpdated { get; set; }
    public int GamesScanned { get; set; }
    public int FieldsScanned { get; set; }
    public int FieldsNormalized { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<GamelistTextNormalizationSystemResult> SystemResults { get; set; } = new();
}

public class GamelistTextNormalizationSystemResult
{
    public string SystemId { get; set; } = string.Empty;
    public string GamelistPath { get; set; } = string.Empty;
    public bool GamelistScanned { get; set; }
    public bool GamelistUpdated { get; set; }
    public int GamesScanned { get; set; }
    public int FieldsScanned { get; set; }
    public int FieldsNormalized { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class GamelistRefreshSelectionsRequest
{
    /// <summary>
    /// Refresh scope: <c>system</c> or <c>all</c>.
    /// </summary>
    public string Scope { get; set; } = "system";
    public string? SystemId { get; set; }
}

public class GamelistRefreshSelectionsResponse
{
    public string Scope { get; set; } = string.Empty;
    public List<string> Systems { get; set; } = new();
    public int SystemsProcessed { get; set; }
    public int SystemsUpdated { get; set; }
    public bool ReloadGamesRequested { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class GamelistConsolidateRequest
{
    public string SystemId { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
    public string? RichGamelistPath { get; set; }
    public int MinimumRichMediaTags { get; set; } = 1;
    public bool OverwriteExistingMedia { get; set; }
    public bool IncludeTextMetadata { get; set; }
}

public class GamelistConsolidateResponse
{
    public string SystemId { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string GamelistPath { get; set; } = string.Empty;
    public string RichGamelistPath { get; set; } = string.Empty;
    public int CurrentGames { get; set; }
    public int CurrentGamesWithMedia { get; set; }
    public int CurrentMediaTags { get; set; }
    public int RichGames { get; set; }
    public int RichGamesWithMedia { get; set; }
    public int RichMediaTags { get; set; }
    public int GamesProcessed { get; set; }
    public int GamesMatched { get; set; }
    public int GamesUpdated { get; set; }
    public int TagsRestored { get; set; }
    public bool Saved { get; set; }
    public bool ReloadGamesRequested { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class LocalScrapePreviewRequest
{
    /// <summary>
    /// Preview scope: <c>game</c>, <c>system</c> or <c>all</c>.
    /// </summary>
    public string Scope { get; set; } = "game";

    /// <summary>
    /// RetroBat frontend system id, for example <c>snes</c>, <c>mame</c> or <c>fbneo</c>.
    /// Required for <c>game</c> and <c>system</c> scopes.
    /// </summary>
    public string? SystemId { get; set; }

    /// <summary>
    /// Game path as stored in the gamelist, or a ROM file path/name. Required for <c>game</c> scope.
    /// </summary>
    public string? GamePath { get; set; }

    /// <summary>
    /// Optional display name used to resolve a gamelist entry when the path is not enough.
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// Preferred media regions, ordered by priority. Defaults to <c>fr, eu, wor, us, jp</c>.
    /// </summary>
    public List<string> PreferredMediaRegions { get; set; } = new();

    /// <summary>
    /// Media kinds to preview. Empty means the default visible/common set.
    /// </summary>
    public List<string> MediaKinds { get; set; } = new();

    /// <summary>
    /// Includes missing media slots in the response.
    /// </summary>
    public bool IncludeMissing { get; set; } = true;

    /// <summary>
    /// Maximum games to evaluate for broad scopes. Use <c>0</c> for no explicit limit.
    /// </summary>
    public int MaxGames { get; set; } = 200;

    /// <summary>
    /// Includes root media pack ZIP detection in the dry-run response.
    /// </summary>
    public bool IncludeRootMediaPacks { get; set; } = true;

    /// <summary>
    /// Maximum media entries sampled per root ZIP pack.
    /// </summary>
    public int MaxPackEntries { get; set; } = 200;
}

public class LocalScrapePreviewResponse
{
    public string Scope { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
    public bool WritesPlanned { get; set; }
    public bool RemoteScrapingEnabled { get; set; }
    public List<string> Systems { get; set; } = new();
    public List<string> PreferredMediaRegions { get; set; } = new();
    public List<string> MediaKinds { get; set; } = new();
    public int ScannedMediaFiles { get; set; }
    public int ParsedMediaFiles { get; set; }
    public int GamesEvaluated { get; set; }
    public int GamesWithLocalMedia { get; set; }
    public int ExactMatches { get; set; }
    public int InheritedMatches { get; set; }
    public int MissingMediaSlots { get; set; }
    public bool Truncated { get; set; }
    public int RootMediaPackCount { get; set; }
    public List<LocalMediaPackPreview> RootMediaPacks { get; set; } = new();
    public List<LocalScrapeGamePreview> Games { get; set; } = new();
}

public class LocalMediaPackPreview
{
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DestinationHint { get; set; } = string.Empty;
    public int TotalEntries { get; set; }
    public int MediaEntries { get; set; }
    public int ParsedMediaEntries { get; set; }
    public int UnsafeEntries { get; set; }
    public bool Truncated { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<LocalMediaPackEntryPreview> SampleEntries { get; set; } = new();
}

public class LocalMediaPackEntryPreview
{
    public string EntryPath { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public string FamilySlug { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class LocalScrapeGamePreview
{
    public string SystemId { get; set; } = string.Empty;
    public string MediaSystemId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public string FamilySlug { get; set; } = string.Empty;
    public string RomRegion { get; set; } = string.Empty;
    public bool IsArcadeLike { get; set; }
    public List<LocalScrapeMediaPreview> Media { get; set; } = new();
}

public class LocalScrapeMediaPreview
{
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Match { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceGameSlug { get; set; } = string.Empty;
    public string TargetRelativePath { get; set; } = string.Empty;
    public bool VolatileTarget { get; set; } = true;
}
