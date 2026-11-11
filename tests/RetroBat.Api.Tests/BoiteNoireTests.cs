using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.BoiteNoire;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;
using Xunit.Abstractions;

namespace RetroBat.Api.Tests;

/// <summary>
/// La boite noire d'une partie (11/11) : une boite par replay, les signaux du .MEM avec leur sens du .SMEM et l'image du
/// coeur, rien hors d'un enregistrement, rien quand elle est coupee, les orphelines purgees ; et son cout mesure.
/// </summary>
public sealed class BoiteNoireTests(ITestOutputHelper sortie)
{
    private static DeckSemantics Sens() => new(
        Smem.SchemaDuMessage, "l1", "arcade", "battle-garegga", "battle-garegga", "battle-garegga.MEM", "battle-garegga.SMEM",
        "ok", "smemsha", "memsha", 3, new DeckSemanticGame("arcade", "battle-garegga", "Battle Garegga"), [], [],
        [
            new DeckSemanticSignal("rank", null, "gauge", null, "Rank", "RANK", [new DeckSemanticRef("0XC9D2", "OBJECT_INTERACTION", null)],
                [], new DeckSemanticScale(-1, 1, 240), 0, 208, 0, "qualified", true),
            new DeckSemanticSignal("players.1.lives", 1, "counter", "ship", "Ship stock", "SHIP",
                [new DeckSemanticRef("0X1687", "LOSE_LIFE", 1), new DeckSemanticRef("0X1687", "GAIN_LIFE", 1)],
                [], new DeckSemanticScale(1, 1, 0), null, null, null, "qualified", true),
        ], [], 1);

    private static void Signal(SimpleEventBus bus, string nom, string adresse, int valeur, long image, int? joueur = null)
        => bus.PublishAsync(new EventEnvelope
        {
            Type = "retroarch.memory.changed",
            Payload = new
            {
                Source = "retroarch.wrapper.pipe", SystemId = "arcade", Rom = "battle-garegga",
                signal = new RetroArchRuntimeSignal { Key = "STATE." + nom, Channel = "STATE", Name = nom, Address = adresse, Value = valeur, Player = joueur, Frame = image },
            },
        });

