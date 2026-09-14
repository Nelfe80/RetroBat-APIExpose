using System.Text.Json;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La lecture d'une charge utile de bouton de borne.
///
/// Ce test existe a cause d'un vrai defaut : `JsonElement.TryGetInt32` LEVE une exception quand
/// l'element n'est pas un nombre, au lieu de rendre faux. Le slot d'une DIRECTION est nul par
/// nature ; l'exception faisait retomber l'identite et le systeme du meme coup, et le panneau de
/// classement restait sourd a gauche/droite tout en repondant aux boutons. Une nature verifiee
/// avant la lecture, et c'est regle.
/// </summary>
public class PanelPayloadTests
{
    /// <summary>La lecture telle que les consommateurs de panel.input doivent la faire.</summary>
    private static (string? Identite, string? Systeme, int? Slot) Lire(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var el = doc.RootElement;
        string? Texte(string nom) => el.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Nombre(string nom) => el.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : null;
        return (Texte("Identity"), Texte("System"), Nombre("Slot"));
    }

    // Charges REELLES relevees sur la borne, dans le journal d'APIExpose.
    private const string Direction = """{"Player":1,"Slot":null,"System":"DPAD","Identity":"left","Device":0}""";
    private const string Bouton = """{"Player":1,"Slot":1,"System":null,"Identity":"b","Device":0}""";

    [Fact]
    public void Une_direction_se_lit_malgre_son_slot_nul()
    {
        var (identite, systeme, slot) = Lire(Direction);
        Assert.Equal("left", identite);      // c'est CA qui se perdait
        Assert.Equal("DPAD", systeme);
        Assert.Null(slot);
    }

    [Fact]
    public void Un_bouton_porte_son_slot_et_pas_de_systeme()
    {
        var (identite, systeme, slot) = Lire(Bouton);
        Assert.Equal("b", identite);
        Assert.Null(systeme);
        Assert.Equal(1, slot);
    }

    [Theory]
    [InlineData("""{"Slot":"1"}""")]          // un slot en texte
    [InlineData("""{"Slot":true}""")]
    [InlineData("""{"Slot":{}}""")]
    [InlineData("{}")]
    public void Un_slot_d_une_autre_nature_ne_fait_pas_tomber_la_lecture(string json)
    {
        var (_, _, slot) = Lire(json);
        Assert.Null(slot);
    }
}

/// <summary>
/// La ligne d'un classement, lue depuis la reponse REELLE de la plateforme.
///
/// Deux choses s'y jouent : le champ `world` (maison / salle verifiee / contest), qui donne le
/// pictogramme de la ligne, et la meme prudence que pour le slot - `TryGetInt64` leve sur un
/// element qui n'est pas un nombre, et un score absent ou textuel ne doit pas faire tomber tout
/// le classement.
/// </summary>
public class LeaderboardRowTests
{
    private const string Reponse = """
        {"ok":true,"rows":[
          {"player":"Nelfe80","value":27410,"city":"Paris","country":"FR","venue":"","sealed":true,
           "world":"home","replay":{"id":"rp_ABC"},"at":"2026-09-12T10:00:00Z"},
          {"player":"Autre","value":"18100","city":"","country":"","venue":"Nelfe Station Origin",
           "sealed":false,"world":"station","at":"2026-09-11T10:00:00Z"}
        ]}
        """;

    [Fact]
    public void Le_monde_d_un_record_est_lu()
    {
        var lignes = RetroBat.Api.Leaderboard.LeaderboardClient.Lire(Reponse, "Nelfe80");
        Assert.Equal(2, lignes.Count);
        Assert.Equal("home", lignes[0].Monde);
        Assert.Equal("station", lignes[1].Monde);
        Assert.True(lignes[0].CestMoi);
        Assert.Equal("rp_ABC", lignes[0].ReplayId);
        Assert.True(lignes[0].Scelle);
    }

    [Fact]
    public void Un_score_non_numerique_vaut_zero_sans_perdre_la_ligne()
    {
        var lignes = RetroBat.Api.Leaderboard.LeaderboardClient.Lire(Reponse, "");
        Assert.Equal(27410, lignes[0].Valeur);
        Assert.Equal(0, lignes[1].Valeur);          // "18100" en texte : pas d'exception, pas de ligne perdue
        Assert.Equal("Nelfe Station Origin", lignes[1].Salle);
    }
}
