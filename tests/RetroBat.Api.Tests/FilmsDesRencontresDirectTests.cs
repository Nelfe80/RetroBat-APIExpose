using RetroBat.Api.Multiplayer;
using RetroBat.Api.Replay.Video;
using Xunit;
using E = RetroBat.Api.Leaderboard.LeaderboardOverlayService.EtatAuLancement;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE LOT M4 DU CDC FILMS MULTIPLAYER (08/11) : les cartes du film d'une rencontre (ecran de lancement VS, ecran de fin), leur
/// passage a l'exe du direct 0.7.2 (version YouTube et direct), la fin de passage qui laisse finir le film (B.4).
/// </summary>
public sealed class FilmsDesRencontresDirectTests
{
    private static readonly string Room = new('a', 32);
    private static readonly string AutreRoom = new('b', 32);

    private static L.JoueurDeLancement Joueur(int place, E etat, string texte)
        => new(place, "player" + place, "", null, "FR", "", "", "VICTOIRES", "", "SERIE", "", etat, texte, 20, 4, DateTime.UtcNow);

    private static L.EcranDeLancement Lancement(string room, DateTime? depart = null, bool sortie = true)
        => new(room, "combat", "VS", "Street Fighter II' Champion Edition", "RENCONTRE CLASSEE", "duel", null,
            [Joueur(1, E.Pret, "PRET !"), Joueur(2, E.Synchronisation, "SYNCHRONISATION")], "", "", "", "On attend PLAYER2", ["A", "B", "C"], 1,
            "A VOUS DE JOUER !", depart, true, "REESSAYER", "QUITTER", "MAINTENIR", "button_a", "button_b", SortiePossible: sortie);

    private static L.EcranDeFin Fin(string room)
        => new(room, "combat", "VICTOIRE !", "Street Fighter II' Champion Edition", "1:01", null, [], null, "", "",
            new L.TextesDeFin("FIN DE PARTIE", "VAINQUEUR", "", "MAINTENIR", "REVANCHE", "QUITTER"), "button_a", "button_b");

    private static L.ContenuMultiplayer Contenu(L.EcranDeLancement? lancement = null, L.EcranDeFin? fin = null)
        => new([string.Empty], 0, false, string.Empty, string.Empty, string.Empty, null, [], -1, string.Empty, string.Empty,
            [new L.Aide("button_a", "REJOUER")], PleinEcran: true, BoutonOk: "OK", GlypheOk: "button_a", Fin: fin, Lancement: lancement);

    /// <summary>Un rendu sans fenetre : il note ce qu'on lui demande de dessiner.</summary>
    private sealed class Rendu
    {
        public readonly List<(L.ContenuMultiplayer Contenu, double Temps)> Dessins = new();
        public bool Echoue;

        public byte[] Dessiner(L.ContenuMultiplayer c, double t)
        {
            if (Echoue) throw new InvalidOperationException("apercu impossible");
            lock (Dessins) Dessins.Add((c, t));
            return [0x89, 0x50, 0x4E, 0x47];
        }
    }

