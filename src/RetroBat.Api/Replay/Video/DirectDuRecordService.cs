using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE PREMIER RANG QUI TOMBE, EN DIRECT (CDC video V2, §5.8 ; decisions du 14 octobre 2026 : au depassement, accord
/// tacite, YouTube Live directement, le direct est la copie du film).
/// <list type="bullet">
/// <item>Au lancement d'une partie NelfePlay, la borne lit le premier rang du classement du jeu (celui du panneau de
/// classement : regle principale, classement mondial).</item>
/// <item>Quand le score en direct passe devant, elle demande le direct au site, qui verifie, ouvre un direct YouTube
/// public et previent tout le monde. La borne y envoie le film que RetroArch tourne deja, jusqu'a la fin du film ou de
/// la partie, puis le dit au site.</item>
/// <item>Depuis le 15 octobre 2026, l'envoi passe par <c>NelfePlay.LiveStreaming.exe</c> (<see cref="LanceurDuDirect"/>),
/// qui redresse les jeux verticaux ; sans lui, la recopie telle quelle (<see cref="RelaisDuFilm"/>), qui ne le peut
/// pas.</item>
/// <item>Pas de direct sans film (capture coupee ou impossible), pour un jeu vertical filme couche sans l'exe (YouTube
/// ne le redresserait pas), pour un contre-la-montre, ni pour un jeu a modes (le mode joue n'est connu qu'a la
/// fin).</item>
/// </list>
/// Un essai (<c>Video:Live:Test</c>, au lancement de l'API) ouvre un direct non repertorie que seul le joueur voit
/// annonce ; <c>Video:Live:TestThreshold</c> remplace le score a battre.
/// </summary>
public sealed class DirectDuRecordService : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly LeaderboardClient _classement;
    private readonly NelfePlayAgentService _agent;
    private readonly RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider _wrapper;
    private readonly NelfePlayScoringCollectionSyncService? _collection;
    private readonly PartieNelfePlayService? _partie;
    private readonly ReplayCaptureService _capture;
    private readonly ReplayVideoPublisher _site;
    private readonly ILogger<DirectDuRecordService> _journal;
    private readonly bool _actif;
    private readonly bool _exe;
    private volatile bool _essai;
    private long? _seuilEssai;

    private readonly object _verrou = new();
    private int _generation;
    private string _romGroup = "";
    private string _regle = "";
    private long? _aBattre;
    private bool _declenche;
    private bool _runTermine;
    private CancellationTokenSource? _relais;
    private IDisposable? _abonnement;

    public DirectDuRecordService(IEventBus bus, ApiContext contexte, LeaderboardClient classement, NelfePlayAgentService agent,
        RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider wrapper, ReplayCaptureService capture,
        ReplayVideoPublisher site, IConfiguration configuration, ILogger<DirectDuRecordService> journal,
        NelfePlayScoringCollectionSyncService? collection = null, PartieNelfePlayService? partie = null)
    {
        _bus = bus;
        _contexte = contexte;
        _classement = classement;
        _agent = agent;
        _wrapper = wrapper;
        _capture = capture;
        _site = site;
        _journal = journal;
        _collection = collection;
        _partie = partie;
        _actif = configuration.GetValue("Video:Live:Enabled", true);
        _exe = configuration.GetValue("Video:Live:Exe", true);
        _essai = configuration.GetValue("Video:Live:Test", false);
        _seuilEssai = configuration.GetValue<long?>("Video:Live:TestThreshold");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_actif)
        {
            _journal.LogInformation("Direct : desactive (Video:Live:Enabled).");
            return Task.CompletedTask;
        }
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        if (_essai) _journal.LogInformation("Direct : mode essai (non repertorie, seul le joueur prevenu).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        lock (_verrou) _relais?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// L'essai, jusqu'au prochain demarrage (route locale <c>live-test</c>) : direct non repertorie annonce au seul
    /// joueur, ouvert quand le score passe <paramref name="seuil"/> (le premier rang si null). Vaut pour le jeu suivant.
    /// </summary>
    public void Essayer(bool actif, long? seuil)
    {
        lock (_verrou)
        {
            _essai = actif;
            _seuilEssai = actif ? seuil : null;
        }
        _journal.LogInformation("Direct : essai {Etat}{Seuil}.", actif ? "arme" : "desarme",
            actif && seuil is long s ? " (seuil " + s + ")" : "");
    }

    /// <summary>Faut-il ouvrir le direct ? Le score passe devant le premier rang, une fois par partie certifiee. Pur.</summary>
    internal static bool PasseDevant(long? aBattre, long score, int joueur, bool dejaDeclenche, bool runTermine)
        => aBattre is long seuil && !dejaDeclenche && !runTermine && joueur == 1 && score > seuil;

    /// <summary>
    /// Les regles d'un jeu ou le premier rang se lit pendant la partie : 1CC, 1LC, 1CC MULTI. Un jeu a modes en a
    /// d'autres, et le mode joue n'est connu qu'a la fin. Pur.
    /// </summary>
    internal static bool SansModes(IReadOnlyList<string> regles)
        => regles.All(r => r is "1cc" or "1lc" or "1cc-multi");

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    int g;
                    lock (_verrou)
                    {
                        g = ++_generation;
                        _romGroup = "";
                        _regle = "";
                        _aBattre = null;
                        _declenche = false;
                        _runTermine = false;
                        _relais?.Cancel();
                    }
                    _ = Task.Run(() => ArmerAsync(g));
                    break;

                case "score.live.changed":
                    if (Nombre(e.Payload, "Score") is not { } score) return;
                    var joueur = (int)(Nombre(e.Payload, "Player") ?? 1);
                    int gen;
                    lock (_verrou)
                    {
                        if (!PasseDevant(_aBattre, score, joueur, _declenche, _runTermine)) return;
                        _declenche = true;
                        gen = _generation;
                    }
                    _ = Task.Run(() => DeclencherAsync(gen, score));
                    break;

                case "scoring.run.ended":
                    lock (_verrou) _runTermine = true;
                    break;

                case "ui.game.ended":
                    lock (_verrou)
                    {
                        _generation++;
                        _aBattre = null;
                        _relais?.Cancel();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>Au lancement : le premier rang du classement du jeu, si la partie est NelfePlay.</summary>
    private async Task ArmerAsync(int g)
    {
        try
        {
            // La partie NelfePlay se decide au lancement, en quelques secondes.
            for (var i = 0; i < 30 && _partie?.EstNelfePlay != true; i++)
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (g != Volatile.Read(ref _generation)) return;
            }
            if (_partie?.EstNelfePlay != true) return;
            if (_contexte.Ui.Running is not { } jeu) return;

            var brut = Path.GetFileNameWithoutExtension(jeu.GamePath.Length > 0 ? jeu.GamePath : jeu.GameName);
            if (brut.Length == 0 || jeu.SystemId.Length == 0) return;
            var def = _wrapper.ResolveDefinitionFor(brut, jeu.SystemId);
            if (!(def.AliasMatched || def.DefinitionExists) || string.IsNullOrWhiteSpace(def.Rom)) return;
            var romGroup = def.Rom.Trim().ToLowerInvariant();

            var regles = _collection?.ReglesOuvertes(romGroup) ?? Array.Empty<string>();
            if (!SansModes(regles))
            {
                _journal.LogInformation("Direct : {Jeu} a plusieurs modes, pas de direct (le mode joue n'est connu qu'a la fin).", romGroup);
                return;
            }
            var regle = LeaderboardClient.ReglePrincipale(regles);
            var classement = await _classement.MondeAsync(romGroup, _agent.Status.Pseudo ?? "", CancellationToken.None, regle)
                .ConfigureAwait(false);
            var tete = classement.Lignes.OrderBy(l => l.Rang).FirstOrDefault();
            if (tete is null && _seuilEssai is null) return;
            if (tete is { PlusBasEstMieux: true }) return;

            lock (_verrou)
            {
                if (g != _generation) return;
                _romGroup = romGroup;
                _regle = regle.Length > 0 ? regle : "1cc";
                _aBattre = _seuilEssai ?? tete!.Valeur;
            }
            _journal.LogInformation("Direct : {Jeu} ({Regle}) arme, premier rang {Valeur} ({Joueur}).",
                romGroup, regle, _seuilEssai ?? tete!.Valeur, tete?.Joueur ?? "essai");
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct : premier rang illisible, pas de direct pour cette partie.");
        }
    }

    /// <summary>
    /// L'envoi a utiliser : l'exe du direct s'il est la et permis, sinon la recopie telle quelle, qui ne sait pas
    /// redresser un jeu vertical (null : pas de direct possible). Pur.
    /// </summary>
    internal static string? Envoi(string? exe, bool exePermis, int? rotation)
        => exe is not null && exePermis ? "exe" : rotation is 1 or 3 ? null : "relais";

    /// <summary>Le score passe devant : le site ouvre le direct, la borne y envoie le film jusqu'a la fin.</summary>
    private async Task DeclencherAsync(int g, long score)
    {
        string romGroup, regle;
        lock (_verrou)
        {
            if (g != _generation) return;
            romGroup = _romGroup;
            regle = _regle;
        }
        if (_capture.FilmEnCours() is not { } film)
        {
            _journal.LogInformation("Direct : {Jeu} passe devant a {Score}, mais aucun film ne tourne : pas de direct.", romGroup, score);
            return;
        }
        var exe = LanceurDuDirect.Chemin(AppContext.BaseDirectory);
        var envoi = Envoi(exe, _exe, film.Rotation);
        if (envoi is null)
        {
            _journal.LogInformation("Direct : {Jeu} passe devant a {Score}, mais le film est couche (jeu vertical) et l'exe du direct manque : pas de direct.", romGroup, score);
            return;
        }

        var ouvert = await _site.OuvrirLeDirectAsync(romGroup, regle, score, _essai, CancellationToken.None).ConfigureAwait(false);
        // Un seul direct de record a la fois (decision du 15 octobre 2026) : la borne attend son tour dans la file et
        // redemande tant que sa partie et son film durent.
        while (!ouvert.Ok && ouvert.Raison == "en_attente")
        {
            var attente = TimeSpan.FromSeconds(Math.Clamp(ouvert.RedemanderApres ?? 60, 15, 300));
            _journal.LogInformation("Direct : {Jeu} en file d'attente (place {Place}), nouvelle demande dans {Secondes:F0} s.", romGroup,
                ouvert.Position, attente.TotalSeconds);
            await Task.Delay(attente).ConfigureAwait(false);
            bool finie;
            lock (_verrou) finie = g != _generation;
            if (finie || _capture.FilmEnCours() is not { } suite || suite.Fichier != film.Fichier)
            {
                _journal.LogInformation("Direct : {Jeu}, partie finie avant son tour dans la file : pas de direct.", romGroup);
                return;
            }
            ouvert = await _site.OuvrirLeDirectAsync(romGroup, regle, score, _essai, CancellationToken.None).ConfigureAwait(false);
        }
        if (!ouvert.Ok || ouvert.Ingest is null || ouvert.LiveId is null)
        {
            _journal.LogInformation("Direct : {Jeu} passe devant a {Score}, direct refuse par le site ({Raison}).", romGroup, score, ouvert.Raison);
            return;
        }
        _journal.LogInformation("Direct : {Jeu} passe devant a {Score}, direct {Direct} ouvert, envoi par {Envoi}.", romGroup, score,
            ouvert.LiveId, envoi == "exe" ? "l'exe du direct" : "recopie du film");

        using var arret = new CancellationTokenSource();
        lock (_verrou)
        {
            if (g != _generation) arret.Cancel();
            _relais = arret;
        }
        try
        {
            if (envoi == "exe")
            {
                var r = await LanceurDuDirect.DiffuserAsync(exe!, film.Fichier, ouvert.Ingest, film.Rotation ?? 0, _journal, arret.Token)
                    .ConfigureAwait(false);
                _journal.LogInformation("Direct : {Direct} fini ({Fin}, code {Code}, {Secondes:F0} s).", ouvert.LiveId, r.Fin, r.Code,
                    r.Duree.TotalSeconds);
                // L'exe tombe au demarrage (aucun encodeur, Windows trop ancien...) : la recopie telle quelle prend le
                // relais pour un jeu droit, la partie continuant.
                if (r.Code != 0 && r.Duree < TimeSpan.FromSeconds(20) && !arret.IsCancellationRequested && film.Rotation is not (1 or 3))
                {
                    _journal.LogInformation("Direct : {Direct}, l'exe n'a pas tenu : recopie du film.", ouvert.LiveId);
                    envoi = "relais";
                }
            }
            if (envoi == "relais")
            {
                var rapport = await RelaisDuFilm.RecopierAsync(film.Fichier, ouvert.Ingest, _journal, arret.Token).ConfigureAwait(false);
                _journal.LogInformation("Direct : {Direct} fini ({Fin}) : {Images} images, {Son} blocs de son, {Mo:F1} Mo en {Secondes:F0} s.",
                    ouvert.LiveId, rapport.Fin, rapport.Images, rapport.BlocsDeSon, rapport.Octets / 1e6, rapport.Secondes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Jamais l'adresse d'entree dans le journal : elle porte la cle de diffusion.
            _journal.LogWarning("Direct : {Direct} interrompu ({Type} : {Message}).", ouvert.LiveId, ex.GetType().Name, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _journal.LogInformation("Direct : {Direct} arrete avec la partie.", ouvert.LiveId);
        }
        finally
        {
            lock (_verrou)
            {
                if (ReferenceEquals(_relais, arret)) _relais = null;
            }
            await _site.CloreLeDirectAsync(ouvert.LiveId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static long? Nombre(object? payload, string nom)
    {
        try
        {
            var el = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
            return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(nom, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
    }
}
