using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using RetroBat.Api.Deck;
using RetroBat.Api.Hubs;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// API-6 (14/11) : le sens des signaux (.SMEM) pour tout jeu et tout client. Il se lit des le depart (avant le premier
/// signal), il se demande pour un jeu qui ne tourne pas, et il part sur les flux ingame et arcade.
/// </summary>
public sealed class SmemPourToutJeuTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Smem", nom);

    private sealed class Dossier : IDisposable
    {
        public string Racine { get; } = Path.Combine(Path.GetTempPath(), "smem-tout-" + Guid.NewGuid().ToString("N"));
        public string Mem { get; }

        public Dossier()
        {
            var arcade = Path.Combine(Racine, "arcade");
            Directory.CreateDirectory(arcade);
            Mem = Path.Combine(arcade, "19xx-the-war-against-destiny.MEM");
            File.Copy(Fichier("19xx-the-war-against-destiny.MEM"), Mem);
            File.Copy(Fichier("19xx-the-war-against-destiny.SMEM"), Path.ChangeExtension(Mem, ".SMEM"));
        }

        public void Dispose()
        {
            try { Directory.Delete(Racine, true); } catch (IOException) { }
        }
    }

    private static async Task Attendre(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    }

    [Fact]
    public async Task Le_sens_est_lu_au_depart_sans_attendre_un_signal()
    {
        using var d = new Dossier();
        var bus = new SimpleEventBus();
        var publies = new List<EventEnvelope>();
        using var ecoute = bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.semantics") lock (publies) publies.Add(e); });
        var deck = new DeckStateService(bus, new ApiContext())
        {
            DefinitionDuJeu = (rom, systeme) => rom == "19xx" ? (d.Mem, "19xx-the-war-against-destiny") : (null, null),
        };
        await deck.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\19xx.zip" } });
            await Attendre(() => { lock (publies) return publies.Count > 0; });

            var s = Assert.IsType<DeckSemantics>(Assert.Single(publies).Payload);
            Assert.Equal("loaded", s.Status);
            Assert.Equal("19xx-the-war-against-destiny", s.MemRom);
            Assert.Same(s, deck.SemantiqueCourante());
            Assert.Contains("\"Type\":\"deck.semantics\"", deck.SemantiqueJson());

            // le premier signal nomme le meme .MEM : rien n'est relu
            await bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = new
                {
                    SystemId = "arcade", Rom = "19xx-the-war-against-destiny", DefinitionFile = d.Mem,
                    signal = new RetroArchRuntimeSignal { Key = "ACTION.AMMO_GAIN", Channel = "ACTION", Name = "AMMO_GAIN", Address = "0xFF8305", Value = 3, Player = 1 },
                },
            });
            Assert.Single(publies);

            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            Assert.Null(deck.SemantiqueCourante());
            Assert.Null(deck.SemantiqueJson());
        }
        finally
        {
            await deck.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Un_autre_MEM_au_premier_signal_relit_le_sens()
    {
        using var d = new Dossier();
        var user = Path.Combine(d.Racine, "user");
        Directory.CreateDirectory(user);
        var memUser = Path.Combine(user, "19xx-the-war-against-destiny.MEM");
        File.Copy(d.Mem, memUser);   // un autre .MEM que celui du prechargement : le sens est relu pour lui

        var bus = new SimpleEventBus();
        var publies = new List<DeckSemantics>();
        using var ecoute = bus.Subscribe<EventEnvelope>(e => { if (e.Payload is DeckSemantics s) lock (publies) publies.Add(s); });
        var deck = new DeckStateService(bus, new ApiContext()) { DefinitionDuJeu = (_, _) => (d.Mem, "19xx-the-war-against-destiny") };
        await deck.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\19xx.zip" } });
            await Attendre(() => { lock (publies) return publies.Count > 0; });
            await bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = new
                {
                    SystemId = "arcade", Rom = "19xx-the-war-against-destiny", DefinitionFile = memUser,
                    signal = new RetroArchRuntimeSignal { Key = "ACTION.AMMO_GAIN", Channel = "ACTION", Name = "AMMO_GAIN", Address = "0xFF8305", Value = 3, Player = 1 },
                },
            });

            Assert.Equal(2, publies.Count);
            Assert.Equal(memUser, publies[1].DefinitionFile);
            Assert.Equal(memUser, deck.SemantiqueCourante()!.DefinitionFile);
        }
        finally
        {
            await deck.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Le_sens_d_un_jeu_qui_ne_tourne_pas()
    {
        using var d = new Dossier();
        var deck = new DeckStateService(new SimpleEventBus(), new ApiContext())
        {
            DefinitionDuJeu = (rom, _) => rom == "19xx" ? (d.Mem, "19xx-the-war-against-destiny") : (null, null),
        };
        var s = deck.SemantiqueDuJeu("fbneo", "19xx");
        Assert.NotNull(s);
        Assert.Equal("loaded", s!.Status);
        Assert.Equal("", s.LaunchId);
        Assert.Null(deck.SemantiqueDuJeu("fbneo", "inconnu"));
        Assert.Null(new DeckStateService(new SimpleEventBus(), new ApiContext()).SemantiqueDuJeu("fbneo", "19xx"));
    }

    [Fact]
    public async Task Le_sens_part_a_la_connexion_des_flux_ingame_et_arcade()
    {
        var ws = new WebSocketConnectionManager(new OptionsFixes())
        {
            DeckSemanticsJson = () => "{\"Type\":\"deck.semantics\",\"Payload\":{}}",
        };
        foreach (var flux in new[] { "ingame", "arcade" })
        {
            var socket = new SocketDEssai();
            await ws.AddSocketAsync(socket, flux);
            Assert.Contains("deck.semantics", Assert.Single(socket.Envoyes));
            Assert.Contains("deck.semantics", WebSocketConnectionManager.StreamPrefixes[flux]);
        }
        var score = new SocketDEssai();
        await ws.AddSocketAsync(score, "score");
        Assert.Empty(score.Envoyes);

        ws.DeckSemanticsJson = () => null;   // hors partie : rien
        var vide = new SocketDEssai();
        await ws.AddSocketAsync(vide, "ingame");
        Assert.Empty(vide.Envoyes);
    }

    private sealed class OptionsFixes : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = new();
        public ApiExposeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ApiExposeOptions, string?> listener) => null;
    }

    private sealed class SocketDEssai : WebSocket
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
