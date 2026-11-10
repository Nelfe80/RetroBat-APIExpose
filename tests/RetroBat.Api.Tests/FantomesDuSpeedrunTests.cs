using System.Text;
using System.Text.Json;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FANTOMES D'UN SPEEDRUN (04/11, CDC MASS RUN §14) : quels replays courir, l'admission du coeur hote par le site, et la
/// conversion d'un replay BSV en .nghost. NELFEPLAY_FANTOME_ESSAI (« replay|attendu.nghost ») compare un vrai replay a la
/// sortie de tools/bsv_vers_nghost.py.
/// </summary>
public class FantomesDuSpeedrunTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom)));

    private static readonly (string, string?)[] Classement = [("Ash", "rp_ash"), ("Ben", null), ("Nelfe", "rp_nelfe")];

    [Fact]
    public void La_duree_d_un_fantome_est_le_chrono_independant_du_classement()
    {
        // Sonic : temps officiel cale sur le chrono du jeu (2289), duree du run du depart a l'arrivee (2387)
        using var json = JsonDocument.Parse("""
            {"rows":[{"player":"Nelfe80","value":2289,"independent_frames":2387,"ghost":{"id":"rp_A"}},
                     {"player":"Ash","value":2400,"independent_frames":2500,"replay":{"id":"rp_B"}},
                     {"player":"Ben","value":2500}]}
            """);
        var durees = FantomesDuSpeedrun.TempsDesReplays(json.RootElement, "independent_frames");
        Assert.Equal(2387, durees["rp_A"]);
        Assert.Equal(2500, durees["rp_B"]);
        Assert.Equal(2, durees.Count);
        Assert.Equal(2289, FantomesDuSpeedrun.TempsDesReplays(json.RootElement)["rp_A"]);
    }

    [Fact]
    public void Mon_fantome_n_est_le_record_mondial_que_s_il_en_a_le_temps()
    {
        var temps = new Dictionary<string, long> { ["rp_ash"] = 1500 };
        // je suis premier du classement : mon fantome local est le WR s'il a ce temps-la, pas un temps plus lent
        IReadOnlyList<(string, string?)> moiPremier = [("Nelfe80", null), ("Ash", "rp_ash")];
        Assert.True(FantomesDuSpeedrun.MeilleurQueLeMondial(1423, moiPremier, temps, "Nelfe80", 1423));
        Assert.False(FantomesDuSpeedrun.MeilleurQueLeMondial(2088, moiPremier, temps, "nelfe80", 1925));
        // un autre est premier : mon temps doit egaler ou battre le sien
        IReadOnlyList<(string, string?)> ashPremier = [("Ash", "rp_ash"), ("Nelfe80", null)];
        Assert.False(FantomesDuSpeedrun.MeilleurQueLeMondial(1600, ashPremier, temps, "Nelfe80"));
        Assert.True(FantomesDuSpeedrun.MeilleurQueLeMondial(1500, ashPremier, temps, "Nelfe80"));
        Assert.False(FantomesDuSpeedrun.MeilleurQueLeMondial(1, [], temps, "Nelfe80"));
    }

    [Fact]
    public void La_longueur_d_un_fantome_se_lit_apres_son_etat_de_depart()
    {
        // NGHOST1\0, crc, decalage, taille de l'etat (3), l'etat, puis le nombre d'images (1232)
        var f = new List<byte>("NGHOST1\0"u8.ToArray());
        f.AddRange(BitConverter.GetBytes(0xF9394E97u));
        f.AddRange(BitConverter.GetBytes(0));
        f.AddRange(BitConverter.GetBytes(3u));
        f.AddRange(new byte[] { 1, 2, 3 });
        f.AddRange(BitConverter.GetBytes(1232u));
        Assert.Equal(1232, FantomesDuSpeedrun.ImagesDuFantome(f.ToArray()));
        Assert.Null(FantomesDuSpeedrun.ImagesDuFantome(f.Take(20).ToArray()));
        Assert.Null(FantomesDuSpeedrun.ImagesDuFantome("PASUNFANTOME_________"u8.ToArray()));
    }

    [Fact]
    public void Un_niveau_de_Mario_se_court_avec_des_fantomes_pas_l_any_ni_un_autre_jeu()
    {
        Assert.True(FantomesDuSpeedrun.PourCeDefi(Lire("super-mario-bros.1-1.json")));
        Assert.False(FantomesDuSpeedrun.PourCeDefi(Lire("super-mario-bros.any.json")));
        Assert.False(FantomesDuSpeedrun.PourCeDefi(Lire("sonic-the-hedgehog.green-hill-1.json")));
        Assert.Equal("fceumm_ghosts", FantomesDuSpeedrun.CoeurDe(Lire("super-mario-bros.1-1.json")));
    }

    [Fact]
    public void Un_niveau_de_Sonic_se_court_sur_le_coeur_hote_Mega_Drive_pas_l_any()
    {
        Assert.Equal("genesis_plus_gx_ghosts", FantomesDuSpeedrun.CoeurDe(Lire("sonic-the-hedgehog.green-hill-1.v2.json")));
        Assert.Equal("genesis_plus_gx_ghosts", FantomesDuSpeedrun.CoeurDe(Lire("sonic-the-hedgehog.final-zone.json")));
        Assert.Null(FantomesDuSpeedrun.CoeurDe(Lire("sonic-the-hedgehog.any.json")));
    }

    [Theory]
    [InlineData("super-metroid.any.json")]
    [InlineData("super-metroid.ceres.json")]
    public void Super_Metroid_se_court_sur_le_coeur_hote_SNES(string reglement)
        => Assert.Equal("snes9x_ghosts", FantomesDuSpeedrun.CoeurDe(Lire(reglement)));

    [Theory]
    [InlineData(ChoixDesFantomes.MonRecordEtMondial, "Nelfe", "rp_nelfe,rp_ash")]
    [InlineData(ChoixDesFantomes.MonRecord, "nelfe", "rp_nelfe")]
    [InlineData(ChoixDesFantomes.RecordMondial, "Nelfe", "rp_ash")]
    [InlineData(ChoixDesFantomes.Aucun, "Nelfe", "")]
    [InlineData(ChoixDesFantomes.MonRecordEtMondial, "Ash", "rp_ash")]
    [InlineData(ChoixDesFantomes.MonRecordEtMondial, "Ben", "rp_ash")]
    [InlineData(ChoixDesFantomes.MonRecord, "Inconnu", "")]
    public void Les_replays_viennent_du_classement_sans_doublon(ChoixDesFantomes choix, string pseudo, string attendus)
        => Assert.Equal(attendus, string.Join(",", FantomesDuSpeedrun.Replays(Classement, pseudo, choix)));

    [Theory]
    // (10/11) record du monde sans replay (SMB 1-2 : Old AF Gamer) : le meilleur temps qui a un fantome le remplace, jamais le mien
    [InlineData("Nelfe", "rp_neo")]
    [InlineData("Neo", "rp_neo,rp_ben")]
    public void Un_record_du_monde_sans_fantome_laisse_courir_le_meilleur_fantome_disponible(string pseudo, string attendus)
    {
        IReadOnlyList<(string Joueur, string? Replay)> lignes = [("Old", null), ("Neo", "rp_neo"), ("Ben", "rp_ben")];
        Assert.Equal(attendus, string.Join(",", FantomesDuSpeedrun.Replays(lignes, pseudo, ChoixDesFantomes.MonRecordEtMondial)));
        Assert.Equal("NEO (#2)", FantomesDuSpeedrun.EtiquetteDuRang("Neo", 2));
    }

    [Fact]
    public void Le_classement_du_site_donne_le_joueur_et_son_replay()
    {
        using var json = JsonDocument.Parse("""{"rows":[{"player":"Ash","replay":{"id":"rp_1"}},{"player":"Ben","replay":null},{"player":"Cid"},{"player":"Dan","replay":null,"ghost":{"id":"rp_4"}}]}""");
        Assert.Equal([("Ash", "rp_1"), ("Ben", null), ("Cid", null), ("Dan", "rp_4")], FantomesDuSpeedrun.LignesDuClassement(json.RootElement));
    }

    [Fact]
    public void Le_coeur_hote_ne_part_que_si_le_site_admet_son_empreinte()
    {
        using var admis = JsonDocument.Parse("""{"ghost_cores":["ABC"]}""");
        using var ancien = JsonDocument.Parse("""{"games":[]}""");
        Assert.True(FantomesDuSpeedrun.Admet(admis.RootElement, "abc"));
        Assert.False(FantomesDuSpeedrun.Admet(admis.RootElement, "def"));
        Assert.False(FantomesDuSpeedrun.Admet(ancien.RootElement, "abc"));
    }

    [Fact]
    public void Mon_temps_vient_du_classement_et_un_temps_ne_compte_que_si_les_fantomes_courent()
    {
        using var json = JsonDocument.Parse("""{"rows":[{"player":"Ash","value":1578},{"player":"Nelfe","value":1695}]}""");
        Assert.Equal(1695L, FantomesDuSpeedrun.MonTemps(json.RootElement, "nelfe"));
        Assert.Null(FantomesDuSpeedrun.MonTemps(json.RootElement, "Inconnu"));
        Assert.False(new FantomesDuSpeedrun(null!, null!, null!, null!, null!).NouveauTemps(1500));
    }

    [Fact]
    public void Le_choix_defile_et_revient()
    {
        var f = new FantomesDuSpeedrun(null!, null!, null!, null!, null!);
        Assert.Equal(ChoixDesFantomes.MonRecordEtMondial, f.Choix);
        for (var i = 0; i < 4; i++) f.ChoixSuivant();
        Assert.Equal(ChoixDesFantomes.MonRecordEtMondial, f.Choix);
    }

    /// <summary>Un BSV v2 minimal : etat brut de 4 octets, deux images, un checkpoint periodique a sauter.</summary>
    private static byte[] Bsv()
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        var etat = new byte[] { 1, 2, 3, 4 };
        w.Write(0x42535632u); w.Write(2u); w.Write(0x393a432fu); w.Write((uint)(2 + 12 + etat.Length));
        w.Write(0L); w.Write(2u); w.Write(64u); w.Write(4u); w.Write(0u);
        w.Write((byte)0); w.Write((byte)0); w.Write((uint)etat.Length); w.Write((uint)etat.Length); w.Write((uint)etat.Length); w.Write(etat);
        // image 1 : une entree, puis un checkpoint
        w.Write(0u); w.Write((byte)0); w.Write((ushort)1);
        w.Write(new byte[] { 0, 1, 0, 0 }); w.Write((ushort)8); w.Write((short)1);
        w.Write((byte)'C'); w.Write((byte)0); w.Write((byte)0); w.Write(4u); w.Write(4u); w.Write(4u); w.Write(new byte[4]);
        // image 2 : rien
        w.Write(0u); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)'f');
        w.Flush();
        return m.ToArray();
    }

    [Fact]
    public void Un_replay_devient_un_nghost()
    {
        var n = ReplayVersFantome.Convertir(Bsv());
        Assert.Equal("NGHOST1\0", Encoding.ASCII.GetString(n, 0, 8));
        Assert.Equal(0x393a432fu, BitConverter.ToUInt32(n, 8));
        Assert.Equal(0, BitConverter.ToInt32(n, 12));
        Assert.Equal(4u, BitConverter.ToUInt32(n, 16));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, n[20..24]);
        Assert.Equal(2u, BitConverter.ToUInt32(n, 24));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(n, 28));
        Assert.Equal((ushort)8, BitConverter.ToUInt16(n, 34));
        Assert.Equal((ushort)0, BitConverter.ToUInt16(n, 38));
        Assert.Equal(40, n.Length);
    }

    [Fact]
    public void Un_fichier_qui_n_est_pas_un_replay_est_refuse()
        => Assert.Throws<InvalidDataException>(() => ReplayVersFantome.Convertir(new byte[64]));

    [Fact]
    public void Un_vrai_replay_donne_le_meme_fantome_que_l_outil()
    {
        if (Environment.GetEnvironmentVariable("NELFEPLAY_FANTOME_ESSAI") is not { Length: > 0 } essai) return;
        var chemins = essai.Split('|');
        Assert.Equal(File.ReadAllBytes(chemins[1]), ReplayVersFantome.Convertir(File.ReadAllBytes(chemins[0])));
    }

    [Fact]
    public void Mon_record_local_garde_le_meilleur_temps_et_son_fantome()
    {
        var avant = FantomesDuSpeedrun.DossierDesRecordsLocaux;
        FantomesDuSpeedrun.DossierDesRecordsLocaux = Path.Combine(Path.GetTempPath(), "nelfe-records-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string defi = "sonic-the-hedgehog.green-hill-2";
            Assert.Null(FantomesDuSpeedrun.LireLeRecordLocal(defi));
            FantomesDuSpeedrun.EcrireLeRecordLocal(defi, 2300, "rp_A", [1, 2]);
            FantomesDuSpeedrun.EcrireLeRecordLocal(defi, 2400, "rp_B", [3]);   // moins bon : garde rp_A
            Assert.Equal((2300L, "rp_A"), (FantomesDuSpeedrun.LireLeRecordLocal(defi)!.Images, FantomesDuSpeedrun.LireLeRecordLocal(defi)!.Replay));
            FantomesDuSpeedrun.EcrireLeRecordLocal(defi, 2200, "rp_C", [4, 5, 6]);
            var r = FantomesDuSpeedrun.LireLeRecordLocal(defi)!;
            Assert.Equal(2200L, r.Images);
            Assert.Equal(new byte[] { 4, 5, 6 }, r.Fantome);
            FantomesDuSpeedrun.EcrireLeRecordLocal("../echappe", 1, "rp_D", [7]);
            Assert.Null(FantomesDuSpeedrun.LireLeRecordLocal("../echappe"));
        }
        finally
        {
            if (Directory.Exists(FantomesDuSpeedrun.DossierDesRecordsLocaux)) Directory.Delete(FantomesDuSpeedrun.DossierDesRecordsLocaux, true);
            FantomesDuSpeedrun.DossierDesRecordsLocaux = avant;
        }
    }

    [Theory]
    [InlineData("Nelfe80", false, "NELFE80")]
    [InlineData("NeoGeoff", true, "NEOGEOFF (WR)")]
    [InlineData("", true, "WR")]
    [InlineData("un pseudo vraiment tres long", false, "UN PSEUDO VRAIMENT T")]
    public void L_etiquette_d_un_fantome_est_le_pseudo_et_WR_pour_le_record_mondial(string pseudo, bool mondial, string attendu)
        => Assert.Equal(attendu, FantomesDuSpeedrun.Etiquette(pseudo, mondial));
}
