using System.Globalization;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE MOTEUR DES PROFILS DE STATISTIQUES (lot 2 du CDC des statistiques) : il lit les signaux d'un profil toutes les 80 ms
/// par <c>READ_CORE_RAM</c>, et en tire les compteurs par les regles de la liste fermee
/// (<see cref="MultiplayerStatsProfil.Regles"/>). Meme lecture que le suivi d'avant les profils : une valeur n'est retenue
/// que lue deux fois de suite (sauf un signal sans confirmation, les manches), une lecture manquee ne compte rien.
/// </summary>
public sealed class MultiplayerStatsMoteur
{
    private readonly ICommandesRetroArch _commandes;
    private readonly TimeSpan _periode;

    public MultiplayerStatsMoteur(ICommandesRetroArch commandes, TimeSpan? periode = null)
    {
        _commandes = commandes;
        _periode = periode ?? TimeSpan.FromMilliseconds(80);
    }

    /// <summary>Suit le profil jusqu'a l'annulation (la fin de la partie). Le suivi se lit en cours de route (le rapport de l'invite).</summary>
    public MultiplayerStatsSuivi Suivre(MultiplayerStatsProfil profil, IReadOnlyList<int> places, CancellationToken ct)
    {
        var suivi = new MultiplayerStatsSuivi(profil, places);
        suivi.Fin = profil.LitLAdversaire && places.Count != 2
            ? Task.FromResult(suivi.Instantane())
            : BouclerAsync(suivi, ct);
        return suivi;
    }

    private async Task<MultiplayerStatsResultat> BouclerAsync(MultiplayerStatsSuivi suivi, CancellationToken ct)
    {
        var profil = suivi.Profil;
        var places = suivi.Places;
        var retenues = new Dictionary<(string, int), int?>();   // la derniere valeur RETENUE de chaque signal
        var brutes = new Dictionary<(string, int), int?>();     // la derniere lecture, confirmee ou non
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nouvelles = new Dictionary<(string, int), int?>();
                foreach (var (nom, signal) in profil.Signals)
                {
                    foreach (var place in places)
                    {
                        var lue = await LireAsync(signal.Decalage(place), signal, ct).ConfigureAwait(false);
                        var cle = (nom, place);
                        nouvelles[cle] = signal.Confirm ? (lue is { } v && v == brutes.GetValueOrDefault(cle) ? v : null) : lue;
                        brutes[cle] = lue;
                    }
                }

                suivi.Appliquer(nouvelles, retenues);
                foreach (var (cle, valeur) in nouvelles)
                {
                    retenues[cle] = valeur ?? retenues.GetValueOrDefault(cle);
                }

                await Task.Delay(_periode, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return suivi.Instantane();
    }

    private async Task<int?> LireAsync(long? decalage, MultiplayerStatsSignal signal, CancellationToken ct)
    {
        var octets = signal.Bytes;
        if (decalage is null)
        {
            return null;
        }

        var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {decalage.Value:x} {octets}", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (morceaux is null || morceaux.Length < 2 + octets || morceaux[0] != "READ_CORE_RAM")
        {
            return null;
        }

        var lus = new List<int>(octets);
        for (var i = 0; i < octets; i++)
        {
            if (!int.TryParse(morceaux[2 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var o))
            {
                return null;
            }

            lus.Add(o);
        }

        return signal.Valeur(lus);
    }
}

/// <summary>Un suivi en cours : ses compteurs, lisibles a tout moment.</summary>
public sealed class MultiplayerStatsSuivi
{
    private readonly object _verrou = new();
    private readonly Dictionary<int, Dictionary<string, long>> _compteurs;
    private readonly Dictionary<int, HashSet<string>> _signauxLus;

    internal MultiplayerStatsSuivi(MultiplayerStatsProfil profil, IReadOnlyList<int> places)
    {
        Profil = profil;
        Places = places;
        _compteurs = places.ToDictionary(p => p, _ => profil.Counters.Keys.ToDictionary(nom => nom, _ => 0L, StringComparer.Ordinal));
        _signauxLus = places.ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal));
    }

