using System.Collections.Concurrent;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public class SimpleEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();

    public Task PublishAsync<TEvent>(TEvent @event) where TEvent : class
    {
        if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            foreach (var handler in handlers.ToList())
            {
                if (handler is Action<TEvent> typedHandler)
                {
                    typedHandler(@event);
                }
            }
        }
        return Task.CompletedTask;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        var type = typeof(TEvent);
        _handlers.AddOrUpdate(type, new List<object> { handler }, (_, list) =>
        {
            list.Add(handler);
            return list;
        });

        return new Subscription(() =>
        {
            if (_handlers.TryGetValue(type, out var list))
            {
                list.Remove(handler);
            }
        });
    }

    private class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}
