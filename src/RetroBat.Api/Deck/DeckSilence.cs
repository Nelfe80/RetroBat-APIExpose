using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE DECK POSE FAIT TAIRE CE QUI PASSE DEVANT LUI (demande du user du 08/11, apres la 1.9.163 : « les notifications
/// RetroArch et les affichages type ranking du challenge passent devant le player deck »).
///
/// Tenu a jour par le bus : <c>deck.game</c> (le Deck du lancement : decor pose, Off, aucun) et <c>deck.choice</c> (Off ou
/// retour du Deck en jeu). Trois questions, posees par ceux qui affichent :
/// <list type="bullet">
/// <item><see cref="DeckVisible"/> : le Deck est la, sur son decor. Le cartouche du defi (LIVE RANK du Deck) et les autres
/// doublons ne s'affichent pas.</item>
/// <item><see cref="AuDeck"/> : un message utile au joueur va au Deck (<c>deck.notice</c>) plutot qu'a RetroArch ou a un
/// bandeau. Vrai quand le Deck est visible, et aussi quand il a ete retire en jeu alors que les notifications de RetroArch
/// sont coupees pour ce lancement (<c>-OnScreenMsg 0</c>) : RetroArch ne montrerait plus rien, le Deck montre la plaque
/// seule.</item>
/// <item><see cref="ZoneDuJeu"/> : le trou du decor, ou un bandeau sans texte anglais (message de la plateforme, traduit par
/// elle) reste confine, sans recouvrir les rails.</item>
/// </list>
/// SANS DECK (Off des le lancement, jeu sans Deck, Deck absent, Deck d'une version sans plaque), tout repond faux : rien ne
/// change par rapport a la 1.9.163.
/// </summary>
public sealed class DeckSilence : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly ILogger<DeckSilence>? _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private Etat? _etat;
    private long _sequence;

    /// <summary>L'instance de l'API, pour les services qui ne la recoivent pas par leur constructeur (MULTIPLAYER, video).</summary>
    public static DeckSilence? Courante { get; private set; }

    public DeckSilence(IEventBus bus, ILogger<DeckSilence>? journal = null)
    {
        _bus = bus;
        _journal = journal;
    }

    /// <summary>Le Deck de la partie en cours, tel que la silence le voit.</summary>
    internal sealed record Etat(string LaunchId, string Status, string? DeckId, bool Notices, bool RetroArchMuet, DeckDecor.Trou? Trou, bool Retire);

    /// <summary>Change quand le Deck parait ou disparait (le cartouche du defi suit).</summary>
    public event Action? Change;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Courante = this;
        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        if (ReferenceEquals(Courante, this)) Courante = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    /// <summary>Couture des essais : pose cette instance comme celle de l'API.</summary>
    internal void Installer() => Courante = this;

    /// <summary>Le Deck est la, sur son decor (pose, non retire en jeu). Les doublons du Deck ne s'affichent pas.</summary>
    public bool DeckVisible
    {
        get { lock (_verrou) return Visible(_etat); }
    }

    /// <summary>Un message utile au joueur va au Deck (deck.notice) plutot qu'a RetroArch ou a un bandeau.</summary>
    public bool AuDeck
    {
        get { lock (_verrou) return VersLeDeck(_etat); }
    }

    /// <summary>Les notifications de RetroArch sont-elles coupees pour la partie en cours (-OnScreenMsg 0) ?</summary>
    public bool RetroArchMuet
    {
        get { lock (_verrou) return _etat is { RetroArchMuet: true }; }
    }

    /// <summary>Le trou du decor (pixels de l'ecran principal) quand le Deck est visible ; null sinon.</summary>
    public DeckDecor.Trou? ZoneDuJeu
    {
        get { lock (_verrou) return Visible(_etat) ? _etat!.Trou : null; }
    }

    internal static bool Visible(Etat? e) => e is { Status: "posed", Trou: not null, Retire: false, Notices: true };

    internal static bool VersLeDeck(Etat? e) => e is { Notices: true } && (Visible(e) || e.RetroArchMuet);

    /// <summary>
    /// Dit <paramref name="notice"/> au Deck s'il doit l'entendre (<see cref="AuDeck"/>) : publie deck.notice et rend vrai. Faux :
    /// l'appelant affiche comme avant (RetroArch, bandeau).
    /// </summary>
    public bool Dire(DeckNotice? notice) => Dire(notice, AuDeck);

    /// <summary>Comme <see cref="Dire(DeckNotice?)"/>, la route ayant ete decidee avant (le choix du Deck change l'etat).</summary>
    public bool Dire(DeckNotice? notice, bool auDeck)
    {
        if (!auDeck || notice is null) return auDeck;
        var n = notice with { Sequence = Interlocked.Increment(ref _sequence) };
        _journal?.LogInformation("PlayerDeck : message au Deck {Kind} ({Severity}) « {Title} {Value} {Text} ».", n.Kind, n.Severity, n.Title, n.Value, n.Text);
        _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.notice", Payload = n });
        return true;
    }

    /// <summary>Le message d'un service sans acces a l'instance : au Deck si la silence est la et le veut, sinon faux.</summary>
    public static bool DireSiPose(DeckNotice? notice) => Courante?.Dire(notice) ?? false;

    // ─────────────────────────────── bus ───────────────────────────────

    private void Recevoir(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "deck.game" when e.Payload is DeckGame g:
                    Poser(g);
                    break;
                case "deck.choice" when e.Payload is DeckChoixPublie c:
                    Choisir(c);
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "PlayerDeck : evenement {Type} ignore par la silence.", e.Type);
        }
    }

    /// <summary>deck.game : le Deck de la partie (decor pose ou non), effacee a la fin.</summary>
    internal void Poser(DeckGame g)
    {
        bool avant, apres;
        lock (_verrou)
        {
            avant = Visible(_etat);
            if (g.State == "ended")
            {
                if (_etat?.LaunchId == g.LaunchId) _etat = null;
            }
            else if (_etat?.LaunchId != g.LaunchId)
            {
                var d = g.Deck;
                _etat = d is null ? null : new Etat(g.LaunchId, d.Status, d.DeckId, d.Notices, d.RetroArchMuet, d.Decor?.Hole, false);
            }

            apres = Visible(_etat);
        }

        if (avant != apres) Signaler(apres);
    }

    /// <summary>deck.choice : Off cache le Deck (le decor reste), le Deck du decor le ramene.</summary>
    internal void Choisir(DeckChoixPublie c)
    {
        bool avant, apres;
        lock (_verrou)
        {
            if (_etat is null) return;
            avant = Visible(_etat);
            if (c.Off) _etat = _etat with { Retire = true };
            else if (c.Now == "show") _etat = _etat with { Retire = false };
            apres = Visible(_etat);
        }

        if (avant != apres) Signaler(apres);
    }

    private void Signaler(bool visible)
    {
        _journal?.LogInformation("PlayerDeck : Deck {Etat} : affichages de l'API {Suite}.", visible ? "visible" : "retire",
            visible ? "en doublon coupes, messages au Deck" : "rendus");
        try { Change?.Invoke(); }
        catch (Exception ex) { _journal?.LogDebug(ex, "PlayerDeck : un abonne de la silence a echoue."); }
    }
}
