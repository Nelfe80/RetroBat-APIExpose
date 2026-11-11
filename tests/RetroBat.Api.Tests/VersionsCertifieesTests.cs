using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES VERSIONS DEJA CERTIFIEES (11/11, bug de Lorenzolamas, decision du user : option A). Depuis la 1.9.169, Super Mario
/// Bros. sortait de sa collection : son « Super Mario Bros (JU) (PRG 1).nes » (md5 94ede934..., « (JP-US)[b4] » au referentiel,
/// drapeau bad_dump) porte pourtant 10 parties publiees (1er en 1LC). Sonic « (Japan, Europe, Korea) (En) » (Rev 1, officielle,
/// que le .MEM ne nomme pas) sortait aussi, avec 1 partie publiee. Le site publie desormais a part (`certified`) les md5 d'un
/// jeu qui portent une partie publiee et que sa regle garde : sans marque, ou marques seulement pour la qualite du dump. La
/// borne les admet ; un hack, un pirate, une traduction ou une reedition reste dehors, meme certifie ; un speedrun ne part que
/// de la ROM de ses etats. Extraits REELS du referentiel (RomsAdmises/systems) et des .MEM (RomsAdmises/ram) ; empreintes
/// publiees lues sur nelfeplay.com le 11/11 a 16:29Z.
/// </summary>
public class VersionsCertifieesTests
{
    private static readonly string Racine = Path.Combine(AppContext.BaseDirectory, "RomsAdmises");
    private static readonly RomAdmission.ReferentielSurDisque Referentiel = new(Path.Combine(Racine, "systems"));
    private static readonly string Etats = Path.Combine(Racine, "states");

    private const string SmbPrg1 = "94ede9347c1416105f1c08ec26b5b73a";        // « Super Mario Bros (JU) (PRG 1) », (JP-US)[b4]
    private const string SmbB2 = "c697ca25530075dfadde0fa22164a807";          // (JP-US)[b2] : mauvais dump jamais certifie
    private const string SmbWorldEnTete = "f94bb9bb55f325d9af8a0fff80b9376d"; // Super Mario Bros. (World), en-tete NES 2.0
    private const string SmbWorld = "8e3630186e35d477231bf8fd50e54cdd";       // Super Mario Bros. (World), sans en-tete
    private const string SmbPirateAsie = "ba9917f659693e2549677f0cc1f6c723";
    private const string SonicRev1 = "09dadb5071eb35050067a32462e39c5f";      // Sonic The Hedgehog (Japan, Europe, Korea) (En)
    private const string SonicUsaEurope = "1bc674be034e43c96b86487ac69d9293";
    private const string SonicReedition = "c6c15aea60bda10ae11c6bc375296153"; // (World) (Sonic Mega Collection)

    // Les md5 que nelfeplay.com publiait le 11/11 a 16:29Z (la 1.9.169 et sa table rom_dumps_exclus).
    private static readonly string[] SmbPublies =
    [
        "673913a23cd612daf5ad32d4085e0760", "811b027eaf99c2def7b933c5208636de", SmbWorld, "a07660b710245d0a1dea0b8e44148598",
        "b16ae0be2fe7f5e9fd2e6991ab079e6b", "ba39dde63ab209b1bc751e0535e72b18", "dab4a1eee5beb9b99735ef0a3586fe5f", SmbWorldEnTete,
    ];
    private static readonly string[] SonicPublies = ["058fb3657c98451a574784094db6d67a", SonicRev1, SonicUsaEurope];

    private static OpenGame Smb(bool corrige, params string[] enPlus) => new()
    {
        SystemId = "nes", RomGroup = "super-mario-bros",
        ContentHashes = new ContentHashes
        {
            Md5 = corrige ? [.. SmbPublies, SmbPrg1, .. enPlus] : [.. SmbPublies, .. enPlus],
            Certified = corrige ? [SmbPrg1, SmbWorldEnTete, .. enPlus] : null,
        },
    };

    private static OpenGame Sonic(bool corrige, params string[] enPlus) => new()
    {
        SystemId = "megadrive", RomGroup = "sonic-the-hedgehog",
        ContentHashes = new ContentHashes
        {
            Md5 = [.. SonicPublies, .. enPlus],
            Certified = corrige ? [SonicRev1, SonicUsaEurope, .. enPlus] : null,
        },
    };

    private static InstalledGame Dump(string systeme, string groupe, string fichier) => new(
        systeme, systeme, groupe, $"H:/RetroBat/roms/{systeme}/{fichier}", fichier, null, null, true,
        Path.Combine(Racine, "ram", groupe + ".MEM"));

    private static RomAdmission.Decision Juger(OpenGame jeu, string md5, bool speedrun = false)
        => NelfePlayScoringCollectionSyncService.JugerLeDump(Dump(jeu.SystemId, jeu.RomGroup, "rom"), jeu, speedrun, Referentiel,
            _ => md5, _ => null, Etats);

    // ── Le bug, tel que la 1.9.169 le fait ────────────────────────────────────────────────

