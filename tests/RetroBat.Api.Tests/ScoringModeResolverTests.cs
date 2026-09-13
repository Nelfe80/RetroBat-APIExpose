using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les quatre conditions de candidature d'une borne (A1.10), et l'ordre dans lequel elles
/// se posent.
/// </summary>
public class ScoringModeResolverTests
{
    /// <summary>Une borne à jour, appairée, consentante et enrôlée, sur un jeu sans profil.</summary>
    private static ScoringConditions Candidate() => new(
        PlayerConsent: true,
        DevicePaired: true,
        VerifierAvailable: true,
        CaptureComponentAvailable: true,
        Enrollment: DiscoveryEnrollment.Shadow,
        ProfileIsOpen: false,
        FingerprintInvalidated: false);

    [Fact]
    public void Les_quatre_conditions_reunies_donnent_la_decouverte_silencieuse()
    {
        var decision = ScoringModeResolver.Resolve(Candidate());

        Assert.Equal(ScoringMode.SilentDiscovery, decision.Mode);
        Assert.True(decision.CapturesFrames);
        Assert.Contains("observation", decision.Reason);
    }

    [Fact]
    public void Une_borne_enrolee_pour_de_bon_le_dit_dans_sa_raison()
    {
        var decision = ScoringModeResolver.Resolve(Candidate() with { Enrollment = DiscoveryEnrollment.On });

        Assert.Equal(ScoringMode.SilentDiscovery, decision.Mode);
        Assert.DoesNotContain("observation", decision.Reason);
    }

    [Fact]
    public void Le_joueur_qui_coupe_la_decouverte_coupe_tout()
    {
        var decision = ScoringModeResolver.Resolve(Candidate() with { PlayerConsent = false });

        Assert.Equal(ScoringMode.Disabled, decision.Mode);
        Assert.False(decision.CapturesFrames);
        Assert.Contains("coupee", decision.Reason);
    }

    [Fact]
    public void Une_machine_non_appairee_n_observe_pas()
    {
        // Sans compte, pas de ticket ni de signature : ce qui serait observé ne pourrait
        // etre atteste par personne.
        var decision = ScoringModeResolver.Resolve(Candidate() with { DevicePaired = false });

        Assert.Equal(ScoringMode.Disabled, decision.Mode);
        Assert.Contains("appairee", decision.Reason);
    }

    [Fact]
    public void Une_borne_que_la_plateforme_n_a_pas_enrolee_n_observe_pas()
    {
        var decision = ScoringModeResolver.Resolve(Candidate() with { Enrollment = DiscoveryEnrollment.Off });

        Assert.Equal(ScoringMode.Disabled, decision.Mode);
        Assert.Contains("enrolee", decision.Reason);
    }

    [Fact]
    public void Sans_verificateur_rien_ne_s_arme_et_personne_n_a_rien_a_regler()
    {
        var decision = ScoringModeResolver.Resolve(Candidate() with { VerifierAvailable = false });

        Assert.Equal(ScoringMode.Disabled, decision.Mode);
        Assert.Contains("verificateur", decision.Reason);
    }

    [Fact]
    public void Sans_composant_de_capture_pour_cet_emulateur_non_plus()
    {
        var decision = ScoringModeResolver.Resolve(Candidate() with { CaptureComponentAvailable = false });

        Assert.Equal(ScoringMode.Disabled, decision.Mode);
        Assert.Contains("capture", decision.Reason);
    }

    [Fact]
    public void Un_profil_ouvert_rend_la_decouverte_sans_objet()
    {
        var decision = ScoringModeResolver.Resolve(Candidate() with { ProfileIsOpen = true });

        Assert.Equal(ScoringMode.OfficialScoring, decision.Mode);
        Assert.False(decision.CapturesFrames);
    }

    [Fact]
    public void Le_scoring_officiel_ne_depend_pas_des_conditions_de_la_decouverte()
    {
        // Une borne sans vérificateur, non enrôlée, découverte coupée, joue quand même
        // normalement un jeu déjà ouvert.
        var decision = ScoringModeResolver.Resolve(Candidate() with
        {
            ProfileIsOpen = true,
            PlayerConsent = false,
            VerifierAvailable = false,
            CaptureComponentAvailable = false,
            Enrollment = DiscoveryEnrollment.Off
        });

        Assert.Equal(ScoringMode.OfficialScoring, decision.Mode);
    }

    [Fact]
    public void Une_empreinte_invalidee_passe_avant_tout_le_reste()
    {
        // Y compris avant un profil ouvert : ce qui a été écarté le reste.
        var decision = ScoringModeResolver.Resolve(Candidate() with
        {
            FingerprintInvalidated = true,
            ProfileIsOpen = true
        });

        Assert.Equal(ScoringMode.Quarantined, decision.Mode);
        Assert.False(decision.CapturesFrames);
    }

    [Fact]
    public void Le_consentement_se_lit_avant_les_questions_techniques()
    {
        // Une borne dont le joueur a coupé la découverte n'a pas à être sondée pour savoir
        // si elle en serait capable : la raison doit nommer le joueur, pas le matériel.
        var decision = ScoringModeResolver.Resolve(Candidate() with
        {
            PlayerConsent = false,
            VerifierAvailable = false,
            CaptureComponentAvailable = false
        });

        Assert.Contains("coupee", decision.Reason);
    }

    [Theory]
    [InlineData("on", DiscoveryEnrollment.On)]
    [InlineData("off", DiscoveryEnrollment.Off)]
    [InlineData("shadow", DiscoveryEnrollment.Shadow)]
    [InlineData("ON", DiscoveryEnrollment.On)]
    [InlineData(" off ", DiscoveryEnrollment.Off)]
    public void L_enrolement_se_lit_tel_que_la_plateforme_le_dit(string value, DiscoveryEnrollment expected)
    {
        Assert.Equal(expected, ScoringModeResolver.ReadEnrollment(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("oui")]
    public void Une_reponse_sans_enrolement_met_la_borne_en_observation(string? value)
    {
        // Le champ est additif : une plateforme qui ne le connaît pas encore ne doit ni
        // arrêter la borne, ni la laisser se croire autorisée à ouvrir des jeux.
        Assert.Equal(DiscoveryEnrollment.Shadow, ScoringModeResolver.ReadEnrollment(value));
    }
}
