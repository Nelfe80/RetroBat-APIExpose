using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Multiplayer;

/// <summary>Ce que le journal de RetroArch dit de cette borne au netplay : elle a pris la manette, l'a rendue, ou n'a pas pu la prendre.</summary>
public enum MultiplayerReponseDuNetplay
{
    Rejoint,
    Quitte,
    Refuse,
}

/// <summary>
/// LE RELAIS DE LA MANETTE, SANS LECTURE (CDC Classements v1.0 §8.3, revise le 29/10) : il recoit l'octet du tour et les
/// reponses du journal de sa borne, dans l'ordre et a leur instant, et dit quand basculer. Une bascule n'est envoyee que la
/// valeur du tour tenue <see cref="MultiplayerTurnRelay.SettleMs"/>, apres une premiere reponse du journal (l'etat de depart
/// de la borne), et jamais deux a la fois : la commande bascule, et une bascule sans reponse arrete le relais plutot que de
/// risquer de rendre la manette a contretemps.
/// </summary>
public sealed class MultiplayerRelaisEnCours
{
    /// <summary>Une bascule sans reponse au journal passe ce delai : le relais s'arrete (la borne garde ce qu'elle a).</summary>
    public const long SansReponseMs = 6000;

    /// <summary>Apres une prise refusee (la manette encore tenue par le sortant), le temps avant de redemander.</summary>
    public const long ApresUnRefusMs = 500;

    private readonly MultiplayerTurnRelay _relais;
    private readonly int _place;
    private (long Valeur, long Depuis)? _tour;
    private long? _envoi;
    private long _pasAvant;
    private bool _etatConnu;

    public MultiplayerRelaisEnCours(MultiplayerTurnRelay relais, int place)
    {
        _relais = relais;
        _place = place;
    }

    /// <summary>La borne tient-elle la manette ?</summary>
    public bool Joue { get; private set; }

    /// <summary>Une bascule est restee sans reponse : le relais ne bascule plus rien.</summary>
    public bool HorsService { get; private set; }

    /// <summary>Une reponse lue au journal de la borne.</summary>
    public void Noter(MultiplayerReponseDuNetplay reponse, long ms)
    {
        _etatConnu = true;
        _envoi = null;
        Joue = reponse == MultiplayerReponseDuNetplay.Rejoint;
        if (reponse == MultiplayerReponseDuNetplay.Refuse)
        {
            _pasAvant = ms + ApresUnRefusMs;
        }
    }

    /// <summary>Un releve de l'octet du tour (null : pas lu). Vrai : envoyer NETPLAY_GAME_WATCH maintenant.</summary>
    public bool Observer(long? valeur, long ms)
    {
        if (valeur is { } lue && (_tour is not { } suivi || suivi.Valeur != lue))
        {
            _tour = (lue, ms);
        }

        if (_envoi is { } envoi && ms - envoi >= SansReponseMs)
        {
            HorsService = true;
        }

        if (HorsService || !_etatConnu || _envoi is not null || ms < _pasAvant
            || _tour is not { } tenu || ms - tenu.Depuis < _relais.SettleMs || _relais.PlaceDuTour(tenu.Valeur) is not { } duTour)
        {
            // Une valeur hors de la table (le tour de l'ordinateur, une transition) : personne n'a a changer de main.
            return false;
        }

        if ((duTour == _place) == Joue)
        {
            return false;
        }

        _envoi = ms;
        return true;
    }
}

/// <summary>
/// LA MANETTE PASSEE AU JOUEUR COURANT (CDC Classements v1.0 §8.3, revise le 29/10 ; Worms) : chaque borne lit l'octet du
/// tour (le netplay le garde le meme partout), rend la manette quand le tour passe a un autre, et la prend quand il lui
/// revient, par NETPLAY_GAME_WATCH, la bascule joueur / spectateur de RetroArch. C'est une commande reseau de la 1.22.2
/// (table <c>map</c> de command.h), mesuree au banc le 30/10 : rendre en 0,1 a 1,9 s, prendre en 0,2 a 2,3 s ; prendre une
/// manette encore tenue est refuse sans effet, et se redemande. Au lancement, l'hote la tient et un invite qui la demande
/// est refuse : il regarde.
/// </summary>
public sealed class MultiplayerRelaisDeLaManette
{
    // Les messages de RetroArch 1.22.2 sur la borne elle-meme (intl/msg_hash_us.h et msg_hash_fr.h) : le journal suit la langue
    // de la borne. Dans une autre langue, rien ne se reconnait et le relais ne bascule jamais (la borne garde ce qu'elle a).
    private static readonly (string Texte, MultiplayerReponseDuNetplay Reponse)[] Messages =
    [
        ("You have joined as player", MultiplayerReponseDuNetplay.Rejoint),
        ("You have joined with input devices", MultiplayerReponseDuNetplay.Rejoint),
        ("Vous avez rejoint le jeu en tant que joueur", MultiplayerReponseDuNetplay.Rejoint),
        ("Vous avez rejoint le jeu avec des dispositifs", MultiplayerReponseDuNetplay.Rejoint),
        ("You have left the game", MultiplayerReponseDuNetplay.Quitte),
        ("Vous avez quitté le jeu", MultiplayerReponseDuNetplay.Quitte),
        ("The input devices requested are not available", MultiplayerReponseDuNetplay.Refuse),
        ("There are no free player slots", MultiplayerReponseDuNetplay.Refuse),
        ("You do not have permission to play", MultiplayerReponseDuNetplay.Refuse),
        ("Cannot switch to play mode", MultiplayerReponseDuNetplay.Refuse),
        ("ne sont pas disponibles", MultiplayerReponseDuNetplay.Refuse),
        ("Aucune place de libre pour jouer", MultiplayerReponseDuNetplay.Refuse),
        ("Vous n'avez pas la permission de jouer", MultiplayerReponseDuNetplay.Refuse),
        ("Impossible de basculer en mode jeu", MultiplayerReponseDuNetplay.Refuse),
    ];

