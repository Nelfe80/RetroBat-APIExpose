using RetroBat.Api.Replay.Video;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE CONTROLE DE L'ARRIVEE D'UNE EPREUVE DANS SON FILM (11/11) : les traces de SMB du banc fceumm (image par image) passent
/// par le wrapper simule et l'evaluateur ; le temps officiel rejoue doit etre le temps certifie, a l'image pres quand les
/// lectures sont faites a chaque image, dans l'intervalle exact sinon.
/// </summary>
public class ControleDEpreuveTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"super-mario-bros.{slug}.json")));

    private static LignesDuMem Mem => LignesDuMem.Lire(File.ReadAllText(Fichier("super-mario-bros.MEM")));

    private static List<PassageMesure> Jouer(string trace, string slug)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), Mem, [(slug, Lire(slug))]);

    private static List<long> Releves(long jusqua, int pas = 1) => Enumerable.Range(0, (int)(jusqua / pas) + 1).Select(i => (long)i * pas).ToList();

    [Fact]
    public void L_arrivee_a_la_bonne_image_est_fidele_et_une_image_de_trop_ne_l_est_pas()
    {
        // 1-1 : main au joueur a l'image 196, mat a l'image 1655 (1 459 images).
        var passages = Jouer("smb-1-1-mat", "1-1");
        var releves = Releves(2000);
        var juste = ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("super-mario-bros.1-1", 1, 1655 - 196), passages, releves, 16, 1955);
        Assert.Equal(ControleDeFidelite.Fidele, juste.Issue);
        Assert.Contains("1459 images rejouees pour 1459", juste.Detail);
        var fausse = ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("super-mario-bros.1-1", 1, 1655 - 196 + 1), passages, releves, 16, 1955);
        Assert.Equal(ControleDeFidelite.Desync, fausse.Issue);
    }

    [Fact]
    public void Des_lectures_espacees_donnent_l_intervalle_exact()
    {
        // Lectures toutes les 4 images : depart vu a 196 (lecture d'avant a 192), mat vu a 1656 (lecture d'avant a 1652).
        var passages = BancDesReplays.Mesurer(Clairsemer(TraceDeReplay.Lire(File.ReadAllLines(Fichier("smb-1-1-mat.trace"))), 4), Mem,
            [("1-1", Lire("1-1"))]);
        var p = Assert.Single(passages);
        var mesure = p.Resultat.FramesOfficielles!.Value;
        var releves = Releves(2000, 4);
        Assert.Equal(ControleDeFidelite.Fidele,
            ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("super-mario-bros.1-1", 1, 1459), passages, releves, 16, 1955).Issue);
        Assert.Equal(ControleDeFidelite.Desync,
            ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("super-mario-bros.1-1", 1, mesure + 8), passages, releves, 16, 1955).Issue);
    }

    /// <summary>La trace lue toutes les <paramref name="pas"/> images seulement, comme un rendu qui lit moins vite.</summary>
    private static TraceDeReplay Clairsemer(TraceDeReplay t, int pas)
    {
        var dernieres = new Dictionary<(long, string), long>();
        var lignes = new List<string> { "# images " + t.Images };
        var parImage = t.Lectures.GroupBy(l => l.Frame).ToDictionary(g => g.Key, g => g.ToList());
        var courantes = new Dictionary<(long, string), long>();
        for (long f = 0; f <= t.Images; f++)
        {
            if (parImage.TryGetValue(f, out var ls)) foreach (var l in ls) courantes[(l.Adresse, l.Type)] = l.Valeur;
            if (f % pas != 0) continue;
            foreach (var (cle, v) in courantes)
            {
                if (dernieres.TryGetValue(cle, out var avant) && avant == v) continue;
                dernieres[cle] = v;
                lignes.Add($"{f} {Hex.Ecrire(cle.Item1)} {cle.Item2} {v}");
            }
        }
        return TraceDeReplay.Lire(lignes);
    }

    [Fact]
    public void Une_mort_rejouee_est_une_derive()
    {
        var passages = Jouer("smb-1-1-mort", "1-1");
        var v = ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("super-mario-bros.1-1", 1, 1459), passages, Releves(3000), 16, 2500);
        Assert.Equal(ControleDeFidelite.Desync, v.Issue);
        Assert.Contains("echec rejoue", v.Detail);
    }

    [Fact]
    public void Sans_depart_rejoue_lectures_denses_derive_lectures_espacees_non_verifie()
    {
        var aucun = new List<PassageMesure>();
        Assert.Equal(ControleDeFidelite.Desync,
            ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("x", 1, 100), aucun, Releves(2000), 0, 2000).Issue);
        Assert.Equal(ControleDeFidelite.NonVerifie,
            ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("x", 1, 100), aucun, Releves(2000, 12), 0, 2000).Issue);
        Assert.Equal(ControleDeFidelite.NonVerifie,
            ControleDEpreuve.JugerLesPassages(new EpreuveCertifiee("x", 1, 100), aucun, [], 0, 2000).Issue);
    }

    [Fact]
    public void Les_adresses_se_lisent_en_blocs_dans_un_seul_datagramme()
    {
        var (c, raison) = ControleDEpreuve.Depuis(Lire("1-1"), File.ReadAllText(Fichier("super-mario-bros.MEM")));
        Assert.Null(raison);
        Assert.True(System.Text.Encoding.ASCII.GetByteCount(c!.Datagramme) <= ControleDEpreuve.DatagrammeMax);
        Assert.StartsWith("GET_CONFIG_PARAM active_replay\n", c.Datagramme);
        // Chaque adresse du .MEM tombe dans un bloc.
        foreach (var l in Mem.Lignes)
            Assert.Contains(c.Blocs, b => b.Adresse <= l.Adresse && l.Adresse < b.Adresse + b.Octets);
        Assert.Equal([new ControleDEpreuve.Bloc(0x10, 3), new ControleDEpreuve.Bloc(0x100, 2)],
            ControleDEpreuve.Regrouper([(0x10, 1), (0x12, 1), (0x100, 2)]));
    }

    [Fact]
    public void Les_valeurs_suivent_l_ordre_d_octets_du_type()
    {
        Assert.Equal(0x1234, ControleDEpreuve.Valeur("u16le", [0x34, 0x12], 0, 2));
        Assert.Equal(0x1234, ControleDEpreuve.Valeur("u16be", [0x12, 0x34], 0, 2));
        Assert.Equal(0x56, ControleDEpreuve.Valeur("u8", [0x12, 0x56], 1, 1));
    }

    [Fact]
    public void Un_reglement_absent_ou_d_une_autre_version_ne_se_controle_pas()
    {
        var catalogue = new CatalogueDesDefis();
        Assert.Contains("absent", ControleDEpreuve.Pour(new EpreuveCertifiee("super-mario-bros.1-1", 1, 1459), catalogue).Raison);
        catalogue.Remplacer([Lire("1-1")]);
        var version = Lire("1-1").Version;
        Assert.Contains("version", ControleDEpreuve.Pour(new EpreuveCertifiee("super-mario-bros.1-1", version + 1, 1459), catalogue).Raison);
        var (c, raison) = ControleDEpreuve.Pour(new EpreuveCertifiee("super-mario-bros.1-1", version, 1459), catalogue,
            _ => File.ReadAllBytes(Fichier("super-mario-bros.MEM")));
        Assert.Null(raison);
        Assert.NotNull(c);
    }
}
