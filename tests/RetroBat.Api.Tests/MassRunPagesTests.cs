using System.Runtime.Versioning;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.MassRun;
using RetroBat.Api.Speedrun;
using Xunit;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES PAGES D'UNE COURSE MASS RUN (04/11, « les ecrans start et stats comme dans multiplayer ») : le depart, l'issue lue
/// dans le coeur, le bilan et son classement. Les apercus (PNG) ne se dessinent que si NELFEPLAY_APERCUS donne un dossier.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class MassRunPagesTests
{
    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");

    private static readonly DateTime Maintenant = new(2026, 11, 4, 20, 0, 0, DateTimeKind.Utc);

    private static MassRunService.SalleOuverte Salle(string mort = "debut")
        => new(4242, "nes/super-mario-bros", "1-1", mort, Maintenant.AddSeconds(150), 180, 12, 0, Maintenant.AddSeconds(90));

    private static DefisPanelService Service() => new(null!, null!, null!, null!, null!);

    private static MassRunService.SalleLocale Locale(uint modeMax, uint arrivee = 0, bool elimine = false, bool survie = false)
        => new("1-1", 0, "NELFE", "A98BFF", 0, 0, "smb.zip") { ModeMax = modeMax, Arrivee = arrivee, Elimine = elimine, Survie = survie };

    private static readonly MassRunCanal.LigneDeClassement[] Classement =
    [
        new(1, false, false, 2390, 0xFF5A5A, "TAILS"),
        new(2, false, false, 2412, 0x5AFF8C, "KNUCKLES"),
        new(3, true, false, 2460, 0xA98BFF, "NELFE"),
        new(4, false, false, 2511, 0xFFC83C, "AMY"),
        new(5, false, false, 2600, 0x3CC8FF, "SHADOW"),
        new(6, false, false, 2702, 0xFF8A3D, "ROUGE"),
    ];

    [Fact]
    public void Les_inscriptions_ferment_avant_le_go()
    {
        var s = Salle();
        Assert.True(s.Inscriptions(Maintenant));
        Assert.False(s.Inscriptions(Maintenant.AddSeconds(91)));
        Assert.True(s.GoUtc - s.InscriptionsUtc == TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(2u, 0u, false, false, null)]
    [InlineData(3u, 0u, false, false, IssueDeCourse.Abandon)]
    [InlineData(4u, 0u, false, false, IssueDeCourse.Abandon)]
    [InlineData(5u, 2460u, false, false, IssueDeCourse.Arrive)]
    [InlineData(5u, 1800u, true, true, IssueDeCourse.Elimine)]
    [InlineData(5u, 0u, false, false, IssueDeCourse.TempsEcoule)]
    [InlineData(5u, 0u, false, true, IssueDeCourse.Survivant)]
    public void L_issue_se_lit_dans_le_coeur(uint modeMax, uint arrivee, bool elimine, bool survie, IssueDeCourse? attendue)
        => Assert.Equal(attendue, DefisPanelService.IssueDe(Locale(modeMax, arrivee, elimine, survie)));

    [Fact]
    public void L_heure_de_la_salle_compte_pas_les_images_du_coeur()
    {
        Assert.False(MassRunService.LimiteDepassee(Maintenant, 180, Maintenant.AddSeconds(181)));
        Assert.True(MassRunService.LimiteDepassee(Maintenant, 180, Maintenant.AddSeconds(183)));
        Assert.False(MassRunService.LimiteDepassee(null, 180, Maintenant));
        Assert.Equal(IssueDeCourse.TempsEcoule, DefisPanelService.IssueDe(Locale(4) with { TempsEcoule = true }));
        Assert.Equal(IssueDeCourse.Survivant, DefisPanelService.IssueDe(Locale(4, survie: true) with { TempsEcoule = true }));
    }

    [Fact]
    public void Les_textes_du_coeur_tiennent_dans_ses_emplacements()
        => Assert.Equal(10, MassRunService.ClesDesTextes.Length);

    [Fact]
    public void Seul_dans_la_salle_le_joueur_le_sait()
    {
        Assert.Contains("alone", Service().ComposerLeDepartMassRun(Salle(), "Super Mario Bros.", null, false, 1, Maintenant).Legende);
        var seul = new BilanDeCourse(Salle(), "Super Mario Bros.", "NELFE", null, IssueDeCourse.TempsEcoule, null, null, 1, 0, [], true);
        Assert.Contains("only runner", Service().ComposerLeBilanDeCourse(seul).Note);
    }

    [Fact]
    public void Le_depart_porte_la_fiche_de_la_salle_et_le_mot_MASS_RUN()
    {
        var page = Service().ComposerLeDepartMassRun(Salle(), "Super Mario Bros.", null, false, 12, Maintenant);

        Assert.Equal("MASS RUN", page.Mot);
        Assert.Equal("Mass Speedrun · Level 1-1", page.SousTitre);
        Assert.Equal(1, page.Etape);
        Assert.True(page.SortiePossible);
        Assert.Contains(page.Fiche!, c => c.Valeur == "2:30");
        Assert.Contains(page.Fiche!, c => c.Valeur == "12");
        Assert.Equal(2, Service().ComposerLeDepartMassRun(Salle(), "Super Mario Bros.", null, true, 12, Maintenant).Etape);
    }

    [Fact]
    public void Le_bilan_donne_la_place_le_temps_et_l_ecart_au_premier()
    {
        var b = new BilanDeCourse(Salle(), "Super Mario Bros.", "NELFE", null, IssueDeCourse.Arrive, 2460, 3, 12, 6, Classement, false);
        var fin = Service().ComposerLeBilanDeCourse(b);

        Assert.Equal("FINISHED!", fin.Titre);
        Assert.Equal("MASS RUN · #3", fin.MonScore!.Ruban);
        Assert.Equal(Chronometrage.Afficher(2460, 60.0988), fin.MonScore.Valeur);
        Assert.StartsWith("+" + Chronometrage.Afficher(70, 60.0988), fin.MonScore.Second);
        Assert.Equal([12L, 6L, 3L], fin.Joueurs[0].Mesures.Select(m => m.Valeur!.Value));
        Assert.Equal(5, fin.Classement!.Count);
        Assert.Contains(fin.Classement, l => l.Marque == "moi");
    }

    [Fact]
    public void Le_premier_arrive_gagne_et_un_elimine_n_a_pas_d_ecart()
    {
        var gagnant = new BilanDeCourse(Salle(), "Super Mario Bros.", "TAILS", null, IssueDeCourse.Arrive, 2390, 1, 12, 6, Classement, true);
        Assert.Equal("WINNER!", Service().ComposerLeBilanDeCourse(gagnant).Titre);
        Assert.True(Service().ComposerLeBilanDeCourse(gagnant).MonScore!.Premier);

        var elimine = gagnant with { Salle = Salle("elimination"), Issue = IssueDeCourse.Elimine, Images = 1800, Rang = 9 };
        var fin = Service().ComposerLeBilanDeCourse(elimine);
        Assert.Equal("ELIMINATED", fin.Titre);
        Assert.Equal("", fin.MonScore!.Second);
    }

    [Fact]
    public void Le_classement_garde_le_premier_puis_le_joueur_et_ses_voisins()
    {
        var lignes = Enumerable.Range(1, 20).Select(r => new MassRunCanal.LigneDeClassement(r, r == 14, false, (uint)(2000 + r * 10), 0, "J" + r)).ToList();
        var autour = DefisPanelService.AutourDuJoueur(lignes);
        Assert.Equal([1, 12, 13, 14, 15], autour.Select(l => l.Rang));
    }

    [Fact]
    public void Les_pages_se_dessinent()
    {
        if (Sortie is null) return;   // outil de mise au point, hors des passes ordinaires

        Directory.CreateDirectory(Sortie);
        PolicesDesEcrans.Dossier = DossierDesPolices();
        const string fanart = @"E:\RetroBat\plugins\APIExpose\media\systems\nes\games\super_mario_bros\artwork\fanart.jpg";
        var style = EsMenuStyle.Lire(null);
        var socle = new L.ContenuMultiplayer(["MASS RUN"], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true);
        var service = new DefisPanelService(null!, null!, null!, null!, null!, textes: new RetroBat.Api.Infrastructure.InterfaceTextService()) { LangueDApercu = "fr" };

        void Page(string nom, L.ContenuMultiplayer contenu, double temps)
            => File.WriteAllBytes(Path.Combine(Sortie, nom), L.ApercuMultiplayer(style, contenu, "", "", "", null!, temps));

        Page("massrun-depart.png", socle with { Lancement = service.ComposerLeDepartMassRun(Salle(), "Super Mario Bros.", fanart, false, 12, Maintenant) }, 2);
        Page("massrun-bilan-arrive.png", socle with
        {
            Fin = service.ComposerLeBilanDeCourse(new BilanDeCourse(Salle(), "Super Mario Bros.", "NELFE", fanart, IssueDeCourse.Arrive, 2460, 3, 12, 6, Classement, false)),
        }, 6);
        Page("massrun-bilan-gagnant.png", socle with
        {
            Fin = service.ComposerLeBilanDeCourse(new BilanDeCourse(Salle(), "Super Mario Bros.", "TAILS", fanart, IssueDeCourse.Arrive, 2390, 1, 12, 12,
                [Classement[0] with { Moi = true }, .. Classement[1..].Select(l => l with { Moi = false })], true)),
        }, 6);
        Page("massrun-bilan-elimine.png", socle with
        {
            Fin = service.ComposerLeBilanDeCourse(new BilanDeCourse(Salle("elimination"), "Super Mario Bros.", "NELFE", fanart, IssueDeCourse.Elimine, 1800, 9, 12, 4,
                [new(1, false, false, 2390, 0, "TAILS"), new(8, false, true, 2100, 0, "AMY"), new(9, true, true, 1800, 0, "NELFE")], true)),
        }, 6);
    }

    private static string DossierDesPolices()
    {
        for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier is not null; dossier = dossier.Parent)
        {
            var candidat = Path.Combine(dossier.FullName, "resources", "theme", "fonts");
            if (Directory.Exists(candidat)) return candidat;
        }

        // le dossier de travail n'a pas les polices (Data Pack) : celles de l'API installee
        const string installees = @"E:/RetroBat/plugins/APIExpose/resources/theme/fonts";
        if (Directory.Exists(installees)) return installees;
        throw new DirectoryNotFoundException("resources/theme/fonts introuvable");
    }
}
