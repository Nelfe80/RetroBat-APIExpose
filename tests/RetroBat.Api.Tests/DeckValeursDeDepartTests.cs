using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Replay.Video;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES VALEURS DE DEPART D'UNE EPREUVE (12/11, capture d'Aynshe : plaques MARIO et SPLITS a « -- » au depart de Super Mario
/// Bros. 1-1). Le wrapper ne parle qu'au changement : l'API lit une fois, par READ_CORE_RAM, les lignes que le .SMEM cite, et
/// les donne au Deck en valeurs d'etat (deck.signals).
/// </summary>
public sealed class DeckValeursDeDepartTests
{
    // Les lignes de super-mario-bros.MEM (Data Pack, 12/11) que le .SMEM cite, et deux qu'il ne cite pas
    private const string Mem = """
        return {
          events = {
            flow = {
              lifecycle = {
                { address=0X075A, type="u8", condition="eq", value=0XFF, action="GAME_OVER", desc="0 (game over)" },
                { address=0X0776, type="u8", condition="change", action="PAUSE_ON", desc="Pause" },
              },
            },
            progression = {
              zone = {
                { address=0X075C, type="u8", condition="change", action="PROGRESSION_ZONE", desc="Stage within world" },
                { address=0X075F, type="u8", condition="change", action="PROGRESSION_ZONE", desc="Current world" },
              },
              stage = {
                { address=0X006D, type="u8", condition="change", action="PROGRESSION_STAGE", desc="Stage scrolling progress" },
              },
            },
            player = {
              lives = {
                { address=0X075A, type="u8", condition="increase", action="GAIN_LIFE", desc="Lives remaining increased" },
                { address=0X075A, type="u8", condition="decrease", action="LOSE_LIFE", desc="Lives remaining decreased" },
                { address=0X075A, type="u8", condition="eq", value=0X00, action="LIVES_STATE", desc="1" },
              },
              inventory = {
                { address=0X0756, type="u8", condition="change", action="DYNAMIC_INVENTORY", desc="Player's powerup state" },
                { address=0X0754, type="u8", condition="change", action="DYNAMIC_INVENTORY", no_log=true, desc="muette" },
              },
            },
          },
        }
        """;

    private const string SmemSmb = """
        return {
          schema = 1,
          kind = "semantics",
          version = 1,
          game = { system = "nes", slug = "super-mario-bros", title = "Super Mario Bros." },
          mem = { file = "super-mario-bros.MEM" },
          signals = {
            { id = "players.1.lives", player = 1, kind = "counter", label = "Lives", short = "LIV",
              from = { { address = 0X075A, action = "GAIN_LIFE" }, { address = 0X075A, action = "LOSE_LIFE" } },
              min = 0, max = 255, quality = "qualified", proof = "replay" },
            { id = "players.1.power.level", player = 1, kind = "enum", label = "Power", short = "PWR",
              from = { { address = 0X0756, action = "DYNAMIC_INVENTORY" } },
              values = { { raw = 0, value = 0, label = "SMALL" }, { raw = 1, value = 1, label = "BIG" }, { raw = 2, value = 2, label = "FIRE" } }, quality = "qualified", proof = "replay" },
            { id = "area", kind = "counter", label = "World", short = "W",
              from = { { address = 0X075F, action = "PROGRESSION_ZONE" } }, min = 0, max = 7, quality = "qualified", proof = "etats" },
            { id = "stage", kind = "counter", label = "Level in world", short = "LVL",
              from = { { address = 0X075C, action = "PROGRESSION_ZONE" } }, min = 0, max = 3, quality = "qualified", proof = "etats" },
            { id = "stage.progress", kind = "counter", label = "Page", short = "PAGE",
              from = { { address = 0X006D, action = "PROGRESSION_STAGE" } }, min = 0, max = 255, quality = "qualified", proof = "replay" },
          },
        }
        """;

