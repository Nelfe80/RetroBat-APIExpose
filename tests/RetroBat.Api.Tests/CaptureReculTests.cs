using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-2 (14/11) : 2 785 refus 409 par jour sur <c>agent/videos/upload-session</c>, un toutes les 30 s. Cause : des
/// films sans replay (« fm_ ») sans aucune session de score, que le site ne peut relier a rien (« pas_encore »), redemandes
/// toutes les 2 min pendant 3 h, plusieurs a la fois. Plus de demande pour eux, et un recul pour tous les autres refus.
/// </summary>
public sealed class CaptureReculTests
{
    private static ReplayCaptureService.CaptureGardee Film(string id = "rp_A", bool sansReplay = false, IReadOnlyList<string>? sessions = null)
        => new(id, @"C:\captures\" + id + ".mkv", 0, 0, DateTime.UtcNow, Sessions: sessions, SansReplay: sansReplay);

    [Fact]
    public void Un_film_sans_replay_ni_session_n_est_jamais_demande_au_site_puis_s_oublie()
    {
        var g = Film("fm_A", sansReplay: true, sessions: []);

        // Les dix minutes ou un verdict peut encore le rejoindre, plus une marge : on attend sans rien demander.
        Assert.Null(ReplayCaptureService.FilmSansScore(g, TimeSpan.FromMinutes(5)));
        Assert.Null(ReplayCaptureService.FilmSansScore(g, TimeSpan.FromMinutes(14)));
        // Apres : aucun score ne le rejoindra, le film s'oublie.
        Assert.True(ReplayCaptureService.FilmSansScore(g, TimeSpan.FromMinutes(16)));
    }

    [Fact]
    public void Un_film_sans_replay_avec_une_session_ou_un_film_de_replay_se_demande_normalement()
    {
        Assert.False(ReplayCaptureService.FilmSansScore(Film("fm_B", sansReplay: true, sessions: ["s1"]), TimeSpan.FromMinutes(30)));
        Assert.False(ReplayCaptureService.FilmSansScore(Film("rp_C"), TimeSpan.FromMinutes(30)));
        // Une rencontre MULTIPLAYER passe par son propre chemin.
        Assert.False(ReplayCaptureService.FilmSansScore(Film("mp_D", sansReplay: true), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Chaque_refus_identique_double_l_attente_jusqu_a_une_heure()
    {
        var t = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var g = Film();

        g = ReplayCaptureService.NoterLeRefus(g, "quota", t, TimeSpan.FromMinutes(2));
        Assert.Equal((1, t.AddMinutes(2)), (g.RefusDeSuite, g.ProchainEssaiUtc!.Value));
        g = ReplayCaptureService.NoterLeRefus(g, "quota", t, TimeSpan.FromMinutes(2));
        Assert.Equal((2, t.AddMinutes(4)), (g.RefusDeSuite, g.ProchainEssaiUtc!.Value));
        for (var i = 0; i < 8; i++) g = ReplayCaptureService.NoterLeRefus(g, "quota", t, TimeSpan.FromMinutes(2));
        Assert.Equal(10, g.RefusDeSuite);
        Assert.Equal(t.AddHours(1), g.ProchainEssaiUtc);

        Assert.True(ReplayCaptureService.EnRecul(g, t.AddMinutes(59)));
        Assert.False(ReplayCaptureService.EnRecul(g, t.AddMinutes(61)));
    }

    [Fact]
    public void Un_autre_refus_repart_de_zero()
    {
        var t = DateTime.UtcNow;
        var g = ReplayCaptureService.NoterLeRefus(Film(), "quota", t, TimeSpan.FromMinutes(2));
        g = ReplayCaptureService.NoterLeRefus(g, "quota", t, TimeSpan.FromMinutes(2));

        g = ReplayCaptureService.NoterLeRefus(g, "pas_encore", t, TimeSpan.FromMinutes(2));

        Assert.Equal(1, g.RefusDeSuite);
        Assert.Equal("pas_encore", g.DernierRefus);
    }

    [Fact]
    public void Seuls_les_refus_du_site_font_oublier_un_film()
    {
        Assert.True(ReplayCaptureService.RefusDuSite("quota"));
        Assert.True(ReplayCaptureService.RefusDuSite("youtube : quotaExceeded"));
        Assert.False(ReplayCaptureService.RefusDuSite("site injoignable"));
        Assert.False(ReplayCaptureService.RefusDuSite("borne non appairee"));
        Assert.False(ReplayCaptureService.RefusDuSite("envoi incomplet, repris au prochain passage"));
        Assert.False(ReplayCaptureService.RefusDuSite("http_503"));
        Assert.False(ReplayCaptureService.RefusDuSite(null));
    }

    [Fact]
    public void Une_fiche_d_avant_le_recul_se_relit_sans_recul()
    {
        var json = "{\"ReplayId\":\"rp_A\",\"Fichier\":\"f.mkv\",\"DebutFilm\":0,\"FinFilm\":0,\"CreeeUtc\":\"2026-11-14T10:00:00Z\"}";
        var g = System.Text.Json.JsonSerializer.Deserialize<ReplayCaptureService.CaptureGardee>(json)!;

        Assert.Equal(0, g.RefusDeSuite);
        Assert.False(ReplayCaptureService.EnRecul(g, DateTime.UtcNow));
    }
}
