using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

public class SuiviDesRendusTests
{
    private static string Fichier() => Path.Combine(Path.GetTempPath(), "suivi-rendus-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Un_replay_interrompu_trois_fois_est_abandonne()
    {
        var s = new SuiviDesRendus(Fichier());
        Assert.False(s.NoterUnEchec("rp_A"));
        Assert.False(s.EstAbandonne("rp_A"));
        Assert.False(s.NoterUnEchec("rp_A"));
        Assert.True(s.NoterUnEchec("rp_A"));
        Assert.True(s.EstAbandonne("rp_A"));
        Assert.False(s.EstAbandonne("rp_B"));
    }

    [Fact]
    public void Le_compte_survit_a_un_redemarrage_et_une_reussite_l_efface()
    {
        var f = Fichier();
        var s = new SuiviDesRendus(f);
        s.NoterUnEchec("rp_A"); s.NoterUnEchec("rp_A");
        var apres = new SuiviDesRendus(f);
        Assert.True(apres.NoterUnEchec("rp_A"));
        var autre = new SuiviDesRendus(Fichier());
        autre.NoterUnEchec("rp_C");
        autre.NoterUneReussite("rp_C");
        autre.NoterUnEchec("rp_C"); autre.NoterUnEchec("rp_C");
        Assert.False(autre.EstAbandonne("rp_C"));
    }

    [Fact]
    public void Un_replay_abandonne_est_repris_apres_la_duree()
    {
        var maintenant = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var s = new SuiviDesRendus(Fichier(), () => maintenant);
        for (var i = 0; i < SuiviDesRendus.Seuil; i++) s.NoterUnEchec("rp_A");
        Assert.True(s.EstAbandonne("rp_A"));
        maintenant += SuiviDesRendus.Duree + TimeSpan.FromMinutes(1);
        Assert.False(s.EstAbandonne("rp_A"));
        Assert.False(s.NoterUnEchec("rp_A"));
    }
}
