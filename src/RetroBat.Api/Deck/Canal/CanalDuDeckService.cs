using System.Diagnostics;
using System.Runtime.Versioning;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// LA LECTURE DU CANAL ET <c>deck.live</c> (API 1.9.211, CDC Canal v0.1, decisions du user du 22/11).
///
/// Quand un Deck est lance pour un jeu qui a un profil d'affichage (<see cref="CanalDuDeck"/>), l'API cherche le canal du wrapper
/// <c>Local\NelfePlay.Canal.&lt;pid&gt;</c> parmi les processus RetroArch, l'ouvre en lecture seule et publie au Deck un message
/// compact <see cref="DeckLive"/> a 30 Hz au plus, seulement sur changement (plus un battement par seconde). Un changement d'epoch
/// (etat charge, retour arriere) est publie tout de suite : le Deck vide ses tables. Un canal ferme ou coupe (netplay) se dit une
/// fois (<c>Active</c> faux) et le Deck reprend le releve et l'extrapolation.
///
/// AFFICHAGE SEULEMENT : le service ne lit rien d'autre, n'ecrit rien dans le jeu, ne touche ni score, ni regle, ni passeport.
/// Un vieux wrapper (pas de canal) : le service cherche toutes les 1,5 s tant que la partie dure et ne publie rien ; sans Deck pose
/// il ne fait rien du tout. Hors Windows ou sans profil : rien.
///
/// Essais sans jeu : la variable d'environnement <c>NELFEPLAY_CANAL_PID</c> (pid du simulateur <c>tools\CanalSimulateur</c>) et
/// <c>NELFEPLAY_CANAL_ROM</c> (ROM du profil, ddpdojblka par defaut) font lire ce canal sans partie ni Deck pose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CanalDuDeckService : IHostedService, IDisposable
{
    /// <summary>Au plus 30 messages par seconde.</summary>
    public const int PeriodeMs = 33;

    /// <summary>Un battement par seconde quand rien ne change (jeu en pause) : le Deck sait que le canal vit.</summary>
    public const int BattementMs = 1000;

    /// <summary>Intervalle de recherche du canal tant qu'il n'est pas ouvert.</summary>
    public const int RechercheMs = 1500;

    private readonly IEventBus _bus;
    private readonly ILogger<CanalDuDeckService>? _journal;
    private CancellationTokenSource? _arret;
    private Task? _boucle;

    public CanalDuDeckService(IEventBus bus, ILogger<CanalDuDeckService>? journal = null)
    {
        _bus = bus;
        _journal = journal;
    }

    /// <summary>Les messages publies depuis le demarrage (diagnostic, tests).</summary>
    public long Publies => Interlocked.Read(ref _publies);
    private long _publies;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _arret = new CancellationTokenSource();
        _boucle = Task.Run(() => BoucleAsync(_arret.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret?.Cancel();
        return _boucle is null ? Task.CompletedTask : Task.WhenAny(_boucle, Task.Delay(1000, cancellationToken));
    }

    public void Dispose() => _arret?.Dispose();

    private (int Pid, string Rom)? Simule()
    {
        var pid = Environment.GetEnvironmentVariable("NELFEPLAY_CANAL_PID");
        if (!int.TryParse(pid, out var p) || p <= 0) return null;
        var rom = Environment.GetEnvironmentVariable("NELFEPLAY_CANAL_ROM");
        return (p, string.IsNullOrWhiteSpace(rom) ? "ddpdojblka" : rom.Trim());
    }

    private async Task BoucleAsync(CancellationToken ct)
    {
        CanalLecteur? lecteur = null;
        CanalAffichageCalcul? calcul = null;
        string rom = "";
        DeckLive? dernier = null;
        var dernierAt = DateTime.MinValue;
        var dernierEpoch = uint.MaxValue;
        var etatDit = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (lecteur is null)
                {
                    etatDit = false;
                    dernier = null;
                    (lecteur, calcul, rom) = Chercher();
                    await Task.Delay(lecteur is null ? RechercheMs : 1, ct).ConfigureAwait(false);
                    continue;
                }

                // le Deck est retire ou la partie finie : on lache le canal (le wrapper le garde tant que le jeu tourne)
                if (Simule() is null && CanalDuDeck.RomDemandee() is null)
                {
                    Publier(calcul!.Inactif("closed"), ref dernier, ref dernierAt);
                    Lacher(ref lecteur, ref calcul);
                    continue;
                }

                if (!lecteur.Lire(out var bloc))
                {
                    // un autre .VIEW a ete charge ? sinon le canal ne repond plus
                    if (lecteur.Recharger())
                    {
                        calcul = Calcul(lecteur, rom);
                        if (calcul is null) { Lacher(ref lecteur, ref calcul); }
                    }
                    else
                    {
                        Publier(calcul!.Inactif("lost"), ref dernier, ref dernierAt);
                        Lacher(ref lecteur, ref calcul);
                    }

                    await Task.Delay(PeriodeMs, ct).ConfigureAwait(false);
                    continue;
                }

                if (bloc.Etat != CanalFormat.EtatActif)
                {
                    // coupe (netplay) ou ferme (fin du jeu) : on le dit une fois, puis on cherche un autre canal
                    if (!etatDit)
                    {
                        Publier(calcul!.Inactif(bloc.Etat == CanalFormat.EtatCoupeNetplay ? "netplay" : "closed"), ref dernier, ref dernierAt);
                        etatDit = true;
                    }

                    if (bloc.Etat == CanalFormat.EtatFerme) Lacher(ref lecteur, ref calcul);
                    await Task.Delay(RechercheMs, ct).ConfigureAwait(false);
                    continue;
                }

                etatDit = false;
                var message = calcul!.Convertir(bloc);
                var maintenant = DateTime.UtcNow;
                var epochChange = bloc.Epoch != dernierEpoch;
                dernierEpoch = bloc.Epoch;
                if (dernier is null || epochChange || CanalAffichageCalcul.Different(dernier, message) || (maintenant - dernierAt).TotalMilliseconds >= BattementMs)
                    Publier(message, ref dernier, ref dernierAt);
                await Task.Delay(PeriodeMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                // jamais d'erreur visible : le canal est un confort d'affichage
                _journal?.LogDebug(ex, "Canal du Deck : lecture abandonnee.");
                Lacher(ref lecteur, ref calcul);
                try { await Task.Delay(RechercheMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        lecteur?.Dispose();
    }

    private void Publier(DeckLive message, ref DeckLive? dernier, ref DateTime dernierAt)
    {
        dernier = message;
        dernierAt = DateTime.UtcNow;
        Interlocked.Increment(ref _publies);
        _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.live", Payload = message });
    }

    private void Lacher(ref CanalLecteur? lecteur, ref CanalAffichageCalcul? calcul)
    {
        lecteur?.Dispose();
        lecteur = null;
        calcul = null;
    }

    private (CanalLecteur?, CanalAffichageCalcul?, string) Chercher()
    {
        var simule = Simule();
        var rom = simule?.Rom ?? CanalDuDeck.RomDemandee();
        if (rom is null || CanalProfilAffichage.Pour(rom) is null) return (null, null, "");
        foreach (var pid in PidsCandidats(simule?.Pid))
        {
            var l = CanalLecteur.Ouvrir(pid);
            if (l is null) continue;
            var calcul = Calcul(l, rom);
            if (calcul is null) { l.Dispose(); continue; }
            _journal?.LogInformation("Canal du Deck : ouvert pour {Rom} (pid {Pid}, vue {Vue}).", rom, pid, l.Descripteur.Nom);
            return (l, calcul, rom);
        }

        return (null, null, rom);
    }

    private static CanalAffichageCalcul? Calcul(CanalLecteur l, string rom)
    {
        var profil = CanalProfilAffichage.Pour(rom) ?? CanalProfilAffichage.PourLaVue(l.Descripteur.Nom);
        if (profil is null) return null;
        var c = new CanalAffichageCalcul(profil, l.Descripteur, rom);
        return c.Utile ? c : null;
    }

    private static IEnumerable<int> PidsCandidats(int? simule)
    {
        if (simule is { } s) { yield return s; yield break; }
        Process[] processus;
        try { processus = Process.GetProcessesByName("retroarch"); }
        catch { yield break; }
        foreach (var p in processus)
        {
            int id;
            try { id = p.Id; } catch { continue; }
            finally { p.Dispose(); }
            yield return id;
        }
    }
}