    /// <summary>La memoire de l'etat de l'epreuve 1-1 : vies 2 (le jeu affiche 3), Mario petit, monde 0, niveau 0, page 0.</summary>
    private sealed class Memoire(Dictionary<long, int> octets) : ICommandesRetroArch
    {
        public readonly List<string> Demandes = [];

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            lock (Demandes) Demandes.Add(commande);
            var c = commande.Split(' ');
            if (c.Length != 3 || c[0] != "READ_CORE_RAM") return Task.FromResult<string?>(null);
            var debut = Convert.ToInt64(c[1], 16);
            var n = int.Parse(c[2]);
            var lus = new List<string>();
            for (var i = 0; i < n; i++)
            {
                if (!octets.TryGetValue(debut + i, out var o)) return Task.FromResult<string?>("READ_CORE_RAM " + c[1] + " -1");
                lus.Add(o.ToString("X2"));
            }

            return Task.FromResult<string?>("READ_CORE_RAM " + c[1] + " " + string.Join(' ', lus));
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
    }

    private static Dictionary<long, int> EtatDu11() => new() { [0x075A] = 2, [0x0756] = 0, [0x075F] = 0, [0x075C] = 0, [0x006D] = 0, [0x0776] = 0 };

    private static IReadOnlyList<DeckSemanticSignal> Sens()
    {
        var r = Smem.Lire(SmemSmb, Mem, "super-mario-bros.MEM");
        Assert.True(r.Valide, string.Join(" ; ", r.Errors));
        return r.Signals;
    }

    // ─────────────────────────── le pur ───────────────────────────

    [Fact]
    public void Les_lignes_a_relever_sont_celles_que_le_smem_cite_et_que_le_wrapper_emet()
    {
        var lignes = DeckValeursDeDepart.LignesARelever(Sens(), LignesDuMem.Lire(Mem));
        Assert.Equal(["GAIN_LIFE@0x75A", "LOSE_LIFE@0x75A", "DYNAMIC_INVENTORY@0x756", "PROGRESSION_ZONE@0x75F", "PROGRESSION_ZONE@0x75C",
            "PROGRESSION_STAGE@0x6D"], lignes.Select(l => l.Action + "@" + Hex.Ecrire(l.Adresse)).ToArray());
        // ni la pause ni la ligne de fin de partie (non citees), ni la ligne muette
        Assert.DoesNotContain(lignes, l => l.Action is "PAUSE_ON" or "GAME_OVER" or "LIVES_STATE" || l.Muette);
    }

    [Theory]
    [InlineData("change", null, null, 7, 7L)]
    [InlineData("increase", null, null, 2, 2L)]
    [InlineData("decrease", null, null, 2, 2L)]
    [InlineData("eq", 0xFF, null, 2, null)]          // GAME_OVER 0xFF ne dit rien de deux vies
    [InlineData("eq", 0xFF, null, 0xFF, 0xFFL)]
    [InlineData("change", 4, null, 3, null)]          // un change filtre sur 4 ne parle pas a 3
    [InlineData("change", null, 0xF8L, 0x3DL, 0x38L)]   // la barre du boss par paliers de 8 (mask)
    [InlineData("bit_true", null, 0x02L, 0x03L, 0x02L)]
    [InlineData("bit_true", null, 0x02L, 0x01L, null)]
    [InlineData("bit_false", null, 0x02L, 0x01L, 0x00L)]
    public void La_valeur_d_etat_suit_le_wrapper(string condition, int? valeur, long? masque, long brute, long? attendue)
    {
        var l = new LigneDuMem(0x10, condition, valeur, "X", false, "u8", masque);
        Assert.Equal(attendue, DeckValeursDeDepart.ValeurDEtat(l, brute));
    }

