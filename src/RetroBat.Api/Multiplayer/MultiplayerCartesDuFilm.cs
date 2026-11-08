using RetroBat.Api.Leaderboard;
using C = RetroBat.Api.Leaderboard.LeaderboardOverlayService.ContenuMultiplayer;
using Etat = RetroBat.Api.Leaderboard.LeaderboardOverlayService.EtatAuLancement;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LES CARTES DU FILM D'UNE RENCONTRE (decision D-M4 du 08/11, lot M4 du CDC films MULTIPLAYER) : l'ecran de lancement VS et
/// l'ecran de fin, tels que la borne hote les montre, rendus en PNG pour l'exe du direct 0.7.2 (<c>--carte-avant</c>,
/// <c>--carte-apres</c>). RetroArch ne les filme pas : ce sont des fenetres de l'API posees devant le jeu.
/// <list type="bullet">
/// <item>Le panneau note ce qu'il montre (une reference, rien ne se dessine pendant la partie).</item>
/// <item>Le direct : la carte d'avant se rend au lancement de l'exe, pendant la boucle du depart pret (avant le vrai depart) ;
/// la carte de fin se rend quand l'ecran de fin parait, le jeu ferme.</item>
/// <item>La version YouTube : les deux cartes se rendent une fois le jeu ferme, l'ecran de lancement a l'etat du depart
/// (« A VOUS DE JOUER ! ») et l'ecran de fin anime jusqu'au bout.</item>
/// </list>
/// Les boutons (QUITTER, REJOUER, REESSAYER) ne paraissent jamais sur une carte : personne ne peut les presser.
/// </summary>
public sealed class MultiplayerCartesDuFilm
{
    /// <summary>La carte VS en tete du film, et l'ecran de fin a la suite (en secondes, comme l'exe les tient).</summary>
    public const double DureeAvantS = 3;
    public const double DureeApresS = 6;

    /// <summary>Le temps de l'animation rendu : le depart pose, l'entree jouee, la fin animee jusqu'aux confettis (aperçus des tests).</summary>
    internal const double TempsDuDepart = 0.6;
    internal const double TempsDuLancement = 2;
    internal const double TempsDeLaFin = 2.85;

    /// <summary>Le temps laisse a l'ecran de fin pour paraitre apres la fermeture du jeu (la gameroom finie chez la plateforme).</summary>
    internal static readonly TimeSpan AttenteDeLaFin = TimeSpan.FromSeconds(25);

    private readonly object _verrou = new();
    private readonly Func<C, double, byte[]> _rendu;
    private readonly ILogger? _journal;
    private (string Room, C Contenu)? _lancement;
    private (string Room, C Contenu)? _depart;
    private (string Room, C Contenu)? _fin;
    private (string Room, string Chemin, bool Rendue)? _finDuDirect;
    private TaskCompletionSource _finNotee = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MultiplayerCartesDuFilm(ILogger<LeaderboardOverlayService> journalDuPanneau, ILogger<MultiplayerCartesDuFilm> journal)
        : this((contenu, temps) => LeaderboardOverlayService.ApercuMultiplayer(EsMenuStyle.Lire(journal), contenu, "", "", "",
            journalDuPanneau, temps), journal)
    {
    }

    /// <summary>Le rendu se remplace dans les tests (aucune fenetre, aucune police).</summary>
    internal MultiplayerCartesDuFilm(Func<C, double, byte[]> rendu, ILogger? journal = null)
    {
        _rendu = rendu;
        _journal = journal;
    }