    private static string Dossier()
    {
        var d = Path.Combine(Path.GetTempPath(), "nelfe-cartes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static async Task<bool> ParaitAsync(string chemin)
    {
        for (var i = 0; i < 100 && !File.Exists(chemin); i++) await Task.Delay(20);
        return File.Exists(chemin);
    }

    [Fact]
    public void Une_carte_n_a_ni_boutons_ni_aide()
    {
        var lancement = MultiplayerCartesDuFilm.PourLeFilm(Contenu(Lancement(Room)));
        Assert.False(lancement.Lancement!.SortiePossible);
        Assert.False(lancement.Lancement.AttenteLongue);
        Assert.Empty(lancement.Aides);
        Assert.Equal("", lancement.BoutonOk);
        var fin = MultiplayerCartesDuFilm.PourLeFilm(Contenu(fin: Fin(Room)));
        Assert.True(fin.Fin!.SansBoutons);
        Assert.False(Fin(Room).SansBoutons);
    }

    [Fact]
    public void La_carte_VS_se_pose_au_depart_meme_sans_boucle_du_depart()
    {
        var maintenant = new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);
        var carte = MultiplayerCartesDuFilm.PourLeFilm(Contenu(Lancement(Room)), depart: true, maintenant).Lancement!;
        Assert.Equal(maintenant, carte.DepartUtc);
        Assert.Equal(2, carte.Etape);
        Assert.Equal("", carte.Legende);
        Assert.All(carte.Joueurs, j => Assert.Equal(E.Pret, j.Etat));
        // Le texte d'un joueur deja pret vaut pour les autres.
        Assert.All(carte.Joueurs, j => Assert.Equal("PRET !", j.TexteDeLEtat));
        // Un depart deja donne garde son heure.
        var donne = maintenant.AddMinutes(-3);
        Assert.Equal(donne, MultiplayerCartesDuFilm.PourLeFilm(Contenu(Lancement(Room, donne)), depart: true, maintenant).Lancement!.DepartUtc);
        // Le direct montre l'ecran tel qu'il est (l'hote pret, l'invite en route).
        Assert.Null(MultiplayerCartesDuFilm.PourLeFilm(Contenu(Lancement(Room))).Lancement!.DepartUtc);
    }

    [Fact]
    public async Task La_version_YouTube_prend_l_ecran_du_depart_et_attend_l_ecran_de_fin()
    {
        var rendu = new Rendu();
        var cartes = new MultiplayerCartesDuFilm(rendu.Dessiner);
        var dossier = Dossier();
        var depart = DateTime.UtcNow.AddSeconds(-90);
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room)));
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room, depart)));
        // Apres le depart, l'ecran s'efface : ce qui paraitrait ensuite ne remplace pas la carte VS.
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room) with { Legende = "apres" }));
        var tache = cartes.RendreLesCartesDeLaVersionAsync(Room, Path.Combine(dossier, "vs.png"), Path.Combine(dossier, "fin.png"),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(tache.IsCompleted);
        cartes.NoterLaFin(Room, Contenu(fin: Fin(Room)));
        var (avant, apres) = await tache;
        Assert.Equal(Path.Combine(dossier, "vs.png"), avant);
        Assert.Equal(Path.Combine(dossier, "fin.png"), apres);
        Assert.True(File.Exists(avant) && File.Exists(apres));
        Assert.Empty(Directory.GetFiles(dossier, "*.tmp"));
        var vs = rendu.Dessins.Single(d => d.Contenu.Lancement is not null);
        Assert.Equal(depart, vs.Contenu.Lancement!.DepartUtc);
        Assert.Equal(MultiplayerCartesDuFilm.TempsDuDepart, vs.Temps);
        var fin = rendu.Dessins.Single(d => d.Contenu.Fin is not null);
        Assert.Equal(MultiplayerCartesDuFilm.TempsDeLaFin, fin.Temps);
        Assert.True(fin.Contenu.Fin!.SansBoutons);
    }

    [Fact]
    public async Task La_version_YouTube_prend_les_ecrans_du_film_et_le_direct_ceux_de_l_hote()
    {
        // Decision du user du 08/11 : la version YouTube en anglais et vue du vainqueur ; le direct garde l'ecran de l'hote.
        var rendu = new Rendu();
        var cartes = new MultiplayerCartesDuFilm(rendu.Dessiner);
        var dossier = Dossier();
        var depart = DateTime.UtcNow.AddSeconds(-90);
        var lancementDuFilm = Lancement(Room, depart) with { Legende = "RANKED MATCH" };
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room, depart)), Contenu(lancementDuFilm));
        var finDuFilm = Fin(Room) with { Titre = "VICTORY!" };
        cartes.AttendreLaFinPourLeDirect(Room, Path.Combine(dossier, "direct-fin.png"));
        cartes.NoterLaFin(Room, Contenu(fin: Fin(Room) with { Titre = "DEFAITE" }), Contenu(fin: finDuFilm));
        await Task.Delay(300);
        var (avant, apres) = await cartes.RendreLesCartesDeLaVersionAsync(Room, Path.Combine(dossier, "vs.png"),
            Path.Combine(dossier, "fin.png"), TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(avant);
        Assert.NotNull(apres);
        Assert.Contains(rendu.Dessins, d => d.Contenu.Fin?.Titre == "DEFAITE");
        Assert.Contains(rendu.Dessins, d => d.Contenu.Fin?.Titre == "VICTORY!");
        Assert.Contains(rendu.Dessins, d => d.Contenu.Lancement?.Legende == "RANKED MATCH");
    }

    [Fact]
    public async Task Sans_ecran_de_fin_le_film_finit_sur_le_jeu()
    {
        var cartes = new MultiplayerCartesDuFilm(new Rendu().Dessiner);
        var dossier = Dossier();
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room)));
        var (avant, apres) = await cartes.RendreLesCartesDeLaVersionAsync(Room, Path.Combine(dossier, "vs.png"),
            Path.Combine(dossier, "fin.png"), TimeSpan.FromMilliseconds(150), CancellationToken.None);
        Assert.NotNull(avant);
        Assert.Null(apres);
        Assert.False(File.Exists(Path.Combine(dossier, "fin.png")));
    }

    [Fact]
    public async Task Une_autre_rencontre_n_a_pas_les_cartes_de_la_precedente()
    {
        var cartes = new MultiplayerCartesDuFilm(new Rendu().Dessiner);
        var dossier = Dossier();
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room, DateTime.UtcNow)));
        cartes.NoterLaFin(Room, Contenu(fin: Fin(Room)));
        cartes.NoterLeLancement(AutreRoom, Contenu(Lancement(AutreRoom)));
        var (avant, apres) = await cartes.RendreLesCartesDeLaVersionAsync(Room, Path.Combine(dossier, "vs.png"),
            Path.Combine(dossier, "fin.png"), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Null(avant);
        Assert.Null(apres);
        // Une fin d'ailleurs ne compte pas non plus.
        cartes.NoterLaFin(Room, Contenu(fin: Fin(Room)));
        Assert.Null((await cartes.RendreLesCartesDeLaVersionAsync(AutreRoom, Path.Combine(dossier, "vs2.png"),
            Path.Combine(dossier, "fin2.png"), TimeSpan.FromMilliseconds(100), CancellationToken.None)).Apres);
    }

    [Fact]
    public async Task Le_direct_recoit_l_ecran_de_fin_quand_il_parait()
    {
        var rendu = new Rendu();
        var cartes = new MultiplayerCartesDuFilm(rendu.Dessiner);
        var dossier = Dossier();
        var chemin = Path.Combine(dossier, "partage-fin.png");
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room)));
        Assert.True(cartes.RendreLeLancementDuDirect(Room, Path.Combine(dossier, "partage-vs.png")));
        Assert.False(cartes.RendreLeLancementDuDirect(AutreRoom, Path.Combine(dossier, "autre.png")));
        Assert.Equal(MultiplayerCartesDuFilm.TempsDuLancement, rendu.Dessins.Single().Temps);
        cartes.AttendreLaFinPourLeDirect(Room, chemin);
        cartes.NoterLaFin(AutreRoom, Contenu(fin: Fin(AutreRoom)));
        await Task.Delay(100);
        Assert.False(File.Exists(chemin));
        cartes.NoterLaFin(Room, Contenu(fin: Fin(Room)));
        Assert.True(await ParaitAsync(chemin));
        // Deja paru : le direct suivant (meme rencontre, relance de l'exe) le recoit tout de suite.
        var deuxieme = Path.Combine(dossier, "partage-fin-2.png");
        cartes.AttendreLaFinPourLeDirect(Room, deuxieme);
        Assert.True(await ParaitAsync(deuxieme));
        // Le passage fini, l'ecran ne s'ecrit plus.
        var troisieme = Path.Combine(dossier, "partage-fin-3.png");
        cartes.NoterLeLancement(AutreRoom, Contenu(Lancement(AutreRoom)));
        cartes.AttendreLaFinPourLeDirect(AutreRoom, troisieme);
        cartes.OublierLeDirect(AutreRoom);
        cartes.NoterLaFin(AutreRoom, Contenu(fin: Fin(AutreRoom)));
        await Task.Delay(150);
        Assert.False(File.Exists(troisieme));
    }

    [Fact]
    public void Un_rendu_rate_n_ecrit_rien()
    {
        var rendu = new Rendu { Echoue = true };
        var cartes = new MultiplayerCartesDuFilm(rendu.Dessiner);
        var dossier = Dossier();
        cartes.NoterLeLancement(Room, Contenu(Lancement(Room)));
        Assert.False(cartes.RendreLeLancementDuDirect(Room, Path.Combine(dossier, "partage-vs.png")));
        Assert.Empty(Directory.GetFiles(dossier));
    }

    [Fact]
    public void La_version_YouTube_d_une_rencontre_passe_ses_cartes_a_l_exe()
    {
        var a = LanceurDuDirect.ArgumentsDeLaVersion("mp.mkv", "mp.flv", 0, 31.2, 152.4, carteAvant: @"C:\c\vs.png", carteAvantS: 3,
            carteApres: @"C:\c\fin.png", carteApresS: 6);
        var i = a.ToList().IndexOf("--carte-avant");
        Assert.Equal(["--carte-avant", @"C:\c\vs.png", "3", "--carte-apres", @"C:\c\fin.png", "6"], a.Skip(i).Take(6));
        // Sans carte (exe ancien, image absente), les arguments d'avant.
        Assert.DoesNotContain("--carte-avant", LanceurDuDirect.ArgumentsDeLaVersion("mp.mkv", "mp.flv", 0, 31.2, 152.4));
        Assert.Equal(["--carte-apres", "fin.png", "6"], LanceurDuDirect.Cartes(null, 3, "fin.png", 6));
        Assert.Equal(["--carte-avant", "vs.png", "2.5"], LanceurDuDirect.Cartes("vs.png", 2.5, "fin.png", 0));
    }

    [Fact]
    public void Un_exe_introuvable_ne_pose_pas_de_cartes()
    {
        Assert.False(LanceurDuDirect.SaitLesCartes(null));
        Assert.False(LanceurDuDirect.SaitLesCartes(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N") + ".exe")));
        Assert.Equal(new Version(0, 7, 2), LanceurDuDirect.VersionDesCartes);
    }

    private static ReplayCaptureService.CaptureGardee Gardee()
        => new("mp_06GRJXJS0RJTDSZT0JDJ9PVXSG", @"C:\films\mp.mkv", 0, 0, DateTime.UtcNow, SansReplay: true, Origine: true, DureeS: 160,
            DebutSequenceS: 31.2, FinSequenceS: 152.4, Room: Room, CarteAvant: @"C:\films\mp.vs.png", CarteAvantS: 3,
            CarteApres: @"C:\films\mp.fin.png", CarteApresS: 6);

    [Fact]
    public void Seules_les_cartes_qui_partiront_comptent_dans_la_duree_annoncee()
    {
        var toutes = ReplayCaptureService.CartesQuiPartent(Gardee(), true, _ => true);
        Assert.Equal((3d, 6d), (toutes.CarteAvantS, toutes.CarteApresS));
        var champs = ReplayCaptureService.ChampsDeLaRencontre(toutes, DateTime.UtcNow);
        Assert.Equal(3d, (double)champs["intro_s"]!);
        Assert.Equal(6d, (double)champs["outro_s"]!);
        // L'image de fin manque : 0 s, et le site l'apprend.
        var sansFin = ReplayCaptureService.CartesQuiPartent(Gardee(), true, f => !f.EndsWith(".fin.png", StringComparison.Ordinal));
        Assert.Equal((3d, 0d), (sansFin.CarteAvantS, sansFin.CarteApresS));
        Assert.Null(sansFin.CarteApres);
        // Un exe 0.7.1 ignorerait les cartes : aucune.
        var ancien = ReplayCaptureService.CartesQuiPartent(Gardee(), false, _ => true);
        Assert.Equal((0d, 0d), (ancien.CarteAvantS, ancien.CarteApresS));
        // Une version deja fabriquee porte ses cartes : rien ne change.
        var faite = Gardee() with { Version = @"C:\films\mp.flv" };
        Assert.Equal(faite, ReplayCaptureService.CartesQuiPartent(faite, false, _ => false));
        Assert.Equal(130_200_000, ReplayCaptureService.TailleEstimee(toutes, 300_000_000));
    }

    [Fact]
    public void Le_journal_dit_les_cartes_du_film()
    {
        Assert.Equal("VS 3 s et fin 6 s", ReplayCaptureService.TexteDesCartes("a", "b"));
        Assert.Equal("VS 3 s, sans ecran de fin", ReplayCaptureService.TexteDesCartes("a", null));
        Assert.Equal("aucune", ReplayCaptureService.TexteDesCartes(null, null));
    }

    [Fact]
    public void La_fin_d_une_partie_finie_laisse_partir_le_film()
    {
        Assert.Contains("\"finirLeFilm\":true", DirectPartageService.CommandeJson(true, false, null, null, false, false, finirLeFilm: true));
        // Sans le champ, la commande d'avant (un exe 0.7.1 ne lit rien de plus).
        Assert.DoesNotContain("finirLeFilm", DirectPartageService.CommandeJson(true, false, null, null, false, false));
        Assert.True(DirectPartageService.FinirLeFilm(finie: true, alert: false));
        // Un passage qui cede l'antenne en pleine partie, ou qu'un direct de record prend : le film grandirait encore.
        Assert.False(DirectPartageService.FinirLeFilm(finie: false, alert: false));
        Assert.False(DirectPartageService.FinirLeFilm(finie: true, alert: true));
        // Le relais garde ses deux commandes, sans le champ : l'arret suit.
        Assert.DoesNotContain("finirLeFilm", DirectPartageService.CommandesDuRelais(false).Finir);
    }

    [Fact]
    public void Le_direct_pose_l_ecran_VS_seulement_avant_le_vrai_depart()
    {
        var avant = DirectPartageService.OptionsDesCartes(carteAvantRendue: true, invitesArrives: false);
        Assert.Equal(["--carte-avant", DirectPartageService.CheminDeLaCarteAvant, "3", "--carte-apres", DirectPartageService.CheminDeLaCarteApres, "6"], avant);
        // Les invites deja la : 3 s d'image fixe retarderaient le direct en pleine rencontre.
        Assert.Equal(["--carte-apres", DirectPartageService.CheminDeLaCarteApres, "6"],
            DirectPartageService.OptionsDesCartes(carteAvantRendue: true, invitesArrives: true));
        Assert.Equal(["--carte-apres", DirectPartageService.CheminDeLaCarteApres, "6"],
            DirectPartageService.OptionsDesCartes(carteAvantRendue: false, invitesArrives: false));
    }
}
