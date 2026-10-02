using RetroBat.Api.Leaderboard;
using Xunit;
using static RetroBat.Api.Leaderboard.LeaderboardPanelModel;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un classement par regle dans le panneau d'ES (2026-10-02). Le panneau melangeait les regles :
/// un score 1CC MULTI se classait au milieu des scores solo, et le meme joueur y figurait deux
/// fois. Les onglets ordinaires montrent la regle principale ; chaque autre regle a son onglet
/// mondial, juste apres « Monde ».
/// </summary>
public sealed class LeaderboardRulesTabsTests
{
    [Theory]
    [InlineData(new[] { "1cc", "1cc-multi" }, "1cc")]
    [InlineData(new[] { "1cc-multi", "1cc" }, "1cc")]
    [InlineData(new[] { "1lc" }, "1lc")]
    [InlineData(new string[0], "")]
    public void La_regle_principale_est_le_1cc_s_il_est_ouvert(string[] regles, string attendue)
    {
        Assert.Equal(attendue, LeaderboardClient.ReglePrincipale(regles));
    }

    [Fact]
    public void Chaque_autre_regle_a_son_onglet()
    {
        Assert.Equal(new[] { Vue.MondeMulti },
            LeaderboardInputService.OngletsDeRegle(new[] { "1cc", "1cc-multi" }, "1cc").Select(o => o.Vue));
        Assert.Equal(new[] { Vue.MondeMulti, Vue.Monde1lc },
            LeaderboardInputService.OngletsDeRegle(new[] { "1cc", "1lc", "1cc-multi" }, "1cc").Select(o => o.Vue));
        Assert.Empty(LeaderboardInputService.OngletsDeRegle(new[] { "1cc" }, "1cc"));
        Assert.Empty(LeaderboardInputService.OngletsDeRegle(new[] { "1lc" }, "1lc"));   // le 1LC est deja la regle des onglets
    }

    [Fact]
    public void La_pastille_ecrit_la_regle_comme_partout()
    {
        Assert.Equal("1CC MULTI", LeaderboardInputService.LibelleDeRegle("1cc-multi"));
        Assert.Equal("1CC", LeaderboardInputService.LibelleDeRegle("1cc"));
        Assert.Equal("1LC", LeaderboardInputService.LibelleDeRegle("1lc"));
    }

    [Fact]
    public void L_onglet_1cc_multi_suit_monde_et_l_on_entre_sur_monde()
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(salleConnue: true, villeConnue: false, paysConnu: false, aDesRecords: true, multi: true);
        m.PoserLesLignes(5);
        Assert.Equal(new[] { Vue.MesRecords, Vue.MaSalle, Vue.Monde, Vue.MondeMulti }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.False(m.SurLaPorte);

        Assert.Equal(Effet.PrendreLeFocus, m.Entree(EntreePanneau.Gauche));
        Assert.Equal(Vue.Monde, m.VueCourante);   // prendre la main ne change pas de vue
        Assert.Equal(Effet.ChargerLaVue, m.Entree(EntreePanneau.Droite));
        Assert.Equal(Vue.MondeMulti, m.VueCourante);
        Assert.True(m.SurLaPorte);
        Assert.Equal(Effet.RendreLeFocus, m.Entree(EntreePanneau.Droite));
    }

    [Fact]
    public void Live_et_contest_reste_contre_la_porte()
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(false, false, false, true, multi: true);
        m.PoserLesEvenements(true);
        Assert.Equal(new[] { Vue.MesRecords, Vue.Monde, Vue.MondeMulti, Vue.LiveEtContest }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
        m.PoserLesEvenements(false);
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.Equal(new[] { Vue.MesRecords, Vue.Monde, Vue.MondeMulti }, m.Onglets);
    }

    [Fact]
    public void Un_jeu_a_une_regle_garde_ses_onglets()
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(false, false, false, true);
        Assert.Equal(new[] { Vue.MesRecords, Vue.Monde }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.True(m.SurLaPorte);
    }
}
