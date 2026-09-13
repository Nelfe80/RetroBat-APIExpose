using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les deux transports, verifies contre un faux emulateur : ce qui part sur le fil est
/// exactement ce que le wrapper et le plugin Lua savent lire.
/// </summary>
public class DiscoveryChannelTests
{
    private static ScoringDiscoveryOptions Options(string? pipe = null, int? controlPort = null, int? framesPort = null) => new()
    {
        WrapperControlPipeName = pipe ?? "RetroBatFramesControlPipe-test-" + Guid.NewGuid().ToString("N")[..8],
        MameControlPort = controlPort ?? FreePort(),
        MameFramesPort = framesPort ?? 12349
    };

    private static int FreePort()
    {
        using var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ── Le tuyau nomme, cote wrapper libretro ─────────────────────────────────

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

    // ── Le socket, cote plugin Lua de MAME ────────────────────────────────────

    [Fact]
    public async Task Sans_plugin_connecte_rien_ne_s_arme()
    {
        await using var channel = new MameCaptureChannel(Options());
        channel.Listen();

        Assert.False(channel.IsAvailable);
        Assert.False(await channel.ArmAsync("tok", 3, default));
    }

    [Fact]
    public async Task Le_plugin_recoit_l_adresse_du_verificateur_dans_l_ordre_d_armement()
    {
        // C'est le plugin qui ouvre la connexion sortante vers le verificateur : APIExpose ne
        // relaie aucun octet d'image, il dit seulement ou les porter.
        var options = Options(framesPort: 12349);
        await using var channel = new MameCaptureChannel(options);
        channel.Listen();

        using var plugin = new TcpClient();
        await plugin.ConnectAsync(System.Net.IPAddress.Loopback, options.MameControlPort);
        using var reader = new StreamReader(plugin.GetStream(), Encoding.UTF8);
        await WaitUntil(() => channel.IsAvailable);

        Assert.True(await channel.ArmAsync("tok1", 3, default));

        Assert.Equal("ARM|127.0.0.1|12349|tok1|3", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Un_second_jeu_remplace_le_plugin_du_precedent()
    {
        var options = Options();
        await using var channel = new MameCaptureChannel(options);
        channel.Listen();

        using var first = new TcpClient();
        await first.ConnectAsync(System.Net.IPAddress.Loopback, options.MameControlPort);
        await WaitUntil(() => channel.IsAvailable);

        using var second = new TcpClient();
        await second.ConnectAsync(System.Net.IPAddress.Loopback, options.MameControlPort);
        using var reader = new StreamReader(second.GetStream(), Encoding.UTF8);
        await WaitUntil(() => channel.IsAvailable);

        await channel.ArmAsync("tok2", 2, default);

        // L'ordre part vers le jeu en cours, pas vers celui d'avant.
        Assert.Contains("tok2", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Un_port_deja_pris_ne_fait_pas_tomber_la_borne()
    {
        var options = Options();
        await using var first = new MameCaptureChannel(options);
        first.Listen();

        await using var second = new MameCaptureChannel(options);
        second.Listen();

        Assert.False(second.IsAvailable);
        Assert.False(await second.ArmAsync("tok", 3, default));
    }

    [Fact]
    public void Le_port_de_commande_et_celui_des_images_sont_distincts_du_pont_RAM()
    {
        var options = new ScoringDiscoveryOptions();

        Assert.NotEqual(12347, options.MameControlPort);
        Assert.NotEqual(12347, options.MameFramesPort);
        Assert.NotEqual(options.MameControlPort, options.MameFramesPort);
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
