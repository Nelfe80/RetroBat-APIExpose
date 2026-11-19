using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE VAISSEAU ET LE TYPE DE BATTLE GAREGGA SUR CHAQUE SCORE (decision du user du 16/11, sans classement a part). Un seul
/// octet du jeu porte les deux (0x15AA = 16 x vaisseau + 4 x type, banc du 16/11) : le .MEM N+1 y ajoute une ligne
/// GAME_DIFFICULTY, et le profil declare DEUX champs sur la meme adresse (le vaisseau, avec « filter », puis le type). Le
/// mecanisme de la difficulte libre et affichee (29/09) suffit : aucun code d'API. Ces tests le prouvent sur un extrait des
/// profils prepares (temp\garegga_vaisseau\prepare\profils).
/// </summary>
public class VaisseauDeGareggaTests
{
    private static readonly JsonElement Profil = JsonDocument.Parse("""
        {"ruleset": "1cc", "difficulty": {"fields": [
          {"address": "0x15AA", "initial": 0, "filter": "ship", "label": {"fr": "Vaisseau", "en": "Ship"},
           "values": {"0": "Silver Sword", "28": "Grasshopper", "72": "Gain"}},
          {"address": "0x15AA", "initial": 0, "label": {"fr": "Type", "en": "Type"},
           "values": {"0": {"en": "A, normal"}, "28": {"en": "Diving Fox (A+B+C, faster, smaller hitbox)"}, "72": {"en": "C, smaller hitbox"}}}
        ]}}
        """).RootElement.Clone();

    [Fact]
    public void Le_passeport_signe_l_octet_une_fois_pour_les_deux_champs()
    {
        // Le wrapper ecrit 0X15AA ou 0X0015AA : une seule cle.
        var ctx = ContexteDeJeu.Vide.AvecDifficulte("0X0015AA", 28);
        var d = ModesDeJeu.DifficultePourLePasseport(Profil, ctx)!;
        Assert.Single(d);
        Assert.Equal(28, (int)d["0x15AA"]!);
    }

    [Fact]
    public void Sans_signal_la_valeur_de_demarrage_vaut_Silver_Sword_A()
    {
        // Silver Sword au bouton A, choisi sans demo avant : l'octet reste a 0, la ligne « change » ne parle pas.
        var d = ModesDeJeu.DifficultePourLePasseport(Profil, ContexteDeJeu.Vide)!;
        Assert.Equal(0, (int)d["0x15AA"]!);
    }

    [Fact]
    public void Les_deux_champs_s_affichent_avec_la_meme_valeur()
    {
        var champs = ModesDeJeu.DifficulteAffichee(Profil, ContexteDeJeu.Vide.AvecDifficulte("0x15AA", 72));
        Assert.Equal(2, champs.Count);
        Assert.All(champs, c => Assert.Equal(72, c.Valeur));
        Assert.All(champs, c => Assert.True(c.Mesuree));
    }

    [Fact]
    public void Le_vaisseau_d_une_demo_ne_reste_pas_celui_de_la_partie()
    {
        // L'attract ecrit 0x30 (Wild Snail) apres un game over ; le joueur prend Silver Sword A : la ligne parle (0x30 -> 0),
        // et le contexte du run est celui de la premiere montee du score, apres le choix.
        var demo = ContexteDeJeu.Vide.AvecDifficulte("0x15AA", 0x30);
        var choix = demo.AvecDifficulte("0x15AA", 0);
        var lectures = new List<(long, long)> { (0, 0), (100, 500), (200, 0), (300, 0), (400, 1200) };
        var contextes = new List<ContexteDeJeu> { ContexteDeJeu.Vide, demo, demo, choix, choix };
        var run = new List<(long, long)> { (300, 0), (400, 1200) };
        var ctx = ModesDeJeu.ContexteDuRun(lectures, contextes, run, choix);
        Assert.Equal(0, ctx.Difficulte["0x15AA"]);
    }

    // 19/11 : un champ sans « accept » est LIBRE (le serveur ne refuse que par « accept »). Avant, seule « initial » passait :
    // changer de vaisseau annoncait « Option du jeu changee : score non classe » alors que le score etait classe.
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x30)]
    [InlineData(0x72)]
    [InlineData(0)]
    public void Changer_de_vaisseau_n_annonce_aucune_option_hors_reglement(int valeur)
    {
        Assert.False(ReglagesDuMenu.HorsReglement([Profil], "0x15AA", valeur, "fr", out var nom));
        Assert.Equal("", nom);
        var d = ModesDeJeu.DifficultePourLePasseport(Profil, ContexteDeJeu.Vide.AvecDifficulte("0x15AA", valeur))!;
        Assert.Empty(ReglagesDuMenu.NomsRefuses(Profil, d));
    }

    [Fact]
    public void Un_champ_a_accept_parle_toujours_hors_de_ses_valeurs()
    {
        // Streets of Rage 2 (4ddf0a7a, 17/11) : vies et difficulte imposees par « accept ».
        var sor2 = JsonDocument.Parse("""
            {"difficulty":{"fields":[{"address":"0xFD04","initial":4,"accept":[4],"label":{"en":"Difficulty"}}]}}
            """).RootElement.Clone();
        Assert.True(ReglagesDuMenu.HorsReglement([sor2], "0xFD04", 6, "en", out var nom));
        Assert.Equal("Difficulty", nom);
        Assert.False(ReglagesDuMenu.HorsReglement([sor2], "0xFD04", 4, "en", out _));
    }
}