    private static string[] Lire(string chemin)
    {
        using var gz = new GZipStream(File.OpenRead(chemin), CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Racine()
    {
        var d = Path.Combine(Path.GetTempPath(), "boite-noire-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "manifests"));
        return d;
    }

    private static void Attendre(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
    }

    [Fact]
    public void Une_boite_par_replay_avec_le_sens_du_smem_l_image_du_coeur_et_le_pied()
    {
        var bus = new SimpleEventBus();
        var racine = Racine();
        var boite = new BoiteNoireService(bus, true, racine);
        boite.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Signal(bus, "OBJECT_INTERACTION", "0xC9D2", 0xF0, 10);          // avant l'enregistrement : ignore
            bus.PublishAsync(new EventEnvelope { Type = "replay.recording.started", Payload = new { ReplayId = "rp_TEST1", SessionId = "s1", System = "fbneo", Game = "bgaregga" } });
            bus.PublishAsync(new EventEnvelope { Type = "deck.semantics", Payload = Sens() });
            Signal(bus, "OBJECT_INTERACTION", "0xc9d2", 0xEF, 3000);
            Signal(bus, "LOSE_LIFE", "0x1687", 1, 3010, 1);
            Signal(bus, "MYSTERE", "0x0042", 7, 3020);
            bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 1, Score = 19050, SystemId = "arcade", Rom = "battle-garegga" } });
            bus.PublishAsync(new EventEnvelope { Type = "replay.recording.stopped", Payload = new { ReplayId = "rp_TEST1", SessionId = "s1", LastFrame = 5185L } });
            Signal(bus, "OBJECT_INTERACTION", "0xC9D2", 0xEE, 9000);         // apres : ignore

            var chemin = boite.Chemin("rp_TEST1");
            Attendre(() => File.Exists(chemin));
            var lignes = Lire(chemin);
            var entete = JsonDocument.Parse(lignes[0]).RootElement;
            Assert.Equal(BoiteNoireService.Schema, entete.GetProperty("schema").GetString());
            Assert.Equal("rp_TEST1", entete.GetProperty("replay_id").GetString());
            var corps = lignes.Skip(1).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var rang = corps.Single(l => l.TryGetProperty("s", out var s) && s.GetString() == "rank");
            Assert.Equal(3000, rang.GetProperty("f").GetInt64());
            Assert.Equal(0xEF, rang.GetProperty("v").GetInt64());
            Assert.Equal("players.1.lives", corps.Single(l => l.TryGetProperty("k", out var k) && k.GetString() == "STATE.LOSE_LIFE").GetProperty("s").GetString());
            var inconnu = corps.Single(l => l.TryGetProperty("k", out var k) && k.GetString() == "STATE.MYSTERE");
            Assert.False(inconnu.TryGetProperty("s", out _));                 // garde la ligne brute, sans sens invente
            Assert.Equal(19050, corps.Single(l => l.TryGetProperty("s", out var s) && s.GetString() == "players.1.score").GetProperty("v").GetInt64());
            var pied = corps.Last();
            Assert.Equal(5185, pied.GetProperty("last_replay_frame").GetInt64());
            Assert.Equal(3020, pied.GetProperty("last_core_frame").GetInt64());
            Assert.DoesNotContain(corps, l => l.TryGetProperty("f", out var f) && (f.GetInt64() == 10 || f.GetInt64() == 9000));
        }
        finally
        {
            boite.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            boite.Dispose();
        }
    }

    [Fact]
    public void Coupee_par_le_reglage_elle_n_ecrit_rien()
    {
        var bus = new SimpleEventBus();
        var racine = Racine();
        var boite = new BoiteNoireService(bus, false, racine);
        boite.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        bus.PublishAsync(new EventEnvelope { Type = "replay.recording.started", Payload = new { ReplayId = "rp_OFF" } });
        Signal(bus, "OBJECT_INTERACTION", "0xC9D2", 0xEF, 1);
        bus.PublishAsync(new EventEnvelope { Type = "replay.recording.stopped", Payload = new { ReplayId = "rp_OFF", LastFrame = 1L } });
        Thread.Sleep(200);
        boite.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(Directory.Exists(Path.Combine(racine, "blackbox")) && Directory.EnumerateFiles(Path.Combine(racine, "blackbox")).Any());
    }

    [Fact]
    public void L_identifiant_standard_compare_les_adresses_en_valeur_et_respecte_le_joueur()
    {
        var sens = Sens();
        Assert.Equal("rank", BoiteNoireService.IdentifiantStandard(sens, "0x00c9d2", "OBJECT_INTERACTION", null));
        Assert.Equal("players.1.lives", BoiteNoireService.IdentifiantStandard(sens, "0x1687", "LOSE_LIFE", 1));
        Assert.Null(BoiteNoireService.IdentifiantStandard(sens, "0x1687", "LOSE_LIFE", 2));
        Assert.Null(BoiteNoireService.IdentifiantStandard(sens with { Status = "invalid" }, "0xC9D2", "OBJECT_INTERACTION", null));
        Assert.Null(BoiteNoireService.IdentifiantStandard(null, "0xC9D2", "OBJECT_INTERACTION", null));
    }

    [Fact]
    public void Une_boite_sans_replay_et_les_morceaux_interrompus_sont_purges()
    {
        var racine = Racine();
        var dossier = Path.Combine(racine, "blackbox");
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(racine, "manifests", "rp_GARDE.json"), "{}");
        var garde = Path.Combine(dossier, "rp_GARDE.blackbox.jsonl.gz");
        var orpheline = Path.Combine(dossier, "rp_PARTI.blackbox.jsonl.gz");
        var recente = Path.Combine(dossier, "rp_RECENT.blackbox.jsonl.gz");
        var morceau = Path.Combine(dossier, "rp_X.blackbox.jsonl.gz.part");
        foreach (var f in new[] { garde, orpheline, recente, morceau }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(orpheline, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(garde, DateTime.UtcNow.AddDays(-3));
        var boite = new BoiteNoireService(new SimpleEventBus(), true, racine);
        Assert.Equal(2, boite.PurgerLesOrphelines());
        Assert.True(File.Exists(garde));
        Assert.True(File.Exists(recente));        // l'enregistrement peut encore etre en cours de scellement
        Assert.False(File.Exists(orpheline));
        Assert.False(File.Exists(morceau));
    }

    [Fact]
    public void Cout_mesure_dix_minutes_a_soixante_changements_par_seconde()
    {
        // Charge d'un shmup riche (DaiOuJou : 85 lignes au .MEM) : 60 changements par seconde pendant 10 minutes.
        var bus = new SimpleEventBus();
        var racine = Racine();
        var boite = new BoiteNoireService(bus, true, racine);
        boite.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        bus.PublishAsync(new EventEnvelope { Type = "replay.recording.started", Payload = new { ReplayId = "rp_COUT", SessionId = "s" } });
        bus.PublishAsync(new EventEnvelope { Type = "deck.semantics", Payload = Sens() });
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        const int images = 60 * 60 * 10;
        for (var i = 0; i < images; i++)
            Signal(bus, i % 3 == 0 ? "OBJECT_INTERACTION" : "LOSE_LIFE", i % 3 == 0 ? "0xC9D2" : "0x1687", i % 240, 1000 + i, 1);
        var publication = sw.Elapsed;
        bus.PublishAsync(new EventEnvelope { Type = "replay.recording.stopped", Payload = new { ReplayId = "rp_COUT", LastFrame = (long)images } });
        var chemin = boite.Chemin("rp_COUT");
        Attendre(() => File.Exists(chemin));
        proc.Refresh();
        var cpu = proc.TotalProcessorTime - cpu0;
        var octets = new FileInfo(chemin).Length;
        var lignes = Lire(chemin).Length;
        sortie.WriteLine($"{images} signaux en 10 min simulees : {octets / 1024.0:F1} Ko compresses ({octets / 10240.0:F1} Ko par minute), "
            + $"{lignes} lignes, publication {publication.TotalMilliseconds:F0} ms ({publication.TotalMilliseconds * 1000 / images:F1} us par signal), "
            + $"processeur total {cpu.TotalMilliseconds:F0} ms");
        boite.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(images + 3, lignes);                                    // en-tete, sens, signaux, pied : rien de perdu
        Assert.True(octets < 2 * 1024 * 1024, "plus de 2 Mo pour 10 minutes");
    }
}
