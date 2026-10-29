using System.Security.Cryptography;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEPART PAR PLACE (29/10, Bomberman PSP) : chaque borne pose l'etat de sa place la ou le coeur le cherche, efface celui
/// d'une partie d'avant, et ne part pas si l'etat de sa place manque ou a ete retouche.
/// </summary>
public sealed class MultiplayerDepartDuCoeurTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "depart-coeur-" + Guid.NewGuid().ToString("N")[..8]);
    private string Catalogue => Path.Combine(_racine, "multiplayer");
    private string Systeme => Path.Combine(_racine, "bios");
    private string Cible => Path.Combine(Systeme, "PPSSPP", "netplay", "ULUS10121.state");

    private const string Contenu = "0123456789abcdef0123456789abcdef";

    public MultiplayerDepartDuCoeurTests()
    {
        Directory.CreateDirectory(Path.Combine(Catalogue, "states", "psp", "bomberman"));
        Directory.CreateDirectory(Systeme);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
    }

    private string Etat(string nom, byte octet)
    {
        var octets = Enumerable.Repeat(octet, 64).ToArray();
        File.WriteAllBytes(Path.Combine(Catalogue, "states", "psp", "bomberman", nom), octets);
        return Convert.ToHexString(SHA256.HashData(octets));
    }

    private MultiplayerGame Jeu() => new()
    {
        Id = "psp/bomberman", System = "psp", Players = 2, Level = MultiplayerLevels.Classique,
        Content = new MultiplayerContent { Md5 = [Contenu] },
        CoreStart = new MultiplayerCoreStart
        {
            Path = "PPSSPP/netplay/ULUS10121.state", Players = 2, Content = Contenu,
            Seats =
            [
                new() { Seat = 1, State = "states/psp/bomberman/p1.state", Sha256 = Etat("p1.state", 1) },
                new() { Seat = 2, State = "states/psp/bomberman/p2.state", Sha256 = Etat("p2.state", 2) },
            ],
        },
    };

    [Fact]
    public void Chaque_place_pose_son_propre_etat_la_ou_le_coeur_le_cherche()
    {
        var jeu = new MultiplayerInstalledGame(Jeu(), "Bomberman (USA).zip", null, Contenu);

        var hote = MultiplayerDepartDuCoeur.Poser(jeu, 2, 1, Catalogue, Systeme);
        Assert.True(hote.Ok);
        Assert.Equal(Cible, hote.Fichier);
        Assert.Equal(1, File.ReadAllBytes(Cible)[0]);

        var invite = MultiplayerDepartDuCoeur.Poser(jeu, 2, 2, Catalogue, Systeme);
        Assert.True(invite.Ok);
        Assert.Equal(2, File.ReadAllBytes(Cible)[0]);

        MultiplayerDepartDuCoeur.Retirer(invite.Fichier);
        Assert.False(File.Exists(Cible));
    }

    [Fact]
    public void Un_autre_contenu_ou_un_autre_nombre_part_sans_etat_et_efface_celui_d_avant()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Cible)!);
        File.WriteAllBytes(Cible, [9]);   // l'etat oublie d'une partie d'avant
        var europe = new MultiplayerInstalledGame(Jeu(), "Bomberman (Europe).zip", null, "ffffffffffffffffffffffffffffffff");

        var resultat = MultiplayerDepartDuCoeur.Poser(europe, 2, 1, Catalogue, Systeme);

        Assert.True(resultat.Ok);
        Assert.Null(resultat.Fichier);
        Assert.False(File.Exists(Cible));
        var aTrois = MultiplayerDepartDuCoeur.Poser(new MultiplayerInstalledGame(Jeu(), "b.zip", null, Contenu), 3, 1, Catalogue, Systeme);
        Assert.True(aTrois.Ok);
        Assert.Null(aTrois.Fichier);
    }

    [Fact]
    public void Un_etat_retouche_ou_absent_empeche_la_borne_de_partir()
    {
        var jeu = new MultiplayerInstalledGame(Jeu(), "Bomberman (USA).zip", null, Contenu);
        File.WriteAllBytes(Path.Combine(Catalogue, "states", "psp", "bomberman", "p2.state"), [7, 7, 7]);

        var retouche = MultiplayerDepartDuCoeur.Poser(jeu, 2, 2, Catalogue, Systeme);
        Assert.False(retouche.Ok);
        Assert.Contains("empreinte", retouche.Raison);
        Assert.False(File.Exists(Cible));

        File.Delete(Path.Combine(Catalogue, "states", "psp", "bomberman", "p1.state"));
        Assert.False(MultiplayerDepartDuCoeur.Poser(jeu, 2, 1, Catalogue, Systeme).Ok);
    }

    [Fact]
    public void Un_etat_livre_en_gzip_se_pose_decompresse()
    {
        var clair = Enumerable.Range(0, 4096).Select(i => (byte)(i % 7)).ToArray();
        using (var fichier = File.Create(Path.Combine(Catalogue, "states", "psp", "bomberman", "p1.state.gz")))
        using (var gz = new System.IO.Compression.GZipStream(fichier, System.IO.Compression.CompressionLevel.Optimal))
        {
            gz.Write(clair);
        }
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Catalogue, "states", "psp", "bomberman", "p1.state.gz"))));
        var depart = Jeu().CoreStart! with { Seats = [new() { Seat = 1, State = "states/psp/bomberman/p1.state.gz", Sha256 = sha }, Jeu().CoreStart!.Seats[1]] };
        var jeu = new MultiplayerInstalledGame(Jeu() with { CoreStart = depart }, "b.zip", null, Contenu);

        var resultat = MultiplayerDepartDuCoeur.Poser(jeu, 2, 1, Catalogue, Systeme);

        Assert.True(resultat.Ok);
        Assert.Equal(clair, File.ReadAllBytes(Cible));
    }

    [Fact]
    public void Un_jeu_sans_depart_par_place_ne_touche_a_rien()
    {
        var jeu = new MultiplayerInstalledGame(Jeu() with { CoreStart = null }, "b.zip", null, Contenu);
        Assert.Equal(new MultiplayerDepartDuCoeur.Resultat(true, null, null), MultiplayerDepartDuCoeur.Poser(jeu, 2, 1, Catalogue, Systeme));
    }

    [Theory]
    [InlineData("chemin", "chemin hors du dossier systeme")]
    [InlineData("place", "pas d'etat pour la place 2")]
    [InlineData("doublon", "plusieurs etats pour la place 1")]
    [InlineData("empreinte", "empreinte de l'etat manquante")]
    [InlineData("dossier", "etat hors du dossier MULTIPLAYER")]
    [InlineData("contenu", "contenu hors du catalogue")]
    public void Le_catalogue_refuse_un_depart_par_place_douteux(string defaut, string raison)
    {
        var depart = Jeu().CoreStart!;
        var premier = depart.Seats[0];
        depart = defaut switch
        {
            "chemin" => depart with { Path = "../retroarch/retroarch.cfg" },
            "place" => depart with { Seats = [premier] },
            "doublon" => depart with { Seats = [premier, premier, depart.Seats[1]] },
            "empreinte" => depart with { Seats = [premier with { Sha256 = "abc" }, depart.Seats[1]] },
            "dossier" => depart with { Seats = [premier with { State = "../etat.state" }, depart.Seats[1]] },
            _ => depart with { Content = "ffffffffffffffffffffffffffffffff" },
        };

        Assert.Contains(raison, MultiplayerCatalogueStore.ValiderLeDepartDuCoeur(Jeu() with { CoreStart = depart }));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepartDuCoeur(Jeu()));
    }
}
