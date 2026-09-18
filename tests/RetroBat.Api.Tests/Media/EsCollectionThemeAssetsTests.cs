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
        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring.svg"), "<svg>couleur</svg>");
        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring-white.svg"), "<svg>blanc</svg>");
        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring-bg.jpg"), "fond");
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private EsCollectionThemeAssets Assets() => new(logger: null, themesRoot: Themes, sourceRoot: Source, stateRoot: Etat);

    /// <summary>Un thème de la famille Carbon : logos, variante blanche, fonds, declaration.</summary>
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

    /// <summary>Un thème de la famille HyperBat : ses propres dossiers, pas de variante blanche.</summary>
    private string ThemeHyperBat(string nom = "one4all4one-hyperbat")
    {
        var racine = Path.Combine(Themes, nom);
        Directory.CreateDirectory(Path.Combine(racine, "_systemmedia", "_logosyst", "clearlogos"));
        Directory.CreateDirectory(Path.Combine(racine, "_systemmedia", "_fanartsysteme", "fresh"));
        File.WriteAllText(Path.Combine(racine, "collections.info"), "Sonic\n");
        return racine;
    }

    [Fact]
    public void Depose_le_logo_couleur_le_fond_et_la_declaration_sous_carbon()
    {
        var theme = ThemeCarbon();

        var resultat = Assets().Install(Nom);

        Assert.True(resultat.Changed);
        Assert.Equal(2, resultat.Files);
        Assert.Equal(1, resultat.Declarations);
        Assert.Equal("<svg>couleur</svg>", File.ReadAllText(Path.Combine(theme, "art", "logos", "collections", Nom + ".svg")));
        Assert.Equal("fond", File.ReadAllText(Path.Combine(theme, "art", "background", "collections", Nom + ".jpg")));
        Assert.Contains(Nom, File.ReadAllLines(Path.Combine(theme, "collections.info")));
    }

    /// <summary>
    /// Carbon cherche « <nom>.svg » puis « <nom>-w.svg » et garde le dernier trouve : deposer la
    /// variante blanche remplacerait la marque par une silhouette sur le fond sombre du theme.
    /// </summary>
    [Fact]
    public void La_variante_blanche_n_est_jamais_deposee_dans_un_theme()
    {
        var theme = ThemeCarbon();

        Assets().Install(Nom);

        Assert.False(File.Exists(Path.Combine(theme, "art", "logos", "collections", Nom + "-w.svg")));
    }

    /// <summary>Un fichier qu'une version precedente deposait et qui n'est plus voulu s'en va.</summary>
    [Fact]
    public void Un_asset_devenu_obsolete_est_retire()
    {
        var theme = ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);
        var obsolete = Path.Combine(theme, "art", "logos", "collections", Nom + "-w.svg");
        File.Copy(Path.Combine(Source, "nelfeplay-worldscoring-white.svg"), obsolete);
        var manifeste = assets.LireManifeste(Nom)!;
        manifeste.Files.Add(new EsCollectionAssetFile
        {
            Path = obsolete,
            Sha256 = Empreinte(obsolete),
        });
        File.WriteAllText(assets.ManifestePath(Nom),
            System.Text.Json.JsonSerializer.Serialize(manifeste));

        var resultat = assets.Install(Nom);

        Assert.True(resultat.Changed);
        Assert.False(File.Exists(obsolete));
        Assert.True(File.Exists(Path.Combine(theme, "art", "logos", "collections", Nom + ".svg")));
    }

    private static string Empreinte(string chemin)
    {
        using var flux = File.OpenRead(chemin);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(flux)).ToLowerInvariant();
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

        Assert.True(File.Exists(Path.Combine(theme, "_systemmedia", "_logosyst", "clearlogos", Nom + ".svg")));
        Assert.True(File.Exists(Path.Combine(theme, "_systemmedia", "_fanartsysteme", "fresh", Nom + ".jpg")));
        Assert.False(File.Exists(Path.Combine(theme, "_systemmedia", "_logosyst", "clearlogos", Nom + "-w.svg")));
    }

    [Fact]
    public void Tous_les_themes_installes_sont_servis()
    {
        var carbon = ThemeCarbon();
        var hyperbat = ThemeHyperBat();

        var resultat = Assets().Install(Nom);

        Assert.Equal(2, resultat.Declarations);
        Assert.Equal(4, resultat.Files);
        Assert.True(File.Exists(Path.Combine(carbon, "art", "logos", "collections", Nom + ".svg")));
        Assert.True(File.Exists(Path.Combine(hyperbat, "_systemmedia", "_logosyst", "clearlogos", Nom + ".svg")));
    }

    [Fact]
    public void Un_theme_sans_collections_info_ne_casse_rien()
    {
        var theme = ThemeCarbon(avecInfo: false);

        var resultat = Assets().Install(Nom);

        Assert.True(resultat.Changed);
        Assert.Equal(0, resultat.Declarations);
        Assert.True(File.Exists(Path.Combine(theme, "art", "logos", "collections", Nom + ".svg")));
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

        File.WriteAllText(Path.Combine(Source, "nelfeplay-worldscoring.svg"), "<svg>version 2</svg>");
        Assert.True(assets.Install(Nom).Changed);
        Assert.Equal("<svg>version 2</svg>",
            File.ReadAllText(Path.Combine(theme, "art", "logos", "collections", Nom + ".svg")));
    }

    [Fact]
    public void Un_fichier_du_joueur_portant_le_meme_nom_n_est_jamais_ecrase()
    {
        var theme = ThemeCarbon();
        var cible = Path.Combine(theme, "art", "logos", "collections", Nom + ".svg");
        File.WriteAllText(cible, "<svg>le mien</svg>");

        Assets().Install(Nom);

        Assert.Equal("<svg>le mien</svg>", File.ReadAllText(cible));
    }

    [Fact]
    public void Le_retrait_enleve_les_assets_et_la_declaration()
    {
        var theme = ThemeCarbon();
        var assets = Assets();
        assets.Install(Nom);

        var resultat = assets.Remove(Nom);

        Assert.True(resultat.Changed);
        Assert.False(File.Exists(Path.Combine(theme, "art", "logos", "collections", Nom + ".svg")));
        Assert.False(File.Exists(Path.Combine(theme, "art", "background", "collections", Nom + ".jpg")));
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
        var cible = Path.Combine(theme, "art", "logos", "collections", Nom + ".svg");
        File.WriteAllText(cible, "<svg>retouche par le joueur</svg>");

        assets.Remove(Nom);

        Assert.True(File.Exists(cible));
        Assert.Equal("<svg>retouche par le joueur</svg>", File.ReadAllText(cible));
    }

    [Fact]
    public void Le_retrait_sans_manifeste_ne_touche_a_rien()
    {
        var theme = ThemeCarbon();
        var cible = Path.Combine(theme, "art", "logos", "collections", Nom + ".svg");
        File.WriteAllText(cible, "<svg>pose a la main</svg>");

        Assert.False(Assets().Remove(Nom).Changed);
        Assert.True(File.Exists(cible));
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
}
