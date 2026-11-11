using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES ROMS ADMISES (rapport de Gogerad du 11/11, APIExpose 1.9.168). Chaque cas du rapport, sur un extrait REEL du referentiel
/// de la borne (RomsAdmises/systems), des .MEM officiels (RomsAdmises/ram) et de l'index public des jeux ouverts du 11/11
/// (RomsAdmises/open-games.json) : ses `content_hashes` contiennent les hacks et les pirates que la borne retenait.
/// </summary>
public class RomsAdmisesTests
{
    private static readonly string Racine = Path.Combine(AppContext.BaseDirectory, "RomsAdmises");
    private static readonly RomAdmission.ReferentielSurDisque Referentiel = new(Path.Combine(Racine, "systems"));
    private static readonly string Etats = Path.Combine(Racine, "states");
    private static readonly RomAdmission.ReferentielArcadeSurDisque Arcades = new(Path.Combine(Racine, "systems"));

    // Les empreintes du rapport (md5 No-Intro ; celui des cartouches NES sans leur en-tete).
    private const string SmbPirateAsie = "ba9917f659693e2549677f0cc1f6c723";   // Super Mario Bros. (Asia) (En) (Pirate)
    private const string SmbHackFds = "7cbc8b41c331e061332bf17b7df83eb8";      // Super Mario Bros. (Japan)[h][FDS to NES]
    private const string SmbWorld = "8e3630186e35d477231bf8fd50e54cdd";        // Super Mario Bros. (World)
    private const string SmbEurope = "a07660b710245d0a1dea0b8e44148598";       // Super Mario Bros. (Europe)
    private const string Sor2HackCouleurs = "bbac766645d5b6ffb1c370307d84c399"; // Streets of Rage 2 (USA)[h change colours Pyron]
    private const string Sor2Usa = "cb75d5a6919b61efe917bea5762c1cb7";         // Streets of Rage 2 (USA), marque « homebrew » a tort
    private const string Sor2TroisD = "69197c1f523d671ea7f144194ac7ad32";      // Streets of Rage 2 (USA) (Sega 3D Classics)
    private const string SonicReedition = "c6c15aea60bda10ae11c6bc375296153";  // (Japan) (En) (Virtual Console) chez Gogerad
    private const string SonicUsaEurope = "1bc674be034e43c96b86487ac69d9293";  // Sonic The Hedgehog (USA, Europe)
    private const string SmwUsa = "cdd3c8c37322978ca8669b34bc89c804";          // Super Mario World (USA), ROM des etats
    private const string SmwEuropeRev1 = "7bb0487eacdb78d6635e89d797e5ab74";   // Super Mario World (Europe) (Rev 1)
    private const string Smb3UsaRev1 = "55b7111567c1709e849c574078699577";     // Super Mario Bros. 3 (USA) (Rev 1)
    private const string Smb3Europe = "bb8ab5b5bfd8da09d1872ca684001f4d";      // Super Mario Bros. 3 (Europe)