    [Fact]
    public void Avant_la_correction_le_PRG_1_de_Lorenzo_sort_de_la_collection()
    {
        var jeu = Smb(corrige: false);
        // Le site ne publie plus son md5 : le contenu n'est meme plus confirme...
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            Dump("nes", "super-mario-bros", "Super Mario Bros (JU) (PRG 1).nes"), jeu, (_, _, _) => null, _ => SmbPrg1,
            sansEnTeteDuContenu: _ => null, md5DuFichier: _ => SmbPrg1));
        // ... et publie, la regle l'exclurait pour sa marque de mauvais dump.
        var d = Juger(Smb(corrige: false, SmbPrg1), SmbPrg1);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("mauvais dump", d.Raison);
        Assert.Equal("Super Mario Bros. (World)", d.RomAttendue);
    }

    [Fact]
    public void Avant_la_correction_Sonic_Rev_1_sort_faute_d_etre_nomme_par_le_MEM()
    {
        var d = Juger(Sonic(corrige: false), SonicRev1);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("non prouvée", d.Raison);
    }

    // ── La correction ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_PRG_1_deja_certifie_reste_au_World_Scoring_avec_la_version_propre_conseillee()
    {
        var jeu = Smb(corrige: true);
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            Dump("nes", "super-mario-bros", "Super Mario Bros (JU) (PRG 1).nes"), jeu, (_, _, _) => null, _ => SmbPrg1,
            sansEnTeteDuContenu: _ => null, md5DuFichier: _ => SmbPrg1));
        var d = Juger(jeu, SmbPrg1);
        Assert.Equal(RomAdmission.Statut.Admise, d.Statut);
        Assert.Contains("déjà certifiée", d.Raison);
        Assert.NotNull(d.Conseil);
        Assert.Contains("« Super Mario Bros. (World) »", d.Conseil);
        Assert.Equal("Super Mario Bros. (World)", d.RomAttendue);
        Assert.Equal(2, RomAdmission.Rang(d));
    }

    [Fact]
    public void Une_partie_sur_le_PRG_1_part_et_le_prevol_a_un_conseil()
    {
        var verdict = NelfePlayScoringReporter.VerdictDuContenu("nes", "super-mario-bros", SmbPrg1, null, speedrun: false,
            Smb(corrige: true).ContentHashes, Referentiel, Path.Combine(Racine, "ram", "super-mario-bros.MEM"), Etats);
        Assert.True(verdict!.Admise);
        Assert.NotNull(verdict.Conseil);
    }

    [Fact]
    public void Le_PRG_1_n_ouvre_pas_les_speedruns_et_le_joueur_apprend_la_ROM_des_etats()
    {
        var d = Juger(Smb(corrige: true), SmbPrg1, speedrun: true);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("ROM attendue : « Super Mario Bros. (World) »", d.Message);
    }

    [Fact]
    public void La_version_propre_passe_avant_le_PRG_1_quand_les_deux_sont_sur_le_disque()
    {
        var jeu = Smb(corrige: true);
        var prg1 = Dump("nes", "super-mario-bros", "Super Mario Bros (JU) (PRG 1).nes");
        var world = Dump("nes", "super-mario-bros", "Super Mario Bros. (World).nes");
        var rangs = new Dictionary<InstalledGame, int>
        {
            [prg1] = RomAdmission.Rang(Juger(jeu, SmbPrg1)),
            [world] = RomAdmission.Rang(Juger(jeu, SmbWorld)),
        };
        var precedents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { prg1.AbsolutePath };
        Assert.Equal(world.AbsolutePath, NelfePlayScoringCollectionSyncService.Choisir([prg1, world], jeu, precedents, null, c => rangs[c]));
    }

    [Fact]
    public void Sonic_Rev_1_deja_certifie_rentre_sans_conseil()
    {
        var d = Juger(Sonic(corrige: true), SonicRev1);
        Assert.Equal(RomAdmission.Statut.Admise, d.Statut);
        Assert.Null(d.Conseil);
        Assert.Equal(1, RomAdmission.Rang(d));
    }

    [Fact]
    public void Aucun_nouveau_mauvais_dump_n_entre()
    {
        // Publie (par erreur ou par un ancien index) mais jamais certifie : dehors.
        var d = Juger(Smb(corrige: true) with { ContentHashes = Smb(corrige: true).ContentHashes! with { Md5 = [.. SmbPublies, SmbPrg1, SmbB2] } }, SmbB2);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("mauvais dump", d.Raison);
    }

    [Fact]
    public void Un_pirate_certifie_reste_dehors()
    {
        var d = Juger(Smb(corrige: true, SmbPirateAsie), SmbPirateAsie);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("pirate", d.Raison);
    }

    [Fact]
    public void Une_reedition_certifiee_reste_dehors()
    {
        var d = Juger(Sonic(corrige: true, SonicReedition), SonicReedition);
        Assert.Equal(RomAdmission.Statut.Exclue, d.Statut);
        Assert.Contains("réédition", d.Raison);
    }

    [Fact]
    public void Les_references_ne_changent_pas()
    {
        Assert.Equal(RomAdmission.Statut.Reference, Juger(Smb(corrige: true), SmbWorld).Statut);
        Assert.Equal(RomAdmission.Statut.Reference, Juger(Sonic(corrige: true), SonicUsaEurope).Statut);
    }

    [Fact]
    public void Les_marques_de_qualite_seules_sont_reconnues()
    {
        Assert.True(RomAdmission.SeulementQualiteDuDump(["mauvais dump"]));
        Assert.True(RomAdmission.SeulementQualiteDuDump(["mauvais dump", "overdump"]));
        Assert.False(RomAdmission.SeulementQualiteDuDump(["mauvais dump", "hack"]));
        Assert.False(RomAdmission.SeulementQualiteDuDump(["traduction"]));
        Assert.False(RomAdmission.SeulementQualiteDuDump([]));
    }

    [Fact]
    public void L_index_du_site_porte_la_liste_certified_et_une_API_ancienne_l_ignore()
    {
        const string json = """{"md5":["94ede9347c1416105f1c08ec26b5b73a"],"sha1":[],"sha256":[],"certified":["94ede9347c1416105f1c08ec26b5b73a"]}""";
        var h = JsonSerializer.Deserialize<ContentHashes>(json)!;
        Assert.Equal([SmbPrg1], h.Certified);
        Assert.Null(JsonSerializer.Deserialize<ContentHashes>("""{"md5":[]}""")!.Certified);
    }
}
