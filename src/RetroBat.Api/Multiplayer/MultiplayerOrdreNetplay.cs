namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LES JOUEURS DANS L'ORDRE DU NETPLAY (29/10, Bomberman PSP) : un jeu a depart par place (<see cref="MultiplayerGame.CoreStart"/>)
/// numerote ses joueurs dans l'ordre de leur connexion a RetroArch, l'hote d'abord (le coeur PPSSPP tire de ce numero
/// l'adresse de chaque PSP). Le joueur 1 du jeu, ses etats et ses compteurs, est donc l'hote, alors que la plateforme
/// elit l'hote sur sa latence et donne les places dans l'ordre d'arrivee : l'hote peut avoir la place 2. Pour une partie a
/// deux, le catalogue (ecrit dans l'ordre du jeu) est remis dans l'ordre des places, une fois, en entrant dans la partie :
/// la fin, les statistiques et le resultat parlent alors des places de la plateforme.
/// </summary>
public static class MultiplayerOrdreNetplay
{
    /// <summary>Le jeu de cette partie, ses places remises dans l'ordre de la plateforme ; le meme quand rien ne change.</summary>
    public static MultiplayerInstalledGame PourLaRoom(MultiplayerInstalledGame jeu, MultiplayerRoom room)
    {
        if (jeu.Game.CoreStart is null || Permutation(room) is not { } permutation)
        {
            return jeu;
        }

        return jeu with { Game = Permuter(jeu.Game, permutation) };
    }

    /// <summary>
    /// Place du jeu vers place de la plateforme : le joueur 1 du jeu est la place de l'hote, le joueur 2 l'autre place. Null
    /// quand l'hote a deja la place 1, ou hors d'une partie a deux.
    /// </summary>
    public static IReadOnlyDictionary<int, int>? Permutation(MultiplayerRoom room)
    {
        var places = room.Players.Where(j => j.Seat is not null).ToList();
        var hote = places.FirstOrDefault(j => j.Host)?.Seat;
        if (places.Count != 2 || hote is not { } placeDeLHote || placeDeLHote == 1)
        {
            return null;
        }

        var autre = places.First(j => !j.Host).Seat!.Value;
        return new Dictionary<int, int> { [1] = placeDeLHote, [2] = autre };
    }

    internal static MultiplayerGame Permuter(MultiplayerGame jeu, IReadOnlyDictionary<int, int> permutation)
    {
        int Place(int p) => permutation.TryGetValue(p, out var nouvelle) ? nouvelle : p;
        return jeu with
        {
            End = jeu.End is { } fin ? fin with { Seats = fin.Seats.Select(s => s with { Seat = Place(s.Seat) }).ToList() } : null,
            MatchEnd = jeu.MatchEnd is { } score ? score with { Seats = score.Seats.Select(s => s with { Seat = Place(s.Seat) }).ToList() } : null,
            RankingEnd = jeu.RankingEnd is { } classement
                ? classement with { Seats = classement.Seats.Select(s => s with { Seat = Place(s.Seat) }).ToList() }
                : null,
            CoreStart = jeu.CoreStart is { } depart ? depart with { Seats = depart.Seats.Select(s => s with { Seat = Place(s.Seat) }).ToList() } : null,
            PlacesDuNetplay = permutation,
        };
    }

    /// <summary>Le profil de statistiques du jeu remis dans l'ordre des places, comme sa fin.</summary>
    public static MultiplayerStatsProfil Permuter(MultiplayerStatsProfil profil, IReadOnlyDictionary<int, int>? permutation)
    {
        if (permutation is null)
        {
            return profil;
        }

        string Place(string p) => int.TryParse(p, out var n) && permutation.TryGetValue(n, out var nouvelle)
            ? nouvelle.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : p;
        return profil with
        {
            Signals = profil.Signals.ToDictionary(e => e.Key, e => e.Value with { Seats = e.Value.Seats.ToDictionary(s => Place(s.Key), s => s.Value) }),
        };
    }
}
