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
        /// <summary>La carte memoire du coeur (READ_CORE_MEMORY / WRITE_CORE_MEMORY), a part de la memoire systeme.</summary>
        public readonly Dictionary<long, int> Carte = new();
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
                "READ_CORE_RAM" => $"READ_CORE_RAM {morceaux[1]} {string.Join(' ', Adresses(morceaux).Select(a => Memoire.GetValueOrDefault(a).ToString("x2")))}",
                "READ_CORE_MEMORY" => Adresses(morceaux).All(Carte.ContainsKey)
                    ? $"READ_CORE_MEMORY {morceaux[1]} {string.Join(' ', Adresses(morceaux).Select(a => Carte[a].ToString("X2")))}"
                    : $"READ_CORE_MEMORY {morceaux[1]} -1 no descriptor for address",
                _ => null,
            });
        }

        private static IEnumerable<long> Adresses(string[] morceaux)
        {
            var debut = long.Parse(morceaux[1], NumberStyles.HexNumber);
            var nombre = morceaux.Length > 2 ? int.Parse(morceaux[2], CultureInfo.InvariantCulture) : 1;
            return Enumerable.Range(0, nombre).Select(i => debut + i);
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            Recues.Add(commande);
            var morceaux = commande.Split(' ');
            if (morceaux[0] is "WRITE_CORE_MEMORY" or "WRITE_CORE_RAM")
            {
                var debut = long.Parse(morceaux[1], NumberStyles.HexNumber);
                for (var i = 2; i < morceaux.Length; i++)
                {
                    var adresse = debut + i - 2;
                    var valeur = int.Parse(morceaux[i], NumberStyles.HexNumber);
                    if (morceaux[0] == "WRITE_CORE_MEMORY")
                    {
                        Carte[adresse] = valeur;
                    }
                    else if (adresse != AdresseQuiResiste)
                    {
                        Memoire[adresse] = valeur;
                    }
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
    public async Task Le_depart_se_reprend_choix_compris_pour_la_boucle()
    {
        var (jeu, etat) = Sf2ce();
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var depart = new MultiplayerDepartPret(ra, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        MultiplayerDepartPret.ApresUnRechargement = TimeSpan.Zero;
        Assert.True((await depart.PreparerAsync(jeu, etat, "sf2ce", Places(4, 5), CancellationToken.None)).Ok);
        // Le rechargement remet la memoire de l'etat : les choix y sont reecrits, puis relus.
        ra.Memoire.Clear();
        ra.Recues.Clear();
        Assert.True(await depart.ReappliquerAsync(jeu, Places(4, 5), CancellationToken.None));
        Assert.Equal("LOAD_STATE_SLOT 97", ra.Recues[0]);
        Assert.Equal(4, ra.Memoire[0x864E]);
        Assert.Equal(5, ra.Memoire[0x894E]);

        // Un choix qui ne se relit pas : le depart ne se reprend pas.
        var resiste = new FauxRetroArch { DossierDesEtats = ra.DossierDesEtats, AdresseQuiResiste = 0x894E };
        Assert.False(await new MultiplayerDepartPret(resiste, TimeSpan.FromSeconds(2), TimeSpan.Zero).ReappliquerAsync(jeu, Places(4, 5), CancellationToken.None));
    }

    /// <summary>Le temps entre chaque chargement de l'etat et la premiere ecriture qui le suit.</summary>
    private sealed class Chronometre(ICommandesRetroArch ra) : ICommandesRetroArch
    {
        private readonly System.Diagnostics.Stopwatch _horloge = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan? _chargement;
        public readonly List<TimeSpan> Delais = new();

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500) => ra.DemanderAsync(commande, ct, delaiMs);

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            if (commande.StartsWith("LOAD_STATE_SLOT", StringComparison.Ordinal))
            {
                _chargement = _horloge.Elapsed;
            }
            else if (commande.StartsWith("WRITE_CORE", StringComparison.Ordinal) && _chargement is { } depuis)
            {
                Delais.Add(_horloge.Elapsed - depuis);
                _chargement = null;
            }

            return ra.EnvoyerAsync(commande, ct);
        }
    }

    [Fact]
    public async Task Le_delai_des_choix_du_catalogue_vaut_pour_chaque_ecriture()
    {
        // Magical Drop III (CDC Classements v1.1 §1) : 9 images, 150 ms ; ni la premiere pose, ni la boucle, ni le repli ne
        // l'oublient. Les delais habituels sont mis a zero : sans celui du jeu, l'ecriture suivrait le chargement.
        var (jeu, etat) = Sf2ce();
        var depart = jeu.Start! with { WriteAfterFrames = 9 };
        jeu = jeu with { Start = depart };
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var chrono = new Chronometre(ra);
        MultiplayerDepartPret.ApresUnRechargement = TimeSpan.Zero;
        var pret = new MultiplayerDepartPret(chrono, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        Assert.True((await pret.PreparerAsync(jeu, etat, "sf2ce", Places(4, 5), CancellationToken.None, depart)).Ok);
        Assert.True(await pret.ReappliquerAsync(jeu, Places(4, 5), CancellationToken.None, null, depart));

        // Un choix qui ne se relit pas : la reprise et son repli, chacun au delai du jeu.
        var resiste = new Chronometre(new FauxRetroArch { DossierDesEtats = ra.DossierDesEtats, AdresseQuiResiste = 0x894E });
        Assert.False(await new MultiplayerDepartPret(resiste, TimeSpan.FromSeconds(2), TimeSpan.Zero).ReappliquerAsync(jeu, Places(4, 5), CancellationToken.None, null, depart));

        Assert.Equal(2, chrono.Delais.Count);
        Assert.Equal(2, resiste.Delais.Count);
        Assert.All(chrono.Delais.Concat(resiste.Delais), delai => Assert.InRange(delai.TotalMilliseconds, 140, 1500));

        // Sans delai au catalogue, rien ne change : l'ecriture suit le chargement.
        var sans = new Chronometre(new FauxRetroArch { DossierDesEtats = ra.DossierDesEtats });
        Assert.True(await new MultiplayerDepartPret(sans, TimeSpan.FromSeconds(2), TimeSpan.Zero).ReappliquerAsync(jeu, Places(4, 5), CancellationToken.None));
        Assert.InRange(sans.Delais.Single().TotalMilliseconds, 0, 100);
    }

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

    /// <summary>Metal Slug 3 : quatre soldats (Marco 0, Tarma 1, Eri 2, Fio 3), deux joueurs jamais sur le meme.</summary>
    private static MultiplayerGame Mslug3() => new()
    {
        Id = "arcade/mslug3", System = "arcade", Set = "mslug3", Players = 2, Level = MultiplayerLevels.NelfePlay,
        Choices =
        [
            new MultiplayerChoice
            {
                Key = "character", Default = 0, Distinct = true,
                Values = [new() { Value = 0, Name = "MARCO" }, new() { Value = 2, Name = "ERI" }, new() { Value = 1, Name = "TARMA" }, new() { Value = 3, Name = "FIO" }],
                Seats = [new() { Seat = 1, CoreRam = "0x2BB" }, new() { Seat = 2, CoreRam = "0x36B" }],
            },
        ],
    };

    [Theory]
    [InlineData(0, 0, 0, 2)]   // le meme soldat : la place 2 prend le suivant de la liste (Eri)
    [InlineData(3, 3, 3, 0)]   // en bout de liste, on repart au debut
    [InlineData(2, 1, 2, 1)]   // deux soldats differents : rien ne change
    [InlineData(-1, -1, 0, 2)] // deux choix absents : le defaut, puis le suivant libre
    public void Un_choix_distinct_ne_donne_jamais_deux_fois_la_meme_valeur(int voulu1, int voulu2, int ecrit1, int ecrit2)
    {
        var ecritures = MultiplayerDepartPret.Ecritures(Mslug3(), Places(voulu1 < 0 ? null : voulu1, voulu2 < 0 ? null : voulu2));
        Assert.Equal([(0x2BBL, ecrit1), (0x36BL, ecrit2)], ecritures);
    }

    [Fact]
    public void Le_panneau_saute_les_valeurs_deja_prises()
    {
        var choix = Mslug3().Choices![0];
        // Marco vers le bas : Eri, deja prise ; Tarma.
        Assert.Equal(1, choix.VoisineLibre(0, 1, new HashSet<int> { 2 }));
        // Vers le haut depuis Marco : Fio en boucle.
        Assert.Equal(3, choix.VoisineLibre(0, -1, new HashSet<int> { 2 }));
        // Un choix sans le drapeau ne saute rien.
        Assert.Equal(2, (choix with { Distinct = false }).Departager([(1, 2), (2, 2)])[2]);
    }

    /// <summary>
    /// Mario Kart: Super Circuit sous mGBA Splitscreen : le curseur du joueur N est a 0x0202A8EC + 4(N-1) dans chaque
    /// console, et s'ecrit dans la console du joueur (mesure du 23/10 : la copie de la console 1 est ecrasee).
    /// </summary>
    private (MultiplayerGame, string) MarioKart()
    {
        var etat = Path.Combine(_dossier, "mksc.state");
        File.WriteAllBytes(etat, [7, 7, 7]);
        var jeu = new MultiplayerGame
        {
            Id = "gba/mario-kart-super-circuit", System = "gba", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = new MultiplayerStart { State = "etat", Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(etat))) },
            Choices =
            [
                new MultiplayerChoice
                {
                    Key = "character", Default = 0, Memory = MultiplayerChoice.CarteDuCoeur,
                    Values = [new() { Value = 0, Name = "MARIO" }, new() { Value = 1, Name = "LUIGI" }, new() { Value = 8, Name = "BOWSER" }],
                    Seats = [new() { Seat = 1, CoreRam = "0x0202A8EC" }, new() { Seat = 2, CoreRam = "0x1202A8F0" }],
                },
            ],
        };
        return (jeu, etat);
    }

    [Fact]
    public async Task Un_choix_par_la_carte_du_coeur_s_ecrit_et_se_relit_dans_chaque_console()
    {
        var (jeu, etat) = MarioKart();
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var resultat = await new MultiplayerDepartPret(ra, apresLeChargement: TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "mksc", Places(8, 1), CancellationToken.None);

        Assert.True(resultat.Ok, resultat.Raison);
        Assert.Equal(8, ra.Carte[0x0202A8EC]);
        Assert.Equal(1, ra.Carte[0x1202A8F0]);
        Assert.Empty(ra.Memoire);   // rien par READ_CORE_RAM : la console 2 n'y est pas
        Assert.Contains("WRITE_CORE_MEMORY 1202a8f0 01", ra.Recues);
        Assert.Contains("READ_CORE_MEMORY 1202a8f0 1", ra.Recues);
    }

    /// <summary>Un etat par nombre de joueurs : celui a deux consoles ne se charge pas a trois.</summary>
    private static MultiplayerStart Etat(int? joueurs, char motif = 'a')
        => new() { State = $"states/gba/mksc/selection-{joueurs}p.state", Sha256 = new string(motif, 64), Players = joueurs };

    private static readonly MultiplayerGame QuatreConsoles = new()
    {
        Id = "gba/mario-kart-super-circuit", System = "gba", Players = 4, Level = MultiplayerLevels.NelfePlay,
        Start = Etat(2), Starts = [Etat(3, 'b'), Etat(4, 'c')],
    };

    [Fact]
    public void Le_depart_se_choisit_par_le_nombre_de_joueurs()
    {
        Assert.Equal(2, QuatreConsoles.DepartPour(2)!.Players);
        Assert.Equal(3, QuatreConsoles.DepartPour(3)!.Players);
        Assert.Equal(4, QuatreConsoles.DepartPour(4)!.Players);
        Assert.Null(QuatreConsoles.DepartPour(5));
        // Un etat d'arcade n'a pas de nombre : il vaut pour tous, comme avant.
        var arcade = QuatreConsoles with { Start = Etat(null), Starts = null };
        Assert.Same(arcade.Start, arcade.DepartPour(3));
    }

    [Fact]
    public void Les_etats_par_nombre_de_joueurs_couvrent_chaque_nombre_une_fois()
    {
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles));
        Assert.Equal("pas d'etat pour 4 joueurs", MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles with { Starts = [Etat(3, 'b')] }));
        Assert.Equal("etats par nombre de joueurs sans nombre ou en double",
            MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles with { Starts = [Etat(3, 'b'), Etat(3, 'c'), Etat(4)] }));
        Assert.Equal("etats par nombre de joueurs sans nombre ou en double",
            MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles with { Starts = [Etat(null, 'b')] }));
        Assert.Equal("etat pour 5 joueurs hors de 2 a 4",
            MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles with { Starts = [Etat(3, 'b'), Etat(4, 'c'), Etat(5, 'd')] }));
        Assert.Equal("empreinte de l'etat manquante",
            MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles with { Starts = [Etat(3, 'b') with { Sha256 = "" }, Etat(4, 'c')] }));
        Assert.Equal("chemin de l'etat hors du dossier MULTIPLAYER",
            MultiplayerCatalogueStore.ValiderLeDepart(QuatreConsoles with { Starts = [Etat(3, 'b') with { State = "../x.state" }, Etat(4, 'c')] }));
    }

    /// <summary>
    /// F-Zero: Maximum Velocity sous mGBA Splitscreen (mesure du 24/10) : le bolide de chaque joueur en IWRAM, dans TOUTES
    /// les consoles ; a deux et a trois, le joueur 2 est en 3e ligne (une ligne de l'ordinateur s'intercale).
    /// </summary>
    private static readonly MultiplayerChoice BolideFZero = new()
    {
        Key = "machine", Default = 0, Memory = MultiplayerChoice.CarteDuCoeur, AllConsoles = true,
        Values = [new() { Value = 0, Name = "HOT VIOLET" }, new() { Value = 1, Name = "FIRE BALL" }, new() { Value = 2, Name = "J.B. CRYSTAL" }, new() { Value = 3, Name = "WIND WALKER" }],
        Seats =
        [
            new() { Seat = 1, CoreRam = "0x030031A4" },
            new() { Seat = 2, CoreRam = "0x030031AC" }, new() { Seat = 2, CoreRam = "0x030031A8", Players = 4 },
            new() { Seat = 3, CoreRam = "0x030031B0", Players = 3 }, new() { Seat = 3, CoreRam = "0x030031AC", Players = 4 },
            new() { Seat = 4, CoreRam = "0x030031B0", Players = 4 },
        ],
    };

    private static Dictionary<int, IReadOnlyDictionary<string, int>?> Bolides(params int[] valeurs)
        => valeurs.Select((v, i) => (i + 1, v)).ToDictionary(p => p.Item1, p => (IReadOnlyDictionary<string, int>?) new Dictionary<string, int> { ["machine"] = p.v });

    [Fact]
    public void Un_choix_de_toutes_les_consoles_s_ecrit_dans_chacune_a_l_adresse_du_nombre_de_joueurs()
    {
        var jeu = QuatreConsoles with { Choices = [BolideFZero] };
        var deux = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(3, 1));
        Assert.Equal(
            new (long, int, bool)[] { (0x030031A4L, 3, true), (0x130031A4L, 3, true), (0x030031ACL, 1, true), (0x130031ACL, 1, true) },
            deux);

        var quatre = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(0, 1, 2, 3));
        Assert.Equal(16, quatre.Count);
        Assert.Contains((0x330031A8L, 1, true), quatre);   // le joueur 2 en 2e ligne a quatre, vu par la console 4
        Assert.Contains((0x030031B0L, 3, true), quatre);
        Assert.DoesNotContain(quatre, e => e.Decalage == 0x030031ACL && e.Valeur == 1);

        var trois = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(0, 1, 2));
        Assert.Contains((0x230031B0L, 2, true), trois);
        Assert.Contains((0x230031ACL, 1, true), trois);
    }

    [Fact]
    public void Les_adresses_par_nombre_de_joueurs_couvrent_chaque_place_de_chaque_nombre()
    {
        var jeu = QuatreConsoles with { Choices = [BolideFZero] };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        var sansTrois = jeu with { Choices = [BolideFZero with { Seats = [.. BolideFZero.Seats.Where(s => !(s.Seat == 3 && s.Players == 3))] }] };
        Assert.Equal("choix « machine » : pas d'adresse pour la place 3 a 3 joueurs", MultiplayerCatalogueStore.ValiderLeDepart(sansTrois));
        var sansCarte = jeu with { Choices = [BolideFZero with { Memory = null }] };
        Assert.Equal("choix « machine » : toutes les consoles sans la carte du coeur", MultiplayerCatalogueStore.ValiderLeDepart(sansCarte));
    }

    /// <summary>F-Zero (24/10) : 8 caracteres ASCII ; la table des joueurs lies dans chaque console, le nom propre dans la sienne.</summary>
    private static readonly MultiplayerNames NomsFZero = new()
    {
        Length = 8, Allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!?.,-'&_/ ", Replacement = "-", Pad = " ", Memory = MultiplayerChoice.CarteDuCoeur,
        Seats =
        [
            new() { Seat = 1, CoreRam = "0x03003330", Consoles = "all" }, new() { Seat = 1, CoreRam = "0x03002D7A" },
            new() { Seat = 2, CoreRam = "0x03003344", Consoles = "all" }, new() { Seat = 2, CoreRam = "0x03002D7A" },
            new() { Seat = 3, CoreRam = "0x03003358", Consoles = "all" }, new() { Seat = 3, CoreRam = "0x03002D7A" },
            new() { Seat = 4, CoreRam = "0x0300336C", Consoles = "all" }, new() { Seat = 4, CoreRam = "0x03002D7A" },
        ],
    };

    [Theory]
    [InlineData("Nelfe", "NELFE   ")]
    [InlineData("  bob  ", "BOB     ")]
    [InlineData("Ève+42!", "EVE-42! ")]
    [InlineData("Rémi", "REMI    ")]
    [InlineData("Æsir", "-SIR    ")]
    [InlineData("TresLongPseudo", "TRESLONG")]
    [InlineData("", "        ")]
    public void Un_pseudo_se_code_comme_le_jeu_l_affiche(string pseudo, string attendu)
        => Assert.Equal(attendu, global::System.Text.Encoding.ASCII.GetString(NomsFZero.Coder(pseudo)));

    [Fact]
    public void Le_pseudo_de_chaque_place_s_ecrit_dans_la_table_de_chaque_console_et_dans_sa_console()
    {
        var jeu = QuatreConsoles with { Choices = [BolideFZero], Names = NomsFZero };
        var ecritures = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(3, 1), new Dictionary<int, string> { [1] = "Nelfe", [2] = "Bob" });
        // 2 bolides x 2 consoles, puis par place : 2 tables + son nom, 8 octets chacun.
        Assert.Equal(4 + 2 * 3 * 8, ecritures.Count);
        Assert.Contains((0x03003330L, (int) 'N', true), ecritures);
        Assert.Contains((0x13003330L + 4, (int) 'E', true), ecritures);   // la console 2 connait le nom du joueur 1
        Assert.Contains((0x13002D7AL, (int) 'B', true), ecritures);       // le nom propre du joueur 2, dans sa console
        Assert.DoesNotContain(ecritures, e => e.Decalage == 0x03002D7AL && e.Valeur == 'B');
        Assert.Contains((0x13003344L + 7, (int) ' ', true), ecritures);
    }

    [Fact]
    public void Les_pseudos_du_catalogue_ont_une_adresse_par_place()
    {
        var jeu = QuatreConsoles with { Choices = [BolideFZero], Names = NomsFZero };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        Assert.Equal("pseudos : pas d'adresse pour la place 4",
            MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Names = NomsFZero with { Seats = [.. NomsFZero.Seats.Where(s => s.Seat != 4)] } }));
        Assert.Equal("pseudos : adresse ou consoles incoherentes",
            MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Names = NomsFZero with { Memory = null } }));
        Assert.Equal("pseudos : longueur ou caracteres incoherents",
            MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Names = NomsFZero with { Length = 0 } }));
    }

    [Fact]
    public async Task Le_depart_ecrit_les_pseudos_et_les_relit()
    {
        var (jeu, etat) = MarioKart();
        var avecNoms = jeu with { Names = NomsFZero };
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var resultat = await new MultiplayerDepartPret(ra, apresLeChargement: TimeSpan.Zero)
            .PreparerAsync(avecNoms, etat, "mksc", Places(8, 1), CancellationToken.None, null, new Dictionary<int, string> { [1] = "Nelfe", [2] = "Bob" });
        Assert.True(resultat.Ok, resultat.Raison);
        Assert.Equal((int) 'N', ra.Carte[0x03003330]);
        Assert.Equal((int) 'O', ra.Carte[0x13003344 + 1]);
        // Un pseudo part en une commande et se relit en une commande.
        Assert.Contains("WRITE_CORE_MEMORY 3003330 4e 45 4c 46 45 20 20 20", ra.Recues);
        Assert.Contains("READ_CORE_MEMORY 3003330 8", ra.Recues);
    }

    private static readonly MultiplayerRotation CoupeMarioKart = new()
    {
        Key = "cup", Memory = MultiplayerChoice.CarteDuCoeur, CoreRam = "0x0300001E", AllConsoles = true,
        Values = [new() { Value = 0, Name = "MUSHROOM CUP" }, new() { Value = 1, Name = "FLOWER CUP" }, new() { Value = 2, Name = "LIGHTNING CUP" },
            new() { Value = 3, Name = "STAR CUP" }, new() { Value = 4, Name = "SPECIAL CUP" }],
    };

    [Fact]
    public void La_coupe_change_toutes_les_deux_heures_aux_heures_paires_UTC()
    {
        var avant = CoupeMarioKart.A(new DateTimeOffset(2026, 10, 24, 13, 59, 59, TimeSpan.Zero))!.Value;
        var apres = CoupeMarioKart.A(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero))!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero), avant.Jusqua);
        Assert.Equal((avant.Valeur.Value + 1) % 5, apres.Valeur.Value);
        // La meme heure vue d'un autre fuseau : la meme coupe.
        Assert.Equal(avant.Valeur, CoupeMarioKart.A(new DateTimeOffset(2026, 10, 24, 15, 59, 59, TimeSpan.FromHours(2)))!.Value.Valeur);
    }

    [Fact]
    public void La_coupe_du_moment_s_ecrit_dans_chaque_console()
    {
        var jeu = QuatreConsoles with { Choices = [], Rotation = CoupeMarioKart };
        var instant = new DateTimeOffset(2026, 10, 24, 14, 30, 0, TimeSpan.Zero);
        var coupe = CoupeMarioKart.A(instant)!.Value.Valeur.Value;
        var ecritures = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(3, 1), maintenant: instant);
        Assert.Equal([(0x0300001EL, coupe, true), (0x1300001EL, coupe, true)], ecritures);
    }

    [Fact]
    public void Un_roulement_incoherent_refuse_le_jeu()
    {
        var jeu = QuatreConsoles with { Choices = [BolideFZero], Rotation = CoupeMarioKart };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        Assert.Equal("roulement : tranche ou valeurs incoherentes", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Rotation = CoupeMarioKart with { PeriodH = 5 } }));
        Assert.Equal("roulement : tranche ou valeurs incoherentes", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Rotation = CoupeMarioKart with { Values = [] } }));
        Assert.Equal("roulement : adresse incoherente", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Rotation = CoupeMarioKart with { CoreRam = "" } }));
        Assert.Equal("roulement : adresse incoherente", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Rotation = CoupeMarioKart with { Memory = null } }));
    }

    [Fact]
    public void La_classe_tenue_part_dans_la_table_de_verrous_du_coeur_une_entree_par_console()
    {
        // F-Zero : la classe ne s'ecrit pas, le coeur la tient (0x03002E4F, Queen 3).
        var classe = new MultiplayerRotation
        {
            Key = "cup", Memory = MultiplayerChoice.CarteDuCoeur, LockCoreRam = "0x03002E4F", AllConsoles = true,
            Values = [new() { Value = 0, Name = "PAWN" }, new() { Value = 1, Name = "KNIGHT" }, new() { Value = 2, Name = "BISHOP" }, new() { Value = 3, Name = "QUEEN" }],
        };
        var jeu = QuatreConsoles with { Choices = [], Rotation = classe };
        var instant = new DateTimeOffset(2026, 10, 24, 14, 30, 0, TimeSpan.Zero);
        var valeur = classe.A(instant)!.Value.Valeur.Value;
        var ecritures = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(3, 1), maintenant: instant);
        Assert.Equal(16, ecritures.Count);
        Assert.Equal([0x4F, 0x2E, 0x00, 0x03, valeur, 1, 0, 0], ecritures.Take(8).Select(e => e.Valeur));
        Assert.Equal([0x4F, 0x2E, 0x00, 0x13, valeur, 1, 0, 0], ecritures.Skip(8).Select(e => e.Valeur));
        Assert.Equal(MultiplayerRotation.TableDesVerrous + 8, ecritures[8].Decalage);
        Assert.All(ecritures, e => Assert.True(e.Carte));
        // Un seul bloc a ecrire et a relire.
        Assert.Single(MultiplayerDepartPret.Blocs(ecritures));

        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        Assert.Equal("roulement : adresse incoherente", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Rotation = classe with { LockCoreRam = "0x13002E4F" } }));
        Assert.Equal("roulement : adresse incoherente", MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Rotation = classe with { Memory = null, AllConsoles = false } }));
    }

    [Fact]
    public void Un_choix_tenu_entre_dans_la_table_de_verrous_apres_la_coupe_dans_chaque_console()
    {
        // Mario Kart a deux : la coupe tenue dans les deux consoles (entrees 0 et 1), puis chaque pilote dans chaque console.
        var coupe = CoupeMarioKart with { LockCoreRam = "0x0202AB38" };
        var jeu = QuatreConsoles with { Choices = [BolideFZero with { Lock = true }], Rotation = coupe };
        var instant = new DateTimeOffset(2026, 10, 24, 14, 30, 0, TimeSpan.Zero);
        var valeurDeLaCoupe = coupe.A(instant)!.Value.Valeur.Value;
        var ecritures = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(3, 1), maintenant: instant);
        var table = ecritures.Where(e => e.Decalage >= MultiplayerRotation.TableDesVerrous).ToList();
        Assert.Equal(6 * 8, table.Count);
        int[] Entree(int k) => table.Skip(k * 8).Take(8).Select(e => e.Valeur).ToArray();
        Assert.Equal([0x38, 0xAB, 0x02, 0x02, valeurDeLaCoupe, 1, 0, 0], Entree(0));
        Assert.Equal([0x38, 0xAB, 0x02, 0x12, valeurDeLaCoupe, 1, 0, 0], Entree(1));
        Assert.Equal([0xA4, 0x31, 0x00, 0x03, 3, 1, 0, 0], Entree(2));
        Assert.Equal([0xA4, 0x31, 0x00, 0x13, 3, 1, 0, 0], Entree(3));
        Assert.Equal([0xAC, 0x31, 0x00, 0x03, 1, 1, 0, 0], Entree(4));
        Assert.Equal([0xAC, 0x31, 0x00, 0x13, 1, 1, 0, 0], Entree(5));
        // La valeur s'ecrit aussi une fois, comme avant.
        Assert.Contains((0x030031A4L, 3, true), ecritures);
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));

        // A quatre, la coupe prend 4 entrees et les pilotes 16 : aucun pilote n'est tenu, la coupe l'est toujours.
        var quatre = MultiplayerDepartPret.EcrituresParCommande(jeu, Bolides(3, 1, 0, 2), maintenant: instant);
        Assert.Equal(4 * 8, quatre.Count(e => e.Decalage >= MultiplayerRotation.TableDesVerrous));
        // Sans roulement, les 16 entrees suffisent aux quatre pilotes dans les quatre consoles.
        var sansCoupe = MultiplayerDepartPret.EcrituresParCommande(jeu with { Rotation = null }, Bolides(3, 1, 0, 2), maintenant: instant);
        Assert.Equal(16 * 8, sansCoupe.Count(e => e.Decalage >= MultiplayerRotation.TableDesVerrous));
    }

    [Fact]
    public void Un_choix_tenu_sans_la_carte_du_coeur_refuse_le_jeu()
    {
        var jeu = QuatreConsoles with { Choices = [BolideFZero with { Lock = true, Memory = null, AllConsoles = false }] };
        Assert.Equal("choix « machine » : tenu sans la carte du coeur", MultiplayerCatalogueStore.ValiderLeDepart(jeu));
    }

    [Fact]
    public void Le_panneau_annonce_la_coupe_et_l_heure_de_la_suivante()
    {
        var jeu = QuatreConsoles with { Rotation = CoupeMarioKart };
        var instant = new DateTimeOffset(2026, 10, 24, 14, 30, 0, TimeSpan.Zero);
        var texte = MultiplayerPanelService.LeRoulement(jeu, instant, cle => cle == "multiplayer.rotation.until" ? "jusqu'a {time}" : cle);
        Assert.StartsWith(CoupeMarioKart.A(instant)!.Value.Valeur.Name + " · jusqu'a ", texte);
        Assert.EndsWith(new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("HH:mm"), texte);
        Assert.Equal(string.Empty, MultiplayerPanelService.LeRoulement(jeu with { Level = MultiplayerLevels.Classique }, instant, cle => cle));
    }

    [Fact]
    public void Les_octets_contigus_d_une_meme_commande_forment_un_bloc()
    {
        var blocs = MultiplayerDepartPret.Blocs([(0x10, 1, true), (0x11, 2, true), (0x12, 3, false), (0x14, 4, false), (0x15, 5, false)]);
        Assert.Equal([0x10L, 0x12L, 0x14L], blocs.Select(b => b.Decalage));
        Assert.Equal([4, 5], blocs[2].Octets);
        Assert.Equal(64, MultiplayerDepartPret.Blocs([.. Enumerable.Range(0, 70).Select(i => ((long) i, 0, true))])[0].Octets.Count);
    }

    private const string Usa = "784a036ff1aae709e90167186639b75e";
    private const string Europe = "f99fb545ac65126e9956a7718c30d6a3";

    private static readonly MultiplayerGame SurLUsa = QuatreConsoles with
    {
        Content = new MultiplayerContent { Md5 = [Usa, Europe] },
        Start = Etat(2) with { Content = Usa },
        Starts = [Etat(3, 'b') with { Content = Usa }, Etat(4, 'c') with { Content = Usa }],
        Choices = [new MultiplayerChoice { Key = "character", Default = 4, Values = [new() { Value = 4, Name = "RANDOM" }] }],
    };

    [Fact]
    public void Un_etat_fait_sur_un_contenu_ne_se_charge_pas_sur_un_autre()
    {
        Assert.Equal(3, SurLUsa.DepartPour(3, Usa)!.Players);
        Assert.Equal(3, SurLUsa.DepartPour(3, Usa.ToUpperInvariant())!.Players);
        Assert.Null(SurLUsa.DepartPour(3, Europe));
        Assert.True(SurLUsa.ADesDepartsPour(Usa));
        Assert.False(SurLUsa.ADesDepartsPour(Europe));

        // Sur la version Europe, le jeu reste au catalogue et se joue en classique : ni choix au panneau, ni etat.
        var europe = new MultiplayerInstalledGame(SurLUsa, "mksc-e.gba", null, Europe);
        Assert.False(europe.ChoixOuverts);
        Assert.Null(europe.DepartPour(2));
        var usa = europe with { ContentKey = Usa };
        Assert.True(usa.ChoixOuverts);
        Assert.Equal(4, usa.DepartPour(4)!.Players);
    }

    [Fact]
    public void Le_contenu_d_un_etat_est_un_contenu_du_catalogue_et_a_tous_ses_nombres()
    {
        var sansChoix = SurLUsa with { Choices = null };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(sansChoix));
        Assert.Equal("etat fait sur un contenu hors du catalogue",
            MultiplayerCatalogueStore.ValiderLeDepart(sansChoix with { Start = sansChoix.Start! with { Content = new string('e', 32) } }));
        // Un nombre fait sur l'Europe seule laisserait l'Europe sans les deux autres.
        Assert.Equal("pas d'etat pour 2 joueurs",
            MultiplayerCatalogueStore.ValiderLeDepart(sansChoix with { Starts = [.. sansChoix.Starts!, Etat(3, 'd') with { Content = Europe }] }));
    }

    [Fact]
    public async Task Le_depart_retenu_est_celui_dont_l_empreinte_se_verifie()
    {
        var (jeu, etat) = MarioKart();
        var ra = new FauxRetroArch { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var trois = Etat(3, '0');
        var refuse = await new MultiplayerDepartPret(ra, apresLeChargement: TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "mksc", Places(8, 1), CancellationToken.None, trois);
        Assert.False(refuse.Ok);
        Assert.Equal("empreinte de l'etat differente du catalogue", refuse.Raison);

        var bon = trois with { Sha256 = jeu.Start!.Sha256 };
        var accepte = await new MultiplayerDepartPret(ra, apresLeChargement: TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "mksc", Places(8, 1), CancellationToken.None, bon);
        Assert.True(accepte.Ok, accepte.Raison);
    }

    [Fact]
    public async Task Une_carte_absente_annule_le_depart()
    {
        // L'ancien coeur (sans carte memoire) : RetroArch repond « no descriptor », le depart ne part pas a l'aveugle.
        var (jeu, etat) = MarioKart();
        var ra = new SansCarte { DossierDesEtats = Path.Combine(_dossier, "etats") };
        var resultat = await new MultiplayerDepartPret(ra, apresLeChargement: TimeSpan.Zero)
            .PreparerAsync(jeu, etat, "mksc", Places(8, 1), CancellationToken.None);
        Assert.False(resultat.Ok);
        Assert.Contains("relecture", resultat.Raison);
    }

    private sealed class SansCarte : ICommandesRetroArch
    {
        public string DossierDesEtats { get; init; } = string.Empty;

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            var morceaux = commande.Split(' ');
            return Task.FromResult<string?>(morceaux[0] switch
            {
                "GET_STATUS" => "GET_STATUS PLAYING game_boy_advance,mksc,crc32=ed316e37",
                "GET_CONFIG_PARAM" => "GET_CONFIG_PARAM savestate_directory " + DossierDesEtats,
                "READ_CORE_MEMORY" => $"READ_CORE_MEMORY {morceaux[1]} -1 no memory map defined",
                _ => null,
            });
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
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

    [Fact]
    public void Chaque_cote_de_Title_Fight_voit_ses_boxeurs_aux_memes_nombres()
    {
        var (jeu, _) = Sf2ce();
        var choix = jeu.Choices![0];
        IReadOnlyList<MultiplayerChoiceValue> rouges = [new() { Value = 0, Name = "MAXIMUM SCHMIDT" }, new() { Value = 4, Name = "GEORGE" }, new() { Value = 5, Name = "PINKY" }];
        var deuxListes = jeu with { Choices = [choix with { Seats = [choix.Seats[0], choix.Seats[1] with { Values = rouges }] }] };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(deuxListes));
        var parCote = deuxListes.Choices![0];
        Assert.Equal("RYU", parCote.ValeursDe(1)[0].Name);
        Assert.Equal("MAXIMUM SCHMIDT", parCote.ValeursDe(2)[0].Name);
        Assert.Equal("RYU", parCote.ValeursDe(null)[0].Name);

        // D'autres nombres que ceux du choix : la plateforme refuserait la valeur, l'hote ecrirait n'importe quoi.
        IReadOnlyList<MultiplayerChoiceValue> autres = [new() { Value = 0, Name = "A" }, new() { Value = 1, Name = "B" }, new() { Value = 5, Name = "C" }];
        var faux = jeu with { Choices = [choix with { Seats = [choix.Seats[0], choix.Seats[1] with { Values = autres }] }] };
        Assert.Equal("choix « character » : valeurs de la place 2 differentes de celles du choix", MultiplayerCatalogueStore.ValiderLeDepart(faux));
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
    public async Task Un_invite_en_pleine_demo_attend_d_avoir_vu_le_depart()
    {
        // L'invite demarre sa propre demo avant de recevoir l'etat de l'hote : un compteur deja a 2 ne gagne rien.
        var ra = new FauxRetroArch { DossierDesEtats = _dossier };
        ra.Memoire[0x864F] = 2;
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(20));
        var attente = guet.AttendreLeVainqueurAsync(Manches, [1, 2], CancellationToken.None);
        await Task.Delay(120);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x864F] = 0;   // l'etat de l'hote arrive : la rencontre commence
        await Task.Delay(80);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x894F] = 2;
        Assert.Equal(2, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static readonly MultiplayerEnd SetsGardes = new()
    {
        FirstTo = 2, CloseAfterS = 5,
        Seats = [new() { Seat = 1, CoreRam = "0x873" }, new() { Seat = 2, CoreRam = "0x8F3" }],
        Duel = new() { CoreRam = "0x100", Value = 3 },
    };

    [Fact]
    public async Task Hors_duel_les_sets_gagnes_par_l_ordinateur_ne_comptent_pas()
    {
        // Les attentes laissent au guet (un releve toutes les 20 ms) le temps de voir chaque etat, meme quand toute la
        // suite tourne en parallele : a 80 ms, il en manquait un de temps en temps (26/10).
        // Mesure au banc (Windjammers, 23/10) : P1 seul contre l'ordinateur (0x100 = 1), la machine gagne deux sets dans la
        // case du joueur 2. Puis le joueur 2 entre (0x100 = 3), les sets repartent de zero, et c'est lui qui gagne.
        var ra = new FauxRetroArch { DossierDesEtats = _dossier };
        ra.Memoire[0x100] = 1;
        ra.Memoire[0x8F3] = 2;
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(20));
        var attente = guet.AttendreLeVainqueurAsync(SetsGardes, [1, 2], CancellationToken.None);
        await Task.Delay(300);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x8F3] = 0;
        ra.Memoire[0x100] = 3;
        await Task.Delay(300);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x8F3] = 2;
        Assert.Equal(2, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Deux_places_au_seuil_ensemble_ne_designent_personne_le_set_suivant_tranche()
    {
        // Un set nul donne un point a chacun : a 2-2, personne ne gagne encore ; a 3-2, la place 1 gagne.
        var ra = new FauxRetroArch { DossierDesEtats = _dossier };
        ra.Memoire[0x100] = 3;
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(20));
        var attente = guet.AttendreLeVainqueurAsync(SetsGardes, [1, 2], CancellationToken.None);
        await Task.Delay(300);
        ra.Memoire[0x873] = 2;
        ra.Memoire[0x8F3] = 2;
        await Task.Delay(300);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x873] = 3;
        Assert.Equal(1, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static readonly MultiplayerMatchEnd AuScore = new()
    {
        Type = MultiplayerMatchEnd.ScoreFinal, CloseAfterS = 5,
        Over = new() { CoreRam = "0x500", Value = 1 },
        Seats = [new() { Seat = 1, CoreRam = "0x600" }, new() { Seat = 2, CoreRam = "0x610" }],
        Score = new() { Bytes = 1, Bcd = true },
    };

    [Fact]
    public async Task Au_score_le_plus_haut_gagne_au_signal_de_fin_et_pas_au_signal_du_match_d_avant()
    {
        var ra = new FauxRetroArch { DossierDesEtats = _dossier };
        ra.Memoire[0x500] = 1;   // le signal du match d'avant, encore la au lancement
        ra.Memoire[0x600] = 0x02;
        ra.Memoire[0x610] = 0x05;
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(20));
        var attente = guet.AttendreLeScoreFinalAsync(AuScore, [1, 2], CancellationToken.None);
        await Task.Delay(120);
        Assert.False(attente.IsCompleted);
        ra.Memoire[0x500] = 0;   // le match commence
        await Task.Delay(80);
        ra.Memoire[0x600] = 0x13;   // 13 en BCD
        ra.Memoire[0x610] = 0x09;
        ra.Memoire[0x500] = 1;
        Assert.Equal(1, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Au_score_une_egalite_est_un_match_nul()
    {
        var ra = new FauxRetroArch { DossierDesEtats = _dossier };
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(20));
        var attente = guet.AttendreLeScoreFinalAsync(AuScore, [1, 2], CancellationToken.None);
        await Task.Delay(80);
        ra.Memoire[0x600] = 0x03;
        ra.Memoire[0x610] = 0x03;
        ra.Memoire[0x500] = 1;
        Assert.Equal(MultiplayerResultats.MatchNul, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(new[] { 0x45, 0x01 }, true, "le", 145)]
    [InlineData(new[] { 0x01, 0x45 }, true, "be", 145)]
    [InlineData(new[] { 0x10, 0x27 }, false, "le", 10000)]
    public void Un_score_se_lit_dans_son_format(int[] octets, bool bcd, string ordre, long attendu)
        => Assert.Equal(attendu, new MultiplayerScoreFormat { Bytes = octets.Length, Bcd = bcd, Order = ordre }.Valeur(octets));

    [Fact]
    public void Un_chiffre_bcd_impossible_ne_donne_pas_de_score()
        => Assert.Null(new MultiplayerScoreFormat { Bytes = 1, Bcd = true }.Valeur([0x1A]));

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
    public async Task Les_statistiques_comptent_coups_ko_et_perfects()
    {
        var ra = new FauxRetroArchAMots { };
        var jeu = Sf2ce().Jeu with
        {
            End = Manches,
            Stats = new MultiplayerStats
            {
                Life = new MultiplayerStatLife
                {
                    Bytes = 2, Full = 144, Ko = 0xFFFF,
                    Seats = [new() { Seat = 1, CoreRam = "0x83E8" }, new() { Seat = 2, CoreRam = "0x86E8" }],
                },
            },
        };
        ra.Mots[0x83E8] = 144;
        ra.Mots[0x86E8] = 144;
        using var fin = new CancellationTokenSource();
        var suivi = new MultiplayerStatsDuDuel(ra, TimeSpan.FromMilliseconds(10)).SuivreAsync(jeu, [1, 2], fin.Token);
        async Task Pose(long adresse, int valeur) { ra.Mots[adresse] = valeur; await Task.Delay(250); }

        await Task.Delay(250);
        await Pose(0x86E8, 120);      // P1 touche P2
        await Pose(0x86E8, 0xFFFF);   // K.O. de P2
        ra.Octets[0x864F] = 1;        // P1 gagne la manche, sa vie pleine : perfect
        await Task.Delay(250);
        await Pose(0x86E8, 144);      // nouvelle manche
        await Pose(0x86E8, 0xFFFF);   // second K.O.
        ra.Octets[0x864F] = 2;
        await Task.Delay(250);
        fin.Cancel();
        var stats = await suivi;

        Assert.Equal(new StatsDeLaPlace(3, 2, 2, 24 + 120 + 144), stats[1]);
        Assert.Equal(new StatsDeLaPlace(0, 0, 0, 0), stats[2]);
    }

    /// <summary>Un RetroArch de test qui repond aux lectures de 1 ou 2 octets (vies sur deux octets, manches sur un).</summary>
    private sealed class FauxRetroArchAMots : ICommandesRetroArch
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Mots = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Octets = new();

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            var m = commande.Split(' ');
            var adresse = long.Parse(m[1], NumberStyles.HexNumber);
            if (m[2] == "2")
            {
                var v = Mots.GetValueOrDefault(adresse);
                return Task.FromResult<string?>($"READ_CORE_RAM {m[1]} {v & 0xFF:x2} {(v >> 8) & 0xFF:x2}");
            }

            return Task.FromResult<string?>($"READ_CORE_RAM {m[1]} {Octets.GetValueOrDefault(adresse):x2}");
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
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
