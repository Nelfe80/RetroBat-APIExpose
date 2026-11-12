using System.Globalization;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Replay.Video;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck;

/// <summary>Ce qu'il faut savoir de l'epreuve pour lire sa memoire : le coeur, la traduction du reglement, le .MEM en vigueur.</summary>
/// <param name="Coeur">Le coeur du lancement (« fceumm », « fceumm_ghosts », « fbneo ») ; null s'il n'est pas connu.</param>
/// <param name="Offset">La traduction du reglement (« runtime.core_ram ») ; null : celle de <see cref="AdressesDuCoeur"/>.</param>
/// <param name="Mem">Le chemin du .MEM charge pour l'epreuve.</param>
public sealed record EpreuveALire(string ChallengeId, string? Coeur, Func<long, long>? Offset, string? Mem, string RomGroup)
{
    public static EpreuveALire De(Reglement r) => new(r.ChallengeId, r.Coeurs.FirstOrDefault(),
        r.BaseDuCoeur is null ? null : r.OffsetDansLeCoeur, r.DefinitionAuLancement(), r.RomGroup);
}

/// <summary>
/// LES VALEURS DE DEPART D'UNE EPREUVE POUR LE PLAYERDECK (12/11, capture d'Aynshe : plaques MARIO et SPLITS a « -- » au depart
/// de Super Mario Bros. 1-1). Le wrapper n'emet une ligne du .MEM qu'a son CHANGEMENT : pendant une epreuve courte, les vies,
/// la puissance et le niveau ne bougent pas, le Deck ne recoit jamais leur valeur (et la page du depart, 0, n'ouvrait jamais
/// le premier segment des splits).
///
/// A l'etat de depart charge (premiere tentative et chaque relance) puis au depart du chrono, les lignes du .MEM que le
/// .SMEM cite (le sens que le Deck affiche) sont lues une fois par READ_CORE_RAM, l'adresse traduite pour le coeur (regle du
/// reglement, sinon celle du controle des films : banques et repli ^1 de FBNeo, index bruts des consoles et des coeurs a
/// fantomes), la valeur decodee comme le wrapper la decode (type, ordre des octets, masque, condition). Elles partent au Deck
/// en <c>deck.signals</c> : des valeurs d'ETAT, comme celles de l'instantane, jamais des evenements (une valeur lue n'a pas
/// d'instant : ni mort comptee, ni anneaux remis a zero, ni bombe lancee). Elles entrent aussi dans l'instantane du Deck.
///
/// Rien d'autre ne les voit : elles ne passent jamais par <c>retroarch.memory.changed</c> (le scoring, l'evaluateur des
/// epreuves, les stats ne lisent que le wrapper). Lecture seule de la memoire du jeu. Pur hors <see cref="LireAsync"/>.
/// </summary>
public static class DeckValeursDeDepart
{
    /// <summary>La source des signaux de depart (champ Source du signal).</summary>
    public const string Source = "apiexpose.deck.start";

    /// <summary>Le canal d'un signal de depart sans signal du wrapper deja vu pour sa ligne.</summary>
    public const string CanalParDefaut = "STATE";

    /// <summary>Une plage plus large se lit octet par octet (une ligne ne fait que 1 a 4 octets).</summary>
    private const int PlageMax = 16;

