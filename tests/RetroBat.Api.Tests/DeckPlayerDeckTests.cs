using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Deck;
using RetroBat.Api.Hubs;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Ce qui manquait au PlayerDeck pour une vraie partie (08/11) : l'instantane a la connexion, l'identite du jeu au
/// lancement, la correspondance effective des boutons, le cadre de l'image et le cadre impose. Les journaux et les
/// fichiers sont ceux de la borne (19XX sous FBNeo, Super Mario Bros. sous FCEUmm), recopies ici.
/// </summary>
public sealed class DeckPlayerDeckTests
{
    /// <summary>Le journal de RetroArch du 08/11 a 16:05, 19XX sous FBNeo (extrait).</summary>
    private static readonly string[] Journal19xx =
    [
        "[INFO] RetroArch 1.22.2 (Git 69a4f0e)",
        "[INFO] [Core] Loading dynamic libretro core from: \"E:\\RetroBat\\emulators\\retroarch\\core_proxy\\fbneo_libretro.dll\".",
        "[INFO] [Remap] Game-specific remap found at \"E:\\RetroBat\\emulators\\retroarch\\config\\remaps\\FinalBurn Neo\\19xx.rmp\".",
        "[INFO] [Content] Content loading skipped. Implementation will load it on its own.",
        "[INFO] [Environ] SET_ROTATION: \"1\" (90 deg).",
        "[INFO] [Environ] SET_PIXEL_FORMAT: XRGB8888.",
        "[INFO] [Core] Geometry: 384x224, Aspect: 0.750, FPS: 59.63, Sample rate: 44126.20 Hz.",
        "[INFO] [Video] Set video size to: 1920x1200.",
        "[INFO] [Environ] SET_INPUT_DESCRIPTORS.",
        "[INFO] [Content] CRC32: 0xa3ddb605.",
    ];

    /// <summary>Le remappage de 19XX tel que la borne l'a (boutons sans usage gares sur R3).</summary>
    private static readonly string[] Remap19xx =
    [
        "input_libretro_device_p1 = \"1\"",
        "input_player1_analog_dpad_mode = \"0\"",
        "input_player1_btn_l = \"15\"",
        "input_player1_btn_l2 = \"15\"",
        "input_player1_btn_r = \"15\"",
        "input_player1_btn_r2 = \"15\"",
        "input_player1_btn_x = \"15\"",
        "input_player1_btn_y = \"15\"",
        "input_remap_port_p1 = \"0\"",
        "input_turbo_enable = \"false\"",
    ];

    /// <summary>Le dynpanel de 19XX, reduit a ce que le Deck lit (sonde du coeur FBNeo du 06/09).</summary>
    private const string Dynpanel19xx = """
        {
          "rom": "19xx",
          "players": {
            "1": {
              "system_inputs": { "start": { "label": "Start", "color": "White" }, "coin": { "label": "Coin", "color": "White" } },
              "buttons": {
                "1": { "function": "Shoot", "color": "Red", "libretro": { "FinalBurn Neo": { "retropad_id": 0, "retropad_button": "b", "descriptor": "Shot" } } },
                "2": { "function": "Bomb", "color": "Blue", "libretro": { "FinalBurn Neo": { "retropad_id": 8, "retropad_button": "a", "descriptor": "Bomb" } } }
              }
            }
          }
        }
        """;

    // ─────────────────────────── identite au lancement ───────────────────────────

    [Fact]
    public void Le_journal_de_19XX_donne_le_coeur_la_geometrie_la_rotation_et_le_remappage()
    {
        var l = DeckLaunchLog.Lire(Journal19xx);

        Assert.True(l.GeometrieConnue);
        Assert.Equal("fbneo", l.Core);
        Assert.Equal(384, l.BaseWidth);
        Assert.Equal(224, l.BaseHeight);
        Assert.Equal(0.75, l.Aspect!.Value, 3);
        Assert.Equal(59.63, l.Fps!.Value, 2);
        Assert.Equal(1, l.Rotation);
        Assert.Equal("game", l.RemapScope);
        Assert.Equal(@"E:\RetroBat\emulators\retroarch\config\remaps\FinalBurn Neo\19xx.rmp", l.RemapPath);
        Assert.Equal(1920, l.VideoWidth);
        Assert.Equal(1200, l.VideoHeight);
        Assert.Equal("a3ddb605", l.Crc32);
    }

