using System.Collections.Concurrent;
using System.Xml.Linq;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Media;

public sealed class GamelistStore : IGamelistStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    public object GetLock(string gamelistPath)
    {
        return Locks.GetOrAdd(NormalizePath(gamelistPath), _ => new object());
    }

    public XDocument? Load(string gamelistPath, LoadOptions loadOptions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(gamelistPath) || !File.Exists(gamelistPath))
        {
            return null;
        }

        using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return XDocument.Load(stream, loadOptions);
    }

    public bool SaveIfChanged(
        string gamelistPath,
        XDocument document,
        Func<XDocument, string, bool> matchesExistingFile,
        Action<XDocument, string, CancellationToken> save,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(matchesExistingFile);
        ArgumentNullException.ThrowIfNull(save);

        lock (GetLock(gamelistPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchesExistingFile(document, gamelistPath))
            {
                return false;
            }

            save(document, gamelistPath, cancellationToken);
            return true;
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
