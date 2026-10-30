using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La lecture du panneau de classement (30/10/2026) : il parcourt les replays a chaque ouverture, et ce qui n'a pas change
/// ne se relit pas. Une meta modifiee ou un replay retire doivent se voir tout de suite.
/// </summary>
public class ReplayStoreLectureTests : IDisposable
{
    private readonly DirectoryInfo _dossier = Directory.CreateTempSubdirectory("lecture-replays-");
    private readonly ReplayStore _magasin;

    public ReplayStoreLectureTests() => _magasin = new ReplayStore(NullLogger<ReplayStore>.Instance, Path.Combine(_dossier.FullName, "magasin"));

    public void Dispose()
    {
        try { _dossier.Delete(true); } catch (IOException) { }
    }

    private void Enregistrer(string id)
    {
        _magasin.SaveManifest(new ReplayManifest(ReplayManifest.SchemaId, id, "s-" + id, new ReplayGame("toki", "arcade", "toki", "1cc", null),
            DateTime.UtcNow, "local", new ReplayRuntime("retroarch", "1.22.2", null, null, null, null, "bsv2"),
            new ReplayObjectRef(new string('a', 64), 1), new ReplayFrames(0, null, null, 100, 60), null, new ReplayRecovery(false)));
        _magasin.SaveMeta(ReplayLocalMetadata.Fresh(id));
    }

    [Fact]
    public void Ce_qui_n_a_pas_change_ne_se_relit_pas()
    {
        Enregistrer("rp_un");

        var premiere = _magasin.ListerSansRelire().Single();
        var seconde = _magasin.ListerSansRelire().Single();

        Assert.Same(premiere.Manifeste, seconde.Manifeste);
        Assert.Same(premiere.Meta, seconde.Meta);
    }

    [Fact]
    public void Une_meta_modifiee_se_voit_tout_de_suite()
    {
        Enregistrer("rp_un");
        Assert.Null(_magasin.ListerSansRelire().Single().Meta!.ScoreValue);

        _magasin.SaveMeta(ReplayLocalMetadata.Fresh("rp_un") with { ScoreValue = 17350 });
        File.SetLastWriteTimeUtc(_magasin.MetaPath("rp_un"), DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(17350, _magasin.ListerSansRelire().Single().Meta!.ScoreValue);
    }

    [Fact]
    public void Un_nouveau_replay_entre_et_un_replay_retire_sort()
    {
        Enregistrer("rp_un");
        Enregistrer("rp_deux");
        Assert.Equal(2, _magasin.ListerSansRelire().Count);

        File.Delete(_magasin.ManifestPath("rp_un"));
        Enregistrer("rp_trois");

        Assert.Equal(["rp_deux", "rp_trois"], _magasin.ListerSansRelire().Select(r => r.Manifeste.ReplayId).Order().ToArray());
    }

    [Fact]
    public void Un_replay_sans_meta_reste_liste()
    {
        Enregistrer("rp_un");
        File.Delete(_magasin.MetaPath("rp_un"));

        Assert.Null(_magasin.ListerSansRelire().Single().Meta);
    }
}
