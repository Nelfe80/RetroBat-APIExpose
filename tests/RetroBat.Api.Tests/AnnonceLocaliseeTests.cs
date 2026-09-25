using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'annonce au lancement s'affiche dans la langue du joueur (2026-09-26). En francais, elle doit
/// redonner MOT POUR MOT le texte du journal : l'outil de diagnostic le lit pour expliquer une partie.
/// </summary>
public class AnnonceLocaliseeTests
{
    [Fact]
    public void En_francais_rien_ne_change_pour_le_journal()
    {
        Assert.Equal(("Partie certifiable", "pour le classement"),
            NelfePlayScoringReporter.AnnonceLocalisee("fr", true, "", [], force: false));
        Assert.Equal(("Partie certifiable", "réglages certifiés appliqués"),
            NelfePlayScoringReporter.AnnonceLocalisee("fr", true, "", [], force: true));
        Assert.Equal(("Partie non certifiable", "rembobinage (Rewind), à désactiver dans les options RetroBat de ce jeu"),
            NelfePlayScoringReporter.AnnonceLocalisee("fr", true, "", ["rewind"], force: false));
        Assert.Equal(("Émulateur pas encore reconnu", "ton score sera gardé et classé dès qu'il le sera"),
            NelfePlayScoringReporter.AnnonceLocalisee("fr", false, "profile.core_mismatch", [], force: false));
    }

    [Fact]
    public void Le_motif_d_un_refus_se_traduit()
    {
        Assert.Equal(("Run not certifiable", "memory definition not recognized"),
            NelfePlayScoringReporter.AnnonceLocalisee("en", false, "profile.mem_mismatch", [], force: false));
        Assert.Equal("認定対象外のプレイ",
            NelfePlayScoringReporter.AnnonceLocalisee("ja", false, "profile.content_mismatch", [], force: false).Titre);
    }

    [Fact]
    public void Un_motif_sans_traduction_garde_le_texte_francais_plutot_que_rien()
    {
        var (_, detail) = NelfePlayScoringReporter.AnnonceLocalisee("es", false, "timing.incoherent", [], force: false);

        Assert.Equal("horodatage incohérent", detail);
    }

    [Fact]
    public void Plusieurs_options_dangereuses_se_listent_dans_la_langue()
    {
        var (_, detail) = NelfePlayScoringReporter.AnnonceLocalisee("en", true, "", ["rewind", "runahead"], force: false);

        Assert.Equal("rewind, run-ahead: turn it off in this game's RetroBat options", detail);
    }
}
