using RetroBat.Api.Multiplayer;
using RetroBat.Api.VocalRoom;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// Y a-t-il une session ouverte (salle MULTIPLAYER ou Adventure, vocal) qui interdit le rendu de station ? Lue a chaque passage
/// du planificateur, sans HTTP : les tests la remplacent par un faux (28/11).
/// </summary>
public interface ISessionsOuvertes
{
    /// <summary>La raison d'occupation (« salle MULTIPLAYER active (waiting) »...), ou null si aucune session.</summary>
    string? Occupation();
}

/// <summary>
/// Lit directement les services. « Occupe » se juge comme dans le ScoreLab (<c>CabinetSession</c>) : salle waiting, countdown,
/// launching ou playing ou la borne est assise (you) ou dont le lancement la concerne ; vocal en starting ou running, ou appel en cours.
/// </summary>
public sealed class SessionsOuvertes : ISessionsOuvertes
{
    private static readonly string[] EtatsDeSalle = { "waiting", "countdown", "launching", "playing" };
    private static readonly string[] EtatsDuVocal = { VocalRoomEtat.Demarrage, VocalRoomEtat.EnMarche };

    private readonly MultiplayerSessionService _multiplayer;
    private readonly VocalRoomProcessService _vocal;

    public SessionsOuvertes(MultiplayerSessionService multiplayer, VocalRoomProcessService vocal)
    {
        _multiplayer = multiplayer; _vocal = vocal;
    }

    public string? Occupation()
    {
        try { return Raison(_multiplayer.Etat, _vocal.Etat); }
        catch (Exception) { return null; }
    }

    /// <summary>Pur, pour etre teste.</summary>
    internal static string? Raison(MultiplayerEtat multiplayer, VocalRoomEtat vocal)
    {
        foreach (var salle in multiplayer.Rooms)
        {
            if (!EtatsDeSalle.Contains(salle.State, StringComparer.OrdinalIgnoreCase)) continue;
            if (salle.Launch is null && !salle.Players.Any(j => j.You)) continue;
            var genre = salle.Adventure is not null ? "aventure Adventure" : "salle MULTIPLAYER";
            return $"{genre} active ({salle.State})";
        }
        if (EtatsDuVocal.Contains(vocal.Processus, StringComparer.OrdinalIgnoreCase) || vocal.Connecte)
            return $"vocal actif ({vocal.Processus}{(vocal.Connecte ? ", appel en cours" : string.Empty)})";
        return null;
    }
}
