using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES CLONES SANS EPINGLE HORS DE LA COLLECTION (decision du user du 15/11, apres l'audit des ROMs parent). 19XX, ESP Ra.De.,
/// Giga Wing, Ketsui, RayForce, Progear et DaiOuJou epinglent l'EEPROM d'usine d'un set : la borne mettait leurs clones
/// regionaux dans la collection World Scoring, et le site les refusait au prevol. Ils en sortent pour l'avenir, sauf un set
/// deja utilise (un ZIP qui porte un score publie), et le joueur lit la ROM attendue.
/// </summary>
public class ClonesHorsCollectionTests
{
    private static InstalledGame Zip(string set, string? md5 = null, string systeme = "fbneo", string canonique = "arcade")
        => new(systeme, canonique, "jeu", $"E:/roms/{systeme}/{set}.zip", set, md5, null, md5 is not null, null);

    private static OpenGame Jeu(string systeme, string[]? epingles, string[]? publies = null)
        => new() { SystemId = systeme, RomGroup = "jeu", NvramSets = epingles, PublishedMd5 = publies };

    private static string? AucunMd5(string _) => null;

    [Fact]
    public void Sans_epingle_ou_sur_un_index_ancien_rien_ne_change()
    {
        var candidats = new[] { Zip("19xx"), Zip("19xxu") };

        Assert.Same(candidats, NelfePlayScoringCollectionSyncService.SansSetsNonEpingles(candidats, Jeu("arcade", null), AucunMd5).Gardes);
        Assert.Same(candidats, NelfePlayScoringCollectionSyncService.SansSetsNonEpingles(candidats, Jeu("arcade", []), AucunMd5).Gardes);
        Assert.Null(NelfePlayScoringCollectionSyncService.SansSetsNonEpingles(candidats, Jeu("arcade", null), AucunMd5).Refus);
    }

    [Fact]
    public void Une_console_n_est_jamais_concernee()
    {
        var candidats = new[] { Zip("sonic", systeme: "megadrive", canonique: "megadrive") };

        var (gardes, refus) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles(candidats, Jeu("megadrive", ["autre"]), AucunMd5);

        Assert.Same(candidats, gardes);
        Assert.Null(refus);
    }

    [Fact]
    public void Le_clone_regional_sort_et_le_parent_epingle_reste()
    {
        var parent = Zip("19xx");
        var (gardes, refus) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles(
            [Zip("19xxu"), parent, Zip("19xxj")], Jeu("arcade", ["19xx"]), AucunMd5);

        Assert.Equal(new[] { parent }, gardes);
        Assert.Equal("set « 19xxu », « 19xxj » sans EEPROM d'usine épinglée, la partie serait refusée. ROM attendue : « 19xx.zip »", refus);
    }

    [Fact]
    public void Plusieurs_sets_epingles_sont_tous_attendus()
    {
        // RayForce : gunlock et rayforce ont leur epingle, rayforcej (nomme par le .MEM) n'en a pas.
        var (gardes, refus) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles(
            [Zip("rayforcej")], Jeu("arcade", ["rayforce", "gunlock"]), AucunMd5);

        Assert.Empty(gardes);
        Assert.EndsWith("ROM attendue : « gunlock.zip, rayforce.zip »", refus);
    }

    [Fact]
    public void DaiOuJou_garde_la_revision_epinglee_et_ecarte_l_autre()
    {
        // Les deux sont nommes par le .MEM ; seul ddpdojblka a son EEPROM d'usine epinglee (ddpdojblka.fs). Avant, la borne
        // pouvait choisir ddpdojblk par l'ordre alphabetique, et le site refusait.
        var blka = Zip("ddpdojblka");
        var (gardes, _) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles([Zip("ddpdojblk"), blka],
            Jeu("arcade", ["ddpdojblka"]), AucunMd5);

        Assert.Equal(new[] { blka }, gardes);
    }

    [Fact]
    public void Un_set_deja_utilise_reste_par_le_md5_de_son_zip()
    {
        var publie = "0123456789abcdef0123456789abcdef";
        var mesures = 0;
        string? Md5(string chemin)
        {
            mesures++;
            return chemin.EndsWith("19xxu.zip", StringComparison.Ordinal) ? publie : "ffffffffffffffffffffffffffffffff";
        }

        var clone = Zip("19xxu");
        var (gardes, refus) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles([clone, Zip("19xxj")],
            Jeu("arcade", ["19xx"], [publie.ToUpperInvariant()]), Md5);

        Assert.Equal(new[] { clone }, gardes);
        Assert.Contains("« 19xxj »", refus);
        Assert.Equal(2, mesures);
    }

    [Fact]
    public void Le_md5_de_la_gamelist_suffit_et_un_set_epingle_n_est_jamais_relu()
    {
        var publie = "0123456789abcdef0123456789abcdef";
        var (gardes, refus) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles([Zip("19xx"), Zip("19xxu", publie)],
            Jeu("arcade", ["19xx"], [publie]), _ => throw new InvalidOperationException("relu"));

        Assert.Equal(2, gardes.Count);
        Assert.Null(refus);
    }

    [Fact]
    public void Sans_score_publie_aucun_zip_n_est_relu()
    {
        var (gardes, _) = NelfePlayScoringCollectionSyncService.SansSetsNonEpingles([Zip("ket1")], Jeu("arcade", ["ket"], []),
            _ => throw new InvalidOperationException("relu"));

        Assert.Empty(gardes);
    }
}
