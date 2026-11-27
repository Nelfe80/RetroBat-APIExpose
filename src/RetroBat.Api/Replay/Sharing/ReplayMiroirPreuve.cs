using RetroBat.Api.Replay.Models;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>Ce que le miroir a repondu a la verification d'un objet.</summary>
public enum VerdictMiroir
{
    /// <summary>Une source du miroir sert l'objet (HEAD reussi).</summary>
    Present,
    /// <summary>Les sources ont repondu, et aucune ne l'a : 404 ou 410 partout.</summary>
    Absent,
    /// <summary>Pas de reponse exploitable (reseau, 5xx, 429) : on ne conclut rien.</summary>
    Inconnu,
}

/// <summary>
/// Un replay n'est dit « mirrored » que sur PREUVE (27/11/2026, decision du user).
///
/// Avant, l'etat etait pose a l'inscription de l'intention de semis : il disait « publie » d'un objet
/// que le site n'avait peut-etre jamais pris, ou qu'il avait retire ensuite de l'amorce (elagage hors
/// du top), et plus rien ne le revoyait. Calculs PURS ici : horloge, budgets et reseau restent a
/// l'appelant (<see cref="ReplaySeedService"/>).
///
/// Etats de <c>PublicationState</c> : « local » (rien), « queued » (intention inscrite, rien prouve),
/// « mirrored » (HEAD reussi, <c>MirroredAt</c> et <c>MirrorCheckedAt</c> poses), « to_resend » (l'objet n'est
/// plus servi et le site le veut encore : renvoi en file), « off_mirror » (plus servi, et le site n'en veut plus
/// ou le plafond de renvois est atteint). Un ancien « mirrored » sans <c>MirrorCheckedAt</c> reste lisible : il est
/// seulement « non verifie » et passe en tete de la revision.
/// </summary>
public static class ReplayMiroirPreuve
{
    public const string EtatLocal = "local";
    public const string EtatEnFile = "queued";
    public const string EtatMiroir = "mirrored";
    public const string EtatARenvoyer = "to_resend";
    public const string EtatHorsMiroir = "off_mirror";

    /// <summary>Une revision par replay et par jour au plus.</summary>
    public static readonly TimeSpan IntervalleParReplay = TimeSpan.FromHours(24);
    /// <summary>Replays verifies par passage ; un passage par heure : le premier demarrage n'a pas de rafale.</summary>
    public const int BudgetParPassage = 5;
    /// <summary>
    /// Le site ne garde sur l'amorce que le top 20 de chaque classement, et le top 10 quand le plafond de fichiers
    /// l'impose (ReplaySeedPublisher::rangDuTop). Le plus strict : un rang au-dela n'est pas renvoye.
    /// </summary>
    public const int RangGardeParLeSite = 10;
    /// <summary>Renvois automatiques au plus par replay : le rang estampille a l'enregistrement vieillit, le site elague.</summary>
    public const int RenvoisMax = 3;

    public static bool EstMiroirProuve(ReplayLocalMetadata? meta)
        => Egal(meta?.PublicationState, EtatMiroir) && meta?.MirrorCheckedAt is not null;

    /// <summary>Intention de semis inscrite : « queued », jamais « mirrored ». Un replay deja prouve reste prouve.</summary>
    public static ReplayLocalMetadata Annoncer(ReplayLocalMetadata meta)
        => Egal(meta.PublicationState, EtatMiroir)
            ? meta with { Visibility = "public" }
            : meta with { Visibility = "public", PublicationState = EtatEnFile };

    /// <summary>Le semis a trouve l'objet sur l'amorce : c'est la preuve.</summary>
    public static ReplayLocalMetadata Prouve(ReplayLocalMetadata meta, DateTime maintenant)
        => meta with
        {
            PublicationState = EtatMiroir,
            MirroredAt = Egal(meta.PublicationState, EtatMiroir) && meta.MirroredAt is not null ? meta.MirroredAt : maintenant,
            MirrorCheckedAt = maintenant,
        };

    /// <summary>Un replay est a verifier s'il se dit « mirrored » et n'a pas ete revu depuis un jour (ou jamais).</summary>
    public static bool Due(ReplayLocalMetadata? meta, DateTime maintenant)
        => meta is not null && Egal(meta.PublicationState, EtatMiroir)
           && (meta.MirrorCheckedAt is not { } vu || maintenant - vu >= IntervalleParReplay);

    /// <summary>
    /// Le site veut-il encore cet objet ? La borne n'a pas de liste du site : elle juge sur le rang estampille a
    /// l'enregistrement (carte du replay) contre le top que l'amorce garde. Rang inconnu = on ne renvoie pas (le geste
    /// manuel hors score certifie n'entre pas au top, l'elagage le retirerait). Les demandes du relais ne passent pas par
    /// ici : elles sont servies a part, a chaque demande du site (ReplayRelayService).
    /// </summary>
    public static bool LeSiteLeVeut(ReplayLocalMetadata? meta)
        => meta?.Rank is int rang && rang >= 1 && rang <= RangGardeParLeSite && (meta.ResendCount ?? 0) < RenvoisMax;

    /// <summary>Les replays a revoir ce passage : les moins recemment verifies d'abord, dans la limite du budget.</summary>
    public static IReadOnlyList<(string ReplayId, string Sha256)> ChoisirARevoir(
        IEnumerable<(string ReplayId, string Sha256, ReplayLocalMetadata? Meta)> candidats, DateTime maintenant, int budget = BudgetParPassage)
        => candidats
            .Where(c => Due(c.Meta, maintenant))
            .OrderBy(c => c.Meta!.MirrorCheckedAt ?? DateTime.MinValue)
            .ThenBy(c => c.ReplayId, StringComparer.Ordinal)
            .Take(Math.Max(0, budget))
            .Select(c => (c.ReplayId, c.Sha256))
            .ToList();

    /// <summary>
    /// Suite d'une verification. Present : prouve, horodate. Inconnu : rien ne change (on reverra au prochain jour,
    /// sans toucher a l'horodatage pour ne pas masquer une panne). Absent : renvoi si le site le veut et que la borne
    /// detient l'objet, sinon « off_mirror ».
    /// </summary>
    public static (ReplayLocalMetadata Meta, bool Renvoyer) Appliquer(
        ReplayLocalMetadata meta, VerdictMiroir verdict, bool objetDetenu, DateTime maintenant)
    {
        switch (verdict)
        {
            case VerdictMiroir.Present:
                return (Prouve(meta, maintenant), false);
            case VerdictMiroir.Inconnu:
                return (meta, false);
            default:
                if (objetDetenu && LeSiteLeVeut(meta))
                    return (meta with
                    {
                        PublicationState = EtatARenvoyer,
                        MirrorCheckedAt = maintenant,
                        ResendCount = (meta.ResendCount ?? 0) + 1,
                    }, true);
                return (meta with { PublicationState = EtatHorsMiroir, MirrorCheckedAt = maintenant }, false);
        }
    }

    /// <summary>Verdict d'une serie de reponses HEAD (null = echec reseau ou delai). Un seul succes suffit ; Absent exige que tout le monde ait repondu « introuvable ».</summary>
    public static VerdictMiroir Classer(IEnumerable<int?> statuts)
    {
        var tous = statuts.ToList();
        if (tous.Any(s => s is >= 200 and < 300)) return VerdictMiroir.Present;
        if (tous.Count == 0 || tous.Any(s => s is null or 408 or 429 or >= 500)) return VerdictMiroir.Inconnu;
        return tous.All(s => s is 404 or 410) ? VerdictMiroir.Absent : VerdictMiroir.Inconnu;
    }

    private static bool Egal(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
