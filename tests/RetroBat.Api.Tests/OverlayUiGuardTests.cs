using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Une surimpression qui echoue ne doit jamais faire tomber l'API (2026-09-29 : six plantages en une
/// heure chez un joueur, quota d'objets fenetre plein). Le fil de fenetre avale l'exception et libere
/// quand meme celui qui attend la fenetre.
/// </summary>
public class OverlayUiGuardTests
{
    [Fact]
    public void Une_exception_du_fil_ne_sort_pas_et_libere_l_attente()
    {
        var pret = new ManualResetEventSlim();
        var fil = new Thread(() => OverlayUiGuard.Run("test", pret, () =>
            throw new System.ComponentModel.Win32Exception(1158)));
        fil.Start();
        Assert.True(pret.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(fil.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Un_signal_deja_libere_ne_fait_pas_echouer_le_garde()
    {
        var pret = new ManualResetEventSlim();
        pret.Dispose();
        OverlayUiGuard.Run("test", pret, () => throw new InvalidOperationException("handle"));
    }

    [Fact]
    public void Les_ressources_du_processus_se_lisent()
    {
        var (user, gdi) = GuiResources.Lire();
        Assert.True(user >= 0);
        Assert.True(gdi >= 0);
    }
}
