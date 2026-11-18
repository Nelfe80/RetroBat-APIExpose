using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES REGLAGES ALLUMES UNE SEULE FOIS (18/11 nuit) : la poussee du panneau vers le marquee est allumee par defaut, et une
/// migration unique l'allume sur les bornes qui l'avaient eteinte ; elle ne se rejoue jamais.
/// </summary>
public sealed class ReglagesPosesUneFoisTests
{
    private const string Cle = "global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled";

    [Fact]
    public void La_poussee_du_panneau_et_le_Deck_du_direct_sont_allumes_par_defaut()
    {
        var o = new ApiExposeOptions();
        Assert.True(o.DatasThemeExpose.CpoPanelWebSocketPushEnabled);
        Assert.True(o.Leaderboard.ChallengeShareLiveDeck);
        // une cle d'ES vide (switchauto) vaut la valeur d'usine : allumee
        Assert.True(ApiExposeAppsettingsSyncService.TryGetShippedBoolDefault(Cle, out var push) && push);
        Assert.True(ApiExposeAppsettingsSyncService.TryGetShippedBoolDefault("global.apiexpose.challenge.share_live_deck", out var deck) && deck);
    }

    [Fact]
    public void La_migration_allume_la_poussee_une_fois_et_marque_la_chose_faite()
    {
        var racine = new XElement("config", new XElement("string", new XAttribute("name", Cle), new XAttribute("value", "0")));
        var faites = ReglagesPosesUneFois.Appliquer(racine);
        Assert.Single(faites);
        Assert.Equal("1", Valeur(racine, Cle));
        Assert.Equal("1", Valeur(racine, faites[0].Marqueur));

        // le joueur la recoupe : la migration ne revient pas
        racine.Elements().First(e => (string?)e.Attribute("name") == Cle).SetAttributeValue("value", "0");
        Assert.Empty(ReglagesPosesUneFois.Appliquer(racine));
        Assert.Equal("0", Valeur(racine, Cle));
    }

    [Fact]
    public void La_migration_cree_la_cle_absente_et_ne_touche_que_des_cles_apiexpose()
    {
        var racine = new XElement("config", new XElement("string", new XAttribute("name", "ShowFPS"), new XAttribute("value", "0")));
        Assert.Single(ReglagesPosesUneFois.Appliquer(racine));
        Assert.Equal("1", Valeur(racine, Cle));
        Assert.Equal("0", Valeur(racine, "ShowFPS"));
        Assert.All(ReglagesPosesUneFois.Liste, m =>
        {
            Assert.StartsWith("global.apiexpose.", m.Cle);
            Assert.StartsWith("global.apiexpose.", m.Marqueur);
        });
    }

    [Fact]
    public void Au_demarrage_le_service_ecrit_appsettings_puis_ES_et_ne_rejoue_pas_la_migration()
    {
        var store = new FauxReglages();
        store.Poser(Cle, "0");
        var options = new ApiExposeOptions();
        options.DatasThemeExpose.CpoPanelWebSocketPushEnabled = false; // l'appsettings d'une vieille borne
        var ecritures = new List<ApiExposeSettingChange>();
        var service = new ApiExposeSettingsDefaultsHostedService(new Moniteur(options), store)
        {
            EcrireAppsettings = c => { ecritures.AddRange(c); return c.Count; },
        };

        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Single(ecritures);
        Assert.Equal(Cle, ecritures[0].Key);
        Assert.Equal("1", ecritures[0].NewValue);
        Assert.Equal("1", store.Valeur(Cle));
        Assert.Equal("1", store.Valeur("global.apiexpose.challenge.share_live_deck"));

        // le joueur la recoupe, l'API redemarre : rien n'est rejoue
        store.Changer(Cle, "0");
        ecritures.Clear();
        new ApiExposeSettingsDefaultsHostedService(new Moniteur(options), store)
        {
            EcrireAppsettings = c => { ecritures.AddRange(c); return c.Count; },
        }.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Empty(ecritures);
        // (le recopiage d'appsettings vers ES est inchange : la valeur d'appsettings, ici fausse, gagne)
        Assert.Equal("0", store.Valeur(Cle));
    }

    [Fact]
    public void Un_echec_d_appsettings_repousse_la_migration_sans_rien_poser_dans_ES()
    {
        var store = new FauxReglages();
        store.Poser(Cle, "0");
        var options = new ApiExposeOptions();
        options.DatasThemeExpose.CpoPanelWebSocketPushEnabled = false;
        var service = new ApiExposeSettingsDefaultsHostedService(new Moniteur(options), store)
        {
            EcrireAppsettings = _ => throw new IOException("verrouille"),
        };
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(store.Valeur(ReglagesPosesUneFois.Liste[0].Marqueur));
        Assert.Equal("0", store.Valeur(Cle));
    }

    private static string? Valeur(XElement racine, string cle)
        => racine.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == cle)?.Attribute("value")?.Value;

    private sealed class Moniteur(ApiExposeOptions o) : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue => o;
        public ApiExposeOptions Get(string? name) => o;
        public IDisposable? OnChange(Action<ApiExposeOptions, string?> listener) => null;
    }

    private sealed class FauxReglages : IEsSettingsStore
    {
        private readonly XDocument _document = new(new XElement("config"));

        public string? Valeur(string cle) => _document.Root!.Elements()
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;

        public void Poser(string cle, string valeur) => _document.Root!.Add(
            new XElement("string", new XAttribute("name", cle), new XAttribute("value", valeur)));

        public void Changer(string cle, string valeur)
            => _document.Root!.Elements().First(e => e.Attribute("name")?.Value == cle).SetAttributeValue("value", valeur);

        public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default) => update(_document);

        public IReadOnlyDictionary<string, string> ReadAllSettings() => _document.Root!.Elements()
            .Where(e => e.Attribute("name") != null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")?.Value ?? "", StringComparer.OrdinalIgnoreCase);

        public EmulationStationSettingsSnapshot ReadSnapshot() => new("", DateTime.UtcNow, ReadAllSettings());
        public void Invalidate() { }
        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