    private static OpenGame Jeu(string systeme, string groupe)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Racine, "open-games.json")));
        foreach (var liste in new[] { "games", "speedrun_games" })
        {
            foreach (var g in doc.RootElement.GetProperty(liste).EnumerateArray())
            {
                if (g.GetProperty("system_id").GetString() != systeme || g.GetProperty("rom_group").GetString() != groupe) continue;
                var h = g.GetProperty("content_hashes");
                List<string> L(string cle) => h.TryGetProperty(cle, out var v) ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
                return new OpenGame { SystemId = systeme, RomGroup = groupe, ContentHashes = new ContentHashes { Md5 = L("md5"), Sha1 = L("sha1"), Sha256 = L("sha256") } };
            }
        }
        throw new InvalidOperationException(groupe);
    }

    private static InstalledGame Dump(string systeme, string groupe, string fichier, string? mem = null) => new(
        systeme, systeme, groupe, $"I:/RetroBat/roms/{systeme}/{fichier}", fichier, null, null, true,
        Path.Combine(Racine, "ram", (mem ?? groupe) + ".MEM"));

    private static RomAdmission.Decision Juger(string systeme, string groupe, string md5, bool speedrun = false, string? mem = null)
        => NelfePlayScoringCollectionSyncService.JugerLeDump(Dump(systeme, groupe, "rom", mem), Jeu(systeme, groupe), speedrun, Referentiel,
            _ => md5, _ => null, Etats);

    // ── Les cas du rapport ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_contenu_publie_par_le_site_contient_le_pirate_et_le_hack_que_la_borne_retenait()
    {
        // La cause racine : les content_hashes publies au 11/11 rangent les hacks sous le jeu.
        var smb = Jeu("nes", "super-mario-bros").ContentHashes!.Md5!;
        Assert.Contains(SmbPirateAsie, smb);
        Assert.Contains(SmbHackFds, smb);
        Assert.Contains(Sor2HackCouleurs, Jeu("megadrive", "streets-of-rage-2").ContentHashes!.Md5!);
    }

    [Fact]
    public void Super_Mario_Bros_pirate_d_Asie_est_exclu_et_le_joueur_apprend_la_ROM_attendue()
    {
        var d = Juger("nes", "super-mario-bros", SmbPirateAsie);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("pirate", d.Raison);
        Assert.Equal("Super Mario Bros. (World)", d.RomAttendue);
        Assert.Contains("ROM attendue : « Super Mario Bros. (World) »", d.Message);
    }

    [Fact]
    public void Super_Mario_Bros_World_est_la_reference()
        => Assert.Equal(RomAdmission.Statut.Reference, Juger("nes", "super-mario-bros", SmbWorld).Statut);

    [Fact]
    public void Le_hack_FDS_de_Super_Mario_Bros_est_exclu()
    {
        var d = Juger("nes", "super-mario-bros", SmbHackFds);
        Assert.False(d.Admise);
        Assert.Contains("hack", d.Raison);
    }

    [Fact]
    public void Super_Mario_Bros_Europe_PAL_non_prouve_par_le_MEM_est_exclu()
    {
        var d = Juger("nes", "super-mario-bros", SmbEurope);
        Assert.False(d.Admise);
        Assert.Contains("non prouvée", d.Raison);
    }

    [Fact]
    public void Une_version_PAL_prouvee_par_le_MEM_entre_marquee_PAL()
    {
        // Super Mario Bros. 3 (Europe) est nomme par son .MEM : au World Scoring, il entrerait marque PAL.
        var jeu = Jeu("nes", "super-mario-bros-3") with { };
        var d = NelfePlayScoringCollectionSyncService.JugerLeDump(Dump("nes", "super-mario-bros-3", "rom"), jeu, speedrun: false, Referentiel,
            _ => Smb3Europe, _ => null, Etats);
        Assert.Equal(RomAdmission.Statut.Pal, d.Statut);
    }

    [Fact]
    public void Le_hack_de_couleurs_de_Streets_of_Rage_2_est_exclu()
    {
        var d = Juger("megadrive", "streets-of-rage-2", Sor2HackCouleurs);
        Assert.False(d.Admise);
        Assert.Contains("hack", d.Raison);
        Assert.Equal("Streets of Rage 2 (USA)", d.RomAttendue);
    }

    [Fact]
    public void Streets_of_Rage_2_USA_reste_la_reference_malgre_la_marque_homebrew_du_referentiel()
        => Assert.Equal(RomAdmission.Statut.Reference, Juger("megadrive", "streets-of-rage-2", Sor2Usa).Statut);

    [Fact]
    public void Streets_of_Rage_2_des_Sega_3D_Classics_est_une_reedition()
        => Assert.Contains("réédition", Juger("megadrive", "streets-of-rage-2", Sor2TroisD).Raison);

    [Fact]
    public void Sonic_Virtual_Console_est_une_reedition_exclue()
    {
        var d = Juger("megadrive", "sonic-the-hedgehog", SonicReedition);
        Assert.False(d.Admise);
        Assert.Contains("réédition", d.Raison);
        Assert.Equal("Sonic The Hedgehog (USA, Europe)", d.RomAttendue);
        Assert.Equal(RomAdmission.Statut.Reference, Juger("megadrive", "sonic-the-hedgehog", SonicUsaEurope).Statut);
    }

    [Fact]
    public void Super_Mario_World_Europe_Rev_1_n_est_pas_la_ROM_des_etats_du_speedrun()
    {
        var d = Juger("snes", "super-mario-world", SmwEuropeRev1, speedrun: true);
        Assert.False(d.Admise);
        Assert.Equal("Super Mario World (USA)", d.RomAttendue);
        Assert.Equal(RomAdmission.Statut.Reference, Juger("snes", "super-mario-world", SmwUsa, speedrun: true).Statut);
    }

    [Fact]
    public void Super_Mario_Bros_3_se_court_sur_la_version_USA_Rev_1_seulement()
    {
        Assert.Equal(RomAdmission.Statut.Reference, Juger("nes", "super-mario-bros-3", Smb3UsaRev1, speedrun: true).Statut);
        var europe = Juger("nes", "super-mario-bros-3", Smb3Europe, speedrun: true);
        Assert.False(europe.Admise);
        Assert.Equal("Super Mario Bros. 3 (USA) (Rev 1)", europe.RomAttendue);
    }

    [Fact]
    public void Un_dump_inconnu_du_referentiel_et_du_MEM_n_entre_pas()
        => Assert.Contains("inconnue", Juger("nes", "super-mario-bros", "00112233445566778899aabbccddeeff").Raison);

    // ── Le choix de LA ROM ─────────────────────────────────────────────────────────────────

    [Fact]
    public void La_reference_passe_avant_le_fichier_deja_choisi()
    {
        var jeu = Jeu("nes", "super-mario-bros");
        var ancien = Dump("nes", "super-mario-bros", "Super Mario Bros. (Japan, USA) (Rev 1).nes");
        var reference = Dump("nes", "super-mario-bros", "Super Mario Bros. (World).nes");
        var precedents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ancien.AbsolutePath.Replace('\\', '/') };
        var rangs = new Dictionary<InstalledGame, int> { [ancien] = 1, [reference] = 0 };
        var choisi = NelfePlayScoringCollectionSyncService.Choisir([ancien, reference], jeu, precedents, null, c => rangs[c]);
        Assert.Equal(reference.AbsolutePath, choisi);
    }

    // ── L'arcade : le set nomme par le .MEM ────────────────────────────────────────────────

    private static OpenGame Arcade(string groupe, IReadOnlyList<string> md5, IReadOnlyList<string> sha1)
        => new() { SystemId = "arcade", RomGroup = groupe, ContentHashes = new ContentHashes { Md5 = md5, Sha1 = sha1 } };

    private static InstalledGame Set(string groupe, string set, string? mem = null) => new(
        "fbneo", "arcade", groupe, $"I:/RetroBat/roms/fbneo/{set}.zip", set, null, null, true, mem);

    [Fact]
    public void Un_clone_officiel_du_set_nomme_entre_et_un_set_hors_famille_non()
    {
        // (11/11, decision du user) 19xxu, clone officiel de 19xx (le set nomme), entre ; un set d'une autre famille, non.
        const string sha1 = "813f465f2d392f6abeadbf661c54cf51171fa006";
        var jeu = Arcade("19xx-the-war-against-destiny", [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("19xx")], [sha1]);
        Func<string?, string?, string?, string?> referentiel = (_, _, _) => sha1;
        bool Clone(string set) => RomAdmission.JugerCloneArcade(set, n => n == "19xx", Arcades).Admise;
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "19xx"), jeu, referentiel, _ => null, md5DuFichier: _ => null, cloneOfficiel: Clone));
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "19xxu"), jeu, referentiel, _ => null, md5DuFichier: _ => null, cloneOfficiel: Clone));
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "tokiu"), jeu, referentiel, _ => null, md5DuFichier: _ => null, cloneOfficiel: Clone));
    }

    [Theory]
    [InlineData("19xxu", "19xx")]
    [InlineData("19xxj", "19xx")]
    [InlineData("tokiu", "toki")]
    [InlineData("mslug3h", "mslug3")]
    [InlineData("mslug3a", "mslug3")]
    [InlineData("ddragonu", "ddragon")]
    [InlineData("1942a", "1942")]
    [InlineData("ddpdojblkb", "ddpdojblk")]
    public void Les_clones_officiels_du_meme_titre_entrent(string clone, string nomme)
    {
        var d = RomAdmission.JugerCloneArcade(clone, n => n == nomme, Arcades);
        Assert.True(d.Admise, d.Raison);
    }

    [Theory]
    [InlineData("doubledr", "ddragon", "autre jeu")]          // Double Dragon Neo-Geo : son propre groupe, pas un clone
    [InlineData("pc_ddrgn", "ddragon", "autre jeu")]          // PlayChoice-10
    [InlineData("mt_beast", "altbeast", "autre jeu")]         // Mega-Tech
    [InlineData("mspacmab", "mspacman", "bootleg")]
    [InlineData("1942h", "1942", "bootleg")]                  // Supercharger 1942
    [InlineData("1942c64", "1942", "bootleg")]                // C64 Music
    [InlineData("bublboblb", "bublbobl", "bootleg")]          // for Bobble Bobble PCB
    [InlineData("tokip", "toki", "bootleg")]                  // prototype
    [InlineData("mslug3unity", "mslug3", "titre")]
    [InlineData("juju", "toki", "titre")]                     // JuJu Densetsu, version japonaise
    [InlineData("altbeastj", "altbeast", "titre")]            // Juuouki
    [InlineData("ddpdoj", "ddpdojblk", "titre")]              // DaiOuJou sans Black Label sous un .MEM de Black Label
    [InlineData("mspacpls", "mspacman", "titre")]             // Ms. Pac-Man Plus
    public void Restent_dehors_les_bootlegs_les_autres_machines_et_les_titres_differents(string set, string nomme, string pourquoi)
    {
        var d = RomAdmission.JugerCloneArcade(set, n => n == nomme, Arcades);
        Assert.False(d.Admise);
        Assert.Contains(pourquoi switch { "autre jeu" => "autre jeu", "bootleg" => "bootleg", _ => "titre différent" }, d.Raison);
    }

    [Fact]
    public void Le_Double_Dragon_Neo_Geo_au_sha1_du_BIOS_n_entre_pas_sous_Double_Dragon()
    {
        // Le profil de double-dragon publie le sha1 du BIOS Neo-Geo (5992277d…), que doubledr declare aussi.
        const string bios = "5992277debadeb64d1c1c64b0a92d9293eaf7e4a";
        var jeu = Arcade("double-dragon", [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("ddragon")], ["1504faaf07c541330cd43b72dc6846911dfd85a3", bios]);
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "doubledr"), jeu, (_, _, _) => bios, _ => null, md5DuFichier: _ => null));
    }

    [Fact]
    public void Un_bootleg_n_entre_jamais_meme_nomme()
    {
        const string sha1 = "5e8b472b615f12efca3fe792410c23619f067845";
        var jeu = Arcade("ms-pac-man", [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("mspacmab")], [sha1]);
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "mspacmab"), jeu, (_, _, _) => sha1, _ => null,
            bootlegOuHack: (_, set) => set == "mspacmab", md5DuFichier: _ => null));
    }

    [Fact]
    public void DaiOuJou_Black_Label_entre_par_son_zip_nomme_par_le_MEM()
    {
        // ddpdojblka : clone officiel ; le .MEM le nomme par le md5 de son zip (dd65cecc…), seul md5 publie.
        var mem = Path.Combine(Path.GetTempPath(), "roms-admises-ddp.MEM");
        File.WriteAllText(mem, "rom = { hashes = {\n { hash = \"dd65cecc2d83132cb7ccb764e6b384f8\", label = \"ddpdojblka.zip <DoDonPachi Dai-Ou-Jou Black Label>\" },\n } }");
        var jeu = Arcade("dodonpachi-daioujou", ["dd65cecc2d83132cb7ccb764e6b384f8"], ["62f887ba0186009456f2c64f320ea906a51cbede"]);
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "ddpdojblka", mem), jeu, (_, _, _) => null, _ => null,
            md5DuFichier: _ => "dd65cecc2d83132cb7ccb764e6b384f8"));
        // Un autre zip dont le md5 serait publie mais que le .MEM ne nomme pas n'entre pas.
        var jeu2 = Arcade("dodonpachi-daioujou", ["dd65cecc2d83132cb7ccb764e6b384f8", "0123456789abcdef0123456789abcdef"], ["62f887ba0186009456f2c64f320ea906a51cbede"]);
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(Set(jeu.RomGroup, "ddpdojblkb", mem), jeu2, (_, _, _) => null, _ => null,
            md5DuFichier: _ => "0123456789abcdef0123456789abcdef"));
    }

    // ── La soumission ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Une_partie_sur_le_pirate_n_est_jamais_soumise()
    {
        var jeu = Jeu("nes", "super-mario-bros");
        var verdict = NelfePlayScoringReporter.VerdictDuContenu("nes", "super-mario-bros", SmbPirateAsie, null, speedrun: false,
            jeu.ContentHashes, Referentiel, Path.Combine(Racine, "ram", "super-mario-bros.MEM"), Etats);
        Assert.NotNull(verdict);
        Assert.False(verdict!.Admise);
        var bon = NelfePlayScoringReporter.VerdictDuContenu("nes", "super-mario-bros", SmbWorld, null, speedrun: false,
            jeu.ContentHashes, Referentiel, Path.Combine(Racine, "ram", "super-mario-bros.MEM"), Etats);
        Assert.True(bon!.Admise);
    }

    [Fact]
    public void Un_speedrun_sur_une_autre_version_n_est_pas_soumis()
    {
        var verdict = NelfePlayScoringReporter.VerdictDuContenu("snes", "super-mario-world", SmwEuropeRev1, null, speedrun: true,
            Jeu("snes", "super-mario-world").ContentHashes, Referentiel, Path.Combine(Racine, "ram", "super-mario-world.MEM"), Etats);
        Assert.False(verdict!.Admise);
    }

    // (11/11 soir) Mega Man X : la reference du speedrun est la (USA) (Rev 1), celle des etats de depart ; la Rev 0 est refusee.
    private const string MmxRev1 = "df1cc0c8c8c4b61e3b834cc03366611c";
    private const string MmxRev0 = "a10071fa78554b57538d0b459e00d224";

    [Fact]
    public void Mega_Man_X_en_speedrun_admet_la_Rev_1_et_refuse_la_Rev_0()
    {
        var mem = Path.Combine(Racine, "ram", "mega-man-x.MEM");
        var reference = RomAdmission.ReferenceDuSpeedrun("snes", "mega-man-x", Referentiel, Etats);
        Assert.Equal("Mega Man X (USA) (Rev 1)", reference!.Nom);
        Assert.Contains(MmxRev1, reference.Empreintes);
        Assert.DoesNotContain(MmxRev0, reference.Empreintes);
        var rev1 = NelfePlayScoringReporter.VerdictDuContenu("snes", "mega-man-x", MmxRev1, null, speedrun: true, null, Referentiel, mem, Etats);
        Assert.True(rev1!.Admise);
        Assert.Equal(RomAdmission.Statut.Reference, rev1.Statut);
        var rev0 = NelfePlayScoringReporter.VerdictDuContenu("snes", "mega-man-x", MmxRev0, null, speedrun: true, null, Referentiel, mem, Etats);
        Assert.False(rev0!.Admise);
        Assert.Equal("Mega Man X (USA) (Rev 1)", rev0.RomAttendue);
    }

    [Fact]
    public void Une_partie_sur_un_set_hors_famille_n_est_pas_soumise_un_clone_officiel_si()
    {
        var mem = Path.Combine(Path.GetTempPath(), "roms-admises-19xx.MEM");
        File.WriteAllText(mem, "rom = { hashes = { } }");
        var profil = new ContentHashes { Md5 = [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("19xx")] };
        Assert.True(NelfePlayScoringReporter.SetNonNomme("arcade", "19xx-the-war-against-destiny", "tokiu", profil, mem, Arcades));
        Assert.False(NelfePlayScoringReporter.SetNonNomme("arcade", "19xx-the-war-against-destiny", "19xx", profil, mem, Arcades));
        // (11/11) le clone officiel 19xxu est soumis.
        Assert.False(NelfePlayScoringReporter.SetNonNomme("arcade", "19xx-the-war-against-destiny", "19xxu", profil, mem, Arcades));
        // Le set nomme par un libelle du .MEM (DaiOuJou Black Label) reste soumis.
        File.WriteAllText(mem, "{ hash = \"dd65cecc2d83132cb7ccb764e6b384f8\", label = \"ddpdojblka.zip <DoDonPachi Dai-Ou-Jou Black Label>\" },");
        Assert.False(NelfePlayScoringReporter.SetNonNomme("arcade", "dodonpachi-daioujou", "ddpdojblka",
            new ContentHashes { Md5 = ["dd65cecc2d83132cb7ccb764e6b384f8"] }, mem));
        // Une console n'est pas concernee.
        Assert.False(NelfePlayScoringReporter.SetNonNomme("nes", "super-mario-bros", "smb", profil, mem));
    }

    [Fact]
    public void L_arcade_garde_sa_regle_de_set_a_la_soumission()
        => Assert.Null(NelfePlayScoringReporter.VerdictDuContenu("arcade", "metal-slug-3", "00112233445566778899aabbccddeeff", null, false));

    [Fact]
    public void La_version_PAL_est_marquee_au_passeport()
    {
        var contenu = NelfePlayScoringReporter.ContentArtifact("sha", "md5", null, null, "pal");
        Assert.Equal("pal", (string?)contenu["video_standard"]);
        Assert.Null(NelfePlayScoringReporter.ContentArtifact("sha", "md5", null)["video_standard"]);
    }

    // ── Les marques ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Super Mario Bros. (Asia) (En) (Pirate)", "pirate")]
    [InlineData("Streets of Rage 2 (USA)[h change colours Pyron]", "hack")]
    [InlineData("Sonic The Hedgehog (Japan) (En) (Virtual Console)", "Virtual Console")]
    [InlineData("Super Mario Bros. (Nintendo) (JP-US)[tr fr]", "traduction")]
    [InlineData("Super Mario Bros. (Nintendo) (JP-US)[t]", "trainer")]
    [InlineData("Super Mario Bros. (Nintendo) (JP-US)[b4]", "mauvais dump")]
    [InlineData("Streets of Rage 2 (USA) (Ru) (Shedevr)", "traduction")]
    [InlineData("Super Metroid (Japan, USA) (En,Ja) (MSU-1) (Symphony)", "MSU-1")]
    public void Les_etiquettes_de_nom_qui_excluent(string nom, string marque)
        => Assert.Contains(marque, RomAdmission.EtiquettesDuNom(nom));

    [Theory]
    [InlineData("Super Mario Bros. (World)")]
    [InlineData("Super Mario Bros. 3 (USA) (Rev 1)")]
    [InlineData("Streets of Rage II (Japan, Europe) (En,Ja)")]
    [InlineData("Super Metroid (Japan, USA) (En,Ja)")]
    public void Un_dump_officiel_n_a_aucune_etiquette(string nom)
        => Assert.Empty(RomAdmission.EtiquettesDuNom(nom));

    // ── Le jeu ralenti par l'affichage (option A du user) ──────────────────────────────────

    [Theory]
    [InlineData(60.0988, 50.0, 600, true)]     // ROM NTSC sur un ecran a 50 Hz : 83 %
    [InlineData(50.0070, 50.0, 600, false)]    // ROM PAL a 50 Hz
    [InlineData(59.1854, 60.0, 600, false)]    // arcade a 59,18 Hz calee sur un ecran a 60 Hz : un peu plus vite
    [InlineData(60.0988, 60.0, 600, false)]    // NTSC a 60 Hz (PAL60 compris)
    [InlineData(60.0988, 50.0, 5, false)]      // trop peu de mesures : on ne sait pas
    public void Le_jeu_ralenti_par_l_affichage_est_refuse(double coeur, double mediane, int mesures, bool ralenti)
        => Assert.Equal(ralenti, RetroBat.Api.Scoring.RalentiDeLAffichage.Ralenti(RetroBat.Api.Scoring.RalentiDeLAffichage.Vitesse(coeur, mediane, mesures)));

    [Fact]
    public void Le_message_du_ralenti_conseille_le_60_Hz_ou_la_version_PAL()
    {
        // Sans dictionnaire (ou sans la cle) : l'anglais, la vitesse et la cadence posees dans le texte
        var m = RetroBat.Api.Scoring.RalentiDeLAffichage.Message(50.0 / 60.0988, 50.0);
        Assert.Contains("60 Hz", m);
        Assert.Contains("PAL60", m);
        Assert.Contains("PAL version", m);
        Assert.Contains("83%", m);
        Assert.Contains("(50 frames/s)", m);
        Assert.DoesNotContain("{", m);
        Assert.Equal("scoring.msg.slow_motion", RetroBat.Api.Scoring.RalentiDeLAffichage.Cle);
        // La langue ecrit les nombres a sa facon
        Assert.Contains("(50,5 frames/s)", RetroBat.Api.Scoring.RalentiDeLAffichage.Message(0.84, 50.5, null, "fr_FR"));
    }
}
