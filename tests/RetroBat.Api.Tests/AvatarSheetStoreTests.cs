using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Avatar;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le magasin des planches d'avatar : ce qui y entre, sous quel nom, et ce qui est annonce.
///
/// L'enjeu est la regle d'accord. Une borne qui garderait des octets sous une empreinte qu'ils
/// n'ont pas, ou qui annoncerait au recensement une planche jamais confirmee, servirait par le
/// relais une image que l'index ne reconnait pas.
/// </summary>
public sealed class AvatarSheetStoreTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "nelfe-avatar-tests-" + Guid.NewGuid().ToString("N"));

    private AvatarSheetStore Magasin() => new(_racine, NullLogger<AvatarSheetStore>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    /// <summary>Un PNG minimal : signature, en-tete, un bloc prive qui fait varier l'empreinte, fin.
    /// Le magasin ne decode pas l'image, il ne lit que sa forme.</summary>
    private static byte[] Png(int largeur, int hauteur, byte marque = 0)
    {
        using var m = new MemoryStream();
        m.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Bloc(m, "IHDR", En32(largeur).Concat(En32(hauteur)).Concat(new byte[] { 8, 3, 0, 0, 0 }).ToArray());
        Bloc(m, "tEXt", new[] { (byte)'n', (byte)0, marque });
        Bloc(m, "IEND", Array.Empty<byte>());
        return m.ToArray();

        static byte[] En32(int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            return b;
        }

        static void Bloc(Stream s, string type, byte[] donnees)
        {
            s.Write(En32(donnees.Length));
            s.Write(System.Text.Encoding.ASCII.GetBytes(type));
            s.Write(donnees);
            s.Write(new byte[4]);
        }
    }

    private static string Sha(byte[] octets) => Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

    [Fact]
    public async Task Une_planche_entre_sous_son_empreinte_recalculee()
    {
        var magasin = Magasin();
        var octets = Png(320, 192);

        var r = await magasin.ImporterAsync(octets, null, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal(Sha(octets), r.Sha256);
        Assert.True(magasin.Has(r.Sha256));
        Assert.Equal(octets, File.ReadAllBytes(magasin.ObjectPath(r.Sha256)));
    }

    [Fact]
    public async Task Ce_qui_n_a_pas_la_forme_d_une_planche_n_entre_pas()
    {
        var magasin = Magasin();

        Assert.Equal("bad_dimensions", (await magasin.ImporterAsync(Png(64, 64), null, CancellationToken.None)).Erreur);
        Assert.Equal("not_png", (await magasin.ImporterAsync(new byte[64], null, CancellationToken.None)).Erreur);
        Assert.Equal("too_large", (await magasin.ImporterAsync(new byte[AvatarSheetStore.OctetsMax + 1], null, CancellationToken.None)).Erreur);
        Assert.Empty(magasin.Objets());
    }

    [Fact]
    public async Task Des_octets_qui_ne_tombent_pas_sur_l_empreinte_annoncee_ne_sont_pas_gardes()
    {
        var magasin = Magasin();
        var octets = Png(320, 192, 1);

        var r = await magasin.ImporterAsync(octets, new string('a', 64), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal("sha_mismatch", r.Erreur);
        // Ni sous le nom annonce, ni sous le vrai : ce n'est pas ce qu'on attendait.
        Assert.Empty(magasin.Objets());
    }

    [Fact]
    public async Task Importer_deux_fois_la_meme_planche_ne_la_double_pas()
    {
        var magasin = Magasin();
        var octets = Png(320, 192, 2);

        await magasin.ImporterAsync(octets, Sha(octets), CancellationToken.None);
        await magasin.ImporterAsync(octets, Sha(octets), CancellationToken.None);

        Assert.Single(magasin.Objets());
    }

    [Fact]
    public async Task Le_catalogue_rend_la_planche_la_plus_recente_et_survit_a_un_redemarrage()
    {
        var magasin = Magasin();
        var v6 = Png(320, 192, 3);
        var v7 = Png(320, 192, 4);
        await magasin.ImporterAsync(v6, null, CancellationToken.None);
        await magasin.ImporterAsync(v7, null, CancellationToken.None);

        magasin.Cataloguer("Nelfe80", "shmup", 0, "atelier64-v6", Sha(v6), verifiee: true);
        magasin.Cataloguer("Nelfe80", "shmup", 0, "atelier64-v7", Sha(v7), verifiee: true);

        Assert.Equal(Sha(v7), magasin.Trouver("Nelfe80", "shmup", 0)?.Sha256);
        Assert.Equal(Sha(v7), Magasin().Trouver("Nelfe80", "shmup", 0)?.Sha256);
        Assert.Null(magasin.Trouver("Nelfe80", "shmup", 1));
    }

    [Fact]
    public async Task La_casse_du_pseudo_designe_un_autre_joueur()
    {
        var magasin = Magasin();
        var octets = Png(320, 192, 5);
        await magasin.ImporterAsync(octets, null, CancellationToken.None);
        magasin.Cataloguer("Nelfe80", "shmup", 0, "atelier64-v6", Sha(octets), verifiee: true);

        Assert.Null(magasin.Trouver("nelfe80", "shmup", 0));
    }

    [Fact]
    public async Task Une_planche_d_essai_n_est_jamais_annoncee_au_recensement()
    {
        var magasin = Magasin();
        var essai = Png(320, 192, 6);
        var confirmee = Png(320, 192, 7);
        var venueDuRelais = Png(320, 192, 8);
        await magasin.ImporterAsync(essai, null, CancellationToken.None);
        await magasin.ImporterAsync(confirmee, null, CancellationToken.None);
        await magasin.ImporterAsync(venueDuRelais, null, CancellationToken.None);

        magasin.Cataloguer("Vero", "rpg", 0, "atelier64-v6", Sha(essai), verifiee: false);
        magasin.Cataloguer("Nelfe80", "shmup", 0, "atelier64-v6", Sha(confirmee), verifiee: true);

        var annoncees = magasin.ADeclarer();
        Assert.DoesNotContain(Sha(essai), annoncees);
        Assert.Contains(Sha(confirmee), annoncees);
        Assert.Contains(Sha(venueDuRelais), annoncees);
    }
}
