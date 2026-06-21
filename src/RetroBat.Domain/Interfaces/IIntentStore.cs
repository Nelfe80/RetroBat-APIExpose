namespace RetroBat.Domain.Interfaces;

public interface IIntentStore
{
    void PushIntent(object intent);
    object? PopIntent();
}
