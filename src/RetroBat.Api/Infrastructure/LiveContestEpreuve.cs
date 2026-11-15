using RetroBat.Api.Media;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UN CONCOURS SUR UNE EPREUVE (LiveContest, 15/11). Le concours nomme une epreuve de NelfePlay (speedrun ou challenge) :
/// <c>/play/contest</c> porte <c>certified: { challenge, game, system, ruleset }</c>, ou <c>challenge</c> est l'identifiant de
/// l'epreuve (« mega-man-2.air-man ») et <c>game</c>, <c>system</c>, <c>ruleset</c> sont ceux de l'epreuve tels que
/// <c>challenges/open</c> les donne (<c>rom_group</c>, <c>system_id</c>, <c>ruleset</c>). Sans <c>challenge</c> (null ou absent),
/// c'est un concours sur score (<see cref="LiveContestCertifie"/>).
///
/// L'epreuve doit etre ouverte dans l'index des defis de la borne (<see cref="CatalogueDesDefis"/>), dans la version que vise le
/// concours ; un entrainement (kind « training ») n'est jamais classe et se refuse ; le fichier est celui que World Scoring a
/// retenu pour les defis du jeu (<see cref="RomDeLEpreuve"/>), comme au panneau. Elle se lance par le lancement type d'un defi
/// (<see cref="SpeedrunSessionService"/>) : cœur du reglement, verrous, etat de depart. Ce qui manque se refuse, rien ne part.
/// </summary>
public static class LiveContestEpreuve
{
    // Les codes de refus propres a une epreuve : la phase que le streamer voit pour ce joueur. Les refus communs (compte non lie,
    // concours incomplet, ROM absente) gardent les codes de LiveContestCertifie.
    public const string RefusEpreuveFermee = "certified-challenge-not-open";
    public const string RefusEntrainement = "certified-challenge-training";
    public const string RefusAutreJeu = "certified-challenge-mismatch";
    public const string RefusPasPrete = "certified-challenge-not-ready";
    public const string RefusJeuEnCours = "certified-game-running";

    /// <summary>
    /// L'epreuve prete a partir (<see cref="Reglement"/> et <see cref="Rom"/> non nuls), ou le refus : un code stable
    /// (<see cref="Refus"/>) et ce qu'on en dit.
    /// </summary>
    public sealed record Preparation(Reglement? Reglement, string? Rom, string Epreuve, string Systeme, string Jeu, string Regle,
        string? Refus, string? Detail)
    {
        public bool Prete => Reglement is not null && Rom is not null && Refus is null;
    }

    /// <summary>Le refus d'une epreuve avant toute resolution (compte, concours sans identifiant...).</summary>
    public static Preparation Refuser(LiveContestCertifie.Demande demande, string code, string detail)
        => new(null, null, demande.Epreuve, demande.Systeme, demande.Jeu, demande.Regle, code, detail);

    /// <summary>
    /// L'epreuve du concours sur cette borne. Pur.
    /// </summary>
    /// <param name="ouvertes">Les epreuves de l'index des defis garde par la borne.</param>
    /// <param name="trouverLaRom">Le fichier de l'epreuve (celui que World Scoring a retenu pour les defis), ou null.</param>
    public static Preparation Resoudre(LiveContestCertifie.Demande demande, IReadOnlyCollection<Reglement> ouvertes,
        Func<Reglement, string?> trouverLaRom)
    {
        if (demande.Epreuve.Length == 0)
        {
            return Refuser(demande, LiveContestCertifie.RefusIncomplet, "concours sur epreuve sans identifiant d'epreuve");
        }

        // Par son identifiant d'abord ; LiveContest accepte aussi un ruleset a la creation.
        var reglement = ouvertes.FirstOrDefault(r => string.Equals(r.ChallengeId, demande.Epreuve, StringComparison.OrdinalIgnoreCase))
            ?? ouvertes.FirstOrDefault(r => string.Equals(r.Ruleset, demande.Epreuve, StringComparison.OrdinalIgnoreCase));
        if (reglement is null)
        {
            return Refuser(demande, RefusEpreuveFermee, ouvertes.Count == 0
                ? "aucune epreuve ouverte sur cette borne (index des defis pas encore recu)"
                : $"« {demande.Epreuve} » n'est pas ouverte dans l'index des defis de cette borne");
        }

        Preparation Refus(string code, string detail)
            => new(null, null, reglement.ChallengeId, reglement.SystemId, reglement.RomGroup, reglement.Ruleset, code, detail);

        // La version : le classement du concours est celui de SON ruleset. Une autre version ouverte ici n'y entrerait pas.
        if (demande.Regle.Length > 0 && !string.Equals(demande.Regle, reglement.Ruleset, StringComparison.OrdinalIgnoreCase))
        {
            return Refus(RefusEpreuveFermee,
                $"le concours vise « {demande.Regle} », la version ouverte sur cette borne est « {reglement.Ruleset} »");
        }
        if (reglement.EstUnEntrainement)
        {
            return Refus(RefusEntrainement, $"« {reglement.ChallengeId} » est un entrainement : il n'est jamais classe");
        }
        if (demande.Jeu.Length > 0 && !string.Equals(demande.Jeu, reglement.RomGroup, StringComparison.OrdinalIgnoreCase))
        {
            return Refus(RefusAutreJeu, $"le concours nomme « {demande.Jeu} », l'epreuve est sur « {reglement.RomGroup} »");
        }
        if (demande.Systeme.Length > 0
            && !string.Equals(RomCanonicalResolver.CanonicalScoringSystem(demande.Systeme),
                RomCanonicalResolver.CanonicalScoringSystem(reglement.SystemId), StringComparison.Ordinal))
        {
            return Refus(RefusAutreJeu, $"le concours nomme la machine « {demande.Systeme} », l'epreuve est sur « {reglement.SystemId} »");
        }

        var rom = trouverLaRom(reglement);
        if (rom is null)
        {
            return Refus(LiveContestCertifie.RefusRomAbsente,
                $"« {reglement.RomGroup} » : aucune ROM admise pour ses epreuves dans la collection World Scoring de cette borne");
        }

        return new Preparation(reglement, rom, reglement.ChallengeId, reglement.SystemId, reglement.RomGroup, reglement.Ruleset, null, null);
    }

    /// <summary>La phase d'un refus du lancement type d'un defi (<see cref="ReponseLancement.Raison"/>). Pur.</summary>
    public static string PhaseDuLancement(string? raison) => raison switch
    {
        "game_running" => RefusJeuEnCours,
        "not_installed" => LiveContestCertifie.RefusRomAbsente,
        "unknown_challenge" => RefusEpreuveFermee,
        _ => RefusPasPrete,
    };
}
