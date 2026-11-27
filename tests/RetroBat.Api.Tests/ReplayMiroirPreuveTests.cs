using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Sharing;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Un replay n'est « mirrored » que sur preuve (27/11/2026).</summary>
public class ReplayMiroirPreuveTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private static ReplayLocalMetadata Meta(string etat, int? rang = null, DateTime? verifie = null, int? renvois = null)
        => ReplayLocalMetadata.Fresh("rp_1") with { PublicationState = etat, Visibility = "public", Rank = rang, MirrorCheckedAt = verifie, ResendCount = renvois };

    [Fact]
    public void L_inscription_donne_queued_jamais_mirrored()
    {
        var apres = ReplayMiroirPreuve.Annoncer(ReplayLocalMetadata.Fresh("rp_1"));
        Assert.Equal("queued", apres.PublicationState);
        Assert.Equal("public", apres.Visibility);
        Assert.False(ReplayMiroirPreuve.EstMiroirProuve(apres));
    }

    [Fact]
    public void Un_replay_deja_prouve_n_est_pas_retrograde_par_une_nouvelle_inscription()
    {
        var prouve = ReplayMiroirPreuve.Prouve(Meta("queued"), Now);
        Assert.Equal("mirrored", ReplayMiroirPreuve.Annoncer(prouve).PublicationState);
    }

    [Fact]
    public void Le_head_reussi_pose_mirrored_et_les_dates()
    {
        var p = ReplayMiroirPreuve.Prouve(Meta("queued"), Now);
        Assert.Equal("mirrored", p.PublicationState);
        Assert.Equal(Now, p.MirroredAt);
        Assert.Equal(Now, p.MirrorCheckedAt);
        Assert.True(ReplayMiroirPreuve.EstMiroirProuve(p));
        // une seconde preuve garde la date d'origine
        var p2 = ReplayMiroirPreuve.Prouve(p, Now.AddDays(2));
        Assert.Equal(Now, p2.MirroredAt);
        Assert.Equal(Now.AddDays(2), p2.MirrorCheckedAt);
    }

    [Fact]
    public void Un_ancien_meta_mirrored_sans_date_reste_lisible_mais_non_prouve_et_a_revoir()
    {
        var json = "{\"Schema\":\"nelfe.replay.local-meta.v1\",\"ReplayId\":\"rp_1\",\"Visibility\":\"public\",\"Pinned\":false,\"PublicationState\":\"mirrored\",\"LastAccessAt\":\"2026-11-01T00:00:00Z\",\"CreatedByThisDevice\":true}";
        var meta = JsonSerializer.Deserialize<ReplayLocalMetadata>(json)!;
        Assert.Equal("mirrored", meta.PublicationState);
        Assert.Null(meta.MirrorCheckedAt);
        Assert.False(ReplayMiroirPreuve.EstMiroirProuve(meta));
        Assert.True(ReplayMiroirPreuve.Due(meta, Now));
    }

    [Fact]
    public void Une_revision_par_jour_au_plus_et_seulement_pour_les_mirrored()
    {
        Assert.False(ReplayMiroirPreuve.Due(Meta("mirrored", verifie: Now.AddHours(-23)), Now));
        Assert.True(ReplayMiroirPreuve.Due(Meta("mirrored", verifie: Now.AddHours(-24)), Now));
        Assert.False(ReplayMiroirPreuve.Due(Meta("queued"), Now));
        Assert.False(ReplayMiroirPreuve.Due(Meta("off_mirror"), Now));
        Assert.False(ReplayMiroirPreuve.Due(null, Now));
    }

    [Fact]
    public void Le_budget_borne_le_passage_et_les_non_verifies_passent_d_abord()
    {
        var cands = Enumerable.Range(0, 40)
            .Select(i => ($"rp_{i:D2}", new string('a', 64), (ReplayLocalMetadata?)Meta("mirrored", verifie: i < 3 ? null : Now.AddDays(-2).AddMinutes(i))))
            .ToList();
        var choisis = ReplayMiroirPreuve.ChoisirARevoir(cands, Now);
        Assert.Equal(ReplayMiroirPreuve.BudgetParPassage, choisis.Count);
        Assert.Equal(new[] { "rp_00", "rp_01", "rp_02", "rp_03", "rp_04" }, choisis.Select(c => c.ReplayId));
    }

    [Fact]
    public void Un_objet_absent_que_le_site_veut_repasse_en_a_renvoyer()
    {
        var (meta, renvoyer) = ReplayMiroirPreuve.Appliquer(Meta("mirrored", rang: 3), VerdictMiroir.Absent, objetDetenu: true, Now);
        Assert.True(renvoyer);
        Assert.Equal("to_resend", meta.PublicationState);
        Assert.Equal(1, meta.ResendCount);
        Assert.False(ReplayMiroirPreuve.EstMiroirProuve(meta));
    }

    [Theory]
    [InlineData(11, true)]   // rang 11 : retire par regle, rp_06H14TQNS0Y2679AZ8C8GP2JMW
    [InlineData(null, true)] // rang inconnu : on ne renvoie pas
    public void Un_objet_que_le_site_a_retire_par_regle_n_est_pas_renvoye(int? rang, bool detenu)
    {
        var (meta, renvoyer) = ReplayMiroirPreuve.Appliquer(Meta("mirrored", rang: rang), VerdictMiroir.Absent, detenu, Now);
        Assert.False(renvoyer);
        Assert.Equal("off_mirror", meta.PublicationState);
        Assert.Equal(Now, meta.MirrorCheckedAt);
    }

    [Fact]
    public void Sans_l_objet_sur_la_borne_pas_de_renvoi_et_le_plafond_de_renvois_s_applique()
    {
        Assert.False(ReplayMiroirPreuve.Appliquer(Meta("mirrored", rang: 1), VerdictMiroir.Absent, objetDetenu: false, Now).Renvoyer);
        Assert.False(ReplayMiroirPreuve.Appliquer(Meta("mirrored", rang: 1, renvois: ReplayMiroirPreuve.RenvoisMax), VerdictMiroir.Absent, true, Now).Renvoyer);
    }

    [Fact]
    public void Une_reponse_inexploitable_ne_change_rien()
    {
        var avant = Meta("mirrored", rang: 1, verifie: Now.AddDays(-3));
        var (apres, renvoyer) = ReplayMiroirPreuve.Appliquer(avant, VerdictMiroir.Inconnu, true, Now);
        Assert.Same(avant, apres);
        Assert.False(renvoyer);
    }

    [Fact]
    public void Un_head_reussi_remet_mirrored_apres_un_renvoi()
    {
        var (aRenvoyer, _) = ReplayMiroirPreuve.Appliquer(Meta("mirrored", rang: 1), VerdictMiroir.Absent, true, Now);
        var p = ReplayMiroirPreuve.Prouve(aRenvoyer, Now.AddHours(1));
        Assert.Equal("mirrored", p.PublicationState);
        Assert.Equal(Now.AddHours(1), p.MirroredAt);
    }

    [Fact]
    public void Classer_distingue_present_absent_inconnu()
    {
        Assert.Equal(VerdictMiroir.Present, ReplayMiroirPreuve.Classer(new int?[] { 404, 200 }));
        Assert.Equal(VerdictMiroir.Absent, ReplayMiroirPreuve.Classer(new int?[] { 404, 404 }));
        Assert.Equal(VerdictMiroir.Absent, ReplayMiroirPreuve.Classer(new int?[] { 404, 410 }));
        Assert.Equal(VerdictMiroir.Inconnu, ReplayMiroirPreuve.Classer(new int?[] { 404, null }));
        Assert.Equal(VerdictMiroir.Inconnu, ReplayMiroirPreuve.Classer(new int?[] { 503, 404 }));
        Assert.Equal(VerdictMiroir.Inconnu, ReplayMiroirPreuve.Classer(new int?[] { 429 }));
        Assert.Equal(VerdictMiroir.Inconnu, ReplayMiroirPreuve.Classer(new int?[] { 403, 404 }));
        Assert.Equal(VerdictMiroir.Inconnu, ReplayMiroirPreuve.Classer(Array.Empty<int?>()));
    }

    // ---- Preuve de presence sur GitHub OU GitLab (27/11/2026, soir) ----
    private const string Sha = "abcdef0123456789";
    private const string Gh = "https://github.com/x/objects/{sha}.replay";
    private const string Gl = "https://gitlab.com/x/objects/{sha}.replay";

    private static async Task<(VerdictMiroir v, List<string> appels)> Verifier(Func<string, int?> reponse)
    {
        var appels = new List<string>();
        var v = await ReplayTransitPublisher.VerifierSurLesSourcesAsync(new[] { Gh, Gl }, Sha, u => { appels.Add(u); return Task.FromResult(reponse(u)); });
        return (v, appels);
    }

    [Fact]
    public async Task GitHub_repond_aucune_requete_GitLab()
    {
        var (v, appels) = await Verifier(u => u.Contains("github") ? 200 : 404);
        Assert.Equal(VerdictMiroir.Present, v);
        Assert.Single(appels);
        Assert.EndsWith(".replay.gz", appels[0]);
        Assert.DoesNotContain(appels, a => a.Contains("gitlab"));
    }

    [Fact]
    public async Task Servi_par_GitLab_seulement_est_present_apres_les_404_de_GitHub()
    {
        var (v, appels) = await Verifier(u => u.Contains("gitlab") && u.EndsWith(".gz") ? 200 : 404);
        Assert.Equal(VerdictMiroir.Present, v);
        Assert.Equal(3, appels.Count);
        Assert.StartsWith("https://github.com", appels[0]);
        Assert.StartsWith("https://gitlab.com", appels[2]);
    }

    [Fact]
    public async Task Forme_brute_de_GitLab_compte_aussi()
    {
        var (v, _) = await Verifier(u => u.Contains("gitlab") && !u.EndsWith(".gz") ? 200 : 404);
        Assert.Equal(VerdictMiroir.Present, v);
    }

    [Fact]
    public async Task Absent_seulement_si_toutes_les_sources_disent_404()
    {
        var (v, appels) = await Verifier(_ => 404);
        Assert.Equal(VerdictMiroir.Absent, v);
        Assert.Equal(4, appels.Count);
    }

    [Fact]
    public async Task Une_source_muette_donne_inconnu_jamais_absent()
    {
        Assert.Equal(VerdictMiroir.Inconnu, (await Verifier(u => u.Contains("gitlab") ? null : 404)).v);
        Assert.Equal(VerdictMiroir.Inconnu, (await Verifier(u => u.Contains("github") ? 503 : 404)).v);
        Assert.Equal(VerdictMiroir.Inconnu, (await Verifier(u => u.Contains("gitlab") ? 429 : 404)).v);
    }

    [Fact]
    public async Task GitHub_muet_mais_GitLab_present_est_present()
    {
        var (v, _) = await Verifier(u => u.Contains("github") ? null : 200);
        Assert.Equal(VerdictMiroir.Present, v);
    }

    [Fact]
    public void Les_gabarits_sont_GitHub_puis_GitLab_sans_doublon_et_se_coupent()
    {
        IConfiguration Cfg(params (string, string)[] kv) => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();
        var g = MirrorPeerSource.Gabarits(Cfg(("Replay:Share:MirrorUrlTemplate", Gh)));
        Assert.Equal(new[] { Gh, MirrorPeerSource.GitLabTemplate }, g);
        Assert.Equal(new[] { Gh }, MirrorPeerSource.Gabarits(Cfg(("Replay:Share:MirrorUrlTemplate", Gh), ("Replay:Share:MirrorFallbackEnabled", "false"))));
        Assert.Empty(MirrorPeerSource.Gabarits(Cfg(("Replay:Share:MirrorFallbackEnabled", "false"))));
    }
}