    /// <summary>Ce qu'une ligne du journal dit de cette borne, ou null (une autre borne, un autre sujet). Pur.</summary>
    public static MultiplayerReponseDuNetplay? Lire(string ligne)
    {
        if (!ligne.Contains("[Netplay]", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var (texte, reponse) in Messages)
        {
            if (ligne.Contains(texte, StringComparison.Ordinal))
            {
                return reponse;
            }
        }

        return null;
    }

    private readonly ICommandesRetroArch _commandes;
    private readonly ILogger? _journal;
    private readonly TimeSpan _periode;

    public MultiplayerRelaisDeLaManette(ICommandesRetroArch commandes, ILogger? journal = null, TimeSpan? periode = null)
    {
        _commandes = commandes;
        _journal = journal;
        _periode = periode ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>Relaie la manette pour cette place jusqu'a l'annulation (la fin de la partie). Rend le nombre de bascules.</summary>
    public async Task<int> RelayerAsync(MultiplayerTurnRelay relais, int place, string cheminDuJournal, CancellationToken ct)
    {
        var enCours = new MultiplayerRelaisEnCours(relais, place);
        var journal = new SuiteDuJournal(cheminDuJournal);
        var horloge = System.Diagnostics.Stopwatch.StartNew();
        var bascules = 0;
        long? vue = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var ligne in journal.Nouvelles())
                {
                    if (Lire(ligne) is { } reponse)
                    {
                        enCours.Noter(reponse, horloge.ElapsedMilliseconds);
                        _journal?.LogInformation("MULTIPLAYER : relais de la manette, place {Place} : {Reponse}", place, reponse);
                    }
                }

                var valeur = await LireLeTourAsync(relais, ct).ConfigureAwait(false);
                if (valeur is not null && valeur != vue)
                {
                    vue = valeur;
                    _journal?.LogInformation("MULTIPLAYER : relais de la manette, place {Place} : tour {Valeur} (place {DuTour})", place, valeur,
                        relais.PlaceDuTour(valeur.Value)?.ToString(CultureInfo.InvariantCulture) ?? "aucune");
                }

                if (enCours.Observer(valeur, horloge.ElapsedMilliseconds))
                {
                    bascules++;
                    _journal?.LogInformation("MULTIPLAYER : relais de la manette, place {Place} : {Sens} (tour {Valeur})", place,
                        enCours.Joue ? "rendue" : "demandee", valeur);
                    await _commandes.EnvoyerAsync("NETPLAY_GAME_WATCH", ct).ConfigureAwait(false);
                }
                else if (enCours.HorsService)
                {
                    _journal?.LogWarning("MULTIPLAYER : relais de la manette arrete, place {Place} : bascule sans reponse au journal", place);
                    break;
                }

                await Task.Delay(_periode, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return bascules;
    }

    private async Task<long?> LireLeTourAsync(MultiplayerTurnRelay relais, CancellationToken ct)
    {
        if (relais.Decalage is not { } decalage)
        {
            return null;
        }

        var commande = relais.ParLaCarteDuCoeur ? "READ_CORE_MEMORY" : "READ_CORE_RAM";
        var reponse = await _commandes.DemanderAsync($"{commande} {decalage:x} {relais.Bytes}", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (morceaux is null || morceaux.Length < 2 + relais.Bytes || morceaux[0] != commande)
        {
            return null;
        }

        var octets = new List<int>(relais.Bytes);
        for (var i = 0; i < relais.Bytes; i++)
        {
            if (!int.TryParse(morceaux[2 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var octet))
            {
                return null;
            }

            octets.Add(octet);
        }

        return relais.Signal.Valeur(octets);
    }

    /// <summary>Les lignes ajoutees au journal depuis la derniere lecture ; il repart du debut s'il a ete recree.</summary>
    private sealed class SuiteDuJournal(string chemin)
    {
        private long _position;
        private string _reste = string.Empty;

        public IReadOnlyList<string> Nouvelles()
        {
            try
            {
                using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (flux.Length < _position)
                {
                    _position = 0;
                    _reste = string.Empty;
                }

                flux.Seek(_position, SeekOrigin.Begin);
                using var lecteur = new StreamReader(flux, Encoding.UTF8);
                var texte = _reste + lecteur.ReadToEnd();
                _position = flux.Position;
                var lignes = texte.Split('\n');
                _reste = lignes[^1];
                return lignes[..^1];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
