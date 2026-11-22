using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA FENETRE SOURDE (22/11, Old AF Gamer, Super Mario World) : apres LOAD_STATE_SLOT l'API jetait tous les signaux pendant 0,5 a
/// 0,7 s ; l'evenement de depart des huit etats SMW tombe 0,5 s apres le chargement (SMB3 0,73 s, MM2 0,8 s). La regle exacte :
/// seul un signal emis AVANT la commande est perime (horloge de la ligne du wrapper), tout signal posterieur est accepte.
/// </summary>
public class DefiFenetreSourdeTests
{
    private static readonly byte[] Octets = [1, 2, 3, 4, 5];

    private static Reglement Smw()
        => Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "super-mario-world.dp4.json")));

    /// <summary>RetroArch simule : date chaque chargement, repond aux lectures du niveau ($13BF) comme l'etat charge.</summary>
    private sealed class Commandes(Reglement r, string dossier) : ICommandesRetroArch
    {
        public readonly List<long> Chargements = [];
        public readonly List<string> Envoyees = [];

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            if (commande == "GET_STATUS") return Task.FromResult<string?>("GET_STATUS PLAYING snes9x,smw,crc32=0");
            if (commande == "GET_CONFIG_PARAM savestate_directory") return Task.FromResult<string?>("GET_CONFIG_PARAM savestate_directory " + dossier);
            if (!commande.StartsWith("READ_CORE_RAM ", StringComparison.Ordinal)) return Task.FromResult<string?>(null);
            var offset = Convert.ToInt64(commande.Split(' ')[1], 16);
            var valeur = r.OffsetDansLeCoeur(0x13BF) == offset ? 0x06 : 0x00;
            return Task.FromResult<string?>($"READ_CORE_RAM {commande.Split(' ')[1]} {valeur:X2}");
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            lock (Chargements)
            {
                Envoyees.Add(commande);
                if (commande.StartsWith("LOAD_STATE_SLOT", StringComparison.Ordinal)) Chargements.Add(DateTimeOffset.Now.ToUnixTimeMilliseconds());
            }

            return Task.CompletedTask;
        }

        public int NombreDeChargements() { lock (Chargements) return Chargements.Count; }
        public long Chargement(int n) { lock (Chargements) return Chargements[n]; }
    }

    private sealed class Traces : Microsoft.Extensions.Logging.ILogger<SpeedrunSessionService>
    {
        public readonly List<string> Lignes = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lignes) Lignes.Add(DateTime.Now.ToString("HH:mm:ss.fff ") + formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
        }
    }

    private sealed record Borne(SpeedrunSessionService S, Commandes RetroArch, SimpleEventBus Bus, string Dossier, Traces Journal) : IDisposable
    {
        public void Dispose() => Directory.Delete(Dossier, true);

        /// <summary>Un signal du wrapper emis a l'instant <paramref name="emisMs"/> (horloge de la ligne du tube).</summary>
        public Task Signal(long emisMs, string nom, string adresse, int valeur, long frame)
        {
            var h = DateTimeOffset.FromUnixTimeMilliseconds(emisMs).ToLocalTime();
            var ligne = $"[{h:HH:mm:ss.fff}] [UDP_OUT] {nom} {adresse} {valeur:X2} | FRAME:{frame}";
            var payload = new JsonObject { ["signal"] = new JsonObject { ["Name"] = nom, ["Address"] = adresse, ["Value"] = valeur, ["Frame"] = frame, ["RawLine"] = ligne } };
            return Bus.PublishAsync(new EventEnvelope { Type = "retroarch.memory.changed", Payload = payload });
        }

        public Task Depart(long emisMs, long frame) => Signal(emisMs, "GAME_PLAYING", "0x0100", 0x14, frame);
    }

    private static async Task<Borne> Ouvrir(Reglement r)
    {
        var bus = new SimpleEventBus();
        var contexte = new ApiContext();
        var rom = Path.Combine(Path.GetTempPath(), "smw.sfc");
        contexte.Ui.Running = new GameReference { SystemId = "snes", GamePath = rom };
        var dossier = Directory.CreateTempSubdirectory("etats-retroarch").FullName;
        var retroArch = new Commandes(r, dossier);
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var traces = new Traces();
        var service = new SpeedrunSessionService(bus, contexte, traces, null, retroArch, journal);
        await service.StartAsync(default);
        service.AttendreLeLancement(r, rom);
        await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = rom } });
        return new Borne(service, retroArch, bus, dossier, traces);
    }

    private static async Task Attendre(Func<bool> condition, int essais = 150)
    {
        for (var i = 0; i < essais && !condition(); i++) await Task.Delay(30);
        Assert.True(condition());
    }

    private static async Task<Borne> PremierLancement(Reglement r)
    {
        var borne = await Ouvrir(r);
        borne.S.PartirSurLEtat(r, Octets, "smw");
        await Attendre(() => borne.S.EtatPoseUtc is not null);
        borne.S.ChargerLEtatDeDepart();
        await Attendre(() => borne.RetroArch.NombreDeChargements() == 1);
        return borne;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(730)]
    public async Task Au_premier_lancement_le_depart_emis_apres_la_commande_fait_partir_le_chrono(int apresMs)
    {
        var r = Smw();
        using var borne = await PremierLancement(r);
        var t0 = borne.RetroArch.Chargement(0);
        await Attendre(() => borne.S.Instantane()?.Etat == SessionSpeedrun.Armed);

        await borne.Depart(t0 + apresMs, 7000);

        await Attendre(() => borne.S.Instantane()?.Etat == SessionSpeedrun.Running, 60);
        Assert.Equal(7000L, borne.S.Instantane()!.Depart?.Frame);
        await borne.S.StopAsync(default);
        borne.S.Dispose();
    }

    [Fact]
    public async Task Un_signal_reste_dans_le_tube_de_l_ancienne_partie_ne_fait_pas_partir_le_chrono()
    {
        var r = Smw();
        using var borne = await PremierLancement(r);
        var t0 = borne.RetroArch.Chargement(0);
        await Attendre(() => borne.S.Instantane()?.Etat == SessionSpeedrun.Armed);

        // emis 300 ms avant la commande, livre apres elle ; et 20 ms apres l'envoi, dans la marge du chargement par RetroArch
        await borne.Depart(t0 - 300, 100);
        await borne.Depart(t0 + 20, 101);
        await Task.Delay(300);
        Assert.Equal(SessionSpeedrun.Armed, borne.S.Instantane()?.Etat);

        await borne.Depart(t0 + 500, 700);
        await Attendre(() => borne.S.Instantane()?.Etat == SessionSpeedrun.Running, 60);
        Assert.Equal(700L, borne.S.Instantane()!.Depart?.Frame);
        await borne.S.StopAsync(default);
        borne.S.Dispose();
    }

    [Fact]
    public async Task La_relance_repetee_dix_fois_part_dix_fois_depuis_l_etat_sans_faux_depart()
    {
        var r = Smw();
        using var borne = await PremierLancement(r);
        await Attendre(() => borne.S.EtatChargeUtc is not null);
        await Attendre(() => borne.S.Instantane()?.Etat == SessionSpeedrun.Armed);
        await borne.Depart(borne.RetroArch.Chargement(0) + 500, 700);
        await Attendre(() => borne.S.Instantane()?.Etat == SessionSpeedrun.Running, 60);

        for (var n = 1; n <= 10; n++)
        {
            var session = borne.S.Instantane()!;
            var relance = borne.S.CommanderAsync(new CommandeSpeedrun("r" + n, session.SessionId, SessionSpeedrun.ActionRelancer, Confirme: true));
            await Attendre(() => borne.RetroArch.NombreDeChargements() == n + 1);
            var t0 = borne.RetroArch.Chargement(n);

            // le depart de l'ancienne partie encore dans le tube, puis celui de l'etat recharge, 0,5 s apres la commande
            await borne.Depart(t0 - 200, 1000L * n);
            await borne.Depart(t0 + 500, 1000L * n + 500);
            await relance;

            for (var i = 0; i < 150 && !(borne.S.Instantane()?.Etat == SessionSpeedrun.Running && borne.S.Instantane()!.Depart?.Frame == 1000L * n + 500); i++) await Task.Delay(30);
            var vu = borne.S.Instantane();
            Assert.True(vu?.Etat == SessionSpeedrun.Running && vu.Depart?.Frame == 1000L * n + 500, $"relance {n} : etat {vu?.Etat}, depart {vu?.Depart?.Frame} | " + string.Join(" | ", borne.Journal.Lignes.TakeLast(25)));
        }

        await borne.S.StopAsync(default);
        borne.S.Dispose();
    }
}
