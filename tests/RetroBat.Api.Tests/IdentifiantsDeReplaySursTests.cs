using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Storage;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un identifiant de replay venu du site ou d'un pair ne sert de nom de fichier que s'il en fait un sur.
/// </summary>
public class IdentifiantsDeReplaySursTests
{
    [Theory]
    [InlineData("rp_01JCZ8X9Q7K3M5N2P4R6T8V0WY")]
    [InlineData("rp_avec")]
    [InlineData("fm_01JCZ8X9Q7K3M5N2P4R6T8V0WY")]
    [InlineData("mp_abc-1")]
    public void Identifiants_ordinaires_acceptes(string id)
    {
        Assert.True(ReplayStore.IdentifiantSur(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"..\..\x")]
    [InlineData("../x")]
    [InlineData("rp_1.nghost")]
    [InlineData(@"c:\windows\x")]
    [InlineData("rp 1")]
    [InlineData("rp_é")]
    public void Identifiants_dangereux_refuses(string? id)
    {
        Assert.False(ReplayStore.IdentifiantSur(id));
    }

    [Fact]
    public void Identifiant_trop_long_refuse()
    {
        Assert.False(ReplayStore.IdentifiantSur("rp_" + new string('a', 62)));
    }

    [Fact]
    public void Empreinte_sure()
    {
        Assert.True(ReplayStore.EmpreinteSure(new string('a', 64)));
        Assert.True(ReplayStore.EmpreinteSure(new string('F', 64)));
        Assert.False(ReplayStore.EmpreinteSure(new string('a', 63)));
        Assert.False(ReplayStore.EmpreinteSure("..\\" + new string('a', 61)));
    }

    [Fact]
    public void Le_magasin_refuse_un_chemin_hors_de_ses_dossiers()
    {
        var dossier = Directory.CreateTempSubdirectory("replay-ids-");
        try
        {
            var magasin = new ReplayStore(NullLogger<ReplayStore>.Instance, Path.Combine(dossier.FullName, "magasin"));
            Assert.Throws<ArgumentException>(() => magasin.ManifestPath(@"..\..\x"));
            Assert.Throws<ArgumentException>(() => magasin.ObjectPath(@"..\x"));
            Assert.EndsWith("rp_ok.json", magasin.ManifestPath("rp_ok"));
        }
        finally
        {
            dossier.Delete(true);
        }
    }

    [Fact]
    public void Le_classement_ignore_un_identifiant_qui_ne_ferait_pas_un_nom_de_fichier()
    {
        using var json = JsonDocument.Parse("""
            {"rows":[
              {"player":"Ash","ghost":{"id":"..\\..\\x"}},
              {"player":"Ben","ghost":{"id":"rp_2"}},
              {"player":"Cid","ghost":{"id":42}},
              {"player":"Dan","replay":{"id":"rp_4/../y"}}
            ]}
            """);
        Assert.Equal([("Ash", (string?)null), ("Ben", "rp_2"), ("Cid", null), ("Dan", null)],
            FantomesDuSpeedrun.LignesDuClassement(json.RootElement));
    }
}
