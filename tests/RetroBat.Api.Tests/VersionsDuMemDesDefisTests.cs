using System.Security.Cryptography;
using System.Text;
using RetroBat.Domain.Paths;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES VERSIONS DU .MEM DES CHALLENGES (Metal Slug 3, 15/11, decision du user « voie B ») : un reglement cite son .MEM par
/// empreinte ; le fichier en vigueur (ram\.defis\systeme\jeu.MEM, le seul que le wrapper ouvre) et ses autres versions a cote
/// (jeu.8hex.MEM). Au lancement, la version citee est posee a la place du fichier en vigueur, l'ancienne gardee ; une version
/// absente ou abimee refuse le lancement, et rien n'est jamais pose d'autre.
/// </summary>
public sealed class VersionsDuMemDesDefisTests : IDisposable
{
    private readonly string _ram = Path.Combine(Path.GetTempPath(), "nelfe-defis-mem-" + Guid.NewGuid().ToString("N"));
    private const string Sys = "arcade", Rom = "metal-slug-3";

    private static readonly byte[] Ancien = Encoding.UTF8.GetBytes("-- ancien\nreturn { events = {} }\n");
    private static readonly byte[] Nouveau = Encoding.UTF8.GetBytes("-- nouveau, ligne de la mort du boss\nreturn { events = {} }\n");
    private static string Sha(byte[] o) => Convert.ToHexString(SHA256.HashData(o)).ToLowerInvariant();

    private string EnVigueur => Path.Combine(_ram, ".defis", Sys, Rom + ".MEM");

    public VersionsDuMemDesDefisTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EnVigueur)!);
        File.WriteAllBytes(EnVigueur, Ancien);
        File.WriteAllBytes(RamDefinitionLayers.CheminDeLaVersion(_ram, Sys, Rom, Sha(Nouveau)), Nouveau);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ram, true); } catch (IOException) { }
    }

    [Fact]
    public void La_version_a_cote_porte_les_huit_premiers_hex_de_son_empreinte()
        => Assert.Equal(Path.Combine(_ram, ".defis", Sys, Rom + "." + Sha(Nouveau)[..8] + ".MEM"),
            RamDefinitionLayers.CheminDeLaVersion(_ram, Sys, Rom, Sha(Nouveau).ToUpperInvariant()));

    [Fact]
    public void Chaque_reglement_lit_sa_version_sans_rien_modifier()
    {
        Assert.Equal(EnVigueur, RamDefinitionLayers.VersionDeChallenge(_ram, Sys, Rom, Sha(Ancien)));
        Assert.Equal(RamDefinitionLayers.CheminDeLaVersion(_ram, Sys, Rom, Sha(Nouveau)), RamDefinitionLayers.VersionDeChallenge(_ram, Sys, Rom, Sha(Nouveau)));
        Assert.Null(RamDefinitionLayers.VersionDeChallenge(_ram, Sys, Rom, new string('0', 64)));
        Assert.Equal(Ancien, File.ReadAllBytes(EnVigueur));
    }

    [Fact]
    public void Au_lancement_la_version_citee_prend_la_place_et_l_ancienne_est_gardee_puis_revient()
    {
        Assert.Null(RamDefinitionLayers.PoserLaVersionDeChallenge(_ram, Sys, Rom, Sha(Nouveau)));
        Assert.Equal(Nouveau, File.ReadAllBytes(EnVigueur));
        var garde = RamDefinitionLayers.CheminDeLaVersion(_ram, Sys, Rom, Sha(Ancien));
        Assert.Equal(Ancien, File.ReadAllBytes(garde));
        // Les autres reglements du jeu (l'ancienne empreinte) se lancent toujours : leur version revient a sa place.
        Assert.Equal(garde, RamDefinitionLayers.VersionDeChallenge(_ram, Sys, Rom, Sha(Ancien)));
        Assert.Null(RamDefinitionLayers.PoserLaVersionDeChallenge(_ram, Sys, Rom, Sha(Ancien)));
        Assert.Equal(Ancien, File.ReadAllBytes(EnVigueur));
        // Deja en place : rien a faire, rien d'ecrit.
        var avant = File.GetLastWriteTimeUtc(EnVigueur);
        Assert.Null(RamDefinitionLayers.PoserLaVersionDeChallenge(_ram, Sys, Rom, Sha(Ancien)));
        Assert.Equal(avant, File.GetLastWriteTimeUtc(EnVigueur));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(EnVigueur)!, "*.tmp-*"));
    }

    [Fact]
    public void Une_version_absente_refuse_clairement_et_ne_touche_a_rien()
    {
        var absente = new string('a', 64);
        var motif = RamDefinitionLayers.PoserLaVersionDeChallenge(_ram, Sys, Rom, absente);
        Assert.NotNull(motif);
        Assert.Contains("aaaaaaaa", motif);
        Assert.Contains("Data Pack", motif);
        Assert.Equal(Ancien, File.ReadAllBytes(EnVigueur));
    }

    [Fact]
    public void Une_version_abimee_ne_se_pose_jamais()
    {
        // Le fichier a cote porte le nom de l'empreinte, mais pas son contenu (copie tronquee, retouche a la main).
        File.WriteAllBytes(RamDefinitionLayers.CheminDeLaVersion(_ram, Sys, Rom, Sha(Nouveau)), Encoding.UTF8.GetBytes("abime"));
        Assert.Null(RamDefinitionLayers.VersionDeChallenge(_ram, Sys, Rom, Sha(Nouveau)));
        Assert.NotNull(RamDefinitionLayers.PoserLaVersionDeChallenge(_ram, Sys, Rom, Sha(Nouveau)));
        Assert.Equal(Ancien, File.ReadAllBytes(EnVigueur));
    }

    [Fact]
    public void Une_empreinte_illisible_ne_pose_rien()
    {
        Assert.NotNull(RamDefinitionLayers.PoserLaVersionDeChallenge(_ram, Sys, Rom, "b752c210"));
        Assert.Null(RamDefinitionLayers.VersionDeChallenge(_ram, Sys, Rom, "b752c210"));
        Assert.Equal(Ancien, File.ReadAllBytes(EnVigueur));
    }
}
