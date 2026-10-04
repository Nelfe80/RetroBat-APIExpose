using System.Xml.Linq;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// es_features_apiexpose.cfg (2026-10-04) : nos options d'ES dans leur propre fichier, que ES
/// fusionne avec le sien, libelles deja traduits, sans plus toucher aux fichiers de RetroBat.
/// </summary>
public sealed class EsFeaturesAnnexeTests
{
    private const string Po = """
        # APIExpose es_features translations - fr
        msgid ""
        msgstr ""
        "Language: fr\n"

        msgctxt "game_options"
        msgid "EXTENDED OPTIONS"
        msgstr "OPTIONS ETENDUES"

        msgctxt "game_options"
        msgid "CONTROL PANEL"
        msgstr "PANNEAU DE CONTROLE"

        msgctxt "game_options"
        msgid "ADVANCED SETTINGS"
        msgstr "REGLAGES AVANCES"

        msgctxt "game_options"
        msgid "Choose the panel layout used for this system. "
        "AUTO uses the cabinet button count."
        msgstr "Choisit la disposition du panel. "
        "AUTO suit le nombre de boutons."

        msgctxt "game_options"
        msgid "NOT TRANSLATED"
        msgstr ""
        """;

    [Fact]
    public void Un_po_donne_ses_traductions_lignes_suivies_comprises()
    {
        var t = EsFeaturesAnnexe.LirePo(Po);
        Assert.Equal("OPTIONS ETENDUES", t["EXTENDED OPTIONS"]);
        Assert.Equal("Choisit la disposition du panel. AUTO suit le nombre de boutons.", t["Choose the panel layout used for this system. AUTO uses the cabinet button count."]);
        Assert.False(t.ContainsKey("NOT TRANSLATED"));
        Assert.False(t.ContainsKey(""));
    }

    [Theory]
    [InlineData("fr_FR", "fr")]
    [InlineData("ja", "ja_JP")]
    [InlineData("cs_CZ", "cs_CZ")]
    [InlineData("en_US", null)]
    [InlineData("", null)]
    public void La_langue_d_ES_choisit_son_dossier(string langue, string? attendu)
    {
        Assert.Equal(attendu, EsFeaturesAnnexe.DossierDeLangue(new[] { "cs_CZ", "fr", "ja_JP", "es" }, langue));
    }

    [Fact]
    public void L_annexe_traduit_nos_libelles_et_garde_les_groupes_de_RetroBat()
    {
        var retroBat = XElement.Parse("""
            <features>
              <emulator name="libretro">
                <cores>
                  <core name="fbneo" features="cheevos">
                    <feature name="TATE MODE" group="ADVANCED SETTINGS" value="fbneo-vertical-mode" />
                    <system name="arcade">
                      <feature name="CONTROL PANEL" group="ADVANCED SETTINGS" value="apiexpose_panel_arcade" description="Choose the panel layout used for this system. AUTO uses the cabinet button count.">
                        <choice name="AUTO" value="auto" />
                      </feature>
                    </system>
                  </core>
                </cores>
              </emulator>
            </features>
            """);
        var options = new[] { XElement.Parse("""<feature name="CONTROL PANEL" value="global.apiexpose.test" description="NOT TRANSLATED" />""") };
        var places = new[] { XElement.Parse("""<sharedFeature group="EXTENDED OPTIONS" submenu="" value="global.apiexpose.test" order="900" />""") };

        var annexe = EsFeaturesAnnexe.Construire(options, places, retroBat,
            v => v is not null && v.StartsWith("apiexpose_panel", StringComparison.Ordinal), EsFeaturesAnnexe.LirePo(Po)).Root!;

        // Nos options et nos groupes sont traduits ; un libelle sans traduction reste tel quel.
        Assert.Equal("PANNEAU DE CONTROLE", (string?)annexe.Element("sharedFeatures")!.Element("feature")!.Attribute("name"));
        Assert.Equal("NOT TRANSLATED", (string?)annexe.Element("sharedFeatures")!.Element("feature")!.Attribute("description"));
        Assert.Equal("OPTIONS ETENDUES", (string?)annexe.Element("globalFeatures")!.Element("sharedFeature")!.Attribute("group"));

        // L'option du panel garde ses parents (emulateur, coeurs, coeur, systeme), noms seuls ; le
        // groupe de RetroBat reste en anglais, pour qu'ES le traduise et le fusionne avec le sien.
        var panel = annexe.Element("emulator")!.Element("cores")!.Element("core")!.Element("system")!.Element("feature")!;
        Assert.Equal("libretro", (string?)annexe.Element("emulator")!.Attribute("name"));
        Assert.Equal("fbneo", (string?)annexe.Element("emulator")!.Element("cores")!.Element("core")!.Attribute("name"));
        Assert.Null(annexe.Element("emulator")!.Element("cores")!.Element("core")!.Attribute("features"));
        Assert.Equal("arcade", (string?)panel.Parent!.Attribute("name"));
        Assert.Equal("PANNEAU DE CONTROLE", (string?)panel.Attribute("name"));
        Assert.Equal("ADVANCED SETTINGS", (string?)panel.Attribute("group"));
        Assert.Equal("Choisit la disposition du panel. AUTO suit le nombre de boutons.", (string?)panel.Attribute("description"));

        // Rien de RetroBat ne passe dans l'annexe.
        Assert.DoesNotContain(annexe.Descendants("feature"), f => (string?)f.Attribute("value") == "fbneo-vertical-mode");
    }

    [Fact]
    public void Le_fichier_est_lisible_par_ES()
    {
        var annexe = EsFeaturesAnnexe.Construire(
            new[] { XElement.Parse("""<feature name="A" value="global.apiexpose.a" />""") },
            Array.Empty<XElement>(), null, _ => false, new Dictionary<string, string>());
        var texte = EsFeaturesAnnexe.Serialiser(annexe);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", texte, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual('\uFEFF', texte[0]);
        Assert.Equal("features", XDocument.Parse(texte).Root!.Name.LocalName);
    }
}
