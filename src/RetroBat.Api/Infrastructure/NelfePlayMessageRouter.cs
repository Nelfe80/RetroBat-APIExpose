using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// CE QUE LA PLATEFORME A DIT, remis au joueur au bon moment et par le bon canal.
///
/// Un message arrive avec un destinataire et un NIVEAU, jamais un canal : le canal se decide
/// ici, selon ce que le joueur fait a cet instant. Le meme « ton score est sorti de
/// quarantaine » passe par EmulationStation s'il est au menu, et attend s'il joue.
///
/// Trois regles commandent tout :
///
/// 1. UNE NOTIFICATION ES RAMENE ES AU PREMIER PLAN. L'envoyer pendant une partie ejecte le
///    joueur de son jeu (vecu le 2026-09-21 : le testeur s'est retrouve sur l'ecran de
///    selection en plein jeu). Elle ne part donc que quand aucun jeu ne tourne.
///
/// 2. EN JEU, SEUL LE CONTEXTE DU JEU EN COURS A LE DROIT DE PARAITRE. Un score classe la
///    semaine derniere n'a rien a faire par-dessus la partie d'aujourd'hui. Seuls les messages
///    qui invalident la partie en cours peuvent s'afficher, et seulement dans un TEMPS MORT.
///
/// 3. CE QU'ON NE PEUT PAS REMETTRE MAINTENANT EST GARDE, pas jete. Il repartira au prochain
///    passage, tant que sa duree de vie n'est pas ecoulee.
///
/// Voir CDC_NELFEPLAY_NOTIFICATIONS_v1.0.md, sections 4 et 9.3.
/// </summary>
public sealed class NelfePlayMessageRouter
{
    private readonly IEmulationStationNotificationService? _es;
    private readonly LiveContestOverlayService? _overlay;
    private readonly IngameGameplayStateService? _jeu;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<NelfePlayMessageRouter>? _logger;
    private readonly Func<bool> _emulateurTourne;

    private readonly object _porte = new();
    private readonly List<Message> _enAttente = [];

    public NelfePlayMessageRouter(
        IOptionsMonitor<ApiExposeOptions> options,
        IEmulationStationNotificationService? es = null,
        LiveContestOverlayService? overlay = null,
        IngameGameplayStateService? jeu = null,
        ILogger<NelfePlayMessageRouter>? logger = null,
        Func<bool>? emulateurTourne = null)
    {
        _options = options;
        _es = es;
        _overlay = overlay;
        _jeu = jeu;
        _logger = logger;
        _emulateurTourne = emulateurTourne ?? EmulatorForeground.EmulateurTourne;
    }

    /// <summary>Un message tel que la plateforme le remet, deja traduit.</summary>
    public sealed record Message(string Id, string Kind, string Niveau, string Texte, int Ttl)
    {
        public DateTime RecuUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Sa duree de vie est-elle ecoulee ? Un message perime ne se remet plus.</summary>
        public bool Perime(DateTime maintenantUtc) => (maintenantUtc - RecuUtc).TotalSeconds > Ttl;
    }

    /// <summary>Ce qui attend encore d'etre remis, pour le statut et les tests.</summary>
    public int EnAttente
    {
        get { lock (_porte) { return _enAttente.Count; } }
    }

    /// <summary>
    /// Range les messages recus d'un releve. Un identifiant deja connu n'entre pas deux fois :
    /// tant que la borne n'acquitte pas, le serveur les represente a chaque passage.
    /// </summary>
    public void Recevoir(IEnumerable<Message> messages)
    {
        lock (_porte)
        {
            foreach (var m in messages)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Texte))
                {
                    continue;
                }

