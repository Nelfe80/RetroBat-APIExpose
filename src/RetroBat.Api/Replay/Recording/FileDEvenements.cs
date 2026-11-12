using System.Diagnostics;
using System.Threading.Channels;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Recording;

/// <summary>
/// LES ANNONCES DE L'ENREGISTREUR PARTENT EN FILE (12/11). Le bus appelle ses abonnes sur le fil de celui qui publie : au
/// banc de 18:17, « replay.recording.stopped », publie par la relance d'un defi pendant qu'elle tenait le tour de
/// l'enregistreur, est reste 34 s chez un abonne ; la relance suivante n'a rien pu ouvrir, une tentative entiere est partie
/// sans replay. Les annonces passent maintenant par une file a un seul lecteur : l'ordre est garde (l'arret d'un replay
/// avant le demarrage du suivant), et un abonne lent ne retient que la file, jamais la relance.
/// </summary>
internal sealed class FileDEvenements
{
    /// <summary>Au-dela, un abonne qui retient la file est signale au journal.</summary>
    internal static readonly TimeSpan SeuilDeLenteur = TimeSpan.FromMilliseconds(500);

    private readonly Channel<EventEnvelope> _file = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IEventBus _bus;
    private readonly ILogger? _journal;
    private readonly Task _lecteur;

    public FileDEvenements(IEventBus bus, ILogger? journal)
    {
        _bus = bus;
        _journal = journal;
        _lecteur = Task.Run(LireAsync);
    }

    /// <summary>Met l'annonce en file et rend la main tout de suite.</summary>
    public void Publier(string type, object payload)
    {
        if (!_file.Writer.TryWrite(new EventEnvelope { Type = type, Payload = payload }))
            _journal?.LogDebug("Replay : file des annonces fermee, {Type} perdu.", type);
    }

    /// <summary>A l'arret : ferme la file et laisse partir ce qui y reste, au plus <paramref name="delai"/>.</summary>
    public async Task ViderAsync(TimeSpan delai)
    {
        _file.Writer.TryComplete();
        await Task.WhenAny(_lecteur, Task.Delay(delai)).ConfigureAwait(false);
    }

    private async Task LireAsync()
    {
        await foreach (var e in _file.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var chrono = Stopwatch.StartNew();
            try { await _bus.PublishAsync(e).ConfigureAwait(false); }
            catch (Exception ex) { _journal?.LogDebug(ex, "Replay : publication event {Type} échouée", e.Type); }
            if (chrono.Elapsed > SeuilDeLenteur)
                _journal?.LogWarning("Replay : l'annonce {Type} a retenu ses abonnes {Ms} ms (la relance, elle, n'attend plus).",
                    e.Type, (long)chrono.Elapsed.TotalMilliseconds);
        }
    }
}
