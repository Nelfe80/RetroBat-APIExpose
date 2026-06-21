using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using System.Xml.Linq;

namespace RetroBat.Api.Infrastructure;

public class MameStartupConfigHostedService : IHostedService
{
    private readonly ILogger<MameStartupConfigHostedService> _logger;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly IEsSettingsStore _settingsStore;

    public MameStartupConfigHostedService(
        ILogger<MameStartupConfigHostedService> logger,
        ApiExposeRuntimeOptionsService runtimeOptions,
        IEsSettingsStore settingsStore)
    {
        _logger = logger;
        _runtimeOptions = runtimeOptions;
        _settingsStore = settingsStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtimeOptions.IsMameOutputsEnabled())
        {
            EnsureSetting(RetroBatPaths.MameIniPath, "output", "network");
            EnsureSetting(RetroBatPaths.EmulatorMameIniPath, "output", "network");
            EnsureEmulationStationStringSetting("mame.mame_output", "network");
        }

        EnsureSetting(RetroBatPaths.MamePluginIniPath, "hiscore", "1");
        EnsureSetting(RetroBatPaths.BiosMamePluginIniPath, "hiscore", "1");
        EnsureHiscorePluginConfig(RetroBatPaths.MameHiscorePluginConfigPath);
        EnsureEmulationStationStringSetting("mame.mame_hiscore", "1");
        EnsureEmulationStationBoolSetting("PublicWebAccess", true);
        if (_runtimeOptions.IsRetroArchWrapperEnabled())
        {
            EnsureQuotedSetting(RetroBatPaths.RetroArchConfigPath, "network_cmd_enable", "true");
            EnsureQuotedSetting(RetroBatPaths.RetroArchConfigPath, "network_cmd_port", "55355");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void EnsureSetting(string path, string key, string value)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Startup config file not found: {Path}", path);
                return;
            }

            var lines = File.ReadAllLines(path).ToList();
            var normalizedPrefix = key + " ";
            var formattedLine = $"{key,-25}{value}";
            var found = false;
            var changed = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                if (!string.Equals(lines[i].Trim(), formattedLine.Trim(), StringComparison.Ordinal))
                {
                    lines[i] = formattedLine;
                    changed = true;
                }

                break;
            }

            if (!found)
            {
                lines.Add(string.Empty);
                lines.Add(formattedLine);
                changed = true;
            }

            if (changed)
            {
                File.WriteAllLines(path, lines);
                _logger.LogInformation("Updated startup config: {Path} => {Key}={Value}", path, key, value);
            }
            else
            {
                _logger.LogInformation("Startup config already OK: {Path} => {Key}={Value}", path, key, value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce startup ES config {Key}={Value}", key, value);
        }
    }

    private void EnsureEmulationStationStringSetting(string key, string value)
        => EnsureEmulationStationSetting("string", key, value);

    private void EnsureEmulationStationBoolSetting(string key, bool value)
        => EnsureEmulationStationSetting("bool", key, value ? "true" : "false");

    private void EnsureEmulationStationSetting(string elementName, string key, string value)
    {
        try
        {
            var changed = _settingsStore.Update(document =>
            {
                var root = document.Root ?? throw new InvalidOperationException("es_settings.cfg root is missing.");
                var existing = root.Elements()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    root.Add(new XText(Environment.NewLine + "  "));
                    root.Add(new XElement(elementName, new XAttribute("name", key), new XAttribute("value", value)));
                    return true;
                }

                if (string.Equals(existing.Attribute("value")?.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }

                existing.SetAttributeValue("value", value);
                return true;
            });

            if (changed)
            {
                _logger.LogInformation("Updated startup ES config: {Key}={Value}", key, value);
            }
            else
            {
                _logger.LogInformation("Startup ES config already OK: {Key}={Value}", key, value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce startup ES config {Key}={Value}", key, value);
        }
    }

    private void EnsureHiscorePluginConfig(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var desired = "{\r\n  \"only_save_at_exit\": false\r\n}\r\n";
            if (File.Exists(path))
            {
                var current = File.ReadAllText(path);
                if (string.Equals(current.Replace("\r\n", "\n"), desired.Replace("\r\n", "\n"), StringComparison.Ordinal))
                {
                    _logger.LogInformation("Startup config already OK: {Path} => only_save_at_exit=false", path);
                    return;
                }
            }

            File.WriteAllText(path, desired);
            _logger.LogInformation("Updated startup config: {Path} => only_save_at_exit=false", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce hiscore plugin config in {Path}", path);
        }
    }

    private void EnsureQuotedSetting(string path, string key, string value)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Startup config file not found: {Path}", path);
                return;
            }

            var lines = File.ReadAllLines(path).ToList();
            var formattedLine = $"{key} = \"{value}\"";
            var found = false;
            var changed = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                if (!string.Equals(lines[i].Trim(), formattedLine, StringComparison.Ordinal))
                {
                    lines[i] = formattedLine;
                    changed = true;
                }

                break;
            }

            if (!found)
            {
                lines.Add(formattedLine);
                changed = true;
            }

            if (changed)
            {
                File.WriteAllLines(path, lines);
                _logger.LogInformation("Updated startup config: {Path} => {Key}=\"{Value}\"", path, key, value);
            }
            else
            {
                _logger.LogInformation("Startup config already OK: {Path} => {Key}=\"{Value}\"", path, key, value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce startup config {Key}=\"{Value}\" in {Path}", key, value, path);
        }
    }
}
