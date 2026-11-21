using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les garde-fous de la lecture d'un paquet par la collection World Scoring (relecture de l'integrateur de la 1.9.208, 21/11) :
/// ce code extrait des fichiers d'un paquet sur la borne d'un joueur sans qu'il l'ait demande. Jamais pendant une partie,
/// dossier provisoire toujours efface, plafond et delai, aucun chemin hors du dossier, pas d'echec retente en boucle, pas
/// d'empreinte d'un paquet qui a change.
/// </summary>
public sealed class EmpreintesDesTemoinsTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "empreintes-temoins-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _maintenant = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    private string Index => Path.Combine(_racine, "index.json");
    private string Cache => Path.Combine(_racine, "state", "empreintes.json");
    private string Provisoire => Path.Combine(_racine, "tmp");
    private string Paquet => Path.Combine(_racine, "package-installer", "gb.rar");

    private static string Md5(byte[] octets) => Convert.ToHexString(MD5.HashData(octets)).ToLowerInvariant();

    /// <summary>Un fichier d'attente par entree, un paquet « gb.rar » dans l'index ; rend les chemins des temoins.</summary>
    private List<string> Poser(string sha256 = "sha-1", params string[] entrees)
    {
        if (entrees.Length == 0) entrees = ["Tetris (World) (Rev 1).zip"];
        Directory.CreateDirectory(Path.GetDirectoryName(Paquet)!);
        if (!File.Exists(Paquet)) File.WriteAllText(Paquet, "paquet");
        var temoins = new List<string>();
        var roms = new List<RomPackRomEntry>();
        foreach (var entree in entrees)
        {
            var nom = Path.GetFileName(entree.Replace('\\', '/').Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (nom.Length == 0 || nom.StartsWith('-') || nom.StartsWith('@') || nom.Contains(':')) nom = "temoin-" + temoins.Count + ".zip";
            var chemin = Path.Combine(_racine, "roms", "gb", nom);
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin, string.Join("\n", "APIEXPOSE_ON_THE_FLY_ROM_PLACEHOLDER_v1", "system=gb", "packPath=" + entree, ""));
            temoins.Add(chemin);
            roms.Add(new RomPackRomEntry
            {
                SystemId = "gb", GameName = nom, GamePath = "./" + nom, DestinationFileName = nom,
                PackRelativePath = entree, ArchiveEntryPath = entree, Size = 100,
            });
        }

        EcrireIndex(sha256, roms);
        return temoins;
    }

    private void EcrireIndex(string sha256, List<RomPackRomEntry> roms)
    {
        var info = new FileInfo(Paquet);
        var index = new RomPackInstallerIndex
        {
            Packs =
            [
                new RomPackIndexEntry
                {
                    PackagePath = Paquet, Sha256 = sha256, Size = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc,
                    Status = "installed", OnTheFlyMode = true, SystemId = "gb", Roms = roms,
                },
            ],
        };
        File.WriteAllText(Index, JsonSerializer.Serialize(index));
    }

    private sealed class Compteur
    {
        public int Extractions;
        public List<string> Dossiers { get; } = [];
    }

    private EmpreintesDesTemoins Temoins(Compteur compteur, Func<string, CancellationToken, Task<string>>? extraire = null,
        byte[]? contenu = null, TimeSpan? delai = null, long? plafond = null)
        => new(Index, Cache, Provisoire,
            async (paquet, entree, relatif, dossier, jeton) =>
            {
                compteur.Extractions++;
                compteur.Dossiers.Add(dossier);
                if (extraire is not null) return await extraire(dossier, jeton);
                var sortie = Path.Combine(dossier, Path.GetFileName(entree));
                await File.WriteAllBytesAsync(sortie, contenu ?? Encoding.ASCII.GetBytes("ROM"), jeton);
                return sortie;
            },
            (chemin, _, _) => (Md5(File.ReadAllBytes(chemin)), null),
            delai: delai, plafond: plafond, pas: TimeSpan.FromMilliseconds(20), horloge: () => _maintenant);

    private void AucunDossierProvisoire()
        => Assert.True(!Directory.Exists(Provisoire) || Directory.GetFileSystemEntries(Provisoire).Length == 0,
            "dossier provisoire laisse : " + string.Join(", ", Directory.Exists(Provisoire) ? Directory.GetFileSystemEntries(Provisoire) : []));

    [Theory]
    [InlineData("Tetris (World) (Rev 1).zip", true)]
    [InlineData("snes/Tetris 2 (Europe).zip", true)]
    [InlineData("./gb/Tetris.zip", true)]
    [InlineData("../../evil.dll", false)]
    [InlineData("gb/../../evil.dll", false)]
    [InlineData("gb\\..\\..\\evil.dll", false)]
    [InlineData("gb/.../evil.dll", false)]
    [InlineData("gb/.. /evil.dll", false)]
    [InlineData("C:\\Windows\\evil.dll", false)]
    [InlineData("/etc/evil", false)]
    [InlineData("\\\\serveur\\partage\\evil", false)]
    [InlineData("rom.zip:flux", false)]
    [InlineData("-P", false)]
    [InlineData("-spf", false)]
    [InlineData("@liste.txt", false)]
    [InlineData("", false)]
    public void Une_entree_ne_sort_pas_du_dossier_et_n_est_pas_une_option(string entree, bool sure)
        => Assert.Equal(sure, EmpreintesDesTemoins.EntreeSure(entree));

    [Theory]
    [InlineData("../../evil.zip")]
    [InlineData("gb/../../../evil.zip")]
    [InlineData("-P")]
    public void Une_entree_qui_sortirait_du_dossier_n_est_jamais_extraite(string entree)
    {
        var temoin = Poser("sha-1", entree)[0];
        var compteur = new Compteur();

        var lu = Temoins(compteur).Lire(temoin, "gb");

        Assert.NotNull(lu);
        Assert.False(lu!.Lu);
        Assert.Equal(0, compteur.Extractions);
        AucunDossierProvisoire();
    }

    [Fact]
    public void Un_fichier_rendu_hors_du_dossier_provisoire_n_est_pas_lu()
    {
        var temoin = Poser()[0];
        var ailleurs = Path.Combine(_racine, "ailleurs.zip");
        Directory.CreateDirectory(_racine);
        File.WriteAllText(ailleurs, "autre chose");
        var compteur = new Compteur();

        var lu = Temoins(compteur, (_, _) => Task.FromResult(ailleurs)).Lire(temoin, "gb");

        Assert.False(lu!.Lu);
        Assert.Equal(1, compteur.Extractions);
        AucunDossierProvisoire();
    }

    [Fact]
    public void Un_paquet_corrompu_est_un_echec_de_l_entree_jamais_une_exception_et_n_est_pas_retente()
    {
        var temoin = Poser()[0];
        var compteur = new Compteur();
        var temoins = Temoins(compteur, async (dossier, _) =>
        {
            await File.WriteAllTextAsync(Path.Combine(dossier, "debut.zip"), "a moitie");
            throw new InvalidDataException("bloc deflate corrompu");
        });

        var premier = temoins.Lire(temoin, "gb");
        var second = temoins.Lire(temoin, "gb");

        Assert.False(premier!.Lu);
        Assert.False(second!.Lu);
        Assert.Equal(1, compteur.Extractions);
        AucunDossierProvisoire();
    }

    [Fact]
    public void Un_delai_depasse_arrete_l_extraction_efface_le_dossier_et_ecarte_tout_le_paquet_sept_jours()
    {
        var temoinsPoses = Poser("sha-1", "Tetris (World) (Rev 1).zip", "Tetris 2 (USA, Europe) (Rev 1) (SGB Enhanced).zip");
        var compteur = new Compteur();
        Func<string, CancellationToken, Task<string>> sansFin = async (dossier, jeton) =>
        {
            await File.WriteAllTextAsync(Path.Combine(dossier, "partiel.zip"), "partiel", CancellationToken.None);
            await Task.Delay(Timeout.Infinite, jeton);
            return string.Empty;
        };

        var lu = Temoins(compteur, sansFin, delai: TimeSpan.FromMilliseconds(200)).Lire(temoinsPoses[0], "gb");
        Assert.False(lu!.Lu);
        Assert.Contains("trop longue", lu.Raison);
        Assert.Equal(1, compteur.Extractions);
        AucunDossierProvisoire();

        // Un redemarrage (nouvelle instance, seul le cache disque reste) : ni cette entree ni l'autre du meme paquet.
        var apres = Temoins(compteur, sansFin, delai: TimeSpan.FromMilliseconds(200));
        Assert.False(apres.Lire(temoinsPoses[0], "gb")!.Lu);
        Assert.False(apres.Lire(temoinsPoses[1], "gb")!.Lu);
        Assert.Equal(1, compteur.Extractions);

        // Huit jours plus tard, une nouvelle chance.
        _maintenant = _maintenant.AddDays(8);
        Assert.True(Temoins(compteur).Lire(temoinsPoses[1], "gb")!.Lu);
        Assert.Equal(2, compteur.Extractions);
    }

    [Fact]
    public void Ce_qui_sort_du_paquet_au_dela_du_plafond_arrete_l_extraction()
    {
        var temoin = Poser()[0];
        var compteur = new Compteur();
        var jetonVu = false;
        var lu = Temoins(compteur, async (dossier, jeton) =>
        {
            await using (var flux = new FileStream(Path.Combine(dossier, "bombe.zip"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                await flux.WriteAsync(new byte[5000], CancellationToken.None);
                await flux.FlushAsync(CancellationToken.None);
                try { await Task.Delay(Timeout.Infinite, jeton); }
                catch (OperationCanceledException) { jetonVu = true; throw; }
            }

            return string.Empty;
        }, plafond: 1000).Lire(temoin, "gb");

        Assert.False(lu!.Lu);
        Assert.Contains("trop grosse", lu.Raison);
        Assert.True(jetonVu, "l'extracteur n'a pas ete arrete");
        AucunDossierProvisoire();
    }

    [Fact]
    public void Une_partie_qui_commence_pendant_la_lecture_l_arrete_sans_compter_d_echec()
    {
        var temoin = Poser()[0];
        var compteur = new Compteur();
        var partie = false;
        var temoins = Temoins(compteur, async (dossier, jeton) =>
        {
            if (compteur.Extractions == 1)
            {
                partie = true;
                await Task.Delay(Timeout.Infinite, jeton);
                return string.Empty;
            }

            var sortie = Path.Combine(dossier, "tetris.zip");
            await File.WriteAllTextAsync(sortie, "ROM", jeton);
            return sortie;
        });
        var lu = temoins.Lire(temoin, "gb", () => partie);

        Assert.False(lu!.Lu);
        Assert.Contains("interrompue", lu.Raison);
        AucunDossierProvisoire();

        // La passe d'apres la partie relit (meme instance : ce n'etait pas un echec).
        partie = false;
        var relu = temoins.Lire(temoin, "gb", () => partie);
        Assert.True(relu!.Lu);
        Assert.Equal(2, compteur.Extractions);
    }

    [Fact]
    public void Rien_n_est_extrait_quand_une_partie_tourne_deja()
    {
        var temoin = Poser()[0];
        var compteur = new Compteur();

        var lu = Temoins(compteur).Lire(temoin, "gb", () => true);

        Assert.False(lu!.Lu);
        Assert.Equal(0, compteur.Extractions);
        AucunDossierProvisoire();
    }

    [Fact]
    public void Un_paquet_remplace_depuis_son_index_n_est_pas_cru()
    {
        var temoin = Poser()[0];
        var compteur = new Compteur();
        File.WriteAllText(Paquet, "un autre paquet, plus long");

        var lu = Temoins(compteur).Lire(temoin, "gb");

        Assert.False(lu!.Lu);
        Assert.Contains("modifié", lu.Raison);
        Assert.Equal(0, compteur.Extractions);
    }

    [Fact]
    public void Un_paquet_sans_empreinte_dans_l_index_n_est_pas_lu()
    {
        var temoin = Poser(sha256: "")[0];
        var compteur = new Compteur();

        Assert.False(Temoins(compteur).Lire(temoin, "gb")!.Lu);
        Assert.Equal(0, compteur.Extractions);
    }

    [Fact]
    public void Une_nouvelle_version_du_paquet_fait_relire_l_entree()
    {
        var temoin = Poser("sha-1")[0];
        var compteur = new Compteur();
        var avant = Temoins(compteur, contenu: Encoding.ASCII.GetBytes("V1")).Lire(temoin, "gb");
        Assert.Equal(Md5(Encoding.ASCII.GetBytes("V1")), avant!.Md5);

        // Le gestionnaire a remesure le paquet : autre sha256 dans l'index.
        Poser("sha-2");
        var apres = Temoins(compteur, contenu: Encoding.ASCII.GetBytes("V2")).Lire(temoin, "gb");

        Assert.Equal(2, compteur.Extractions);
        Assert.Equal(Md5(Encoding.ASCII.GetBytes("V2")), apres!.Md5);
        // L'ancienne version est sortie du cache.
        Assert.DoesNotContain("sha-1", File.ReadAllText(Cache));
    }

    [Fact]
    public void Un_dossier_laisse_par_une_api_arretee_est_efface_au_premier_appel()
    {
        var temoin = Poser()[0];
        var reste = Path.Combine(Provisoire, "empreinte-0123456789abcdef");
        Directory.CreateDirectory(reste);
        File.WriteAllText(Path.Combine(reste, "partiel.zip"), "partiel");
        var autre = Path.Combine(Provisoire, "on-the-fly-a-garder");
        Directory.CreateDirectory(autre);

        Assert.True(Temoins(new Compteur()).Lire(temoin, "gb")!.Lu);

        Assert.False(Directory.Exists(reste));
        Assert.True(Directory.Exists(autre), "un dossier du gestionnaire a ete efface");
    }

    [Fact]
    public void Un_fichier_qui_n_est_pas_un_temoin_n_est_pas_lu()
    {
        Directory.CreateDirectory(_racine);
        var rom = Path.Combine(_racine, "Tetris.gb");
        File.WriteAllBytes(rom, new byte[2048]);
        var compteur = new Compteur();

        Assert.Null(Temoins(compteur).Lire(rom, "gb"));
        Assert.False(EmpreintesDesTemoins.EstUnTemoin(rom));
        Assert.Equal(0, compteur.Extractions);
    }
}
