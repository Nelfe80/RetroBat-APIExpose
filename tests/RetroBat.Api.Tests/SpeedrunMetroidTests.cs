using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE SUPER METROID (04/11, demande du user) : le jeu complet et la station Ceres, chronometres sur l'etat du jeu
/// $0998. Le depart est la fin de la cinematique d'une nouvelle partie ($1E vers $1F, mesure au banc) : charger une sauvegarde
/// passe par $02 et ne fait pas partir le chrono. Arrivees : $20 (depart de Ceres) et $26 (fin du jeu).
/// </summary>
public class SpeedrunMetroidTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier($"super-metroid.{nom}.json")));

    private static int _ids;

    private static Signal S(long frame, int etat)
        => Signal.Depuis(frame, "EVENT_TRIGGER", "0x0998", etat, 1_000_000 + (long)(frame * 1000 / 60.098812));

    private static SessionSpeedrun Session(string nom)
    {
        var r = Lire(nom);
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "m" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        return s;
    }

    private static void Etats(SessionSpeedrun s, params (long Frame, int Etat)[] suite)
    {
        foreach (var (frame, etat) in suite)
        {
            s.RecevoirImage([S(frame, etat)]);
            // le service releve dans RetroArch les adresses que la session ne connait pas encore
            foreach (var a in s.AdressesInconnues) s.Relever(a, etat);
        }
    }

    [Theory]
    [InlineData("any")]
    [InlineData("ceres")]
    public void Le_reglement_ne_cite_que_des_lignes_du_MEM(string nom)
        => Assert.Empty(Lire(nom).VerifierContre(File.ReadAllBytes(Fichier("super-metroid.MEM"))));

    [Theory]
    [InlineData("kraid", 0xA59F, "BOSS_HIT", "0xD829", 0x01)]
    [InlineData("phantoon", 0xCD13, "BOSS_HIT", "0xD82B", 0x01)]
    [InlineData("draygon", 0xDA60, "BOSS_DEFEATED", "0xD82C", 0x03)]
    [InlineData("ridley", 0xB32E, "BOSS_DEFEATED", "0xD82A", 0x01)]
    public void Un_boss_part_a_l_entree_de_sa_salle_et_finit_a_sa_mort(string nom, int salle, string action, string drapeau, int vaincu)
    {
        var r = Lire(nom);
        Assert.Equal("speedrun_boss", r.Kind);
        Assert.Empty(r.VerifierContre(File.ReadAllBytes(Fichier("super-metroid.MEM"))));
        Assert.True(SessionSpeedrun.SeRelanceSeul(r));
        var s = Session(nom);
        Image(s, 100, S(100, 0x08), Salle(100, 0x1234));
        Image(s, 900, Salle(900, salle));
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Image(s, 5000, Signal.Depuis(5000, action, drapeau, vaincu, 1_000_000 + 5000 * 1000 / 60));
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Une_mort_contre_un_boss_fait_echouer()
    {
        var s = Session("kraid");
        Image(s, 100, S(100, 0x08), Salle(100, 0x1234));
        Image(s, 900, Salle(900, 0xA59F));
        Image(s, 3000, S(3000, 0x13));
        Assert.NotEqual(SessionSpeedrun.Running, s.Etat);
        Assert.NotEqual(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Mother_Brain_va_jusqu_a_la_fin_du_jeu()
    {
        var s = Session("mother-brain");
        Image(s, 100, S(100, 0x08), Salle(100, 0xDDF3));
        Image(s, 900, Salle(900, 0xDD58));
        Image(s, 30000, S(30000, 0x26));
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    private static Signal Salle(long frame, int salle)
        => Signal.Depuis(frame, "PROGRESSION_ZONE", "0x079B", salle, 1_000_000 + (long)(frame * 1000 / 60.098812));

    private static void Image(SessionSpeedrun s, long frame, params Signal[] signaux)
    {
        s.RecevoirImage(signaux);
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
    }

    [Fact]
    public void Ceres_part_sur_son_etat_et_se_relance_seul_l_any_part_du_demarrage()
    {
        Assert.True(SessionSpeedrun.SeRelanceSeul(Lire("ceres")));
        Assert.False(SessionSpeedrun.SeRelanceSeul(Lire("any")));
        Assert.Equal("normal_boot", Lire("any").StartPolicy);
    }

    [Fact]
    public void Ceres_se_finit_au_depart_vers_Zebes()
    {
        var s = Session("ceres");
        Etats(s, (100, 0x1E), (160, 0x1F), (390, 0x08), (9000, 0x20));
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Une_mort_a_Ceres_fait_echouer_la_tentative()
    {
        var s = Session("ceres");
        Etats(s, (100, 0x1E), (160, 0x1F), (390, 0x08), (4000, 0x13));
        Assert.NotEqual(SessionSpeedrun.Running, s.Etat);
        Assert.NotEqual(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void L_any_tient_les_portes_et_les_morts_et_finit_a_la_fin_du_jeu()
    {
        var s = Session("any");
        Etats(s, (100, 0x1E), (8000, 0x1F), (8300, 0x08), (9000, 0x0B), (9100, 0x08), (20000, 0x13), (20500, 0x1A), (21000, 0x08));
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Etats(s, (300000, 0x26));
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Une_sauvegarde_chargee_ne_fait_pas_partir_le_chrono()
    {
        var s = Session("any");
        Etats(s, (100, 0x02), (200, 0x1F), (300, 0x08), (400, 0x26));
        Assert.NotEqual(SessionSpeedrun.Finished, s.Etat);
    }
}

/// <summary>Les jeux ouverts aux seuls speedruns arrivent a part dans l'index du site.</summary>
public class JeuxDesSpeedrunsSeulsTests
{
    [Fact]
    public void L_index_du_site_porte_les_jeux_des_speedruns_a_part()
    {
        const string json = """
            {"ok":true,"games":[{"system_id":"nes","rom_group":"super-mario-bros","rulesets":[{"id":"1cc","profile_version":1}]}],
             "speedrun_games":[{"system_id":"snes","rom_group":"super-metroid","mem_sha256":"f8","rulesets":[{"id":"speedrun","profile_version":1}]}]}
            """;
        var index = System.Text.Json.JsonSerializer.Deserialize<RetroBat.Api.Infrastructure.OpenGamesManifest>(json)!;
        Assert.Equal(["super-mario-bros"], index.Games.Select(g => g.RomGroup));
        Assert.Equal(["super-metroid"], index.SpeedrunGames.Select(g => g.RomGroup));
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<RetroBat.Api.Infrastructure.OpenGamesManifest>("""{"games":[]}""")!.SpeedrunGames);
    }
}
