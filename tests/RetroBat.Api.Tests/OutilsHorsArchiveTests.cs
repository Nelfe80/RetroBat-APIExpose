using System;
using System.IO;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'outil de diagnostic faisait 62 Mo des 62 Mo de chaque update.7z (1.9.14, 1.9.15) : il voyage
/// desormais a part, et outils.json dit a la borne ou le prendre (decision user 2026-10-01). Un
/// outils.json ne doit pouvoir ecrire que les outils prevus, depuis les releases du depot.
/// </summary>
public class OutilsHorsArchiveTests
{
    private const string Depot = "Nelfe80/RetroBat-APIExpose";
    private const string Sha = "628f18c2e0e7aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Outil(string fichier = "RetroBat.Api.Diagnostic.exe", string sha = Sha,
        string url = "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.16/RetroBat.Api.Diagnostic.exe",
        long taille = 65958697)
        => JsonSerializer.Serialize(new { fichier, version = "1.2.0+20260930.1bf15ce", sha256 = sha, taille, url });

    [Fact]
    public void Un_outil_prevu_depuis_les_releases_du_depot_est_retenu()
    {
        var outils = SelfUpdateService.LireOutils($"{{\"outils\":[{Outil()}]}}", Depot);

        var outil = Assert.Single(outils);
        Assert.Equal("RetroBat.Api.Diagnostic.exe", outil.Fichier);
        Assert.Equal(Sha, outil.Sha256);
        Assert.Equal(65958697, outil.Taille);
    }

    [Theory]
    [InlineData("RetroBat.Api.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("..\\RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", "abc", "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/autre/depot/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "http://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 0)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 600L * 1024 * 1024)]
    public void Le_reste_est_ignore(string fichier, string sha, string url, long taille)
    {
        Assert.Empty(SelfUpdateService.LireOutils($"{{\"outils\":[{Outil(fichier, sha, url, taille)}]}}", Depot));
    }

    [Fact]
    public void Sans_liste_rien_n_est_retenu()
    {
        Assert.Empty(SelfUpdateService.LireOutils("{}", Depot));
        Assert.Empty(SelfUpdateService.LireOutils("{\"outils\":{}}", Depot));
    }

    [Fact]
    public void L_adresse_d_outils_json_se_lit_dans_les_actifs_de_la_release()
    {
        using var avec = JsonDocument.Parse("{\"tag_name\":\"v1.9.16\",\"assets\":[{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://x/SHA256SUMS.txt\"},{\"name\":\"outils.json\",\"browser_download_url\":\"https://x/outils.json\"}]}");
        using var sans = JsonDocument.Parse("{\"tag_name\":\"v1.9.15\",\"assets\":[{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://x/SHA256SUMS.txt\"}]}");

        Assert.Equal("https://x/outils.json", SelfUpdateService.AdresseDesOutils(avec.RootElement));
        Assert.Null(SelfUpdateService.AdresseDesOutils(sans.RootElement));
    }

    [Theory]
    [InlineData(null, "1.2.0+20260930.1bf15ce", true)]                       // la borne ne l'a pas
    [InlineData("", "1.2.0+20260930.1bf15ce", true)]                         // illisible
    [InlineData("1.1.0+20260925.abc", "1.2.0+20260930.1bf15ce", true)]       // plus ancien
    [InlineData("1.2.0+20260930.1bf15ce", "1.2.0+20260930.1bf15ce", false)]  // le meme
    [InlineData("1.3.0+20261002.def", "1.2.0+20260930.1bf15ce", false)]      // borne de developpement : jamais redescendre
    [InlineData("1.1.0", "illisible", false)]                                // version publiee illisible : on ne touche a rien
    public void On_ne_prend_l_outil_publie_que_s_il_manque_ou_s_il_est_plus_recent(string? locale, string publiee, bool attendu)
    {
        Assert.Equal(attendu, SelfUpdateService.AMettreAJour(locale, publiee));
    }

    [Fact]
    public void Le_nouvel_outil_remplace_l_ancien()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "outils-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var cible = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe");
            var nouveau = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe.part");
            File.WriteAllText(cible, "ancien");
            File.WriteAllText(nouveau, "nouveau");

            SelfUpdateService.Poser(nouveau, cible);

            Assert.Equal("nouveau", File.ReadAllText(cible));
            Assert.False(File.Exists(nouveau));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Un_outil_ouvert_part_en_old_et_le_nouveau_prend_sa_place()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "outils-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var cible = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe");
            var nouveau = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe.part");
            File.WriteAllText(cible, "ancien");
            File.WriteAllText(nouveau, "nouveau");

            // Comme un exe en cours d'execution : ni ecrasable ni supprimable, mais renommable.
            using (new FileStream(cible, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                SelfUpdateService.Poser(nouveau, cible);
            }

            Assert.Equal("nouveau", File.ReadAllText(cible));
            Assert.Equal("ancien", File.ReadAllText(cible + ".old"));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }
}
