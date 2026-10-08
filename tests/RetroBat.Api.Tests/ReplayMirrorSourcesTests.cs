using Microsoft.Extensions.Configuration;
using RetroBat.Api.Replay.Sharing;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le serveur NelfePlay pose chaque replay sur GitHub et sur GitLab (2026-10-08) : une borne
/// demande l'amorce GitHub, puis GitLab quand GitHub ne répond pas.
/// </summary>
public class ReplayMirrorSourcesTests
{
    private const string GitHub = "https://github.com/Nelfe80/NelfeNet-Replays/releases/download/objects/{sha}.replay";

    private static IConfiguration Reglages(params (string Cle, string? Valeur)[] valeurs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(valeurs.Select(v => new KeyValuePair<string, string?>(v.Cle, v.Valeur)))
            .Build();

    [Fact]
    public async Task GitHub_d_abord_puis_GitLab_par_defaut()
    {
        var source = new MirrorPeerSource(Reglages(("Replay:Share:MirrorUrlTemplate", GitHub)));

        var pairs = await source.DiscoverAsync(CancellationToken.None);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(GitHub, pairs[0].UrlTemplate);
        Assert.Equal(MirrorPeerSource.GitLabTemplate, pairs[1].UrlTemplate);
        Assert.Equal("amorce gitlab.com", pairs[1].Name);
        Assert.All(pairs, p => Assert.Equal(MirrorPeerSource.SourceTag, p.Source));
    }

    [Fact]
    public async Task La_seconde_source_se_coupe()
    {
        var source = new MirrorPeerSource(Reglages(
            ("Replay:Share:MirrorUrlTemplate", GitHub),
            ("Replay:Share:MirrorFallbackEnabled", "false")));

        var pairs = await source.DiscoverAsync(CancellationToken.None);

        Assert.Single(pairs);
        Assert.Equal(GitHub, pairs[0].UrlTemplate);
    }

    [Fact]
    public async Task La_seconde_source_se_remplace_et_un_gabarit_sans_marqueur_est_ecarte()
    {
        var source = new MirrorPeerSource(Reglages(
            ("Replay:Share:MirrorUrlTemplate", GitHub),
            ("Replay:Share:MirrorFallbackUrlTemplates:0", "https://exemple.org/objets/{sha}.replay"),
            ("Replay:Share:MirrorFallbackUrlTemplates:1", "https://exemple.org/sans-marqueur.replay")));

        var pairs = await source.DiscoverAsync(CancellationToken.None);

        Assert.Equal(2, pairs.Count);
        Assert.Equal("https://exemple.org/objets/{sha}.replay", pairs[1].UrlTemplate);
    }

    [Fact]
    public async Task Coupee_l_amorce_ne_donne_rien()
    {
        var source = new MirrorPeerSource(Reglages(
            ("Replay:Share:MirrorUrlTemplate", GitHub),
            ("Replay:Share:MirrorEnabled", "false")));

        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void GitLab_est_interroge_compresse_d_abord()
    {
        var gitlab = new ReplayPeer("amorce gitlab.com", MirrorPeerSource.GitLabTemplate, ApiKey: null,
            Source: MirrorPeerSource.SourceTag, UrlTemplate: MirrorPeerSource.GitLabTemplate);

        var adresses = NelfeNetSourceResolver.Adresses(gitlab, "abc123");

        Assert.Equal(("https://gitlab.com/Nelfe80/NelfeNet-Replays/-/raw/main/objects/abc123.replay.gz", true), adresses[0]);
    }

    [Fact]
    public void Le_manifeste_se_lit_a_chaque_source_dans_l_ordre()
    {
        Assert.Equal(["https://github.com/m.json", "https://gitlab.com/m.json"],
            ReplayReplicationService.AdressesDuManifeste(["https://github.com/m.json", "", "https://gitlab.com/m.json"], "ignoree"));
        // Une collection d'avant le 2026-10-08 n'en donne qu'une.
        Assert.Equal(["https://github.com/m.json"], ReplayReplicationService.AdressesDuManifeste(null, "https://github.com/m.json"));
        Assert.Empty(ReplayReplicationService.AdressesDuManifeste(null, null));
    }
}