    /// <summary>
    /// L'ecran de lancement de cette gameroom, tel qu'il parait (le panneau le recompose toutes les demi-secondes). Celui du
    /// depart donne (<c>DepartUtc</c>) se garde a part : c'est la carte VS du film.
    /// </summary>
    public void NoterLeLancement(string room, C contenu)
    {
        if (string.IsNullOrWhiteSpace(room) || contenu.Lancement is null) return;
        lock (_verrou)
        {
            if (_lancement is { } avant && avant.Room != room)
            {
                // Une autre rencontre : ce qu'on savait de la precedente ne sert plus.
                _depart = null;
                _fin = null;
                _finNotee = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _lancement = (room, contenu);
            if (contenu.Lancement.DepartUtc is not null) _depart = (room, contenu);
        }
    }

    /// <summary>
    /// L'ecran de fin de cette gameroom, tel qu'il parait (le resultat se recompose quand les cotes arrivent : le dernier
    /// compte). Le direct qui l'attend le recoit aussitot.
    /// </summary>
    public void NoterLaFin(string room, C contenu)
    {
        if (string.IsNullOrWhiteSpace(room) || contenu.Fin is null) return;
        string? chemin = null;
        lock (_verrou)
        {
            var premiere = _fin is not { } deja || deja.Room != room;
            _fin = (room, contenu);
            if (premiere) _finNotee.TrySetResult();
            if (_finDuDirect is { Rendue: false } direct && direct.Room == room)
            {
                chemin = direct.Chemin;
                _finDuDirect = direct with { Rendue = true };
            }
        }
        if (chemin is not null) _ = Task.Run(() => Rendre(PourLeFilm(contenu), TempsDeLaFin, chemin, "ecran de fin du direct"));
    }

    /// <summary>
    /// Le direct de cette rencontre attend son ecran de fin a ce chemin (l'exe le lit a la fin du jeu). Deja paru : rendu tout
    /// de suite.
    /// </summary>
    public void AttendreLaFinPourLeDirect(string room, string chemin)
    {
        C? deja = null;
        lock (_verrou)
        {
            if (_fin is { } fin && fin.Room == room)
            {
                deja = fin.Contenu;
                _finDuDirect = (room, chemin, true);
            }
            else
            {
                _finDuDirect = (room, chemin, false);
            }
        }
        if (deja is not null) _ = Task.Run(() => Rendre(PourLeFilm(deja), TempsDeLaFin, chemin, "ecran de fin du direct"));
    }

    /// <summary>Le passage de cette rencontre est fini : son ecran de fin ne s'ecrit plus pour le direct.</summary>
    public void OublierLeDirect(string room)
    {
        lock (_verrou)
        {
            if (_finDuDirect is { } direct && direct.Room == room) _finDuDirect = null;
        }
    }

    /// <summary>
    /// LA CARTE D'AVANT DU DIRECT : l'ecran de lancement tel qu'il parait a l'instant (l'hote pret, ses invites en route),
    /// rendu a <paramref name="chemin"/>. Faux sans ecran de lancement connu pour cette gameroom, ou si le rendu echoue.
    /// </summary>
    public bool RendreLeLancementDuDirect(string room, string chemin)
    {
        C? contenu;
        lock (_verrou) contenu = _lancement is { } l && l.Room == room ? l.Contenu : null;
        return contenu is not null && Rendre(PourLeFilm(contenu), TempsDuLancement, chemin, "ecran de lancement du direct");
    }

    /// <summary>
    /// LES CARTES DE LA VERSION YOUTUBE, le jeu ferme : l'ecran de lancement au depart (<paramref name="avant"/>) et l'ecran de
    /// fin (<paramref name="apres"/>), attendu <paramref name="attente"/> au plus. Rend les chemins ecrits (null pour une
    /// carte qui manque).
    /// </summary>
    public async Task<(string? Avant, string? Apres)> RendreLesCartesDeLaVersionAsync(string room, string avant, string apres,
        TimeSpan attente, CancellationToken ct)
    {
        C? depart;
        Task finNotee;
        lock (_verrou)
        {
            depart = _depart is { } d && d.Room == room ? d.Contenu : _lancement is { } l && l.Room == room ? l.Contenu : null;
            finNotee = _fin is { } f && f.Room == room ? Task.CompletedTask : _finNotee.Task;
        }
        var vs = depart is not null && Rendre(PourLeFilm(depart, depart: true), TempsDuDepart, avant, "carte VS du film") ? avant : null;
        if (!finNotee.IsCompleted)
        {
            try { await finNotee.WaitAsync(attente, ct).ConfigureAwait(false); }
            catch (TimeoutException) { /* pas d'ecran de fin : le film finit sur le jeu */ }
        }
        C? fin;
        lock (_verrou) fin = _fin is { } f && f.Room == room ? f.Contenu : null;
        var ecranDeFin = fin is not null && Rendre(PourLeFilm(fin), TempsDeLaFin, apres, "ecran de fin du film") ? apres : null;
        return (vs, ecranDeFin);
    }

    /// <summary>
    /// Ce que montre une carte : l'ecran tel quel, sans ses boutons ni son aide ; une carte VS se pose au depart (tous
    /// prets, « A VOUS DE JOUER ! ») meme quand le panneau n'a pas tenu l'ecran jusque-la (un jeu sans boucle du depart). Pur.
    /// </summary>
    internal static C PourLeFilm(C contenu, bool depart = false, DateTime? maintenant = null)
    {
        var carte = contenu with { Aides = [], BoutonOk = string.Empty, GlypheOk = string.Empty };
        if (carte.Fin is { } fin) carte = carte with { Fin = fin with { SansBoutons = true } };
        if (carte.Lancement is { } l)
        {
            l = l with { SortiePossible = false, AttenteLongue = false };
            if (depart && l.DepartUtc is null)
            {
                // Le texte « PRET ! » d'un joueur deja pret (l'hote l'est des l'arrivee du jeu) vaut pour tous.
                var pret = l.Joueurs.FirstOrDefault(j => j.Etat == Etat.Pret)?.TexteDeLEtat;
                l = l with
                {
                    DepartUtc = maintenant ?? DateTime.UtcNow,
                    Etape = Math.Max(l.Etape, 2),
                    // « On attend … » ne vaut plus : tout le monde est la.
                    Legende = string.Empty,
                    Joueurs = [.. l.Joueurs.Select(j => j.Etat == Etat.Pret ? j : j with { Etat = Etat.Pret, TexteDeLEtat = pret ?? j.TexteDeLEtat })],
                };
            }
            carte = carte with { Lancement = l };
        }
        return carte;
    }

    /// <summary>Rend la carte et l'ecrit en <c>.tmp</c> puis la renomme : l'exe ne lit jamais une image a moitie ecrite.</summary>
    private bool Rendre(C contenu, double temps, string chemin, string quoi)
    {
        try
        {
            var png = _rendu(contenu, temps);
            if (png.Length == 0) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllBytes(chemin + ".tmp", png);
            File.Move(chemin + ".tmp", chemin, overwrite: true);
            _journal?.LogInformation("MULTIPLAYER : {Quoi} rendu ({Taille} octets).", quoi, png.Length);
            return true;
        }
        catch (Exception ex)
        {
            _journal?.LogInformation("MULTIPLAYER : {Quoi} non rendu ({Message}).", quoi, ex.Message);
            try { File.Delete(chemin + ".tmp"); } catch (Exception) { /* absent */ }
            return false;
        }
    }
}