                if (!_enAttente.Any(existant => string.Equals(existant.Id, m.Id, StringComparison.Ordinal)))
                {
                    _enAttente.Add(m);
                }
            }
        }
    }

    /// <summary>
    /// Tente de remettre ce qui attend, et rend les identifiants effectivement affiches.
    ///
    /// Ce sont eux, et eux seuls, qui seront acquittes aupres de la plateforme : un message
    /// garde en attente doit revenir au releve suivant, sinon une borne eteinte au mauvais
    /// moment le perdrait.
    /// </summary>
    public IReadOnlyList<string> Remettre()
    {
        List<Message> candidats;
        lock (_porte)
        {
            var maintenant = DateTime.UtcNow;
            _enAttente.RemoveAll(m => m.Perime(maintenant));
            candidats = [.. _enAttente];
        }

        var remis = new List<string>();
        foreach (var message in candidats)
        {
            if (!Afficher(message))
            {
                continue;
            }

            remis.Add(message.Id);
            lock (_porte)
            {
                _enAttente.RemoveAll(m => string.Equals(m.Id, message.Id, StringComparison.Ordinal));
            }
        }

        return remis;
    }

    /// <summary>Le choix du canal, isole pour etre teste sans machine.</summary>
    internal enum Canal
    {
        /// <summary>Rien maintenant : le message reste en attente.</summary>
        Attendre,
        /// <summary>Notification d'EmulationStation, au menu.</summary>
        EmulationStation,
        /// <summary>Surimpression par-dessus le jeu, dans un temps mort.</summary>
        Surimpression,
    }

    /// <summary>
    /// LA REGLE DE ROUTAGE.
    ///
    /// <paramref name="gameplayActif"/> vaut null quand la definition du jeu ne porte aucun
    /// marqueur de cycle de vie : on ne sait pas si le joueur est engage, et ne pas savoir se
    /// tranche ici du cote du SILENCE. Une notification de trop pendant une partie coute plus
    /// cher qu'une notification en retard.
    /// </summary>
    internal static Canal Choisir(
        string niveau,
        bool jeuEnCours,
        bool? gameplayActif,
        InGameMessageLevel seuil)
    {
        if (!jeuEnCours)
        {
            // Aucun jeu : ES a la main, et la notification ne derange personne.
            return Canal.EmulationStation;
        }

        // En jeu : seul le contexte de la partie en cours a le droit de paraitre, donc rien
        // au-dessous du seuil, et rien tant que le joueur est engage.
        if (!AuDessusDuSeuil(niveau, seuil) || gameplayActif != false)
        {
            return Canal.Attendre;
        }

        return Canal.Surimpression;
    }

    /// <summary>Le niveau passe-t-il le seuil regle sur cette borne ?</summary>
    internal static bool AuDessusDuSeuil(string niveau, InGameMessageLevel seuil) => seuil switch
    {
        InGameMessageLevel.Off => false,
        InGameMessageLevel.Critical => Est(niveau, "critical"),
        InGameMessageLevel.Score => Est(niveau, "critical") || Est(niveau, "score"),
        InGameMessageLevel.All => true,
        _ => Est(niveau, "critical"),
    };

    private static bool Est(string niveau, string attendu) =>
        string.Equals(niveau?.Trim(), attendu, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// LE JOUEUR EST-IL ENGAGE DANS SA PARTIE ? Null quand on n'en sait rien.
    ///
    /// L'etat vient des actions de cycle de vie de la definition de score : titre, demo,
    /// continue, game over valent faux, la partie en cours vaut vrai. Une definition qui ne
    /// porte aucun de ces marqueurs ne dit rien, et un etat trop vieux ne dit plus rien non
    /// plus : dans les deux cas on rend null, et le routeur se tait. Ne pas savoir se tranche
    /// ici du cote du silence.
    /// </summary>
    private bool? GameplayActif()
    {
        var etat = _jeu?.GetSnapshot();
        if (etat is null || etat.StateName.Length == 0 || etat.LastUpdatedUtc == DateTime.MinValue)
        {
            return null;
        }

        // Deux minutes : au-dela, l'etat decrit une partie qui n'est probablement plus celle
        // qui tourne.
        return DateTime.UtcNow - etat.LastUpdatedUtc > TimeSpan.FromMinutes(2)
            ? null
            : etat.IsGameplayActive;
    }

    private bool Afficher(Message message)
    {
        var canal = Choisir(
            message.Niveau,
            _emulateurTourne(),
            GameplayActif(),
            _options.CurrentValue.NelfePlay.InGameMessageLevel);

        try
        {
            switch (canal)
            {
                case Canal.EmulationStation when _es is not null:
                    // Sans attendre : une notification qui n'arrive pas ne doit pas retenir
                    // le releve, et le message reviendra s'il n'a pas ete acquitte.
                    _ = _es.NotifyAsync(message.Texte);
                    return true;

                case Canal.Surimpression when _overlay is not null:
                    _overlay.ShowTop("NELFE PLAY", message.Texte, null, 6000);
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Nelfe Play : message {Kind} non remis", message.Kind);
            return false;
        }
    }
}

/// <summary>
/// CE QUI A LE DROIT DE PARAITRE PAR-DESSUS UN JEU, par ordre croissant.
///
/// Le reglage ne concerne QUE l'affichage en jeu : il ne coupe ni EmulationStation, ni le
/// push, ni la liste du compte sur le site. Une famille sous le seuil arrive au menu, pas
/// par-dessus la partie.
/// </summary>
public enum InGameMessageLevel
{
    /// <summary>Rien en jeu. La borne d'exposition, le stream.</summary>
    Off = 0,

    /// <summary>Ce qui invalide la partie en cours : le wrapper absent, l'emulateur inconnu.</summary>
    Critical = 1,

    /// <summary>Plus ce qui concerne le score du joueur assis. Le defaut.</summary>
    Score = 2,

    /// <summary>Plus la competition sur le jeu en cours. La borne a la maison.</summary>
    All = 3,
}
