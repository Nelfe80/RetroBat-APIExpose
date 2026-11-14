using System.Collections.Concurrent;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Le bus d'evenements interne de l'API.
///
/// (brief du 13/11, ROB-01) Deux protections :
/// - chaque abonne est appele dans son propre <c>try</c> : un abonne qui leve une exception ne prive plus les suivants de
///   l'evenement, et l'exception ne remonte plus chez l'editeur (fil de lecture du jeu, tube du wrapper, fil du panneau) ;
/// - la liste des abonnes d'un type est un tableau immuable, remplace sous verrou a chaque abonnement ou desabonnement :
///   une publication parcourt un instantane coherent, et deux abonnements simultanes ne se perdent plus.
///
/// Les abonnes restent appeles en synchrone, dans l'ordre d'abonnement, sur le fil de l'editeur.
/// </summary>
public class SimpleEventBus : IEventBus
{
    private static readonly TimeSpan IntervalleDuJournal = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Type, object[]> _handlers = new();
    private readonly object _verrou = new();
    // Une panne d'abonne journalisee une fois par minute et par abonne : un abonne qui casse a chaque image ne noie pas le journal.
    private readonly ConcurrentDictionary<string, DateTime> _derniereErreur = new();

    /// <summary>Journal des pannes d'abonnes. Le bus est cree avant le journal : il est pose au demarrage.</summary>
    public ILogger? Journal { get; set; }

    /// <summary>Pannes d'abonnes depuis le demarrage (tests, diagnostic).</summary>
    public long Pannes => Interlocked.Read(ref _pannes);
    private long _pannes;

    public Task PublishAsync<TEvent>(TEvent @event) where TEvent : class
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            return Task.CompletedTask;
        }

        // Tableau jamais modifie en place : pas de copie a faire, pas de verrou a prendre.
        foreach (var handler in handlers)
        {
            if (handler is not Action<TEvent> typedHandler)
            {
                continue;
            }

            try
            {
                typedHandler(@event);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _pannes);
                Noter(typedHandler, typeof(TEvent), @event, ex);
            }
        }

        return Task.CompletedTask;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var type = typeof(TEvent);
        lock (_verrou)
        {
            var actuels = _handlers.TryGetValue(type, out var liste) ? liste : Array.Empty<object>();
            var nouveaux = new object[actuels.Length + 1];
            Array.Copy(actuels, nouveaux, actuels.Length);
            nouveaux[^1] = handler;
            _handlers[type] = nouveaux;
        }

        return new Subscription(() => Retirer(type, handler));
    }

    private void Retirer(Type type, object handler)
    {
        lock (_verrou)
        {
            if (!_handlers.TryGetValue(type, out var actuels))
            {
                return;
            }

            // Le meme delegue peut etre abonne deux fois : un desabonnement n'en retire qu'un, comme List.Remove.
            var index = Array.IndexOf(actuels, handler);
            if (index < 0)
            {
                return;
            }

            var nouveaux = new object[actuels.Length - 1];
            Array.Copy(actuels, 0, nouveaux, 0, index);
            Array.Copy(actuels, index + 1, nouveaux, index, actuels.Length - index - 1);
            _handlers[type] = nouveaux;
        }
    }

    private void Noter(Delegate handler, Type typeDEvenement, object @event, Exception ex)
    {
        var journal = Journal;
        if (journal is null)
        {
            return;
        }

        var abonne = (handler.Method.DeclaringType?.FullName ?? "?") + "." + handler.Method.Name;
        var maintenant = DateTime.UtcNow;
        var cle = typeDEvenement.Name + "|" + abonne;
        if (_derniereErreur.TryGetValue(cle, out var derniere) && maintenant - derniere < IntervalleDuJournal)
        {
            return;
        }

        _derniereErreur[cle] = maintenant;
        var nature = @event is RetroBat.Domain.Events.EventEnvelope enveloppe ? enveloppe.Type : typeDEvenement.Name;
        journal.LogWarning(ex, "Bus : l'abonne {Abonne} a echoue sur {Evenement} ; les autres abonnes l'ont recu.", abonne, nature);
    }

    private class Subscription : IDisposable
    {
        private Action? _unsubscribe;
        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