    [Fact]
    public void Un_jeu_tourne_d_un_quart_est_vertical_et_echange_largeur_et_hauteur()
    {
        Assert.Equal((224, 384, "vertical"), DeckLaunchLog.Affichage(384, 224, 1, 0.75));
        Assert.Equal((304, 224, "horizontal"), DeckLaunchLog.Affichage(304, 224, null, 1.333));
    }

    [Fact]
    public void Le_remappage_du_dossier_du_contenu_perd_sa_double_barre()
    {
        var l = DeckLaunchLog.Lire(
        [
            "[INFO] [Remap] Content-dir-specific remap found at \"E:\\RetroBat\\emulators\\retroarch\\config\\remaps\\FCEUmm\\\\nes.rmp\".",
            "[INFO] [Core] Geometry: 256x224, Aspect: 1.306, FPS: 60.10, Sample rate: 48000.00 Hz.",
        ]);
        Assert.Equal("content-dir", l.RemapScope);
        Assert.Equal(@"E:\RetroBat\emulators\retroarch\config\remaps\FCEUmm\nes.rmp", l.RemapPath);
    }

    [Fact]
    public void Un_journal_sans_geometrie_ne_dit_pas_que_le_jeu_est_charge()
    {
        var l = DeckLaunchLog.Lire(Journal19xx.Take(3).ToArray());
        Assert.False(l.GeometrieConnue);
        Assert.Equal("game", l.RemapScope); // le remappage est connu avant le contenu
    }

    // ─────────────────────────── boutons ───────────────────────────

    [Fact]
    public void Les_boutons_de_19XX_tir_et_bombe_et_les_boutons_gares_sont_coupes()
    {
        using var doc = JsonDocument.Parse(Dynpanel19xx);
        var sonde = DeckControls.LireSonde(doc.RootElement, "FinalBurn Neo");
        var systeme = DeckControls.LireBoutonsSysteme(doc.RootElement);
        var boutons = DeckControls.Composer(1, DeckControls.LireRemap(Remap19xx), sonde[1], systeme).ToDictionary(b => b.Identity);

        Assert.Equal("Shoot", boutons["b"].Function);
        Assert.Equal("Shot", boutons["b"].Descriptor);
        Assert.Equal("1", boutons["b"].GameButton);
        Assert.False(boutons["b"].Remapped);
        Assert.Equal("Bomb", boutons["a"].Function);
        Assert.Equal("Blue", boutons["a"].Color);

        foreach (var gare in new[] { "x", "y", "l", "r", "l2", "r2" })
        {
            Assert.True(boutons[gare].Disabled, gare);
            Assert.True(boutons[gare].Remapped, gare);
            Assert.Null(boutons[gare].Function);
        }

        Assert.Equal("Start", boutons["start"].Function);
        Assert.Equal("Coin", boutons["select"].Function);
    }

    [Fact]
    public void Un_remappage_qui_echange_deux_boutons_echange_leurs_fonctions()
    {
        using var doc = JsonDocument.Parse(Dynpanel19xx);
        var sonde = DeckControls.LireSonde(doc.RootElement, "FinalBurn Neo");
        var remap = DeckControls.LireRemap(["input_player1_btn_b = \"8\"", "input_player1_btn_a = \"0\""]);
        var boutons = DeckControls.Composer(1, remap, sonde[1], null).ToDictionary(b => b.Identity);

        Assert.Equal("Bomb", boutons["b"].Function);
        Assert.True(boutons["b"].Remapped);
        Assert.Equal("a", boutons["b"].RetroPad);
        Assert.Equal("Shoot", boutons["a"].Function);
    }

    [Fact]
    public void Sans_sonde_du_coeur_aucune_fonction_n_est_devinee()
    {
        using var doc = JsonDocument.Parse(Dynpanel19xx);
        var sonde = DeckControls.LireSonde(doc.RootElement, "MAME");
        Assert.Empty(sonde);
        var boutons = DeckControls.Composer(1, null, null, null);
        Assert.All(boutons, b => Assert.Null(b.Function));
        Assert.All(boutons, b => Assert.False(b.Remapped));
    }

    [Fact]
    public void Le_nom_de_bibliotheque_vient_du_fichier_info_du_coeur()
    {
        Assert.Equal("FinalBurn Neo", DeckControls.NomDeBibliotheque(["display_name = \"Arcade (FinalBurn Neo)\"", "corename = \"FinalBurn Neo\""]));
        Assert.Null(DeckControls.NomDeBibliotheque(["display_name = \"x\""]));
    }

