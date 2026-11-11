using System.Xml.Linq;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'OPTION A (autorisee par le user le 11/11) : une partie NelfePlay lancee par ES coupe les notifications de RetroArch par la
/// cle par jeu <c>systeme["rom"].OnScreenMsg</c>, posee au crochet game-start et rendue des que l'emulateur parait, avec un
/// journal sur disque rendu au demarrage. Rien pour une partie du joueur, aucune autre cle touchee.
/// </summary>
[Collection(NotificationsRetroArchCollection.Nom)]
public sealed class NotificationsDesLancementsEsTests : IDisposable
{
    private const string Script = "fbneo \"E:\\RetroBat\\roms\\fbneo\\19xx.zip\"";
    private const string Cle = "fbneo[\"19xx.zip\"].OnScreenMsg";

    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "notif-es-" + Guid.NewGuid().ToString("N"));
    private readonly FauxReglages _es = new();

    public NotificationsDesLancementsEsTests()
    {
        Directory.CreateDirectory(_dossier);
        // Des reglages du joueur qui ne doivent jamais bouger
        _es.Poser("global.OnScreenMsg", "1");
        _es.Poser("fbneo[\"19xx.zip\"].rewind", "0");
        _es.Poser("mame[\"19xx.zip\"].OnScreenMsg", "1");
    }

    public void Dispose()
    {
        NotificationsRetroArch.PoserPourEssai(false);
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private string Journal => Path.Combine(_dossier, "notifications-es.json");

    private NotificationsDesLancementsEs Service(string? verdict, bool emulateur = true)
        => new(_es, new SimpleEventBus(), null, null, Journal)
        {
            Verdict = _ => verdict,
            EmulateurLa = _ => emulateur,
            Marge = TimeSpan.Zero,
        };

    [Fact]
    public void La_cle_a_la_forme_que_le_lanceur_lit()
    {
        Assert.Equal(Cle, NotificationsDesLancementsEs.CleDuJeu("fbneo", "19xx.zip"));
        // comme le lanceur (Program.cs) : « = » et « # » retires du nom du fichier
        Assert.Equal("nes[\"Mario 1.nes\"].OnScreenMsg", NotificationsDesLancementsEs.CleDuJeu("nes", "Mario #1=.nes"));
    }

    [Fact]
    public void Une_partie_du_joueur_n_ecrit_rien()
    {
        var avant = _es.Texte();
        NotificationsRetroArch.PoserPourEssai(true);
        Assert.False(Service(verdict: null).PoserAvantLeLancement(Script));
        Assert.Equal(avant, _es.Texte());
        Assert.False(File.Exists(Journal));
        Assert.False(NotificationsRetroArch.Coupees);
    }

    [Fact]
    public async Task Une_partie_NelfePlay_pose_la_cle_puis_la_retire_des_que_l_emulateur_parait()
    {
        var avant = _es.Texte();
        var service = Service("collection", emulateur: false);
        Assert.True(service.PoserAvantLeLancement(Script));
        Assert.Equal("0", _es.Valeur(Cle));
        Assert.True(File.Exists(Journal));
        Assert.True(NotificationsRetroArch.Coupees);
        // les autres cles n'ont pas bouge
        Assert.Equal("1", _es.Valeur("global.OnScreenMsg"));
        Assert.Equal("1", _es.Valeur("mame[\"19xx.zip\"].OnScreenMsg"));
        Assert.Equal("0", _es.Valeur("fbneo[\"19xx.zip\"].rewind"));

        service.EmulateurLa = _ => true;
        await service.RendreQuandLEmulateurParaitAsync(DateTime.Now, 1);
        Assert.Null(_es.Valeur(Cle));
        Assert.False(File.Exists(Journal));
        Assert.Equal(avant, _es.Texte());
    }

    [Fact]
    public async Task La_valeur_d_avant_du_joueur_revient()
    {
        _es.Poser(Cle, "1");
        var service = Service("defi");
        Assert.True(service.PoserAvantLeLancement(Script));
        Assert.Equal("0", _es.Valeur(Cle));
        await service.RendreQuandLEmulateurParaitAsync(DateTime.Now, 1);
        Assert.Equal("1", _es.Valeur(Cle));
        Assert.Single(_es.Elements(Cle));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    public void Un_jeu_deja_muet_ne_s_ecrit_pas_mais_ses_messages_vont_au_bandeau(string valeur)
    {
        _es.Poser(Cle, valeur);
        var avant = _es.Texte();
        Assert.False(Service("site").PoserAvantLeLancement(Script));
        Assert.Equal(avant, _es.Texte());
        Assert.False(File.Exists(Journal));
        Assert.True(NotificationsRetroArch.Coupees);
    }

    [Fact]
    public void Une_API_arretee_en_pleine_partie_rend_la_cle_au_demarrage()
    {
        var premiere = Service("collection", emulateur: false);
        Assert.True(premiere.PoserAvantLeLancement(Script));
        // l'API tombe avant le rendu ; au demarrage suivant :
        var suivante = Service(null);
        Assert.True(suivante.Reparer());
        Assert.Null(_es.Valeur(Cle));
        Assert.False(File.Exists(Journal));
        Assert.False(suivante.Reparer());
    }

    [Fact]
    public async Task Le_rendu_d_un_lancement_rate_ne_rend_pas_la_cle_du_suivant()
    {
        // Les rendus lances par la pose attendent (emulateur absent) ; ceux de l'essai, dates du MinValue, le voient aussitot
        var service = Service("collection", emulateur: false);
        service.EmulateurLa = depart => depart == DateTime.MinValue;
        Assert.True(service.PoserAvantLeLancement(Script));          // jeton 1 : l'emulateur n'est jamais venu
        Assert.True(service.PoserAvantLeLancement(Script));          // jeton 2 : la cle du 1 rendue, puis reposee
        Assert.Equal("0", _es.Valeur(Cle));
        await service.RendreQuandLEmulateurParaitAsync(DateTime.MinValue, 1); // le rendu du 1 arrive en retard
        Assert.Equal("0", _es.Valeur(Cle));
        Assert.True(File.Exists(Journal));
        await service.RendreQuandLEmulateurParaitAsync(DateTime.MinValue, 2);
        Assert.Null(_es.Valeur(Cle));
    }

    [Fact]
    public void Un_journal_qui_ne_nomme_pas_cette_fonction_ne_touche_rien()
    {
        File.WriteAllText(Journal, "{\"Cle\":\"global.OnScreenMsg\",\"Avant\":null}");
        var avant = _es.Texte();
        Assert.False(Service(null).Reparer());
        Assert.Equal(avant, _es.Texte());
    }

    [Fact]
    public void Un_lancement_illisible_n_ecrit_rien()
    {
        var avant = _es.Texte();
        Assert.False(Service("collection").PoserAvantLeLancement(""));
        Assert.False(Service("collection").PoserAvantLeLancement("-system \"-bad\\x.zip\""));
        Assert.Equal(avant, _es.Texte());
    }

    [Fact]
    public void Le_verdict_prevu_au_crochet_ne_consomme_pas_l_annonce()
    {
        var partie = new PartieNelfePlayService(new SimpleEventBus(), new MediaRuntimeState());
        Assert.Null(partie.Prevoir(@"E:\RetroBat\roms\fbneo\19xx.zip"));
        partie.AnnoncerLancement("defi");
        Assert.Equal("defi", partie.Prevoir(@"E:\RetroBat\roms\fbneo\19xx.zip"));
        Assert.Equal("defi", partie.Prevoir(@"E:\RetroBat\roms\fbneo\19xx.zip"));
    }

    /// <summary>es_settings.cfg en memoire.</summary>
    private sealed class FauxReglages : IEsSettingsStore
    {
        private readonly XDocument _document = new(new XElement("config"));

        public IEnumerable<XElement> Elements(string cle) => _document.Root!.Elements()
            .Where(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase));

        public string? Valeur(string cle) => Elements(cle).FirstOrDefault()?.Attribute("value")?.Value;

        public void Poser(string cle, string valeur) => _document.Root!.Add(
            new XElement("string", new XAttribute("name", cle), new XAttribute("value", valeur)));

        public string Texte() => string.Join("|", _document.Root!.Elements().Select(e => e.Attribute("name")?.Value + "=" + e.Attribute("value")?.Value));

        public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default) => update(_document);

        public IReadOnlyDictionary<string, string> ReadAllSettings() => _document.Root!.Elements()
            .Where(e => e.Attribute("name") != null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")?.Value ?? "");

        public EmulationStationSettingsSnapshot ReadSnapshot() => new("memoire", DateTime.UtcNow, ReadAllSettings());

        public void Invalidate() { }

        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
