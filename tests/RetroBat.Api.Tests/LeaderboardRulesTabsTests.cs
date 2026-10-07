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
        // Une place par regle, de gauche a droite : le 1LC, puis le 1CC MULTI contre Monde.
        Assert.Equal(new[] { (Vue.AutreRegle1, "1cc-multi") },
            LeaderboardInputService.OngletsDeRegle(new[] { "1cc", "1cc-multi" }, "1cc"));
        Assert.Equal(new[] { (Vue.AutreRegle1, "1lc"), (Vue.AutreRegle2, "1cc-multi") },
            LeaderboardInputService.OngletsDeRegle(new[] { "1cc", "1lc", "1cc-multi" }, "1cc"));
        Assert.Empty(LeaderboardInputService.OngletsDeRegle(new[] { "1cc" }, "1cc"));
        Assert.Empty(LeaderboardInputService.OngletsDeRegle(new[] { "1lc" }, "1lc"));   // le 1LC est deja la regle des onglets
    }

    [Fact]
    public void Les_modes_ont_leurs_onglets_ranges_par_famille()
    {
        // Bubble Bobble (2026-10-06), dans l'ordre de l'index de la plateforme. De gauche a droite :
        // les modes du 1CC MULTI puis lui, puis les modes du 1CC, contre Monde qui est le 1CC.
        var regles = new[] { "1cc", "1cc-multi", "1cc-multi-original", "1cc-multi-power-up", "1cc-multi-super",
            "1cc-original", "1cc-power-up", "1cc-super" };
        var onglets = LeaderboardInputService.OngletsDeRegle(regles, "1cc");
        Assert.Equal(new[] { "1cc-multi-original", "1cc-multi-power-up", "1cc-multi-super", "1cc-multi",
            "1cc-original", "1cc-power-up", "1cc-super" }, onglets.Select(o => o.Regle));
        Assert.Equal(LeaderboardPanelModel.PlacesDeRegle.Take(7), onglets.Select(o => o.Vue));
    }

    [Fact]
    public void Plus_de_regles_que_de_places_on_garde_les_plus_proches_de_monde()
    {
        var regles = new[] { "1cc" }.Concat(Enumerable.Range(1, 10).Select(i => "1cc-mode" + i)).ToArray();
        var onglets = LeaderboardInputService.OngletsDeRegle(regles, "1cc");
        Assert.Equal(LeaderboardPanelModel.PlacesDeRegle, onglets.Select(o => o.Vue));
        Assert.Equal("1cc-mode3", onglets[0].Regle);
        Assert.Equal("1cc-mode10", onglets[^1].Regle);
    }

    [Fact]
    public void Au_dela_de_deux_modes_Monde_ne_garde_que_sa_pastille()
    {
        // Demande user 2026-10-07 : « [1CC-O] [1CC-PU] [1CC] » plutot que « [1CC-O] [1CC-PU] WORLD [1CC] ».
        string Nom(Vue v) => v == Vue.Monde ? "WORLD" : v.ToString();
        Assert.Equal(new[] { "MesRecords", "", "", "" },
            LeaderboardInputService.NomsDesOnglets(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.AutreRegle2, Vue.Monde }, Nom));
        // Deux modes (1CC MULTI et le 1CC de Monde) : Monde garde son nom.
        Assert.Equal(new[] { "MesRecords", "", "WORLD" },
            LeaderboardInputService.NomsDesOnglets(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.Monde }, Nom));
        Assert.Equal(new[] { "MesRecords", "WORLD", "LiveEtContest" },
            LeaderboardInputService.NomsDesOnglets(new[] { Vue.MesRecords, Vue.Monde, Vue.LiveEtContest }, Nom));
    }

    [Fact]
    public void La_pastille_ecrit_la_regle_comme_partout()
    {
        Assert.Equal("1CC MULTI", LeaderboardInputService.LibelleDeRegle("1cc-multi"));
        Assert.Equal("1CC", LeaderboardInputService.LibelleDeRegle("1cc"));
        Assert.Equal("1LC", LeaderboardInputService.LibelleDeRegle("1lc"));
    }

    [Fact]
    public void Un_mode_ne_garde_que_ses_initiales()
    {
        // Demande user 2026-10-06 : « 1CC-PU » pour power-up, pour tenir dans la rangee d'onglets.
        Assert.Equal("1CC-PU", LeaderboardInputService.LibelleDeRegle("1cc-power-up"));
        Assert.Equal("1CC-O", LeaderboardInputService.LibelleDeRegle("1cc-original"));
        Assert.Equal("1CC-S", LeaderboardInputService.LibelleDeRegle("1cc-super"));
        Assert.Equal("1CC MULTI-PU", LeaderboardInputService.LibelleDeRegle("1cc-multi-power-up"));
        Assert.Equal("1CC MULTI-S", LeaderboardInputService.LibelleDeRegle("1cc-multi-super"));
        Assert.Equal("1LC-PU", LeaderboardInputService.LibelleDeRegle("1lc-power-up"));
    }

    [Fact]
    public void Deux_modes_aux_memes_initiales_gardent_leur_nom()
    {
        var regles = new[] { "1cc", "1cc-super", "1cc-speed", "1cc-power-up" };
        Assert.Equal("1CC-SUPER", LeaderboardInputService.LibelleDeRegle("1cc-super", regles));
        Assert.Equal("1CC-SPEED", LeaderboardInputService.LibelleDeRegle("1cc-speed", regles));
        Assert.Equal("1CC-PU", LeaderboardInputService.LibelleDeRegle("1cc-power-up", regles));
    }

    [Fact]
    public void L_onglet_1cc_multi_precede_monde_et_l_on_entre_sur_monde()
    {
        // « [1CC MULTI] MONDE [1CC] » (decision user 2026-10-03) : Monde reste contre la porte.
        var m = new LeaderboardPanelModel();
        m.Ouvrir(salleConnue: true, villeConnue: false, paysConnu: false, aDesRecords: true);
        Assert.True(m.PoserLesRegles(new[] { Vue.AutreRegle1 }));
        m.PoserLesLignes(5);
        Assert.Equal(new[] { Vue.MesRecords, Vue.MaSalle, Vue.AutreRegle1, Vue.Monde }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.True(m.SurLaPorte);

        Assert.Equal(Effet.PrendreLeFocus, m.Entree(EntreePanneau.Gauche));
        Assert.Equal(Vue.Monde, m.VueCourante);   // prendre la main ne change pas de vue
        Assert.Equal(Effet.ChargerLaVue, m.Entree(EntreePanneau.Gauche));
        Assert.Equal(Vue.AutreRegle1, m.VueCourante);
        Assert.False(m.SurLaPorte);
        Assert.Equal(Effet.ChargerLaVue, m.Entree(EntreePanneau.Droite));
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.Equal(Effet.RendreLeFocus, m.Entree(EntreePanneau.Droite));
    }

    [Fact]
    public void Live_et_contest_reste_contre_la_porte()
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(false, false, false, true);
        m.PoserLesEvenements(true);
        m.PoserLesRegles(new[] { Vue.AutreRegle1 });
        Assert.Equal(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.Monde, Vue.LiveEtContest }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
        m.PoserLesEvenements(false);
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.Equal(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.Monde }, m.Onglets);
    }

    [Fact]
    public void Sans_classement_1cc_multi_pas_d_onglet()
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(false, false, false, true);
        Assert.False(m.PoserLesRegles(Array.Empty<Vue>()));
        Assert.Equal(new[] { Vue.MesRecords, Vue.Monde }, m.Onglets);

        // Le classement se vide pendant qu'on le regarde : on retombe sur Monde. (1LC en premiere
        // place, 1CC MULTI en deuxieme, comme OngletsDeRegle les donne.)
        m.PoserLesRegles(new[] { Vue.AutreRegle2, Vue.AutreRegle1 });
        Assert.Equal(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.AutreRegle2, Vue.Monde }, m.Onglets);
        m.Entree(EntreePanneau.Gauche);
        m.Entree(EntreePanneau.Gauche);
        Assert.Equal(Vue.AutreRegle2, m.VueCourante);
        Assert.True(m.PoserLesRegles(new[] { Vue.AutreRegle1 }));
        Assert.Equal(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.Monde }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
    }

    [Fact]
    public void Une_regle_arrivee_apres_sa_voisine_se_range_a_sa_place()
    {
        // Les classements des modes arrivent dans le desordre : la rangee garde l'ordre des places.
        // Inseree d'office contre Monde, la premiere place se serait rangee apres la troisieme.
        var m = new LeaderboardPanelModel();
        m.Ouvrir(false, false, false, true);
        m.PoserLesRegles(new[] { Vue.AutreRegle3 });
        m.PoserLesRegles(new[] { Vue.AutreRegle3, Vue.AutreRegle1 });
        Assert.True(m.PoserLesRegles(new[] { Vue.AutreRegle3, Vue.AutreRegle1, Vue.AutreRegle2 }));
        Assert.Equal(new[] { Vue.MesRecords, Vue.AutreRegle1, Vue.AutreRegle2, Vue.AutreRegle3, Vue.Monde }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);
    }

    [Fact]
    public void Ville_et_pays_arrivent_apres_chargement_sans_deplacer_le_curseur()
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(salleConnue: true, villeConnue: false, paysConnu: false, aDesRecords: true);
        m.PoserLesRegles(new[] { Vue.AutreRegle1 });
        Assert.Equal(Vue.Monde, m.VueCourante);

        Assert.True(m.PoserLesLieux(villeConnue: false, paysConnu: true));
        Assert.Equal(new[] { Vue.MesRecords, Vue.MaSalle, Vue.MonPays, Vue.AutreRegle1, Vue.Monde }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);

        // La ville arrive plus tard : elle se range quand meme avant le pays.
        Assert.True(m.PoserLesLieux(villeConnue: true, paysConnu: true));
        Assert.Equal(new[] { Vue.MesRecords, Vue.MaSalle, Vue.MaVille, Vue.MonPays, Vue.AutreRegle1, Vue.Monde }, m.Onglets);
        Assert.Equal(Vue.Monde, m.VueCourante);

        Assert.False(m.PoserLesLieux(villeConnue: true, paysConnu: true));
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
