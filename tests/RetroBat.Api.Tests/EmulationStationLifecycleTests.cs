using System.ComponentModel;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Fermer EmulationStation pouvait éteindre l'API. Le moniteur de cycle de vie trie les
/// processus par date de démarrage, et lire celle d'un EmulationStation qui vient de se fermer
/// lève « Accès refusé ». L'exception partait du comparateur de tri, hors de tout try, et
/// l'hôte est configuré pour s'arrêter quand un service de fond en laisse échapper une : trois
/// morts de l'API sans trace (2026-09-17 23:44, 2026-09-19 07:30 et 09:29).
/// </summary>
public class EmulationStationLifecycleTests
{
    [Fact]
    public void DemarrageOuMinimum_RendLaDateQuandElleSeLit()
    {
        var quand = new DateTime(2026, 9, 19, 9, 17, 57, DateTimeKind.Local);

        Assert.Equal(quand, EmulationStationLifecycleHostedService.DemarrageOuMinimum(() => quand));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]            // « Accès refusé » sur un processus qui se ferme
    [InlineData(typeof(InvalidOperationException))] // le processus a déjà disparu
    [InlineData(typeof(NotSupportedException))]     // processus distant
    public void DemarrageOuMinimum_UnProcessusIllisiblePasseEnDernier(Type type)
    {
        Func<DateTime> lecture = () => throw (Exception)Activator.CreateInstance(type)!;

        // Le minimum : trié par date décroissante, il passe derrière tous les autres, et
        // surtout la lecture ne remonte rien à l'hôte.
        Assert.Equal(DateTime.MinValue, EmulationStationLifecycleHostedService.DemarrageOuMinimum(lecture));
    }

    [Fact]
    public void DemarrageOuMinimum_UneAutreExceptionRemonte()
    {
        // La ceinture ne couvre que ce qu'une lecture de processus peut lever : une panne d'une
        // autre nature doit rester visible plutôt que de se déguiser en processus ancien.
        Assert.Throws<OutOfMemoryException>(
            () => EmulationStationLifecycleHostedService.DemarrageOuMinimum(() => throw new OutOfMemoryException()));
    }
}
