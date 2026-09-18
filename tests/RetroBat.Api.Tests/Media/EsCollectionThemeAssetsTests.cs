using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// L'identite visuelle posee dans les themes. Sans la ligne dans collections.info, la
/// collection perd sa tuile et tombe dans le fourre-tout « collections » : c'est le piege
/// principal de ce lot. Et rien de ce que le joueur a mis dans son theme ne doit disparaitre.
/// </summary>
public class EsCollectionThemeAssetsTests : IDisposable
{
    private const string Nom = "nelfeplay-scoring";

    private readonly string _racine = Path.Combine(Path.GetTempPath(), "assets-collection-" + Guid.NewGuid().ToString("N")[..8]);

    private string Themes => Path.Combine(_racine, "themes");

    private string Source => Path.Combine(_racine, "source");

    private string Etat => Path.Combine(_racine, "state");

    public EsCollectionThemeAssetsTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring.png"), "PNG du visuel d'origine");
        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring-bg.jpg"), "fond");
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private EsCollectionThemeAssets Assets() => new(logger: null, themesRoot: Themes, sourceRoot: Source, stateRoot: Etat);

    /// <summary>Un theme de la famille Carbon : logos, fonds, declaration.</summary>
    private string ThemeCarbon(string nom = "es-theme-carbon", bool avecInfo = true, string contenuInfo = "Sonic\nMario\n")
    {
        var racine = Path.Combine(Themes, nom);
        Directory.CreateDirectory(Path.Combine(racine, "art", "logos", "collections"));
        Directory.CreateDirectory(Path.Combine(racine, "art", "background", "collections"));
        if (avecInfo)
        {
            File.WriteAllText(Path.Combine(racine, "collections.info"), contenuInfo);
        }

        return racine;
    }

    /// <summary>Un theme de la famille HyperBat : ses propres dossiers.</summary>
    private string ThemeHyperBat(string nom = "one4all4one-hyperbat")
    {
        var racine = Path.Combine(Themes, nom);
        Directory.CreateDirectory(Path.Combine(racine, "_systemmedia", "_logosyst", "clearlogos"));
        Directory.CreateDirectory(Path.Combine(racine, "_systemmedia", "_fanartsysteme", "fresh"));
        File.WriteAllText(Path.Combine(racine, "collections.info"), "Sonic\n");
        return racine;
    }

    private string Logo(string theme) => Path.Combine(theme, "art", "logos", "collections", Nom + ".png");

    private string Fond(string theme) => Path.Combine(theme, "art", "background", "collections", Nom + ".jpg");

    [Fact]
    public void Depose_le_logo_le_fond_et_la_declaration_sous_carbon()
    {
        var theme = ThemeCarbon();

        var resultat = Assets().Install(Nom);

        Assert.True(resultat.Changed);
        Assert.Equal(2, resultat.Files);
        Assert.Equal(1, resultat.Declarations);
        Assert.Equal("PNG du visuel d'origine", File.ReadAllText(Logo(theme)));
        Assert.Equal("fond", File.ReadAllText(Fond(theme)));
        Assert.Contains(Nom, File.ReadAllLines(Path.Combine(theme, "collections.info")));
    }

    /// <summary>
    /// Carbon essaie « nom.png », « nom.svg », puis les variantes « -w », et garde le dernier
    /// trouve : deux fichiers deposes et c'est le mauvais qui gagne. Un seul suffit.
    /// </summary>
    [Fact]
    public void Un_seul_fichier_de_logo_est_depose()
    {
        var theme = ThemeCarbon();

        Assets().Install(Nom);

        var logos = Directory.GetFiles(Path.Combine(theme, "art", "logos", "collections"));
        Assert.Single(logos);
        Assert.EndsWith(Nom + ".png", logos[0]);
    }

    /// <summary>Un fichier qu'une version precedente deposait et qui n'est plus voulu s'en va.</summary>
    [Fact]
    public void Un_asset_devenu_obsolete_est_retire()
    {
        var theme = ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);
        var obsolete = Path.Combine(theme, "art", "logos", "collections", Nom + ".svg");
        File.WriteAllText(obsolete, "<svg>une version precedente</svg>");
        var manifeste = assets.LireManifeste(Nom)!;
        manifeste.Files.Add(new EsCollectionAssetFile { Path = obsolete, Sha256 = Empreinte(obsolete) });
        File.WriteAllText(assets.ManifestePath(Nom), System.Text.Json.JsonSerializer.Serialize(manifeste));

        var resultat = assets.Install(Nom);

