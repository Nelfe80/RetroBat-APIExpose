using System.Diagnostics;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA FIN D'UN JEU LANCE PAR L'API (09/11) : elle tombe des que le RetroArch du lanceur disparait (processus ou fenetre),
/// sans attendre que le lanceur rende la main (48 s vues apres une erreur de FBNeo). Le lanceur reste le filet.
/// </summary>
public sealed class FinDuJeuDirectTests
{
    // ---------- La decision, releve par releve ----------

    [Fact]
    public void Rien_avant_que_RetroArch_apparaisse()
    {
        var guet = new GuetDeLaFinDuJeu();
        for (var i = 0; i < 50; i++) Assert.Null(guet.Relever(processusLa: false, fenetreLa: false));
        Assert.False(guet.ProcessusVu);
    }

    [Fact]
    public void Processus_termine_fin_au_second_releve_sans_lui()
    {
        var guet = new GuetDeLaFinDuJeu();
        Assert.Null(guet.Relever(true, true));
        Assert.Null(guet.Relever(false, false));
        Assert.Equal("processus", guet.Relever(false, false));
    }

    [Fact]
    public void Un_releve_manque_ne_termine_pas_la_partie()
    {
        var guet = new GuetDeLaFinDuJeu();
        Assert.Null(guet.Relever(true, true));
        Assert.Null(guet.Relever(false, false));
        Assert.Null(guet.Relever(true, true));
        Assert.Null(guet.Relever(false, false));
        Assert.Null(guet.Relever(true, true));
    }

    [Fact]
    public void Chargement_sans_fenetre_pas_de_fin()
    {
        // Un coeur d'arcade met plusieurs secondes a ouvrir sa fenetre : une fenetre jamais vue ne compte pas.
        var guet = new GuetDeLaFinDuJeu();
        for (var i = 0; i < 40; i++) Assert.Null(guet.Relever(true, false));
        Assert.True(guet.ProcessusVu);
    }

    [Fact]
    public void Fenetre_recreee_au_plein_ecran_pas_de_fin()
    {
        var guet = new GuetDeLaFinDuJeu();
        Assert.Null(guet.Relever(true, true));
        for (var i = 0; i < GuetDeLaFinDuJeu.AbsencesDeLaFenetre - 1; i++) Assert.Null(guet.Relever(true, false));
        Assert.Null(guet.Relever(true, true));
        for (var i = 0; i < GuetDeLaFinDuJeu.AbsencesDeLaFenetre - 1; i++) Assert.Null(guet.Relever(true, false));
    }

