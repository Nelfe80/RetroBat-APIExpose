using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class RomSetManagerService
{
    private const string ApiHiddenTag = "apiexpose_romset_hidden";
    private const string ApiReasonsTag = "apiexpose_romset_reasons";
    private const string ApiOriginalHiddenTag = "apiexpose_romset_original_hidden";
    private const string BackupDirectoryName = ".api-expose-gamelist-backups";
    private static readonly HashSet<string> IgnoredAllSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "ports",
        "retrobat",
        "screenshots"
    };
    private static readonly Regex NonAlphaNumericRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TagRegex = new(@"[\(\[]([^\)\]]+)[\)\]]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] TagSeparators = { ',', '/', ';', '+' };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly MediaRuntimeState _runtimeState;
    private readonly ITaskProgressService _taskProgressService;
    private readonly IGamelistStore _gamelistStore;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ApiExposeTaxonomyService _taxonomy;
    private readonly ILogger<RomSetManagerService>? _logger;

    public RomSetManagerService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiExposeRuntimeOptionsService runtimeOptions,
        MediaRuntimeState runtimeState,
        ITaskProgressService taskProgressService,
        IGamelistStore gamelistStore,
        GamelistUpdateService gamelistUpdateService,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService,
        ApiExposeTaxonomyService taxonomy,
        ILogger<RomSetManagerService>? logger = null)
    {
        _options = options;
        _runtimeOptions = runtimeOptions;
        _runtimeState = runtimeState;
        _taskProgressService = taskProgressService;
        _gamelistStore = gamelistStore;
        _gamelistUpdateService = gamelistUpdateService;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
        _taxonomy = taxonomy;
        _logger = logger;
    }

    public RomSetManagerOptionsSnapshot GetOptions()
    {
        return BuildEffectiveOptions(ReadEsSettings());
    }

    public async Task<RomSetManagerApplyResponse> AuditAsync(RomSetManagerApplyRequest request, CancellationToken cancellationToken = default)
    {
        request.DryRun = true;
        return await ApplyAsync(request, cancellationToken);
    }

    public async Task<RomSetManagerApplyResponse> ApplyAsync(RomSetManagerApplyRequest request, CancellationToken cancellationToken = default)
    {
        var options = GetOptions();
        var systems = ResolveRequestedSystems(request);
        var response = new RomSetManagerApplyResponse
        {
            DryRun = request.DryRun,
            Restore = false,
            Enabled = options.Enabled,
            Options = options
        };
        response.Systems.AddRange(systems);

        if (!options.Enabled)
        {
            response.Message = "Roms Manager disabled.";
            return response;
        }

        if (systems.Count == 0)
        {
            response.Message = "No systems requested.";
            return response;
        }

        var progress = !request.DryRun;
        var language = _settingsService.GetScrapingSettings().Language;
        if (progress)
        {
            _taskProgressService.Report("rom-set-manager", "Roms Manager", 0, systems.Count, _interfaceTextService.Text("progress.romset.analysis", language));
        }

        try
        {
            for (var index = 0; index < systems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progress)
                {
                    _taskProgressService.Report("rom-set-manager", "Roms Manager", index, systems.Count, systems[index]);
                }

                var systemResult = await ApplySystemAsync(
                    systems[index],
                    options,
                    request.DryRun,
                    restore: false,
                    claimExistingHidden: request.ClaimExistingHidden,
                    cancellationToken);
                response.Results.Add(systemResult);
                response.GamesScanned += systemResult.GamesScanned;
                response.GamesMatched += systemResult.GamesMatched;
                response.GamesToHide += systemResult.GamesToHide;
                response.GamesToRestore += systemResult.GamesToRestore;
                response.GamesChanged += systemResult.GamesChanged;
                response.VariantGroupsAnalyzed += systemResult.VariantDecisions.Count;
                response.Warnings.AddRange(systemResult.Warnings);
            }

            RomSetHiddenLedger.SaveIfDirty(_logger);

            if (options.DebugReport)
            {
                response.DebugReportPath = WriteDebugReport(response);
            }

            if (!request.DryRun && response.GamesChanged > 0 && options.ReloadGamesAfterApply && request.ReloadGames)
            {
                _runtimeState.RequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(2));
                response.ReloadGamesRequested = true;
                if (progress)
                {
                    _taskProgressService.Report("rom-set-manager", "Roms Manager", systems.Count, systems.Count, _interfaceTextService.Text("progress.romset.reloadgames", language));
                }
            }
        }
        finally
        {
            if (progress)
            {
                _taskProgressService.Complete("rom-set-manager");
            }
        }

        response.Message = request.DryRun
            ? "Roms Manager audit completed."
            : !IsMode(options.OutputMode, "gamelist_hidden")
                ? $"Roms Manager completed in {options.OutputMode} mode without gamelist writes."
            : response.GamesChanged > 0
                ? "Roms Manager applied."
                : "Roms Manager found no gamelist changes.";
        return response;
    }

    public async Task<RomSetManagerApplyResponse> RestoreAsync(RomSetManagerApplyRequest request, CancellationToken cancellationToken = default)
    {
        var options = GetOptions();
        var systems = ResolveRequestedSystems(request);
        var response = new RomSetManagerApplyResponse
        {
            DryRun = request.DryRun,
            Restore = true,
            Enabled = options.Enabled,
            Options = options
        };
        response.Systems.AddRange(systems);

        var progress = !request.DryRun;
        var language = _settingsService.GetScrapingSettings().Language;
        if (progress)
        {
            _taskProgressService.Report("rom-set-manager", "Roms Manager", 0, systems.Count, _interfaceTextService.Text("progress.romset.restore", language));
        }

        try
        {
            for (var index = 0; index < systems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progress)
                {
                    _taskProgressService.Report("rom-set-manager", "Roms Manager", index, systems.Count, systems[index]);
                }

                var systemResult = await ApplySystemAsync(
                    systems[index],
                    options,
                    request.DryRun,
                    restore: true,
                    claimExistingHidden: false,
                    cancellationToken);
                response.Results.Add(systemResult);
                response.GamesScanned += systemResult.GamesScanned;
                response.GamesMatched += systemResult.GamesMatched;
                response.GamesToRestore += systemResult.GamesToRestore;
                response.GamesChanged += systemResult.GamesChanged;
                response.Warnings.AddRange(systemResult.Warnings);
            }

            RomSetHiddenLedger.SaveIfDirty(_logger);

            if (!request.DryRun && response.GamesChanged > 0 && request.ReloadGames)
            {
                _runtimeState.RequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(2));
                response.ReloadGamesRequested = true;
            }
        }
        finally
        {
            if (progress)
            {
                _taskProgressService.Complete("rom-set-manager");
            }
        }

        response.Message = request.DryRun
            ? "Roms Manager restore audit completed."
            : response.GamesChanged > 0
                ? "Roms Manager marks restored."
                : "No APIExpose Roms Manager marks found.";
        return response;
    }

    private async Task<RomSetManagerSystemResult> ApplySystemAsync(
        string systemId,
        RomSetManagerOptionsSnapshot options,
        bool dryRun,
        bool restore,
        bool claimExistingHidden,
        CancellationToken cancellationToken)
    {
        var result = new RomSetManagerSystemResult
        {
            SystemId = systemId,
            DryRun = dryRun,
            Restore = restore,
            GamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml")
        };

        if (!File.Exists(result.GamelistPath))
        {
            result.Warnings.Add($"gamelist.xml not found for {systemId}.");
            return result;
        }

        lock (GetGamelistLock(result.GamelistPath))
        {
            var document = XDocument.Load(result.GamelistPath, LoadOptions.PreserveWhitespace);
            var games = document.Root?.Elements("game").ToList() ?? new List<XElement>();
            var gamelistEntries = games
                .Select(BuildGamelistEntry)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.NormalizedRom) &&
                    GamelistGameExists(result.GamelistPath, entry.Path))
                .ToList();
            if (gamelistEntries.Count == 0)
            {
                return result;
            }

            var installedGamePaths = gamelistEntries
                .Select(entry => NormalizeGamePath(entry.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var romSetPlan = restore
                ? new RomSetPlan()
                : LoadRomSetPlan(systemId, options, gamelistEntries, result);
            var romSetPlanUnavailable = !restore &&
                romSetPlan.ReasonsByRom.Count == 0 &&
                romSetPlan.ReasonsByPath.Count == 0 &&
                result.Warnings.Count > 0;
            if (romSetPlanUnavailable && !options.NeverHideFavorites && !options.OnlyRetroAchievements)
            {
                return result;
            }

            if (!restore)
            {
                AddLocalVariantDecisions(systemId, options, gamelistEntries, romSetPlan);
                AddGamelistMarkerReasons(options, gamelistEntries, romSetPlan);
                AddUnknownRomReasons(options, gamelistEntries, romSetPlan, romSetPlanUnavailable);
            }

            result.VariantDecisions.AddRange(romSetPlan.VariantDecisions);
            var changed = false;
            var canWrite = !dryRun && (restore || IsMode(options.OutputMode, "gamelist_hidden"));

            // paths this very run elected as variant winners: an orphaned hidden
            // entry among them was provably hidden by us in a previous run (a
            // user never hides the game the manager elects), so it is reclaimable
            var electedVariantWinners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reasons in romSetPlan.ReasonsByPath.Values.Concat(romSetPlan.ReasonsByRom.Values))
            {
                foreach (var reason in reasons)
                {
                    if (reason.StartsWith("variant-selected:", StringComparison.OrdinalIgnoreCase))
                    {
                        electedVariantWinners.Add(NormalizeGamePath(reason["variant-selected:".Length..]));
                    }
                }
            }

            foreach (var game in games)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = game.Element("path")?.Value ?? string.Empty;
                var normalizedRom = NormalizeRomId(Path.GetFileNameWithoutExtension(path));
                if (string.IsNullOrWhiteSpace(normalizedRom))
                {
                    continue;
                }

                var pathKey = NormalizeGamePath(path);
                if (!installedGamePaths.Contains(pathKey))
                {
                    continue;
                }

                result.GamesScanned++;

                if (canWrite)
                {
                    if (IsApiHidden(game))
                    {
                        // keep the ledger in sync with every owned hide we see
                        RomSetHiddenLedger.Set(
                            systemId,
                            pathKey,
                            string.Equals(game.Element(ApiOriginalHiddenTag)?.Value, "true", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (IsHidden(game))
                    {
                        var inLedger = RomSetHiddenLedger.TryGet(systemId, pathKey, out var ledgerOriginalHidden);
                        var reclaimable = inLedger ||
                            electedVariantWinners.Contains(pathKey) ||
                            (romSetPlan.ReasonsByPath.TryGetValue(pathKey, out var orphanReasons) && orphanReasons.Count > 0);
                        if (reclaimable)
                        {
                            // F2 : hidden entry stripped of its ownership tags by
                            // ES's fragment ingestion but provably ours (ledger,
                            // elected winner, or currently-decided hide) — re-tag
                            // it so the branches below hide or restore it through
                            // the normal flow
                            SetElementValue(game, ApiHiddenTag, "true");
                            SetElementValue(game, ApiReasonsTag, "reclaimed-orphan");
                            if (game.Element(ApiOriginalHiddenTag) == null)
                            {
                                game.Add(new XElement(ApiOriginalHiddenTag, inLedger && ledgerOriginalHidden ? "true" : "false"));
                            }

                            changed = true;
                        }
                    }
                }

                if (restore)
                {
                    if (IsApiHidden(game))
                    {
                        result.GamesMatched++;
                        result.GamesToRestore++;
                        result.Changes.Add(BuildChange(systemId, path, normalizedRom, "restore", GetApiReasons(game)));
                        if (canWrite)
                        {
                            RestoreApiHidden(game);
                            RomSetHiddenLedger.Remove(systemId, pathKey);
                            changed = true;
                            result.GamesChanged++;
                        }
                    }

                    continue;
                }

                if (ShouldProtectFavorite(game, options))
                {
                    if (IsApiHidden(game))
                    {
                        result.GamesMatched++;
                        result.GamesToRestore++;
                        result.Changes.Add(BuildChange(
                            systemId,
                            path,
                            normalizedRom,
                            "restore",
                            MergeReasons(GetApiReasons(game), ["favorite-protected"])));
                        if (canWrite)
                        {
                            RestoreApiHidden(game);
                            RomSetHiddenLedger.Remove(systemId, pathKey);
                            changed = true;
                            result.GamesChanged++;
                        }
                    }

                    continue;
                }

                if (ShouldProtectRetroAchievements(game, options))
                {
                    if (IsApiHidden(game))
                    {
                        result.GamesMatched++;
                        result.GamesToRestore++;
                        result.Changes.Add(BuildChange(
                            systemId,
                            path,
                            normalizedRom,
                            "restore",
                            MergeReasons(GetApiReasons(game), ["retroachievements-protected"])));
                        if (canWrite)
                        {
                            RestoreApiHidden(game);
                            RomSetHiddenLedger.Remove(systemId, pathKey);
                            changed = true;
                            result.GamesChanged++;
                        }
                    }

                    continue;
                }

                var hasPathReason = romSetPlan.ReasonsByPath.TryGetValue(pathKey, out var pathReasons) && pathReasons.Count > 0;
                var hasRomReason = romSetPlan.ReasonsByRom.TryGetValue(normalizedRom, out var romReasons) && romReasons.Count > 0;
                var noRetroAchievementsReasons = ShouldHideWithoutRetroAchievements(game, options)
                    ? new[] { "no-retroachievements" }
                    : null;
                var hasReason = hasPathReason || hasRomReason || noRetroAchievementsReasons != null;
                if (hasReason)
                {
                    var effectiveReasons = MergeReasons(pathReasons, romReasons, noRetroAchievementsReasons);
                    result.GamesMatched++;
                    result.GamesToHide++;
                    result.Changes.Add(BuildChange(systemId, path, normalizedRom, "hide", effectiveReasons));
                    if (canWrite && ApplyApiHidden(game, effectiveReasons, claimExistingHidden))
                    {
                        changed = true;
                        result.GamesChanged++;
                    }

                    if (canWrite && IsApiHidden(game))
                    {
                        RomSetHiddenLedger.Set(
                            systemId,
                            pathKey,
                            string.Equals(game.Element(ApiOriginalHiddenTag)?.Value, "true", StringComparison.OrdinalIgnoreCase));
                    }

                    continue;
                }

                if (IsApiHidden(game) && ShouldRestoreApiHiddenWithoutCurrentReason(game, options, romSetPlanUnavailable))
                {
                    result.GamesMatched++;
                    result.GamesToRestore++;
                    result.Changes.Add(BuildChange(systemId, path, normalizedRom, "restore", GetApiReasons(game)));
                    if (canWrite)
                    {
                        RestoreApiHidden(game);
                        RomSetHiddenLedger.Remove(systemId, pathKey);
                        changed = true;
                        result.GamesChanged++;
                    }
                }
            }

            if (changed && canWrite)
            {
                if (_gamelistUpdateService.SaveExternalGamelistDocument(
                    document,
                    result.GamelistPath,
                    "rom-set-manager:" + systemId,
                    cancellationToken))
                {
                    result.BackupPath = FindLatestBackup(result.GamelistPath);
                }
                else
                {
                    result.Warnings.Add($"central gamelist writer rejected or skipped write for {systemId}.");
                }
            }
        }

        await Task.CompletedTask;
        return result;
    }

    private RomSetPlan LoadRomSetPlan(
        string systemId,
        RomSetManagerOptionsSnapshot options,
        IReadOnlyList<RomSetGamelistEntry> gamelistEntries,
        RomSetManagerSystemResult result)
    {
        var plan = new RomSetPlan();
        var groupsRoot = options.GroupsRootPath;
        result.GroupsFilePath = ResolveRomSetDataFile(groupsRoot, systemId);
        if (string.IsNullOrWhiteSpace(result.GroupsFilePath))
        {
            result.Warnings.Add($"systems_data_games file not found for {systemId} in {groupsRoot}.");
            return plan;
        }

        var gamelistIndex = RomSetGamelistIdentityIndex.Build(gamelistEntries);
        var installedRoms = gamelistEntries
            .Select(entry => entry.NormalizedRom)
            .Where(rom => !string.IsNullOrWhiteSpace(rom))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compactVariantGroups = new Dictionary<string, RomSetResolvedVariantGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(result.GroupsFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? group;
            try
            {
                group = JsonNode.Parse(line)?.AsObject();
            }
            catch (JsonException ex)
            {
                _logger?.LogDebug(ex, "Ignoring invalid romset group line for {SystemId}", systemId);
                continue;
            }

            if (group == null)
            {
                continue;
            }

            if (IsCompactRomSetEntry(group))
            {
                var resolvedEntries = ResolveCompactGamelistEntries(group, gamelistIndex);
                foreach (var resolvedEntry in resolvedEntries)
                {
                    if (!string.IsNullOrWhiteSpace(resolvedEntry.Gamelist.PathKey))
                    {
                        plan.ResolvedCompactPaths.Add(resolvedEntry.Gamelist.PathKey);
                        plan.ResolvedPaths.Add(resolvedEntry.Gamelist.PathKey);
                    }

                    if (!string.IsNullOrWhiteSpace(resolvedEntry.Gamelist.NormalizedRom))
                    {
                        plan.ResolvedRoms.Add(resolvedEntry.Gamelist.NormalizedRom);
                    }
                }

                AddCompactEntryReasons(systemId, group, options, plan, resolvedEntries);
                AddCompactEntryToVariantGroups(systemId, group, options, resolvedEntries, compactVariantGroups);
                continue;
            }

            AddLegacyGroupReasons(systemId, group, options, installedRoms, plan);
        }

        foreach (var group in compactVariantGroups.Values)
        {
            AddResolvedVariantDecision(systemId, group, options, plan);
        }

        return plan;
    }

    private RomSetManagerOptionsSnapshot BuildEffectiveOptions(IReadOnlyDictionary<string, string> esSettings)
    {
        var currentOptions = _options.CurrentValue;
        var romset = currentOptions.RomSetManager;
        var apiSettings = currentOptions.ApiSettings;
        var controls = _options.CurrentValue.ControlManager;
        var romsetVisibilityInitialized = esSettings.ContainsKey("global.apiexpose.romset.defaults_initialized")
            || esSettings.Keys.Any(key => key.StartsWith("global.apiexpose.romset.show_", StringComparison.OrdinalIgnoreCase));
        var profile = NormalizeOption(ResolveString("global.apiexpose.romset.profile", romset.Profile, esSettings));
        var profilePolicy = BuildProfilePolicy(profile);
        var retroAchievementsMode = ResolveProfileMode(
            ResolveString("global.apiexpose.romset.ra_mode", romset.RetroAchievementsMode, esSettings),
            profilePolicy.RetroAchievementsMode);
        var languageMode = ResolveProfileMode(
            ResolveString("global.apiexpose.romset.language_mode", romset.LanguageMode, esSettings),
            profilePolicy.LanguageMode);
        var regionMode = ResolveProfileMode(
            ResolveString("global.apiexpose.romset.region_mode", romset.RegionMode, esSettings),
            profilePolicy.RegionMode);
        var romVersionMode = ResolveProfileMode(
            ResolveString("global.apiexpose.romset.rom_version", romset.RomVersionMode, esSettings),
            profilePolicy.RomVersionMode);
        var showOfficialGames = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.official_games_mode", romset.OfficialGamesMode, esSettings),
            profilePolicy.ShowOfficialGames);
        var showClones = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.clones_mode", romset.ClonesMode, esSettings),
            profilePolicy.ShowClones);
        var showPrototypes = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.prototypes_mode", romset.PrototypesMode, esSettings),
            profilePolicy.ShowPrototypes);
        var showDemos = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.demos_mode", romset.DemosMode, esSettings),
            profilePolicy.ShowDemos);
        var showBetaAlpha = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.beta_alpha_mode", romset.BetaAlphaMode, esSettings),
            profilePolicy.ShowBetaAlpha);
        var arcadeTestsMode = ResolveString(
            "global.apiexpose.romset.arcade_diagnostics_mode",
            romset.ArcadeDiagnosticsMode,
            esSettings);
        var showArcadeDiagnostics = ResolveProfileShow(arcadeTestsMode, profilePolicy.ShowArcadeDiagnostics);
        var showLocationTests = showArcadeDiagnostics ||
            (IsAutoMode(arcadeTestsMode) &&
             ResolveProfileShow(
                 ResolveString("global.apiexpose.romset.location_tests_mode", romset.LocationTestsMode, esSettings),
                 profilePolicy.ShowLocationTests));
        var showUsefulPatches = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.useful_patches_mode", romset.UsefulPatchesMode, esSettings),
            profilePolicy.ShowUsefulPatches);
        var showHacksMods = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.hacks_mods_mode", romset.HacksModsMode, esSettings),
            profilePolicy.ShowHacksMods);
        var showCheatsTrainers = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.cheats_trainers_mode", romset.CheatsTrainersMode, esSettings),
            profilePolicy.ShowCheatsTrainers);
        var showBootlegsPirates = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.bootlegs_pirates_mode", romset.BootlegsPiratesMode, esSettings),
            profilePolicy.ShowBootlegsPirates);
        var showUnlicensed = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.unlicensed_mode", romset.UnlicensedMode, esSettings),
            profilePolicy.ShowUnlicensed);
        var showHomebrewsAftermarket = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.homebrews_aftermarket_mode", romset.HomebrewsAftermarketMode, esSettings),
            profilePolicy.ShowHomebrewsAftermarket);
        var showAdult = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.adult_mode", romset.AdultMode, esSettings),
            profilePolicy.ShowAdult);
        var showCasino = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.casino_mode", romset.CasinoMode, esSettings),
            profilePolicy.ShowCasino);
        var showMahjong = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.mahjong_mode", romset.MahjongMode, esSettings),
            profilePolicy.ShowMahjong);
        var showQuiz = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.quiz_mode", romset.QuizMode, esSettings),
            profilePolicy.ShowQuiz);
        var showNonGames = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.non_games_mode", romset.NonGamesMode, esSettings),
            profilePolicy.ShowNonGames);
        var showUnknownRoms = ResolveProfileShow(
            ResolveString("global.apiexpose.romset.unknown_roms_mode", romset.UnknownRomsMode, esSettings),
            profilePolicy.ShowUnknownRoms);
        var showBootlegsAndHacksLegacy = showUsefulPatches ||
            showHacksMods ||
            showCheatsTrainers ||
            showBootlegsPirates ||
            showUnlicensed ||
            showHomebrewsAftermarket;
        var scrapingSettings = _settingsService.GetScrapingSettings();
        var effectiveProfiles = ApiExposeProfileResolver.Resolve(
            scrapingSettings.Language,
            ResolveFirstString(
                esSettings,
                apiSettings.LanguageProfile,
                ("global.apiexpose.api.language_profile", apiSettings.LanguageProfile),
                ("global.apiexpose.romset.language_profile", romset.LanguageProfile)),
            ResolveFirstString(
                esSettings,
                apiSettings.RegionProfile,
                ("global.apiexpose.api.region_profile", apiSettings.RegionProfile),
                ("global.apiexpose.romset.region_profile", romset.RegionProfile)));

        return new RomSetManagerOptionsSnapshot
        {
            Enabled = ResolveBool("global.apiexpose.rom_set_manager.enabled", romset.Enabled, esSettings),
            NeverHideFavorites = ResolveBool("global.apiexpose.romset.never_hide_favorites", romset.NeverHideFavorites, esSettings),
            Profile = profile,
            RetroAchievementsMode = retroAchievementsMode,
            RetroAchievementsAlwaysShow = IsMode(retroAchievementsMode, "always_show"),
            OnlyRetroAchievements = IsMode(retroAchievementsMode, "show_only"),
            LanguageMode = languageMode,
            RegionMode = regionMode,
            RomVersionMode = romVersionMode,
            ShowOfficialGames = showOfficialGames,
            ShowClones = showClones,
            ShowPrototypes = showPrototypes,
            ShowDemos = showDemos,
            ShowBetaAlpha = showBetaAlpha,
            ShowLocationTests = showLocationTests,
            ShowUsefulPatches = showUsefulPatches,
            ShowHacksMods = showHacksMods,
            ShowCheatsTrainers = showCheatsTrainers,
            ShowBootlegsPirates = showBootlegsPirates,
            ShowUnlicensed = showUnlicensed,
            ShowHomebrewsAftermarket = showHomebrewsAftermarket,
            ShowBootlegsAndHacks = showBootlegsAndHacksLegacy,
            ShowAdult = showAdult,
            ShowCasino = showCasino,
            ShowMahjong = showMahjong,
            ShowQuiz = showQuiz,
            ShowNonGames = showNonGames,
            ShowUnknownRoms = showUnknownRoms,
            ShowArcadeDiagnostics = showArcadeDiagnostics,
            ShowNonArcade = ResolveRomSetShowBool("global.apiexpose.romset.show_non_arcade", romset.ShowNonArcade, esSettings, romsetVisibilityInitialized),
            ShowHorizontal = ResolveRomSetShowBool("global.apiexpose.romset.show_horizontal", romset.ShowHorizontal, esSettings, romsetVisibilityInitialized),
            ShowVertical = ResolveRomSetShowBool("global.apiexpose.romset.show_vertical", romset.ShowVertical, esSettings, romsetVisibilityInitialized),
            ScreenOrientation = ResolveString("global.apiexpose.romset.screen_orientation", romset.ScreenOrientation, esSettings),
            CocktailGames = ResolveString("global.apiexpose.romset.cocktail_games", romset.CocktailGames, esSettings),
            MultiScreenGames = ResolveString("global.apiexpose.romset.multi_screen_games", romset.MultiScreenGames, esSettings),
            FunctionalSecondScreen = ResolveString("global.apiexpose.romset.functional_second_screen", romset.FunctionalSecondScreen, esSettings),
            WideSurroundDisplay = ResolveString("global.apiexpose.romset.wide_surround_display", romset.WideSurroundDisplay, esSettings),
            PortableLinkGameplay = ResolveString("global.apiexpose.romset.portable_link_gameplay", romset.PortableLinkGameplay, esSettings),
            CabinetControlsCompatibility = ResolveString("global.apiexpose.romset.cabinet_controls_compatibility", romset.CabinetControlsCompatibility, esSettings),
            PlayerCount = ResolveString("global.apiexpose.romset.player_count", romset.PlayerCount, esSettings),
            ButtonCompatibility = ResolveString("global.apiexpose.romset.button_compatibility", romset.ButtonCompatibility, esSettings),
            VariantMode = ResolveString("global.apiexpose.romset.variant_mode", romset.VariantMode, esSettings),
            RegionProfile = effectiveProfiles.RegionProfile,
            LanguageProfile = effectiveProfiles.LanguageProfile,
            Translations = ResolveString("global.apiexpose.romset.translations", romset.Translations, esSettings),
            ArcadeHandling = ResolveString("global.apiexpose.romset.arcade_handling", romset.ArcadeHandling, esSettings),
            OutputMode = ResolveString("global.apiexpose.romset.output_mode", romset.OutputMode, esSettings),
            DebugReport = ResolveBool("global.apiexpose.romset.debug_report", romset.DebugReport, esSettings),
            ReloadGamesAfterApply = romset.ReloadGamesAfterApply,
            ControlCabinetProfile = ResolveString("global.apiexpose.control_manager.cabinet_profile", controls.CabinetProfile, esSettings),
            ControlPlayerCount = ResolveInt("global.apiexpose.control_manager.player_count", controls.PlayerCount, esSettings, 1, 8),
            ControlButtonsPerPlayer = ResolveInt("global.apiexpose.control_manager.buttons_per_player", controls.ButtonsPerPlayer, esSettings, 0, 12),
            ControlArcadeJoystick = ResolveBool("global.apiexpose.control_manager.arcade_joystick", controls.ArcadeJoystick, esSettings),
            ControlAnalogJoystick = ResolveBool("global.apiexpose.control_manager.analog_joystick", controls.AnalogJoystick, esSettings),
            ControlRotaryJoystick = ResolveBool("global.apiexpose.control_manager.rotary_joystick", controls.RotaryJoystick, esSettings),
            ControlSpinner = ResolveString("global.apiexpose.control_manager.spinner", controls.Spinner, esSettings),
            ControlTrackball = ResolveString("global.apiexpose.control_manager.trackball", controls.Trackball, esSettings),
            ControlWheel = ResolveString("global.apiexpose.control_manager.wheel", controls.Wheel, esSettings),
            ControlPedals = ResolveString("global.apiexpose.control_manager.pedals", controls.Pedals, esSettings),
            ControlShifter = ResolveString("global.apiexpose.control_manager.shifter", controls.Shifter, esSettings),
            ControlLightgun = ResolveString("global.apiexpose.control_manager.lightgun", controls.Lightgun, esSettings),
            ControlDanceMat = ResolveString("global.apiexpose.control_manager.dance_mat", controls.DanceMat, esSettings),
            ControlGuitar = ResolveString("global.apiexpose.control_manager.guitar", controls.Guitar, esSettings),
            ControlDrums = ResolveString("global.apiexpose.control_manager.drums", controls.Drums, esSettings),
            ControlTurntable = ResolveString("global.apiexpose.control_manager.turntable", controls.Turntable, esSettings),
            ControlMicrophone = ResolveBool("global.apiexpose.control_manager.microphone", controls.Microphone, esSettings),
            ControlKeyboard = ResolveBool("global.apiexpose.control_manager.keyboard", controls.Keyboard, esSettings),
            ControlMouse = ResolveBool("global.apiexpose.control_manager.mouse", controls.Mouse, esSettings),
            ControlTouchscreen = ResolveBool("global.apiexpose.control_manager.touchscreen", controls.Touchscreen, esSettings),
            ControlMotionController = ResolveBool("global.apiexpose.control_manager.motion_controller", controls.MotionController, esSettings),
            GroupsRootPath = ResolveGroupsRootPath(romset.GroupsRootPath)
        };
    }

    private List<string> ResolveRequestedSystems(RomSetManagerApplyRequest request)
    {
        if (!request.AllSystems)
        {
            return string.IsNullOrWhiteSpace(request.SystemId)
                ? new List<string>()
                : new List<string> { request.SystemId.Trim() };
        }

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return new List<string>();
        }

        return Directory.EnumerateDirectories(RetroBatPaths.RomsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Where(name => !IgnoredAllSystems.Contains(name))
            .Where(name => HasManageableGamelistEntries(Path.Combine(RetroBatPaths.RomsRoot, name, "gamelist.xml")))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasManageableGamelistEntries(string gamelistPath)
    {
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(gamelistPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.Name, "game", StringComparison.Ordinal))
                {
                    continue;
                }

                using var gameReader = reader.ReadSubtree();
                while (gameReader.Read())
                {
                    if (gameReader.NodeType != XmlNodeType.Element || !string.Equals(gameReader.Name, "path", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var path = gameReader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(NormalizeRomId(Path.GetFileNameWithoutExtension(path))) &&
                        GamelistGameExists(gamelistPath, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool GamelistGameExists(string gamelistPath, string? gamePath)
    {
        var resolvedPath = ResolveGamelistGamePath(gamelistPath, gamePath);
        return !string.IsNullOrWhiteSpace(resolvedPath) &&
            (File.Exists(resolvedPath) || Directory.Exists(resolvedPath));
    }

    private static string? ResolveGamelistGamePath(string gamelistPath, string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return null;
        }

        var systemDirectory = Path.GetDirectoryName(gamelistPath);
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            return null;
        }

        var normalizedPath = gamePath
            .Trim()
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (normalizedPath.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        if (!Path.IsPathRooted(normalizedPath))
        {
            normalizedPath = normalizedPath.TrimStart(Path.DirectorySeparatorChar);
            normalizedPath = Path.Combine(systemDirectory, normalizedPath);
        }

        try
        {
            return Path.GetFullPath(normalizedPath);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string> ReadEsSettings()
    {
        return _runtimeOptions.GetLocalOptionsSnapshot().RawApiExposeSettings;
    }

    private void AddLegacyGroupReasons(
        string systemId,
        JsonObject group,
        RomSetManagerOptionsSnapshot options,
        IReadOnlySet<string> installedRoms,
        RomSetPlan plan)
    {
        var groupRoms = ReadStringArray(group, "r");
        foreach (var rom in groupRoms)
        {
            var normalized = NormalizeRomId(rom);
            if (!string.IsNullOrWhiteSpace(normalized) && installedRoms.Contains(normalized))
            {
                plan.ResolvedRoms.Add(normalized);
            }
        }

        AddCloneReasons(systemId, group, options, installedRoms, plan);
        AddListReasons(group, "pr", "prototype", options.ShowPrototypes, groupRoms, plan.ReasonsByRom);
        AddListReasons(group, "bt", "bootleg-hack", options.ShowBootlegsAndHacks, groupRoms, plan.ReasonsByRom);
        AddListReasons(group, "adu", "adult", options.ShowAdult, groupRoms, plan.ReasonsByRom);
        AddListReasons(group, "cas", "casino", options.ShowCasino, groupRoms, plan.ReasonsByRom);
        AddListReasons(group, "mah", "mahjong", options.ShowMahjong, groupRoms, plan.ReasonsByRom);
        AddListReasons(group, "ng", "non-game", options.ShowNonGames, groupRoms, plan.ReasonsByRom);
        AddListReasons(group, "np", "non-arcade", options.ShowNonArcade, groupRoms, plan.ReasonsByRom);

        if (!options.ShowHorizontal && ReadInt(group, "y") == 1)
        {
            AddReason(groupRoms, "horizontal", plan.ReasonsByRom);
        }

        if (!options.ShowVertical && ReadInt(group, "t") == 1)
        {
            AddReason(groupRoms, "vertical", plan.ReasonsByRom);
        }

        AddVariantDecision(systemId, group, options, installedRoms, plan);
    }

    private void AddCompactEntryReasons(
        string systemId,
        JsonObject entry,
        RomSetManagerOptionsSnapshot options,
        RomSetPlan plan,
        IReadOnlyList<RomSetResolvedCompactEntry> resolvedEntries)
    {
        var identifiers = ReadCompactIdentifiers(entry);
        var type = NormalizeOption(ReadString(entry, "t"));
        var role = NormalizeOption(ReadString(entry, "role"));
        var releaseKind = NormalizeOption(ReadString(entry, "rk"));
        var mediaType = NormalizeOption(ReadString(entry, "mt"));
        var orientation = NormalizeOption(ReadString(entry, "ori"));
        var flags = ReadStringArray(entry, "flg").Select(NormalizeOption).ToList();
        var aux = ReadStringArray(entry, "aux").Select(NormalizeOption).ToList();
        var controls = ReadStringArray(entry, "ctl").Select(NormalizeOption).ToList();
        var contentWarnings = ReadStringArray(entry, "cw").Select(NormalizeOption).ToList();
        var categories = ReadStringArray(entry, "cat").Select(NormalizeOption).ToList();
        var screenCount = ReadOptionalInt(entry, "scr") ?? 1;
        var buttonCount = ReadOptionalInt(entry, "btn");
        var playerCount = ReadNestedOptionalInt(entry, "pl", "max");
        var hasExplicitAlphaBetaBuild = HasExplicitAlphaBetaBuildMarker(ReadString(entry, "fn")) ||
            HasExplicitAlphaBetaBuildMarker(ReadString(entry, "set"));
        var isPrototype = type == "prototype" || releaseKind == "prototype" || flags.Contains("prototype");
        var isDemo = type == "demo" || releaseKind == "demo" || flags.Contains("demo");
        var isBetaAlpha = hasExplicitAlphaBetaBuild && (IsPrereleaseAlphaBeta(type) ||
            IsPrereleaseAlphaBeta(releaseKind) ||
            flags.Any(IsPrereleaseAlphaBeta));
        var isLocationTest = flags.Any(flag => flag is "location_test" or "location-test") ||
            releaseKind is "location_test" or "location-test";
        var isUsefulPatch = releaseKind is "bugfix" or "stability_patch" or "quality_of_life" or "restoration" or "uncensored" or "widescreen" or "accessibility" or "compatibility_patch" ||
            flags.Any(flag => flag is "bugfix" or "stability_patch" or "quality_of_life" or "qol" or "restoration" or "uncensored" or "widescreen" or "accessibility" or "compatibility_patch");
        var isCheatTrainer = releaseKind is "trainer" or "cheat" ||
            flags.Any(flag => flag is "trainer" or "cheat" or "debug_menu" or "level_select");
        var isHackMod = type == "hack" ||
            releaseKind is "hack" or "romhack" or "custom_hack" or "randomizer" or "total_conversion" ||
            flags.Any(flag => flag is "hack" or "romhack" or "custom_hack" or "randomizer" or "total_conversion" or "difficulty_hack" or "kaizo");
        var isBootlegPirate = releaseKind is "bootleg" or "pirate" || flags.Any(flag => flag is "bootleg" or "pirate");
        var isUnlicensed = releaseKind is "unlicensed" || flags.Contains("unlicensed");
        var isHomebrew = releaseKind is "homebrew" or "aftermarket" || flags.Any(flag => flag is "homebrew" or "aftermarket");
        var isQuiz = contentWarnings.Contains("quiz") || categories.Any(value => value.Contains("quiz", StringComparison.OrdinalIgnoreCase));
        var isArcadeDiagnostic = type is "test" or "diagnostic" or "tool" ||
            releaseKind is "test" or "diagnostic" ||
            flags.Any(flag => flag is "test" or "diagnostic" or "input_test" or "monitor_test" or "sound_test" or "service_cartridge" or "calibration");
        var isNonGame = role == "bios" || type is "bios" or "device" or "sample" or "media" or "tool" or "non_game";
        var isOfficialGame = IsOfficialGameEntry(
            type,
            role,
            releaseKind,
            flags,
            contentWarnings,
            categories,
            isPrototype,
            isDemo,
            isBetaAlpha,
            isLocationTest,
            isUsefulPatch,
            isCheatTrainer,
            isHackMod,
            isBootlegPirate,
            isUnlicensed,
            isHomebrew,
            isQuiz,
            isArcadeDiagnostic,
            isNonGame);

        if (!options.ShowOfficialGames && isOfficialGame)
        {
            AddCompactReason(identifiers, resolvedEntries, "official-game", plan);
        }

        if (!options.ShowClones && role == "clone")
        {
            AddCompactReason(identifiers, resolvedEntries, "clone", plan);
        }

        if (!options.ShowPrototypes && isPrototype)
        {
            AddCompactReason(identifiers, resolvedEntries, "prototype", plan);
        }

        if (!options.ShowDemos && isDemo)
        {
            AddCompactReason(identifiers, resolvedEntries, "demo", plan);
        }

        if (!options.ShowBetaAlpha && isBetaAlpha)
        {
            AddCompactReason(identifiers, resolvedEntries, "beta-alpha", plan);
        }

        if (!options.ShowLocationTests && isLocationTest)
        {
            AddCompactReason(identifiers, resolvedEntries, "location-test", plan);
        }

        if (!options.ShowUsefulPatches && isUsefulPatch)
        {
            AddCompactReason(identifiers, resolvedEntries, "useful-patch", plan);
        }

        if (!options.ShowHacksMods && isHackMod && !isUsefulPatch)
        {
            AddCompactReason(identifiers, resolvedEntries, "hack-mod", plan);
        }

        if (!options.ShowCheatsTrainers && isCheatTrainer)
        {
            AddCompactReason(identifiers, resolvedEntries, "cheat-trainer", plan);
        }

        if (!options.ShowBootlegsPirates && isBootlegPirate)
        {
            AddCompactReason(identifiers, resolvedEntries, "bootleg-pirate", plan);
        }

        if (!options.ShowUnlicensed && isUnlicensed)
        {
            AddCompactReason(identifiers, resolvedEntries, "unlicensed", plan);
        }

        if (!options.ShowHomebrewsAftermarket && isHomebrew)
        {
            AddCompactReason(identifiers, resolvedEntries, "homebrew-aftermarket", plan);
        }

        if (!options.ShowAdult && contentWarnings.Contains("adult"))
        {
            AddCompactReason(identifiers, resolvedEntries, "adult", plan);
        }

        if (!options.ShowCasino &&
            (contentWarnings.Any(value => value is "casino" or "gambling") ||
             categories.Any(value => value.Contains("casino", StringComparison.OrdinalIgnoreCase))))
        {
            AddCompactReason(identifiers, resolvedEntries, "casino", plan);
        }

        if (!options.ShowMahjong &&
            (contentWarnings.Contains("mahjong") ||
             categories.Any(value => value.Contains("mahjong", StringComparison.OrdinalIgnoreCase))))
        {
            AddCompactReason(identifiers, resolvedEntries, "mahjong", plan);
        }

        if (!options.ShowQuiz && isQuiz)
        {
            AddCompactReason(identifiers, resolvedEntries, "quiz", plan);
        }

        if (!options.ShowNonGames && isNonGame)
        {
            AddCompactReason(identifiers, resolvedEntries, "non-game", plan);
        }

        if (!options.ShowArcadeDiagnostics && isArcadeDiagnostic)
        {
            AddCompactReason(identifiers, resolvedEntries, "arcade-diagnostic", plan);
        }

        if (!options.ShowNonArcade &&
            IsArcadeSystem(systemId) &&
            !string.IsNullOrWhiteSpace(mediaType) &&
            mediaType != "arcade_board")
        {
            AddCompactReason(identifiers, resolvedEntries, "non-arcade", plan);
        }

        if (!options.ShowHorizontal && orientation != "vertical")
        {
            AddCompactReason(identifiers, resolvedEntries, "horizontal", plan);
        }

        if (!options.ShowVertical && orientation == "vertical")
        {
            AddCompactReason(identifiers, resolvedEntries, "vertical", plan);
        }

        AddScreenAndCabinetReasons(
            systemId,
            identifiers,
            orientation,
            screenCount,
            flags,
            aux,
            controls,
            buttonCount,
            playerCount,
            options,
            plan,
            resolvedEntries);
    }

    private void AddScreenAndCabinetReasons(
        string systemId,
        IReadOnlyList<string> identifiers,
        string orientation,
        int screenCount,
        IReadOnlyList<string> flags,
        IReadOnlyList<string> aux,
        IReadOnlyList<string> controls,
        int? buttonCount,
        int? playerCount,
        RomSetManagerOptionsSnapshot options,
        RomSetPlan plan,
        IReadOnlyList<RomSetResolvedCompactEntry> resolvedEntries)
    {
        if (!IsArcadeSystem(systemId))
        {
            return;
        }

        var isCocktail = orientation == "cocktail" || flags.Contains("cocktail");
        var isMultiScreen = screenCount > 1 || flags.Contains("multi_screen");
        var functionalSecondScreen =
            flags.Contains("multi_screen_gameplay") ||
            aux.Any(value => value is "second_screen" or "gamepad_screen" or "private_screen" or "touchscreen");
        var wideSurroundDisplay =
            flags.Contains("screen_extended_playfield") ||
            flags.Contains("screen_surround_view") ||
            flags.Contains("screen_side_by_side");
        var portableLink =
            flags.Contains("portable_live_link") ||
            aux.Any(value => value is "gba_link" or "ds_link" or "vmu_screen" or "portable_controller");
        var portableRequired =
            flags.Any(value => value is "portable_core_required" or "portable_core_required_multiplayer" or "portable_core_multiplayer");

        switch (_taxonomy.NormalizeOrientationFilter(options.ScreenOrientation))
        {
            case "only_horizontal" when orientation is "vertical" or "cocktail":
                AddCompactReason(identifiers, resolvedEntries, "orientation-horizontal-only", plan);
                break;
            case "only_vertical" when orientation != "vertical":
                AddCompactReason(identifiers, resolvedEntries, "orientation-vertical-only", plan);
                break;
            case "only_cocktail" when !isCocktail:
                AddCompactReason(identifiers, resolvedEntries, "orientation-cocktail-only", plan);
                break;
            case "hide_cocktail" when isCocktail:
                AddCompactReason(identifiers, resolvedEntries, "orientation-cocktail-hidden", plan);
                break;
        }

        AddModeReason(identifiers, resolvedEntries, options.CocktailGames, isCocktail, "cocktail-only", "cocktail-hidden", plan);
        AddModeReason(identifiers, resolvedEntries, options.MultiScreenGames, isMultiScreen, "multi-screen-only", "multi-screen-hidden", plan);

        if (IsMode(options.FunctionalSecondScreen, "only") && !functionalSecondScreen)
        {
            AddCompactReason(identifiers, resolvedEntries, "functional-second-screen-only", plan);
        }

        if (IsMode(options.WideSurroundDisplay, "only") && !wideSurroundDisplay)
        {
            AddCompactReason(identifiers, resolvedEntries, "wide-surround-only", plan);
        }

        if (IsMode(options.PortableLinkGameplay, "only") && !portableLink)
        {
            AddCompactReason(identifiers, resolvedEntries, "portable-link-only", plan);
        }
        else if (IsMode(options.PortableLinkGameplay, "hide_required") && portableRequired)
        {
            AddCompactReason(identifiers, resolvedEntries, "portable-link-required", plan);
        }

        if (ShouldCheckPlayerCount(options) &&
            playerCount.HasValue &&
            options.ControlPlayerCount > 0 &&
            playerCount.Value > options.ControlPlayerCount)
        {
            AddCompactReason(identifiers, resolvedEntries, "player-count", plan);
        }

        if (ShouldCheckButtonCompatibility(options) &&
            buttonCount.HasValue &&
            options.ControlButtonsPerPlayer >= 0 &&
            buttonCount.Value > options.ControlButtonsPerPlayer)
        {
            AddCompactReason(identifiers, resolvedEntries, "button-compatibility", plan);
        }

        if (IsMode(options.CabinetControlsCompatibility, "only") &&
            HasUnsupportedRequiredControl(controls, aux, options))
        {
            AddCompactReason(identifiers, resolvedEntries, "cabinet-controls", plan);
        }
    }

    private static void AddModeReason(
        IReadOnlyList<string> identifiers,
        IReadOnlyList<RomSetResolvedCompactEntry> resolvedEntries,
        string mode,
        bool matched,
        string onlyReason,
        string hideReason,
        RomSetPlan plan)
    {
        if (IsMode(mode, "only") && !matched)
        {
            AddCompactReason(identifiers, resolvedEntries, onlyReason, plan);
        }
        else if ((IsMode(mode, "hide") || IsMode(mode, "hide_required")) && matched)
        {
            AddCompactReason(identifiers, resolvedEntries, hideReason, plan);
        }
    }

    private static void AddCompactEntryToVariantGroups(
        string systemId,
        JsonObject entry,
        RomSetManagerOptionsSnapshot options,
        IReadOnlyList<RomSetResolvedCompactEntry> resolvedEntries,
        Dictionary<string, RomSetResolvedVariantGroup> compactVariantGroups)
    {
        if (IsVariantDecisionDisabled(options, systemId))
        {
            return;
        }

        var groupId = ReadString(entry, "grp");
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        if (resolvedEntries.Count == 0)
        {
            return;
        }

        if (!compactVariantGroups.TryGetValue(groupId, out var group))
        {
            group = new RomSetResolvedVariantGroup
            {
                GroupId = groupId,
                DisplayName = ReadString(entry, "n")
            };
            compactVariantGroups[groupId] = group;
        }

        foreach (var resolved in resolvedEntries)
        {
            if (group.Entries.Any(entry => string.Equals(entry.Gamelist.Path, resolved.Gamelist.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            group.Entries.Add(resolved);
            if ((resolved.Role.Equals("parent", StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(group.PreferredPath)) &&
                !string.IsNullOrWhiteSpace(resolved.Gamelist.Path))
            {
                group.PreferredPath = resolved.Gamelist.Path;
            }
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                array.Add(value);
            }
        }

        return array;
    }

    private static IReadOnlyList<RomSetResolvedCompactEntry> ResolveCompactGamelistEntries(
        JsonObject entry,
        RomSetGamelistIdentityIndex gamelistIndex)
    {
        var matches = new List<RomSetResolvedCompactEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ReadCompactMatchKeys(entry))
        {
            if (!gamelistIndex.ByKey.TryGetValue(key, out var entries))
            {
                continue;
            }

            foreach (var gamelistEntry in entries)
            {
                if (!seenPaths.Add(gamelistEntry.Path))
                {
                    continue;
                }

                matches.Add(new RomSetResolvedCompactEntry
                {
                    Entry = entry,
                    Gamelist = gamelistEntry,
                    GroupId = ReadString(entry, "grp"),
                    Role = NormalizeOption(ReadString(entry, "role")),
                    FullName = ReadString(entry, "fn"),
                    DisplayName = ReadString(entry, "n"),
                    Regions = ReadStringArray(entry, "reg"),
                    Languages = ReadStringArray(entry, "lang")
                });
            }
        }

        return matches;
    }

    private static List<string> ReadCompactMatchKeys(JsonObject entry)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMatchKey(keys, seen, "rom", ReadString(entry, "id"));
        AddMatchKey(keys, seen, "rom", ReadString(entry, "set"));
        AddMatchKey(keys, seen, "rom", ReadString(entry, "fn"));

        if (entry.TryGetPropertyValue("hsh", out var hashNode))
        {
            AddHashMatchKeys(keys, seen, hashNode);
        }

        if (entry.TryGetPropertyValue("aka", out var aliasesNode) && aliasesNode is JsonArray aliases)
        {
            foreach (var aliasNode in aliases)
            {
                if (aliasNode is not JsonObject alias)
                {
                    continue;
                }

                AddMatchKey(keys, seen, "rom", ReadString(alias, "id"));
                AddMatchKey(keys, seen, "rom", ReadString(alias, "set"));
                AddMatchKey(keys, seen, "rom", ReadString(alias, "fn"));

                if (alias.TryGetPropertyValue("hsh", out var aliasHashNode))
                {
                    AddHashMatchKeys(keys, seen, aliasHashNode);
                }
            }
        }

        if (entry.TryGetPropertyValue("ra", out var raNode) && raNode is JsonObject retroAchievements)
        {
            var raHash = ReadString(retroAchievements, "h");
            AddHashMatchKey(keys, seen, "cheevos-hash", raHash);
            AddHashMatchKey(keys, seen, "md5", raHash);
            var raId = ReadPositiveInt(retroAchievements, "id");
            if (raId > 0)
            {
                AddRawMatchKey(keys, seen, "cheevos-id:" + raId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return keys;
    }

    private static void AddHashMatchKeys(List<string> keys, ISet<string> seen, JsonNode? hashNode)
    {
        switch (hashNode)
        {
            case JsonObject hashes:
                AddHashMatchKey(keys, seen, "md5", ReadString(hashes, "md5"));
                AddHashMatchKey(keys, seen, "crc32", ReadString(hashes, "crc"));
                AddHashMatchKey(keys, seen, "sha1", ReadString(hashes, "sha1"));
                break;
            case JsonArray hashSets:
                foreach (var item in hashSets)
                {
                    if (item is JsonObject hashSet)
                    {
                        AddHashMatchKeys(keys, seen, hashSet);
                    }
                }

                break;
        }
    }

    private static void AddCompactReason(
        IReadOnlyList<string> identifiers,
        IReadOnlyList<RomSetResolvedCompactEntry> resolvedEntries,
        string reason,
        RomSetPlan plan)
    {
        if (resolvedEntries.Count == 0)
        {
            AddReason(identifiers, reason, plan.ReasonsByRom);
            return;
        }

        foreach (var resolved in resolvedEntries)
        {
            AddPathReason(resolved.Gamelist.Path, reason, plan.ReasonsByPath);
        }
    }

    private static bool IsCompactRomSetEntry(JsonObject entry)
    {
        return entry.ContainsKey("id") && entry.ContainsKey("grp");
    }

    private static List<string> ReadCompactIdentifiers(JsonObject entry)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCompactIdentifier(ReadString(entry, "id"), values, seen);
        AddCompactIdentifier(ReadString(entry, "set"), values, seen);
        AddCompactIdentifier(ReadString(entry, "fn"), values, seen);
        if (entry.TryGetPropertyValue("aka", out var aliasesNode) && aliasesNode is JsonArray aliases)
        {
            foreach (var aliasNode in aliases)
            {
                if (aliasNode is not JsonObject alias)
                {
                    continue;
                }

                AddCompactIdentifier(ReadString(alias, "id"), values, seen);
                AddCompactIdentifier(ReadString(alias, "set"), values, seen);
                AddCompactIdentifier(ReadString(alias, "fn"), values, seen);
            }
        }

        return values;
    }

    private static void AddCompactIdentifier(string value, List<string> values, ISet<string> seen)
    {
        if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
        {
            values.Add(value);
        }
    }

    private static void AddListReasons(
        JsonObject group,
        string field,
        string reason,
        bool show,
        IReadOnlyList<string> fallbackRoms,
        Dictionary<string, List<string>> reasonsByRom)
    {
        if (show || !group.ContainsKey(field))
        {
            return;
        }

        var roms = ReadStringArray(group, field);
        AddReason(roms.Count > 0 ? roms : fallbackRoms, reason, reasonsByRom);
    }

    private void AddCloneReasons(
        string systemId,
        JsonObject group,
        RomSetManagerOptionsSnapshot options,
        IReadOnlySet<string> installedRoms,
        RomSetPlan plan)
    {
        if (options.ShowClones || !group.ContainsKey("cl"))
        {
            return;
        }

        var groupRoms = ReadStringArray(group, "r");
        var cloneRoms = ReadStringArray(group, "cl");
        if (groupRoms.Count == 0 || cloneRoms.Count == 0)
        {
            return;
        }

        var cloneSet = cloneRoms
            .Select(NormalizeRomId)
            .Where(rom => !string.IsNullOrWhiteSpace(rom))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedGroupCandidates = groupRoms
            .Select(rom => BuildVariantCandidate(rom, group, options))
            .Where(candidate => installedRoms.Contains(candidate.NormalizedRom))
            .ToList();
        if (installedGroupCandidates.Count == 0)
        {
            return;
        }

        var installedPrimaryCandidates = installedGroupCandidates
            .Where(candidate => !cloneSet.Contains(candidate.NormalizedRom))
            .ToList();
        if (installedPrimaryCandidates.Count > 0)
        {
            AddReason(
                installedGroupCandidates
                    .Where(candidate => cloneSet.Contains(candidate.NormalizedRom))
                    .Select(candidate => candidate.Rom)
                    .ToList(),
                "clone",
                plan.ReasonsByRom);
            return;
        }

        var installedCloneCandidates = installedGroupCandidates
            .Where(candidate => cloneSet.Contains(candidate.NormalizedRom))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Rom.Length)
            .ThenBy(candidate => candidate.Rom, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (installedCloneCandidates.Count < 2)
        {
            return;
        }

        var selected = installedCloneCandidates[0];
        var decision = new RomSetVariantDecision
        {
            SystemId = systemId,
            GroupId = ReadString(group, "p"),
            DisplayName = ReadString(group, "n"),
            Mode = options.VariantMode,
            RegionProfile = options.RegionProfile,
            LanguageProfile = options.LanguageProfile,
            SelectedRom = selected.Rom,
            SelectedNormalizedRom = selected.NormalizedRom,
            SelectedScore = selected.Score,
            SelectedReasons = selected.Reasons.ToList(),
            CandidateCount = installedCloneCandidates.Count
        };

        foreach (var candidate in installedCloneCandidates)
        {
            var isSelected = string.Equals(candidate.NormalizedRom, selected.NormalizedRom, StringComparison.OrdinalIgnoreCase);
            decision.Candidates.Add(new RomSetVariantCandidateReport
            {
                Rom = candidate.Rom,
                NormalizedRom = candidate.NormalizedRom,
                Score = candidate.Score,
                Regions = candidate.Regions.ToList(),
                Languages = candidate.Languages.ToList(),
                Reasons = candidate.Reasons.ToList(),
                Selected = isSelected
            });

            if (!isSelected)
            {
                AddReason(new[] { candidate.Rom }, $"clone-selected:{selected.NormalizedRom}", plan.ReasonsByRom);
            }
        }

        plan.VariantDecisions.Add(decision);
    }

    private static void AddReason(
        IReadOnlyList<string> roms,
        string reason,
        Dictionary<string, List<string>> reasonsByRom)
    {
        foreach (var rom in roms)
        {
            var normalized = NormalizeRomId(rom);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!reasonsByRom.TryGetValue(normalized, out var reasons))
            {
                reasons = new List<string>();
                reasonsByRom[normalized] = reasons;
            }

            if (!reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
            {
                reasons.Add(reason);
            }
        }
    }

    private void AddLanguageAndRegionOnlyReasons(
        IReadOnlyList<RomSetVariantCandidate> candidates,
        RomSetManagerOptionsSnapshot options,
        RomSetPlan plan)
    {
        var eligibleCandidates = candidates
            .Where(candidate => HasNoRomReasons(candidate.NormalizedRom, plan))
            .ToList();

        if (IsMode(options.LanguageMode, "show_only_my_language") &&
            eligibleCandidates.Any(candidate => IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)))
        {
            AddReason(
                eligibleCandidates
                    .Where(candidate => !IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile))
                    .Select(candidate => candidate.Rom)
                    .ToList(),
                "language-only",
                plan.ReasonsByRom);
        }

        eligibleCandidates = candidates
            .Where(candidate => HasNoRomReasons(candidate.NormalizedRom, plan))
            .ToList();

        if (IsMode(options.RegionMode, "show_only_my_region") &&
            eligibleCandidates.Any(candidate => ScoreRegion(candidate.Regions, options.RegionProfile) > 0))
        {
            AddReason(
                eligibleCandidates
                    .Where(candidate => ScoreRegion(candidate.Regions, options.RegionProfile) <= 0)
                    .Select(candidate => candidate.Rom)
                    .ToList(),
                "region-only",
                plan.ReasonsByRom);
        }
    }

    private void AddLocalLanguageAndRegionOnlyReasons(
        IReadOnlyList<RomSetLocalVariantCandidate> candidates,
        RomSetManagerOptionsSnapshot options,
        RomSetPlan plan)
    {
        var eligibleCandidates = candidates
            .Where(candidate => HasNoPathReasons(candidate.Path, plan))
            .ToList();

        if (IsMode(options.LanguageMode, "show_only_my_language") &&
            eligibleCandidates.Any(candidate => IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)))
        {
            foreach (var candidate in eligibleCandidates.Where(candidate => !IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)))
            {
                AddPathReason(candidate.Path, "language-only", plan.ReasonsByPath);
            }
        }

        eligibleCandidates = candidates
            .Where(candidate => HasNoPathReasons(candidate.Path, plan))
            .ToList();

        if (IsMode(options.RegionMode, "show_only_my_region") &&
            eligibleCandidates.Any(candidate => ScoreRegion(candidate.Regions, options.RegionProfile) > 0))
        {
            foreach (var candidate in eligibleCandidates.Where(candidate => ScoreRegion(candidate.Regions, options.RegionProfile) <= 0))
            {
                AddPathReason(candidate.Path, "region-only", plan.ReasonsByPath);
            }
        }
    }

    private void AddResolvedLanguageAndRegionOnlyReasons(
        IReadOnlyList<RomSetResolvedVariantCandidate> candidates,
        RomSetManagerOptionsSnapshot options,
        RomSetPlan plan)
    {
        var eligibleCandidates = candidates
            .Where(candidate => HasNoPathReasons(candidate.Path, plan))
            .ToList();

        if (IsMode(options.LanguageMode, "show_only_my_language") &&
            eligibleCandidates.Any(candidate => IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)))
        {
            foreach (var candidate in eligibleCandidates.Where(candidate => !IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)))
            {
                AddPathReason(candidate.Path, "language-only", plan.ReasonsByPath);
            }
        }

        eligibleCandidates = candidates
            .Where(candidate => HasNoPathReasons(candidate.Path, plan))
            .ToList();

        if (IsMode(options.RegionMode, "show_only_my_region") &&
            eligibleCandidates.Any(candidate => ScoreRegion(candidate.Regions, options.RegionProfile) > 0))
        {
            foreach (var candidate in eligibleCandidates.Where(candidate => ScoreRegion(candidate.Regions, options.RegionProfile) <= 0))
            {
                AddPathReason(candidate.Path, "region-only", plan.ReasonsByPath);
            }
        }
    }

    private static bool HasNoRomReasons(string normalizedRom, RomSetPlan plan)
    {
        return string.IsNullOrWhiteSpace(normalizedRom) ||
            !plan.ReasonsByRom.TryGetValue(normalizedRom, out var reasons) ||
            reasons.Count == 0;
    }

    private static bool HasNoPathReasons(string path, RomSetPlan plan)
    {
        return string.IsNullOrWhiteSpace(path) ||
            !plan.ReasonsByPath.TryGetValue(path, out var reasons) ||
            reasons.Count == 0;
    }

    private static void AddPathReason(
        string path,
        string reason,
        Dictionary<string, List<string>> reasonsByPath)
    {
        var normalized = NormalizeGamePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!reasonsByPath.TryGetValue(normalized, out var reasons))
        {
            reasons = new List<string>();
            reasonsByPath[normalized] = reasons;
        }

        if (!reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add(reason);
        }
    }

    private static List<string> MergeReasons(params IReadOnlyList<string>?[] reasonLists)
    {
        var result = new List<string>();
        foreach (var reasonList in reasonLists)
        {
            if (reasonList == null)
            {
                continue;
            }

            foreach (var reason in reasonList)
            {
                if (!result.Contains(reason, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(reason);
                }
            }
        }

        return result;
    }

    private static bool ShouldHideWithoutRetroAchievements(XElement game, RomSetManagerOptionsSnapshot options)
    {
        if (!options.OnlyRetroAchievements)
        {
            return false;
        }

        var rawCheevosId = game.Element("cheevosId")?.Value;
        return !int.TryParse(rawCheevosId?.Trim(), out var cheevosId) || cheevosId <= 0;
    }

    private static bool ShouldProtectFavorite(XElement game, RomSetManagerOptionsSnapshot options)
    {
        return options.NeverHideFavorites && IsFavorite(game);
    }

    private static bool ShouldProtectRetroAchievements(XElement game, RomSetManagerOptionsSnapshot options)
    {
        if (!options.RetroAchievementsAlwaysShow || options.OnlyRetroAchievements)
        {
            return false;
        }

        var rawCheevosId = game.Element("cheevosId")?.Value;
        return int.TryParse(rawCheevosId?.Trim(), out var cheevosId) && cheevosId > 0;
    }

    private static bool ShouldRestoreApiHiddenWithoutCurrentReason(
        XElement game,
        RomSetManagerOptionsSnapshot options,
        bool romSetPlanUnavailable)
    {
        if (!romSetPlanUnavailable)
        {
            return true;
        }

        if (!options.OnlyRetroAchievements || ShouldHideWithoutRetroAchievements(game, options))
        {
            return false;
        }

        var reasons = GetApiReasons(game);
        return reasons.Count > 0 &&
            reasons.All(reason => string.Equals(reason, "no-retroachievements", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFavorite(XElement game)
    {
        return TryParseBool(game.Element("favorite")?.Value, out var favorite) && favorite;
    }

    private void AddVariantDecision(
        string systemId,
        JsonObject group,
        RomSetManagerOptionsSnapshot options,
        IReadOnlySet<string> installedRoms,
        RomSetPlan plan)
    {
        if (IsVariantDecisionDisabled(options, systemId))
        {
            return;
        }

        var groupRoms = ReadStringArray(group, "r");
        if (groupRoms.Count < 2)
        {
            return;
        }

        var candidates = groupRoms
            .Select(rom => BuildVariantCandidate(rom, group, options))
            .Where(candidate => installedRoms.Contains(candidate.NormalizedRom))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Rom.Length)
            .ThenBy(candidate => candidate.Rom, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count < 2)
        {
            return;
        }

        var selected = candidates[0];
        var decision = new RomSetVariantDecision
        {
            SystemId = systemId,
            GroupId = ReadString(group, "p"),
            DisplayName = ReadString(group, "n"),
            Mode = options.VariantMode,
            RegionProfile = options.RegionProfile,
            LanguageProfile = options.LanguageProfile,
            SelectedRom = selected.Rom,
            SelectedNormalizedRom = selected.NormalizedRom,
            SelectedScore = selected.Score,
            SelectedReasons = selected.Reasons.ToList(),
            CandidateCount = candidates.Count
        };

        foreach (var candidate in candidates)
        {
            decision.Candidates.Add(new RomSetVariantCandidateReport
            {
                Rom = candidate.Rom,
                NormalizedRom = candidate.NormalizedRom,
                Score = candidate.Score,
                Regions = candidate.Regions.ToList(),
                Languages = candidate.Languages.ToList(),
                Reasons = candidate.Reasons.ToList(),
                Selected = string.Equals(candidate.NormalizedRom, selected.NormalizedRom, StringComparison.OrdinalIgnoreCase)
            });
        }

        plan.VariantDecisions.Add(decision);
        AddLanguageAndRegionOnlyReasons(candidates, options, plan);
        if (IsMode(options.VariantMode, "hide_variants"))
        {
            foreach (var candidate in candidates.Skip(1))
            {
                AddReason(new[] { candidate.Rom }, $"variant-selected:{selected.NormalizedRom}", plan.ReasonsByRom);
            }
        }
    }

    private void AddResolvedVariantDecision(
        string systemId,
        RomSetResolvedVariantGroup group,
        RomSetManagerOptionsSnapshot options,
        RomSetPlan plan)
    {
        if (IsVariantDecisionDisabled(options, systemId) || group.Entries.Count < 2)
        {
            return;
        }

        var candidates = group.Entries
            .Select(entry => BuildResolvedVariantCandidate(entry, group, options))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DisplayName.Length)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count < 2)
        {
            return;
        }

        var selected = candidates[0];
        var decision = new RomSetVariantDecision
        {
            SystemId = systemId,
            GroupId = group.GroupId,
            DisplayName = string.IsNullOrWhiteSpace(group.DisplayName) ? selected.DisplayName : group.DisplayName,
            Mode = options.VariantMode,
            RegionProfile = options.RegionProfile,
            LanguageProfile = options.LanguageProfile,
            SelectedRom = selected.DisplayName,
            SelectedNormalizedRom = selected.NormalizedRom,
            SelectedScore = selected.Score,
            SelectedReasons = selected.Reasons.ToList(),
            CandidateCount = candidates.Count
        };

        foreach (var candidate in candidates)
        {
            var isSelected = string.Equals(candidate.Path, selected.Path, StringComparison.OrdinalIgnoreCase);
            decision.Candidates.Add(new RomSetVariantCandidateReport
            {
                Rom = candidate.DisplayName,
                NormalizedRom = candidate.NormalizedRom,
                Score = candidate.Score,
                Regions = candidate.Regions.ToList(),
                Languages = candidate.Languages.ToList(),
                Reasons = candidate.Reasons.ToList(),
                Selected = isSelected
            });

            if (!isSelected && IsMode(options.VariantMode, "hide_variants"))
            {
                AddPathReason(candidate.Path, $"variant-selected:{selected.Path}", plan.ReasonsByPath);
            }
        }

        AddResolvedLanguageAndRegionOnlyReasons(candidates, options, plan);
        plan.VariantDecisions.Add(decision);
    }

    private RomSetResolvedVariantCandidate BuildResolvedVariantCandidate(
        RomSetResolvedCompactEntry entry,
        RomSetResolvedVariantGroup group,
        RomSetManagerOptionsSnapshot options)
    {
        var displayName = !string.IsNullOrWhiteSpace(entry.FullName)
            ? entry.FullName
            : !string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.DisplayName
                : entry.Gamelist.DisplayName;
        var candidate = new RomSetResolvedVariantCandidate
        {
            Path = entry.Gamelist.Path,
            DisplayName = entry.Gamelist.DisplayName,
            NormalizedRom = entry.Gamelist.NormalizedRom
        };

        candidate.Regions.AddRange(ExtractRegions(displayName));
        candidate.Languages.AddRange(ExtractLanguages(displayName));
        foreach (var regionValue in entry.Regions)
        {
            var region = _taxonomy.NormalizeRomRegionToken(regionValue);
            if (!string.IsNullOrWhiteSpace(region) && !candidate.Regions.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                candidate.Regions.Add(region);
            }
        }

        foreach (var languageValue in entry.Languages)
        {
            foreach (var language in _taxonomy.NormalizeRomLanguageTokens(languageValue))
            {
                if (!candidate.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
                {
                    candidate.Languages.Add(language);
                }
            }
        }

        AddResolvedScore(candidate, ScoreRegion(candidate.Regions, options.RegionProfile), $"region:{string.Join(",", candidate.Regions.DefaultIfEmpty("unknown"))}");
        AddResolvedScore(candidate, ScoreLanguage(candidate.Languages, options.LanguageProfile), $"language:{string.Join(",", candidate.Languages.DefaultIfEmpty("unknown"))}");

        if (!string.IsNullOrWhiteSpace(group.PreferredPath) &&
            string.Equals(group.PreferredPath, entry.Gamelist.Path, StringComparison.OrdinalIgnoreCase))
        {
            AddResolvedScore(candidate, 25, "group-pref");
        }

        if (LooksLikeRetail(displayName))
        {
            AddResolvedScore(candidate, IsMode(options.RomVersionMode, "original") ? 45 : 20, "retail");
        }

        if (LooksLikeLatestRevision(displayName))
        {
            AddResolvedScore(candidate, IsMode(options.RomVersionMode, "latest") ? 45 : 12, "latest-revision");
        }

        if (IsMode(options.RomVersionMode, "stable") && LooksLikeRetail(displayName) && !LooksLikeSpecialVersion(displayName))
        {
            AddResolvedScore(candidate, 35, "stable");
        }

        if (LooksLikeTranslation(displayName))
        {
            var translationScore = IsMode(options.Translations, "prefer_if_language_match") &&
                IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)
                    ? 35
                    : IsMode(options.Translations, "hide") ? -500 : 0;
            AddResolvedScore(candidate, translationScore, "translation");
        }

        if (IsMode(options.RomVersionMode, "enhanced") && LooksLikeEnhancedVersion(displayName))
        {
            AddResolvedScore(candidate, 55, "enhanced");
        }

        if (LooksLikeSpecialVersion(displayName))
        {
            AddResolvedScore(candidate, IsMode(options.RomVersionMode, "enhanced") ? -15 : -80, "special-version");
        }

        return candidate;
    }

    private static void AddResolvedScore(RomSetResolvedVariantCandidate candidate, int score, string reason)
    {
        candidate.Score += score;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            candidate.Reasons.Add($"{reason}:{score}");
        }
    }

    private void AddLocalVariantDecisions(
        string systemId,
        RomSetManagerOptionsSnapshot options,
        IReadOnlyList<RomSetGamelistEntry> entries,
        RomSetPlan plan)
    {
        if (IsVariantDecisionDisabled(options, systemId))
        {
            return;
        }

        var fallbackEntries = entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.PathKey) || !plan.ResolvedCompactPaths.Contains(entry.PathKey))
            .ToList();
        if (fallbackEntries.Count < 2)
        {
            return;
        }

        foreach (var group in BuildLocalVariantGroups(fallbackEntries))
        {
            if (group.Count < 2)
            {
                continue;
            }

            var candidates = group
                .Select(entry => BuildLocalVariantCandidate(entry, options))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.DisplayName.Length)
                .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = candidates[0];

            var decision = new RomSetVariantDecision
            {
                SystemId = systemId,
                GroupId = group[0].LocalGroupKey,
                DisplayName = selected.DisplayName,
                Mode = options.VariantMode,
                RegionProfile = options.RegionProfile,
                LanguageProfile = options.LanguageProfile,
                SelectedRom = selected.DisplayName,
                SelectedNormalizedRom = selected.NormalizedRom,
                SelectedScore = selected.Score,
                SelectedReasons = selected.Reasons.ToList(),
                CandidateCount = candidates.Count
            };

            foreach (var candidate in candidates)
            {
                var isSelected = string.Equals(candidate.Path, selected.Path, StringComparison.OrdinalIgnoreCase);
                decision.Candidates.Add(new RomSetVariantCandidateReport
                {
                    Rom = candidate.DisplayName,
                    NormalizedRom = candidate.NormalizedRom,
                    Score = candidate.Score,
                    Regions = candidate.Regions.ToList(),
                    Languages = candidate.Languages.ToList(),
                    Reasons = candidate.Reasons.ToList(),
                    Selected = isSelected
                });

                if (!isSelected && (IsMode(options.VariantMode, "hide_variants") || !options.ShowClones))
                {
                    AddPathReason(candidate.Path, $"local-variant-selected:{selected.Path}", plan.ReasonsByPath);
                }
            }

            AddLocalLanguageAndRegionOnlyReasons(candidates, options, plan);

            plan.VariantDecisions.Add(decision);
        }
    }

    private static void AddGamelistMarkerReasons(
        RomSetManagerOptionsSnapshot options,
        IReadOnlyList<RomSetGamelistEntry> entries,
        RomSetPlan plan)
    {
        if (options.ShowNonGames)
        {
            return;
        }

        foreach (var entry in entries.Where(entry => entry.IsExplicitNonGame))
        {
            AddPathReason(entry.Path, "non-game", plan.ReasonsByPath);
        }
    }

    private static void AddUnknownRomReasons(
        RomSetManagerOptionsSnapshot options,
        IReadOnlyList<RomSetGamelistEntry> entries,
        RomSetPlan plan,
        bool romSetPlanUnavailable)
    {
        if (options.ShowUnknownRoms || romSetPlanUnavailable)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.IsExplicitNonGame ||
                entry.HasKnownMetadata ||
                IsResolvedByRomSet(entry, plan))
            {
                continue;
            }

            AddPathReason(entry.Path, "unknown-rom", plan.ReasonsByPath);
        }
    }

    private static bool IsResolvedByRomSet(RomSetGamelistEntry entry, RomSetPlan plan)
    {
        return (!string.IsNullOrWhiteSpace(entry.PathKey) && plan.ResolvedPaths.Contains(entry.PathKey)) ||
            (!string.IsNullOrWhiteSpace(entry.NormalizedRom) && plan.ResolvedRoms.Contains(entry.NormalizedRom));
    }

    private static List<List<RomSetGamelistEntry>> BuildLocalVariantGroups(IReadOnlyList<RomSetGamelistEntry> entries)
    {
        var grouped = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.LocalGroupKey))
            .GroupBy(entry => entry.LocalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.ToList())
            .ToList();

        var numericGroups = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.NumericFamilyKey))
            .GroupBy(entry => entry.NumericFamilyKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.ToList());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<List<RomSetGamelistEntry>>();
        foreach (var group in grouped.Concat(numericGroups))
        {
            var signature = string.Join("|", group.Select(entry => entry.NormalizedRom).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            if (seen.Add(signature))
            {
                result.Add(group);
            }
        }

        return result;
    }

    private RomSetLocalVariantCandidate BuildLocalVariantCandidate(RomSetGamelistEntry entry, RomSetManagerOptionsSnapshot options)
    {
        var variant = BuildVariantCandidate(entry.DisplayName, new JsonObject(), options);
        return new RomSetLocalVariantCandidate
        {
            Path = entry.Path,
            DisplayName = entry.DisplayName,
            NormalizedRom = entry.NormalizedRom,
            Score = variant.Score,
            Regions = variant.Regions.ToList(),
            Languages = variant.Languages.ToList(),
            Reasons = variant.Reasons.ToList()
        };
    }

    private static bool IsVariantDecisionDisabled(RomSetManagerOptionsSnapshot options, string systemId)
    {
        if (IsMode(options.VariantMode, "off"))
        {
            return true;
        }

        if (IsArcadeSystem(systemId))
        {
            return IsMode(options.ArcadeHandling, "disabled");
        }

        return false;
    }

    private static bool IsOfficialGameEntry(
        string type,
        string role,
        string releaseKind,
        IReadOnlyCollection<string> flags,
        IReadOnlyCollection<string> contentWarnings,
        IReadOnlyCollection<string> categories,
        bool isPrototype,
        bool isDemo,
        bool isBetaAlpha,
        bool isLocationTest,
        bool isUsefulPatch,
        bool isCheatTrainer,
        bool isHackMod,
        bool isBootlegPirate,
        bool isUnlicensed,
        bool isHomebrew,
        bool isQuiz,
        bool isArcadeDiagnostic,
        bool isNonGame)
    {
        if (isPrototype ||
            isDemo ||
            isBetaAlpha ||
            isLocationTest ||
            isUsefulPatch ||
            isCheatTrainer ||
            isHackMod ||
            isBootlegPirate ||
            isUnlicensed ||
            isHomebrew ||
            isQuiz ||
            isArcadeDiagnostic ||
            isNonGame)
        {
            return false;
        }

        if (contentWarnings.Any(value => value is "adult" or "casino" or "gambling" or "mahjong") ||
            categories.Any(value =>
                value.Contains("casino", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("mahjong", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("quiz", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (type is "" or "game" or "release" or "retail" ||
            releaseKind is "" or "official" or "retail" or "licensed" ||
            role is "" or "parent" or "clone")
        {
            return true;
        }

        return flags.Count == 0;
    }

    private RomSetVariantCandidate BuildVariantCandidate(
        string rom,
        JsonObject group,
        RomSetManagerOptionsSnapshot options)
    {
        var candidate = new RomSetVariantCandidate
        {
            Rom = rom,
            NormalizedRom = NormalizeRomId(rom)
        };

        candidate.Regions.AddRange(ExtractRegions(rom));
        candidate.Languages.AddRange(ExtractLanguages(rom));
        AddCompactMetadata(candidate, group);
        var preferredRom = ReadString(group, "pref");

        AddScore(candidate, ScoreRegion(candidate.Regions, options.RegionProfile), $"region:{string.Join(",", candidate.Regions.DefaultIfEmpty("unknown"))}");
        AddScore(candidate, ScoreLanguage(candidate.Languages, options.LanguageProfile), $"language:{string.Join(",", candidate.Languages.DefaultIfEmpty("unknown"))}");

        if (string.Equals(NormalizeRomId(preferredRom), candidate.NormalizedRom, StringComparison.OrdinalIgnoreCase))
        {
            AddScore(candidate, 25, "group-pref");
        }

        if (LooksLikeRetail(rom))
        {
            AddScore(candidate, IsMode(options.RomVersionMode, "original") ? 45 : 20, "retail");
        }

        if (LooksLikeLatestRevision(rom))
        {
            AddScore(candidate, IsMode(options.RomVersionMode, "latest") ? 45 : 12, "latest-revision");
        }

        if (IsMode(options.RomVersionMode, "stable") && LooksLikeRetail(rom) && !LooksLikeSpecialVersion(rom))
        {
            AddScore(candidate, 35, "stable");
        }

        if (LooksLikeTranslation(rom))
        {
            var translationScore = IsMode(options.Translations, "prefer_if_language_match") &&
                IsLanguageProfileMatched(candidate.Languages, options.LanguageProfile)
                    ? 35
                    : IsMode(options.Translations, "hide") ? -500 : 0;
            AddScore(candidate, translationScore, "translation");
        }

        if (IsMode(options.RomVersionMode, "enhanced") && LooksLikeEnhancedVersion(rom))
        {
            AddScore(candidate, 55, "enhanced");
        }

        if (LooksLikeSpecialVersion(rom))
        {
            AddScore(candidate, IsMode(options.RomVersionMode, "enhanced") ? -15 : -80, "special-version");
        }

        return candidate;
    }

    private static void AddScore(RomSetVariantCandidate candidate, int score, string reason)
    {
        candidate.Score += score;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            candidate.Reasons.Add($"{reason}:{score}");
        }
    }

    private void AddCompactMetadata(RomSetVariantCandidate candidate, JsonObject group)
    {
        if (!group.TryGetPropertyValue("meta", out var metaNode) ||
            metaNode is not JsonObject meta ||
            !meta.TryGetPropertyValue(candidate.NormalizedRom, out var entryNode) ||
            entryNode is not JsonObject entry)
        {
            return;
        }

        foreach (var regionValue in ReadStringArray(entry, "reg"))
        {
            var region = _taxonomy.NormalizeRomRegionToken(regionValue);
            if (!string.IsNullOrWhiteSpace(region) && !candidate.Regions.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                candidate.Regions.Add(region);
            }
        }

        foreach (var languageValue in ReadStringArray(entry, "lang"))
        {
            foreach (var language in _taxonomy.NormalizeRomLanguageTokens(languageValue))
            {
                if (!candidate.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
                {
                    candidate.Languages.Add(language);
                }
            }
        }
    }

    private int ScoreRegion(IReadOnlyList<string> regions, string profile)
    {
        var priorities = _taxonomy.BuildRomRegionPriority(profile);
        if (regions.Count == 0)
        {
            return 5;
        }

        var bestIndex = regions
            .Select(region => FindIndex(priorities, value => string.Equals(value, region, StringComparison.OrdinalIgnoreCase)))
            .Where(index => index >= 0)
            .DefaultIfEmpty(priorities.Count + 2)
            .Min();
        return Math.Max(0, 100 - (bestIndex * 12));
    }

    private int ScoreLanguage(IReadOnlyList<string> languages, string profile)
    {
        var priorities = _taxonomy.BuildRomLanguagePriority(profile);
        if (languages.Count == 0)
        {
            return 5;
        }

        var bestIndex = languages
            .Select(language => FindIndex(priorities, value => string.Equals(value, language, StringComparison.OrdinalIgnoreCase)))
            .Where(index => index >= 0)
            .DefaultIfEmpty(priorities.Count + 2)
            .Min();
        return Math.Max(0, 80 - (bestIndex * 10));
    }

    private List<string> ExtractRegions(string rom)
    {
        var regions = new List<string>();
        foreach (var token in ExtractTagTokens(rom))
        {
            var region = _taxonomy.NormalizeRomRegionToken(token);
            if (!string.IsNullOrWhiteSpace(region) && !regions.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                regions.Add(region);
            }
        }

        return regions;
    }

    private static int FindIndex(IReadOnlyList<string> values, Predicate<string> predicate)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (predicate(values[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private List<string> ExtractLanguages(string rom)
    {
        var languages = new List<string>();
        foreach (var token in ExtractTagTokens(rom))
        {
            foreach (var language in _taxonomy.NormalizeRomLanguageTokens(token))
            {
                if (!languages.Contains(language, StringComparer.OrdinalIgnoreCase))
                {
                    languages.Add(language);
                }
            }
        }

        if (Regex.IsMatch(rom, @"T[+-](Fre|Fr|French)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            !languages.Contains("Fr", StringComparer.OrdinalIgnoreCase))
        {
            languages.Add("Fr");
        }

        return languages;
    }

    private static IEnumerable<string> ExtractTagTokens(string rom)
    {
        foreach (Match match in TagRegex.Matches(rom))
        {
            foreach (var token in match.Groups[1].Value.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    yield return token.Trim();
                }
            }
        }
    }

    private static bool LooksLikeRetail(string rom)
    {
        return !LooksLikeSpecialVersion(rom) && !LooksLikeTranslation(rom);
    }

    private static bool LooksLikeSpecialVersion(string rom)
    {
        return Regex.IsMatch(rom, @"\b(proto|prototype|beta|demo|sample|pirate|unl|unlicensed|homebrew|hack)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeEnhancedVersion(string rom)
    {
        return Regex.IsMatch(
            rom,
            @"\b(qol|quality[ -]?of[ -]?life|bugfix|fix|stability|restoration|uncensored|widescreen|accessibility|compatibility|translation|translated)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeLatestRevision(string rom)
    {
        return Regex.IsMatch(rom, @"\b(rev\s*[2-9]|revision\s*[2-9]|v[2-9](\.\d+)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeTranslation(string rom)
    {
        return Regex.IsMatch(rom, @"(\btranslation\b|\btranslated\b|T[+-](Fre|Fr|French|Eng|En))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private bool IsLanguageProfileMatched(IReadOnlyList<string> languages, string profile)
    {
        var priority = _taxonomy.BuildRomLanguagePriority(profile);
        return languages.Any(language => string.Equals(language, priority[0], StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsArcadeSystem(string systemId)
    {
        return systemId.Equals("mame", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("fbneo", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("neogeo", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMode(string value, string expected)
    {
        return string.Equals(NormalizeOption(value), NormalizeOption(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOption(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
    }

    private static bool ShouldCheckPlayerCount(RomSetManagerOptionsSnapshot options)
    {
        return IsMode(options.PlayerCount, "only") ||
            IsMode(options.CabinetControlsCompatibility, "only");
    }

    private static bool ShouldCheckButtonCompatibility(RomSetManagerOptionsSnapshot options)
    {
        return IsMode(options.ButtonCompatibility, "only") ||
            IsMode(options.CabinetControlsCompatibility, "only");
    }

    private static bool HasUnsupportedRequiredControl(
        IReadOnlyList<string> controls,
        IReadOnlyList<string> aux,
        RomSetManagerOptionsSnapshot options)
    {
        foreach (var control in controls)
        {
            if (control is "only_buttons" or "unknown" or "misc")
            {
                continue;
            }

            if (control.Contains("spinner", StringComparison.OrdinalIgnoreCase) ||
                control.Contains("dial", StringComparison.OrdinalIgnoreCase) ||
                control.Contains("paddle", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlSpinner))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("trackball", StringComparison.OrdinalIgnoreCase) ||
                control.Contains("roller", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlTrackball))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("wheel", StringComparison.OrdinalIgnoreCase) ||
                control.Contains("handlebar", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlWheel))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("pedal", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlPedals))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("shifter", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlShifter))
                {
                    return true;
                }

                continue;
            }

            if (control is "gun" or "lightgun")
            {
                if (!HasDevice(options.ControlLightgun))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("rotary_joystick", StringComparison.OrdinalIgnoreCase))
            {
                if (!options.ControlRotaryJoystick)
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("analog_stick", StringComparison.OrdinalIgnoreCase) ||
                control.Contains("yoke", StringComparison.OrdinalIgnoreCase) ||
                control.Contains("throttle", StringComparison.OrdinalIgnoreCase))
            {
                if (!options.ControlAnalogJoystick)
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("turntable", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlTurntable))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("dance", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasDevice(options.ControlDanceMat))
                {
                    return true;
                }

                continue;
            }

            if (control.Contains("guitar", StringComparison.OrdinalIgnoreCase) && !HasDevice(options.ControlGuitar))
            {
                return true;
            }

            if (control.Contains("drum", StringComparison.OrdinalIgnoreCase) && !HasDevice(options.ControlDrums))
            {
                return true;
            }

            if ((control.Contains("mahjong", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("hanafuda", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("keypad", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("gambling", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("poker", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("slot", StringComparison.OrdinalIgnoreCase)) &&
                !options.ControlKeyboard)
            {
                return true;
            }

            if ((control.Contains("joy", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("triggerstick", StringComparison.OrdinalIgnoreCase) ||
                 control.Contains("top_fire", StringComparison.OrdinalIgnoreCase)) &&
                !options.ControlArcadeJoystick)
            {
                return true;
            }
        }

        return aux.Any(value =>
            (value == "touchscreen" && !options.ControlTouchscreen) ||
            (value == "mouse" && !options.ControlMouse) ||
            (value == "motion_controller" && !options.ControlMotionController) ||
            (value == "microphone" && !options.ControlMicrophone));
    }

    private static bool HasDevice(string value)
    {
        var normalized = NormalizeOption(value);
        return !string.IsNullOrWhiteSpace(normalized) &&
            normalized is not "0" and not "none" and not "false" and not "off";
    }

    private static string ReadString(JsonObject group, string field)
    {
        return group.TryGetPropertyValue(field, out var node) && node != null
            ? node.ToString()
            : string.Empty;
    }

    private static List<string> ReadStringArray(JsonObject group, string field)
    {
        if (!group.TryGetPropertyValue(field, out var node) || node is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static int ReadInt(JsonObject group, string field)
    {
        if (!group.TryGetPropertyValue(field, out var node) || node == null)
        {
            return 0;
        }

        return int.TryParse(node.ToString(), out var value) && value == 1 ? 1 : 0;
    }

    private static int ReadPositiveInt(JsonObject group, string field)
    {
        if (!group.TryGetPropertyValue(field, out var node) || node == null)
        {
            return 0;
        }

        return int.TryParse(node.ToString(), out var value) && value > 0 ? value : 0;
    }

    private static int? ReadOptionalInt(JsonObject group, string field)
    {
        if (!group.TryGetPropertyValue(field, out var node) || node == null)
        {
            return null;
        }

        return int.TryParse(node.ToString(), out var value) ? value : null;
    }

    private static int? ReadNestedOptionalInt(JsonObject group, string objectField, string intField)
    {
        if (!group.TryGetPropertyValue(objectField, out var node) || node is not JsonObject nested)
        {
            return null;
        }

        return ReadOptionalInt(nested, intField);
    }

    /// <summary>
    /// F1 (bug Super Mario World) — ownership ledger of every game path this
    /// manager hid, persisted OUTSIDE gamelist.xml. ES's addgames ingestion
    /// clears a game's unknown XML elements (MetaData loadFromXML), stripping
    /// the apiexpose_romset_* ownership tags while &lt;hidden&gt; survives: the
    /// entry becomes an orphan the manager would otherwise refuse to touch
    /// forever. The ledger survives that strip and lets the manager reclaim
    /// its orphans on the next run.
    /// </summary>
    private static class RomSetHiddenLedger
    {
        private static readonly object Sync = new();
        private static Dictionary<string, Dictionary<string, bool>>? _entries;
        private static bool _dirty;

        private static string LedgerPath => Path.Combine(RetroBatPaths.MediaAliasesSharedRoot, "romset-hidden-ledger.json");

        private static Dictionary<string, Dictionary<string, bool>> EnsureLoaded()
        {
            if (_entries != null)
            {
                return _entries;
            }

            try
            {
                if (File.Exists(LedgerPath))
                {
                    _entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(
                        File.ReadAllText(LedgerPath));
                }
            }
            catch
            {
                _entries = null;
            }

            _entries ??= new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
            return _entries;
        }

        public static bool TryGet(string systemId, string pathKey, out bool originalHidden)
        {
            lock (Sync)
            {
                originalHidden = false;
                return EnsureLoaded().TryGetValue(systemId, out var system) &&
                    system.TryGetValue(pathKey, out originalHidden);
            }
        }

        public static void Set(string systemId, string pathKey, bool originalHidden)
        {
            lock (Sync)
            {
                var entries = EnsureLoaded();
                if (!entries.TryGetValue(systemId, out var system))
                {
                    entries[systemId] = system = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                }

                if (!system.TryGetValue(pathKey, out var existing) || existing != originalHidden)
                {
                    system[pathKey] = originalHidden;
                    _dirty = true;
                }
            }
        }

        public static void Remove(string systemId, string pathKey)
        {
            lock (Sync)
            {
                if (EnsureLoaded().TryGetValue(systemId, out var system) && system.Remove(pathKey))
                {
                    _dirty = true;
                }
            }
        }

        public static void SaveIfDirty(ILogger? logger)
        {
            lock (Sync)
            {
                if (!_dirty || _entries == null)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
                    File.WriteAllText(LedgerPath, System.Text.Json.JsonSerializer.Serialize(
                        _entries,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Unable to persist the romset hidden ledger.");
                }
            }
        }
    }

    private static bool ApplyApiHidden(XElement game, IReadOnlyList<string> reasons, bool claimExistingHidden)
    {
        var wasHidden = IsHidden(game);
        if (wasHidden && !IsApiHidden(game) && !claimExistingHidden)
        {
            return false;
        }

        var changed = SetElementValue(game, "hidden", "true");
        changed |= SetElementValue(game, ApiHiddenTag, "true");
        changed |= SetElementValue(game, ApiReasonsTag, string.Join(";", reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase)));
        if (game.Element(ApiOriginalHiddenTag) == null)
        {
            game.Add(new XElement(ApiOriginalHiddenTag, wasHidden ? "true" : "false"));
            changed = true;
        }

        return changed;
    }

    private static void RestoreApiHidden(XElement game)
    {
        var originalHidden = string.Equals(game.Element(ApiOriginalHiddenTag)?.Value, "true", StringComparison.OrdinalIgnoreCase);
        if (originalHidden)
        {
            SetElementValue(game, "hidden", "true");
        }
        else
        {
            game.Element("hidden")?.Remove();
        }

        game.Element(ApiHiddenTag)?.Remove();
        game.Element(ApiReasonsTag)?.Remove();
        game.Element(ApiOriginalHiddenTag)?.Remove();
    }

    private static bool SetElementValue(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element == null)
        {
            parent.Add(new XElement(name, value));
            return true;
        }

        if (string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = value;
        return true;
    }

    private static bool IsHidden(XElement game)
    {
        var value = game.Element("hidden")?.Value?.Trim() ?? string.Empty;
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private static bool IsApiHidden(XElement game)
    {
        var value = game.Element(ApiHiddenTag)?.Value?.Trim() ?? string.Empty;
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private static List<string> GetApiReasons(XElement game)
    {
        return (game.Element(ApiReasonsTag)?.Value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static RomSetManagerChange BuildChange(string systemId, string path, string normalizedRom, string action, IReadOnlyList<string> reasons)
    {
        return new RomSetManagerChange
        {
            SystemId = systemId,
            GamePath = path,
            NormalizedRom = normalizedRom,
            Action = action,
            Reasons = reasons.ToList()
        };
    }

    private static RomSetGamelistEntry BuildGamelistEntry(XElement game)
    {
        var path = game.Element("path")?.Value ?? string.Empty;
        var fileTitle = Path.GetFileNameWithoutExtension(path);
        var displayName = game.Element("name")?.Value;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = fileTitle;
        }

        return new RomSetGamelistEntry
        {
            Path = path,
            PathKey = NormalizeGamePath(path),
            DisplayName = displayName ?? string.Empty,
            NormalizedRom = NormalizeRomId(fileTitle),
            Md5 = NormalizeHex(game.Element("md5")?.Value),
            Crc32 = NormalizeHex(game.Element("crc32")?.Value),
            Sha1 = NormalizeHex(game.Element("sha1")?.Value),
            Hash = NormalizeHex(game.Element("hash")?.Value),
            CheevosHash = NormalizeHex(game.Element("cheevosHash")?.Value),
            CheevosId = NormalizeDecimal(game.Element("cheevosId")?.Value),
            EsGameId = game.Element("gameid")?.Value?.Trim() ?? string.Empty,
            HasKnownMetadata = HasKnownGamelistMetadata(game),
            IsExplicitNonGame = LooksLikeNonGameMarker(displayName) || LooksLikeNonGameMarker(path),
            LocalGroupKey = BuildLocalVariantGroupKey(displayName ?? fileTitle ?? string.Empty),
            NumericFamilyKey = BuildNumericFamilyKey(displayName ?? fileTitle ?? string.Empty)
        };
    }

    private static bool HasKnownGamelistMetadata(XElement game)
    {
        if (!string.IsNullOrWhiteSpace(game.Attribute("id")?.Value) ||
            game.Elements("scrap").Any())
        {
            return true;
        }

        var meaningfulTags = new[]
        {
            "desc",
            "genre",
            "genres",
            "developer",
            "publisher",
            "players",
            "source",
            "image",
            "thumbnail",
            "video",
            "marquee",
            "fanart",
            "mix",
            "mixrbv1",
            "mixrbv2",
            "wheel",
            "steamgrid",
            "boxart",
            "screenmarquee",
            "screenmarqueesmall",
            "label",
            "cartridge",
            "manual",
            "magazine",
            "map",
            "bezel"
        };

        return meaningfulTags.Any(tag => !string.IsNullOrWhiteSpace(game.Element(tag)?.Value));
    }

    private static bool LooksLikeNonGameMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("#NONGAME", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("notgame", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("non-game", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("non_game", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLocalVariantGroupKey(string value)
    {
        var withoutTags = StripVariantTags(value);
        return NormalizeRomId(withoutTags);
    }

    private static string BuildNumericFamilyKey(string value)
    {
        var normalized = BuildLocalVariantGroupKey(value);
        var match = Regex.Match(normalized, @"^(\d{3,4})(?:-|$)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string StripVariantTags(string value)
    {
        var stripped = TagRegex.Replace(value ?? string.Empty, string.Empty);
        return Regex.Replace(stripped, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string NormalizeGamePath(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .TrimStart('.');
    }

    private static string NormalizeRomId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = normalized.Replace('\\', '/');
        normalized = Path.GetFileName(normalized);
        normalized = NormalizeKnownRomTags(normalized);
        normalized = NonAlphaNumericRegex.Replace(normalized, "-");
        return normalized.Trim('-');
    }

    private static string NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value.Trim(), "[^0-9a-fA-F]", string.Empty, RegexOptions.CultureInvariant);
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value.Trim(), "[^0-9]", string.Empty, RegexOptions.CultureInvariant);
        return normalized.TrimStart('0');
    }

    private static void AddMatchKey(List<string> keys, ISet<string> seen, string prefix, string value)
    {
        var normalized = NormalizeRomId(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AddRawMatchKey(keys, seen, prefix + ":" + normalized);
        }
    }

    private static void AddHashMatchKey(List<string> keys, ISet<string> seen, string prefix, string value)
    {
        var normalized = NormalizeHex(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AddRawMatchKey(keys, seen, prefix + ":" + normalized);
        }
    }

    private static void AddRawMatchKey(List<string> keys, ISet<string> seen, string key)
    {
        if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
        {
            keys.Add(key);
        }
    }

    private static string NormalizeKnownRomTags(string value)
    {
        var normalized = value;
        normalized = Regex.Replace(normalized, @"\((u)\)", "(usa)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\((e)\)", "(europe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\((j)\)", "(japan)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\((w)\)", "(world)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\[(?:!|a\d*|b\d*|o\d*|p\d*|h\d*|f\d*)\]", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string ResolveProfileMode(string configuredMode, string profileMode)
    {
        var normalized = NormalizeOption(configuredMode);
        return string.IsNullOrWhiteSpace(normalized) || normalized == "auto"
            ? NormalizeOption(profileMode)
            : normalized;
    }

    private static bool ResolveProfileShow(string configuredMode, bool profileValue)
    {
        var normalized = NormalizeOption(configuredMode);
        return normalized switch
        {
            "show" or "on" or "true" or "1" => true,
            "hide" or "off" or "false" or "0" => false,
            _ => profileValue
        };
    }

    private static bool IsAutoMode(string? value)
    {
        var normalized = NormalizeOption(value);
        return string.IsNullOrWhiteSpace(normalized) || normalized == "auto";
    }

    private static bool IsPrereleaseAlphaBeta(string? value)
    {
        var normalized = NormalizeOption(value);
        return normalized is "alpha" or "beta" or "alpha_version" or "beta_version" or "version_alpha" or "version_beta";
    }

    private static bool HasExplicitAlphaBetaBuildMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(
            value,
            @"(?:\((?:alpha|beta)(?:\s*[-_#]?\s*\d+)?\)|\[(?:alpha|beta)(?:\s*[-_#]?\s*\d+)?\])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static RomSetProfilePolicy BuildProfilePolicy(string profile)
    {
        var policy = new RomSetProfilePolicy();
        switch (NormalizeOption(profile))
        {
            case "casual_gamer":
                policy.ShowMahjong = false;
                policy.ShowUnlicensed = false;
                break;
            case "hard_gamer":
                policy.ShowHacksMods = true;
                break;
            case "pro_gamer":
                policy.RetroAchievementsMode = "no_filter";
                policy.ShowUnlicensed = false;
                policy.ShowMahjong = false;
                policy.ShowQuiz = false;
                break;
            case "retroachiever":
                policy.RetroAchievementsMode = "show_only";
                break;
            case "localized_player":
                policy.LanguageMode = "show_only_my_language";
                policy.RomVersionMode = "enhanced";
                break;
            case "arcade_purist":
                policy.RetroAchievementsMode = "no_filter";
                policy.RomVersionMode = "original";
                policy.ShowUsefulPatches = false;
                policy.ShowUnlicensed = false;
                policy.ShowHomebrewsAftermarket = false;
                policy.ShowMahjong = false;
                policy.ShowQuiz = false;
                break;
            case "arcade_gamer":
            case "arcade_cleaner":
                policy.ShowMahjong = false;
                break;
            case "historian":
                policy.RetroAchievementsMode = "no_filter";
                policy.RomVersionMode = "original";
                policy.ShowClones = true;
                policy.ShowPrototypes = true;
                policy.ShowDemos = true;
                policy.ShowBetaAlpha = true;
                policy.ShowLocationTests = true;
                policy.ShowBootlegsPirates = true;
                policy.ShowCasino = true;
                policy.ShowArcadeDiagnostics = true;
                break;
            case "preservationist":
                policy.RetroAchievementsMode = "no_filter";
                policy.RomVersionMode = "auto";
                policy.ShowClones = true;
                policy.ShowPrototypes = true;
                policy.ShowDemos = true;
                policy.ShowBetaAlpha = true;
                policy.ShowLocationTests = true;
                policy.ShowHacksMods = true;
                policy.ShowCheatsTrainers = true;
                policy.ShowBootlegsPirates = true;
                policy.ShowAdult = true;
                policy.ShowCasino = true;
                policy.ShowArcadeDiagnostics = true;
                break;
            case "homebrew_player":
                policy.RomVersionMode = "enhanced";
                policy.ShowPrototypes = true;
                policy.ShowDemos = true;
                policy.ShowBetaAlpha = true;
                break;
            case "modder":
                policy.RomVersionMode = "enhanced";
                policy.ShowClones = true;
                policy.ShowHacksMods = true;
                break;
            case "hacker":
                policy.RetroAchievementsMode = "no_filter";
                policy.RomVersionMode = "auto";
                policy.ShowClones = true;
                policy.ShowPrototypes = true;
                policy.ShowDemos = true;
                policy.ShowBetaAlpha = true;
                policy.ShowLocationTests = true;
                policy.ShowHacksMods = true;
                policy.ShowCheatsTrainers = true;
                policy.ShowBootlegsPirates = true;
                policy.ShowAdult = true;
                policy.ShowCasino = true;
                policy.ShowNonGames = true;
                policy.ShowArcadeDiagnostics = true;
                break;
        }

        return policy;
    }

    private static bool ResolveBool(string key, bool appsettingsValue, IReadOnlyDictionary<string, string> esSettings)
    {
        return esSettings.TryGetValue(key, out var rawValue) && TryParseBool(rawValue, out var parsed)
            ? parsed
            : appsettingsValue;
    }

    private static bool ResolveRomSetShowBool(string key, bool appsettingsValue, IReadOnlyDictionary<string, string> esSettings, bool romsetVisibilityInitialized)
    {
        if (esSettings.TryGetValue(key, out var rawValue) && TryParseBool(rawValue, out var parsed))
        {
            return parsed;
        }

        return romsetVisibilityInitialized ? false : appsettingsValue;
    }

    private static string ResolveString(string key, string appsettingsValue, IReadOnlyDictionary<string, string> esSettings)
    {
        return esSettings.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue)
            ? rawValue.Trim()
            : appsettingsValue;
    }

    private static string ResolveFirstString(
        IReadOnlyDictionary<string, string> esSettings,
        string fallback,
        params (string Key, string AppsettingsValue)[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (esSettings.TryGetValue(candidate.Key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue))
            {
                return rawValue.Trim();
            }
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.AppsettingsValue))
            {
                return candidate.AppsettingsValue.Trim();
            }
        }

        return fallback;
    }

    private static int ResolveInt(
        string key,
        int appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        int minimum,
        int maximum)
    {
        var value = esSettings.TryGetValue(key, out var rawValue) && int.TryParse(rawValue, out var parsed)
            ? parsed
            : appsettingsValue;
        return Math.Clamp(value, minimum, maximum);
    }

    private static bool TryParseBool(string? value, out bool result)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static string ResolveGroupsRootPath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configuredPath));
    }

    private static string ResolveRomSetDataFile(string groupsRoot, string systemId)
    {
        if (string.IsNullOrWhiteSpace(groupsRoot) || string.IsNullOrWhiteSpace(systemId))
        {
            return string.Empty;
        }

        var normalizedSystem = NormalizeRomId(systemId);
        var aliasPath = Path.Combine(groupsRoot, "aliases.json");
        if (File.Exists(aliasPath))
        {
            try
            {
                var aliases = JsonSerializer.Deserialize<Dictionary<string, RomSetSystemAlias>>(File.ReadAllText(aliasPath))
                    ?? new Dictionary<string, RomSetSystemAlias>(StringComparer.OrdinalIgnoreCase);
                if (aliases.TryGetValue(normalizedSystem, out var alias) && !string.IsNullOrWhiteSpace(alias.Jsonl))
                {
                    var path = Path.Combine(groupsRoot, alias.Jsonl);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch (JsonException)
            {
                // Fall back to direct files when the alias manifest is missing or malformed.
            }
        }

        foreach (var fileName in BuildRomSetDataFileCandidates(normalizedSystem))
        {
            var path = Path.Combine(groupsRoot, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> BuildRomSetDataFileCandidates(string normalizedSystem)
    {
        if (!string.IsNullOrWhiteSpace(normalizedSystem))
        {
            yield return $"{normalizedSystem}_lt.json";
            yield return $"{normalizedSystem}_lt.jsonl";
            yield return $"{normalizedSystem}.json";
            yield return $"{normalizedSystem}.jsonl";
        }

        if (normalizedSystem is "arcade" or "mame64")
        {
            yield return "mame_lt.json";
            yield return "mame_lt.jsonl";
            yield return "mame.json";
            yield return "mame.jsonl";
        }
    }

    private static string WriteDebugReport(RomSetManagerApplyResponse response)
    {
        var directory = Path.Combine(RetroBatPaths.PluginRoot, "artifacts", "rom-set-manager");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"romset-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(path, json);
        return path;
    }

    private object GetGamelistLock(string gamelistPath)
    {
        return _gamelistStore.GetLock(gamelistPath);
    }

    private static string FindLatestBackup(string gamelistPath)
    {
        var backupDirectory = Path.Combine(Path.GetDirectoryName(gamelistPath)!, BackupDirectoryName);
        if (!Directory.Exists(backupDirectory))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(backupDirectory)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? string.Empty;
    }

    private sealed class RomSetPlan
    {
        public Dictionary<string, List<string>> ReasonsByRom { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> ReasonsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ResolvedCompactPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ResolvedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ResolvedRoms { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RomSetVariantDecision> VariantDecisions { get; } = new();
    }

    private sealed class RomSetGamelistEntry
    {
        public string Path { get; set; } = string.Empty;
        public string PathKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string NormalizedRom { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public string Crc32 { get; set; } = string.Empty;
        public string Sha1 { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string CheevosHash { get; set; } = string.Empty;
        public string CheevosId { get; set; } = string.Empty;
        public string EsGameId { get; set; } = string.Empty;
        public bool HasKnownMetadata { get; set; }
        public bool IsExplicitNonGame { get; set; }
        public string LocalGroupKey { get; set; } = string.Empty;
        public string NumericFamilyKey { get; set; } = string.Empty;
    }

    private sealed class RomSetGamelistIdentityIndex
    {
        public Dictionary<string, List<RomSetGamelistEntry>> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static RomSetGamelistIdentityIndex Build(IReadOnlyList<RomSetGamelistEntry> entries)
        {
            var index = new RomSetGamelistIdentityIndex();
            foreach (var entry in entries)
            {
                index.Add(entry);
            }

            return index;
        }

        private void Add(RomSetGamelistEntry entry)
        {
            AddEntryKey("rom:" + entry.NormalizedRom, entry);
            AddMatchKeyFromValue("rom", entry.DisplayName, entry);
            AddMatchKeyFromValue("rom", Path.GetFileNameWithoutExtension(entry.Path), entry);
            AddHashEntryKey("md5", entry.Md5, entry);
            AddHashEntryKey("crc32", entry.Crc32, entry);
            AddHashEntryKey("sha1", entry.Sha1, entry);
            AddGenericHashEntryKey(entry.Hash, entry);
            AddHashEntryKey("cheevos-hash", entry.CheevosHash, entry);
            AddHashEntryKey("md5", entry.CheevosHash, entry);
            if (!string.IsNullOrWhiteSpace(entry.CheevosId))
            {
                AddEntryKey("cheevos-id:" + entry.CheevosId, entry);
            }
        }

        private void AddMatchKeyFromValue(string prefix, string value, RomSetGamelistEntry entry)
        {
            var normalized = NormalizeRomId(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                AddEntryKey(prefix + ":" + normalized, entry);
            }
        }

        private void AddHashEntryKey(string prefix, string value, RomSetGamelistEntry entry)
        {
            var normalized = NormalizeHex(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                AddEntryKey(prefix + ":" + normalized, entry);
            }
        }

        private void AddGenericHashEntryKey(string value, RomSetGamelistEntry entry)
        {
            var normalized = NormalizeHex(value);
            switch (normalized.Length)
            {
                case 8:
                    AddEntryKey("crc32:" + normalized, entry);
                    break;
                case 32:
                    AddEntryKey("md5:" + normalized, entry);
                    break;
                case 40:
                    AddEntryKey("sha1:" + normalized, entry);
                    break;
            }
        }

        private void AddEntryKey(string key, RomSetGamelistEntry entry)
        {
            if (string.IsNullOrWhiteSpace(key) || key.EndsWith(":", StringComparison.Ordinal))
            {
                return;
            }

            if (!ByKey.TryGetValue(key, out var entries))
            {
                entries = new List<RomSetGamelistEntry>();
                ByKey[key] = entries;
            }

            if (!entries.Any(candidate => string.Equals(candidate.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(entry);
            }
        }
    }

    private sealed class RomSetResolvedCompactEntry
    {
        public JsonObject Entry { get; set; } = new();
        public RomSetGamelistEntry Gamelist { get; set; } = new();
        public string GroupId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> Regions { get; set; } = new();
        public List<string> Languages { get; set; } = new();
    }

    private sealed class RomSetResolvedVariantGroup
    {
        public string GroupId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string PreferredPath { get; set; } = string.Empty;
        public List<RomSetResolvedCompactEntry> Entries { get; } = new();
    }

    private sealed class RomSetResolvedVariantCandidate
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string NormalizedRom { get; set; } = string.Empty;
        public int Score { get; set; }
        public List<string> Regions { get; } = new();
        public List<string> Languages { get; } = new();
        public List<string> Reasons { get; } = new();
    }

    private sealed class RomSetVariantCandidate
    {
        public string Rom { get; set; } = string.Empty;
        public string NormalizedRom { get; set; } = string.Empty;
        public int Score { get; set; }
        public List<string> Regions { get; } = new();
        public List<string> Languages { get; } = new();
        public List<string> Reasons { get; } = new();
    }

    private sealed class RomSetLocalVariantCandidate
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string NormalizedRom { get; set; } = string.Empty;
        public int Score { get; set; }
        public List<string> Regions { get; set; } = new();
        public List<string> Languages { get; set; } = new();
        public List<string> Reasons { get; set; } = new();
    }

    private sealed class RomSetProfilePolicy
    {
        public string RetroAchievementsMode { get; set; } = "no_filter";
        public string LanguageMode { get; set; } = "auto";
        public string RegionMode { get; set; } = "auto";
        public string RomVersionMode { get; set; } = "stable";
        public bool ShowOfficialGames { get; set; } = true;
        public bool ShowClones { get; set; }
        public bool ShowPrototypes { get; set; }
        public bool ShowDemos { get; set; }
        public bool ShowBetaAlpha { get; set; }
        public bool ShowLocationTests { get; set; }
        public bool ShowUsefulPatches { get; set; } = true;
        public bool ShowHacksMods { get; set; }
        public bool ShowCheatsTrainers { get; set; }
        public bool ShowBootlegsPirates { get; set; }
        public bool ShowUnlicensed { get; set; } = true;
        public bool ShowHomebrewsAftermarket { get; set; } = true;
        public bool ShowAdult { get; set; }
        public bool ShowCasino { get; set; }
        public bool ShowMahjong { get; set; } = true;
        public bool ShowQuiz { get; set; } = true;
        public bool ShowNonGames { get; set; }
        public bool ShowUnknownRoms { get; set; } = true;
        public bool ShowArcadeDiagnostics { get; set; }
    }

    private sealed class RomSetSystemAlias
    {
        [JsonPropertyName("jsonl")]
        public string Jsonl { get; set; } = string.Empty;
    }
}

public sealed class RomSetManagerApplyRequest
{
    public string? SystemId { get; set; }
    public bool AllSystems { get; set; }
    public bool DryRun { get; set; } = true;
    public bool ReloadGames { get; set; } = true;
    public bool ClaimExistingHidden { get; set; }
}

public sealed class RomSetManagerApplyCurrentCheckRequest
{
    public string? SystemId { get; set; }
    public bool AllSystems { get; set; } = true;
    public bool ReloadGames { get; set; } = true;
    public bool ClaimExistingHidden { get; set; } = true;
}

public sealed class RomSetManagerApplyCurrentCheckResponse
{
    public bool Coherent { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> IgnoredSystems { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public RomSetManagerApplyResponse Apply { get; set; } = new();
    public RomSetManagerApplyResponse Verification { get; set; } = new();
}

public sealed class RomSetManagerApplyResponse
{
    public bool DryRun { get; set; }
    public bool Restore { get; set; }
    public bool Enabled { get; set; }
    public string Message { get; set; } = string.Empty;
    public RomSetManagerOptionsSnapshot Options { get; set; } = new();
    public List<string> Systems { get; set; } = new();
    public int GamesScanned { get; set; }
    public int GamesMatched { get; set; }
    public int GamesToHide { get; set; }
    public int GamesToRestore { get; set; }
    public int GamesChanged { get; set; }
    public int VariantGroupsAnalyzed { get; set; }
    public bool ReloadGamesRequested { get; set; }
    public string DebugReportPath { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public List<RomSetManagerSystemResult> Results { get; set; } = new();
}

public sealed class RomSetManagerSystemResult
{
    public string SystemId { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public bool Restore { get; set; }
    public string GamelistPath { get; set; } = string.Empty;
    public string GroupsFilePath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public int GamesScanned { get; set; }
    public int GamesMatched { get; set; }
    public int GamesToHide { get; set; }
    public int GamesToRestore { get; set; }
    public int GamesChanged { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<RomSetManagerChange> Changes { get; set; } = new();
    public List<RomSetVariantDecision> VariantDecisions { get; set; } = new();
}

public sealed class RomSetManagerChange
{
    public string SystemId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string NormalizedRom { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
}

public sealed class RomSetVariantDecision
{
    public string SystemId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string RegionProfile { get; set; } = string.Empty;
    public string LanguageProfile { get; set; } = string.Empty;
    public string SelectedRom { get; set; } = string.Empty;
    public string SelectedNormalizedRom { get; set; } = string.Empty;
    public int SelectedScore { get; set; }
    public int CandidateCount { get; set; }
    public List<string> SelectedReasons { get; set; } = new();
    public List<RomSetVariantCandidateReport> Candidates { get; set; } = new();
}

public sealed class RomSetVariantCandidateReport
{
    public string Rom { get; set; } = string.Empty;
    public string NormalizedRom { get; set; } = string.Empty;
    public int Score { get; set; }
    public bool Selected { get; set; }
    public List<string> Regions { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> Reasons { get; set; } = new();
}

public sealed class RomSetManagerOptionsSnapshot
{
    public bool Enabled { get; set; }
    public string GroupsRootPath { get; set; } = string.Empty;
    public bool NeverHideFavorites { get; set; } = true;
    public string Profile { get; set; } = "gamer";
    public string RetroAchievementsMode { get; set; } = "no_filter";
    public bool RetroAchievementsAlwaysShow { get; set; }
    public string LanguageMode { get; set; } = "auto";
    public string RegionMode { get; set; } = "auto";
    public string RomVersionMode { get; set; } = "stable";
    public bool OnlyRetroAchievements { get; set; } = false;
    public bool ShowOfficialGames { get; set; } = true;
    public bool ShowClones { get; set; } = true;
    public bool ShowPrototypes { get; set; } = true;
    public bool ShowDemos { get; set; }
    public bool ShowBetaAlpha { get; set; }
    public bool ShowLocationTests { get; set; }
    public bool ShowUsefulPatches { get; set; } = true;
    public bool ShowHacksMods { get; set; }
    public bool ShowCheatsTrainers { get; set; }
    public bool ShowBootlegsPirates { get; set; }
    public bool ShowUnlicensed { get; set; } = true;
    public bool ShowHomebrewsAftermarket { get; set; } = true;
    public bool ShowBootlegsAndHacks { get; set; } = true;
    public bool ShowAdult { get; set; } = true;
    public bool ShowCasino { get; set; } = true;
    public bool ShowMahjong { get; set; } = true;
    public bool ShowQuiz { get; set; } = true;
    public bool ShowNonGames { get; set; } = true;
    public bool ShowUnknownRoms { get; set; } = true;
    public bool ShowArcadeDiagnostics { get; set; }
    public bool ShowNonArcade { get; set; } = true;
    public bool ShowHorizontal { get; set; } = true;
    public bool ShowVertical { get; set; } = true;
    public string ScreenOrientation { get; set; } = "auto";
    public string CocktailGames { get; set; } = "auto";
    public string MultiScreenGames { get; set; } = "auto";
    public string FunctionalSecondScreen { get; set; } = "auto";
    public string WideSurroundDisplay { get; set; } = "auto";
    public string PortableLinkGameplay { get; set; } = "auto";
    public string CabinetControlsCompatibility { get; set; } = "auto";
    public string PlayerCount { get; set; } = "auto";
    public string ButtonCompatibility { get; set; } = "auto";
    public string VariantMode { get; set; } = "display_only";
    public string RegionProfile { get; set; } = "usa";
    public string LanguageProfile { get; set; } = "english";
    public string Translations { get; set; } = "prefer_if_language_match";
    public string ArcadeHandling { get; set; } = "parent_clone_group";
    public string OutputMode { get; set; } = "gamelist_hidden";
    public bool DebugReport { get; set; }
    public bool ReloadGamesAfterApply { get; set; } = true;
    public string ControlCabinetProfile { get; set; } = "generic_arcade";
    public int ControlPlayerCount { get; set; } = 2;
    public int ControlButtonsPerPlayer { get; set; } = 6;
    public bool ControlArcadeJoystick { get; set; } = true;
    public bool ControlAnalogJoystick { get; set; }
    public bool ControlRotaryJoystick { get; set; }
    public string ControlSpinner { get; set; } = "none";
    public string ControlTrackball { get; set; } = "none";
    public string ControlWheel { get; set; } = "none";
    public string ControlPedals { get; set; } = "none";
    public string ControlShifter { get; set; } = "none";
    public string ControlLightgun { get; set; } = "none";
    public string ControlDanceMat { get; set; } = "none";
    public string ControlGuitar { get; set; } = "none";
    public string ControlDrums { get; set; } = "none";
    public string ControlTurntable { get; set; } = "none";
    public bool ControlMicrophone { get; set; }
    public bool ControlKeyboard { get; set; }
    public bool ControlMouse { get; set; }
    public bool ControlTouchscreen { get; set; }
    public bool ControlMotionController { get; set; }
}