    [Fact]
    public void Les_octets_se_lisent_dans_l_ordre_du_type()
    {
        Assert.Equal(0x1234, DeckValeursDeDepart.Brute("u16le", [0x34, 0x12]));
        Assert.Equal(0x1234, DeckValeursDeDepart.Brute("u16be", [0x12, 0x34]));
        Assert.Equal(0x07, DeckValeursDeDepart.Brute("u8", [0x07]));
        Assert.Equal(0x00123456, DeckValeursDeDepart.Brute("u32le", [0x56, 0x34, 0x12, 0x00]));
    }

    [Fact]
    public void L_adresse_se_traduit_par_coeur()
    {
        var u8 = new LigneDuMem(0x075A, "change", null, "X", false);
        var u16 = new LigneDuMem(0x10ED18, "change", null, "X", false, "u16le");
        // consoles et coeurs a fantomes : index brut
        Assert.Equal([0x075AL], DeckValeursDeDepart.Places(u8, "fceumm", null, []));
        Assert.Equal([0x075AL], DeckValeursDeDepart.Places(u8, "fceumm_ghosts", null, []));
        Assert.Equal([0xF60EL], DeckValeursDeDepart.Places(u8 with { Adresse = 0xF60E }, "genesis_plus_gx_ghosts", null, []));
        // FBNeo : RAM de travail Neo-Geo, mots 68000 inverses (octet MAME A a l'index A ^ 1)
        Assert.Equal([0xED19L, 0xED18L], DeckValeursDeDepart.Places(u16, "fbneo", null, []));
        // le reglement qui dit son bloc (« runtime.core_ram ») passe devant
        Func<long, long> bloc = a => (a - 0x100000) ^ 1;
        Assert.Equal([0xED19L, 0xED18L], DeckValeursDeDepart.Places(u16, null, bloc, []));
        // MAME n'expose rien a READ_CORE_RAM, un coeur inconnu non plus
        Assert.Null(DeckValeursDeDepart.Places(u8, "mame", null, []));
        Assert.Null(DeckValeursDeDepart.Places(u8, null, null, []));
    }

    [Fact]
    public async Task Une_ligne_sur_deux_octets_de_fbneo_se_lit_en_une_commande_et_dans_l_ordre_du_68000()
    {
        // 0x10ED18 = 0x1234 en u16be (ordre du 68000) : FBNeo range l'octet fort (0x12) a 0xED19, le faible a 0xED18
        var memoire = new Memoire(new() { [0xED18] = 0x34, [0xED19] = 0x12 });
        var l = new LigneDuMem(0x10ED18, "change", null, "OBJECT_INTERACTION", false, "u16be");
        var lus = await DeckValeursDeDepart.LireAsync(memoire, [l], new EpreuveALire("x", "fbneo", null, null, "mslug3"), "fbneo", "mslug3", null,
            CancellationToken.None);
        var s = Assert.Single(lus);
        Assert.Equal(0x1234, s.Value);
        Assert.Equal("READ_CORE_RAM ed18 2", Assert.Single(memoire.Demandes));
    }

    [Fact]
    public async Task Sans_reponse_rien_n_est_invente()
    {
        var memoire = new Memoire(new());
        var lignes = DeckValeursDeDepart.LignesARelever(Sens(), LignesDuMem.Lire(Mem));
        var lus = await DeckValeursDeDepart.LireAsync(memoire, lignes, new EpreuveALire("x", "fceumm", null, null, "smb"), "nes", "smb", null,
            CancellationToken.None);
        Assert.Empty(lus);
        // une lecture par adresse : les deux lignes des vies partagent la leur
        Assert.Equal(5, memoire.Demandes.Count);
    }

    // ─────────────────────────── le service, par le bus ───────────────────────────

