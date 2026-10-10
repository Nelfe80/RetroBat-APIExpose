using System.Buffers.Binary;
using RetroBat.Api.Replay.Playback;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les points de controle d'un replay, lus avant la lecture (2026-10-10). Sur un replay venu d'une autre borne, sans
/// aucun point, l'appui sur ▶ affichait « Seek Forward Failed », arretait le film et rendait la manette au spectateur ;
/// le maintien de ◀/▶ ramenait au debut. Les fichiers ci-dessous suivent le format de RetroArch 1.22.2 octet pour octet.
/// </summary>
public class PointsDeControleTests
{
    private static readonly long[] Tous = { 299, 599, 899, 1199 };

    /// <summary>Un replay v2 : en-tete, etat initial, puis une frame par jeton ('f' ou 'C').</summary>
    private static MemoryStream Replay(string jetons, uint version = 2, uint signature = 0x42535632, int coupeA = -1)
    {
        var flux = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); flux.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); flux.Write(b); }
        U32(signature); U32(version); U32(0xF52D7D3C); U32(9766); U32(1); U32(2); U32((uint)jetons.Length); U32(16384); U32(64); U32(0x04020200);
        flux.WriteByte(2); flux.WriteByte(1);
        U32(9766); U32(9800); U32(5); flux.Write(new byte[5]);
        for (var i = 0; i < jetons.Length; i++)
        {
            U32(12);
            // Une touche et deux evenements de temps en temps : ils se sautent sans se lire.
            var touches = i % 3 == 0 ? 1 : 0;
            flux.WriteByte((byte)touches);
            flux.Write(new byte[12 * touches]);
            var evenements = (ushort)(i % 2 == 0 ? 2 : 0);
            U16(evenements);
            flux.Write(new byte[8 * evenements]);
            flux.WriteByte((byte)jetons[i]);
            if (jetons[i] == 'C')
            {
                flux.WriteByte(2); flux.WriteByte(1);
                U32(9766); U32(1200); U32(37); flux.Write(new byte[37]);
            }
        }
        if (coupeA >= 0) flux.SetLength(coupeA);
        flux.Position = 0;
        return flux;
    }

    [Fact]
    public void Lit_les_frames_des_points_de_controle()
    {
        Assert.Equal(new long[] { 3, 7 }, PointsDeControle.Lire(Replay("fffCfffCff")));
    }

    [Fact]
    public void Un_replay_sans_point_rend_une_liste_vide()
    {
        Assert.Empty(PointsDeControle.Lire(Replay("ffffffffff"))!);
        // Un film v0 : RetroArch ne saute jamais dedans.
        Assert.Empty(PointsDeControle.Lire(Replay("ff", version: 0))!);
    }

    [Fact]
    public void Un_fichier_coupe_garde_les_points_d_avant_la_coupure()
    {
        var entier = Replay("fffCfffC");
        Assert.Equal(new long[] { 3, 7 }, PointsDeControle.Lire(entier));
        Assert.Equal(new long[] { 3 }, PointsDeControle.Lire(Replay("fffCfffC", coupeA: (int)entier.Length - 10)));
    }

    [Fact]
    public void Une_signature_ou_une_version_inconnue_rend_null()
    {
        Assert.Null(PointsDeControle.Lire(Replay("fffC", signature: 0x12345678)));
        Assert.Null(PointsDeControle.Lire(Replay("fffC", version: 3)));
        Assert.Null(PointsDeControle.Lire(new MemoryStream(new byte[3])));
        Assert.Null(PointsDeControle.Lire(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid() + ".replay")));
    }

    [Fact]
    public void En_avant_on_vise_le_point_le_plus_proche_de_la_cible()
    {
        // RetroArch atterrit sur le dernier point AVANT la frame demandee : pour le point c, on demande c + 1.
        Assert.Equal(new PointsDeControle.Saut(900, 899), PointsDeControle.Relatif(Tous, 600, 300));
        Assert.Equal(new PointsDeControle.Saut(900, 899), PointsDeControle.Relatif(Tous, 598, 300));
        // Au-dela du dernier point, on s'arrete au dernier ; sur le dernier, rien.
        Assert.Equal(new PointsDeControle.Saut(1200, 1199), PointsDeControle.Relatif(Tous, 1150, 300));
        Assert.Null(PointsDeControle.Relatif(Tous, 1199, 300));
        // La fin de la lecture (celle d'un 1LC) borne les sauts en avant.
        Assert.Null(PointsDeControle.Relatif(Tous, 600, 300, fin: 899));
    }

    [Fact]
    public void En_arriere_le_debut_compte_comme_un_point()
    {
        Assert.Equal(new PointsDeControle.Saut(600, 599), PointsDeControle.Relatif(Tous, 905, -300));
        Assert.Equal(new PointsDeControle.Saut(0, 0), PointsDeControle.Relatif(Tous, 400, -300));
        Assert.Null(PointsDeControle.Relatif(Tous, 10, -300));
    }

    [Fact]
    public void Sans_point_aucun_saut()
    {
        var aucun = Array.Empty<long>();
        Assert.Null(PointsDeControle.Relatif(aucun, 600, 300));
        Assert.Null(PointsDeControle.Relatif(aucun, 600, -300));
        Assert.Null(PointsDeControle.Suivant(aucun, 600));
        Assert.Null(PointsDeControle.Precedent(aucun, 600));
    }

    [Fact]
    public void Suivant_et_precedent_ne_retombent_pas_sur_le_point_qu_on_vient_de_passer()
    {
        Assert.Equal(new PointsDeControle.Saut(900, 899), PointsDeControle.Suivant(Tous, 600));
        Assert.Equal(new PointsDeControle.Saut(900, 899), PointsDeControle.Suivant(Tous, 599));
        Assert.Null(PointsDeControle.Suivant(Tous, 1199));
        Assert.Null(PointsDeControle.Suivant(Tous, 600, fin: 899));
        Assert.Equal(new PointsDeControle.Saut(300, 299), PointsDeControle.Precedent(Tous, 610));
        Assert.Equal(new PointsDeControle.Saut(600, 599), PointsDeControle.Precedent(Tous, 700));
        Assert.Equal(new PointsDeControle.Saut(0, 0), PointsDeControle.Precedent(Tous, 100));
    }
}
