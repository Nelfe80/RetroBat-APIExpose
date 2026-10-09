using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'atelier de NelfeScoreLab (APX-LAB-001) : tant qu'il tient, la partie ne produit rien. Le doute
/// fait taire la borne, mais jamais plus de six heures apres l'ecriture du drapeau.
/// </summary>
public class ScoreLabAtelierTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>Un drapeau ecrit a la date donnee (par defaut : maintenant, au sens du test).</summary>
    private static string Drapeau(string contenu, DateTime? ecritLe = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "scorelab-atelier-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, contenu);
        File.SetLastWriteTimeUtc(path, ecritLe ?? Now);
        return path;
    }

    private static void Avec(string path, Action action)
    {
        try { action(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Sans_drapeau_rien_ne_change()
    {
        Assert.Null(ScoreLabAtelier.Lire(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid()), Now, out _));
    }

    [Fact]
    public void Un_drapeau_valide_tient_jusqu_a_son_echeance()
    {
        var path = Drapeau($$"""{"suppress_scoring":true,"expires_utc":"{{Now.AddMinutes(30):O}}","by":"NelfeScoreLab","run_id":"r42"}""");
        Avec(path, () =>
        {
            var etat = ScoreLabAtelier.Lire(path, Now, out var raison);
            Assert.NotNull(etat);
            Assert.Contains("atelier actif", raison);
            Assert.Equal(Now.AddMinutes(30), etat!.ExpiresUtc);
            Assert.Equal("NelfeScoreLab", etat.By);
            Assert.Equal("r42", etat.RunId);
            Assert.Null(ScoreLabAtelier.Lire(path, Now.AddMinutes(31), out var expire));
            Assert.Contains("expire", expire);
        });
    }

    [Fact]
    public void Sans_suppress_scoring_l_atelier_ne_tient_pas()
    {
        var faux = Drapeau($$"""{"suppress_scoring":false,"expires_utc":"{{Now.AddMinutes(30):O}}"}""");
        var absent = Drapeau($$"""{"expires_utc":"{{Now.AddMinutes(30):O}}"}""");
        Avec(faux, () => Assert.Null(ScoreLabAtelier.Lire(faux, Now, out _)));
        Avec(absent, () => Assert.Null(ScoreLabAtelier.Lire(absent, Now, out _)));
    }

    [Fact]
    public void Une_echeance_au_dela_de_six_heures_est_ramenee_a_six_heures()
    {
        var path = Drapeau($$"""{"suppress_scoring":true,"expires_utc":"{{Now.AddDays(3):O}}"}""");
        Avec(path, () =>
        {
            Assert.Equal(Now.AddHours(6), ScoreLabAtelier.Lire(path, Now, out _)!.ExpiresUtc);
            Assert.Null(ScoreLabAtelier.Lire(path, Now.AddHours(6).AddMinutes(1), out _));
        });
    }

    [Fact]
    public void Dans_le_doute_la_borne_se_tait()
    {
        // En cours d'ecriture par le Lab, ou abime : la borne se tait, six heures au plus.
        var illisible = Drapeau("{\"suppress_scoring\":tr");
        Avec(illisible, () =>
        {
            Assert.NotNull(ScoreLabAtelier.Lire(illisible, Now.AddMinutes(5), out var raison));
            Assert.Contains("illisible", raison);
            Assert.Null(ScoreLabAtelier.Lire(illisible, Now.AddHours(6).AddMinutes(1), out _));
        });
        // Sans echeance lisible, le silence demande vaut jusqu'au plafond.
        var sansEcheance = Drapeau("""{"suppress_scoring":true,"expires_utc":"bientot"}""");
        Avec(sansEcheance, () => Assert.Equal(Now.AddHours(6), ScoreLabAtelier.Lire(sansEcheance, Now, out _)!.ExpiresUtc));
    }

    [Fact]
    public void Un_drapeau_oublie_depuis_plus_de_six_heures_ne_fait_plus_rien()
    {
        var path = Drapeau($$"""{"suppress_scoring":true,"expires_utc":"{{Now.AddHours(1):O}}"}""", ecritLe: Now.AddHours(-7));
        Avec(path, () =>
        {
            Assert.Null(ScoreLabAtelier.Lire(path, Now, out var raison));
            Assert.Contains("plus de six heures", raison);
        });
    }

    [Fact]
    public void L_atelier_l_emporte_sur_le_drapeau_de_labo()
    {
        var labo = Path.Combine(Path.GetTempPath(), "scorelab-lab-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(labo, $$"""{"submit_unopened":true,"expires_utc":"{{Now.AddMinutes(30):O}}"}""");
        var atelier = Drapeau($$"""{"suppress_scoring":true,"expires_utc":"{{Now.AddMinutes(30):O}}"}""");
        try
        {
            Assert.False(ScoreLabLabMode.IsActive(labo, atelier, Now, out var raison));
            Assert.Contains("atelier", raison);
            // Sans atelier, le drapeau de labo agit comme avant.
            Assert.True(ScoreLabLabMode.IsActive(labo, atelier + ".absent", Now, out _));
        }
        finally
        {
            File.Delete(labo);
            File.Delete(atelier);
        }
    }
}
