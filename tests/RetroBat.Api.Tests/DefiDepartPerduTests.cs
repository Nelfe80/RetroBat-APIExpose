using System.Diagnostics;
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
/// LE DEPART PERDU (14/11, Rambo d'Ash ; entrainement M3-04 sur la borne du user a 15:17) : le depart de Metal Slug 3 est
/// « 0x1002C1 quitte 00 » (soldat pose). La valeur 00 ne se dit par aucun evenement, l'API la releve. Sur une borne chargee, le
/// releve venait apres les conditions d'armement (1,8 s) et rendait 01, deja pose ; l'atterrissage arrivait ensuite du wrapper
/// avec 01 pour valeur d'avant : le chrono ne partait jamais, la tentative restait ARMED.
/// </summary>
public class DefiDepartPerduTests
{
    private static Reglement Rambo()
        => Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "metal-slug-3.mission-1-rambo.json")));

    [Fact]
    public void L_adresse_quittee_par_le_depart_se_releve_en_premier()
    {
        var r = Rambo();

        Assert.Equal([(0x1002C1L, 0x00)], r.ValeursQuitteesAuDepart());
        Assert.Equal(0x1002C1L, r.AdressesDeDepart().First());
        Assert.Equal([0x1002C1L, 0x10FDB6L, 0x10040CL, 0x10040DL], r.AdressesDeDepart().ToList());
    }

    /// <summary>
    /// Une borne lente : chaque lecture met 300 ms a revenir, le soldat se pose 1,2 s apres le chargement de l'etat, et le
    /// wrapper ne livre l'atterrissage que 2,6 s apres (signaux en retard sur la memoire, vu sur la borne du user).
    /// </summary>
    private sealed class RetroArchLent : ICommandesRetroArch
    {
        private readonly Reglement _r;
        private readonly Stopwatch _depuisLeChargement = new();

        public RetroArchLent(Reglement r) => _r = r;

        public string Dossier = "";

        public static readonly TimeSpan Lecture = TimeSpan.FromMilliseconds(300);

        public static readonly TimeSpan Atterrissage = TimeSpan.FromMilliseconds(1200);

        public bool Charge => _depuisLeChargement.IsRunning;

        private int Memoire(long adresse)
        {
            if (!_depuisLeChargement.IsRunning) return adresse == 0x1002C1 ? 0x01 : 0x00; // ecran titre, demo
            return adresse switch
            {
                0x10FDB6 => 0x01,
                0x1002C1 => _depuisLeChargement.Elapsed >= Atterrissage ? 0x01 : 0x00,
                _ => 0x00,
            };
        }

        public async Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            if (commande == "GET_STATUS") return "GET_STATUS PLAYING fbneo,mslug3,crc32=0";
            if (commande == "GET_CONFIG_PARAM savestate_directory") return "GET_CONFIG_PARAM savestate_directory " + Dossier;
            if (!commande.StartsWith("READ_CORE_RAM ", StringComparison.Ordinal)) return null;
            var offset = Convert.ToInt64(commande.Split(' ')[1], 16);
            var adresse = new[] { 0x10FDB6L, 0x10040CL, 0x10040DL, 0x1002C1L }.First(a => _r.OffsetDansLeCoeur(a) == offset);
            // La memoire se lit a l'arrivee de la commande ; la reponse revient plus tard.
            var valeur = Memoire(adresse);
            await Task.Delay(Lecture, ct);
            return $"READ_CORE_RAM {commande.Split(' ')[1]} {valeur:X2}";
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            if (commande.StartsWith("LOAD_STATE_SLOT", StringComparison.Ordinal)) _depuisLeChargement.Restart();
            return Task.CompletedTask;
        }
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

    [Fact]
    public async Task Sur_une_borne_lente_le_soldat_pose_fait_partir_le_chrono()
    {
        var reglement = Rambo();
        var bus = new SimpleEventBus();
        var contexte = new ApiContext();
        var rom = Path.Combine(Path.GetTempPath(), "mslug3.zip");
        contexte.Ui.Running = new GameReference { SystemId = "arcade", GamePath = rom };
        var dossier = Directory.CreateTempSubdirectory("etats-retroarch").FullName;
        var retroArch = new RetroArchLent(reglement) { Dossier = dossier };
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var traces = new Traces();
        var service = new SpeedrunSessionService(bus, contexte, traces, null, retroArch, journal);
        try
        {
            await service.StartAsync(default);
            service.AttendreLeLancement(reglement, rom);
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = rom } });

            service.PartirSurLEtat(reglement, [1, 2, 3], "mslug3");
            for (var i = 0; i < 100 && service.EtatPoseUtc is null; i++) await Task.Delay(30);
            service.ChargerLEtatDeDepart();
            for (var i = 0; i < 100 && !retroArch.Charge; i++) await Task.Delay(30);
            Assert.True(retroArch.Charge);

            // Le wrapper livre l'atterrissage (00 -> 01) bien apres qu'il a eu lieu.
            await Task.Delay(2600);
            Assert.True(service.Instantane()?.Etat == SessionSpeedrun.Armed, string.Join(" | ", traces.Lignes));
            await bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = JsonNode.Parse("""{"signal":{"Name":"MOUNT_STATE","Address":"0x1002C1","Value":1,"Frame":400}}"""),
            });
            await bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = JsonNode.Parse("""{"signal":{"Name":"AMMO_STATE","Address":"0x10EAFA","Value":999,"Frame":401}}"""),
            });

            string? etat = null;
            for (var i = 0; i < 60; i++)
            {
                etat = service.Instantane()?.Etat;
                if (etat == SessionSpeedrun.Running) break;
                await Task.Delay(50);
            }

            Assert.Equal(SessionSpeedrun.Running, etat);
            Assert.Equal(400L, service.Instantane()!.Depart?.Frame);
        }
        finally
        {
            await service.StopAsync(default);
            service.Dispose();
            Directory.Delete(dossier, true);
            if (File.Exists(journal)) File.Delete(journal);
        }
    }

    /// <summary>
    /// « LES MONDES A L'ENVERS » NE PARTAIT JAMAIS (v1, 14/11) : le mode tient le type de bombe 0x10EAF4 a 03 (caillou), et
    /// 0x1002C1 recopie le type de bombe quand le soldat est a pied : il se pose a 03, jamais a 01 (sessions du user a 09:53,
    /// 10:02 et 15:28 : « arme, image 261 : MOUNT_STATE 0x1002C1 = 0x3 », jamais RUNNING). La v2 part sur 00 quitte.
    /// </summary>
    [Theory]
    [InlineData("metal-slug-3.mondes-a-l-envers.json", false)]
    [InlineData("metal-slug-3.mondes-a-l-envers.v2.json", true)]
    public void Le_soldat_des_mondes_se_pose_a_03(string fichier, bool part)
    {
        var e = new Evaluateur(Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", fichier))));
        e.Relever(0x10FDB6, 0x01);
        e.Relever(0x10040C, 0x00);
        e.Relever(0x10040D, 0x00);
        e.Relever(0x1002C1, 0x00);
        Assert.Equal(EtatTentative.Armee, e.Etat);

        e.Recevoir(Signal.Depuis(261, "MOUNT_STATE", "0x1002C1", 0x03));

        Assert.Equal(part ? EtatTentative.EnCourse : EtatTentative.Armee, e.Etat);
        if (!part) return;
        e.Recevoir(Signal.Depuis(9000, "PROGRESSION_STAGE", "0x10040C", 0x01));
        Assert.Equal(EtatTentative.Finie, e.Etat);
        Assert.Equal(9000 - 261, e.Resultat().FramesOfficielles);
    }
}
