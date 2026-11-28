using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Api.Extend;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// EXTEND MODE (28/11) : les applicateurs IPS et BPS (vecteurs construits ici), la fiche de conversion, et le service qui derive
/// la ROM multijoueur de celle du joueur, sur un dossier temporaire. Aucune ROM dans le depot.
/// </summary>
public class ExtendTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "extend-" + Guid.NewGuid().ToString("N")[..8]);
    private const long Plafond = ExtendCatalogue.PlafondResultat;

    public ExtendTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ---- IPS ----

    private static byte[] Ips(params (int Decalage, byte[]? Donnees, int Repetitions, byte Valeur)[] enregistrements)
        => IpsAvecFin(null, enregistrements);

    private static byte[] IpsAvecFin(int? troncature, params (int Decalage, byte[]? Donnees, int Repetitions, byte Valeur)[] enregistrements)
    {
        using var flux = new MemoryStream();
        flux.Write("PATCH"u8);
        foreach (var (decalage, donnees, repetitions, valeur) in enregistrements)
        {
            flux.Write([(byte)(decalage >> 16), (byte)(decalage >> 8), (byte)decalage]);
            if (donnees is not null)
            {
                flux.Write([(byte)(donnees.Length >> 8), (byte)donnees.Length]);
                flux.Write(donnees);
            }
            else
            {
                flux.Write([0, 0, (byte)(repetitions >> 8), (byte)repetitions, valeur]);
            }
        }

        flux.Write("EOF"u8);
        if (troncature is { } t)
        {
            flux.Write([(byte)(t >> 16), (byte)(t >> 8), (byte)t]);
        }

        return flux.ToArray();
    }

    private static byte[] Source(int taille, int graine = 7)
    {
        var octets = new byte[taille];
        new Random(graine).NextBytes(octets);
        return octets;
    }

    [Fact]
    public void Ips_applique_enregistrements_normaux_rle_et_extension()
    {
        var source = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var patch = Ips((2, [0xAA, 0xBB, 0xCC], 0, 0), (10, null, 4, 0x55), (20, [0x99, 0x98], 0, 0));
        Assert.True(PatchIps.Appliquer(source, patch, Plafond, out var resultat, out var erreur), erreur);
        var attendu = new byte[22];
        Array.Copy(source, attendu, 16);
        attendu[2] = 0xAA; attendu[3] = 0xBB; attendu[4] = 0xCC;
        for (var i = 10; i < 14; i++) attendu[i] = 0x55;
        attendu[20] = 0x99; attendu[21] = 0x98;
        Assert.Equal(attendu, resultat);
    }

    [Fact]
    public void Ips_tronque_apres_EOF_et_laisse_la_source_intacte()
    {
        var source = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var copie = (byte[])source.Clone();
        Assert.True(PatchIps.Appliquer(source, IpsAvecFin(12, (0, [0xFF], 0, 0)), Plafond, out var resultat, out _));
        Assert.Equal(12, resultat!.Length);
        Assert.Equal(0xFF, resultat[0]);
        Assert.Equal(copie, source);
    }

    [Fact]
    public void Ips_refuse_un_patch_abime_ou_trop_gros()
    {
        var source = new byte[16];
        Assert.False(PatchIps.Appliquer(source, "NOPATCH!"u8.ToArray(), Plafond, out _, out var e1));
        Assert.Contains("PATCH", e1);
        var sansFin = Ips((0, [1, 2], 0, 0))[..^3];
        Assert.False(PatchIps.Appliquer(source, sansFin, Plafond, out _, out var e2));
        Assert.Contains("EOF", e2);
        var tronque = Ips((0, [1, 2, 3, 4], 0, 0));
        Assert.False(PatchIps.Appliquer(source, tronque[..(tronque.Length - 5)], Plafond, out _, out _));
        Assert.False(PatchIps.Appliquer(source, Ips((0x100000, [1], 0, 0)), 1000, out _, out var e3));
        Assert.Contains("depasserait", e3);
        Assert.False(PatchIps.Appliquer(source, [.. Ips((0, [1], 0, 0)), 9], Plafond, out _, out _));
    }

    // ---- BPS ----

    private static void Varint(MemoryStream flux, ulong valeur)
    {
        while (true)
        {
            var x = (byte)(valeur & 0x7F);
            valeur >>= 7;
            if (valeur == 0)
            {
                flux.WriteByte((byte)(x | 0x80));
                return;
            }

            flux.WriteByte(x);
            valeur--;
        }
    }

    private static byte[] Bps(byte[] source, byte[] cible, Action<MemoryStream> actions, uint? crcSource = null, uint? crcCible = null)
    {
        using var flux = new MemoryStream();
        flux.Write("BPS1"u8);
        Varint(flux, (ulong)source.Length);
        Varint(flux, (ulong)cible.Length);
        Varint(flux, 0);
        actions(flux);
        flux.Write(BitConverter.GetBytes(crcSource ?? PatchBps.Crc32(source)));
        flux.Write(BitConverter.GetBytes(crcCible ?? PatchBps.Crc32(cible)));
        flux.Write(BitConverter.GetBytes(PatchBps.Crc32(flux.ToArray())));
        return flux.ToArray();
    }

    private static void Action(MemoryStream flux, int commande, int longueur) => Varint(flux, (ulong)(((longueur - 1) << 2) | commande));

    private static void Decalage(MemoryStream flux, long delta) => Varint(flux, (ulong)((Math.Abs(delta) << 1) | (delta < 0 ? 1 : 0)));

    private static (byte[] Source, byte[] Cible, byte[] Patch) VecteurBps()
    {
        var source = Source(64, 3);
        // cible : 8 octets lus de la source, 4 litteraux, 6 recopies de la source a 40, 5 recopies de la cible (motif repete).
        var cible = new List<byte>();
        cible.AddRange(source[..8]);
        byte[] litteraux = [1, 2, 3, 4];
        cible.AddRange(litteraux);
        cible.AddRange(source[40..46]);
        for (var i = 0; i < 5; i++) cible.Add(cible[12 + i]);
        var cibleOctets = cible.ToArray();
        var patch = Bps(source, cibleOctets, flux =>
        {
            Action(flux, 0, 8);
            Action(flux, 1, 4);
            flux.Write(litteraux);
            Action(flux, 2, 6);
            Decalage(flux, 40);
            Action(flux, 3, 5);
            Decalage(flux, 12); // relCible demarre a 0 : +12 = debut de la copie de source ci-dessus
        });
        return (source, cibleOctets, patch);
    }

    [Fact]
    public void Bps_applique_les_quatre_actions_et_verifie_les_trois_crc()
    {
        var (source, cible, patch) = VecteurBps();
        Assert.True(PatchBps.Appliquer(source, patch, Plafond, out var resultat, out var erreur), erreur);
        Assert.Equal(cible, resultat);
    }

    [Fact]
    public void Bps_refuse_une_source_fausse_un_patch_corrompu_et_une_cible_fausse()
    {
        var (source, cible, patch) = VecteurBps();
        var autre = (byte[])source.Clone();
        autre[5] ^= 0xFF;
        Assert.False(PatchBps.Appliquer(autre, patch, Plafond, out _, out var e1));
        Assert.Contains("source", e1);

        var abime = (byte[])patch.Clone();
        abime[10] ^= 0x01;
        Assert.False(PatchBps.Appliquer(source, abime, Plafond, out _, out var e2));
        Assert.Contains("patch", e2);

        var mauvaiseCible = Bps(source, cible, flux =>
        {
            Action(flux, 0, 8);
            Action(flux, 1, 4);
            flux.Write(new byte[] { 1, 2, 3, 4 });
            Action(flux, 2, 6);
            Decalage(flux, 40);
            Action(flux, 3, 5);
            Decalage(flux, 12);
        }, crcCible: 0xDEADBEEF);
        Assert.False(PatchBps.Appliquer(source, mauvaiseCible, Plafond, out _, out var e3));
        Assert.Contains("cible", e3);

        Assert.False(PatchBps.Appliquer(source[..32], patch, Plafond, out _, out _));
        Assert.False(PatchBps.Appliquer(source, patch, 8, out _, out var e4));
        Assert.Contains("depasserait", e4);
    }

    // ---- fiche et confinement ----

    private static string Sha(byte[] octets) => Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

    private static ExtendConversion Fiche(byte[] source, byte[] patch, byte[] resultat, string format = "ips", string version = "1.0",
        string fichier = "Jeu Multiplayer (NelfePlay).md", string chemin = "extend/patches/megadrive/jeu/1.0.ips")
        => new()
        {
            Id = "megadrive/jeu", System = "megadrive", Title = "Jeu Multiplayer", Version = version,
            Source = new ExtendSource { Sha256 = Sha(source), Size = source.Length },
            Patch = new ExtendPatch { Format = format, Path = chemin, Sha256 = Sha(patch), Size = patch.Length },
            Result = new ExtendResultat { Sha256 = Sha(resultat), Size = resultat.Length, File = fichier },
            Core = new ExtendCoeur { Name = "genesis_plus_gx_nelfelink" },
        };

    [Theory]
    [InlineData("extend/patches/megadrive/jeu/1.0.ips", true)]
    [InlineData("extend/patches/a.bps", true)]
    [InlineData("extend/patches/", false)]
    [InlineData("extend/patches/../catalogue.json", false)]
    [InlineData("extend/patches/a/../../x.ips", false)]
    [InlineData("extend/autre/x.ips", false)]
    [InlineData("/extend/patches/x.ips", false)]
    [InlineData("extend\\patches\\x.ips", false)]
    [InlineData("extend/patches/C:/x.ips", false)]
    [InlineData("extend/patches//x.ips", false)]
    [InlineData("", false)]
    public void Chemin_de_patch_confine_sous_extend_patches(string chemin, bool attendu)
        => Assert.Equal(attendu, ExtendCatalogue.CheminDePatchSur(chemin));

    [Fact]
    public void Chemin_local_du_patch_reste_sous_extend_patches()
    {
        Assert.NotNull(ExtendCatalogue.CheminLocalDuPatch(_racine, "extend/patches/a/b.ips"));
        Assert.Null(ExtendCatalogue.CheminLocalDuPatch(_racine, "extend/patches/../../x.ips"));
    }

    [Theory]
    [InlineData("Dune II Multiplayer (NelfePlay).md", true)]
    [InlineData("..\\x.md", false)]
    [InlineData("sous/x.md", false)]
    [InlineData(".md", false)]
    [InlineData("x.exe", false)]
    [InlineData("x.zip", false)]
    [InlineData("x", false)]
    public void Nom_de_la_rom_derivee_est_un_simple_nom_de_fichier(string nom, bool attendu)
        => Assert.Equal(attendu, ExtendCatalogue.NomDeFichierSur(nom));

    [Fact]
    public void Catalogue_ecarte_seulement_la_fiche_invalide_et_refuse_un_schema_inconnu()
    {
        var source = Source(32); var patch = Ips((0, [1], 0, 0)); var resultat = (byte[])source.Clone(); resultat[0] = 1;
        var bonne = Fiche(source, patch, resultat);
        var gros = Fiche(source, patch, resultat); gros.Id = "x/gros"; gros.Patch!.Size = ExtendCatalogue.PlafondPatch + 1;
        var evade = Fiche(source, patch, resultat); evade.Id = "x/evade"; evade.Patch!.Path = "extend/patches/../../../x.ips";
        var enorme = Fiche(source, patch, resultat); enorme.Id = "x/enorme"; enorme.Result!.Size = ExtendCatalogue.PlafondResultat + 1;
        var futur = Fiche(source, patch, resultat); futur.Id = "x/futur"; futur.MinApi = "99.0.0";
        var json = JsonSerializer.Serialize(new { schema = ExtendCatalogue.Schema, conversions = new[] { bonne, gros, evade, enorme, futur, bonne } });
        using var flux = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var catalogue = ExtendCatalogue.Lire(flux, new Version(1, 9, 226), out var erreur, out var ecartees);
        Assert.Null(erreur);
        Assert.Equal(["megadrive/jeu"], catalogue!.Conversions.Select(c => c.Id));
        Assert.Equal(5, ecartees.Count);

        using var autre = new MemoryStream("{\"schema\":\"autre/9\",\"conversions\":[]}"u8.ToArray());
        Assert.Null(ExtendCatalogue.Lire(autre, new Version(1, 9, 226), out var erreur2, out _));
        Assert.NotNull(erreur2);
    }

    // ---- le service ----

    private sealed class Banc
    {
        public string Resources = "", Roms = "", Dossier = "";
        public bool Partie, Actif = true;
        public List<string> Declarees = [], Retirees = [];
        public ExtendRomsService Service = null!;
    }

    private Banc Monter(string nom)
    {
        var banc = new Banc { Resources = Path.Combine(_racine, nom, "resources"), Roms = Path.Combine(_racine, nom, "roms") };
        banc.Dossier = Path.Combine(banc.Roms, "megadrive");
        Directory.CreateDirectory(banc.Dossier);
        Directory.CreateDirectory(Path.Combine(banc.Resources, "extend", "patches", "megadrive", "jeu"));
        banc.Service = new ExtendRomsService(reglages: new ExtendRomsService.Reglages
        {
            RacineResources = banc.Resources, RacineRoms = banc.Roms, FichierEtat = Path.Combine(_racine, nom, "extend.json"),
            PartieEnCours = () => banc.Partie, Actif = () => banc.Actif, Api = new Version(1, 9, 226),
            Declarer = (systeme, chemin, titre, coeur) =>
            {
                var entree = Path.GetFileName(chemin) + "|" + titre + "|" + coeur;
                if (banc.Declarees.Contains(entree)) return false; // comme la gamelist : rien ne change la seconde fois
                banc.Declarees.Add(entree);
                return true;
            },
            Retirer = (systeme, chemin) => { banc.Retirees.Add(Path.GetFileName(chemin)); return true; },
            CoeurPose = _ => true,
        });
        return banc;
    }

    private static void Publier(Banc banc, byte[] patch, params ExtendConversion[] fiches)
    {
        var chemin = Path.Combine(banc.Resources, "extend", "patches", "megadrive", "jeu");
        File.WriteAllBytes(Path.Combine(chemin, "1.0.ips"), patch);
        File.WriteAllBytes(Path.Combine(chemin, "2.0.ips"), patch);
        File.WriteAllText(Path.Combine(banc.Resources, "extend", "catalogue.json"),
            JsonSerializer.Serialize(new { schema = ExtendCatalogue.Schema, conversions = fiches }));
    }

    private (byte[] Source, byte[] Patch, byte[] Resultat) Cas()
    {
        var source = Source(2048);
        var patch = Ips((16, [0xDE, 0xAD, 0xBE, 0xEF], 0, 0), (3000, [0x42], 0, 0), (100, null, 8, 0x77));
        Assert.True(PatchIps.Appliquer(source, patch, Plafond, out var resultat, out var erreur), erreur);
        return (source, patch, resultat!);
    }

    [Fact]
    public void Service_depose_la_rom_derivee_depuis_une_source_nue_sans_toucher_celle_du_joueur()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("nue");
        var rom = Path.Combine(banc.Dossier, "Jeu (Europe).md");
        File.WriteAllBytes(rom, source);
        var fiche = Fiche(source, patch, resultat);
        Publier(banc, patch, fiche);

        Assert.True(banc.Service.Passe("essai", CancellationToken.None));
        var derivee = Path.Combine(banc.Dossier, fiche.Result!.File!);
        Assert.Equal(resultat, File.ReadAllBytes(derivee));
        Assert.Equal(source, File.ReadAllBytes(rom));
        Assert.Empty(Directory.GetFiles(banc.Dossier, "*.extend-tmp"));
        Assert.Equal(["Jeu Multiplayer (NelfePlay).md|Jeu Multiplayer|genesis_plus_gx_nelfelink"], banc.Declarees);
        var etat = Assert.Single(banc.Service.Etat.Fiches);
        Assert.Equal("deposee", etat.State);
        Assert.True(etat.SourceFound && etat.DerivedPresent);
        Assert.Equal("Jeu (Europe).md", etat.SourceFile);

        // Une seconde passe ne refait rien.
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
    }

    [Fact]
    public void Service_trouve_la_source_dans_un_zip_et_depose_aussi_avec_un_patch_bps()
    {
        var source = Source(2048, 11);
        var cible = (byte[])source.Clone();
        cible[7] = 0x11;
        var bps = Bps(source, cible, flux =>
        {
            Action(flux, 0, 7);
            Action(flux, 1, 1);
            flux.WriteByte(0x11);
            Action(flux, 0, 2040);
        });
        var banc = Monter("zip");
        using (var zip = ZipFile.Open(Path.Combine(banc.Dossier, "Jeu (Europe).zip"), ZipArchiveMode.Create))
        {
            using var entree = zip.CreateEntry("Jeu (Europe).md").Open();
            entree.Write(source);
        }

        var fiche = Fiche(source, bps, cible, format: "bps");
        Publier(banc, bps, fiche);
        Assert.True(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal(cible, File.ReadAllBytes(Path.Combine(banc.Dossier, fiche.Result!.File!)));
        Assert.Equal("Jeu (Europe).zip", banc.Service.Etat.Fiches[0].SourceFile);
    }

    [Fact]
    public void Service_ne_depose_rien_sans_la_bonne_source()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("absente");
        Publier(banc, patch, Fiche(source, patch, resultat));
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal("source_absente", banc.Service.Etat.Fiches[0].State);

        // Une ROM de meme taille mais autre : ce n'est pas la source.
        var fausse = Source(2048, 99);
        File.WriteAllBytes(Path.Combine(banc.Dossier, "Autre.md"), fausse);
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal("source_absente", banc.Service.Etat.Fiches[0].State);
        Assert.Equal(["Autre.md"], Directory.GetFiles(banc.Dossier).Select(Path.GetFileName));
    }

    [Fact]
    public void Service_refuse_un_patch_corrompu_et_un_resultat_dont_l_empreinte_differe()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("refus");
        File.WriteAllBytes(Path.Combine(banc.Dossier, "Jeu.md"), source);

        // Patch du Data Pack different de celui de la fiche.
        var fiche = Fiche(source, patch, resultat);
        var abime = (byte[])patch.Clone();
        abime[6] ^= 0xFF;
        Publier(banc, abime, fiche);
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal("refus", banc.Service.Etat.Fiches[0].State);
        Assert.Contains("patch", banc.Service.Etat.Fiches[0].Message);

        // Fiche dont le resultat annonce une autre empreinte.
        var fausse = Fiche(source, patch, resultat);
        fausse.Result!.Sha256 = new string('a', 64);
        Publier(banc, patch, fausse);
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal("refus", banc.Service.Etat.Fiches[0].State);
        Assert.Contains("empreinte", banc.Service.Etat.Fiches[0].Message);
        Assert.False(File.Exists(Path.Combine(banc.Dossier, fiche.Result!.File!)));
        Assert.Empty(banc.Declarees);
    }

    [Fact]
    public void Service_remplace_la_version_anterieure_et_retire_celle_d_un_autre_nom()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("versions");
        File.WriteAllBytes(Path.Combine(banc.Dossier, "Jeu.md"), source);
        var v1 = Fiche(source, patch, resultat);
        Publier(banc, patch, v1);
        Assert.True(banc.Service.Passe("essai", CancellationToken.None));
        var chemin1 = Path.Combine(banc.Dossier, v1.Result!.File!);
        Assert.True(File.Exists(chemin1));

        // Version 2 : autre patch (autre resultat), meme nom : remplacee sur place.
        var patch2 = Ips((16, [0x01, 0x02], 0, 0));
        Assert.True(PatchIps.Appliquer(source, patch2, Plafond, out var resultat2, out _));
        var v2 = Fiche(source, patch2, resultat2!, version: "2.0", chemin: "extend/patches/megadrive/jeu/2.0.ips");
        Publier(banc, patch2, v2);
        File.WriteAllBytes(Path.Combine(banc.Resources, "extend", "patches", "megadrive", "jeu", "2.0.ips"), patch2);
        Assert.True(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal(resultat2, File.ReadAllBytes(chemin1));
        Assert.Equal("2.0", banc.Service.Etat.Fiches[0].Version);

        // Version 3 : autre nom de fichier : l'ancienne ROM derivee disparait, la nouvelle arrive.
        var v3 = Fiche(source, patch2, resultat2!, version: "3.0", fichier: "Jeu Multiplayer v3 (NelfePlay).md", chemin: "extend/patches/megadrive/jeu/2.0.ips");
        Publier(banc, patch2, v3);
        Assert.True(banc.Service.Passe("essai", CancellationToken.None));
        Assert.False(File.Exists(chemin1));
        Assert.True(File.Exists(Path.Combine(banc.Dossier, "Jeu Multiplayer v3 (NelfePlay).md")));
        Assert.Contains("Jeu Multiplayer (NelfePlay).md", banc.Retirees);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(banc.Dossier, "Jeu.md")));
    }

    [Fact]
    public void Service_ne_touche_jamais_un_fichier_du_joueur_qui_porte_le_nom_de_la_rom_derivee()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("joueur");
        File.WriteAllBytes(Path.Combine(banc.Dossier, "Jeu.md"), source);
        var fiche = Fiche(source, patch, resultat);
        var sien = Path.Combine(banc.Dossier, fiche.Result!.File!);
        File.WriteAllText(sien, "ma propre ROM");
        Publier(banc, patch, fiche);
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.Equal("ma propre ROM", File.ReadAllText(sien));
        Assert.Equal("refus", banc.Service.Etat.Fiches[0].State);
    }

    [Fact]
    public void Service_ne_fait_rien_pendant_une_partie_ni_quand_la_collection_est_coupee()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("partie");
        File.WriteAllBytes(Path.Combine(banc.Dossier, "Jeu.md"), source);
        var fiche = Fiche(source, patch, resultat);
        Publier(banc, patch, fiche);
        banc.Partie = true;
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(banc.Dossier, fiche.Result!.File!)));
        Assert.Equal("partie en cours", banc.Service.Etat.Raison);

        banc.Partie = false;
        banc.Actif = false;
        Assert.False(banc.Service.Passe("essai", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(banc.Dossier, fiche.Result!.File!)));

        banc.Actif = true;
        Assert.True(banc.Service.Passe("essai", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(banc.Dossier, fiche.Result!.File!)));
    }

    [Fact]
    public void Signature_change_quand_une_rom_arrive_dans_le_dossier_du_systeme()
    {
        var (source, patch, resultat) = Cas();
        var banc = Monter("signature");
        Publier(banc, patch, Fiche(source, patch, resultat));
        var avant = banc.Service.Signature();
        Assert.Equal(avant, banc.Service.Signature());
        File.WriteAllBytes(Path.Combine(banc.Dossier, "Jeu.md"), source);
        Assert.NotEqual(avant, banc.Service.Signature());
    }

    // ---- le vecteur reel : la ROM Europe de Dune II et l'IPS v0.12 (aucun fichier dans le depot) ----

    [Fact]
    public void Vecteur_reel_Dune_II_si_les_fichiers_sont_la()
    {
        const string rom = @"E:\RetroBat\roms\megadrive\Dune II - The Battle for Arrakis (Europe).zip";
        const string ips = @"E:\claude_travail\temp\dune2_skirmish_012\NelfePlay_DuneII_Skirmish_v0.12-alpha\multiplayer\DuneII-NelfePlay-Session-v0.12-alpha.ips";
        if (Environment.GetEnvironmentVariable("NELFEPLAY_EXTEND_REEL") != "1" || !File.Exists(rom) || !File.Exists(ips))
        {
            return; // ignore par defaut : NELFEPLAY_EXTEND_REEL=1 sur la machine du user
        }

        using var contenu = RetroBat.Api.Infrastructure.ContenuDUneRom.Ouvrir(rom, Plafond)!;
        var source = new byte[(int)contenu.Taille];
        contenu.Flux.ReadExactly(source);
        Assert.Equal("31979d1d9a5d6fe5c9ae95f3cfc1dd6f9f51e9e075268bb8a9f9ca57da847547", Sha(source));
        Assert.True(PatchIps.Appliquer(source, File.ReadAllBytes(ips), Plafond, out var resultat, out var erreur), erreur);
        Assert.Equal(2_097_152, resultat!.Length);
        Assert.Equal("b77b300192b23ccc66b90744cb38ed9135a370acc64a55378fc0a7ec9f2bb197", Sha(resultat));
    }

    // ---- le Data Pack : chaque ROM derivee a son jeu au catalogue MULTIPLAYER, et ce jeu n'est pas ecarte a la lecture ----
    // (essai du 28/11 : Dune II en niveau « nelfeplay » sans depart pret etait ecarte, la ROM deposee n'entrait pas dans la collection)

    [Fact]
    public void Chaque_rom_derivee_du_Data_Pack_a_un_jeu_MULTIPLAYER_retenu()
    {
        var pack = Environment.GetEnvironmentVariable("NELFEPLAY_DATAPACK");
        var fiches = pack is null ? null : Path.Combine(pack, "extend", "catalogue.json");
        if (fiches is null || !File.Exists(fiches))
        {
            return; // Data Pack sans Extend Mode
        }

        var api = new Version(99, 0, 0);
        using var flux = File.OpenRead(fiches);
        var extend = ExtendCatalogue.Lire(flux, api, out var erreur, out var ecartees);
        Assert.True(extend is not null, erreur);
        Assert.Empty(ecartees);
        var lu = JsonSerializer.Deserialize<RetroBat.Api.Multiplayer.MultiplayerCatalogue>(
            File.ReadAllText(Path.Combine(pack!, "multiplayer", "catalogue.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var retenu = RetroBat.Api.Multiplayer.MultiplayerCatalogueStore.Retenir(lu, api, out var erreurMulti, out var ecartesMulti);
        Assert.True(retenu is not null, erreurMulti);
        foreach (var c in extend!.Conversions)
        {
            var jeu = retenu!.Games.FirstOrDefault(j => (j.Content?.Md5 ?? []).Contains(c.Result!.Md5!, StringComparer.OrdinalIgnoreCase));
            Assert.True(jeu is not null, c.Id + " : aucun jeu MULTIPLAYER retenu pour la ROM derivee ; ecartes : " + string.Join(" | ", ecartesMulti));
            Assert.Equal(c.System, jeu!.System);
            Assert.Equal(c.Core!.Name, jeu.Core);
        }
    }
}
