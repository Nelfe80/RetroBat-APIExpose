using System.Globalization;
using System.Security.Cryptography;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

public class MultiplayerDepartPretTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "mp-depart-" + Guid.NewGuid().ToString("N"));

    public MultiplayerDepartPretTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    /// <summary>Un RetroArch de test : une memoire, un dossier d'etats, et les commandes recues.</summary>
    private sealed class FauxRetroArch : ICommandesRetroArch
    {
        public readonly Dictionary<long, int> Memoire = new();
        public readonly List<string> Recues = new();
        public bool Muet { get; init; }
        public string DossierDesEtats { get; init; } = string.Empty;
        public long? AdresseQuiResiste { get; init; }

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            Recues.Add(commande);
            if (Muet)
            {
                return Task.FromResult<string?>(null);
            }

            var morceaux = commande.Split(' ');
            return Task.FromResult<string?>(morceaux[0] switch
            {
                "GET_STATUS" => "GET_STATUS PLAYING fb_alpha,sf2ce,crc32=2090400e",
                "GET_CONFIG_PARAM" => "GET_CONFIG_PARAM savestate_directory " + DossierDesEtats,
                "READ_CORE_RAM" => $"READ_CORE_RAM {morceaux[1]} {Memoire.GetValueOrDefault(long.Parse(morceaux[1], NumberStyles.HexNumber)):x2}",
                _ => null,
            });
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            Recues.Add(commande);
            var morceaux = commande.Split(' ');
            if (morceaux[0] == "WRITE_CORE_RAM")
            {
                var adresse = long.Parse(morceaux[1], NumberStyles.HexNumber);
                if (adresse != AdresseQuiResiste)
                {
                    Memoire[adresse] = int.Parse(morceaux[2], NumberStyles.HexNumber);
                }
            }

            return Task.CompletedTask;
        }
    }

    private (MultiplayerGame Jeu, string Etat) Sf2ce(byte[]? contenu = null)
    {
        var etat = Path.Combine(_dossier, "selection-2p-fbneo.state");
        File.WriteAllBytes(etat, contenu ?? [1, 2, 3, 4]);
        var jeu = new MultiplayerGame
        {
            Id = "arcade/sf2ce", System = "arcade", Set = "sf2ce", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = new MultiplayerStart { State = "states/arcade/sf2ce/selection-2p-fbneo.state", Sha256 = Convert.ToHexString(SHA256.HashData(contenu ?? [1, 2, 3, 4])) },
            Choices =
            [
                new MultiplayerChoice
                {
                    Key = "character", Default = 0,
                    Values = [new() { Value = 0, Name = "RYU" }, new() { Value = 4, Name = "KEN" }, new() { Value = 5, Name = "CHUN-LI" }],
                    Seats = [new() { Seat = 1, CoreRam = "0x864E" }, new() { Seat = 2, CoreRam = "0x894E" }],
                },
            ],
        };
        return (jeu, etat);
    }

    private static Dictionary<int, IReadOnlyDictionary<string, int>?> Places(int? p1, int? p2) => new()
    {
        [1] = p1 is { } a ? new Dictionary<string, int> { ["character"] = a } : null,
        [2] = p2 is { } b ? new Dictionary<string, int> { ["character"] = b } : null,
    };

    [Fact]
    public async Task Le_depart_pose_l_etat_ecrit_chaque_place_et_relit()
    {
        var (jeu, etat) = Sf2ce();
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var resultat = await new MultiplayerDepartPret(ra, TimeSpan.FromSeconds(2), TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "sf2ce", Places(4, 5), CancellationToken.None);

        Assert.True(resultat.Ok, resultat.Raison);
        Assert.Equal(Path.Combine(ra.DossierDesEtats, "sf2ce.state97"), resultat.Fichier);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(resultat.Fichier!));
        Assert.Equal(4, ra.Memoire[0x864E]);
        Assert.Equal(5, ra.Memoire[0x894E]);
        // L'etat d'abord, les ecritures ensuite : ecrire avant le chargement serait efface par lui.
        Assert.True(ra.Recues.IndexOf("LOAD_STATE_SLOT 97") < ra.Recues.IndexOf("WRITE_CORE_RAM 864e 04"));
    }

    [Fact]
    public void Un_choix_absent_ou_hors_liste_prend_le_defaut()
    {
        var (jeu, _) = Sf2ce();
        var ecritures = MultiplayerDepartPret.Ecritures(jeu, Places(7, null));
        Assert.Equal([(0x864EL, 0), (0x894EL, 0)], ecritures);
    }

    [Fact]
    public async Task Un_etat_retouche_ne_se_charge_pas()
    {
        var (jeu, etat) = Sf2ce();
        File.WriteAllBytes(etat, [9, 9, 9]);
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var resultat = await new MultiplayerDepartPret(ra, TimeSpan.FromSeconds(1), TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "sf2ce", Places(4, 5), CancellationToken.None);

        Assert.False(resultat.Ok);
        Assert.Contains("empreinte", resultat.Raison);
        Assert.Empty(ra.Recues);
    }

    [Fact]
    public async Task Sans_reponse_de_RetroArch_le_depart_est_annule()
    {
        var (jeu, etat) = Sf2ce();
        var resultat = await new MultiplayerDepartPret(new FauxRetroArch { Muet = true }, TimeSpan.FromMilliseconds(600), TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "sf2ce", Places(4, 5), CancellationToken.None);

        Assert.False(resultat.Ok);
        Assert.Equal("RetroArch ne repond pas", resultat.Raison);
        Assert.Null(resultat.Fichier);
    }

    [Fact]
    public async Task Une_ecriture_qui_ne_prend_pas_annule_le_depart_et_rend_l_etat_a_retirer()
    {
        var (jeu, etat) = Sf2ce();
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats"), AdresseQuiResiste = 0x894E };
        var resultat = await new MultiplayerDepartPret(ra, TimeSpan.FromSeconds(2), TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "sf2ce", Places(4, 5), CancellationToken.None);

        Assert.False(resultat.Ok);
        Assert.Contains("0x894E", resultat.Raison);
        Assert.NotNull(resultat.Fichier);
    }

    [Fact]
    public void Le_catalogue_exige_le_depart_pret_et_une_adresse_par_place()
    {
        var (jeu, _) = Sf2ce();
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        Assert.Equal("niveau NelfePlay sans depart pret", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Start = null }));
        Assert.Equal("chemin de l'etat hors du dossier MULTIPLAYER",
            MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Start = jeu.Start! with { State = "../../retroarch.cfg" } }));
        Assert.Equal("empreinte de l'etat manquante", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Start = jeu.Start! with { Sha256 = "" } }));
        var sansP2 = jeu with { Choices = [jeu.Choices![0] with { Seats = [new() { Seat = 1, CoreRam = "0x864E" }] }] };
        Assert.Equal("choix « character » : pas d'adresse pour la place 2", MultiplayerCatalogueStore.ValiderLeDepart(sansP2));
        var defautHors = jeu with { Choices = [jeu.Choices![0] with { Default = 9 }] };
        Assert.Equal("choix « character » : defaut hors des valeurs", MultiplayerCatalogueStore.ValiderLeDepart(defautHors));
        // Un jeu Classique n'a pas besoin de depart.
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Level = MultiplayerLevels.Classique, Start = null, Choices = null }));
    }

    private static readonly MultiplayerEnd Manches = new()
    {
        FirstTo = 2, CloseAfterS = 5,
        Seats = [new() { Seat = 1, CoreRam = "0x864F" }, new() { Seat = 2, CoreRam = "0x894F" }],
    };

    [Fact]
    public async Task La_premiere_place_a_deux_manches_gagne_confirmee_par_deux_lectures()
    {
        var ra = new FauxRetroArch { DossierDesEtats = _dossier };
        ra.Memoire[0x894F] = 0xFF;   // une valeur d'avant le combat ne gagne rien
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(20));
        var attente = guet.AttendreLeVainqueurAsync(Manches, [1, 2], CancellationToken.None);
        await Task.Delay(80);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x894F] = 1;
        ra.Memoire[0x864F] = 2;
        Assert.Equal(1, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task La_partie_fermee_avant_la_fin_ne_designe_personne()
    {
        using var fin = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var guet = new MultiplayerFinDeRencontre(new FauxRetroArch { DossierDesEtats = _dossier }, TimeSpan.FromMilliseconds(20));
        Assert.Null(await guet.AttendreLeVainqueurAsync(Manches, [1, 2], fin.Token));
    }

    [Fact]
    public void La_fin_de_rencontre_exige_une_adresse_par_place()
    {
        var (jeu, _) = Sf2ce();
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu with { End = Manches }));
        Assert.Equal("fin de rencontre : pas d'adresse pour la place 2",
            MultiplayerCatalogueStore.ValiderLeDepart(jeu with { End = Manches with { Seats = [Manches.Seats[0]] } }));
        Assert.Equal("fin de rencontre incoherente", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { End = Manches with { FirstTo = 0 } }));
    }

    [Fact]
    public void Les_fleches_parcourent_les_personnages_en_boucle()
    {
        var choix = Sf2ce().Jeu.Choices![0];
        Assert.Equal(4, choix.Voisine(0, 1));
        Assert.Equal(0, choix.Voisine(5, 1));
        Assert.Equal(5, choix.Voisine(0, -1));
        Assert.Equal("CHUN-LI", choix.NomDe(5));
    }
}
