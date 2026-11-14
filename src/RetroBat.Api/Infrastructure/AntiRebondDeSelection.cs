namespace RetroBat.Api.Infrastructure;

/// <summary>
/// L'anti-rebond d'une selection dans ES (lot 4 bis C-7, 14/11) : chaque selection prend un ticket ; apres son attente, seule
/// la derniere selection agit. Un lancement presse les selections qui attendent : elles agissent tout de suite.
/// </summary>
public sealed class AntiRebondDeSelection
{
    private readonly object _verrou = new();
    private long _dernier;
    private TaskCompletionSource _presse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Une nouvelle selection : son ticket. Les selections d'avant ne sont plus les dernieres.</summary>
    public long Prendre()
    {
        lock (_verrou) return ++_dernier;
    }

    /// <summary>Ce ticket est-il celui de la derniere selection ?</summary>
    public bool ToujoursLeDernier(long ticket)
    {
        lock (_verrou) return ticket == _dernier;
    }

    /// <summary>Attend <paramref name="delai"/>, ou moins si un lancement presse les selections.</summary>
    public Task AttendreAsync(TimeSpan delai, CancellationToken ct)
    {
        Task presse;
        lock (_verrou) presse = _presse.Task;
        return Task.WhenAny(Task.Delay(delai, ct), presse);
    }

    /// <summary>Un lancement : les selections qui attendent agissent tout de suite.</summary>
    public void Presser()
    {
        TaskCompletionSource ancien;
        lock (_verrou)
        {
            ancien = _presse;
            _presse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        ancien.TrySetResult();
    }
}
