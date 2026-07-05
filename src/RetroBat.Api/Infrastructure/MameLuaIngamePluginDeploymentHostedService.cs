using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class MameLuaIngamePluginDeploymentHostedService : IHostedService
{
    private const string PluginName = "apiexpose_ingame";

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<MameLuaIngamePluginDeploymentHostedService> _logger;

    public MameLuaIngamePluginDeploymentHostedService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<MameLuaIngamePluginDeploymentHostedService> logger)
    {
        _options = options;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.GameEventsManager;
        if (!_runtimeOptions.IsMameLuaIngameEnabled() ||
            !options.MameLuaIngamePluginDeploymentEnabled)
        {
            return Task.CompletedTask;
        }

        try
        {
            var source = Path.Combine(RetroBatPaths.RamToolsRoot, "mame_apiexpose_ingame");
            var biosTarget = Path.Combine(RetroBatPaths.BiosMamePluginsRoot, PluginName);
            DeployDirectory(source, biosTarget);
            WriteConfig(biosTarget, options.MameLuaIngamePort);
            EnsurePluginEnabled(RetroBatPaths.BiosMamePluginIniPath);

            if (options.MameLuaIngameMirrorToEmulatorPlugins)
            {
                var emulatorTarget = Path.Combine(RetroBatPaths.EmulatorMamePluginsRoot, PluginName);
                DeployDirectory(source, emulatorTarget);
                WriteConfig(emulatorTarget, options.MameLuaIngamePort);
                EnsurePluginEnabled(RetroBatPaths.MamePluginIniPath);
            }

            _logger.LogInformation("MAME Lua ingame plugin deployed and enabled as {PluginName}", PluginName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to deploy MAME Lua ingame plugin.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void DeployDirectory(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relative);
            var targetParent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static void WriteConfig(string targetDirectory, int port)
    {
        var effectivePort = port is > 0 and < 65536 ? port : 12347;
        var path = Path.Combine(targetDirectory, "config.lua");
        var lines = new[]
        {
            "return {",
            "    host = \"127.0.0.1\",",
            $"    port = {effectivePort},",
            "    poll_frames = 1,",
            "    reconnect_frames = 60,",
            "}"
        };

        File.WriteAllLines(path, lines);
    }

    private static void EnsurePluginEnabled(string pluginIniPath)
    {
        var directory = Path.GetDirectoryName(pluginIniPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = File.Exists(pluginIniPath)
            ? File.ReadAllLines(pluginIniPath).ToList()
            : new List<string>
            {
                "#",
                "# PLUGINS OPTIONS",
                "#"
            };

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var key = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(key, PluginName, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{PluginName,-25} 1";
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            lines.Add($"{PluginName,-25} 1");
        }

        File.WriteAllLines(pluginIniPath, lines);
    }
}
