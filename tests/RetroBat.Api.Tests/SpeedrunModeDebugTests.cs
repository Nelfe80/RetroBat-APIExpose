using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE MODE DEBUG DE SONIC (reglement v2 de Green Hill 1, plugins-64, mesure du 30/10) : 0xFFFB passe a 1 au chargement du
/// niveau quand le code a ete entre a l'ecran titre. Il empeche le depart ou annule la tentative, et le joueur en est
/// prevenu (decision du user du 31/10).
/// </summary>
public class SpeedrunModeDebugTests
{
    private static readonly Reglement GreenHill1V2 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.v2.json")));

    private static int _ids;

    private static SessionSpeedrun Nouvelle() => new(GreenHill1V2.ChallengeId, GreenHill1V2,
        () => "id" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.922743));

    private static void Image(SessionSpeedrun s, params Signal[] signaux) => s.RecevoirImage(signaux);

    private static void Partir(SessionSpeedrun s)
    {
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        Image(s, S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(909, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(934, "GAME_PLAYING", "0x00F601", 0x0C));
    }

    [Fact]
    public void Le_mode_debug_empeche_le_depart_et_le_joueur_en_est_prevenu()
    {
        var s = Nouvelle();
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        s.PrendreLesMessages();
        Image(s, S(700, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(908, "LEVEL_CHEAT_ACTIVATED", "0x00FFFB", 1));
        Image(s, S(910, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(936, "GAME_PLAYING", "0x00F601", 0x0C));

        Assert.Equal(SessionSpeedrun.EtatHud(EtatTentative.Preparee), s.Etat);
        Assert.Equal("speedrun.msg.debug_blocked", Assert.Single(s.PrendreLesMessages()).Cle);
    }

    [Fact]
    public void Le_mode_debug_releve_au_depart_empeche_d_armer()
    {
        var s = Nouvelle();
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0xFFFB ? 1 : 0);
        Image(s, S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(909, "GENERAL_TIMER", "0x00FE1F", 1));

        Assert.Equal(SessionSpeedrun.EtatHud(EtatTentative.Preparee), s.Etat);
        Assert.Contains(s.PrendreLesMessages(), m => m.Cle == "speedrun.msg.debug_blocked");
    }

    [Fact]
    public void Le_mode_debug_en_course_annule_la_tentative_avec_son_message()
    {
        var s = Nouvelle();
        Partir(s);
        s.PrendreLesMessages();
        Image(s, S(1500, "LEVEL_CHEAT_ACTIVATED", "0x00FFFB", 1));

        Assert.Equal(SessionSpeedrun.EtatHud(EtatTentative.Annulee), s.Etat);
        Assert.Equal("speedrun.msg.voided_debug", Assert.Single(s.PrendreLesMessages()).Cle);
    }

    [Fact]
    public void Sans_le_mode_debug_rien_n_est_signale()
    {
        var s = Nouvelle();
        Partir(s);
        Assert.DoesNotContain(s.PrendreLesMessages(), m => m.Cle.Contains("debug", StringComparison.Ordinal));
    }
}
