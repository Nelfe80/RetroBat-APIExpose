namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Ce dont on dispose au lancement d'une partie pour décider du mode.
/// </summary>
/// <param name="PlayerConsent">
/// L'interrupteur du joueur (<c>ScoringDiscovery:Enabled</c>, reflété dans le menu ES).
/// </param>
/// <param name="DevicePaired">
/// La machine est appairée à un compte. Sans cela, pas de ticket, pas de signature : ce qui
/// serait observé ne pourrait être attesté par personne.
/// </param>
/// <param name="VerifierAvailable">
/// Le binaire du vérificateur répond. Une borne qui n'a pas reçu la mise à jour n'est pas
/// candidate, et personne n'a rien à configurer pour cela.
/// </param>
/// <param name="CaptureComponentAvailable">
/// Le composant de capture existe pour l'émulateur lancé : plugin Lua pour MAME, wrapper
/// avec le crochet vidéo pour RetroArch.
/// </param>
/// <param name="Enrollment">Ce que la plateforme dit de cette borne.</param>
/// <param name="ProfileIsOpen">Le jeu a un profil ouvert et compatible.</param>
/// <param name="FingerprintInvalidated">L'empreinte exacte est au registre d'invalidation.</param>
public readonly record struct ScoringConditions(
    bool PlayerConsent,
    bool DevicePaired,
    bool VerifierAvailable,
    bool CaptureComponentAvailable,
    DiscoveryEnrollment Enrollment,
    bool ProfileIsOpen,
    bool FingerprintInvalidated);

/// <summary>
/// Décide du mode d'une partie, et de lui seul.
///
/// Quatre conditions doivent tenir ensemble pour observer (A1.10), et l'ordre dans lequel on
/// les examine n'est pas indifférent : l'invalidation passe avant tout, parce qu'une empreinte
/// écartée le reste quoi qu'en dise le reste ; le consentement du joueur passe avant les
/// questions techniques, parce qu'une borne dont le joueur a coupé la découverte n'a pas à
/// être sondée pour savoir si elle en serait capable.
///
/// La classe ne fait pas d'entrées-sorties : elle reçoit des faits et rend un mode. C'est ce
/// qui permet de la tester entièrement, et c'est elle qui garde la règle quand le reste du
/// Lot 1 se construira autour.
/// </summary>
public static class ScoringModeResolver
{
    public static ScoringModeDecision Resolve(in ScoringConditions conditions)
    {
        if (conditions.FingerprintInvalidated)
        {
            return new ScoringModeDecision(ScoringMode.Quarantined, "empreinte invalidee");
        }

        // Un profil ouvert rend la découverte sans objet : le jeu est déjà ouvert, il n'y a
        // plus rien à découvrir, et le scoring officiel ne dépend d'aucune des conditions
        // ci-dessous.
        if (conditions.ProfileIsOpen)
        {
            return new ScoringModeDecision(ScoringMode.OfficialScoring, "profil ouvert et compatible");
        }

        if (!conditions.PlayerConsent)
        {
            return new ScoringModeDecision(ScoringMode.Disabled, "decouverte coupee sur cette borne");
        }

        if (!conditions.DevicePaired)
        {
            return new ScoringModeDecision(ScoringMode.Disabled, "machine non appairee");
        }

        if (conditions.Enrollment == DiscoveryEnrollment.Off)
        {
            return new ScoringModeDecision(ScoringMode.Disabled, "borne non enrolee par la plateforme");
        }

        if (!conditions.VerifierAvailable)
        {
            return new ScoringModeDecision(ScoringMode.Disabled, "verificateur absent");
        }

        if (!conditions.CaptureComponentAvailable)
        {
            return new ScoringModeDecision(ScoringMode.Disabled, "capture indisponible pour cet emulateur");
        }

        return new ScoringModeDecision(
            ScoringMode.SilentDiscovery,
            conditions.Enrollment == DiscoveryEnrollment.Shadow ? "decouverte, en observation" : "decouverte");
    }

    /// <summary>
    /// Lit l'enrôlement tel que la route profile le rend. Le champ est additif : une réponse
    /// qui ne le porte pas vient d'une plateforme qui ne le connaît pas encore, et la borne
    /// se met alors en observation plutôt que de s'arrêter ou de se croire autorisée.
    /// </summary>
    public static DiscoveryEnrollment ReadEnrollment(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "on" => DiscoveryEnrollment.On,
            "off" => DiscoveryEnrollment.Off,
            "shadow" => DiscoveryEnrollment.Shadow,
            _ => DiscoveryEnrollment.Shadow
        };
}
