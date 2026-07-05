namespace RetroBat.Domain.Interfaces;

public interface IEsSettingsChangeBus
{
    IDisposable Subscribe(Func<EsSettingsChangedEvent, CancellationToken, Task> handler);
}

public sealed record EsSettingsChangedEvent(
    string Path,
    DateTimeOffset ObservedAt);
