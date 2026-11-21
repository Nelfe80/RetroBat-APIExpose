using System.Text.Json;

namespace RetroBat.Api.Scoring;

/// <summary>
/// L'AVERTISSEMENT D'UNE OPTION HORS REGLEMENT NE PART QUE SUR UNE VALEUR GARDEE (21/11, Tetris Game Boy type B 9-5).
/// Dans le menu, le joueur fait defiler le niveau (5, 6, 7, 8, 9) puis la hauteur (3, 4, 5) : chaque valeur traversee est
/// une ligne GAME_DIFFICULTY, et la premiere hors « accept » levait le bandeau « score non classe » alors que le joueur
/// n'avait pas fini de choisir (deux joueurs ont cru ne pas pouvoir regler 9-5 et ont abandonne). Ici, une valeur hors
/// reglement est seulement NOTEE avec l'heure ; elle ne devient un avertissement que si elle est TOUJOURS la valeur du
/// champ apres <see cref="Delai"/> sans changement de ce champ. Une valeur admise efface la note et REARME le champ : un
/// joueur qui se trompe, corrige, puis se retrompe est prevenu a nouveau. Un champ averti ne l'est pas deux fois tant
/// qu'il n'est pas revenu a une valeur admise (le cas de Streets of Rage 2, vies a 5 dans OPTIONS, reste couvert : la
/// valeur est gardee). Pur : l'heure vient de l'appelant. Non thread-safe : l'appelant verrouille.
/// </summary>
public sealed class AvertisseurReglages
{
    /// <summary>Le temps sans changement du champ au bout duquel une valeur hors reglement est jugee choisie.</summary>
    public static readonly TimeSpan Delai = TimeSpan.FromSeconds(3.5);

    private sealed record Note(int Valeur, string Nom, DateTime Depuis);

    private readonly Dictionary<string, Note> _enAttente = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _avertis = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Un champ averti dont la valeur est revenue a une valeur admise (le joueur est rentre dans le reglement).</summary>
    public sealed record Retour(string Adresse, string Nom);

    /// <summary>
    /// Une valeur lue pour un champ de difficulte. Rend vrai quand elle est hors reglement (une verification est a
    /// programmer apres <see cref="Delai"/>). <paramref name="redevenuConforme"/> : le champ etait averti et revient dans le reglement.
    /// </summary>
    public bool Observer(IEnumerable<JsonElement> profilsSolo, string adresse, int valeur, string langue, DateTime maintenant, out Retour? redevenuConforme)
    {
        redevenuConforme = null;
        var cle = ModesDeJeu.Adresse(adresse);
        if (!ReglagesDuMenu.HorsReglement(profilsSolo, adresse, valeur, langue, out var nom))
        {
            _enAttente.Remove(cle);
            if (_avertis.Remove(cle)) redevenuConforme = new Retour(cle, Nom(profilsSolo, adresse, langue));
            return false;
        }
        // Une valeur differente du meme champ repart a zero ; la meme valeur relue ne prolonge pas l'attente.
        if (_avertis.Contains(cle)) return false;
        if (!_enAttente.TryGetValue(cle, out var n) || n.Valeur != valeur) _enAttente[cle] = new Note(valeur, nom, maintenant);
        return true;
    }

    /// <summary>
    /// Les champs dont la valeur hors reglement n'a pas bouge depuis <see cref="Delai"/> : a avertir (une fois). Rend les
    /// mots des champs (vides s'ils n'en ont pas).
    /// </summary>
    public IReadOnlyList<string> Echus(DateTime maintenant)
    {
        var sortie = new List<string>();
        foreach (var (cle, n) in _enAttente.ToArray())
        {
            if (maintenant - n.Depuis < Delai) continue;
            _enAttente.Remove(cle);
            _avertis.Add(cle);
            sortie.Add(n.Nom);
        }
        return sortie;
    }

    private static string Nom(IEnumerable<JsonElement> profils, string adresse, string langue)
    {
        // Le mot du champ : HorsReglement le donne pour une valeur refusee ; ici une valeur admise, on cherche une valeur absurde.
        ReglagesDuMenu.HorsReglement(profils, adresse, int.MinValue, langue, out var nom);
        return nom;
    }
}
