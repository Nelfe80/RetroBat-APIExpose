using System.Diagnostics;
using System.Runtime.Versioning;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE DECK DANS LES DIRECTS YOUTUBE DU WORLD SCORING (CDC PlayerDeck H-D28, 09/11). Pour un CHALLENGE diffuse en LIVE dont le
/// Deck est pose chez le joueur, le direct montre ce que voit le joueur : le PlayerDeck (le meme exe, le meme theme, les memes
/// messages deck.*) tourne une seconde fois, sans fenetre, en mode direct (<c>--direct-out</c>) : il rend son image a la taille
/// du cadre du direct (1280 x 720), trou du jeu calcule pour ce cadre, et la depose dans une memoire partagee. L'exe du direct
/// (<c>--deck-memoire</c>, 0.7.3) pose le jeu du film de la borne dans le trou. Le Deck du direct part avec la partie (le defi
/// diffuse), pas avec le passage a l'antenne : ses instruments qui s'accumulent sont ceux du Deck de la borne ; il s'arrete a la
/// fin de la partie (<see cref="DeckLaunchService"/>).
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

    private readonly Process? _processus;
    private readonly EventWaitHandle? _fin;
    private bool _arrete;

    /// <summary>Le suffixe de l'evenement nomme par lequel l'API demande l'arret au Deck du direct (PlayerDeck 0.1.16 et suivants).</summary>
    public const string SuffixeDeFin = "-fin";

    /// <summary>Un Deck d'avant l'arret demande (il ne regarde pas l'evenement) est tue apres ce delai.</summary>
    internal static readonly TimeSpan PatienceDeFin = TimeSpan.FromSeconds(3);

    // Les processus des Decks du direct encore vivants (y compris ceux qui s'arretent) : ce ne sont jamais des Decks de la borne
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> Pids = new();

    /// <summary>Ce processus est-il un Deck du direct (lance ici, pas encore sorti) ? Le Deck de la borne porte le meme nom d'exe.</summary>
    public static bool EstUnDeckDuDirect(int pid) => Pids.ContainsKey(pid);

    /// <summary>Les directs qui montrent ce Deck en ce moment (bail pris par l'exe du direct, rendu a sa fin).</summary>
    internal int Lecteurs;

    /// <summary>La partie est finie : le Deck ne sert plus que les directs en cours, puis s'arrete.</summary>
    internal bool FinDemandee;

    /// <summary>Le nom de la memoire partagee, passe aux deux exe.</summary>
    public string Memoire { get; }

    private DeckDuDirect(Process? processus, string memoire, EventWaitHandle? fin = null)
    {
        _processus = processus;
        _fin = fin;
        Memoire = memoire;
    }

    /// <summary>Les options de l'exe du direct pour ce Deck.</summary>
    public IReadOnlyList<string> OptionsDuDirect => Options(Memoire);

    /// <summary>« --deck-memoire &lt;nom&gt; ». Pur.</summary>
    internal static IReadOnlyList<string> Options(string memoire) => ["--deck-memoire", memoire];

    /// <summary>Le lancement en cours montre-t-il le Deck dans le direct ? CHALLENGE du World Scoring diffuse, ou speedrun / challenge du panneau des defis ; Deck pose. Jamais MULTIPLAYER. Pur.</summary>
    internal static bool PourCeLancement(DeckLancement? l)
        => l is { Status: "posed", DeckId: { Length: > 0 } }
            && ((l.Mode == DeckModes.WorldScoring && l.Diffuse) || l.Mode == DeckModes.Speedrun);

    /// <summary>La ligne du PlayerDeck du direct. Pur.</summary>
    internal static string Arguments(string memoire, int parent)
        => $"--direct-out {memoire} --direct-parent {parent} --direct-fps {ImagesParSeconde} --render-size {Largeur}x{Hauteur}";

    /// <summary>Un nom de memoire partagee neuf (session locale de Windows).</summary>
    internal static string NouveauNom() => "Local\\nelfeplay-deck-direct-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Demarre le Deck du direct si ce direct doit le montrer ; null sinon (sans Deck, Deck retire, defi prive, MULTIPLAYER,
    /// exe du direct trop ancien, PlayerDeck absent, echec). Jamais bloquant ni visible.
    /// </summary>
    public static DeckDuDirect? Demarrer(DeckLaunchService? deck, string? exeDuDirect, ILogger? journal)
    {
        try
        {
            if (deck is null || !PourCeLancement(deck.EnCours)) return null;
            if (!Replay.Video.LanceurDuDirect.SaitLeDeck(exeDuDirect))
            {
                journal?.LogInformation("PlayerDeck : l'exe du direct ne sait pas poser le Deck (0.7.3 requis), direct sans Deck.");
                return null;
            }

            if (deck.ExeDuDeckInstalle() is not { } exe) return null;
            var memoire = NouveauNom();
            // L'ARRET DEMANDE (19/11) : l'evenement existe avant le Deck, qui l'ouvre a son demarrage
            var fin = new EventWaitHandle(false, EventResetMode.ManualReset, memoire + SuffixeDeFin);
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Arguments(memoire, Environment.ProcessId),
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                fin.Dispose();
                return null;
            }

            // LA PRIORITE NORMALE (banc du 19/11) : en priorite basse sur une borne a 100 %, le Deck du direct restait 3 a 6,6 s sans
            // processeur (battement fige) et le direct sautait de cadre. Il prend 0,1 a 1,5 % d'un coeur ; RetroArch est au-dessus de
            // la normale pendant un defi. Le PlayerDeck 0.1.16 reste lui-meme en normale avec --direct-out.
            Pids[p.Id] = 0;
            journal?.LogInformation("PlayerDeck : Deck du direct demarre ({Deck}, {Mode}, {L} x {H}, {Ips} images/s au plus).",
                deck.EnCours?.DeckId, deck.EnCours?.Mode, Largeur, Hauteur, ImagesParSeconde);
            return new DeckDuDirect(p, memoire, fin);
        }
        catch (Exception ex)
        {
            journal?.LogInformation(ex, "PlayerDeck : Deck du direct non demarre, direct sans Deck.");
            return null;
        }
    }

    /// <summary>Le Deck du direct tourne-t-il encore ?</summary>
    public bool Vivant
    {
        get
        {
            if (_processus is null) return !_arrete;
            try { return !_processus.HasExited; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Arrete le Deck du direct (fin du direct, nouveau lancement, arret de l'API) ; il s'arrete aussi seul si l'API s'arrete.
    /// Sans attendre : l'evenement de fin est leve (le Deck ferme son WebSocket et sort), et un Deck qui ne l'a pas lu au bout de
    /// <see cref="PatienceDeFin"/> (PlayerDeck plus ancien) est tue. Le Deck de la borne porte le meme nom d'exe : celui-ci reste
    /// marque Deck du direct jusqu'a sa sortie, pour que DemarrerLeDeck ne le prenne jamais pour le Deck de la borne.
    /// </summary>
    public void Dispose()
    {
        if (_arrete) return;
        _arrete = true;
        if (_processus is null)
        {
            _fin?.Dispose();
            return;
        }

        try { _fin?.Set(); } catch (ObjectDisposedException) { /* deja leve */ }
        var p = _processus;
        var fin = _fin;
        _ = Task.Run(() =>
        {
            try
            {
                if (!p.WaitForExit(PatienceDeFin)) p.Kill();
                p.WaitForExit(2000);
            }
            catch (Exception) { /* deja parti */ }
            finally
            {
                try { Pids.TryRemove(p.Id, out _); } catch (Exception) { /* processus illisible */ }
                p.Dispose();
                fin?.Dispose();
            }
        });
    }

    /// <summary>Couture des essais : un Deck du direct sans processus, vivant jusqu'a son arret.</summary>
    internal static DeckDuDirect PourEssai(string memoire) => new(null, memoire);
}

/// <summary>
/// LE BAIL D'UN DIRECT SUR LE DECK DU DIRECT (19/11) : les options de l'exe du direct (<c>--deck-memoire</c>, ou rien), et sa
/// reddition a la fin de l'exe. Le film arrive au direct 5 a 19 s apres le jeu : tant qu'un direct tient le Deck, la fin de la
/// partie ne l'arrete pas (la fin de la course garde ses rails) ; il s'arrete quand le dernier direct le rend, et au plus tard
/// <see cref="DeckLaunchService.GardeApresLaPartie"/> apres la partie.
/// </summary>
public sealed class BailDuDirect : IDisposable
{
    /// <summary>Pas de Deck dans ce direct.</summary>
    public static BailDuDirect Aucun => new([], null);

    private Action? _rendre;

    internal BailDuDirect(IReadOnlyList<string> options, Action? rendre)
    {
        Options = options;
        _rendre = rendre;
    }

    /// <summary>Les options de l'exe du direct.</summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>Rend le bail (une seule fois).</summary>
    public void Dispose() => Interlocked.Exchange(ref _rendre, null)?.Invoke();
}
