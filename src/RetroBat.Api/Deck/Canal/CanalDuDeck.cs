using System.Text;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// L'ALLUMAGE DU CANAL (decisions du user du 22/11, CDC Canal v0.1) : le canal du wrapper ne s'ouvre que lorsqu'un Deck est lance,
/// pour un jeu qui a un profil d'affichage. L'API le demande en posant « canal=on » dans <c>certified.txt</c> (le wrapper 0.348 lit
/// cette cle au chargement du jeu, a cote de sa ROM). Jamais en netplay (le wrapper se coupe de lui-meme) ni au rendu d'un film
/// (<c>pipe=off</c> : le fichier de lecture ne porte pas la cle). Un wrapper plus ancien ignore la cle : rien ne change.
///
/// Meme principe que <see cref="RetroBat.Api.Speedrun.VerrousDuLancement"/> : un etat en memoire, une validite, une ecriture dans
/// le fichier a chaque reecriture. AFFICHAGE SEULEMENT : la cle ne change ni un reglage, ni un verrou, ni une empreinte du passeport.
/// </summary>
public static class CanalDuDeck
{
    /// <summary>La cle du fichier.</summary>
    public const string Ligne = "canal=on";

    /// <summary>Le Deck se lance quelques secondes avant le jeu ; au-dela de ce delai la demande est perimee.</summary>
    public static readonly TimeSpan Validite = TimeSpan.FromMinutes(10);

    private static readonly object Verrou = new();
    private static string? _rom;
    private static DateTime _jusqua;

    /// <summary>Un Deck se lance pour cette ROM : le canal est demande, si le jeu a un profil d'affichage. Rend vrai s'il l'est.</summary>
    public static bool Demander(string? rom, DateTime? maintenant = null)
    {
        if (CanalProfilAffichage.Pour(rom) is null) return false;
        lock (Verrou)
        {
            _rom = rom!.Trim();
            _jusqua = (maintenant ?? DateTime.UtcNow) + Validite;
        }

        return true;
    }

    /// <summary>Plus de demande (fin de partie, Deck retire).</summary>
    public static void Retirer()
    {
        lock (Verrou) { _rom = null; }
    }

    /// <summary>Le canal est-il demande pour cette ROM ?</summary>
    public static bool Voulu(string? rom, DateTime? maintenant = null)
    {
        if (string.IsNullOrWhiteSpace(rom)) return false;
        lock (Verrou)
        {
            return _rom is not null && string.Equals(_rom, rom.Trim(), StringComparison.OrdinalIgnoreCase) && (maintenant ?? DateTime.UtcNow) <= _jusqua;
        }
    }

    /// <summary>La ROM dont le canal est demande en ce moment (demande encore valide : avant le lancement du jeu), ou null.</summary>
    public static string? RomDemandee(DateTime? maintenant = null)
    {
        lock (Verrou)
        {
            return _rom is not null && (maintenant ?? DateTime.UtcNow) <= _jusqua ? _rom : null;
        }
    }

    /// <summary>
    /// La ROM du Deck pose tant qu'il n'est pas retire (fin de partie, Deck retire, MULTIPLAYER), SANS la validite de la demande :
    /// un canal deja ouvert reste lu pendant toute la partie. La validite de 10 min ne vaut que pour l'ecriture de la cle et pour la
    /// recherche du canal au lancement (relecture d'integration 1.9.211 : le canal etait lache 10 min apres la pose du Deck).
    /// </summary>
    public static string? RomEnCours()
    {
        lock (Verrou) { return _rom; }
    }

    /// <summary>Ajoute la cle au texte de certified.txt de cette ROM, si le canal est demande pour elle.</summary>
    public static void Ecrire(StringBuilder texte, string romBasename)
    {
        if (Voulu(romBasename)) texte.Append(Ligne).Append('\n');
    }
}
