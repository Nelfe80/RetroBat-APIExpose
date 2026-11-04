using System.Runtime.Versioning;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Speedrun;
using Xunit;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES PAGES D'UNE EPREUVE (CDC du panneau, §8) : le mot central, le titre selon le sort du temps, l'ecart au record. Les apercus
/// (PNG) ne se dessinent que si NELFEPLAY_APERCUS donne un dossier de sortie.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class DefisPagesTests
{
    private static readonly string Modele = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json"));

    private static readonly Reglement GreenHill1 = Reglement.Lire(Modele);

    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");

    private static DefisPanelService Service() => new(null!, null!, null!, null!, null!);

    private static BilanDEpreuve Bilan(IssueDuBilan issue, long? temps = 2900, int? rang = null, long? record = null, int? classes = null, string motif = "",
        IReadOnlyList<TempsClasse>? classement = null)
        => new(GreenHill1, "Sonic the Hedgehog", "Nelfe", null, issue, temps, 4, 2, rang, record, classes, motif, classement);

    [Fact]
    public void Le_mot_central_dit_le_genre_de_l_epreuve()
    {
        Assert.Equal("SPEEDRUN", DefisPanelService.MotDe(GreenHill1));
        Assert.Equal("CHALLENGE", DefisPanelService.MotDe(Reglement.Lire(Modele.Replace("speedrun_level", "challenge_rule"))));
    }

    [Fact]
    public void La_preparation_porte_la_fiche_et_passe_a_l_etape_suivante_quand_le_jeu_arrive()
    {
        var page = Service().ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", null, false, "fr");

        Assert.Equal("SPEEDRUN", page.Mot);
        Assert.Equal("Green Hill Zone · Acte 1", page.SousTitre);
        Assert.NotEmpty(page.Fiche!);
        Assert.Equal(1, page.Etape);
        Assert.True(page.SortiePossible);
        Assert.Equal(2, Service().ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", null, true, "fr").Etape);
    }

    [Theory]
    [InlineData(IssueDuBilan.EnVerification, "TIME SENT")]
    [InlineData(IssueDuBilan.Retenu, "TIME ON HOLD")]
    [InlineData(IssueDuBilan.Refuse, "TIME REFUSED")]
    [InlineData(IssueDuBilan.NonEnvoye, "NOT SENT")]
    [InlineData(IssueDuBilan.AucunTemps, "NO TIME")]
    public void Le_titre_dit_le_sort_du_temps(IssueDuBilan issue, string titre)
        => Assert.Equal(titre, Service().ComposerLeBilan(Bilan(issue), "fr").Titre);

    [Fact]
    public void Un_temps_classe_donne_son_rang_et_son_ecart_au_record()
    {
        var fin = Service().ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2900, rang: 3, record: 2845, classes: 12), "fr");

        Assert.Equal("TIME RANKED", fin.Titre);
        Assert.Equal(Chronometrage.Afficher(2900, GreenHill1.Cadence), fin.MonScore!.Valeur);
        Assert.Equal("SPEEDRUN · #3", fin.MonScore.Ruban);
        Assert.StartsWith("+" + Chronometrage.Afficher(55, GreenHill1.Cadence), fin.MonScore.Second);
        Assert.False(fin.MonScore.Premier);
        Assert.Equal([4L, 2L, 12L], fin.Joueurs[0].Mesures.Select(m => m.Valeur!.Value));
    }

    [Fact]
    public void Les_barres_disent_les_tentatives_finies_et_la_place_parmi_les_joueurs()
    {
        var mesures = Service().ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2900, rang: 3, record: 2845, classes: 5), "fr").Joueurs[0].Mesures;

        Assert.Equal(0.5, mesures[1].Part);
        Assert.Equal(0.5, mesures[2].Part);
    }

    [Fact]
    public void Un_record_garde_le_temps_a_l_ecran_et_non_le_nom_du_joueur()
    {
        var fin = Service().ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2830, rang: 1, record: 2830), "fr");

        Assert.DoesNotContain(fin.Joueurs, j => j.Gagnant);
        Assert.Equal(Chronometrage.Afficher(2830, GreenHill1.Cadence), fin.MonScore!.Valeur);
    }

    private static List<TempsClasse> Classement(int nombre) => Enumerable.Range(1, nombre).Select(i => new TempsClasse(i, "J" + i, 2800 + i)).ToList();

    [Theory]
    [InlineData(1, new[] { 1, 2, 3, 4, 5 })]
    [InlineData(3, new[] { 1, 2, 3, 4, 5 })]
    [InlineData(50, new[] { 1, 48, 49, 50, 51 })]
    [InlineData(100, new[] { 1, 97, 98, 99, 100 })]
    public void Le_bilan_montre_le_record_et_les_voisins_du_joueur(int rang, int[] attendu)
        => Assert.Equal(attendu, DefisPanelService.AutourDe(Classement(100), rang).Select(l => l.Rang));

    [Fact]
    public void La_ligne_du_joueur_se_distingue_dans_le_classement_du_bilan()
    {
        var fin = Service().ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2803, rang: 3, record: 2801, classes: 4, classement: Classement(4)), "fr");

        Assert.Equal(["", "", "moi", ""], fin.Classement!.Select(l => l.Marque));
    }

    [Fact]
    public void Un_interdit_commence_par_une_majuscule()
    {
        var page = Service().ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", null, false, "fr");

        Assert.Equal("Dying, pause", page.Fiche![2].Valeur);
    }

    [Fact]
    public void La_premiere_place_est_un_nouveau_record_sans_ecart()
    {
        var fin = Service().ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2845, rang: 1, record: 2845), "fr");

        Assert.Equal("NEW RECORD!", fin.Titre);
        Assert.True(fin.MonScore!.Premier);
        Assert.Equal("", fin.MonScore.Second);
    }

    [Fact]
    public void Sans_temps_le_bilan_montre_un_temps_vide_et_sa_cause()
    {
        var fin = Service().ComposerLeBilan(Bilan(IssueDuBilan.AucunTemps, temps: null, motif: "Vie perdue"), "fr");

        Assert.Equal("--:--.--", fin.MonScore!.Valeur);
        Assert.Equal("Vie perdue", fin.Note);
    }

    [Fact]
    public void Les_pages_se_dessinent()
    {
        if (Sortie is null) return;   // outil de mise au point, hors des passes ordinaires

        Directory.CreateDirectory(Sortie);
        PolicesDesEcrans.Dossier = DossierDesPolices();
        const string fanart = @"E:\RetroBat\plugins\APIExpose\media\systems\megadrive\games\sonic_the_hedgehog\artwork\fanart.jpg";
        var style = EsMenuStyle.Lire(null);
        var socle = new L.ContenuMultiplayer(["SPEEDRUN"], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true);
        var service = new DefisPanelService(null!, null!, null!, null!, null!, textes: new RetroBat.Api.Infrastructure.InterfaceTextService()) { LangueDApercu = "fr" };
        var classement = new[] { new TempsClasse(1, "Tails", 2845), new TempsClasse(2, "Knuckles", 2871), new TempsClasse(3, "Nelfe", 2900), new TempsClasse(4, "Amy", 2954), new TempsClasse(5, "Shadow", 3012) };

        void Page(string nom, L.ContenuMultiplayer contenu, double temps)
            => File.WriteAllBytes(Path.Combine(Sortie, nom), L.ApercuMultiplayer(style, contenu, "", "", "", null!, temps));

        Page("page-preparation.png", socle with { Lancement = service.ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", fanart, false, "fr") }, 2);
        var niveauDeMario = Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "super-mario-bros.1-1.json")));
        var avecFantomes = new DefisPanelService(null!, null!, null!, null!, null!, textes: new RetroBat.Api.Infrastructure.InterfaceTextService(),
            fantomes: new FantomesDuSpeedrun(null!, null!, null!, null!, null!)) { LangueDApercu = "fr" };
        Page("page-preparation-mario.png", socle with { Lancement = avecFantomes.ComposerLaPreparation(niveauDeMario, "Super Mario Bros.", fanart, false, "fr") }, 2);
        Page("page-bilan-classe.png", socle with { Fin = service.ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2900, rang: 3, record: 2845, classes: 5, classement: classement) with { Fanart = fanart }, "fr") }, 6);
        Page("page-bilan-record.png", socle with { Fin = service.ComposerLeBilan(Bilan(IssueDuBilan.Classe, 2830, rang: 1, record: 2830, classes: 5, classement: [new TempsClasse(1, "Nelfe", 2830), .. classement.Where(l => l.Joueur != "Nelfe").Select((l, i) => l with { Rang = i + 2 })]) with { Fanart = fanart }, "fr") }, 6);
        Page("page-bilan-sans-temps.png", socle with { Fin = service.ComposerLeBilan(Bilan(IssueDuBilan.AucunTemps, temps: null, motif: "Vie perdue") with { Fanart = fanart }, "fr") }, 6);
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
