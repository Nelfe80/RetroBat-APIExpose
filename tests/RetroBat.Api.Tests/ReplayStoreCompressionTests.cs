using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le magasin de replays dort compressé : les joueurs se plaignaient de la place (2026-09-25), et un
/// replay de RetroArch 1.22.2 tient en 1 à 2 % de sa taille une fois compressé. Ces tests gèlent ce
/// qui ne doit JAMAIS arriver : perdre un replay, ou en lire un autre que celui demandé.
/// </summary>
public class ReplayStoreCompressionTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "replay-store-" + Guid.NewGuid().ToString("N"));
    private readonly ReplayStore _store;

    public ReplayStoreCompressionTests()
    {
        _store = new ReplayStore(NullLogger<ReplayStore>.Instance, _racine);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
    }

    /// <summary>Un objet rangé comme le fait l'enregistreur : brut, nommé par son empreinte.</summary>
    private async Task<ReplayObjectRef> Ranger(int taille = 200_000)
    {
        var octets = new byte[taille];
        var alea = new Random(7);
        for (var i = 0; i < taille; i += 3) octets[i] = (byte)alea.Next(256);   // creux, comme les vrais
        var source = Path.Combine(_racine, "source.replay");
        Directory.CreateDirectory(_racine);
        await File.WriteAllBytesAsync(source, octets);
        return await _store.ImportObjectAsync(source, CancellationToken.None);
    }

    private static string Sha(string chemin) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(chemin))).ToLowerInvariant();

    [Fact]
    public async Task Le_compactage_remplace_le_brut_par_une_forme_bien_plus_legere()
    {
        var obj = await Ranger();
        var brut = _store.ObjectPath(obj.Sha256);

        var (compresses, _, avant, apres) = await _store.CompacterAsync(10, CancellationToken.None);

        Assert.Equal(1, compresses);
        Assert.False(File.Exists(brut));                 // le brut est parti...
        Assert.True(_store.HasObject(obj.Sha256));      // ...mais le replay est toujours detenu
        Assert.True(apres < avant / 2, $"{apres} octets pour {avant}");
    }

    [Fact]
    public async Task Un_replay_compresse_se_relit_a_l_identique()
    {
        var obj = await Ranger();
        await _store.CompacterAsync(10, CancellationToken.None);

        var brut = await _store.EnsureRawAsync(obj.Sha256, CancellationToken.None);

        Assert.NotNull(brut);
        Assert.Equal(obj.Sha256, Sha(brut!));
        Assert.True(await _store.VerifyObjectAsync(obj, CancellationToken.None));   // la verification R6 d'avant lecture passe
    }

    [Fact]
    public async Task Une_forme_compressee_alteree_n_est_jamais_lue()
    {
        // Un disque qui pourrit, ou une main qui remplace le fichier : l'archive se decompresse
        // peut-etre, mais ne redonne pas l'empreinte. On ne la lit pas, on ne la sert pas.
        var obj = await Ranger();
        await _store.CompacterAsync(10, CancellationToken.None);
        var gz = _store.ObjectPath(obj.Sha256) + ReplayCompression.Suffixe;
        var autre = Path.Combine(_racine, "autre.bin");
        await File.WriteAllBytesAsync(autre, new byte[] { 1, 2, 3, 4, 5 });
        await ReplayCompression.CompresserAsync(autre, gz, CancellationToken.None);

        Assert.Null(await _store.EnsureRawAsync(obj.Sha256, CancellationToken.None));
        Assert.False(await _store.VerifyObjectAsync(obj, CancellationToken.None));
    }

    [Fact]
    public async Task Le_brut_materialise_pour_une_lecture_est_retire_au_passage_suivant()
    {
        var obj = await Ranger();
        await _store.CompacterAsync(10, CancellationToken.None);
        await _store.EnsureRawAsync(obj.Sha256, CancellationToken.None);   // une lecture l'a ressorti
        Assert.True(File.Exists(_store.ObjectPath(obj.Sha256)));

        var (compresses, retires, _, _) = await _store.CompacterAsync(10, CancellationToken.None);

        Assert.Equal(0, compresses);   // rien a recompresser : la forme compressee est deja la
        Assert.Equal(1, retires);
        Assert.False(File.Exists(_store.ObjectPath(obj.Sha256)));
        Assert.True(_store.HasObject(obj.Sha256));
    }

    [Fact]
    public async Task Un_brut_dont_le_nom_ne_correspond_pas_au_contenu_reste_brut()
    {
        // Un fichier deja corrompu avant la compression : on ne le compresse pas, et surtout on ne
        // supprime pas le seul exemplaire qu'on a.
        var obj = await Ranger();
        var brut = _store.ObjectPath(obj.Sha256);
        var octets = await File.ReadAllBytesAsync(brut);
        octets[10] ^= 0xFF;
        await File.WriteAllBytesAsync(brut, octets);

        var (compresses, _, _, _) = await _store.CompacterAsync(10, CancellationToken.None);

        Assert.Equal(0, compresses);
        Assert.True(File.Exists(brut));
        Assert.False(File.Exists(brut + ReplayCompression.Suffixe));
    }

    [Fact]
    public async Task Supprimer_un_objet_retire_ses_deux_formes()
    {
        var obj = await Ranger();
        await _store.CompacterAsync(10, CancellationToken.None);
        await _store.EnsureRawAsync(obj.Sha256, CancellationToken.None);

        _store.DeleteObject(obj.Sha256);

        Assert.False(_store.HasObject(obj.Sha256));
    }
}
