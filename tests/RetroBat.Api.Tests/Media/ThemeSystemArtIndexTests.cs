using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Resolution logic for the carbon/active-theme system logo &amp; fanart index: alias table,
/// "-w" white preference, collection language suffix, active-theme-before-carbon priority,
/// candidate-order, and the fanart differences (no white, no language). Fixture-based,
/// machine-independent.
/// </summary>
public class ThemeSystemArtIndexTests
{
    // A fake theme set: an "active" theme and es-theme-carbon, each with art/logos + art/background.
    private static ThemeSystemArtIndex BuildFixture(MediaTree tree)
    {
        tree.With("active/art/logos/snes.svg")                 // active wins over carbon for snes
            .With("active/art/logos/mame.svg")
            .With("active/art/logos/mame-w.svg")               // white variant preferred
            .With("active/art/logos/atarijaguar.svg")          // reached via alias jaguar
            .With("active/art/logos/auto-verticalarcade.svg")  // collection base (EN)
            .With("active/art/logos/auto-verticalarcade-fr.svg") // collection localized
            .With("active/art/background/snes.jpg")
            .With("active/art/background/atarijaguar.jpg")      // fanart via alias
            .With("es-theme-carbon/art/logos/snes.svg")        // also here (carbon fallback)
            .With("es-theme-carbon/art/logos/carbononly.svg")  // only in carbon
            .With("es-theme-carbon/art/background/carbononly.jpg");

        return ThemeSystemArtIndex.Build(new[]
        {
            Path.Combine(tree.Root, "active"),
            Path.Combine(tree.Root, "es-theme-carbon"),
        });
    }

    [Fact]
    public void Logo_prefers_active_theme_then_falls_back_to_carbon()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith(Path.Combine("active", "art", "logos", "snes.svg"),
            index.ResolveLogoPath(new[] { "snes" }, null));
        Assert.EndsWith(Path.Combine("es-theme-carbon", "art", "logos", "carbononly.svg"),
            index.ResolveLogoPath(new[] { "carbononly" }, null));
    }

    [Fact]
    public void Logo_prefers_the_white_variant()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith("mame-w.svg", index.ResolveLogoPath(new[] { "mame" }, null));
    }

    [Fact]
    public void Logo_resolves_through_the_alias_table()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith("atarijaguar.svg", index.ResolveLogoPath(new[] { "jaguar" }, null));
    }

    [Fact]
    public void First_matching_candidate_in_the_list_wins()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        // The specific candidate comes first: "mame" (present) beats the later "snes".
        Assert.EndsWith("mame-w.svg", index.ResolveLogoPath(new[] { "mame", "snes" }, null));
        // An absent first candidate falls through to the next.
        Assert.EndsWith("snes.svg", index.ResolveLogoPath(new[] { "does-not-exist", "snes" }, null));
    }

    [Theory]
    [InlineData("fr", "auto-verticalarcade-fr.svg")] // localized when present
    [InlineData("ja", "auto-verticalarcade.svg")]    // falls back to base when the language is absent
    [InlineData(null, "auto-verticalarcade.svg")]    // no language -> base
    public void Collection_logo_uses_language_then_base(string? language, string expectedFile)
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith(expectedFile, index.ResolveLogoPath(new[] { "auto-verticalarcade" }, language));
    }

    [Fact]
    public void Fanart_uses_alias_and_ignores_white_and_language()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith(Path.Combine("active", "art", "background", "atarijaguar.jpg"),
            index.ResolveFanartPath(new[] { "jaguar" }));
    }

    [Fact]
    public void Returns_null_for_an_unknown_system()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.Null(index.ResolveLogoPath(new[] { "does-not-exist" }, "fr"));
        Assert.Null(index.ResolveFanartPath(new[] { "does-not-exist" }));
    }

    [Fact]
    public void Collection_logo_and_background_are_found_in_the_collections_folder()
    {
        // Brief du 13/11, point 1.5 : EsCollectionThemeAssets pose le logo World Scoring dans art/logos/collections ;
        // l'index ne lisait que art/logos, et le cache du logo devenait sa propre source.
        using var tree = new MediaTree();
        tree.With("es-theme-carbon/art/logos/snes.svg")
            .With("es-theme-carbon/art/logos/collections/nelfeplay-scoring.png")
            .With("es-theme-carbon/art/background/collections/nelfeplay-scoring.jpg");
        var index = ThemeSystemArtIndex.Build(new[] { Path.Combine(tree.Root, "es-theme-carbon") });

        Assert.EndsWith(Path.Combine("art", "logos", "collections", "nelfeplay-scoring.png"),
            index.ResolveLogoPath(new[] { "nelfeplay-scoring", null, "nelfeplay-scoring", "World Scoring" }, "fr"));
        Assert.EndsWith(Path.Combine("art", "background", "collections", "nelfeplay-scoring.jpg"),
            index.ResolveFanartPath(new[] { "nelfeplay-scoring" }));
    }

    [Fact]
    public void The_system_folder_wins_over_the_collections_folder_for_every_candidate()
    {
        // Un nom de collection du theme (« Batman.svg ») ne prend jamais la place du logo d'un systeme, meme si ce nom
        // passe avant dans la liste des candidats.
        using var tree = new MediaTree();
        tree.With("es-theme-carbon/art/logos/snes.svg")
            .With("es-theme-carbon/art/logos/collections/Super Nintendo.svg");
        var index = ThemeSystemArtIndex.Build(new[] { Path.Combine(tree.Root, "es-theme-carbon") });

        Assert.EndsWith(Path.Combine("art", "logos", "snes.svg"), index.ResolveLogoPath(new[] { "Super Nintendo", "snes" }, null));
    }

    [Fact]
    public void Empty_theme_set_resolves_to_null_without_throwing()
    {
        var index = ThemeSystemArtIndex.Build(Array.Empty<string>());

        Assert.Null(index.ResolveLogoPath(new[] { "snes" }, null));
        Assert.Null(index.ResolveFanartPath(new[] { "snes" }));
    }
}
