using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La session MULTIPLAYER de la borne contre une plateforme en bouteille : ce qu'elle envoie (le jeu
/// et la cle de contenu de la collection, l'appui du joueur), et ce qu'elle publie (jamais un mot de
/// passe netplay).
/// </summary>
public class MultiplayerSessionServiceTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "mp-session-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NelfePlayOptions _nelfeplay = new() { ShowMultiplayerCollection = true };
    private readonly SimpleEventBus _bus = new();

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<(MultiplayerSessionService Session, Plateforme Plateforme, string Jeu)> MonterAsync(Plateforme? plateforme = null)
    {
        var roms = Path.Combine(_racine, "roms");
        Directory.CreateDirectory(Path.Combine(roms, "fbneo"));
        var jeu = Path.Combine(roms, "fbneo", "mslug3.zip");
        File.WriteAllText(jeu, "rom");
        var catalogue = Path.Combine(_racine, "catalogue.json");
        File.WriteAllText(catalogue, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            cores = new Dictionary<string, object> { ["arcade"] = new { core = "fbneo" } },
            games = new[] { new { id = "arcade/mslug3", system = "arcade", set = "mslug3", name = "Metal Slug 3", family = "coop", players = 2, level = "classique" } },
        }));

        var options = new FauxOptions(new ApiExposeOptions { NelfePlay = _nelfeplay });
        var etat = Path.Combine(_racine, "state");
        var collection = new MultiplayerCollectionSyncService(
            options,
            new MultiplayerCatalogueStore(logger: null, path: catalogue),
            new InstalledGameCatalog(new SansDefinition(), logger: null, romsRoot: roms, ramRoot: Path.Combine(_racine, "ram")),
            new EsCustomCollectionWriter(new FauxReglages(), logger: null, collectionsRoot: Path.Combine(_racine, "collections"), stateRoot: etat),
            new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"), sourceRoot: Path.Combine(_racine, "assets"), stateRoot: etat),
            new MediaRuntimeState(),
            romsRoot: roms,
            emulateurTourne: () => false);
        await collection.SynchroniserAsync("test", CancellationToken.None);

        plateforme ??= new Plateforme();
        var client = new MultiplayerClient(new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance),
            clientDeTest: () => new HttpClient(plateforme) { BaseAddress = new Uri("https://nelfeplay.test") });
        return (new MultiplayerSessionService(client, collection, options, relais: null, bus: _bus), plateforme, jeu);
    }

    [Fact]
    public async Task L_appui_long_envoie_le_jeu_et_le_set_de_la_collection()
    {
        var (session, plateforme, jeu) = await MonterAsync();

        var refus = await session.RejoindreAsync(jeu);

        Assert.Null(refus);
        var join = plateforme.Corps("join");
        Assert.Equal("arcade/mslug3", join.GetProperty("game").GetString());
        Assert.Equal("mslug3", join.GetProperty("content").GetString());
        // Un sondage suit tout de suite : la gameroom s'affiche sans attendre le tic suivant.
        Assert.Equal("r1", Assert.Single(session.Etat.Rooms).Room);
    }

    [Fact]
    public async Task Un_jeu_hors_collection_n_appelle_pas_la_plateforme()
    {
        var (session, plateforme, _) = await MonterAsync();

        Assert.Equal("not_multiplayer", await session.RejoindreAsync(Path.Combine(_racine, "roms", "fbneo", "autre.zip")));
        Assert.Empty(plateforme.Appels);
    }

    [Fact]
    public async Task Un_appui_sur_la_borne_part_au_sondage_suivant_puis_retombe()
    {
        var (session, plateforme, _) = await MonterAsync();

        await _bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed" });
        await session.SonderAsync(CancellationToken.None);
        await session.SonderAsync(CancellationToken.None);

        var sondages = plateforme.Tous("poll");
        Assert.True(sondages[0].GetProperty("input").GetBoolean());
        Assert.False(sondages[1].GetProperty("input").GetBoolean());
    }

    [Fact]
    public async Task L_etat_publie_ne_porte_jamais_le_mot_de_passe_netplay()
    {
        var (session, _, _) = await MonterAsync(new Plateforme(avecSession: true));
        string? publie = null;
        using var abonnement = _bus.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type == MultiplayerSessionService.EvenementEtat) publie = JsonSerializer.Serialize(e.Payload);
        });

        await session.SonderAsync(CancellationToken.None);

        // La session complete reste en memoire pour le lancement...
        Assert.Equal("pw-joueur", session.Etat.Rooms[0].Launch?.Netplay?.PlayerPassword);
        // ...mais rien de ce qui part vers le WebSocket ne la contient.
        Assert.NotNull(publie);
        Assert.DoesNotContain("pw-joueur", publie);
        Assert.Contains("\"netplay_ready\":true", publie);
        Assert.DoesNotContain("pw-joueur", session.Etat.Rooms[0].Launch!.Netplay!.ToString());
    }

    [Fact]
    public async Task Plateforme_injoignable_le_refus_le_dit_et_l_appui_n_est_pas_perdu()
    {
        var (session, plateforme, jeu) = await MonterAsync(new Plateforme(enPanne: true));

        Assert.Equal("offline", await session.RejoindreAsync(jeu));
        await session.SonderAsync(CancellationToken.None);
        Assert.Equal("offline", session.Etat.Erreur);

        plateforme.EnPanne = false;
        await session.SonderAsync(CancellationToken.None);
        Assert.True(plateforme.Tous("poll")[^1].GetProperty("input").GetBoolean());
    }

    [Fact]
    public async Task L_invite_parti_derriere_l_hote_le_dit_a_la_plateforme()
    {
        var plateforme = new Plateforme();
        var client = new MultiplayerClient(new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance),
            clientDeTest: () => new HttpClient(plateforme) { BaseAddress = new Uri("https://nelfeplay.test") });

        await client.QuitterAsync("r1", CancellationToken.None, "host_gone");
        await client.QuitterAsync("r1", CancellationToken.None);

        var departs = plateforme.Tous("leave");
        // 26/10 : sa borne parlait parfois avant celle de l'hote, et il prenait l'abandon a sa place.
        Assert.Equal("host_gone", departs[0].GetProperty("reason").GetString());
        Assert.False(departs[1].TryGetProperty("reason", out _));
    }

    /// <summary>NelfePlay en bouteille : une gameroom r1, et la trace des corps envoyes.</summary>
    private sealed class Plateforme(bool avecSession = false, bool enPanne = false) : HttpMessageHandler
    {
        public bool EnPanne { get; set; } = enPanne;

        public List<(string Action, string Corps)> Appels { get; } = [];

        public JsonElement Corps(string action) => Tous(action).Single();

        public List<JsonElement> Tous(string action)
            => Appels.Where(a => a.Action == action).Select(a => JsonDocument.Parse(a.Corps).RootElement).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (EnPanne)
            {
                throw new HttpRequestException("hors ligne");
            }

            var action = request.RequestUri!.AbsolutePath.Split('/')[^1];
            Appels.Add((action, request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var lancement = avecSession
                ? ", \"launch\": {\"role\": \"guest\", \"seat\": 2, \"players\": 2, \"netplay\": {\"relay\": \"relay.test\", \"port\": 55435, \"session\": \"abc\", \"player_password\": \"pw-joueur\", \"core\": \"FinalBurn Neo\"}}"
                : string.Empty;
            var corps = action switch
            {
                "join" => "{\"ok\": true, \"room\": \"r1\"}",
                "poll" => "{\"ok\": true, \"rooms\": [{\"room\": \"r1\", \"game\": \"arcade/mslug3\", \"state\": \"" + (avecSession ? "playing" : "waiting") + "\", \"max\": 2, \"players\": []" + lancement + "}], \"invitations\": []}",
                _ => "{\"ok\": true}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SansFabrique : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("le test passe son propre client");
    }

    private sealed class SansDefinition : IScoreSlugResolver
    {
        public string? ResolveScoreSlug(string systemId, string? romFileName, string? md5, string? cheevosHash) => null;
    }

    private sealed class FauxOptions(ApiExposeOptions valeur) : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = valeur;

        public ApiExposeOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ApiExposeOptions, string?> listener) => new Rien();

        private sealed class Rien : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FauxReglages : IEsSettingsStore
    {
        private readonly XDocument _document = new(new XElement("config"));

        public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default) => update(_document);

        public IReadOnlyDictionary<string, string> ReadAllSettings() => new Dictionary<string, string>();

        public EmulationStationSettingsSnapshot ReadSnapshot() => new(string.Empty, DateTime.UtcNow, ReadAllSettings());

        public void Invalidate() { }

        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
