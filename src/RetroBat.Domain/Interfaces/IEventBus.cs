namespace RetroBat.Domain.Interfaces;

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event) where TEvent : class;
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
}
