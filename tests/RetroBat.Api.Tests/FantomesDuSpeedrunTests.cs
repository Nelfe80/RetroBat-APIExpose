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
    public void Un_niveau_de_Mario_se_court_avec_des_fantomes_pas_l_any_ni_un_autre_jeu()
    {
        Assert.True(FantomesDuSpeedrun.PourCeDefi(Lire("super-mario-bros.1-1.json")));
        Assert.False(FantomesDuSpeedrun.PourCeDefi(Lire("super-mario-bros.any.json")));
        Assert.False(FantomesDuSpeedrun.PourCeDefi(Lire("sonic-the-hedgehog.green-hill-1.json")));
    }

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

    [Fact]
    public void Le_classement_du_site_donne_le_joueur_et_son_replay()
    {
        using var json = JsonDocument.Parse("""{"rows":[{"player":"Ash","replay":{"id":"rp_1"}},{"player":"Ben","replay":null},{"player":"Cid"}]}""");
        Assert.Equal([("Ash", "rp_1"), ("Ben", null), ("Cid", null)], FantomesDuSpeedrun.LignesDuClassement(json.RootElement));
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
}
