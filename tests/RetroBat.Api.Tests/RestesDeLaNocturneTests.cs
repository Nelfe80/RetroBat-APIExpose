using System.Security.Cryptography;
using System.Text;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les restes de la nocturne de RetroArch (2026-10-17) : seuls partent les fichiers de la liste a leur taille et a leur
/// empreinte exactes, et rien tant que l'exe de la nocturne est en place.
/// </summary>
public sealed class RestesDeLaNocturneTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "apiexpose-restes-" + Guid.NewGuid().ToString("N"));

    public RestesDeLaNocturneTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, true); } catch (IOException) { }
    }

    private (long Taille, string Sha256) Poser(string nom, string contenu)
    {
        var octets = Encoding.ASCII.GetBytes(contenu);
        File.WriteAllBytes(Path.Combine(_dossier, nom), octets);
        return (octets.Length, Convert.ToHexString(SHA256.HashData(octets)));
    }

    [Fact]
    public void Un_reste_a_sa_taille_et_a_son_empreinte_part()
    {
        var avutil = Poser("avutil-61.dll", "ffmpeg 8 de la nocturne");
        var restes = RetroArchNelfePlay.RestesDans(_dossier, new Dictionary<string, (long, string)> { ["avutil-61.dll"] = avutil }, "exe");
        Assert.Equal([Path.Combine(_dossier, "avutil-61.dll")], restes);
    }

    [Fact]
    public void Un_fichier_de_RetroBat_du_meme_nom_reste()
    {
        var nocturne = Poser("SDL3.dll", "SDL3 de la nocturne");
        Poser("SDL3.dll", "SDL3 d'un autre RetroArch");
        Assert.Empty(RetroArchNelfePlay.RestesDans(_dossier, new Dictionary<string, (long, string)> { ["SDL3.dll"] = nocturne }, "exe"));
    }

    [Fact]
    public void Rien_ne_part_tant_que_l_exe_de_la_nocturne_est_la()
    {
        var avutil = Poser("avutil-61.dll", "ffmpeg 8 de la nocturne");
        var exe = Poser("retroarch.exe", "exe de la nocturne");
        Assert.Empty(RetroArchNelfePlay.RestesDans(_dossier, new Dictionary<string, (long, string)> { ["avutil-61.dll"] = avutil }, exe.Sha256));
    }

    [Fact]
    public void Sous_le_RetroArch_de_RetroBat_les_restes_partent()
    {
        var avutil = Poser("avutil-61.dll", "ffmpeg 8 de la nocturne");
        Poser("retroarch.exe", "exe 1.22.2");
        Assert.Single(RetroArchNelfePlay.RestesDans(_dossier, new Dictionary<string, (long, string)> { ["avutil-61.dll"] = avutil }, "cbc50e13"));
    }

    [Fact]
    public void La_liste_porte_les_bibliotheques_qui_cassaient_le_direct()
    {
        Assert.Equal(27, RetroArchNelfePlay.Restes.Count);
        Assert.True(RetroArchNelfePlay.Restes.ContainsKey("AVUTIL-61.DLL"));
        Assert.All(RetroArchNelfePlay.Restes.Values, v => Assert.Equal(64, v.Sha256.Length));
        // Jamais un fichier que RetroArch 1.22.2 livre lui-meme.
        Assert.DoesNotContain("libwinpthread-1.dll", RetroArchNelfePlay.Restes.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("avutil-56.dll", RetroArchNelfePlay.Restes.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
