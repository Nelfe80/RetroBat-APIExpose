using System.Security.Cryptography;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un replay de RetroArch 1.22.2 se compresse à 1 ou 2 % de sa taille (mesuré le 25 septembre 2026
/// sur tout le magasin de la borne). Il voyage donc compressé, mais son IDENTITÉ reste le SHA-256
/// du brut, et c'est le brut qu'on vérifie à l'arrivée. Ces tests gèlent les trois garanties :
/// l'aller-retour rend le même octet, une archive qui gonfle au-delà de la taille annoncée est
/// arrêtée, et une amorce est interrogée sous ses deux noms, compressé d'abord.
/// </summary>
public class ReplayCompressionTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "replay-gz-" + Guid.NewGuid().ToString("N"));

    public ReplayCompressionTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private string Fichier(string nom) => Path.Combine(_dossier, nom);

    /// <summary>Un faux replay à l'image des vrais : aux deux tiers des zéros, un peu de contenu.</summary>
    private string ReplayCreux(int taille)
    {
        var octets = new byte[taille];
        var alea = new Random(42);
        for (var i = 0; i < taille; i += 3) octets[i] = (byte)alea.Next(256);
        var chemin = Fichier("objet.replay");
        File.WriteAllBytes(chemin, octets);
        return chemin;
    }

    private static string Sha(string chemin) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(chemin))).ToLowerInvariant();

    [Fact]
    public async Task L_aller_retour_rend_le_meme_objet_a_l_octet()
    {
        var brut = ReplayCreux(300_000);
        var gz = Fichier("objet.replay.gz");
        var sortie = Fichier("sortie.replay");

        await ReplayCompression.CompresserAsync(brut, gz, CancellationToken.None);
        var ecrits = await ReplayCompression.DecompresserPlafonneAsync(gz, sortie, new FileInfo(brut).Length, CancellationToken.None);

        Assert.Equal(new FileInfo(brut).Length, ecrits);
        Assert.Equal(Sha(brut), Sha(sortie));   // l'identite est celle du BRUT, et elle tient
    }

    [Fact]
    public async Task Un_replay_creux_devient_nettement_plus_leger()
    {
        // Les vrais sont a 62-72 % de zeros et tombent a 1-2 %. Ce faux, moins creux, doit au moins
        // perdre plus de la moitie : sinon la compression ne sert a rien et il faut le savoir.
        var brut = ReplayCreux(300_000);
        var gz = Fichier("objet.replay.gz");

        var taille = await ReplayCompression.CompresserAsync(brut, gz, CancellationToken.None);

        Assert.True(taille < new FileInfo(brut).Length / 2, $"{taille} octets compresses pour {new FileInfo(brut).Length}");
        Assert.True(ReplayCompression.EstGzip(gz));
        Assert.False(ReplayCompression.EstGzip(brut));
    }

    [Fact]
    public async Task Une_archive_qui_gonfle_au_dela_de_la_taille_annoncee_est_arretee()
    {
        // Une amorce n'est pas une autorite. Une archive qui annoncerait 1 Ko et en donnerait 300
        // doit etre arretee AVANT d'avoir rempli le disque.
        var brut = ReplayCreux(300_000);
        var gz = Fichier("objet.replay.gz");
        await ReplayCompression.CompresserAsync(brut, gz, CancellationToken.None);

        var ecrits = await ReplayCompression.DecompresserPlafonneAsync(gz, Fichier("sortie.replay"), 1024, CancellationToken.None);

        Assert.Null(ecrits);
    }

    [Fact]
    public void Une_amorce_est_interrogee_compressee_d_abord_puis_brute()
    {
        var amorce = new ReplayPeer("amorce github.com", "https://github.com", ApiKey: null, Source: "miroir",
            UrlTemplate: "https://github.com/Nelfe80/NelfeNet-Replays/releases/download/objects/{sha}.replay");

        var adresses = NelfeNetSourceResolver.Adresses(amorce, "abc123");

        Assert.Equal(2, adresses.Count);
        Assert.Equal(("https://github.com/Nelfe80/NelfeNet-Replays/releases/download/objects/abc123.replay.gz", true), adresses[0]);
        Assert.Equal(("https://github.com/Nelfe80/NelfeNet-Replays/releases/download/objects/abc123.replay", false), adresses[1]);
    }

    [Fact]
    public void Une_borne_pair_sert_le_brut_de_son_magasin()
    {
        // Entre bornes rien ne change : le magasin local garde le brut, et une borne restee sur une
        // ancienne version sait toujours le lire.
        var pair = new ReplayPeer("borne salon", "http://192.168.1.20:12345/", ApiKey: "k");

        var adresses = NelfeNetSourceResolver.Adresses(pair, "abc123");

        Assert.Single(adresses);
        Assert.Equal(("http://192.168.1.20:12345/api/v1/object/abc123", false), adresses[0]);
    }
}
