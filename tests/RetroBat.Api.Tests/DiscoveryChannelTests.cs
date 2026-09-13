using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le transport vers le wrapper libretro, verifie contre un faux emulateur : ce qui part sur
/// le fil est exactement ce que le wrapper sait lire.
///
/// MAME standalone n'a pas de canal (CDC A1.14) : un test le rappelle en bas de ce fichier.
/// </summary>
public class DiscoveryChannelTests
{
    private static ScoringDiscoveryOptions Options() => new()
    {
        WrapperControlPipeName = "RetroBatFramesControlPipe-test-" + Guid.NewGuid().ToString("N")[..8]
    };

    [Fact]
    public async Task Sans_emulateur_au_bout_du_tuyau_rien_ne_s_arme()
    {
        var options = Options();
        await using var channel = new WrapperCaptureChannel(options);
        channel.Listen();

        Assert.False(channel.IsAvailable);
        Assert.False(await channel.ArmAsync("tok", 3, default));
    }

    [Fact]
    public async Task Le_wrapper_recoit_l_ordre_d_armement_tel_qu_il_l_attend()
    {
        var options = Options();
        await using var channel = new WrapperCaptureChannel(options);
        channel.Listen();

        await using var wrapper = new NamedPipeClientStream(".", options.WrapperControlPipeName, PipeDirection.In);
        await wrapper.ConnectAsync(5000);
        using var reader = new StreamReader(wrapper, Encoding.UTF8);

        await WaitUntil(() => channel.IsAvailable);
        Assert.True(await channel.ArmAsync("tok1", 3, default));

        Assert.Equal("ARM|tok1|3", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Le_desarmement_passe_par_le_meme_tuyau()
    {
        var options = Options();
        await using var channel = new WrapperCaptureChannel(options);
        channel.Listen();

        await using var wrapper = new NamedPipeClientStream(".", options.WrapperControlPipeName, PipeDirection.In);
        await wrapper.ConnectAsync(5000);
        using var reader = new StreamReader(wrapper, Encoding.UTF8);
        await WaitUntil(() => channel.IsAvailable);

        await channel.DisarmAsync(default);

        Assert.Equal("DISARM", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Un_wrapper_parti_avec_son_jeu_ne_fait_pas_tomber_la_borne()
    {
        var options = Options();
        await using var channel = new WrapperCaptureChannel(options);
        channel.Listen();

        var wrapper = new NamedPipeClientStream(".", options.WrapperControlPipeName, PipeDirection.In);
        await wrapper.ConnectAsync(5000);
        await WaitUntil(() => channel.IsAvailable);
        await wrapper.DisposeAsync();

        // La premiere ecriture apres la fermeture peut encore reussir (le tampon du tuyau
        // l'absorbe) ; ce qui compte est qu'aucune exception ne remonte.
        await channel.ArmAsync("tok1", 3, default);
        await channel.ArmAsync("tok1", 3, default);
        await channel.DisarmAsync(default);
    }

    [Fact]
    public async Task Un_nom_de_tuyau_deja_pris_ne_fait_pas_tomber_la_borne()
    {
        var options = Options();
        await using var first = new WrapperCaptureChannel(options);
        first.Listen();

        await using var second = new WrapperCaptureChannel(options);
        second.Listen();   // le nom est pris : on n'attend rien d'autre que le silence

        Assert.False(second.IsAvailable);
        Assert.False(await second.ArmAsync("tok", 3, default));
    }

    [Fact]
    public void Aucun_canal_n_ecoute_pour_MAME_standalone()
    {
        // A1.14 : le Lua de MAME n'ouvre que des sockets, et ce serait le seul endroit du
        // dispositif ou la liste d'acces ne protegerait rien. Le jour ou quelqu'un rebranche
        // MAME, ce test tombe et l'oblige a relire la decision.
        var canaux = typeof(WrapperCaptureChannel).Assembly.GetTypes()
            .Where(t => typeof(IEmulatorCaptureChannel).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Select(t => t.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(WrapperCaptureChannel) }, canaux);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "condition jamais atteinte");
    }
}