    public MultiplayerStatsProfil Profil { get; }

    public IReadOnlyList<int> Places { get; }

    /// <summary>La fin du suivi : le resultat a la fin de la partie.</summary>
    public Task<MultiplayerStatsResultat> Fin { get; internal set; } = Task.FromResult<MultiplayerStatsResultat>(null!);

    /// <summary>Ce qui est compte a cet instant.</summary>
    public MultiplayerStatsResultat Instantane()
    {
        lock (_verrou)
        {
            return new MultiplayerStatsResultat(
                Profil,
                _compteurs.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, long>) new Dictionary<string, long>(p.Value)),
                _signauxLus.Where(p => Profil.Signals.Keys.All(p.Value.Contains)).Select(p => p.Key).ToHashSet());
        }
    }

    /// <summary>
    /// Arrete les comptes : la rencontre est jouee (vainqueur designe). Le jeu tourne encore le temps de montrer la victoire,
    /// et le gagnant y continue seul (contre l'ordinateur dans SF2CE, en solo dans TGM2) : rien de cela ne compte.
    /// </summary>
    public void Figer()
    {
        lock (_verrou)
        {
            _fige = true;
        }
    }

    private bool _fige;

    /// <summary>Un tour de lecture : <paramref name="nouvelles"/> (null = rien de retenu ce tour), apres <paramref name="avant"/>.</summary>
    /// <summary>Jusqu'a quand les regles qui lisent l'adversaire se taisent (le calme apres un changement de manche).</summary>
    private DateTime _calmeJusqua = DateTime.MinValue;

    internal void Appliquer(IReadOnlyDictionary<(string Signal, int Place), int?> nouvelles, IReadOnlyDictionary<(string Signal, int Place), int?> avant)
    {
        lock (_verrou)
        {
            if (_fige)
            {
                return;
            }

            // LE CALME APRES UNE MANCHE (KOF '98, 28/10) : entre deux manches, la vie se recharge par a-coups (103 un instant,
            // puis 34) ; une baisse lue la n'est pas un coup.
            if (Profil.SettleAfterRoundMs > 0 && Profil.Counters.Values.Select(c => c.Rounds).OfType<string>().Distinct().Any(manches =>
                    Places.Any(place => avant.GetValueOrDefault((manches, place)) is { } a && nouvelles.GetValueOrDefault((manches, place)) is { } b && a != b)))
            {
                _calmeJusqua = DateTime.UtcNow.AddMilliseconds(Profil.SettleAfterRoundMs);
            }

            var calme = DateTime.UtcNow < _calmeJusqua;

            foreach (var ((signal, place), valeur) in nouvelles)
            {
                if (valeur is not null)
                {
                    _signauxLus[place].Add(signal);
                }
            }

            foreach (var (nom, compteur) in Profil.Counters)
            {
                var signal = Profil.Signals[compteur.Signal];
                foreach (var place in Places)
                {
                    var adversaire = Places.FirstOrDefault(p => p != place);
                    if (calme && MultiplayerStatsProfil.LitLAdversaireSelon(compteur.Rule))
                    {
                        continue;
                    }

                    switch (compteur.Rule)
                    {
                        case MultiplayerStatsProfil.RegleBaisseAdverse or MultiplayerStatsProfil.RegleSommeBaisseAdverse
                            or MultiplayerStatsProfil.ReglePlusGrandeBaisseAdverse:
                            if (Baisse(signal, avant.GetValueOrDefault((compteur.Signal, adversaire)), nouvelles.GetValueOrDefault((compteur.Signal, adversaire))) is { } baisse)
                            {
                                _compteurs[place][nom] = compteur.Rule switch
                                {
                                    MultiplayerStatsProfil.RegleBaisseAdverse => _compteurs[place][nom] + 1,
                                    MultiplayerStatsProfil.RegleSommeBaisseAdverse => _compteurs[place][nom] + baisse,
                                    _ => Math.Max(_compteurs[place][nom], baisse),
                                };
                            }
                            break;

                        case MultiplayerStatsProfil.RegleAtteintAdverse:
                            if (compteur.Cible(signal) is { } cible
                                && avant.GetValueOrDefault((compteur.Signal, adversaire)) is { } precedente && precedente != cible
                                && nouvelles.GetValueOrDefault((compteur.Signal, adversaire)) == cible)
                            {
                                _compteurs[place][nom]++;
                            }
                            break;

                        case MultiplayerStatsProfil.RegleMancheGagneeSiPlein:
                            // Une manche gagnee, la vie pleine (lue ce tour, sinon la derniere retenue) : un perfect.
                            if (compteur.Rounds is { } manches
                                && avant.GetValueOrDefault((manches, place)) is { } m && nouvelles.GetValueOrDefault((manches, place)) is { } n && n == m + 1
                                && (nouvelles.GetValueOrDefault((compteur.Signal, place)) ?? avant.GetValueOrDefault((compteur.Signal, place))) == signal.Full)
                            {
                                _compteurs[place][nom]++;
                            }
                            break;

                        case MultiplayerStatsProfil.RegleMaximum:
                            if (nouvelles.GetValueOrDefault((compteur.Signal, place)) is { } valeurMax && avant.ContainsKey((compteur.Signal, place)))
                            {
                                _compteurs[place][nom] = Math.Max(_compteurs[place][nom], valeurMax);
                            }
                            break;

                        case MultiplayerStatsProfil.RegleAtteint:
                            if (compteur.Cible(signal) is { } atteinte
                                && avant.GetValueOrDefault((compteur.Signal, place)) is { } d && d != atteinte
                                && nouvelles.GetValueOrDefault((compteur.Signal, place)) == atteinte)
                            {
                                _compteurs[place][nom]++;
                            }
                            break;

                        case MultiplayerStatsProfil.RegleHausse or MultiplayerStatsProfil.RegleSommeHausse or MultiplayerStatsProfil.RegleHausseDAuMoins
                            or MultiplayerStatsProfil.ReglePlusGrandeHausse:
                            // La valeur de la place elle-meme : une hausse entre deux valeurs retenues (une baisse ne compte rien).
                            if (avant.GetValueOrDefault((compteur.Signal, place)) is { } a && nouvelles.GetValueOrDefault((compteur.Signal, place)) is { } b && b > a)
                            {
                                _compteurs[place][nom] = compteur.Rule switch
                                {
                                    MultiplayerStatsProfil.RegleSommeHausse => _compteurs[place][nom] + (b - a),
                                    MultiplayerStatsProfil.RegleHausseDAuMoins => _compteurs[place][nom] + (b - a >= compteur.Cible(signal) ? 1 : 0),
                                    MultiplayerStatsProfil.ReglePlusGrandeHausse => Math.Max(_compteurs[place][nom], b - a),
                                    _ => _compteurs[place][nom] + 1,
                                };
                            }
                            break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// La baisse d'une valeur adverse entre deux valeurs retenues, ou null : un passage au K.O. compte la vie qui restait
    /// (bornee a la vie pleine), une hausse ou une valeur deja au K.O. ne compte rien.
    /// </summary>
    private static long? Baisse(MultiplayerStatsSignal signal, int? precedente, int? actuelle)
    {
        if (precedente is not { } p || actuelle is not { } a || a == p || (signal.Ko is { } ko && p == ko))
        {
            return null;
        }
        if (signal.Ko is { } k && a == k)
        {
            return Math.Clamp(p, 0, signal.Full ?? p);
        }
        // LA VIE PASSE SOUS ZERO (KOF '98, 28/10) : l'octet repart par le haut (7, puis 253). Au-dessus de la vie pleine, c'est
        // le K.O. : le dernier coup compte ce qui restait.
        if (signal.Full is { } plein && a > plein && p <= plein)
        {
            return Math.Clamp(p, 0, plein);
        }
        return a < p ? p - a : null;
    }
}
