using System.Text.Json;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// API-1 (14/11) : <c>records/leaderboard</c> sans <c>ruleset</c> repond pour le mode joue, et liste les classements du jeu.
/// Avant, toujours « 1cc » : le marquee affichait le 1CC d'une partie 1LC ou d'un mode propre au jeu.
/// </summary>
public class ModesDuClassementTests
{
    [Fact]
    public void La_regle_demandee_l_emporte_toujours()
    {
        var (regle, source) = ModesDuClassement.Choisir(" 1lc ", "1cc-master", ["1cc-normal", "1cc-master"]);
        Assert.Equal("1lc", regle);
        Assert.Equal(ModesDuClassement.SourceDemandee, source);
    }

    [Fact]
    public void Sans_demande_le_mode_joue()
    {
        var (regle, source) = ModesDuClassement.Choisir(null, "1cc-master", ["1cc-normal", "1cc-master"]);
        Assert.Equal("1cc-master", regle);
        Assert.Equal(ModesDuClassement.SourceJouee, source);
    }

    [Fact]
    public void Sans_mode_joue_la_regle_principale_du_jeu()
    {
        // TGM2 : l'index met le mode de depart en tete, « 1cc » y nomme Master.
        var (regle, source) = ModesDuClassement.Choisir("", null, ["1lc", "1cc-normal", "1cc"]);
        Assert.Equal("1cc-normal", regle);
        Assert.Equal(ModesDuClassement.SourcePrincipale, source);
    }

    [Fact]
    public void Jeu_inconnu_de_l_index_reste_en_1cc_comme_avant()
    {
        var (regle, source) = ModesDuClassement.Choisir(null, null, []);
        Assert.Equal("1cc", regle);
        Assert.Equal(ModesDuClassement.SourceParDefaut, source);
        Assert.Empty(ModesDuClassement.Liste([], regle, null));
    }

    [Fact]
    public void La_liste_donne_libelle_nom_famille_et_drapeaux()
    {
        var modes = ModesDuClassement.Liste(["1cc", "1cc-power-up", "1lc", "1cc-multi"], "1cc-power-up", "1cc-power-up");
        Assert.Equal(["1cc", "1cc-power-up", "1lc", "1cc-multi"], modes.Select(m => m.Ruleset));
        var pu = modes[1];
        Assert.Equal("1CC-PU", pu.Label);
        Assert.Equal("1CC POWER UP", pu.Name);
        Assert.Equal("1cc", pu.Family);
        Assert.Equal("power-up", pu.Mode);
        Assert.True(pu.Played);
        Assert.True(pu.Selected);
        Assert.False(pu.Principal);
        Assert.True(modes[0].Principal);
        Assert.Equal("1cc-multi", modes[3].Family);
        Assert.Equal("1CC MULTI", modes[3].Label);
    }

    [Fact]
    public void Un_mode_joue_absent_de_l_index_est_ajoute()
    {
        var modes = ModesDuClassement.Liste([], "1cc-master", "1cc-master");
        var seul = Assert.Single(modes);
        Assert.Equal("1cc-master", seul.Ruleset);
        Assert.True(seul.Played);
    }

    [Fact]
    public void Les_champs_json_sont_en_snake_case()
    {
        var json = JsonSerializer.Serialize(ModesDuClassement.Liste(["1cc"], "1cc", null));
        Assert.Contains("\"ruleset\":\"1cc\"", json);
        Assert.Contains("\"label\":\"1CC\"", json);
        Assert.Contains("\"principal\":true", json);
        Assert.Contains("\"played\":false", json);
        Assert.Contains("\"selected\":true", json);
    }

    [Fact]
    public void La_difficulte_affichee_dit_la_valeur_mesuree_ou_celle_du_depart()
    {
        var profil = JsonDocument.Parse("""
            {"ruleset":"1cc","difficulty":{"fields":[
              {"address":"0x1005","name":"DIFFICULTY","initial":3},
              {"address":"0x00FFC4","initial":0},
              {"address":"0xFFC5"}]}}
            """).RootElement.Clone();
        var contexte = ContexteDeJeu.Vide.AvecDifficulte("0xFFC4", 7);
        var champs = ModesDeJeu.DifficulteAffichee(profil, contexte);
        Assert.Equal(2, champs.Count);   // le champ sans valeur initiale ni mesure ne dit rien
        Assert.Equal(new DifficulteAffichee("0x1005", "DIFFICULTY", 3, 3, false), champs[0]);
        Assert.Equal(new DifficulteAffichee("0xFFC4", null, 7, 0, true), champs[1]);
        var json = JsonSerializer.Serialize(champs[0]);
        Assert.Equal("""{"address":"0x1005","name":"DIFFICULTY","value":3,"initial":3,"measured":false}""", json);
    }

    [Fact]
    public void Profil_sans_difficulte_rien_a_afficher()
    {
        var profil = JsonDocument.Parse("""{"ruleset":"1cc"}""").RootElement.Clone();
        Assert.Empty(ModesDeJeu.DifficulteAffichee(profil, ContexteDeJeu.Vide));
    }
}
