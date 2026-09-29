using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un jeu a modes a un classement par mode (Tetris Game Boy : type A, type B), et la difficulte de
/// depart s'affiche a cote du score (2026-09-29). Le signal peut manquer : le profil declare la
/// valeur de demarrage.
/// </summary>
public class ModesDeJeuTests
{
    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement TypeA = Profil("""
        {"ruleset":"type-a","mode":{"value":55,"default":true},
         "difficulty":{"fields":[{"address":"0xFFC2","initial":0}]}}
        """);

    private static readonly JsonElement TypeB = Profil("""
        {"ruleset":"type-b","mode":{"value":119},
         "difficulty":{"fields":[{"address":"0xFFC3","initial":0},{"address":"0xFFC4","initial":0}]}}
        """);

    private static readonly JsonElement SansModes = Profil("""{"ruleset":"1cc"}""");

    private static string? Regle(JsonElement? p) => p?.GetProperty("ruleset").GetString();

    [Fact]
    public void Le_profil_suit_le_mode_joue()
    {
        Assert.Equal("type-a", Regle(ModesDeJeu.ChoisirProfil([TypeA, TypeB], 55)));
        Assert.Equal("type-b", Regle(ModesDeJeu.ChoisirProfil([TypeA, TypeB], 119)));
    }

    [Fact]
    public void Sans_signal_le_mode_de_demarrage()
    {
        Assert.Equal("type-a", Regle(ModesDeJeu.ChoisirProfil([TypeB, TypeA], null)));
    }

    [Fact]
    public void Un_mode_sans_classement_ne_soumet_rien()
    {
        Assert.Null(ModesDeJeu.ChoisirProfil([TypeA, TypeB], 0x42));
        Assert.Null(ModesDeJeu.ChoisirProfil([TypeB], null));
        Assert.Null(ModesDeJeu.ChoisirProfil([], 55));
    }

    [Fact]
    public void Un_jeu_sans_modes_garde_son_profil_quoi_que_dise_la_memoire()
    {
        Assert.Equal("1cc", Regle(ModesDeJeu.ChoisirProfil([SansModes], null)));
        Assert.Equal("1cc", Regle(ModesDeJeu.ChoisirProfil([SansModes], 7)));
    }

    [Fact]
    public void La_difficulte_signee_prend_la_mesure_puis_la_valeur_de_demarrage()
    {
        var ctx = ContexteDeJeu.Vide.AvecMode(119).AvecDifficulte("0X00FFC3", 5);
        var d = ModesDeJeu.DifficultePourLePasseport(TypeB, ctx)!;
        Assert.Equal(5, (int)d["0xFFC3"]!);
        Assert.Equal(0, (int)d["0xFFC4"]!);
        Assert.Equal(2, d.Count);
        Assert.Equal(119, ModesDeJeu.ModePourLePasseport(TypeB, ctx));
    }

    [Fact]
    public void Le_mode_du_profil_vaut_quand_rien_n_a_ete_mesure()
    {
        Assert.Equal(55, ModesDeJeu.ModePourLePasseport(TypeA, ContexteDeJeu.Vide));
        Assert.Null(ModesDeJeu.ModePourLePasseport(SansModes, ContexteDeJeu.Vide));
    }

    [Fact]
    public void Sans_champ_au_profil_la_mesure_part_telle_quelle_pour_le_labo()
    {
        var ctx = ContexteDeJeu.Vide.AvecDifficulte("0xffc2", 3);
        var d = ModesDeJeu.DifficultePourLePasseport(SansModes, ctx)!;
        Assert.Equal(3, (int)d["0xFFC2"]!);
        Assert.Null(ModesDeJeu.DifficultePourLePasseport(SansModes, ContexteDeJeu.Vide));
    }

    [Fact]
    public void Le_contexte_est_celui_du_debut_du_run_retenu()
    {
        var a5 = ContexteDeJeu.Vide.AvecMode(55).AvecDifficulte("0xFFC2", 5);
        var a9 = a5.AvecDifficulte("0xFFC2", 9);   // le jeu reutilise l'octet pour le niveau en cours
        var b0 = ContexteDeJeu.Vide.AvecMode(119);
        // Une partie de type B (0 -> 300), puis une partie de type A (0 -> 1200) dont le niveau
        // monte en jeu : le run retenu est le second, et son niveau est celui du depart.
        var lectures = new List<(long, long)> { (0, 0), (10, 300), (20, 0), (30, 400), (40, 1200) };
        var contextes = new List<ContexteDeJeu> { b0, b0, a5, a5, a9 };
        var run = new List<(long, long)> { (20, 0), (30, 400), (40, 1200) };
        var ctx = ModesDeJeu.ContexteDuRun(lectures, contextes, run, b0);
        Assert.Equal(55, ctx.Mode);
        Assert.Equal(5, ctx.Difficulte["0xFFC2"]);
    }

    [Fact]
    public void Sans_pic_retrouve_le_contexte_courant()
    {
        var courant = ContexteDeJeu.Vide.AvecMode(55);
        var ctx = ModesDeJeu.ContexteDuRun([(0, 10)], [ContexteDeJeu.Vide], [(5, 99)], courant);
        Assert.Same(courant, ctx);
    }
}