    // ─────────────────────────── cadre ───────────────────────────

    private static DeckVideoSettings Reglages(params (string Cle, string Valeur)[] cles)
        => DeckVideoSettings.Depuis(cles.ToDictionary(c => c.Cle, c => c.Valeur, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Le_cadre_de_19XX_sur_l_ecran_de_la_borne_est_900_par_1200_a_510()
    {
        var s = Reglages(("aspect_ratio_index", "22"), ("video_scale_integer", "false"));
        var (rect, approche) = DeckViewport.Calculer(s, 1920, 1200, 224, 384, 0.75);
        Assert.Equal(new DeckRect(510, 0, 900, 1200), rect);
        Assert.False(approche);

        (rect, _) = DeckViewport.Calculer(s, 1920, 1080, 224, 384, 0.75);
        Assert.Equal(new DeckRect(555, 0, 810, 1080), rect);
    }

    [Fact]
    public void Un_jeu_4_3_pousse_a_droite_par_le_biais()
    {
        var centre = DeckViewport.Calculer(Reglages(("aspect_ratio_index", "22")), 1920, 1080, 304, 224, 4.0 / 3.0).Rect;
        Assert.Equal(new DeckRect(240, 0, 1440, 1080), centre);
        var droite = DeckViewport.Calculer(Reglages(("aspect_ratio_index", "22"), ("video_viewport_bias_x", "1.000000")), 1920, 1080, 304, 224, 4.0 / 3.0).Rect;
        Assert.Equal(new DeckRect(480, 0, 1440, 1080), droite);
    }

    [Fact]
    public void Le_viewport_personnalise_est_pris_tel_quel()
    {
        var s = Reglages(("aspect_ratio_index", "23"), ("custom_viewport_x", "480"), ("custom_viewport_y", "0"),
            ("custom_viewport_width", "1440"), ("custom_viewport_height", "1080"));
        Assert.Equal("custom", DeckViewport.Mode(s));
        Assert.Equal(new DeckRect(480, 0, 1440, 1080), DeckViewport.Calculer(s, 1920, 1080, 304, 224, 1.333).Rect);
    }

    [Fact]
    public void La_configuration_de_la_borne_se_lit_avec_ses_guillemets()
    {
        var cfg = DeckViewport.LireCfg(["aspect_ratio_index = \"22\"", "# commentaire", "custom_viewport_width = \"1405\"", "video_aspect_ratio = \"1.333300\""]);
        var s = DeckVideoSettings.Depuis(cfg);
        Assert.Equal(22, s.AspectRatioIndex);
        Assert.Equal(1405, s.CustomViewport!.Value.Width);
        Assert.Equal(1.3333, s.VideoAspectRatio!.Value, 4);
        Assert.Equal(0.5, s.BiasX);
    }

    // ─────────────────────────── cadre impose ───────────────────────────

    [Theory]
    [InlineData(0, 0, 10, 100, null, "frame_too_small")]
    [InlineData(-1, 0, 100, 100, null, "frame_outside_screen")]
    [InlineData(1000, 0, 1000, 100, 1920, "frame_outside_screen")]
    [InlineData(0, 0, 1920, 1080, 1920, null)]
    public void Un_cadre_irrecevable_est_refuse_avec_son_motif(int x, int y, int w, int h, int? ecran, string? motif)
        => Assert.Equal(motif, new DeckFramePlan { X = x, Y = y, Width = w, Height = h, ScreenWidth = ecran }.Refus());

    [Fact]
    public void Le_cadre_d_un_Deck_part_avec_sa_connexion_et_le_plus_precis_gagne()
    {
        var store = new DeckFramePlanStore(null);
        var connexion = new object();
        Assert.Null(store.Poser(new DeckFramePlan { X = 0, Y = 0, Width = 800, Height = 600 }, connexion));
        Assert.Null(store.Poser(new DeckFramePlan { SystemId = "fbneo", Rom = "mslug3", X = 480, Y = 0, Width = 1440, Height = 1080 }, connexion));

        Assert.Equal(480, store.Pour("fbneo", "mslug3")!.X);
        Assert.Equal(800, store.Pour("fbneo", "19xx")!.Width);
        Assert.Equal("frame_too_small", store.Poser(new DeckFramePlan { Width = 1, Height = 1 }, connexion));

        Assert.True(store.Oublier(connexion));
        Assert.Null(store.Pour("fbneo", "mslug3"));
    }

    [Fact]
    public void Un_cadre_garde_survit_a_un_redemarrage()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "deck-cadres-" + Guid.NewGuid().ToString("N"));
        var fichier = Path.Combine(dossier, "cadres.json");
        try
        {
            var store = new DeckFramePlanStore(fichier);
            Assert.Null(store.Poser(new DeckFramePlan { SystemId = "fbneo", Rom = "mslug3", X = 480, Y = 0, Width = 1440, Height = 1080 }, connexion: null));
            var relu = new DeckFramePlanStore(fichier);
            Assert.Equal(1440, relu.Pour("fbneo", "mslug3")!.Width);
            Assert.Equal(1, relu.Retirer("fbneo", "mslug3", connexion: null));
            Assert.Null(new DeckFramePlanStore(fichier).Pour("fbneo", "mslug3"));
        }
        finally
        {
            try { Directory.Delete(dossier, true); } catch (IOException) { }
        }
    }

