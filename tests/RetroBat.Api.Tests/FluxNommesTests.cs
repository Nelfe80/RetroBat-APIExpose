using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using RetroBat.Api.Hubs;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les flux nommes des plugins (brief du 13/11, lot 2). API-2 : scoring.* et multiplayer.* ne sortaient que sur /ws ;
/// LedManager, qui n'ouvre que des flux nommes, ne les voyait jamais.
/// </summary>
public sealed class FluxNommesTests
{
    [Fact]
    public void Scoring_et_multiplayer_ont_leur_flux()
    {
        Assert.Equal(new[] { "scoring." }, WebSocketConnectionManager.StreamPrefixes["scoring"]);
        Assert.Contains("multiplayer.", WebSocketConnectionManager.StreamPrefixes["multiplayer"]);
        Assert.Contains("netplay.", WebSocketConnectionManager.StreamPrefixes["multiplayer"]);
        Assert.Equal(new[] { "multiplayer.state" }, WebSocketConnectionManager.RetainedTypesByStream["multiplayer"]);
    }

    [Fact]
    public async Task Un_verdict_part_sur_scoring_et_pas_ailleurs()
    {
        var ws = new WebSocketConnectionManager(new OptionsFixes());
        var scoring = new SocketDEssai();
        var multi = new SocketDEssai();
        var global = new SocketDEssai();
        var frontend = new SocketDEssai();
        await ws.AddSocketAsync(scoring, "scoring");
        await ws.AddSocketAsync(multi, "multiplayer");
        await ws.AddSocketAsync(global, "");
        await ws.AddSocketAsync(frontend, "frontend");

        await ws.BroadcastAsync(new EventEnvelope { Type = "scoring.verdict", Payload = new { Status = "published" } });
        await ws.BroadcastAsync(new EventEnvelope { Type = "multiplayer.match.started", Payload = new { Room = "r1" } });

        Assert.Contains("scoring.verdict", Assert.Single(scoring.Envoyes));
        Assert.Contains("multiplayer.match.started", Assert.Single(multi.Envoyes));
        Assert.Equal(2, global.Envoyes.Count);   // le flux global, inchange
        Assert.Empty(frontend.Envoyes);
    }

    [Fact]
    public async Task L_etat_des_gamerooms_est_rejoue_a_la_connexion()
    {
        var ws = new WebSocketConnectionManager(new OptionsFixes());
        await ws.BroadcastAsync(new EventEnvelope { Type = "multiplayer.state", Payload = new { rooms = new[] { new { room = "r1" } } } });
        await ws.BroadcastAsync(new EventEnvelope { Type = "multiplayer.match.started", Payload = new { Room = "r1" } });

        var multi = new SocketDEssai();
        await ws.AddSocketAsync(multi, "multiplayer");
        var rejoue = Assert.Single(multi.Envoyes);
        Assert.Contains("multiplayer.state", rejoue);
        Assert.Contains("r1", rejoue);

        var scoring = new SocketDEssai();
        await ws.AddSocketAsync(scoring, "scoring");
        Assert.Empty(scoring.Envoyes);
    }

    internal sealed class OptionsFixes : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = new();
        public ApiExposeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ApiExposeOptions, string?> listener) => null;
    }

    internal sealed class SocketDEssai : WebSocket
    {
        public List<string> Envoyes { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (Envoyes) Envoyes.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// API-3 (14/11) : un plugin qui se (re)connecte recoit le dernier instantane de sa surface et la derniere selection d'ES,
/// au lieu d'attendre la selection suivante.
/// </summary>
public sealed class RejeuDesSurfacesTests
{
    private static async Task<List<string>> Connecter(WebSocketConnectionManager ws, string flux)
    {
        // les selections et les instantanes partent apres une courte fenetre de coalescence (20 a 35 ms) : on la laisse passer
        await Task.Delay(150);
        var socket = new FluxNommesTests.SocketDEssai();
        await ws.AddSocketAsync(socket, flux);
        return socket.Envoyes;
    }

    private static EventEnvelope Instantane(string type, long sequence, string rom)
        => new() { Type = type, Payload = new { Sequence = sequence, Rom = rom } };

    [Fact]
    public async Task Chaque_surface_rejoue_son_dernier_instantane()
    {
        var ws = new WebSocketConnectionManager(new FluxNommesTests.OptionsFixes());
        Assert.Empty(await Connecter(ws, "marquee"));   // rien de garde : rien d'envoye

        await ws.BroadcastAsync(Instantane("marquee.snapshot", 4, "sf2"));
        await ws.BroadcastAsync(Instantane("topper.snapshot", 4, "sf2"));
        await ws.BroadcastAsync(Instantane("instruction-card.snapshot", 4, "sf2"));
        await ws.BroadcastAsync(Instantane("marquee.snapshot.updated", 4, "sf2-genere"));

        Assert.Contains("sf2-genere", Assert.Single(await Connecter(ws, "marquee")));
        Assert.Contains("topper.snapshot", Assert.Single(await Connecter(ws, "topper")));
        Assert.Contains("instruction-card.snapshot", Assert.Single(await Connecter(ws, "instruction-card")));
        Assert.Empty(await Connecter(ws, "screen"));
    }

    [Fact]
    public async Task Un_instantane_plus_ancien_ne_remplace_pas_le_garde()
    {
        var ws = new WebSocketConnectionManager(new FluxNommesTests.OptionsFixes());
        await ws.BroadcastAsync(Instantane("marquee.snapshot", 9, "neuf"));
        await ws.BroadcastAsync(Instantane("marquee.snapshot", 7, "vieux"));
        Assert.Contains("neuf", Assert.Single(await Connecter(ws, "marquee")));
    }

    [Fact]
    public async Task Le_flux_frontend_rejoue_la_derniere_selection_et_l_oublie_quand_ES_s_arrete()
    {
        var ws = new WebSocketConnectionManager(new FluxNommesTests.OptionsFixes());
        await ws.BroadcastAsync(new EventEnvelope { Type = "ui.game.selected.raw", Payload = new { Rom = "brute" } });
        Assert.Contains("brute", Assert.Single(await Connecter(ws, "frontend")));

        await ws.BroadcastAsync(new EventEnvelope { Type = "ui.game.selected", Payload = new { Rom = "enrichie" } });
        Assert.Contains("enrichie", Assert.Single(await Connecter(ws, "frontend")));

        // une partie lancee ne se rejoue pas (effets de depart des plugins) : seule la selection repart
        await ws.BroadcastAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { Rom = "lance" } });
        Assert.Contains("enrichie", Assert.Single(await Connecter(ws, "frontend")));

        await ws.BroadcastAsync(new EventEnvelope { Type = "ui.frontend.stopped", Payload = new { } });
        Assert.Empty(await Connecter(ws, "frontend"));
    }

    [Fact]
    public void Les_types_retenus_sont_annonces()
    {
        Assert.Contains("marquee.snapshot", WebSocketConnectionManager.RetainedTypesByStream["marquee"]);
        Assert.Contains("ui.game.selected", WebSocketConnectionManager.RetainedTypesByStream["frontend"]);
        Assert.Null(WebSocketConnectionManager.RetainedSurfaceSlot("ui.game.started"));
    }
}
