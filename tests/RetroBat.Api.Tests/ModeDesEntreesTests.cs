using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES REPLAYS ENREGISTRES TOUCHE PAR TOUCHE (11/11). Mesure sur la borne : Streets of Rage 2 du 09/11 relu avec le masque,
/// 1 090 au lieu de 3 960 ; masque refuse par le wrapper 0.345, 3 960 exact. Garegga 1LC du 09/11 : 0 avec le masque ; 10 620
/// (1LC) et 19 050 (1CC) exacts sans.
/// </summary>
public sealed class ModeDesEntreesTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "mode-entrees-" + Guid.NewGuid().ToString("N"));

    public ModeDesEntreesTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, true); } catch (IOException) { }
    }

    /// <summary>Un BSV v2 minimal : en-tete, etat initial vide, puis des images de lectures de manette.</summary>
    private static byte[] Bsv(params ushort[][] images)
    {
        using var m = new MemoryStream();
        var tete = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(tete, 0x42535632);
        BinaryPrimitives.WriteUInt32LittleEndian(tete.AsSpan(4), 2);
        m.Write(tete);
        m.Write(new byte[14]);                                   // compression, encodage, trois tailles (etat vide)
        foreach (var ids in images)
        {
            m.Write(new byte[4]);                                // renvoi
            m.WriteByte(0);                                      // aucune touche clavier
            var n = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(n, (ushort)ids.Length);
            m.Write(n);
            foreach (var id in ids)
            {
                var e = new byte[8];
                e[0] = 0; e[1] = 1; e[2] = 0;                    // port 0, manette, index 0
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), id);
                m.Write(e);
            }
            m.WriteByte((byte)'f');
        }
        return m.ToArray();
    }

    private static readonly ushort[] Seize = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();

    [Fact]
    public void Le_masque_et_les_touches_se_reconnaissent()
    {
        Assert.Equal(ModeDesEntrees.Mode.Masque, ModeDesEntrees.Lire(new MemoryStream(Bsv([256], [256]))));
        Assert.Equal(ModeDesEntrees.Mode.Touches, ModeDesEntrees.Lire(new MemoryStream(Bsv(Seize, Seize))));
        Assert.Equal(ModeDesEntrees.Mode.Inconnu, ModeDesEntrees.Lire(new MemoryStream(Bsv([], []))));
        Assert.Equal(ModeDesEntrees.Mode.Inconnu, ModeDesEntrees.Lire(new MemoryStream(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public void Un_objet_gzip_du_magasin_se_lit()
    {
        var chemin = Path.Combine(_dossier, "objet.replay.gz");
        using (var f = File.Create(chemin))
        using (var gz = new GZipStream(f, CompressionLevel.Fastest))
            gz.Write(Bsv(Seize, Seize, Seize));
        Assert.Equal(ModeDesEntrees.Mode.Touches, ModeDesEntrees.Lire(chemin));
        Assert.Equal(ModeDesEntrees.Mode.Inconnu, ModeDesEntrees.Lire(Path.Combine(_dossier, "absent")));
    }

    private static ReplayManifest Manifeste() => new(
        ReplayManifest.SchemaId, "rp_1", "sess_1", new ReplayGame("mega-drive/streets-of-rage-2", "mega_drive", "streets-of-rage-2", null, "e01fa526"),
        DateTime.UtcNow, "home",
        new ReplayRuntime("nelfe-mega-drive-r1", "1.22.2", null, null, null, null, "bsv", "genesis_plus_gx", null, null),
        new ReplayObjectRef(new string('a', 64), 10), new ReplayFrames(0, null, null, 100, 59.92), null, new ReplayRecovery(false));

    [Fact]
    public void Sans_wrapper_capable_un_replay_touche_par_touche_garde_le_vrai_coeur()
    {
        File.WriteAllText(Path.Combine(_dossier, "genesis_plus_gx_libretro.dll"), "pas un wrapper");
        var psi = new ProcessStartInfo();
        var certifie = Path.Combine(_dossier, "certified.txt");
        var (coeur, erreur) = LectureAvecVerrous.Preparer(Manifeste(), @"E:\cores\genesis_plus_gx_libretro.dll", @"E:\roms\sor2.zip", psi,
            _dossier, certifie, ModeDesEntrees.Mode.Touches);
        Assert.Equal(ReplayErrorCode.None, erreur);
        Assert.Equal(@"E:\cores\genesis_plus_gx_libretro.dll", coeur);
        Assert.False(File.Exists(certifie));
        Assert.False(psi.Environment.ContainsKey(LectureAvecVerrous.VariableSansMasque));
    }

    [Fact]
    public void Avec_le_wrapper_0_345_le_masque_est_refuse_et_le_tube_coupe()
    {
        // Le wrapper construit dans son worktree (branche masque-des-touches) ; sans lui, l'essai ne prouve rien.
        var wrapper = Environment.GetEnvironmentVariable("NELFE_WRAPPER_345") ?? @"E:\claude_travail\worktrees\wrapper-masque\wrapper.dll";
        if (!File.Exists(wrapper) || LectureAvecVerrous.VersionDuWrapper(wrapper) < LectureAvecVerrous.VersionSansMasque) return;
        File.Copy(wrapper, Path.Combine(_dossier, "genesis_plus_gx_libretro.dll"), true);
        var psi = new ProcessStartInfo();
        var certifie = Path.Combine(_dossier, "certified.txt");
        var (coeur, erreur) = LectureAvecVerrous.Preparer(Manifeste(), @"E:\cores\genesis_plus_gx_libretro.dll", @"E:\roms\Streets of Rage 2 (USA).zip",
            psi, _dossier, certifie, ModeDesEntrees.Mode.Touches);
        Assert.Equal(ReplayErrorCode.None, erreur);
        Assert.Equal(Path.Combine(_dossier, "genesis_plus_gx_libretro.dll"), coeur);
        Assert.Equal("off", psi.Environment[LectureAvecVerrous.VariableSansMasque]);
        Assert.Equal(@"E:\cores\genesis_plus_gx_libretro.dll", psi.Environment["RETROBAT_LIBRETRO_REAL_CORE"]);
        Assert.Contains("pipe=off", File.ReadAllLines(certifie));
        Assert.Contains("rom=Streets of Rage 2 (USA)", File.ReadAllLines(certifie));

        // Un replay en mode masque ne passe pas par le wrapper.
        var psi2 = new ProcessStartInfo();
        var (coeur2, _) = LectureAvecVerrous.Preparer(Manifeste(), @"E:\cores\genesis_plus_gx_libretro.dll", @"E:\roms\sor2.zip", psi2,
            _dossier, Path.Combine(_dossier, "autre.txt"), ModeDesEntrees.Mode.Masque);
        Assert.Equal(@"E:\cores\genesis_plus_gx_libretro.dll", coeur2);
    }

    [Fact]
    public void Les_vrais_replays_de_la_borne()
    {
        // Les objets du magasin de la borne, quand il est la : SoR2 touche par touche, Sonic au masque.
        var objets = @"E:\RetroBat\plugins\APIExpose\state\nelfenet\replay\objects\sha256";
        var sor2 = Path.Combine(objets, "dd", "ddebb13af5be52e53f587042d6f551d6d181d351224224a18b4c55d4f654f525.replay.gz");
        var sonic = Directory.Exists(Path.Combine(objets, "6d")) ? Directory.GetFiles(Path.Combine(objets, "6d"), "6d2036a5dd*").FirstOrDefault() : null;
        if (File.Exists(sor2)) Assert.Equal(ModeDesEntrees.Mode.Touches, ModeDesEntrees.Lire(sor2));
        if (sonic is not null) Assert.Equal(ModeDesEntrees.Mode.Masque, ModeDesEntrees.Lire(sonic));
    }
}
