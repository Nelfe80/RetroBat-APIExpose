using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// En 1CC MULTI, chaque joueur garde son mode (decision user 2026-10-20, Tetris TGM2 : le joueur 1 en MASTER, le joueur 2 en
/// NORMAL). Le mode de la machine reste celui du joueur 1, du solo et des jeux a drapeaux.
/// </summary>
public class ModesParJoueurTests
{
    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement Master = Profil("""{"ruleset":"1cc","mode":{"value":1,"default":true}}""");
    private static readonly JsonElement Normal = Profil("""{"ruleset":"1cc-normal","mode":{"value":2}}""");
    private static readonly JsonElement MultiMaster = Profil("""{"ruleset":"1cc-multi","mode":{"value":1,"default":true}}""");
    private static readonly JsonElement MultiNormal = Profil("""{"ruleset":"1cc-multi-normal","mode":{"value":2}}""");
    private static readonly JsonElement[] Profils = { Master, Normal, MultiMaster, MultiNormal };

    private static string? Regle(JsonElement? p) => p?.GetProperty("ruleset").GetString();

    [Fact]
    public void Chaque_joueur_a_son_mode()
    {
        var contexte = ContexteDeJeu.Vide.AvecMode(1).AvecModeDuJoueur(1, 1).AvecModeDuJoueur(2, 2);

        Assert.Equal(1, contexte.PourLeJoueur(1).Mode);
        Assert.Equal(2, contexte.PourLeJoueur(2).Mode);
        Assert.Equal("1cc-multi", Regle(ModesDeJeu.ChoisirProfilMulti(Profils, contexte.PourLeJoueur(1).Mode)));
        Assert.Equal("1cc-multi-normal", Regle(ModesDeJeu.ChoisirProfilMulti(Profils, contexte.PourLeJoueur(2).Mode)));
    }

    [Fact]
    public void Sans_mode_lu_pour_un_joueur_le_mode_de_la_machine_vaut()
    {
        var contexte = ContexteDeJeu.Vide.AvecMode(2);

        Assert.Equal(2, contexte.PourLeJoueur(2).Mode);
        Assert.Same(contexte, contexte.PourLeJoueur(3));
    }

    [Fact]
    public void Le_mode_d_un_joueur_ne_change_pas_celui_de_la_machine()
    {
        var contexte = ContexteDeJeu.Vide.AvecMode(1).AvecModeDuJoueur(2, 2);

        Assert.Equal(1, contexte.Mode);
        Assert.Equal(new FiltreDeMode(1, 1).Garde(contexte), true);
    }

    [Fact]
    public void Un_mode_identique_ne_recree_pas_le_contexte()
    {
        var contexte = ContexteDeJeu.Vide.AvecModeDuJoueur(2, 2);

        Assert.Same(contexte, contexte.AvecModeDuJoueur(2, 2));
    }
}