    /// <summary>Les conditions dont le wrapper publie la valeur lue a chaque emission.</summary>
    private static readonly HashSet<string> Conditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "change", "increase", "decrease", "eq", "equal", "neq", "not_equal", "bit_true", "bit_false",
    };

    /// <summary>
    /// Les lignes a relever : celles que le .SMEM cite (adresse, action, joueur), emises par le wrapper (ni no_log ni no_survey),
    /// d'une condition connue ; une fois chacune. Pur.
    /// </summary>
    public static IReadOnlyList<LigneDuMem> LignesARelever(IReadOnlyList<DeckSemanticSignal> signaux, LignesDuMem mem)
    {
        var lignes = new List<LigneDuMem>();
        foreach (var s in signaux)
            foreach (var r in s.From)
            {
                long adresse;
                try { adresse = Hex.Lire(r.Address); }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { continue; }
                foreach (var l in mem.Emises(adresse))
                {
                    if (!string.Equals(l.Action, r.Action, StringComparison.OrdinalIgnoreCase)) continue;
                    if (r.Player is { } p && l.Joueur is { } j && p != j) continue;
                    if (!Conditions.Contains(l.Condition) || lignes.Contains(l)) continue;
                    lignes.Add(l);
                }
            }

        return lignes;
    }

    /// <summary>Le nombre d'octets d'un type du .MEM (u8, u16le, u24be, u32...). Pur.</summary>
    public static int Octets(string type) => LecteurDuScore.Octets(type);

    /// <summary>
    /// Les index, dans la memoire que lit READ_CORE_RAM, des octets de la ligne, dans l'ordre de son adresse ; null si on ne
    /// sait pas ou les lire (MAME, coeur inconnu hors d'un reglement qui dit son bloc). Pur sur ses banques.
    /// </summary>
    public static long[]? Places(LigneDuMem l, string? coeur, Func<long, long>? offset, IReadOnlyList<AdressesDuCoeur.Banque> banques)
    {
        var n = Octets(l.Type);
        if (offset is not null) return Enumerable.Range(0, n).Select(i => offset(l.Adresse + i)).ToArray();
        if (!LecteurDuScore.CoeurReleve(coeur)) return null;
        return AdressesDuCoeur.Places(coeur, l.Adresse, n, banques);
    }

    /// <summary>La valeur brute de la ligne a partir de ses octets (ordre de l'adresse), comme le wrapper la lit. Pur.</summary>
    public static long Brute(string type, IReadOnlyList<int> octets)
    {
        long v = 0;
        var grosBoutiste = type.EndsWith("be", StringComparison.OrdinalIgnoreCase);
        var n = octets.Count;
        for (var i = 0; i < n; i++)
        {
            var o = grosBoutiste ? octets[i] : octets[n - 1 - i];
            v = (v << 8) | (uint)(o & 0xFF);
        }

        return v;
    }

    /// <summary>
    /// La valeur que la ligne publierait maintenant, comme le wrapper (<see cref="WrapperSimule"/>) : masquee, et seulement si sa
    /// condition tient (une ligne « eq 0xFF » ne dit rien d'une valeur 2) ; null sinon. Pur.
    /// </summary>
    public static long? ValeurDEtat(LigneDuMem l, long brute)
    {
        var v = l.Masque is { } m ? brute & m : brute;
        var attendue = l.Valeur ?? 0;
        var masque = l.Masque ?? 0xFFFFFFFF;
        var tient = l.Condition.ToLowerInvariant() switch
        {
            "change" => l.Valeur is null or 0 || v == attendue,
            "increase" or "decrease" => true,
            "eq" or "equal" => v == attendue,
            "neq" or "not_equal" => v != attendue,
            "bit_true" => (v & masque) == masque,
            "bit_false" => (v & masque) == 0,
            _ => false,
        };
        return tient ? v : null;
    }

    /// <summary>
    /// Lit les lignes (une commande par ligne, la plage de ses octets) et rend leurs signaux d'etat. Une ligne sans reponse
    /// (RetroArch occupe, coeur sans memoire, « -1 ») ne donne rien : jamais une valeur inventee.
    /// </summary>
    public static async Task<IReadOnlyList<DeckSignal>> LireAsync(ICommandesRetroArch commandes, IReadOnlyList<LigneDuMem> lignes,
        EpreuveALire epreuve, string systemId, string rom, string? definitionFile, CancellationToken ct)
    {
        var banques = AdressesDuCoeur.FamilleFbneo(epreuve.Coeur) ? AdressesDuCoeur.BanquesDe(epreuve.RomGroup) : [];
        var lus = new Dictionary<(long, string), long?>();
        var signaux = new List<DeckSignal>();
        foreach (var l in lignes)
        {
            var cle = (l.Adresse, l.Type.ToLowerInvariant());
            if (!lus.TryGetValue(cle, out var brute))
            {
                brute = Places(l, epreuve.Coeur, epreuve.Offset, banques) is { } places
                    ? await LireAsync(commandes, places, ct).ConfigureAwait(false) is { } octets ? Brute(l.Type, octets) : null
                    : null;
                lus[cle] = brute;
            }

            if (brute is not { } b || ValeurDEtat(l, b) is not { } valeur) continue;
            var largeur = Math.Max(2, Octets(l.Type) * 2);
            signaux.Add(new DeckSignal(
                Key: CanalParDefaut + "." + l.Action,
                Channel: CanalParDefaut,
                Name: l.Action,
                Address: Hex.Ecrire(l.Adresse),
                Value: valeur,
                RawValueHex: "0x" + b.ToString("X" + largeur.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                Player: l.Joueur,
                Frame: null,
                Source: Source,
                SystemId: systemId,
                Rom: rom,
                DefinitionFile: definitionFile,
                Ts: DateTime.UtcNow));
        }

        return signaux;
    }

    /// <summary>Les octets aux places donnees, dans leur ordre : une commande pour la plage qu'elles couvrent ; null sans reponse.</summary>
    private static async Task<IReadOnlyList<int>?> LireAsync(ICommandesRetroArch commandes, long[] places, CancellationToken ct)
    {
        if (places.Length == 0) return null;
        var bas = places.Min();
        var n = (int)(places.Max() - bas + 1);
        if (n > PlageMax)
        {
            var un = new List<int>(places.Length);
            foreach (var p in places)
            {
                if (await LireAsync(commandes, [p], ct).ConfigureAwait(false) is not { Count: 1 } o) return null;
                un.Add(o[0]);
            }

            return un;
        }

        var commande = "READ_CORE_RAM " + bas.ToString("x", CultureInfo.InvariantCulture) + " " + n.ToString(CultureInfo.InvariantCulture);
        var bloc = LecteurDuScore.OctetsDeLaReponse(await commandes.DemanderAsync(commande, ct).ConfigureAwait(false), n);
        if (bloc is null) return null;
        return places.Select(p => bloc[(int)(p - bas)]).ToList();
    }
}