    [Fact]
    public void Fenetre_disparue_processus_reste_fin_apres_deux_secondes()
    {
        var guet = new GuetDeLaFinDuJeu();
        Assert.Null(guet.Relever(true, true));
        for (var i = 0; i < GuetDeLaFinDuJeu.AbsencesDeLaFenetre - 1; i++) Assert.Null(guet.Relever(true, false));
        Assert.Equal("fenetre", guet.Relever(true, false));
        Assert.True(GuetDeLaFinDuJeu.AbsencesDeLaFenetre * GuetDeLaFinDuJeu.Pas <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Fin_du_processus_annoncee_en_moins_de_trois_secondes()
    {
        // Pire cas : RetroArch sort juste apres un releve ; la fin tombe au releve AbsencesDuProcessus suivant.
        Assert.True(GuetDeLaFinDuJeu.AbsencesDuProcessus * GuetDeLaFinDuJeu.Pas < TimeSpan.FromSeconds(3));
    }

    // ---------- Le guet complet, sonde simulee ----------

    [Fact]
    public async Task Guet_rend_processus_sans_attendre_le_lanceur()
    {
        var lanceur = new TaskCompletionSource();
        var releves = new Queue<(bool, bool)?>([(true, true), (true, true), (false, false), (false, false)]);
        var chrono = Stopwatch.StartNew();
        var raison = await LancementDirect.GuetterLaFinAsync(() => releves.Count > 0 ? releves.Dequeue() : (false, false),
            lanceur.Task, TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal("processus", raison);
        Assert.False(lanceur.Task.IsCompleted);
        Assert.True(chrono.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Guet_rend_lanceur_si_RetroArch_jamais_vu()
    {
        var lanceur = new TaskCompletionSource();
        var n = 0;
        var guet = LancementDirect.GuetterLaFinAsync(() =>
        {
            if (++n == 5) lanceur.TrySetResult();
            return (false, false);
        }, lanceur.Task, TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal("lanceur", await guet.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Guet_lanceur_sorti_pendant_la_partie()
    {
        // Le filet d'avant reste : un lanceur qui rend la main termine la partie, meme RetroArch encore vu.
        var lanceur = new TaskCompletionSource();
        var n = 0;
        var guet = LancementDirect.GuetterLaFinAsync(() =>
        {
            if (++n == 3) lanceur.TrySetResult();
            return (true, true);
        }, lanceur.Task, TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal("lanceur", await guet.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Guet_saute_les_releves_refuses()
    {
        var lanceur = new TaskCompletionSource();
        var releves = new Queue<(bool, bool)?>([(true, true), null, null, null, null, (true, true), (false, false), null, (false, false)]);
        var raison = await LancementDirect.GuetterLaFinAsync(() => releves.Count > 0 ? releves.Dequeue() : null,
            lanceur.Task, TimeSpan.FromMilliseconds(5), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("processus", raison);
        Assert.Empty(releves);
    }

    [Fact]
    public async Task Guet_rend_fenetre_si_le_processus_traine()
    {
        var lanceur = new TaskCompletionSource();
        var vus = 0;
        var raison = await LancementDirect.GuetterLaFinAsync(() => (true, ++vus <= 3),
            lanceur.Task, TimeSpan.FromMilliseconds(5), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("fenetre", raison);
        Assert.Equal(3 + GuetDeLaFinDuJeu.AbsencesDeLaFenetre, vus);
    }

    // ---------- Le RetroArch du lanceur, et lui seul ----------

    private static ArbreDesProcessus.Entree P(int id, int parent, string exe) => new(id, parent, exe);

    [Fact]
    public void RetroArch_enfant_du_lanceur_trouve()
    {
        var arbre = new[] { P(4, 0, "System"), P(100, 50, "emulatorLauncher.exe"), P(200, 100, "retroarch.patched.RETROBAT.exe") };
        Assert.Equal([200], ArbreDesProcessus.RetroArchSous(arbre, 100));
    }

    [Fact]
    public void RetroArch_petit_enfant_du_lanceur_trouve()
    {
        var arbre = new[] { P(100, 50, "emulatorLauncher.exe"), P(150, 100, "cmd.exe"), P(200, 150, "RetroArch.exe") };
        Assert.Equal([200], ArbreDesProcessus.RetroArchSous(arbre, 100));
    }

    [Fact]
    public void Autres_RetroArch_ignores()
    {
        // Le RetroArch cache d'un rendu (enfant de l'API), un banc ouvert a cote, un spectateur : aucun ne descend du lanceur.
        var arbre = new[]
        {
            P(10, 1, "RetroBat.Api.exe"), P(11, 10, "retroarch.exe"),
            P(20, 1, "python.exe"), P(21, 20, "retroarch.patched.RETROBAT.exe"),
            P(100, 10, "emulatorLauncher.exe"), P(101, 100, "conhost.exe"),
        };
        Assert.Empty(ArbreDesProcessus.RetroArchSous(arbre, 100));
    }

    [Fact]
    public void Parent_mort_ou_boucle_ne_rattache_rien()
    {
        var arbre = new[] { P(100, 50, "emulatorLauncher.exe"), P(300, 299, "retroarch.exe"), P(400, 400, "retroarch.exe"), P(500, 501, "x.exe"), P(501, 500, "retroarch.exe") };
        Assert.Empty(ArbreDesProcessus.RetroArchSous(arbre, 100));
    }

    [Fact]
    public void Instantane_reel_porte_ce_processus_et_son_parent()
    {
        var instantane = ArbreDesProcessus.Instantane();
        Assert.NotNull(instantane);
        using var moi = Process.GetCurrentProcess();
        var entree = Assert.Single(instantane!, e => e.Id == moi.Id);
        Assert.True(entree.Parent > 0);
        Assert.False(string.IsNullOrEmpty(entree.Exe));
    }

    [Fact]
    public void Aucune_fenetre_pour_un_processus_inexistant()
    {
        Assert.False(ArbreDesProcessus.FenetreVisible([]));
        Assert.False(ArbreDesProcessus.FenetreVisible([int.MaxValue - 3]));
    }
}
