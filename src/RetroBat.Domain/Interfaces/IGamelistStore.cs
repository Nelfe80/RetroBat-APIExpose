using System.Xml.Linq;

namespace RetroBat.Domain.Interfaces;

public interface IGamelistStore
{
    object GetLock(string gamelistPath);

    XDocument? Load(string gamelistPath, LoadOptions loadOptions, CancellationToken cancellationToken = default);

    bool SaveIfChanged(
        string gamelistPath,
        XDocument document,
        Func<XDocument, string, bool> matchesExistingFile,
        Action<XDocument, string, CancellationToken> save,
        CancellationToken cancellationToken = default);
}
