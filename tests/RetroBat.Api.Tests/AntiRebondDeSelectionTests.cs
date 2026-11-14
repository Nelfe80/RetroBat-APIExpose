using System.Diagnostics;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-7 (14/11) : <c>agent/scores/profile</c> relu a chaque jeu effleure dans ES. Seule la derniere selection,
/// 400 ms plus tard, demande le profil ; un lancement n'attend pas.
/// </summary>
public sealed class AntiRebondDeSelectionTests
{
    [Fact]
    public void Seule_la_derniere_selection_agit()
    {
        var rebond = new AntiRebondDeSelection();
        var a = rebond.Prendre();
        var b = rebond.Prendre();

        Assert.False(rebond.ToujoursLeDernier(a));
        Assert.True(rebond.ToujoursLeDernier(b));
    }

    [Fact]
    public async Task L_attente_dure_l_anti_rebond()
    {
        var rebond = new AntiRebondDeSelection();
        var chrono = Stopwatch.StartNew();

        await rebond.AttendreAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.True(chrono.ElapsedMilliseconds >= 120);
    }

    [Fact]
    public async Task Un_lancement_presse_la_selection_qui_attend()
    {
        var rebond = new AntiRebondDeSelection();
        var ticket = rebond.Prendre();
        var attente = rebond.AttendreAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        rebond.Presser();

        Assert.Same(attente, await Task.WhenAny(attente, Task.Delay(TimeSpan.FromSeconds(5))));
        // Le lancement ne prend pas de ticket : la selection reste la derniere et se prepare.
        Assert.True(rebond.ToujoursLeDernier(ticket));
    }
}