        Assert.True(resultat.Changed);
        Assert.False(File.Exists(obsolete));
        Assert.True(File.Exists(Logo(theme)));
    }

    [Fact]
    public void La_declaration_n_altere_aucune_ligne_existante()
    {
        var theme = ThemeCarbon(contenuInfo: "# commentaire du theme\nSonic\nMario\n");

        Assets().Install(Nom);

        var lignes = File.ReadAllLines(Path.Combine(theme, "collections.info"));
        Assert.Equal("# commentaire du theme", lignes[0]);
        Assert.Equal("Sonic", lignes[1]);
        Assert.Equal("Mario", lignes[2]);
        Assert.Equal(Nom, lignes[^1]);
    }

    [Fact]
    public void Un_theme_hyperbat_recoit_ses_chemins_a_lui()
    {
        var theme = ThemeHyperBat();

        Assets().Install(Nom);

        Assert.True(File.Exists(Path.Combine(theme, "_systemmedia", "_logosyst", "clearlogos", Nom + ".png")));
        Assert.True(File.Exists(Path.Combine(theme, "_systemmedia", "_fanartsysteme", "fresh", Nom + ".jpg")));
    }

    [Fact]
    public void Tous_les_themes_installes_sont_servis()
    {
        var carbon = ThemeCarbon();
        var hyperbat = ThemeHyperBat();

        var resultat = Assets().Install(Nom);

        Assert.Equal(2, resultat.Declarations);
        Assert.Equal(4, resultat.Files);
        Assert.True(File.Exists(Logo(carbon)));
        Assert.True(File.Exists(Path.Combine(hyperbat, "_systemmedia", "_logosyst", "clearlogos", Nom + ".png")));
    }

    [Fact]
    public void Un_theme_sans_collections_info_ne_casse_rien()
    {
        var theme = ThemeCarbon(avecInfo: false);

        var resultat = Assets().Install(Nom);

        Assert.True(resultat.Changed);
        Assert.Equal(0, resultat.Declarations);
        Assert.True(File.Exists(Logo(theme)));
    }

    [Fact]
    public void Un_theme_qui_ne_gere_rien_est_ignore()
    {
        Directory.CreateDirectory(Path.Combine(Themes, "theme-minimal"));

        Assert.False(Assets().Install(Nom).Changed);
    }

    [Fact]
    public void Deux_installations_de_suite_ne_recopient_rien()
    {
        ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);

        Assert.False(assets.Install(Nom).Changed);
    }

    [Fact]
    public void Une_source_mise_a_jour_est_recopiee()
    {
        var theme = ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);

        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring.png"), "PNG version 2");

        Assert.True(assets.Install(Nom).Changed);
        Assert.Equal("PNG version 2", File.ReadAllText(Logo(theme)));
    }

    [Fact]
    public void Un_fichier_du_joueur_portant_le_meme_nom_n_est_jamais_ecrase()
    {
        var theme = ThemeCarbon();
        File.WriteAllText(Logo(theme), "mon propre logo");

        Assets().Install(Nom);

        Assert.Equal("mon propre logo", File.ReadAllText(Logo(theme)));
    }

    [Fact]
    public void Le_retrait_enleve_les_assets_et_la_declaration()
    {
        var theme = ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);

        var resultat = assets.Remove(Nom);

        Assert.True(resultat.Changed);
        Assert.False(File.Exists(Logo(theme)));
        Assert.False(File.Exists(Fond(theme)));
        var lignes = File.ReadAllLines(Path.Combine(theme, "collections.info"));
        Assert.DoesNotContain(Nom, lignes);
        Assert.Contains("Sonic", lignes);
        Assert.Contains("Mario", lignes);
        Assert.DoesNotContain(lignes, ligne => ligne.StartsWith("# Ajoute par APIExpose", StringComparison.Ordinal));
    }

    [Fact]
    public void Un_asset_retouche_depuis_son_depot_survit_au_retrait()
    {
        var theme = ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);
        File.WriteAllText(Logo(theme), "retouche par le joueur");

        assets.Remove(Nom);

        Assert.True(File.Exists(Logo(theme)));
        Assert.Equal("retouche par le joueur", File.ReadAllText(Logo(theme)));
    }

    [Fact]
    public void Le_retrait_sans_manifeste_ne_touche_a_rien()
    {
        var theme = ThemeCarbon();
        File.WriteAllText(Logo(theme), "pose a la main");

        Assert.False(Assets().Remove(Nom).Changed);
        Assert.True(File.Exists(Logo(theme)));
    }

    [Fact]
    public void Le_manifeste_dit_ce_qui_a_ete_pose()
    {
        ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);

        var manifeste = assets.LireManifeste(Nom);

        Assert.NotNull(manifeste);
        Assert.Equal(Nom, manifeste!.Collection);
        Assert.Equal(2, manifeste.Files.Count);
        Assert.All(manifeste.Files, fichier => Assert.Matches("^[0-9a-f]{64}$", fichier.Sha256));
        Assert.Single(manifeste.Declarations);
        Assert.EndsWith("collections.info", manifeste.Declarations[0]);
    }

    [Fact]
    public void Sans_assets_source_rien_n_est_pose()
    {
        ThemeCarbon();
        foreach (var fichier in Directory.GetFiles(Source))
        {
            File.Delete(fichier);
        }

        var resultat = Assets().Install(Nom);

        // La declaration reste utile : elle donne sa tuile a la collection meme sans logo.
        Assert.Equal(0, resultat.Files);
        Assert.Equal(1, resultat.Declarations);
    }

    [Fact]
    public void Une_collection_deja_connue_du_theme_n_est_pas_declaree_deux_fois()
    {
        var theme = ThemeCarbon(contenuInfo: "Sonic\n" + Nom + "\n");

        Assets().Install(Nom);

        Assert.Single(File.ReadAllLines(Path.Combine(theme, "collections.info")),
            ligne => string.Equals(ligne.Trim(), Nom, StringComparison.OrdinalIgnoreCase));
    }

    private static string Empreinte(string chemin)
    {
        using var flux = File.OpenRead(chemin);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(flux)).ToLowerInvariant();
    }
}
