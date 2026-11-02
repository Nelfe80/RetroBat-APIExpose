using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES VERROUS D'UN DEFI (D18, wrapper 0.343) : le reglement les declare (« locks »), le lancement les ecrit dans
/// certified.txt, le wrapper les pose avant chaque image et les rend au bilan. Valeurs du labo du 02/11 : Metal Slug 3, bit 0
/// de 0x100596 tenu, 832 images jouees, 832 verrouillees, 268 ou le jeu avait efface le bit.
/// </summary>
public class VerrousDeDefiTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static readonly string ModeleV3 = File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v3.json"));
    private static readonly Reglement Couteau = Reglement.Lire(ModeleV3);

    private const string LigneDuCouteau = "0x100596,1,set_bits,0x01";

    /// <summary>Le bilan du wrapper : l'etat de depart charge a l'image 180, le verrou pose sur chaque image jouee depuis.</summary>
    private static string Bilan(string locks, long images, string? comptes = null)
        => "{\"frame_count\":" + images + ",\"save_state_loads\":1,\"locks\":\"" + locks + "\",\"lock_frames\":["
           + (comptes ?? $"{{\"lock\":\"{LigneDuCouteau}\",\"valid\":true,\"applied\":{images - 180},\"restored\":268,\"missed\":0,\"from_frame\":180}}") + "]}";

    [Fact]
    public void Le_reglement_v3_tient_le_bit_du_couteau_comme_le_wrapper_l_ecrit()
    {
        Assert.Equal("ch-ms3-m1-knife-v3", Couteau.Ruleset);
        var verrou = Assert.Single(Couteau.Verrous);
        Assert.Equal(0x100596, verrou.Adresse);
        Assert.Equal("constraint", verrou.Raison);
        Assert.Equal(LigneDuCouteau, verrou.Ligne);
        Assert.Equal(LigneDuCouteau, Couteau.LignesDesVerrous);
        Assert.Empty(Couteau.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.MEM"))));
    }

    /// <summary>
    /// LA V4 (essai du user du 02/11, 22:56) : le bit du couteau ne vaut que pour le pistolet ; la mitrailleuse lourde ramassee
    /// tirait (munitions 200 puis 20). Le second verrou tient l'arme du joueur 1 sur le pistolet (0x10EAF3 = 00).
    /// </summary>
    [Fact]
    public void La_v4_tient_aussi_l_arme_sur_le_pistolet()
    {
        var v4 = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v4.json")));
        Assert.Equal("ch-ms3-m1-knife-v4", v4.Ruleset);
        Assert.Equal(4, v4.Version);
        Assert.Equal(2, v4.Verrous.Count);
        Assert.Equal(LigneDuCouteau + ";0x10EAF3,1,value,0x00", v4.LignesDesVerrous);
        Assert.Empty(v4.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.MEM"))));
    }

    [Fact]
    public void Un_reglement_sans_verrou_n_en_demande_aucun()
    {
        var v2 = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.json")));
        Assert.Empty(v2.Verrous);
        Assert.Equal("", v2.LignesDesVerrous);
    }

    [Theory]
    [InlineData("\"start_policy\": \"canonical_state\"", "\"start_policy\": \"normal_boot\"")]
    [InlineData("\"address\": \"0x100596\"", "\"address\": \"0x100590\"")]
    [InlineData("\"set_bits\": \"0x01\"", "\"set_bits\": \"0x01\", \"value\": \"0x03\"")]
    [InlineData("\"set_bits\": \"0x01\"", "\"set_bits\": \"0x00\"")]
    [InlineData("\"set_bits\": \"0x01\"", "\"set_bits\": \"0x1FF\"")]
    [InlineData("\"type\": \"u8\"", "\"type\": \"u16\"")]
    [InlineData("\"player\": 1", "\"player\": 2")]
    [InlineData("\"from\": \"attempt_start\"", "\"from\": \"boss\"")]
    [InlineData("\"reason\": \"constraint\"", "\"reason\": \"fun\"")]
    public void Un_verrou_hors_des_limites_rend_le_reglement_invalide(string avant, string apres)
    {
        Assert.Contains(avant, ModeleV3);
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(ModeleV3.Replace(avant, apres)));
    }

    [Fact]
    public void Le_bilan_du_labo_est_conforme()
        => Assert.Null(PasseportDeDefi.EcartDesVerrous(Bilan(LigneDuCouteau, 832), Couteau));

    [Fact]
    public void Un_wrapper_qui_n_a_pas_lu_le_verrou_ne_rend_aucun_temps()
    {
        // Wrapper 0.342 : ni « locks » ni « lock_frames ».
        Assert.NotNull(PasseportDeDefi.EcartDesVerrous("{\"frame_count\":832,\"save_state_loads\":1}", Couteau));
        Assert.NotNull(PasseportDeDefi.EcartDesVerrous(Bilan("", 832, ""), Couteau));
    }

    [Fact]
    public void Un_verrou_que_le_reglement_ne_demande_pas_ecarte_le_lancement()
    {
        var v2 = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.json")));
        Assert.NotNull(PasseportDeDefi.EcartDesVerrous(Bilan(LigneDuCouteau, 832), v2));
        Assert.Null(PasseportDeDefi.EcartDesVerrous("{\"frame_count\":832}", v2));
        Assert.Null(PasseportDeDefi.EcartDesVerrous(Bilan("", 832, ""), v2));
    }

    [Theory]
    [InlineData(652, 0, 180, true)]
    [InlineData(651, 0, 180, false)]
    [InlineData(652, 1, 180, false)]
    [InlineData(832, 0, 0, true)]
    [InlineData(0, 0, -1, false)]
    public void Le_verrou_doit_tenir_sur_toutes_les_images_depuis_le_depart(int posees, int manquees, int depuis, bool conforme)
    {
        var comptes = $"{{\"lock\":\"{LigneDuCouteau}\",\"valid\":true,\"applied\":{posees},\"restored\":268,\"missed\":{manquees},\"from_frame\":{depuis}}}";
        Assert.Equal(conforme, PasseportDeDefi.EcartDesVerrous(Bilan(LigneDuCouteau, 832, comptes), Couteau) is null);
    }

    [Fact]
    public void Un_verrou_illisible_par_le_wrapper_ecarte_le_lancement()
    {
        var comptes = $"{{\"lock\":\"{LigneDuCouteau}\",\"valid\":false,\"applied\":0,\"restored\":0,\"missed\":832}}";
        Assert.NotNull(PasseportDeDefi.EcartDesVerrous(Bilan(LigneDuCouteau, 832, comptes), Couteau));
    }

    [Fact]
    public void Les_verrous_poses_ne_valent_que_pour_leur_rom_et_un_temps()
    {
        var t0 = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        try
        {
            VerrousDuLancement.Poser("mslug3", [LigneDuCouteau], t0);
            Assert.Equal([LigneDuCouteau], VerrousDuLancement.Pour("MSLUG3", t0.AddSeconds(10)));
            Assert.Empty(VerrousDuLancement.Pour("mslug3h", t0.AddSeconds(10)));
            Assert.Empty(VerrousDuLancement.Pour("mslug3", t0 + VerrousDuLancement.Validite + TimeSpan.FromSeconds(1)));

            var texte = new System.Text.StringBuilder();
            VerrousDuLancement.Ecrire(texte, VerrousDuLancement.Pour("mslug3", t0));
            Assert.Equal("lock.00=" + LigneDuCouteau + "\n", texte.ToString());

            VerrousDuLancement.Poser("mslug3", [], t0);
            Assert.Empty(VerrousDuLancement.Pour("mslug3", t0));
        }
        finally
        {
            VerrousDuLancement.Retirer();
        }
    }
}
