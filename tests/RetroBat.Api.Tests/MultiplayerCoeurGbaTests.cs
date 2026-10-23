using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le coeur GBA de MULTIPLAYER sur chaque borne (23/10) : il arrive par outils.json, se pose au bon endroit selon le montage
/// du wrapper, et le systeme gba d'EmulationStation l'apprend sans perdre ses autres emulateurs.
/// </summary>
public class MultiplayerCoeurGbaTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "coeur-gba-" + Guid.NewGuid().ToString("N")[..8]);

    public MultiplayerCoeurGbaTests() => Directory.CreateDirectory(_racine);

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
        </systemList>
        """;

    [Fact]
    public void Le_systeme_gba_garde_ses_emulateurs_et_gagne_le_coeur_en_dernier()
    {
        var sortie = MultiplayerCoeurGbaService.EsSystemsNelfePlay(EsSystems);
        Assert.NotNull(sortie);
        var doc = System.Xml.Linq.XDocument.Parse(sortie!);
        var systeme = Assert.Single(doc.Root!.Elements("system"));
        Assert.Equal("gba", (string?) systeme.Element("name"));
        var emulateurs = systeme.Element("emulators")!.Elements("emulator").Select(e => e.Attribute("name")!.Value).ToArray();
        Assert.Equal(["libretro", "mgba", "nosgba"], emulateurs);
        var coeurs = systeme.Element("emulators")!.Elements("emulator").First().Element("cores")!.Elements("core").Select(c => c.Value).ToArray();
        Assert.Equal(["mgba", "gpsp", "mgba_splitscreen"], coeurs);
        // Rien des autres systemes, rien que RetroBat decrit ailleurs (nom complet, extensions).
        Assert.Null(systeme.Element("fullname"));
        // Une seconde passe ne l'ajoute pas deux fois.
        Assert.Equal(sortie, MultiplayerCoeurGbaService.EsSystemsNelfePlay(EsSystems.Replace("<core>gpsp</core>", "<core>gpsp</core><core>mgba_splitscreen</core>")));
    }

    [Theory]
    [InlineData("<systemList><system><name>snes</name><emulators/></system></systemList>")]
    [InlineData("<systemList><system><name>gba</name><emulators><emulator name=\"mgba\"/></emulators></system></systemList>")]
    public void Sans_gba_ou_sans_libretro_rien_n_est_ecrit(string esSystems)
        => Assert.Null(MultiplayerCoeurGbaService.EsSystemsNelfePlay(esSystems));

    [Fact]
    public void Le_coeur_va_dans_cores_real_si_l_ancien_montage_l_y_range_deja()
    {
        var cores = Path.Combine(_racine, "cores");
        var coresReal = Path.Combine(_racine, "cores_real");
        Directory.CreateDirectory(cores);
        Directory.CreateDirectory(coresReal);
        Assert.Equal(Path.Combine(cores, MultiplayerCoeurGbaService.Coeur), MultiplayerCoeurGbaService.OuEstLeVraiCoeur(cores, coresReal));
        File.WriteAllText(Path.Combine(coresReal, MultiplayerCoeurGbaService.Coeur), "vrai coeur");
        Assert.Equal(Path.Combine(coresReal, MultiplayerCoeurGbaService.Coeur), MultiplayerCoeurGbaService.OuEstLeVraiCoeur(cores, coresReal));
    }

    [Fact]
    public void Un_fichier_identique_n_est_pas_recopie()
    {
        var source = Path.Combine(_racine, "source.dll");
        var cible = Path.Combine(_racine, "sous", "cible.dll");
        File.WriteAllText(source, "v1");
        Assert.True(MultiplayerCoeurGbaService.Poser(source, cible));
        Assert.False(MultiplayerCoeurGbaService.Poser(source, cible));
        File.WriteAllText(source, "v2");
        Assert.True(MultiplayerCoeurGbaService.Poser(source, cible));
        Assert.Equal("v2", File.ReadAllText(cible));
    }

    [Fact]
    public void Outils_json_peut_livrer_le_coeur_et_sa_fiche_et_rien_d_autre_sous_tools_cores()
    {
        const string sha = "28e99e260bb5914fad5a1dc2055c1d8bea4b817ef9b95199700824dedee32708";
        string Outil(string fichier, string nom) => System.Text.Json.JsonSerializer.Serialize(new
        {
            fichier, version = nom, sha256 = sha, taille = 2003921,
            url = "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.89/" + nom,
        });
        var json = "{\"outils\":[" + Outil(MultiplayerCoeurGbaService.CheminDuCoeur, "mgba_splitscreen_libretro.dll") + ","
            + Outil(MultiplayerCoeurGbaService.CheminDeLaFiche, "mgba_splitscreen_libretro.info") + ","
            + Outil("tools/cores/fbneo_libretro.dll", "fbneo_libretro.dll") + "]}";
        var retenus = SelfUpdateService.LireOutils(json, "Nelfe80/RetroBat-APIExpose").Select(o => o.Fichier).ToArray();
        Assert.Equal(["tools/cores/mgba_splitscreen_libretro.dll", "tools/cores/mgba_splitscreen_libretro.info"], retenus);
        Assert.False(SelfUpdateService.EstUnExe(retenus[0]));
    }
}
