using System.Text;
using System.Xml;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Providers.EmulationStation;

public class EmulationStationHiscoreThemeWriter : IHiscoreThemeWriter
{
    public async Task WriteAsync(GameReference game, HiscoreExtractionResult result, CancellationToken cancellationToken = default)
    {
        var systemId = string.IsNullOrWhiteSpace(game.SystemId) ? "unknown" : game.SystemId;
        var gameFileName = result.RomName;
        if (string.IsNullOrWhiteSpace(gameFileName))
        {
            gameFileName = Path.GetFileNameWithoutExtension(game.GamePath);
        }

        if (string.IsNullOrWhiteSpace(gameFileName))
        {
            gameFileName = "unknown";
        }

        var targetDir = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".hiscore", systemId);
        var targetPath = Path.Combine(targetDir, gameFileName + ".xml");
        var archiveDir = Path.Combine(RetroBatPaths.ThemeHiscoreResourcesRoot, systemId);
        var archivePath = Path.Combine(archiveDir, gameFileName + ".xml");

        if (result.Scores.Count == 0)
        {
            await DeleteIfExistsAsync(targetPath, cancellationToken);
            await DeleteIfExistsAsync(archivePath, cancellationToken);
            return;
        }

        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(archiveDir);

        var tempPath = targetPath + ".tmp";
        var archiveTempPath = archivePath + ".tmp";
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = Environment.NewLine,
            NewLineHandling = NewLineHandling.Replace,
            Async = false
        };

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("theme");
            writer.WriteWhitespace(Environment.NewLine + Environment.NewLine);

            writer.WriteStartElement("view");
            writer.WriteAttributeString("name", "gamecarousel, detailed, grid");

            for (var i = 0; i < result.Scores.Count; i++)
            {
                var score = result.Scores[i];
                writer.WriteWhitespace(Environment.NewLine + "\t\t");
                writer.WriteStartElement("text");
                writer.WriteAttributeString("name", $"hiscoreline{i + 1}");
                writer.WriteAttributeString("extra", "true");

                writer.WriteWhitespace(Environment.NewLine + "\t\t\t");
                writer.WriteStartElement("text");
                writer.WriteString($"#{score.Rank} {score.Name} {score.Score}");
                writer.WriteEndElement();

                writer.WriteWhitespace(Environment.NewLine + "\t\t");
                writer.WriteEndElement();
            }

            writer.WriteWhitespace(Environment.NewLine + "\t");
            writer.WriteEndElement();
            writer.WriteWhitespace(Environment.NewLine);
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        await ReplaceWithRetryAsync(tempPath, targetPath, cancellationToken);
        File.Copy(targetPath, archiveTempPath, true);
        await ReplaceWithRetryAsync(archiveTempPath, archivePath, cancellationToken);

        return;
    }

    private static async Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                var tempPath = path + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.SetAttributes(tempPath, FileAttributes.Normal);
                    File.Delete(tempPath);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        var lingeringTempPath = path + ".tmp";
        if (File.Exists(lingeringTempPath))
        {
            File.SetAttributes(lingeringTempPath, FileAttributes.Normal);
            File.Delete(lingeringTempPath);
        }
    }

    private static async Task ReplaceWithRetryAsync(string tempPath, string targetPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(targetPath))
                {
                    File.SetAttributes(targetPath, FileAttributes.Normal);
                    File.Copy(tempPath, targetPath, true);
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        if (File.Exists(targetPath))
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
            File.Copy(tempPath, targetPath, true);
            File.Delete(tempPath);
        }
        else
        {
            File.Move(tempPath, targetPath);
        }
    }
}
