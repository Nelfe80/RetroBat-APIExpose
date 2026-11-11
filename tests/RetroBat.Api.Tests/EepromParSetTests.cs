using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'EEPROM D'USINE EST CELLE D'UN SET (11/11, Progear de neogeoff refuse `profile.nvram_mismatch`). L'EEPROM d'usine de
/// progear porte sa region (« PROGEEUR ») dans la plage epinglee : celle d'un clone ne repond jamais a l'epingle du parent.
/// La borne le dit au prevol, avec le set a lancer, au lieu de laisser le site refuser apres la partie.
/// </summary>
public class EepromParSetTests
{
    private const string Fbneo = "800f25e9f9f0ace6191321cff02b985663bf84568352b29ca68325e94fb3446e";
    private const string Mame = "4bcfb12b87f6";
    private static readonly string Racine = Path.Combine(AppContext.BaseDirectory, "RomsAdmises");
    private static readonly RomAdmission.ReferentielArcadeSurDisque Arcades = new(Path.Combine(Racine, "systems"));

    private static JsonElement Profil(string json) => JsonDocument.Parse(json.Replace("@FBNEO", Fbneo).Replace("@MAME", Mame)).RootElement.Clone();

    [Theory]
    [InlineData("fbneo/progear.nv", null, "progear")]
    [InlineData("fbneo/progearj.nv", null, "progearj")]
    [InlineData("fbneo/ket.fs", null, "ket")]
    [InlineData("nvram/19xx/eeprom", null, "19xx")]
    [InlineData("fbneo/progear.nv", "ProgearU", "progearu")]
    [InlineData("../x.nv", null, null)]
    public void Le_set_d_une_epingle_se_lit_dans_son_chemin_ou_son_champ(string fichier, string? declare, string? attendu)
        => Assert.Equal(attendu, NvramSnapshotService.SetDeLEpingle(fichier, declare));

    [Fact]
    public void Un_clone_sans_epingle_est_dit_au_prevol_avec_le_set_a_lancer()
    {
        var profil = Profil("""{"profile":{"nvram_pins":[{"file":"fbneo/progear.nv","cores":["@FBNEO"]}]}}""");

        Assert.Equal(new[] { "progear" }, NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Fbneo, "progearj"));
        Assert.Equal(new[] { "progear" }, NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Fbneo.ToUpperInvariant(), "ProgearU"));
        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Fbneo, "progear"));
    }

    [Fact]
    public void Rien_a_dire_sans_epingle_pour_ce_coeur_ni_set_connu()
    {
        var profil = Profil("""{"profile":{"nvram_pins":[{"file":"fbneo/19xx.nv","cores":["@FBNEO"]},{"file":"nvram/19xx/eeprom","cores":["@MAME"]}]}}""");

        // MAME autonome a sa propre epingle, au dossier du set.
        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Mame, "19xx"));
        Assert.Equal(new[] { "19xx" }, NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Mame, "19xxj"));
        // Un coeur que le profil n'epingle pas (MAME de RetroArch) : le site jugera.
        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, "9e8f985d", "19xxj"));
        // Set inconnu, profil sans epingle.
        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Fbneo, null));
        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(Profil("""{"profile":{"bios":{"mode":"none"}}}"""), Fbneo, "progearj"));
        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(null, Fbneo, "progearj"));
    }

    [Fact]
    public void Un_clone_epingle_a_son_nom_passe()
    {
        var profil = Profil("""{"profile":{"nvram_pins":[{"file":"fbneo/progear.nv","set":"progear","cores":["@FBNEO"]},{"file":"fbneo/progearu.nv","set":"progearu","cores":["@FBNEO"]}]}}""");

        Assert.Null(NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Fbneo, "progearu"));
        Assert.Equal(new[] { "progear", "progearu" }, NvramSnapshotService.SetsEpinglesSansCeluiCi(profil, Fbneo, "progeara"));
    }

    [Fact]
    public void Progear_no_Arashi_n_entre_pas_par_le_sha1_du_parent_en_1_9_169()
    {
        // neogeoff jouait progearj (1.9.168) : son sha1 declare est celui de progear, le site l'admettait. La borne 1.9.169
        // l'ecarte par son NOM de set : titre different (« Progear no Arashi »), et progearjd est un bootleg.
        var mem = Path.Combine(Path.GetTempPath(), "eeprom-par-set-progear.MEM");
        File.WriteAllText(mem, "rom = { hashes = { } }");
        var profil = new ContentHashes { Md5 = [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("progear")] };

        Assert.True(NelfePlayScoringReporter.SetNonNomme("arcade", "progear", "progearj", profil, mem, Arcades));
        Assert.False(RomAdmission.JugerCloneArcade("progearjd", n => n == "progear", Arcades).Admise);
        // Les clones du meme titre restent soumis : c'est pour eux que le prevol parle d'EEPROM.
        Assert.False(NelfePlayScoringReporter.SetNonNomme("arcade", "progear", "progearu", profil, mem, Arcades));
        Assert.False(NelfePlayScoringReporter.SetNonNomme("arcade", "progear", "progeara", profil, mem, Arcades));
    }

    [Fact]
    public void Le_set_part_au_prevol_en_minuscules_et_jamais_hors_arcade()
    {
        Assert.Equal("progearj", NelfePlayScoringReporter.ContentArtifact(null, null, "afbb", set: " ProgearJ ")["set"]!.GetValue<string>());
        Assert.False(NelfePlayScoringReporter.ContentArtifact(null, null, "afbb", set: NelfePlayScoringReporter.SetArcade("nes", "smb")).ContainsKey("set"));
    }

    [Fact]
    public void Le_motif_se_dit_dans_les_six_langues()
    {
        foreach (var langue in new[] { "fr", "en", "es", "ja", "zh", "ko" })
        {
            Assert.False(string.IsNullOrWhiteSpace(CabinetAnnounceText.Find("reason_profile_nvram_set_unpinned", langue)));
            Assert.False(string.IsNullOrWhiteSpace(CabinetAnnounceText.Find("reason_core_fbneo_required", langue)));
        }
    }

    [Theory]
    // Final Fight de Lorenzo (11/11), Bubble Bobble, TGM2 : MAME de RetroArch, MAME autonome, FB Alpha sur un set que FBNeo connait.
    [InlineData("arcade", "MAME", "ffight", true)]
    [InlineData("arcade", "mame_standalone", "bublbobl", true)]
    [InlineData("arcade", "FB Alpha", "tgm2", true)]
    // FBNeo, et le coeur a fantomes qui garde son nom.
    [InlineData("arcade", "FinalBurn Neo", "ffight", false)]
    [InlineData("arcade", "FinalBurn Neo (NelfePlay Ghosts)", "ffight", false)]
    // Un set que FBNeo ne connait pas, une console, un coeur que l'attestation ne nomme pas : rien n'est refuse sur une supposition.
    [InlineData("arcade", "MAME", "inconnu_de_fbneo", false)]
    [InlineData("nes", "FCEUmm", "smb", false)]
    [InlineData("arcade", null, "ffight", false)]
    public void Une_partie_classee_ne_tourne_que_sous_FBNeo_quand_FBNeo_connait_le_set(string systeme, string? coeur, string set, bool refusee)
    {
        static bool Connus(string s) => s is "ffight" or "bublbobl" or "tgm2";
        Assert.Equal(refusee, NelfePlayScoringReporter.HorsFbneoAlorsQueFbneoSait(systeme, coeur, set, Connus));
    }
}