/// <summary>
/// Le message <c>deck.signals</c> : des valeurs d'etat lues par l'API (<c>Reason</c> : state-loaded, run-start). Le Deck les
/// applique comme celles de l'instantane (<c>FromSnapshot</c>) : la valeur, jamais l'evenement.
/// </summary>
public sealed record DeckSignals(string LaunchId, string Reason, IReadOnlyList<DeckSignal> Signals, long Revision);

/// <summary>
/// LE DECLENCHEUR DES VALEURS DE DEPART (voir <see cref="DeckValeursDeDepart"/>) : l'etat de depart charge
/// (<c>speedrun.etat.charge</c>, premiere tentative et relances) et le depart du chrono (<c>speedrun.started</c>, qui couvre
/// aussi un depart sans etat). Seulement pour un lancement avec Deck pose, pendant une epreuve. Les commandes de RetroArch
/// sont celles de la session des epreuves (port 55355, une socket par demande : rien ne se croise avec ses releves).
/// </summary>
public sealed class DeckValeursDeDepartService : IHostedService, IDisposable
{
    public const string RaisonEtat = "state-loaded", RaisonDepart = "run-start";

    private readonly IEventBus _bus;
    private readonly DeckStateService _etat;
    private readonly ICommandesRetroArch _commandes;
    private readonly ILogger<DeckValeursDeDepartService>? _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private CancellationTokenSource? _lecture;

    /// <summary>L'epreuve en cours (la session des speedruns) ; remplacable par les essais.</summary>
    internal Func<EpreuveALire?> Epreuve { get; set; }

    public DeckValeursDeDepartService(IEventBus bus, DeckStateService etat, SpeedrunSessionService? speedrun = null,
        ILogger<DeckValeursDeDepartService>? journal = null, ICommandesRetroArch? commandes = null)
    {
        _bus = bus;
        _etat = etat;
        _journal = journal;
        _commandes = commandes ?? new CommandesRetroArchUdp();
        Epreuve = () => speedrun?.EpreuveEnCours is { } r ? EpreuveALire.De(r) : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        lock (_verrou) _lecture?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        lock (_verrou) _lecture?.Cancel();
    }

    private void Recevoir(EventEnvelope e)
    {
        var raison = e.Type switch
        {
            "speedrun.etat.charge" => RaisonEtat,
            "speedrun.started" => RaisonDepart,
            _ => null,
        };
        if (raison is null) return;
        CancellationTokenSource cts;
        lock (_verrou)
        {
            // une lecture plus recente remplace celle qui court (relance pendant la lecture)
            _lecture?.Cancel();
            _lecture = cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        }

        _ = Task.Run(async () =>
        {
            try { await LireAsync(raison, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _journal?.LogDebug(ex, "Deck : valeurs de depart non lues ({Raison}).", raison); }
        });
    }

    /// <summary>Lit et publie les valeurs de depart ; rend le nombre de signaux publies (0 : rien a faire, ou rien de lu).</summary>
    public async Task<int> LireAsync(string raison, CancellationToken ct)
    {
        if (_etat.ContexteDeDepart() is not { AvecDeck: true } partie || Epreuve() is not { } epreuve) return 0;
        var mem = partie.DefinitionFile is { Length: > 0 } d && File.Exists(d) ? d : epreuve.Mem;
        if (string.IsNullOrEmpty(mem) || !File.Exists(mem)) return 0;
        var semantique = partie.Semantique is { Status: "loaded" } s ? s
            : Smem.Semantique(partie.LaunchId, partie.SystemId, partie.Rom, null, mem, RetroBatPaths.RamResourcesRoot, 0);
        if (semantique.Status != "loaded" || semantique.Signals.Count == 0) return 0;

        var lignes = DeckValeursDeDepart.LignesARelever(semantique.Signals, LignesDuMem.Lire(await File.ReadAllTextAsync(mem, ct).ConfigureAwait(false)));
        if (lignes.Count == 0) return 0;
        var depuis = DateTime.UtcNow;
        var lus = await DeckValeursDeDepart.LireAsync(_commandes, lignes, epreuve with { Coeur = epreuve.Coeur ?? partie.Core }, partie.SystemId, partie.Rom,
            mem, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var publies = _etat.PoserLesValeursDeDepart(partie.LaunchId, lus, depuis, raison);
        _journal?.LogInformation("Deck : valeurs de depart de {Defi} ({Raison}) : {Lignes} ligne(s) du .SMEM, {Lues} lue(s), {Publiees} publiee(s){Detail}.",
            epreuve.ChallengeId, raison, lignes.Count, lus.Count, publies,
            lus.Count == 0 ? "" : " : " + string.Join(", ", lus.Select(x => x.Name + " " + x.Address + "=" + x.Value)));
        return publies;
    }
}