    private sealed class Banc : IAsyncDisposable
    {
        public readonly SimpleEventBus Bus = new();
        public readonly DeckStateService Etat;
        public readonly DeckValeursDeDepartService Service;
        public readonly Memoire Memoire = new(EtatDu11());
        public readonly List<DeckSignals> Publies = [];
        public readonly string Dossier = Path.Combine(Path.GetTempPath(), "deck-depart-" + Guid.NewGuid().ToString("N"));
        public string MemFichier => Path.Combine(Dossier, "nes", "super-mario-bros.MEM");
        private const string Rom = @"E:\RetroBat\roms\nes\Super Mario Bros. (World).nes";

        public Banc(bool avecDeck = true)
        {
            Directory.CreateDirectory(Path.Combine(Dossier, "nes"));
            File.WriteAllText(MemFichier, Mem);
            File.WriteAllText(Path.ChangeExtension(MemFichier, ".SMEM"), SmemSmb);
            Etat = new DeckStateService(Bus, new ApiContext());
            Service = new DeckValeursDeDepartService(Bus, Etat, commandes: Memoire)
            {
                Epreuve = () => new EpreuveALire("smb.level.1-1", "fceumm", null, MemFichier, "super-mario-bros"),
            };
            Bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.signals" && e.Payload is DeckSignals s) lock (Publies) Publies.Add(s); });
            Etat.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (avecDeck)
                Etat.PoserLeLancement(new DeckLancement(DeckModes.Speedrun, "nes", "Super Mario Bros. (World)", "super-mario-bros", "Super Mario Bros.",
                    "player", Rom, "posed", "super-mario-bros-brick", "Brick", true, "official-default", ["super-mario-bros-brick", "off"], null, null,
                    DateTime.UtcNow));
            Bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "nes", GamePath = Rom } });
        }

        public void Signal(string nom, string adresse, int valeur) => Bus.PublishAsync(new EventEnvelope
        {
            Type = "retroarch.memory.changed",
            Payload = new
            {
                Source = "retroarch.wrapper.pipe", SystemId = "nes", Rom = "super-mario-bros", DefinitionFile = MemFichier,
                signal = new RetroArchRuntimeSignal { Key = "ACTION." + nom, Channel = "ACTION", Name = nom, Address = adresse, Value = valeur, Frame = 10 },
            },
        });

        public async Task<DeckSignals> AttendreAsync(int n = 1)
        {
            for (var i = 0; i < 100; i++)
            {
                lock (Publies) if (Publies.Count >= n) return Publies[n - 1];
                await Task.Delay(20);
            }

            throw new TimeoutException("aucun deck.signals");
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            await Etat.StopAsync(CancellationToken.None);
            try { Directory.Delete(Dossier, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_l_etat_charge_les_plaques_MARIO_et_la_page_du_depart_recoivent_leur_valeur()
    {
        await using var b = new Banc();
        b.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.etat.charge", Payload = new { defi = "smb.level.1-1" } });
        var m = await b.AttendreAsync();
        Assert.Equal(DeckValeursDeDepartService.RaisonEtat, m.Reason);
        Assert.Equal(b.Etat.LaunchIdCourant, m.LaunchId);
        var valeurs = m.Signals.ToDictionary(s => s.Name + "@" + s.Address, s => s.Value);
        Assert.Equal(2, valeurs["GAIN_LIFE@0x75A"]);
        Assert.Equal(2, valeurs["LOSE_LIFE@0x75A"]);
        Assert.Equal(0, valeurs["DYNAMIC_INVENTORY@0x756"]);
        Assert.Equal(0, valeurs["PROGRESSION_ZONE@0x75F"]);
        Assert.Equal(0, valeurs["PROGRESSION_ZONE@0x75C"]);
        Assert.Equal(0, valeurs["PROGRESSION_STAGE@0x6D"]);
        Assert.All(m.Signals, s => Assert.Equal(DeckValeursDeDepart.Source, s.Source));

        // l'instantane du Deck les porte (un Deck qui se connecte apres les voit)
        var instantane = b.Etat.Snapshot("connect");
        Assert.Contains(instantane.Signals, s => s.Name == "PROGRESSION_STAGE" && s.Value == 0);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(m));
        Assert.Equal("0x6D", json.RootElement.GetProperty("Signals").EnumerateArray()
            .First(s => s.GetProperty("Name").GetString() == "PROGRESSION_STAGE").GetProperty("Address").GetString());
    }

    [Fact]
    public async Task Une_ligne_dont_le_wrapper_a_parle_pendant_la_lecture_garde_la_valeur_du_wrapper()
    {
        await using var b = new Banc();
        // le wrapper parle de la page APRES le debut de la lecture : sa valeur (1) est plus fraiche que la memoire lue (0)
        var avant = DateTime.UtcNow.AddSeconds(-1);
        b.Signal("PROGRESSION_STAGE", "0x006D", 1);
        var partie = b.Etat.ContexteDeDepart()!;
        var lus = new[]
        {
            new DeckSignal("STATE.PROGRESSION_STAGE", "STATE", "PROGRESSION_STAGE", "0x6D", 0, "0x00", null, null, DeckValeursDeDepart.Source, "nes", "smb", null, avant),
            new DeckSignal("STATE.DYNAMIC_INVENTORY", "STATE", "DYNAMIC_INVENTORY", "0x756", 1, "0x01", null, null, DeckValeursDeDepart.Source, "nes", "smb", null, avant),
        };
        Assert.Equal(1, b.Etat.PoserLesValeursDeDepart(partie.LaunchId, lus, avant, "run-start"));
        var m = await b.AttendreAsync();
        var s = Assert.Single(m.Signals);
        Assert.Equal("DYNAMIC_INVENTORY", s.Name);
        var page = Assert.Single(b.Etat.Snapshot("connect").Signals, x => x.Name == "PROGRESSION_STAGE");
        Assert.Equal(1, page.Value);
        Assert.Equal("ACTION", page.Channel);
    }

    [Fact]
    public async Task Une_valeur_lue_remplace_la_ligne_du_wrapper_d_avant_sous_sa_cle()
    {
        await using var b = new Banc();
        b.Signal("LOSE_LIFE", "0x075A", 4);          // le titre et la demo, avant l'etat de depart
        await Task.Delay(30);
        b.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.etat.charge", Payload = new { defi = "smb.level.1-1" } });
        await b.AttendreAsync();
        var vies = Assert.Single(b.Etat.Snapshot("connect").Signals, x => x.Name == "LOSE_LIFE");
        Assert.Equal(2, vies.Value);
        Assert.Equal("ACTION.LOSE_LIFE", vies.Key);   // une seule ligne dans l'instantane, sous la cle du wrapper
    }

    [Fact]
    public async Task Sans_Deck_pose_ou_hors_epreuve_rien_n_est_lu()
    {
        await using var sans = new Banc(avecDeck: false);
        Assert.Equal(0, await sans.Service.LireAsync(DeckValeursDeDepartService.RaisonEtat, CancellationToken.None));
        Assert.Empty(sans.Memoire.Demandes);

        await using var horsEpreuve = new Banc();
        horsEpreuve.Service.Epreuve = () => null;
        Assert.Equal(0, await horsEpreuve.Service.LireAsync(DeckValeursDeDepartService.RaisonDepart, CancellationToken.None));
        Assert.Empty(horsEpreuve.Memoire.Demandes);
    }

    [Fact]
    public async Task Les_valeurs_de_depart_ne_passent_jamais_par_le_flux_du_wrapper()
    {
        await using var b = new Banc();
        var memoire = 0;
        b.Bus.Subscribe<EventEnvelope>(e => { if (e.Type is "retroarch.memory.changed" or "ingame.memory.changed") Interlocked.Increment(ref memoire); });
        b.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.started", Payload = new { sessionId = "s", attemptId = "a" } });
        var m = await b.AttendreAsync();
        Assert.Equal(DeckValeursDeDepartService.RaisonDepart, m.Reason);
        Assert.Equal(0, memoire);
    }
}
