using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// La collection que la borne ecrit elle-meme : elle doit etre stable d'une synchronisation a
/// l'autre (sinon EmulationStation se recharge pour rien) et ne jamais abimer les collections
/// que le joueur a faites a la main.
/// </summary>
public class EsCustomCollectionWriterTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "es-collection-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FauxReglages _reglages = new();

    private EsCustomCollectionWriter Writer() => new(
        _reglages,
        logger: null,
        collectionsRoot: Path.Combine(_racine, "collections"),
        stateRoot: Path.Combine(_racine, "state"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Ecrit_les_chemins_tries_en_separateur_es()
    {
        var writer = Writer();
        var resultat = writer.Apply("nelfeplay-scoring", new[]
        {
            @"E:\RetroBat\roms\megadrive\Sonic The Hedgehog (Europe).zip",
            @"E:\RetroBat\roms\fbneo\19xx.zip",
        });

        Assert.True(resultat.FileChanged);
        Assert.True(resultat.SettingsChanged);
        Assert.Equal(2, resultat.GameCount);
        Assert.Equal(
            "E:/RetroBat/roms/fbneo/19xx.zip" + Environment.NewLine
            + "E:/RetroBat/roms/megadrive/Sonic The Hedgehog (Europe).zip" + Environment.NewLine,
            File.ReadAllText(writer.ConfigPath("nelfeplay-scoring")));
        Assert.Equal("nelfeplay-scoring", _reglages.Valeur("CollectionSystemsCustom"));
    }

    [Fact]
    public void Deux_synchronisations_identiques_n_ecrivent_rien()
    {
        var writer = Writer();
        var jeux = new[] { @"E:\RetroBat\roms\fbneo\19xx.zip" };
        writer.Apply("nelfeplay-scoring", jeux);
        var ecritureAvant = File.GetLastWriteTimeUtc(writer.ConfigPath("nelfeplay-scoring"));

        var second = writer.Apply("nelfeplay-scoring", jeux);

        Assert.False(second.FileChanged);
        Assert.False(second.SettingsChanged);
        Assert.False(second.Changed);
        Assert.Equal(ecritureAvant, File.GetLastWriteTimeUtc(writer.ConfigPath("nelfeplay-scoring")));
    }

    [Fact]
    public void L_ordre_des_jeux_recus_ne_change_pas_le_fichier()
    {
        var writer = Writer();
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip", "E:/b/2.zip" });
        Assert.False(writer.Apply("nelfeplay-scoring", new[] { "E:/b/2.zip", "E:/a/1.zip" }).Changed);
    }

    [Fact]
    public void Un_doublon_ne_donne_qu_une_ligne()
    {
        var writer = Writer();
        var resultat = writer.Apply("nelfeplay-scoring", new[]
        {
            @"E:\RetroBat\roms\fbneo\19xx.zip",
            "E:/RetroBat/roms/fbneo/19xx.zip",
            "   ",
        });

        Assert.Equal(1, resultat.GameCount);
        Assert.Single(File.ReadAllLines(writer.ConfigPath("nelfeplay-scoring")));
    }

    [Fact]
    public void Une_collection_vide_est_retiree_au_lieu_d_afficher_une_tuile_morte()
    {
        var writer = Writer();
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip" });

        var resultat = writer.Apply("nelfeplay-scoring", Array.Empty<string>());

        Assert.True(resultat.FileChanged);
        Assert.False(File.Exists(writer.ConfigPath("nelfeplay-scoring")));
        Assert.Null(_reglages.Valeur("CollectionSystemsCustom"));
    }

    [Fact]
    public void Le_retrait_preserve_les_collections_du_joueur()
    {
        _reglages.Poser("CollectionSystemsCustom", "sonic,nelfeplay-scoring,Street Fighter");
        var writer = Writer();
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip" });

        writer.Remove("nelfeplay-scoring");

        Assert.Equal("sonic,Street Fighter", _reglages.Valeur("CollectionSystemsCustom"));
    }

    [Fact]
    public void L_inscription_n_altere_pas_l_ordre_des_valeurs_existantes()
    {
        _reglages.Poser("CollectionSystemsCustom", "sonic,mario");
        Writer().Apply("nelfeplay-scoring", new[] { "E:/a/1.zip" });
        Assert.Equal("sonic,mario,nelfeplay-scoring", _reglages.Valeur("CollectionSystemsCustom"));
    }

    [Fact]
    public void Un_fichier_homonyme_du_joueur_n_est_jamais_supprime()
    {
        var writer = Writer();
        var chemin = writer.ConfigPath("nelfeplay-scoring");
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, "E:/mes/jeux/a-moi.zip" + Environment.NewLine);

        var resultat = writer.Remove("nelfeplay-scoring");

        Assert.False(resultat.FileChanged);
        Assert.True(File.Exists(chemin));
        Assert.Equal("E:/mes/jeux/a-moi.zip" + Environment.NewLine, File.ReadAllText(chemin));
    }

    [Fact]
    public void Un_fichier_modifie_a_la_main_est_reecrit_et_la_propriete_est_verifiable()
    {
        var writer = Writer();
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip" });
        Assert.True(writer.Owns("nelfeplay-scoring"));

        File.WriteAllText(writer.ConfigPath("nelfeplay-scoring"), "E:/autre.zip" + Environment.NewLine);
        Assert.False(writer.Owns("nelfeplay-scoring"));

        Assert.True(writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip" }).FileChanged);
        Assert.True(writer.Owns("nelfeplay-scoring"));
    }

    [Fact]
    public void L_etat_de_propriete_dit_ce_qui_a_ete_ecrit()
    {
        var writer = Writer();
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip", "E:/b/2.zip" });

        var etat = writer.ReadState("nelfeplay-scoring");

        Assert.NotNull(etat);
        Assert.Equal("nelfeplay-scoring", etat!.Collection);
        Assert.Equal(2, etat.GameCount);
        Assert.True(etat.ListedInSettings);
        Assert.Matches("^[0-9a-f]{64}$", etat.ContentSha256 ?? string.Empty);
        // L'etat ne vit pas dans l'arborescence d'EmulationStation.
        Assert.DoesNotContain("collections", Path.GetDirectoryName(writer.StatePath("nelfeplay-scoring"))!);
    }

    [Fact]
    public void Un_nom_vide_ne_touche_a_rien()
    {
        var writer = Writer();
        Assert.False(writer.Apply("   ", new[] { "E:/a/1.zip" }).Changed);
        Assert.False(writer.Remove("").Changed);
        Assert.False(Directory.Exists(Path.Combine(_racine, "collections")));
    }

    [Fact]
    public void Aucun_fichier_temporaire_ne_reste_apres_l_ecriture()
    {
        var writer = Writer();
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip" });
        writer.Apply("nelfeplay-scoring", new[] { "E:/a/1.zip", "E:/b/2.zip" });

        Assert.Empty(Directory.GetFiles(Path.Combine(_racine, "collections"), "*.tmp"));
    }

    [Fact]
    public void Sans_cle_de_reglages_le_retrait_ne_cree_rien()
    {
        Assert.False(Writer().Remove("nelfeplay-scoring").SettingsChanged);
        Assert.Null(_reglages.Valeur("CollectionSystemsCustom"));
    }

    /// <summary>es_settings.cfg en memoire : le vrai store ecrit un fichier que le test n'a pas a toucher.</summary>
    private sealed class FauxReglages : IEsSettingsStore
    {
        private readonly XDocument _document = new(new XElement("config"));

        public string? Valeur(string cle) => _document.Root!
            .Elements()
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;

        public void Poser(string cle, string valeur) => _document.Root!.Add(
            new XElement("string", new XAttribute("name", cle), new XAttribute("value", valeur)));

        public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default)
            => update(_document);

        public IReadOnlyDictionary<string, string> ReadAllSettings() => _document.Root!
            .Elements()
            .Where(e => e.Attribute("name") != null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")?.Value ?? string.Empty);

        public EmulationStationSettingsSnapshot ReadSnapshot()
            => new(string.Empty, DateTime.UtcNow, ReadAllSettings());

        public void Invalidate() { }

        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
