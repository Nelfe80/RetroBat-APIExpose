using System.Diagnostics;
using System.Runtime.Versioning;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE DECK DANS LES DIRECTS YOUTUBE DU WORLD SCORING (CDC PlayerDeck H-D28, 09/11). Pour un CHALLENGE diffuse en LIVE dont le
/// Deck est pose chez le joueur, le direct montre ce que voit le joueur : le PlayerDeck (le meme exe, le meme theme, les memes
/// messages deck.*) tourne une seconde fois, sans fenetre, en mode direct (<c>--direct-out</c>) : il rend son image a la taille
/// du cadre du direct (1280 x 720), trou du jeu calcule pour ce cadre, et la depose dans une memoire partagee. L'exe du direct
/// (<c>--deck-memoire</c>, 0.7.3) pose le jeu du film de la borne dans le trou.
///
/// L'API ne calcule rien de plus : le Deck du direct lit le flux /ws comme celui de la borne (deck.snapshot a la connexion, puis
/// deck.game, deck.rank, deck.notice, signaux...). Ni film, ni MULTIPLAYER, ni speedrun : seulement ce lancement. Sans Deck, ou
/// avec un exe du direct trop ancien, rien ne change. Certification inchangee : rien de ce qui est ici ne touche au jeu.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeckDuDirect : IDisposable
{
    /// <summary>La taille du cadre du direct avec le Deck (celle du cadre 16:9 de l'exe).</summary>
    public const int Largeur = 1280, Hauteur = 720;

    /// <summary>Au plus 10 images du Deck par seconde dans le direct (le Deck de la borne en fait 20).</summary>
    public const int ImagesParSeconde = 10;

    private readonly Process _processus;

    /// <summary>Le nom de la memoire partagee, passe aux deux exe.</summary>
    public string Memoire { get; }

    private DeckDuDirect(Process processus, string memoire)
    {
        _processus = processus;
        Memoire = memoire;
    }

    /// <summary>Les options de l'exe du direct pour ce Deck.</summary>
    public IReadOnlyList<string> OptionsDuDirect => Options(Memoire);

    /// <summary>« --deck-memoire &lt;nom&gt; ». Pur.</summary>
    internal static IReadOnlyList<string> Options(string memoire) => ["--deck-memoire", memoire];

    /// <summary>Le lancement en cours montre-t-il le Deck dans le direct ? CHALLENGE du World Scoring, diffuse, Deck pose. Pur.</summary>
    internal static bool PourCeLancement(DeckLancement? l)
        => l is { Status: "posed", Mode: DeckModes.WorldScoring, Diffuse: true, DeckId: { Length: > 0 } };

    /// <summary>La ligne du PlayerDeck du direct. Pur.</summary>
    internal static string Arguments(string memoire, int parent)
        => $"--direct-out {memoire} --direct-parent {parent} --direct-fps {ImagesParSeconde} --render-size {Largeur}x{Hauteur}";

    /// <summary>Un nom de memoire partagee neuf (session locale de Windows).</summary>
    internal static string NouveauNom() => "Local\\nelfeplay-deck-direct-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Demarre le Deck du direct si ce direct doit le montrer ; null sinon (sans Deck, Deck retire, defi prive, MULTIPLAYER,
    /// exe du direct trop ancien, PlayerDeck absent, echec). Jamais bloquant ni visible.
    /// </summary>
    public static DeckDuDirect? Demarrer(DeckLaunchService? deck, string? exeDuDirect, ILogger journal)
    {
        try
        {
            if (deck is null || !PourCeLancement(deck.EnCours)) return null;
            if (!Replay.Video.LanceurDuDirect.SaitLeDeck(exeDuDirect))
            {
                journal.LogInformation("Direct : l'exe du direct ne sait pas poser le PlayerDeck (0.7.3 requis), direct sans Deck.");
                return null;
            }

            if (deck.ExeDuDeck() is not { } exe) return null;
            var memoire = NouveauNom();
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Arguments(memoire, Environment.ProcessId),
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* le Deck se met lui-meme en dessous */ }
            journal.LogInformation("Direct : PlayerDeck du direct demarre ({Deck}, {L} x {H}, {Ips} images/s au plus).",
                deck.EnCours?.DeckId, Largeur, Hauteur, ImagesParSeconde);
            return new DeckDuDirect(p, memoire);
        }
        catch (Exception ex)
        {
            journal.LogInformation(ex, "Direct : PlayerDeck du direct non demarre, direct sans Deck.");
            return null;
        }
    }

    /// <summary>Arrete le Deck du direct (fin du passage) ; il s'arrete aussi seul si l'API s'arrete.</summary>
    public void Dispose()
    {
        try
        {
            if (!_processus.HasExited) _processus.Kill();
        }
        catch (Exception) { /* deja parti */ }
        _processus.Dispose();
    }
}
