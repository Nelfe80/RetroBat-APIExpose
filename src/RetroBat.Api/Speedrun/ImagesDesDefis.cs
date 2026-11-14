using RetroBat.Api.Media;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// L'IMAGE DU BANDEAU D'UN CHALLENGE dans le panneau des defis. Ordre : l'image propre a l'epreuve (un .png ou .jpg pose a
/// cote de son etat de depart, meme nom que l'etat), puis la capture du jeu dans son gamelist (« image », puis « fanart »),
/// puis rien (le panneau garde son aplat). Les chemins se resolvent UNE FOIS, a l'ouverture du panneau, jamais a l'appui
/// d'une touche.
/// </summary>
public static class ImagesDesDefis
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    /// <summary>L'image posee a cote de l'etat de depart du reglement ; null sans etat ou sans image.</summary>
    public static string? DeLEpreuve(Reglement defi, string? racineDesEtats = null)
    {
        if (defi.Etat is not { } etat || EtatsDesDefis.Chemin(etat, racineDesEtats) is not { } chemin) return null;
        var sansExtension = Path.ChangeExtension(chemin, null);
        foreach (var extension in Extensions)
        {
            var candidat = sansExtension + extension;
            if (File.Exists(candidat)) return candidat;
        }

        return null;
    }

    /// <summary>La capture du jeu dans son gamelist, sinon son fanart ; null si le gamelist n'en donne aucune.</summary>
    public static string? DuJeu(string cheminDuJeu) => MediaDuGamelist.Resoudre(cheminDuJeu, "image", "fanart");

    /// <summary>
    /// Le chemin d'image de chaque challenge (identifiant vers fichier). Un speedrun n'a pas de bandeau et n'est pas traite.
    /// L'image du jeu n'est cherchee qu'une fois, et seulement si une epreuve n'a pas la sienne.
    /// </summary>
    public static Dictionary<string, string> Pour(IEnumerable<Reglement> defis, string cheminDuJeu,
        string? racineDesEtats = null, Func<string, string?>? imageDuJeu = null)
    {
        var resultat = new Dictionary<string, string>(StringComparer.Ordinal);
        string? duJeu = null;
        var duJeuCherchee = false;
        foreach (var defi in defis)
        {
            // l'entrainement (14/11) est une liste (onglet TRAINING), sans bandeau
            if (defi.Kind.StartsWith("speedrun_", StringComparison.Ordinal) || defi.EstUnEntrainement) continue;
            var image = DeLEpreuve(defi, racineDesEtats);
            if (image is null)
            {
                if (!duJeuCherchee)
                {
                    duJeuCherchee = true;
                    try { duJeu = (imageDuJeu ?? DuJeu)(cheminDuJeu); }
                    catch (Exception) { duJeu = null; }
                }

                image = duJeu;
            }

            if (image is not null) resultat[defi.ChallengeId] = image;
        }

        return resultat;
    }
}
