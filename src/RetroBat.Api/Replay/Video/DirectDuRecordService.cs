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
/// <item>DEPUIS LE 15 OCTOBRE 2026, LE DIRECT S'OUVRE A L'APPROCHE : quand le score en direct atteint 90 % du premier
/// rang, elle demande le direct au site, qui verifie, ouvre un direct YouTube public et previent tout le monde (« sur le
/// point d'etablir un nouveau record »). La borne y envoie le film que RetroArch tourne deja, jusqu'a la fin du film ou
/// de la partie, puis le dit au site. Quand le score passe devant, elle le signale (<see cref="ReplayVideoPublisher.SignalerLeRecordAsync"/>) :
/// le site note le moment et previent une seconde fois. Un record manque efface l'archive du direct ; un record
/// certifie la range dans la playlist « Records en direct ».</item>
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
    /// <summary>Le direct ouvert pour cette partie (pour signaler le record), et le meilleur score du joueur 1.</summary>
    private string? _liveId;
    private long _meilleurScore;
    private bool _recordSignale;
    private int _essaisDeSignal;
    /// <summary>Le classement lu au lancement et le nom du jeu : la carte de fin du direct en est faite.</summary>
    private IReadOnlyList<LeaderboardClient.Ligne> _lignes = Array.Empty<LeaderboardClient.Ligne>();
    private string _nomDuJeu = "";

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

    /// <summary>Le direct s'ouvre a 90 % du record (decision du 15 octobre 2026), arrondi au-dessus, comme le site. Pur.</summary>
    internal static long SeuilDApproche(long record) => (record * 90 + 99) / 100;

    /// <summary>
    /// Faut-il ouvrir le direct ? Le score du joueur 1 atteint 90 % du premier rang, une fois par partie certifiee. Pur.
    /// </summary>
    internal static bool AApproche(long? aBattre, long score, int joueur, bool dejaDeclenche, bool runTermine)
        => aBattre is long record && record > 0 && !dejaDeclenche && !runTermine && joueur == 1 && score >= SeuilDApproche(record);

    /// <summary>Le record tombe : le score du joueur 1 passe devant le premier rang, avant tout continue. Pur.</summary>
    internal static bool PasseDevant(long? aBattre, long score, int joueur, bool runTermine)
        => aBattre is long record && !runTermine && joueur == 1 && score > record;

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
                        _liveId = null;
                        _meilleurScore = 0;
                        _recordSignale = false;
                        _essaisDeSignal = 0;
                        _lignes = Array.Empty<LeaderboardClient.Ligne>();
                        _nomDuJeu = "";
                        _relais?.Cancel();
                    }
                    _ = Task.Run(() => ArmerAsync(g));
                    break;

                case "score.live.changed":
                    if (Nombre(e.Payload, "Score") is not { } score) return;
                    var joueur = (int)(Nombre(e.Payload, "Player") ?? 1);
                    int gen;
                    bool ouvrir, signaler;
                    string? direct;
                    lock (_verrou)
                    {
                        if (joueur == 1 && !_runTermine) _meilleurScore = Math.Max(_meilleurScore, score);
                        ouvrir = AApproche(_aBattre, score, joueur, _declenche, _runTermine);
                        if (ouvrir) _declenche = true;
                        // Le direct ouvert a l'approche : le moment ou le record tombe se signale une fois.
                        signaler = !ouvrir && _liveId is not null && !_recordSignale && PasseDevant(_aBattre, score, joueur, _runTermine);
                        if (signaler) _recordSignale = true;
                        direct = _liveId;
                        gen = _generation;
                    }
                    if (ouvrir) _ = Task.Run(() => DeclencherAsync(gen, score));
                    else if (signaler) _ = Task.Run(() => SignalerAsync(gen, direct!, score));
                    break;

                case "scoring.run.ended":
                    lock (_verrou) _runTermine = true;
                    // Le record est tombe pendant le direct : la carte de fin prend le score final du run.
                    EcrireLaCarte();
                    break;

                case "ui.game.ended":
                    EcrireLaCarte();
                    lock (_verrou)
                    {
                        _generation++;
                        _aBattre = null;
                        _liveId = null;
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
                _lignes = classement.Lignes;
                _nomDuJeu = string.IsNullOrWhiteSpace(jeu.GameName) ? romGroup : jeu.GameName.Trim();
            }
            _journal.LogInformation("Direct : {Jeu} ({Regle}) arme, premier rang {Valeur} ({Joueur}), direct a {Seuil}.",
                romGroup, regle, _seuilEssai ?? tete!.Valeur, tete?.Joueur ?? "essai", SeuilDApproche(_seuilEssai ?? tete!.Valeur));
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct : premier rang illisible, pas de direct pour cette partie.");
        }
    }

    /// <summary>
    /// L'envoi a utiliser : l'exe du direct s'il est la et permis, sinon la recopie telle quelle, qui ne sait ni
    /// redresser un jeu vertical ni agrandir un film a la taille d'origine (null : pas de direct possible). Pur.
    /// </summary>
    internal static string? Envoi(string? exe, bool exePermis, int? rotation, bool origine = false)
        => exe is not null && exePermis ? "exe" : rotation is 1 or 3 || origine ? null : "relais";

    /// <summary>Le film en cours est-il a la taille d'origine (4:4:4) ? Son en-tete est ecrit des le debut.</summary>
    private static bool FilmALaTailleDOrigine(string fichier) => MatroskaEntete.Lire(fichier) is { Origine: true };

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
            _journal.LogInformation("Direct : {Jeu} approche du record a {Score}, mais aucun film ne tourne : pas de direct.", romGroup, score);
            return;
        }
        var exe = LanceurDuDirect.Chemin(AppContext.BaseDirectory);
        var origine = FilmALaTailleDOrigine(film.Fichier);
        var envoi = Envoi(exe, _exe, film.Rotation, origine);
        if (envoi is null)
        {
            _journal.LogInformation("Direct : {Jeu} approche du record a {Score}, mais le film est {Film} et l'exe du direct manque : pas de direct.",
                romGroup, score, origine ? "a la taille d'origine" : "couche (jeu vertical)");
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
            _journal.LogInformation("Direct : {Jeu} approche du record a {Score}, direct refuse par le site ({Raison}).", romGroup, score, ouvert.Raison);
            return;
        }
        _journal.LogInformation("Direct : {Jeu} approche du record a {Score}, direct {Direct} ouvert, envoi par {Envoi}.", romGroup, score,
            ouvert.LiveId, envoi == "exe" ? "l'exe du direct" : "recopie du film");
        // Le direct partage de cette borne se termine : elle ne peut pas alimenter deux directs (16 octobre 2026).
        try { await _bus.PublishAsync(new EventEnvelope { Type = "video.live.record.started" }).ConfigureAwait(false); }
        catch (Exception) { /* sans bus, le passage partage finira avec la partie */ }

        // Le record a pu tomber pendant l'ouverture ou l'attente dans la file : il se signale tout de suite.
        long? dejaTombe = null;
        lock (_verrou)
        {
            if (g == _generation)
            {
                _liveId = ouvert.LiveId;
                if (!_recordSignale && PasseDevant(_aBattre, _meilleurScore, 1, false))
                {
                    _recordSignale = true;
                    dejaTombe = _meilleurScore;
                }
            }
        }
        if (dejaTombe is long tombe) _ = Task.Run(() => SignalerAsync(g, ouvert.LiveId, tombe));

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
                // La carte du classement a la fin si le record tombe (fichier ecrit alors). L'annonce, elle, se joue sur
                // le site avant le direct (video YouTube) : le site ne sert jamais de video.
                try { File.Delete(CheminDeLaCarte); } catch (Exception) { /* absente */ }
                // LES CLIPS DU DIRECT (16 octobre 2026), installes par le Data Pack : « record » avant le jeu, « champion » a
                // la fin quand le record est tombe (l'exe ne le joue qu'avec la carte du classement). Jamais sans leur
                // empreinte attendue, que le site donne.
                var (annonce, optionsDesClips) = Clips(ouvert.Clips);
                var r = await LanceurDuDirect.DiffuserAsync(exe!, film.Fichier, ouvert.Ingest, film.Rotation ?? 0, _journal, arret.Token,
                        annonce: annonce, classement: CheminDeLaCarte, options: optionsDesClips)
                    .ConfigureAwait(false);
                _journal.LogInformation("Direct : {Direct} fini ({Fin}, code {Code}, {Secondes:F0} s).", ouvert.LiveId, r.Fin, r.Code,
                    r.Duree.TotalSeconds);
                // L'exe tombe au demarrage (aucun encodeur, Windows trop ancien...) : la recopie telle quelle prend le
                // relais pour un jeu droit filme agrandi, la partie continuant.
                if (r.Code != 0 && r.Duree < TimeSpan.FromSeconds(20) && !arret.IsCancellationRequested
                    && Envoi(null, false, film.Rotation, origine) == "relais")
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

    /// <summary>
    /// Le record tombe pendant le direct : le site le note et previent une seconde fois. Refuse ou injoignable, le
    /// signal se retente au changement de score suivant, trois fois au plus.
    /// </summary>
    private async Task SignalerAsync(int g, string liveId, long score)
    {
        var pris = await _site.SignalerLeRecordAsync(liveId, score, CancellationToken.None).ConfigureAwait(false);
        if (pris)
        {
            _journal.LogInformation("Direct : {Direct}, le record tombe a {Score}, signale au site.", liveId, score);
            // La carte de fin existe des maintenant ; elle prendra le score final a la fin du run.
            EcrireLaCarte();
            return;
        }
        lock (_verrou)
        {
            if (g == _generation && ++_essaisDeSignal < 3) _recordSignale = false;
        }
        _journal.LogInformation("Direct : {Direct}, le record tombe a {Score}, signal non pris par le site.", liveId, score);
    }

    /// <summary>
    /// L'annonce (clip « record ») et les options des clips d'une alert : chaque clip present sur la borne et accompagne de
    /// son empreinte. Rien sinon.
    /// </summary>
    internal static (string? Annonce, IReadOnlyList<string> Options) Clips(System.Text.Json.Nodes.JsonObject? clips, string? racine = null)
    {
        racine ??= RetroBat.Domain.Paths.RetroBatPaths.PluginRoot;
        string? Chemin(string cle, out string? empreinte)
        {
            var c = clips?[cle] as System.Text.Json.Nodes.JsonObject;
            empreinte = (string?)c?["sha256"];
            var chemin = DirectPartageService.CheminDuClip(racine, (string?)c?["file"]);
            return chemin is not null && File.Exists(chemin) && !string.IsNullOrWhiteSpace(empreinte) ? chemin : null;
        }
        var options = new List<string>();
        var annonce = Chemin("record", out var empreinteRecord);
        if (annonce is not null) options.AddRange(["--annonce-sha256", empreinteRecord!.Trim()]);
        if (Chemin("champion", out var empreinteChampion) is { } champion) options.AddRange(["--clip", champion, "--clip-sha256", empreinteChampion!.Trim()]);
        return (annonce, options);
    }

    /// <summary>Le classement mis a jour, que l'exe du direct montre a la fin quand il existe.</summary>
    private static string CheminDeLaCarte
        => Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "direct-classement.json");

    /// <summary>Une ligne de la carte de fin du direct.</summary>
    internal sealed record LigneDeCarte(int Rang, string Joueur, long Score, bool Nouveau);

    /// <summary>
    /// LE CLASSEMENT MIS A JOUR (fin d'un direct de record, demande du 15 octobre 2026) : le classement lu au lancement,
    /// la ligne du joueur remplacee par son nouveau score, reclasse, 10 lignes au plus (la nouvelle toujours dedans). Pur.
    /// </summary>
    internal static IReadOnlyList<LigneDeCarte> ClassementMisAJour(IReadOnlyList<LeaderboardClient.Ligne> lignes, string joueur,
        long score, int max = 10)
    {
        var autres = lignes.Where(l => !l.CestMoi && !string.Equals(l.Joueur, joueur, StringComparison.OrdinalIgnoreCase))
            .Select(l => (l.Joueur, l.Valeur, Nouveau: false));
        var tous = autres.Append((Joueur: joueur, Valeur: score, Nouveau: true))
            .OrderByDescending(l => l.Valeur).ThenBy(l => l.Nouveau ? 0 : 1).ToList();
        var carte = tous.Select((l, i) => new LigneDeCarte(i + 1, l.Joueur, l.Valeur, l.Nouveau)).ToList();
        var garde = carte.Take(max).ToList();
        if (!garde.Any(l => l.Nouveau)) garde[^1] = carte.First(l => l.Nouveau);
        return garde;
    }

    /// <summary>
    /// Ecrit la carte de fin quand le record est tombe pendant le direct (au signal, puis a la fin du run avec le score
    /// final). Sans record tombe, rien : le direct finit sur le jeu.
    /// </summary>
    private void EcrireLaCarte()
    {
        try
        {
            string jeu, regle, joueur;
            long score;
            IReadOnlyList<LeaderboardClient.Ligne> lignes;
            lock (_verrou)
            {
                if (!_recordSignale || _liveId is null) return;
                jeu = _nomDuJeu;
                regle = _regle;
                score = _meilleurScore;
                lignes = _lignes;
                joueur = string.IsNullOrWhiteSpace(_agent.Status.Pseudo) ? "player" : _agent.Status.Pseudo!;
            }
            var carte = new
            {
                jeu,
                regle,
                lignes = ClassementMisAJour(lignes, joueur, score)
                    .Select(l => new { rang = l.Rang, joueur = l.Joueur, score = l.Score, nouveau = l.Nouveau }),
            };
            var chemin = CheminDeLaCarte;
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin + ".tmp", JsonSerializer.Serialize(carte));
            File.Move(chemin + ".tmp", chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct : carte du classement non ecrite.");
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
