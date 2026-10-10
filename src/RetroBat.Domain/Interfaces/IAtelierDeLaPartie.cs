namespace RetroBat.Domain.Interfaces;

/// <summary>
/// L'atelier de NelfeScoreLab tient-il sur la partie en cours (APX-LAB-001) ? Une partie sous l'atelier peut partir
/// d'un etat fabrique (un niveau choisi, des vies imposees) : son score n'est pas celui d'un joueur. Rien n'en sort,
/// pas meme une trace locale : ni la table des meilleurs scores de la console, ni la liste des jeux recents.
/// Vu une fois, l'atelier couvre la partie jusqu'au lancement suivant.
/// </summary>
public interface IAtelierDeLaPartie
{
    bool SousAtelier { get; }
}
