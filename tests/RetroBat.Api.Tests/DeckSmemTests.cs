using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le .SMEM, sens des signaux du .MEM pour l'affichage (decision du user du 08/11). Les fichiers de 19XX sont ceux de
/// la couche de test de la borne (rejeu de 17 replays, E:\claude_travail\smem_19xx\RAPPORT.md), recopies ici.
/// </summary>
public sealed class DeckSmemTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Smem", nom);

    private static string Smem19xx => File.ReadAllText(Fichier("19xx-the-war-against-destiny.SMEM"));

    private static string MemDeTest => File.ReadAllText(Fichier("19xx-the-war-against-destiny.MEM"));

    private const string Entete = """
        return {
          schema = 1, kind = "semantics", version = 1,
          game = { system = "arcade", slug = "jeu" },
          mem = { file = "jeu.MEM" },
        """;

    private const string MemMinimal = """
        return {
          events = {
            combat = {
              enemies = {
                { address=0XFF8305, type="u8", condition="decrease", action="BOMB_FIRED", desc="bomb" },
                { address=0XFF8306, type="u8", condition="change", action="AMMO_STATE", player=1, no_log=true, desc="muette" },
              },
            },
          },
        }
        """;

    private static SmemResultat LireAvec(string signaux, string mem = MemMinimal)
        => Smem.Lire(Entete + signaux + "\n}\n", mem, "jeu.MEM");

    // ─────────────────────────── 19XX ───────────────────────────

    [Fact]
    public void Le_SMEM_de_19XX_est_valide_contre_la_couche_de_test_et_publie_ses_signaux()
    {
        var r = Smem.Lire(Smem19xx, MemDeTest, "19xx-the-war-against-destiny.MEM");

        Assert.True(r.Valide, string.Join(" | ", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(1, r.Version);
        Assert.Equal("19xx-the-war-against-destiny", r.Game!.Slug);
        Assert.Equal(10, r.Signals.Count);
        Assert.Equal(7, r.Instruments.Count);

        var bombes = Assert.Single(r.Signals, s => s.Id == "players.1.resources.bombs");
        Assert.Equal(new[] { ("0xFF8305", "BOMB_FIRED", (int?)null), ("0xFF8305", "AMMO_GAIN", (int?)1) },
            bombes.From.Select(f => (f.Address, f.Action, f.Player)).ToArray());
        Assert.Equal(3, bombes.Initial);
        Assert.Null(bombes.Max); // le maximum du jeu n'est pas prouve : pas de jauge

        var tir = Assert.Single(r.Signals, s => s.Id == "players.1.power.level");
        Assert.Equal((1L, 2L), (tir.Min!.Value, tir.Max!.Value));
        Assert.Equal(2, Assert.Single(tir.Values, v => v.Raw == 0xFF).Value);

        var stage = Assert.Single(r.Signals, s => s.Id == "progress.stageId");
        Assert.Equal(new DeckSemanticScale(1, 1, 1), stage.Scale);

        var puissance = Assert.Single(r.Signals, s => s.Id == "players.1.bomb.power");
        Assert.Equal("unproven", puissance.Quality);
        Assert.False(puissance.Available);

        var boss = Assert.Single(r.Signals, s => s.Id == "bosses.stage.state");
        Assert.Equal("Fighting", Assert.Single(boss.Values, v => v.RawMin == 1).Label);
    }

    [Fact]
    public void Contre_le_MEM_officiel_le_SMEM_de_19XX_est_refuse_ligne_par_ligne()
    {
        // L'officiel n'a ni les montees (AMMO_GAIN, WEAPON_UPGRADE), ni l'arme, ni le stage, ni le boss, et sa ligne
        // 0xFF830F part encore en BOMB_FIRED : le sens ne peut pas citer des lignes qui n'existent pas.
        var officiel = File.ReadAllText(Fichier("19xx-the-war-against-destiny.officiel.MEM"));
        var r = Smem.Lire(Smem19xx, officiel, "19xx-the-war-against-destiny.MEM");

        Assert.False(r.Valide);
        Assert.Empty(r.Signals);
        Assert.Contains(r.Errors, e => e.Contains("0xFF8305 AMMO_GAIN joueur 1"));
        Assert.Contains(r.Errors, e => e.Contains("0xFF830F RESOURCE_LOSE sans joueur"));
        Assert.Contains(r.Errors, e => e.Contains("0xFF4DD4 PROGRESSION_STAGE"));
        Assert.DoesNotContain(r.Errors, e => e.Contains("0xFF8305 BOMB_FIRED"));
    }

    [Fact]
    public void Sans_MEM_aucune_reference_ne_peut_etre_verifiee()
    {
        var r = Smem.Lire(Smem19xx, null, null);
        Assert.False(r.Valide);
        Assert.Contains(r.Errors, e => e.Contains(".MEM introuvable"));
    }

    // ─────────────────────────── regles ───────────────────────────

    [Fact]
    public void Un_signal_cite_une_ligne_par_adresse_action_et_joueur()
    {
        var bon = LireAvec("""
              signals = { { id = "players.1.resources.bombs", kind = "counter", quality = "qualified",
                from = { { address = 0XFF8305, action = "BOMB_FIRED" } } } },
            """);
        Assert.True(bon.Valide, string.Join(" | ", bon.Errors));

        var mauvaisJoueur = LireAvec("""
              signals = { { id = "b", kind = "counter", quality = "qualified",
                from = { { address = 0XFF8305, action = "BOMB_FIRED", player = 1 } } } },
            """);
        Assert.Contains(mauvaisJoueur.Errors, e => e.Contains("aucune ligne du .MEM 0xFF8305 BOMB_FIRED joueur 1"));

        var muette = LireAvec("""
              signals = { { id = "b", kind = "counter", quality = "qualified",
                from = { { address = 0XFF8306, action = "AMMO_STATE", player = 1 } } } },
            """);
        Assert.Contains(muette.Errors, e => e.Contains("muette"));
    }

    [Fact]
    public void Le_SMEM_ne_declare_aucune_adresse_a_surveiller()
    {
        // Le modele du CDC RetroGhost du 09/10 (« memory = { x = { address=... } } ») est remplace par les references au
        // .MEM : une adresse hors d'une reference est une cle inconnue.
        var r = Smem.Lire(Entete + """
              memory = { stage = { address = 0XFF4DD4, type = "u8" } },
              signals = { },
            }
            """, MemMinimal, "jeu.MEM");
        Assert.Contains(r.Errors, e => e.Contains("cle \"memory\" inconnue"));

        var dansUnSignal = LireAvec("""
              signals = { { id = "s", kind = "level", quality = "qualified", address = 0XFF4DD4,
                from = { { address = 0XFF8305, action = "BOMB_FIRED" } } } },
            """);
        Assert.Contains(dansUnSignal.Errors, e => e.Contains("cle \"address\" inconnue"));
    }

    [Theory]
    [InlineData("signals = { { id = \"s\", kind = \"gauge\", quality = \"qualified\", from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } } },", "une jauge exige un max")]
    [InlineData("signals = { { id = \"s\", kind = \"level\", quality = \"qualified\", scale = { den = 0 }, from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } } },", "den ne peut pas valoir 0")]
    [InlineData("signals = { { id = \"s\", kind = \"enum\", quality = \"qualified\", values = { { raw = 1, label = \"A\" }, { min = 0, max = 3, label = \"B\" } }, from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } } },", "se recouvrent")]
    [InlineData("signals = { { id = \"s\", kind = \"level\", quality = \"qualified\", min = 3, max = 1, from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } } },", "plus grand que max")]
    [InlineData("signals = { { id = \"s\", kind = \"level\", quality = \"sure\", from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } } },", "quality \"sure\" inconnue")]
    [InlineData("signals = { { id = \"s\", kind = \"level\", quality = \"qualified\", from = { } } },", "au moins une ligne du .MEM")]
    [InlineData("signals = { { id = \"s\", kind = \"level\", quality = \"qualified\", from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } }, { id = \"s\", kind = \"level\", quality = \"qualified\", from = { { address = 0XFF8305, action = \"BOMB_FIRED\" } } } },", "en double")]
    [InlineData("signals = { }, instruments = { { id = \"i\", show = \"counter\", signals = { \"absent\" } } },", "signal \"absent\" non declare")]
    public void Les_regles_du_schema_refusent_un_sens_faux(string corps, string attendu)
    {
        var r = LireAvec(corps);
        Assert.False(r.Valide);
        Assert.Empty(r.Signals);
        Assert.Contains(r.Errors, e => e.Contains(attendu));
    }

    [Theory]
    [InlineData("return { schema = function() end }", "valeur « function » refusee")]
    [InlineData("return { schema = nil }", "valeur « nil » refusee")]
    [InlineData("return { schema = 1.5 }", "nombre decimal refuse")]
    [InlineData("return { schema = 1, schema = 1 }", "en double")]
    [InlineData("local t = {} return t", "doit commencer par « return { »")]
    [InlineData("return { a = \"x\" } os.exit()", "texte apres la table finale")]
    [InlineData("return { a = { { { { { { { { { 1 } } } } } } } } } }", "trop profondes")]
    [InlineData("return { a = \"x\\q\" }", "echappement")]
    [InlineData("return { a = [[long]] }", "valeur attendue")]
    public void Le_lecteur_n_execute_rien_et_refuse_ce_qui_sort_du_sous_ensemble(string texte, string attendu)
    {
        var r = Smem.Lire(texte, MemMinimal);
        Assert.False(r.Valide);
        Assert.Contains(r.Errors, e => e.Contains(attendu));
    }

    [Fact]
    public void Spatial_est_reserve_et_signale()
    {
        var r = Smem.Lire(Entete + "  spatial = { }, signals = { },\n}\n", MemMinimal, "jeu.MEM");
        Assert.True(r.Valide, string.Join(" | ", r.Errors));
        Assert.Contains(r.Warnings, w => w.Contains("spatial est reserve"));
    }

    // ─────────────────────────── fichier et message ───────────────────────────

    [Fact]
    public void Le_SMEM_se_cherche_a_cote_du_MEM_en_vigueur_puis_dans_la_couche_officielle()
    {
        var racine = Path.Combine(Path.GetTempPath(), "smem-" + Guid.NewGuid().ToString("N"));
        try
        {
            var user = Path.Combine(racine, ".user", "arcade");
            var officielle = Path.Combine(racine, "arcade");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(officielle);
            var mem = Path.Combine(user, "jeu.MEM");
            File.WriteAllText(mem, MemMinimal);

            Assert.Null(Smem.Trouver(mem, racine));
            File.WriteAllText(Path.Combine(officielle, "jeu.SMEM"), "return { }");
            Assert.Equal(Path.Combine(officielle, "jeu.SMEM"), Smem.Trouver(mem, racine));
            File.WriteAllText(Path.Combine(user, "jeu.SMEM"), "return { }");
            Assert.Equal(Path.Combine(user, "jeu.SMEM"), Smem.Trouver(mem, racine));
            Assert.Null(Smem.Trouver(null, racine));
        }
        finally
        {
            try { Directory.Delete(racine, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Le_service_publie_deck_semantics_une_fois_et_le_met_dans_l_instantane()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "smem-" + Guid.NewGuid().ToString("N"), "arcade");
        Directory.CreateDirectory(dossier);
        var mem = Path.Combine(dossier, "19xx-the-war-against-destiny.MEM");
        File.Copy(Fichier("19xx-the-war-against-destiny.MEM"), mem);
        File.Copy(Fichier("19xx-the-war-against-destiny.SMEM"), Path.ChangeExtension(mem, ".SMEM"));

        var bus = new SimpleEventBus();
        var publies = new List<EventEnvelope>();
        using var ecoute = bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.semantics") lock (publies) publies.Add(e); });
        var deck = new DeckStateService(bus, new ApiContext());
        await deck.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\19xx.zip" } });
            for (var i = 0; i < 3; i++)
            {
                await bus.PublishAsync(new EventEnvelope
                {
                    Type = "retroarch.memory.changed",
                    Payload = new
                    {
                        Source = "retroarch.wrapper.pipe",
                        SystemId = "arcade",
                        Rom = "19xx-the-war-against-destiny",
                        DefinitionFile = mem,
                        signal = new RetroArchRuntimeSignal { Key = "ACTION.AMMO_GAIN", Channel = "ACTION", Name = "AMMO_GAIN", Address = "0xFF8305", Value = 3 + i, Player = 1 },
                    },
                });
            }

            var s = Assert.Single(publies).Payload as DeckSemantics;
            Assert.NotNull(s);
            Assert.Equal("loaded", s!.Status);
            Assert.Equal(Smem.SchemaDuMessage, s.Schema);
            Assert.Equal(Path.ChangeExtension(mem, ".SMEM"), s.SmemFile);
            Assert.Equal(64, s.SmemSha256!.Length);
            Assert.Equal(10, s.Signals.Count);

            var instantane = deck.Snapshot("connect");
            Assert.Same(s, instantane.Semantics);
            Assert.Equal(instantane.Game!.LaunchId, s.LaunchId);
            using var json = JsonDocument.Parse(deck.SnapshotJson("connect"));
            var sem = json.RootElement.GetProperty("Payload").GetProperty("Semantics");
            Assert.Equal("players.1.resources.bombs", sem.GetProperty("Signals")[0].GetProperty("Id").GetString());
            Assert.Equal("0xFF8305", sem.GetProperty("Signals")[0].GetProperty("From")[0].GetProperty("Address").GetString());

            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            Assert.Null(deck.Snapshot("connect").Semantics);
        }
        finally
        {
            await deck.StopAsync(CancellationToken.None);
            try { Directory.Delete(Path.GetDirectoryName(dossier)!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Sans_SMEM_le_message_dit_absent_et_ne_porte_aucun_signal()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "smem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var mem = Path.Combine(dossier, "jeu.MEM");
            File.WriteAllText(mem, MemMinimal);
            var s = Smem.Semantique("l", "fbneo", "jeu", "jeu", mem, dossier, 4);
            Assert.Equal("absent", s.Status);
            Assert.Empty(s.Signals);
            Assert.Null(s.SmemFile);

            File.WriteAllText(Path.Combine(dossier, "jeu.SMEM"), "return { schema = 2 }");
            var invalide = Smem.Semantique("l", "fbneo", "jeu", "jeu", mem, dossier, 5);
            Assert.Equal("invalid", invalide.Status);
            Assert.Contains(invalide.Errors, e => e.Contains("schema 2 inconnu"));
        }
        finally
        {
            try { Directory.Delete(dossier, true); } catch (IOException) { }
        }
    }
}
