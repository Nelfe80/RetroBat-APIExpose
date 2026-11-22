using System.Text;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// L'ALLUMAGE DU CANAL (decisions du user du 22/11, CDC Canal v0.1) : le canal du wrapper ne s'ouvre que lorsqu'un Deck est lance,
/// pour un jeu qui a un profil d'affichage. L'API le demande en posant « canal=on » dans <c>certified.txt</c> (le wrapper 0.348 lit
/// cette cle au chargement du jeu, a cote de sa ROM). En netplay le wrapper se coupe de lui-meme, SAUF a l'hote admis par
/// <see cref="PermettreLHoteNetplay"/> (cle <c>canal.netplay=hote</c>, wrapper 0.349 : defi diffuse en LIVE, direct solo) ; jamais
/// chez un client netplay ni en MULTIPLAYER (le Deck y est retire, la cle aussi). Jamais au rendu d'un film
/// (<c>pipe=off</c> : le fichier de lecture ne porte pas la cle). Un wrapper plus ancien ignore la cle : rien ne change.
///
/// Meme principe que <see cref="RetroBat.Api.Speedrun.VerrousDuLancement"/> : un etat en memoire, une validite, une ecriture dans
/// le fichier a chaque reecriture. AFFICHAGE SEULEMENT : la cle ne change ni un reglage, ni un verrou, ni une empreinte du passeport.
/// </summary>
public static class CanalDuDeck
{
    /// <summary>La cle du fichier.</summary>
    public const string Ligne = "canal=on";

    /// <summary>
    /// La cle de l'HOTE netplay (wrapper 0.349) : cette borne heberge une partie qui n'est pas une partie MULTIPLAYER a plusieurs
    /// (defi diffuse en LIVE, direct solo par le site) ; le canal y vit comme hors netplay. Un wrapper plus ancien l'ignore (il se
    /// coupe en netplay, comme avant). Le canal n'ecrit rien dans la memoire du jeu ni dans ses entrees : aucun risque pour la
    /// synchronisation de la netplay.
    /// </summary>
    public const string LigneHoteNetplay = "canal.netplay=hote";

    /// <summary>Le Deck se lance quelques secondes avant le jeu ; au-dela de ce delai la demande est perimee.</summary>
    public static readonly TimeSpan Validite = TimeSpan.FromMinutes(10);

    private static readonly object Verrou = new();
    private static string? _rom;
    private static DateTime _jusqua;
    private static string? _hoteNetplay;

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
        lock (Verrou) { _rom = null; _hoteNetplay = null; }
    }

    /// <summary>
    /// Cette borne va HEBERGER cette ROM en netplay pour une partie qui n'est pas une partie MULTIPLAYER a plusieurs (defi diffuse en
    /// LIVE, direct par le site : <c>NetplayHostService.HebergerAsync</c>) : si un Deck y demande le canal, certified.txt porte aussi
    /// <see cref="LigneHoteNetplay"/>. Effacee par <see cref="Retirer"/> (fin de partie, Deck retire, MULTIPLAYER) et par
    /// <see cref="InterdireLeNetplay"/> (la borne rejoint la partie d'un autre).
    /// </summary>
    public static void PermettreLHoteNetplay(string? rom)
    {
        lock (Verrou) { _hoteNetplay = string.IsNullOrWhiteSpace(rom) ? null : rom.Trim(); }
    }

    /// <summary>La borne rejoint la partie d'un autre (client ou spectateur netplay) : le canal y reste coupe.</summary>
    public static void InterdireLeNetplay()
    {
        lock (Verrou) { _hoteNetplay = null; }
    }

    /// <summary>Le canal est-il demande pour cette ROM, et cette borne en est-elle l'hote netplay admis ?</summary>
    public static bool HoteNetplayAdmis(string? rom, DateTime? maintenant = null)
    {
        if (!Voulu(rom, maintenant)) return false;
        lock (Verrou) { return _hoteNetplay is not null && string.Equals(_hoteNetplay, rom!.Trim(), StringComparison.OrdinalIgnoreCase); }
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
        if (!Voulu(romBasename)) return;
        texte.Append(Ligne).Append('\n');
        if (HoteNetplayAdmis(romBasename)) texte.Append(LigneHoteNetplay).Append('\n');
    }
}
