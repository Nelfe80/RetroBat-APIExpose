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
