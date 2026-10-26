using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES JEUX RESTES DANS LES PAQUETS DU GESTIONNAIRE DE ROM (26/10) : un fichier d'attente se reconnait au contenu du jeu
/// dans son paquet, lu une fois ; une partie qui tourne arrete la lecture ; la collection MULTIPLAYER retient le fichier.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerContenusDesPaquetsTests : IDisposable
{
    private const string Marque = "APIEXPOSE_ON_THE_FLY_ROM_PLACEHOLDER_v1";

    private readonly string _racine = Path.Combine(Path.GetTempPath(), "mp-paquets-" + Guid.NewGuid().ToString("N")[..8]);

    internal string Roms => Path.Combine(_racine, "roms");

    private string Index => Path.Combine(_racine, "index.json");

    private string Cache => Path.Combine(_racine, "state", "contenus-des-paquets.json");

    private string Paquet => Path.Combine(_racine, "paquets", "snes.rar");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    /// <summary>Un paquet SNES a un jeu, son fichier d'attente dans roms/snes, et l'extracteur qui en sort le zip.</summary>
    /// <param name="roms">Le dossier roms ou poser le fichier d'attente (celui des tests de la collection) ; par defaut le sien.</param>
    internal (MultiplayerContenusDesPaquets Paquets, string Temoin, string Md5) Monter(Func<string, string, CancellationToken, Task>? extraire = null,
        string? roms = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paquet)!);
        File.WriteAllText(Paquet, "paquet");
        var octets = Encoding.UTF8.GetBytes("la cartouche de Wild Guns");
        var md5 = Convert.ToHexString(MD5.HashData(octets)).ToLowerInvariant();
        File.WriteAllText(Index, JsonSerializer.Serialize(new
        {
            Packs = new[]
            {
                new
                {
                    PackagePath = Paquet, Sha256 = "abc", Size = 6L, SystemId = "snes", OnTheFlyMode = true,
                    Roms = new[] { new { SystemId = "snes", GameName = "Wild Guns", GamePath = "./Wild Guns (Europe).zip",
                        DestinationFileName = "Wild Guns (Europe).zip", PackRelativePath = "Wild Guns (Europe).zip",
                        ArchiveEntryPath = "snes/Wild Guns (Europe).zip", Size = 319L } },
                },
            },
        }));
        var temoin = Path.Combine(roms ?? Roms, "snes", "Wild Guns (Europe).zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temoin)!);
        File.WriteAllText(temoin, Marque + Environment.NewLine + "This lightweight file keeps EmulationStation from pruning an on-the-fly ROM");
        extraire ??= (paquet, dossier, _) =>
        {
            var zip = Path.Combine(dossier, "snes", "Wild Guns (Europe).zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            using var flux = archive.CreateEntry("Wild Guns (Europe).sfc").Open();
            flux.Write(octets);
            return Task.CompletedTask;
        };
        var paquets = new MultiplayerContenusDesPaquets(Index, Cache, extraire, NelfePlayScoringCollectionSyncService.Md5DuContenu,
            RomPackInstallerService.EstUnTemoin);
        return (paquets, temoin, md5);
    }

    internal static readonly IReadOnlySet<string> Snes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "snes" };

    [Fact]
    public async Task Un_paquet_lu_une_fois_fait_reconnaitre_ses_fichiers_d_attente()
    {
        var (paquets, temoin, md5) = Monter();
        Assert.Null(paquets.Md5DuTemoin(temoin));
        Assert.Single(paquets.ALire(Snes));

        Assert.True(await paquets.LireAsync(Snes, () => false, CancellationToken.None));

        Assert.Equal(md5, paquets.Md5DuTemoin(temoin));
        Assert.Empty(paquets.ALire(Snes));
        Assert.True(File.Exists(Cache));
        // Le dossier provisoire, a cote du paquet, est efface.
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(Paquet)!));
        // Relu depuis le disque par une autre instance : rien a refaire.
        var apres = new MultiplayerContenusDesPaquets(Index, Cache, (_, _, _) => throw new InvalidOperationException("relu"),
            NelfePlayScoringCollectionSyncService.Md5DuContenu, RomPackInstallerService.EstUnTemoin);
        Assert.Equal(md5, apres.Md5DuTemoin(temoin));
        // Un vrai jeu n'est pas un fichier d'attente.
        var vrai = Path.Combine(Roms, "snes", "Pop'n TwinBee (Europe).zip");
        File.WriteAllBytes(vrai, new byte[10_000]);
        Assert.Null(paquets.Md5DuTemoin(vrai));
    }

    [Fact]
    public async Task Une_partie_qui_tourne_empeche_la_lecture()
    {
        var (paquets, temoin, _) = Monter();

        Assert.False(await paquets.LireAsync(Snes, () => true, CancellationToken.None));

        Assert.Null(paquets.Md5DuTemoin(temoin));
        Assert.Single(paquets.ALire(Snes));
        Assert.False(File.Exists(Cache));
    }
}
