namespace RetroBat.Api.Speedrun;

/// <summary>
/// LA ROM D'UNE EPREUVE LANCEE PAR LA ROUTE (12/11, essai reel de la nuit). La route <c>POST /api/v1/speedrun/launch</c> prenait
/// le premier fichier du gamelist dont l'identite de scoring est le groupe (<see cref="Infrastructure.RomDuGroupe"/>) : sur la
/// borne, « Super Mario Bros (E).nes » (PAL) avant « Super Mario Bros. (World).zip ». Le coeur hote des fantomes refuse ce
/// dump (crc 393a432f), et les etats de depart des epreuves sont faits sur la version de reference : l'epreuve partait sans
/// fantome, sur un autre jeu que celui des classements. Le panneau, lui, lance le fichier que World Scoring a retenu
/// (collection des defis), qui pour un jeu a speedruns est SA ROM DE REFERENCE (<c>RomAdmission</c>).
///
/// La route prend maintenant le meme fichier : celui de World Scoring pour ce « systeme/jeu ». Un jeu que World Scoring a
/// ecarte des defis (aucune ROM de reference installee) n'a pas d'epreuve, comme au panneau. Avant la premiere passe de World
/// Scoring, seul un jeu sans ROM de reference de speedrun (les challenges d'arcade) garde l'ancien repli. Pur.
/// </summary>
public static class RomDeLEpreuve
{
    /// <param name="cheminsParJeu">Les fichiers retenus par World Scoring, par « systeme/jeu » ; null avant sa premiere passe.</param>
    /// <param name="aUneReference">Le jeu a-t-il une ROM de reference de speedrun (<c>RomAdmission.ReferenceDuSpeedrun</c>) ?</param>
    /// <param name="repli">L'ancienne recherche par le gamelist.</param>
    /// <param name="existe">Le fichier existe-t-il (disque) ?</param>
    public static string? Choisir(IReadOnlyDictionary<string, string>? cheminsParJeu, string systeme, string groupe, Func<bool> aUneReference,
        Func<string?> repli, Func<string, bool> existe)
    {
        if (cheminsParJeu is not null)
        {
            if (cheminsParJeu.TryGetValue(systeme + "/" + groupe, out var retenu) && existe(retenu)) return retenu;
            // World Scoring a passe : un jeu absent n'a pas de ROM admise pour ses defis. Le repli ne vaut que pour un jeu sans
            // reference de speedrun (challenge d'arcade dont le fichier n'est pas dans la passe pour une autre raison).
            return aUneReference() ? null : repli();
        }

        return aUneReference() ? null : repli();
    }
}
