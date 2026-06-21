namespace RetroBat.Domain.Interfaces;

public interface IRuntimeConfigSnapshotProvider<out TOptions>
{
    TOptions Current { get; }
}
