using System.IO.Compression;
using System.Security.Cryptography;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEPART PAR PLACE AVEC LE COEUR PSP « nelfeplay-netpacket-resume » (19/11) : les etats sont des enveloppes NLFPSSP1
/// capturees ensemble par les deux PSP (octet 16 = numero netpacket du joueur : 0 l'hote, 1 l'invite). L'API n'en lit rien :
/// elle pose l'archive de la place, decompressee, au chemin canonique du coeur. Ces tests verifient, sans gameroom, que
/// l'hote recoit toujours l'enveloppe du joueur 0, qu'il ait la place 1 ou la place 2 de la plateforme.
/// Avec NELFEPLAY_PSP_ETATS=&lt;dossier des deux .gz captures&gt;, le dernier test pose les VRAIS etats dans
/// NELFEPLAY_PSP_BIOS=&lt;dossier&gt;\{hote,invite}\PPSSPP\netplay\ULUS10121.state, pour une reprise au banc.
/// </summary>
public sealed class MultiplayerDepartPspNetpacketTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "depart-psp-" + Guid.NewGuid().ToString("N")[..8]);
    private string Catalogue => Path.Combine(_racine, "multiplayer");
    private const string Contenu = "ca29f6886726a2d5e8e859cfbd0f41aa";
    private const string Chemin = "PPSSPP/netplay/ULUS10121.state";

    public MultiplayerDepartPspNetpacketTests() => Directory.CreateDirectory(Path.Combine(Catalogue, "states", "psp", "bomberman"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
    }

    /// <summary>Une enveloppe minimale : le marqueur et le numero du joueur, la ou le coeur les lit.</summary>
    private static byte[] Enveloppe(int joueurNetpacket)
    {
        var octets = new byte[168 + 32];
        "NLFPSSP1"u8.CopyTo(octets);
        BitConverter.GetBytes(joueurNetpacket).CopyTo(octets, 16);
        BitConverter.GetBytes(2).CopyTo(octets, 20);
        return octets;
    }

    private string Archive(string nom, byte[] clair)
    {
        var chemin = Path.Combine(Catalogue, "states", "psp", "bomberman", nom);
        using (var fichier = File.Create(chemin))
        using (var gz = new GZipStream(fichier, CompressionLevel.Optimal))
        {
            gz.Write(clair);
        }

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(chemin)));
    }

    private MultiplayerGame Bomberman(string shaP1, string shaP2, string p1 = "manche1-p1-netpacket.state.gz", string p2 = "manche1-p2-netpacket.state.gz") => new()
    {
        Id = "psp/bomberman", System = "psp", Players = 2, Level = MultiplayerLevels.Classique,
        Content = new MultiplayerContent { Md5 = [Contenu] },
        Options = new Dictionary<string, string> { ["ppsspp_retroarch_netplay_start"] = "enabled" },
        CoreStart = new MultiplayerCoreStart
        {
            Path = Chemin, Players = 2, Content = Contenu,
            Seats =
            [
                new() { Seat = 1, State = "states/psp/bomberman/" + p1, Sha256 = shaP1 },
                new() { Seat = 2, State = "states/psp/bomberman/" + p2, Sha256 = shaP2 },
            ],
        },
    };

    private static MultiplayerRoom Room(int placeDeLHote) => new()
    {
        Players =
        [
            new() { Handle = "hote", Host = true, Seat = placeDeLHote },
            new() { Handle = "invite", Seat = 3 - placeDeLHote },
        ],
    };

    private static int JoueurPose(string fichier) => BitConverter.ToInt32(File.ReadAllBytes(fichier), 16);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void L_hote_recoit_l_enveloppe_du_joueur_0_quelle_que_soit_sa_place(int placeDeLHote)
    {
        var jeu = new MultiplayerInstalledGame(Bomberman(Archive("manche1-p1-netpacket.state.gz", Enveloppe(0)),
            Archive("manche1-p2-netpacket.state.gz", Enveloppe(1))), "Bomberman (USA).iso", null, Contenu);
        var partie = MultiplayerOrdreNetplay.PourLaRoom(jeu, Room(placeDeLHote));

        var biosHote = Path.Combine(_racine, "bios-hote");
        var biosInvite = Path.Combine(_racine, "bios-invite");
        var hote = MultiplayerDepartDuCoeur.Poser(partie, 2, placeDeLHote, Catalogue, biosHote);
        var invite = MultiplayerDepartDuCoeur.Poser(partie, 2, 3 - placeDeLHote, Catalogue, biosInvite);

        Assert.True(hote.Ok, hote.Raison);
        Assert.True(invite.Ok, invite.Raison);
        Assert.Equal(Path.Combine(biosHote, "PPSSPP", "netplay", "ULUS10121.state"), hote.Fichier);
        Assert.Equal(0, JoueurPose(hote.Fichier!));
        Assert.Equal(1, JoueurPose(invite.Fichier!));
    }

    [Fact]
    public void Les_options_du_jeu_allument_le_depart_que_le_systeme_eteint()
    {
        var catalogue = new MultiplayerCatalogue
        {
            Cores = new Dictionary<string, MultiplayerCore>
            {
                ["psp"] = new()
                {
                    Core = "ppsspp_netplay",
                    Options = new Dictionary<string, string>
                    {
                        ["ppsspp_retroarch_netplay"] = "enabled", ["ppsspp_retroarch_netplay_start"] = "disabled",
                        ["ppsspp_retroarch_netplay_capture"] = "disabled",
                    },
                },
            },
        };

        var options = catalogue.CoreOf(Bomberman("00", "00"))!.Options!;
        Assert.Equal("enabled", options["ppsspp_retroarch_netplay_start"]);
        Assert.Equal("disabled", options["ppsspp_retroarch_netplay_capture"]);
        var autreJeu = catalogue.CoreOf(new MultiplayerGame { Id = "psp/autre", System = "psp", Players = 2 })!.Options!;
        Assert.Equal("disabled", autreJeu["ppsspp_retroarch_netplay_start"]);
    }

    /// <summary>
    /// Avec NELFEPLAY_PSP_CATALOGUE=&lt;catalogue.json d'un Data Pack&gt; : le catalogue est sain, Bomberman y a ses deux etats a
    /// l'empreinte du catalogue, et une API plus ancienne que son min_api (le coeur livre avec elle parle un autre protocole)
    /// ne le propose pas.
    /// </summary>
    [Fact]
    public void Le_catalogue_qui_rend_bomberman_est_sain()
    {
        var chemin = Environment.GetEnvironmentVariable("NELFEPLAY_PSP_CATALOGUE");
        if (string.IsNullOrEmpty(chemin))
        {
            return;
        }

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
        var brut = System.Text.Json.JsonSerializer.Deserialize<MultiplayerCatalogue>(File.ReadAllText(chemin), options);
        Assert.Null(MultiplayerCatalogueStore.Valider(brut));
        var recent = new MultiplayerCatalogueStore(logger: null, path: chemin, api: new Version(99, 0)).Lire(out var erreur);
        Assert.Null(erreur);
        var bomberman = Assert.Single(recent!.Games, j => j.Id == "psp/bomberman");
        Assert.Equal("enabled", recent.CoreOf(bomberman)!.Options!["ppsspp_retroarch_netplay_start"]);
        var dossier = Path.GetDirectoryName(chemin)!;
        foreach (var place in bomberman.CoreStart!.Seats)
        {
            var octets = File.ReadAllBytes(Path.Combine(dossier, place.State.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(place.Sha256, Convert.ToHexString(SHA256.HashData(octets)), ignoreCase: true);
        }

        var ancienne = new MultiplayerCatalogueStore(logger: null, path: chemin, api: new Version(1, 9, 200)).Lire(out _);
        Assert.DoesNotContain(ancienne!.Games, j => j.Id == "psp/bomberman");
    }

    [Fact]
    public void Les_vrais_etats_du_banc_se_posent_pour_un_hote_a_la_place_2()
    {
        var source = Environment.GetEnvironmentVariable("NELFEPLAY_PSP_ETATS");
        var sortie = Environment.GetEnvironmentVariable("NELFEPLAY_PSP_BIOS");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sortie))
        {
            return;
        }

        string Copier(string nom)
        {
            File.Copy(Path.Combine(source, nom), Path.Combine(Catalogue, "states", "psp", "bomberman", nom), overwrite: true);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, nom))));
        }

        var jeu = new MultiplayerInstalledGame(Bomberman(Copier("manche1-p1-netpacket.state.gz"), Copier("manche1-p2-netpacket.state.gz")),
            "Bomberman (USA).iso", null, Contenu);
        var partie = MultiplayerOrdreNetplay.PourLaRoom(jeu, Room(2));
        var hote = MultiplayerDepartDuCoeur.Poser(partie, 2, 2, Catalogue, Path.Combine(sortie, "hote"));
        var invite = MultiplayerDepartDuCoeur.Poser(partie, 2, 1, Catalogue, Path.Combine(sortie, "invite"));

        Assert.True(hote.Ok, hote.Raison);
        Assert.True(invite.Ok, invite.Raison);
        Assert.Equal(0, JoueurPose(hote.Fichier!));
        Assert.Equal(1, JoueurPose(invite.Fichier!));
    }
}
