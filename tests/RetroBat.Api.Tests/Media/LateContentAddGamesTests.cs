using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Le jeton de RATTRAPAGE d'une fiche consultee.
///
/// Regle du projet : un meme game-selected ne produit qu'un seul /addgames. Une exception
/// existait pour la video fraichement scrapee. Mesure du 2026-09-21 sur zaviga et zaxxon : les
/// medias locaux partaient en une seconde et depensaient l'unique envoi, puis la description
/// anglaise arrivait 11 s plus tard et se voyait refuser l'entree, alors qu'elle etait ecrite
/// sur le disque. La fiche restait muette toute la session.
///
/// Le jeton vaut donc desormais pour la video comme pour la premiere description, et il n'en
/// existe toujours qu'UN : les deux voyagent ensemble quand elles arrivent dans la meme passe
/// (demande du user).
/// </summary>
public class LateContentAddGamesTests
{
    private const string Systeme = "fbneo";
    private const string Chemin = @"E:\RetroBat\roms\fbneo\zaviga.zip";

    private static MediaRuntimeState EtatSurLaFiche()
    {
        var etat = new MediaRuntimeState();
        etat.RecordGameSelectedSelection(Systeme, Chemin);
        return etat;
    }

    [Fact]
    public void Le_premier_envoi_d_une_fiche_passe()
    {
        var etat = EtatSurLaFiche();

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(Systeme, Chemin, out _));
    }

    [Fact]
    public void Un_second_envoi_sans_contenu_tardif_est_refuse()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.True(etat.ShouldSuppressLiveAddGamesForSelection(Systeme, Chemin, out var raison));
        Assert.Equal("already-pushed-current-selection", raison);
    }

    /// <summary>Le cas zaviga : la description arrive apres l'envoi des images.</summary>
    [Fact]
    public void Une_premiere_description_arrivee_apres_obtient_son_rattrapage()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out _,
            allowLateContentException: true));
    }

    [Fact]
    public void Le_rattrapage_ne_sert_qu_une_fois()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);
        // le rattrapage part (texte, video, ou les deux)
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.True(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out var raison,
            allowLateContentException: true));
        Assert.Equal("already-pushed-current-selection-late-content", raison);
    }

    /// <summary>
    /// Le premier envoi ne doit JAMAIS consommer le rattrapage, meme s'il porte deja du texte :
    /// sinon la video du meme voyage serait condamnee.
    /// </summary>
    [Fact]
    public void Le_premier_envoi_ne_consomme_pas_le_rattrapage()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out _,
            allowLateContentException: true));
    }

    [Fact]
    public void Une_autre_fiche_garde_son_propre_jeton()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        const string autre = @"E:\RetroBat\roms\fbneo\zaxxon.zip";
        etat.RecordGameSelectedSelection(Systeme, autre);

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(Systeme, autre, out _));
    }

    [Fact]
    public void Une_fiche_qui_n_est_plus_celle_affichee_ne_recoit_rien()
    {
        var etat = EtatSurLaFiche();
        etat.RecordGameSelectedSelection(Systeme, @"E:\RetroBat\roms\fbneo\zaxxon.zip");

        Assert.True(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out var raison,
            allowLateContentException: true));
        Assert.Equal("not-current-selection", raison);
    }
}

/// <summary>
/// Quand le texte attend la video, et quand il ne l'attend pas.
///
/// Le jeton de rattrapage est unique : depense pour le seul texte, il condamnerait la video qui
/// arrive juste apres dans la meme passe de scrap. Le texte l'attend donc, et les deux partent
/// dans le meme fragment. Precision du user (2026-09-21) : si la video est DEJA sur le disque,
/// il n'y a rien a attendre, le texte prend le rattrapage sans delai.
///
/// Piege verifie dans le code : un type peut etre redemande au scraper alors que le fichier
/// existe (exactLocalMissingKinds redemande un media herite pour obtenir l'exact). La liste des
/// types demandes ne prouve donc rien ; seul le fichier compte.
/// </summary>
public class LateContentVideoWaitTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "late-" + Guid.NewGuid().ToString("N")[..8]);

    public LateContentVideoWaitTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Fichier(string nom)
    {
        var chemin = Path.Combine(_dossier, nom);
        File.WriteAllText(chemin, nom);
        return chemin;
    }

    /// <summary>La regle telle que le provider la calcule, sur le seul critere du fichier.</summary>
    private static bool VideoDejaPresente(MediaProjectionPlan plan)
        => plan.Needs.Any(need =>
            string.Equals(MediaKinds.Normalize(need.Kind), MediaKinds.Video, StringComparison.OrdinalIgnoreCase) &&
            ((!string.IsNullOrWhiteSpace(need.ExistingPath) && File.Exists(need.ExistingPath)) ||
                (!string.IsNullOrWhiteSpace(need.ImportedPath) && File.Exists(need.ImportedPath))));

    private static MediaProjectionPlan Plan(params MediaNeed[] besoins)
        => new() { SystemId = "arcade", FrontendSystemId = "fbneo", GameSlug = "zaviga", Needs = besoins.ToList() };

    [Fact]
    public void Une_video_sur_le_disque_est_vue_comme_presente()
    {
        var plan = Plan(new MediaNeed { Kind = MediaKinds.Video, ExistingPath = Fichier("video.mp4") });

        Assert.True(VideoDejaPresente(plan));
    }

    [Fact]
    public void Une_video_annoncee_mais_absente_du_disque_ne_compte_pas()
    {
        var plan = Plan(new MediaNeed
        {
            Kind = MediaKinds.Video,
            ExistingPath = Path.Combine(_dossier, "jamais-ecrit.mp4")
        });

        Assert.False(VideoDejaPresente(plan));
    }

    [Fact]
    public void Sans_besoin_video_rien_n_est_presume_present()
    {
        Assert.False(VideoDejaPresente(Plan(new MediaNeed { Kind = MediaKinds.Image, ExistingPath = Fichier("image.png") })));
        Assert.False(VideoDejaPresente(Plan()));
    }

    [Fact]
    public void Une_video_tout_juste_importee_compte_aussi()
    {
        var plan = Plan(new MediaNeed { Kind = MediaKinds.Video, ImportedPath = Fichier("importee.mp4") });

        Assert.True(VideoDejaPresente(plan));
    }
}
