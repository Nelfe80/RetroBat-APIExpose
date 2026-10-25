using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les coeurs de MULTIPLAYER sur chaque borne (GBA le 23/10, NGPC le 25/10) : ils arrivent par outils.json, se posent au
/// bon endroit selon le montage du wrapper, et chaque systeme d'EmulationStation apprend le sien sans perdre ses autres
/// emulateurs.
/// </summary>
public class MultiplayerCoeursTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "coeurs-mp-" + Guid.NewGuid().ToString("N")[..8]);

    public MultiplayerCoeursTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string EsSystems = """
        <?xml version="1.0"?>
        <systemList>
          <system>
            <name>snes</name>
            <emulators><emulator name="libretro"><cores><core>snes9x</core></cores></emulator></emulators>
          </system>
          <system>
            <name>gba</name>
            <fullname>Game Boy Advance</fullname>
            <emulators>
              <emulator name="libretro">
                <cores>
                  <core>mgba</core>
                  <core>gpsp</core>
                </cores>
              </emulator>
              <emulator name="mgba"><cores><core>mgba</core></cores></emulator>
              <emulator name="nosgba"/>
            </emulators>
          </system>
          <system>
            <name>ngpc</name>
            <emulators>
              <emulator name="libretro"><cores><core>mednafen_ngp</core><core>race</core></cores></emulator>
            </emulators>
          </system>
        </systemList>
        """;

    private static string[] CoeursDe(System.Xml.Linq.XElement systeme)
        => systeme.Element("emulators")!.Elements("emulator").First().Element("cores")!.Elements("core").Select(c => c.Value).ToArray();

    [Fact]
    public void Chaque_systeme_garde_ses_emulateurs_et_gagne_son_coeur_en_dernier()
    {
        var sortie = MultiplayerCoeursService.EsSystemsNelfePlay(EsSystems, MultiplayerCoeursService.Coeurs);
        Assert.NotNull(sortie);
        var doc = System.Xml.Linq.XDocument.Parse(sortie!);
        var systemes = doc.Root!.Elements("system").ToArray();
        Assert.Equal(["gba", "ngpc"], systemes.Select(s => (string?) s.Element("name")));
        var emulateurs = systemes[0].Element("emulators")!.Elements("emulator").Select(e => e.Attribute("name")!.Value).ToArray();
        Assert.Equal(["libretro", "mgba", "nosgba"], emulateurs);
        Assert.Equal(["mgba", "gpsp", "mgba_splitscreen"], CoeursDe(systemes[0]));
        Assert.Equal(["mednafen_ngp", "race", "mednafen_ngp_link"], CoeursDe(systemes[1]));
        // Rien des autres systemes, rien que RetroBat decrit ailleurs (nom complet, extensions).
        Assert.Null(systemes[0].Element("fullname"));
        // Une seconde passe ne l'ajoute pas deux fois.
        Assert.Equal(sortie, MultiplayerCoeursService.EsSystemsNelfePlay(
            EsSystems.Replace("<core>gpsp</core>", "<core>gpsp</core><core>mgba_splitscreen</core>"), MultiplayerCoeursService.Coeurs));
    }

    [Fact]
    public void Un_coeur_pas_encore_livre_ne_touche_pas_a_son_systeme()
    {
        var gba = MultiplayerCoeursService.Coeurs.Where(c => c.Systeme == "gba").ToArray();
        var doc = System.Xml.Linq.XDocument.Parse(MultiplayerCoeursService.EsSystemsNelfePlay(EsSystems, gba)!);
        Assert.Equal(["gba"], doc.Root!.Elements("system").Select(s => (string?) s.Element("name")));
    }

    [Theory]
    [InlineData("<systemList><system><name>snes</name><emulators/></system></systemList>")]
    [InlineData("<systemList><system><name>gba</name><emulators><emulator name=\"mgba\"/></emulators></system></systemList>")]
    public void Sans_le_systeme_ou_sans_libretro_rien_n_est_ecrit(string esSystems)
        => Assert.Null(MultiplayerCoeursService.EsSystemsNelfePlay(esSystems, MultiplayerCoeursService.Coeurs));

    [Fact]
    public void Le_coeur_va_dans_cores_real_si_l_ancien_montage_l_y_range_deja()
    {
        const string coeur = "mednafen_ngp_link_libretro.dll";
        var cores = Path.Combine(_racine, "cores");
        var coresReal = Path.Combine(_racine, "cores_real");
        Directory.CreateDirectory(cores);
        Directory.CreateDirectory(coresReal);
        Assert.Equal(Path.Combine(cores, coeur), MultiplayerCoeursService.OuEstLeVraiCoeur(cores, coresReal, coeur));
        File.WriteAllText(Path.Combine(coresReal, coeur), "vrai coeur");
        Assert.Equal(Path.Combine(coresReal, coeur), MultiplayerCoeursService.OuEstLeVraiCoeur(cores, coresReal, coeur));
    }

    [Fact]
    public void Un_fichier_identique_n_est_pas_recopie()
    {
        var source = Path.Combine(_racine, "source.dll");
        var cible = Path.Combine(_racine, "sous", "cible.dll");
        File.WriteAllText(source, "v1");
        Assert.True(MultiplayerCoeursService.Poser(source, cible));
        Assert.False(MultiplayerCoeursService.Poser(source, cible));
        File.WriteAllText(source, "v2");
        Assert.True(MultiplayerCoeursService.Poser(source, cible));
        Assert.Equal("v2", File.ReadAllText(cible));
    }

    [Fact]
    public void Outils_json_peut_livrer_les_coeurs_et_leurs_fiches_et_rien_d_autre_sous_tools_cores()
    {
        const string sha = "28e99e260bb5914fad5a1dc2055c1d8bea4b817ef9b95199700824dedee32708";
        string Outil(string fichier) => System.Text.Json.JsonSerializer.Serialize(new
        {
            fichier, version = Path.GetFileName(fichier), sha256 = sha, taille = 2003921,
            url = "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.92/" + Path.GetFileName(fichier),
        });
        var livres = MultiplayerCoeursService.Coeurs.SelectMany(c => c.CheminsDesOutils).ToArray();
        Assert.Equal(["tools/cores/mgba_splitscreen_libretro.dll", "tools/cores/mgba_splitscreen_libretro.info",
            "tools/cores/mednafen_ngp_link_libretro.dll", "tools/cores/mednafen_ngp_link_libretro.info"], livres);
        var json = "{\"outils\":[" + string.Join(",", livres.Append("tools/cores/fbneo_libretro.dll").Select(Outil)) + "]}";
        var retenus = SelfUpdateService.LireOutils(json, "Nelfe80/RetroBat-APIExpose").Select(o => o.Fichier).ToArray();
        Assert.Equal(livres, retenus);
        Assert.All(retenus, f => Assert.False(SelfUpdateService.EstUnExe(f)));
    }

    [Fact]
    public void Release_ps1_livre_les_memes_fichiers_que_le_service()
    {
        var release = File.ReadAllText(Path.Combine(TrouverLaRacine(), "release.ps1"));
        foreach (var chemin in MultiplayerCoeursService.Coeurs.SelectMany(c => c.CheminsDesOutils))
        {
            Assert.Contains("'" + chemin + "'", release, StringComparison.Ordinal);
        }
    }

    private static string TrouverLaRacine()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "release.ps1")))
        {
            dossier = dossier.Parent;
        }

        return dossier?.FullName ?? throw new InvalidOperationException("release.ps1 introuvable");
    }
}