    // ─────────────────────────── instantane ───────────────────────────

    [Fact]
    public void L_instantane_garde_la_derniere_valeur_de_chaque_signal_le_score_et_les_entrees_tenues()
    {
        var bus = new SimpleEventBus();
        var deck = new DeckStateService(bus, new ApiContext());
        deck.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            void Signal(string nom, string adresse, int valeur) => bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe",
                    SystemId = "arcade",
                    Rom = "19xx",
                    DefinitionFile = "resources/ram/arcade/19xx.MEM",
                    signal = new RetroArchRuntimeSignal { Key = "ACTION." + nom, Channel = "ACTION", Name = nom, Address = adresse, Value = valeur, RawValueHex = "0x" + valeur.ToString("X"), Player = 1, Frame = 600 },
                },
            });

            Signal("LOSE_LIFE", "0xFF82EC", 3);
            Signal("BOMB_FIRED", "0xFF8305", 2);
            Signal("LOSE_LIFE", "0xFF82EC", 2);
            // un signal du pont MAME : charge anonyme, joueur en minuscules
            bus.PublishAsync(new EventEnvelope
            {
                Type = "ingame.memory.changed",
                Payload = new { Source = "mame.lua", SystemId = "mame", Rom = "mslug3", signal = new { Key = "AMMO_STATE", Channel = "ACTION", Name = "AMMO_STATE", Address = "0x10EAF5", Value = 9L }, player = 1 },
            });
            bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 1, Score = 123450, SystemId = "arcade", Rom = "19xx", Source = "retroarch.wrapper" } });
            bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed", Payload = new { Player = 1, Slot = (int?)1, System = (string?)null, Identity = "b", Device = 0, EsRole = (string?)null } });
            bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed", Payload = new { Player = 1, Slot = (int?)2, System = (string?)null, Identity = "a", Device = 0, EsRole = (string?)null } });
            bus.PublishAsync(new EventEnvelope { Type = "panel.input.released", Payload = new { Player = 1, Slot = (int?)2, System = (string?)null, Identity = "a", Device = 0, EsRole = (string?)null } });

            var s = deck.Snapshot("connect");
            Assert.Equal(DeckStateService.Schema, s.Schema);
            Assert.Null(s.Game);
            var vies = Assert.Single(s.Signals, x => x.Address == "0xFF82EC");
            Assert.Equal(2, vies.Value);
            Assert.Equal(600, vies.Frame);
            Assert.Equal(2, Assert.Single(s.Signals, x => x.Address == "0xFF8305").Value);
            var munitions = Assert.Single(s.Signals, x => x.Address == "0x10EAF5");
            Assert.Equal(9, munitions.Value);
            Assert.Equal(1, munitions.Player);
            Assert.Equal(123450, Assert.Single(s.Scores).Score);
            var tenue = Assert.Single(s.HeldInputs);
            Assert.Equal("b", tenue.Identity);
            Assert.Equal(1, tenue.Slot);
            Assert.True(s.Revision >= 8);

            using var json = JsonDocument.Parse(deck.SnapshotJson("request"));
            Assert.Equal("deck.snapshot", json.RootElement.GetProperty("Type").GetString());
            Assert.Equal("request", json.RootElement.GetProperty("Payload").GetProperty("Reason").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("Payload").GetProperty("Signals").GetArrayLength());
        }
        finally
        {
            deck.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Un_cadre_demande_par_le_WebSocket_est_accuse_et_refuse_avec_son_motif()
    {
        var deck = new DeckStateService(new SimpleEventBus(), new ApiContext());
        var connexion = new object();
        using var bon = JsonDocument.Parse("""{"Frame":{"SystemId":"fbneo","Rom":"mslug3","X":480,"Y":0,"Width":1440,"Height":1080,"Theme":"metal-slug-3"}}""");
        using var accuse = JsonDocument.Parse(deck.Commande(connexion, "deck.frame.request", bon.RootElement)!);
        Assert.Equal("deck.ack", accuse.RootElement.GetProperty("Type").GetString());
        Assert.True(accuse.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean());
        Assert.Equal(1440, deck.Cadres.Pour("fbneo", "mslug3")!.Width);

        using var mauvais = JsonDocument.Parse("""{"X":0,"Y":0,"Width":2,"Height":2}""");
        using var refus = JsonDocument.Parse(deck.Commande(connexion, "deck.frame.request", mauvais.RootElement)!);
        Assert.False(refus.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean());
        Assert.Equal("frame_too_small", refus.RootElement.GetProperty("Payload").GetProperty("Reason").GetString());

        deck.ConnexionFermee(connexion);
        Assert.Null(deck.Cadres.Pour("fbneo", "mslug3"));
    }

    // ─────────────────────────── WebSocket ───────────────────────────

    [Fact]
    public async Task L_instantane_part_a_la_connexion_du_flux_global_et_de_deck_et_sur_demande()
    {
        var ws = new WebSocketConnectionManager(new OptionsFixes());
        ws.DeckSnapshotJson = raison => "{\"Type\":\"deck.snapshot\",\"Payload\":{\"Reason\":\"" + raison + "\"}}";

        var global = new SocketDEssai();
        await ws.AddSocketAsync(global, "");
        Assert.Contains("\"Reason\":\"connect\"", Assert.Single(global.Envoyes));

        var deck = new SocketDEssai();
        await ws.AddSocketAsync(deck, "deck");
        Assert.Single(deck.Envoyes);

        var score = new SocketDEssai();
        await ws.AddSocketAsync(score, "score");
        Assert.Empty(score.Envoyes);

        await ws.HandleClientMessageAsync(global, "{\"Type\":\"deck.snapshot.request\"}");
        Assert.Contains("\"Reason\":\"request\"", global.Envoyes.Last());
        await ws.HandleClientMessageAsync(global, "pas du json");
        await ws.HandleClientMessageAsync(global, "{\"Type\":\"speedrun.restart\"}");
        Assert.Equal(2, global.Envoyes.Count);
    }

    [Fact]
    public void Le_flux_deck_porte_ce_que_le_Deck_affiche()
    {
        var prefixes = WebSocketConnectionManager.StreamPrefixes["deck"];
        foreach (var attendu in new[] { "deck.", "score.live.", "retroarch.memory.", "ingame.memory.", "panel.input.", "ui.game.started", "ui.game.ended" })
        {
            Assert.Contains(attendu, prefixes);
        }
        Assert.Equal(new[] { "deck.snapshot" }, WebSocketConnectionManager.RetainedTypesByStream["deck"]);
    }

    [Fact]
    public void Le_contrat_asyncapi_documente_le_flux_deck()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "wiki", "asyncapi.yaml"))) dossier = dossier.Parent;
        Assert.NotNull(dossier);
        var contrat = File.ReadAllText(Path.Combine(dossier!.FullName, "wiki", "asyncapi.yaml"));
        // release.ps1 compare les adresses « /ws/<flux> » du contrat a celles que sert l'API
        foreach (var flux in WebSocketConnectionManager.StreamPrefixes.Keys)
        {
            Assert.Contains("address: /ws/" + flux + "\n", contrat.Replace("\r\n", "\n"));
        }
        foreach (var message in new[] { "deck.snapshot", "deck.game", "deck.controls", "deck.frame", "deck.ack", "deck.snapshot.request", "deck.frame.request", "deck.frame.clear" })
        {
            Assert.Contains(message, contrat);
        }
    }

    private sealed class OptionsFixes : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = new();
        public ApiExposeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ApiExposeOptions, string?> listener) => null;
    }

    /// <summary>Un WebSocket ouvert qui garde ce qu'on lui envoie.</summary>
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
            Envoyes.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
