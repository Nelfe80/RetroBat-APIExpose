using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le drapeau du laboratoire : il ne doit jamais faire soumettre une borne sans echeance, ni au-dela
/// de six heures, ni quand il est abime.
/// </summary>
public class ScoreLabLabModeTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc);

    private static string Flag(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "scorelab-flag-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Sans_drapeau_rien_ne_part()
    {
        Assert.False(ScoreLabLabMode.IsActive(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid()), Now, out _));
    }

    [Fact]
    public void Un_drapeau_valide_ouvre_la_soumission_jusqu_a_son_echeance()
    {
        var path = Flag($$"""{"submit_unopened":true,"expires_utc":"{{Now.AddMinutes(30):O}}"}""");
        try
        {
            Assert.True(ScoreLabLabMode.IsActive(path, Now, out var reason));
            Assert.Contains("labo actif", reason);
            Assert.False(ScoreLabLabMode.IsActive(path, Now.AddMinutes(31), out var expired));
            Assert.Contains("expire", expired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Une_echeance_au_dela_de_six_heures_est_refusee()
    {
        var path = Flag($$"""{"submit_unopened":true,"expires_utc":"{{Now.AddHours(7):O}}"}""");
        try
        {
            Assert.False(ScoreLabLabMode.IsActive(path, Now, out var reason));
            Assert.Contains("six heures", reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{"expires_utc":"2026-09-15T21:00:00Z"}""")]
    [InlineData("""{"submit_unopened":false,"expires_utc":"2026-09-15T21:00:00Z"}""")]
    [InlineData("""{"submit_unopened":true}""")]
    [InlineData("pas du json")]
    public void Un_drapeau_incomplet_ou_abime_ne_fait_rien_soumettre(string json)
    {
        var path = Flag(json);
        try
        {
            Assert.False(ScoreLabLabMode.IsActive(path, Now, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Le_profil_de_substitution_porte_ce_que_le_passeport_lit()
    {
        var profile = ScoreLabLabMode.PlaceholderProfile();

        Assert.Equal("1cc", profile.GetProperty("ruleset").GetString());
        Assert.Equal(1, profile.GetProperty("profile_version").GetInt64());
        Assert.Equal(string.Empty, profile.GetProperty("profile_document_sha256").GetString());
        Assert.Equal("libretro", profile.GetProperty("engine").GetString());
        Assert.Equal("score", profile.GetProperty("metric").GetProperty("type").GetString());
    }
}
