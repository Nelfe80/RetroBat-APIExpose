using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Tetris Game Boy type B v2 (depart 9-5 impose) : les valeurs traversees en faisant defiler le menu ne lèvent pas le
/// bandeau « option hors reglement », une valeur gardee le leve, et le verdict lit la derniere valeur (rejeu des journaux
/// de lorenzo 00:37:37 et de neogeoff 23:15:47 du 20/11).
/// </summary>
public class TetrisMenuDefilementTests
{
    private static readonly JsonElement TypeB = JsonDocument.Parse("""
        {"difficulty":{"fields":[
          {"address":"0xFFC3","initial":0,"label":{"fr":"Niveau","en":"Level"},"accept":[9]},
          {"address":"0xFFC4","initial":0,"label":{"fr":"Hauteur","en":"High"},"accept":[5]}]}}
        """).RootElement.Clone();

    private static readonly DateTime T0 = new(2026, 11, 20, 23, 15, 0, DateTimeKind.Utc);

    private static (AvertisseurReglages a, ContexteDeJeu ctx, List<string> avertis) Rejouer(params (double s, string adr, int v)[] lignes)
    {
        var a = new AvertisseurReglages();
        var ctx = ContexteDeJeu.Vide;
        var avertis = new List<string>();
        foreach (var (s, adr, v) in lignes)
        {
            var t = T0.AddSeconds(s);
            avertis.AddRange(a.Echus(t)); // la verification programmee tombe a l'heure dite
            ctx = ctx.AvecDifficulte(adr, v);
            a.Observer([TypeB], adr, v, "fr", t, out _);
        }
        return (a, ctx, avertis);
    }

    private static bool Admis(ContexteDeJeu ctx)
    {
        // Comme le serveur (ScoringRepository::difficulte) : chaque champ signe doit etre dans son « accept ».
        // (NomsRefuses ne compte que les champs a « name » : il ne sert qu'au motif dit au joueur.)
        var d = ModesDeJeu.DifficultePourLePasseport(TypeB, ctx)!;
        foreach (var champ in TypeB.GetProperty("difficulty").GetProperty("fields").EnumerateArray())
        {
            var cle = ModesDeJeu.Adresse(champ.GetProperty("address").GetString()!);
            var v = (int)d[cle]!;
            if (!champ.GetProperty("accept").EnumerateArray().Any(x => x.GetInt32() == v)) return false;
        }
        return true;
    }

    [Fact]
    public void Lorenzo_defilement_niveau_puis_hauteur_puis_depart_9_5_aucun_bandeau_et_verdict_admis()
    {
        var (a, ctx, avertis) = Rejouer(
            (0, "0xFFC3", 5), (0.3, "0xFFC3", 6), (0.6, "0xFFC3", 7), (0.75, "0xFFC3", 8), (0.9, "0xFFC3", 9),
            (2.8, "0xFFC4", 3), (3.1, "0xFFC4", 4), (3.3, "0xFFC4", 5));
        avertis.AddRange(a.Echus(T0.AddSeconds(60))); // le depart : plus rien a dire
        Assert.Empty(avertis);
        Assert.True(Admis(ctx));
    }

    [Fact]
    public void Neogeoff_5_4_9_puis_depart_aucun_bandeau()
    {
        var (a, ctx, avertis) = Rejouer((0, "0xFFC3", 5), (0.3, "0xFFC3", 4), (0.9, "0xFFC3", 9), (1.5, "0xFFC4", 5));
        avertis.AddRange(a.Echus(T0.AddSeconds(60)));
        Assert.Empty(avertis);
        Assert.True(Admis(ctx));
    }

    [Fact]
    public void Une_valeur_hors_reglement_gardee_donne_le_bandeau_une_seule_fois()
    {
        var (a, _, avertis) = Rejouer((0, "0xFFC3", 9), (1, "0xFFC4", 3), (2, "0xFFC4", 2));
        Assert.Empty(avertis);
        var plus_tard = a.Echus(T0.AddSeconds(2 + 3.6));
        Assert.Equal(["Hauteur"], plus_tard);
        a.Observer([TypeB], "0xFFC4", 2, "fr", T0.AddSeconds(10), out _);
        Assert.Empty(a.Echus(T0.AddSeconds(30)));
    }

    [Fact]
    public void Revenir_a_une_valeur_admise_rearme_le_bandeau()
    {
        var (a, _, _) = Rejouer((0, "0xFFC4", 1));
        Assert.Equal(["Hauteur"], a.Echus(T0.AddSeconds(5)));
        a.Observer([TypeB], "0xFFC4", 5, "fr", T0.AddSeconds(6), out var retour);
        Assert.Equal("Hauteur", retour?.Nom);
        a.Observer([TypeB], "0xFFC4", 2, "fr", T0.AddSeconds(7), out _);
        Assert.Equal(["Hauteur"], a.Echus(T0.AddSeconds(11)));
    }

    [Fact]
    public void Streets_of_Rage_2_vies_a_5_gardees_previent_toujours()
    {
        var sor2 = JsonDocument.Parse("""
            {"difficulty":{"fields":[{"address":"0xFD04","initial":4,"accept":[4],"label":{"en":"Difficulty"}}]}}
            """).RootElement.Clone();
        var a = new AvertisseurReglages();
        Assert.True(a.Observer([sor2], "0xFD04", 5, "en", T0, out _));
        Assert.Empty(a.Echus(T0.AddSeconds(1)));
        Assert.Equal(["Difficulty"], a.Echus(T0.AddSeconds(4)));
    }

    [Fact]
    public void Une_valeur_jamais_emise_vaut_la_valeur_initiale_du_profil_donc_refusee()
    {
        // NeoGeoff 23:16:12 : seule 0xFFC4 est emise ; le wrapper n'emet que les CHANGEMENTS (prechauffe de 120 images
        // sans emission), donc un niveau reste a sa valeur de demarrage (0 au boot de Tetris) n'est jamais vu : la
        // difficulte signee prend « initial » (0), hors accept [9] : refus correct, le niveau n'etait pas 9.
        var (_, ctx, _) = Rejouer((0, "0xFFC4", 1), (1, "0xFFC4", 2), (2, "0xFFC4", 5));
        var d = ModesDeJeu.DifficultePourLePasseport(TypeB, ctx)!;
        Assert.Equal(0, (int)d["0xFFC3"]!);
        Assert.False(Admis(ctx));
    }
}
