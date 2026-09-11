using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Releve ce qui est joue, et rien d'autre.
///
/// Le but est de mesurer une audience, pas de suivre quelqu'un : ce qui part
/// d'ici est le titre lance, le systeme, et la duree. Aucun identifiant de
/// joueur ne quitte la machine - le serveur reduit sur-le-champ ce qu'il
/// recoit a un pseudonyme dont le sel est detruit chaque mois.
///
/// Deux natures de machine :
///
///  - APPAIREE : le credential de l'appareil sert deja pour le reste de
///    l'API, on s'en sert aussi ici ;
///  - NON APPAIREE : la plupart des installations n'ouvriront jamais de
///    compte, et ce sont justement elles qui font le volume. Elles
///    s'inscrivent donc une fois, sans compte ni adresse, et gardent un
///    secret local. Pourquoi authentifier ce qui est anonyme ? Parce qu'un
///    relais ouvert se remplit tout seul, et que des chiffres gonfles valent
///    moins que pas de chiffres.
///
/// Le relevé s'éteint d'un réglage : une mesure sans interrupteur n'est pas
/// une mesure, c'est une surveillance.
/// </summary>
public sealed class NelfePlayPlayReporter : BackgroundService
{
    /// <summary>
    /// Au-dela de cette taille, on ne calcule pas d'empreinte.
    ///
    /// Une image de CD ou de DVD prend plusieurs secondes a lire entierement,
    /// et ce serait au moment ou le joueur attend que son jeu demarre. On
    /// retombe alors sur le couple systeme + nom, moins sur mais gratuit.
    /// </summary>
    private const long MaxHashBytes = 96L * 1024 * 1024;

    /// <summary>Une partie plus courte n'en est pas une : c'est un jeu lance
    /// puis quitte aussitot, souvent par erreur. La compter fausserait la
    /// duree moyenne, qui est justement ce qui distingue un titre qu'on essaie
    /// d'un titre qu'on joue.</summary>
    private const int MinimumSeconds = 20;

    /// <summary>
    /// En deca de cette duree, le jeu ne s'est pas lance : il a rendu la main
    /// avant meme d'afficher quoi que ce soit. C'est le seul echec qu'une
    /// machine puisse constater seule, et il vaut d'etre dit - un titre qui ne
    /// demarre pas sur une configuration doit se voir sur la fiche.
    ///
    /// Entre ce seuil et <see cref="MinimumSeconds"/>, on ne conclut RIEN :
    /// un joueur peut avoir quitte de lui-meme, et transformer son changement
    /// d'avis en « incompatible » salirait le passeport d'un jeu qui marche.
    /// </summary>
    private const int FailedLaunchSeconds = 5;

    /// <summary>La file d'attente hors ligne ne grandit pas indefiniment : une
    /// machine coupee du reseau pendant des mois ne doit pas remplir son disque
    /// avec des parties que personne n'attend plus.</summary>
    private const int MaxPending = 200;

    private static readonly string StatePath =
        Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ILogger<NelfePlayPlayReporter>? _logger;

    private readonly object _sync = new();
    private readonly Queue<PlayRecord> _pending = new();
    private readonly Queue<EnvironmentRecord> _pendingEnvironments = new();
    private IDisposable? _subscription;
    private Play? _current;
    private string? _anonymousCredential;

    /// <summary>Interrupteur du releve. Coupe, plus rien ne part.</summary>
    public static bool Enabled { get; set; } = true;

    public NelfePlayPlayReporter(
        IEventBus eventBus,
        ApiContext context,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ILogger<NelfePlayPlayReporter>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _httpFactory = httpFactory;
        _devices = devices;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arret normal.
        }
        finally
        {
            _subscription?.Dispose();
        }
    }

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (string.Equals(envelope.Type, "ui.game.started", StringComparison.OrdinalIgnoreCase))
            {
                BeginPlay(envelope);
                return;
            }

            if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                EndPlay();
            }
        }
        catch (Exception ex)
        {
            // Un relevé raté ne doit jamais gêner la partie en cours.
            _logger?.LogDebug(ex, "Play reporting skipped for {EventType}", envelope.Type);
        }
    }

    private void BeginPlay(EventEnvelope envelope)
    {
        // Le jeu se lit dans le CONTEXTE, pas dans la charge de l'evenement.
        //
        // J'avais suppose une charge JSON portant SystemId, GameName et
        // GamePath. Elle n'existe pas : le bus publie un objet anonyme
        // { EventName, RawArgs, Context }, et le jeu vit dans Context.Running.
        // Mon extraction exigeait un JsonElement, recevait un objet .NET, et
        // rendait null a chaque fois - aucune partie n'etait donc enregistree,
        // sans qu'aucune erreur ne le signale.
        //
        // On lit maintenant le contexte typé, ou l'objet ne peut pas mentir sur
        // sa forme : une faute de nom deviendrait une erreur de compilation.
        var running = ResolveRunning(envelope.Payload) ?? _context.Ui.Running;
        if (running is null)
        {
            return;
        }

        var systemId = running.SystemId;
        var gameName = running.GameName;
        var gamePath = running.GamePath;

        if (string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(gameName))
        {
            return;
        }

        lock (_sync)
        {
            // Une nouvelle partie ferme la precedente : Emulationstation ne
            // signale pas toujours la fin quand l'emulateur meurt seul.
            EndPlayLocked();
            // L'emulateur est lu ICI et relu a la fin : au demarrage, le
            // journal de lancement n'est pas toujours analyse, et a la fin le
            // contexte a parfois deja oublie la partie. Prendre les deux
            // occasions evite de perdre l'information par simple mauvais
            // timing.
            _current = new Play(
                systemId,
                gameName,
                gamePath,
                running.Launch?.Emulator,
                running.Launch?.Core,
                Stopwatch.StartNew());
        }
    }

    private void EndPlay()
    {
        lock (_sync)
        {
            EndPlayLocked();
        }
    }

    private void EndPlayLocked()
    {
        var play = _current;
        _current = null;
        if (play is null)
        {
            return;
        }

        var seconds = (int)play.Clock.Elapsed.TotalSeconds;

        QueueEnvironmentLocked(play, seconds);

        if (seconds < MinimumSeconds)
        {
            return;
        }

        // L'empreinte est calculee A LA FIN : au demarrage, elle retarderait le
        // lancement du jeu pour un chiffre dont personne n'a besoin tout de
        // suite.
        var md5 = HashOf(play.GamePath);

        // Identite DECLAREE, en plus de l'empreinte mesuree.
        //
        // Sans elle, aucune partie d'arcade ne resout jamais vers un groupe canonique :
        // la plateforme n'indexe que ce que disent les DAT, et pour l'arcade ce n'est
        // pas une empreinte du fichier - sa gamelist ne porte meme aucun md5. Toute la
        // telemetrie arcade retombait donc sur des cles de repli « systeme:~nom », ce
        // qui eclate les statistiques d'un meme jeu au lieu de les regrouper.
        var sha1 = GamelistIdentity.DeclaredSha1(
            play.SystemId,
            play.GameName,
            System.IO.Path.GetFileNameWithoutExtension(play.GamePath));

        if (_pending.Count >= MaxPending)
        {
            _pending.Dequeue();
        }

        // L'emulateur et le coeur, tels qu'EmulationStation les a lances. La
        // plateforme s'en sert pour ne mettre en vitrine que ce qui tourne sous
        // RetroArch (emulateur « libretro ») : le nom du systeme ne le dit pas,
        // un jeu Model 3 est range sous « arcade » et tourne sous Supermodel.
        // Rien sur la personne : c'est une propriete du jeu tel qu'installe.
        var launch = _context.Ui.Running?.Launch;
        var emulator = FirstNonEmpty(play.Emulator, launch?.Emulator);
        var core = FirstNonEmpty(play.Core, launch?.Core);

        _pending.Enqueue(new PlayRecord(play.SystemId, play.GameName, md5, sha1, seconds, emulator, core));
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        PlayRecord[] batch;
        var hasEnvironments = false;
        lock (_sync)
        {
            hasEnvironments = _pendingEnvironments.Count > 0;
            if (_pending.Count == 0 && !hasEnvironments)
            {
                return;
            }
            batch = _pending.ToArray();
        }

        var credential = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        // Les environnements partent AVANT les parties : ils sont rares, ils
        // n'ont pas de sel a preserver, et une file de parties qui n'avance pas
        // ne doit pas retarder ce que le passeport attend.
        await FlushEnvironmentsAsync(credential, cancellationToken).ConfigureAwait(false);

        if (batch.Length == 0)
        {
            return;
        }

        var sent = 0;
        foreach (var record in batch)
        {
            if (!await PostAsync(credential, record, cancellationToken).ConfigureAwait(false))
            {
                // Le serveur ne repond pas : on garde le reste pour plus tard
                // plutot que de s'acharner.
                break;
            }
            sent++;
        }

        if (sent == 0)
        {
            return;
        }

        lock (_sync)
        {
            for (var i = 0; i < sent && _pending.Count > 0; i++)
            {
                _pending.Dequeue();
            }
        }

        _logger?.LogDebug("Reported {Count} play(s) to NelfePlay", sent);
    }

    /// <summary>
    /// Ce que cette partie apprend sur la compatibilite du titre.
    ///
    /// Seuls les jeux INSTALLES depuis Nelfe Play sont concernes : nous n'avons
    /// rien a dire de la compatibilite d'une ROM que le joueur a apportee
    /// lui-meme, et le serveur n'aurait aucune fiche ou la ranger.
    ///
    /// Une partie normale vaut « ca se lance ». Un abandon immediat vaut « ca
    /// ne se lance pas ». Entre les deux, on se tait : l'ambiguite ne se
    /// resout pas en choisissant la reponse qui arrange.
    /// </summary>
    private void QueueEnvironmentLocked(Play play, int seconds)
    {
        if (seconds >= FailedLaunchSeconds && seconds < MinimumSeconds)
        {
            return;
        }

        var slug = ResolveNelfePlaySlug(play.SystemId, play.GamePath);
        if (string.IsNullOrEmpty(slug))
        {
            return;
        }

        var launch = _context.Ui.Running?.Launch;
        var emulator = FirstNonEmpty(play.Emulator, launch?.Emulator);
        var core = FirstNonEmpty(play.Core, launch?.Core);

        if (_pendingEnvironments.Count >= MaxPending)
        {
            _pendingEnvironments.Dequeue();
        }

        _pendingEnvironments.Enqueue(new EnvironmentRecord(slug, emulator, core, seconds >= MinimumSeconds));
    }

    /// <summary>
    /// Le titre Nelfe Play correspondant a ce fichier, s'il y en a un.
    ///
    /// La licence locale posee a l'installation porte le nom du fichier vise et
    /// son systeme ; le dossier qui la contient porte le slug. C'est donc elle
    /// qui fait le lien, et personne n'a besoin d'un registre de plus.
    /// </summary>
    private static string ResolveNelfePlaySlug(string? systemId, string? gamePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                return string.Empty;
            }

            var root = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "packages");
            if (!Directory.Exists(root))
            {
                return string.Empty;
            }

            var fileName = Path.GetFileName(gamePath);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(directory, "license.json");
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root_ = document.RootElement;
                var target = root_.TryGetProperty("TargetFileName", out var t) ? t.GetString() : null;
                var system = root_.TryGetProperty("SystemId", out var s) ? s.GetString() : null;

                if (string.Equals(target, fileName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(systemId)
                        || string.Equals(system, systemId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Path.GetFileName(directory);
                }
            }

            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first.Trim()
            : !string.IsNullOrWhiteSpace(second) ? second.Trim()
            : string.Empty;

    /// <summary>
    /// Envoie ce que la machine a constate de ses environnements.
    ///
    /// Le releve part avec les VERSIONS de la machine : RetroBat, RetroArch,
    /// EmulationStation, Windows. Le materiel accompagne le releve mais ne le
    /// definit pas - cote serveur, c'est la pile logicielle seule qui regroupe
    /// les machines, faute de quoi aucune configuration n'atteindrait jamais
    /// les trois exemplaires qui confirment un resultat.
    /// </summary>
    private async Task<bool> PostEnvironmentAsync(
        string credential,
        EnvironmentRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            var machine = NelfePlayEnvironment.Current;

            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            using var form = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("slug", record.Slug),
                new("launched", record.Launched ? "1" : "0"),
                new("emulator", record.Emulator),
                new("core", record.Core),
                new("retrobat", machine.RetroBat),
                new("retroarch", machine.RetroArch),
                new("emulationstation", machine.EmulationStation),
                new("windows", machine.Windows),
                new("cpu", machine.Cpu),
                new("gpu", machine.Gpu),
                new("ram_mb", machine.RamMb.ToString())
            });

            using var response = await client
                .PostAsync("/api/v1/agent/environment", form, cancellationToken)
                .ConfigureAwait(false);

            // 400 : ce releve ne sera jamais accepte (titre inconnu du serveur,
            // pile vide). Le garder bloquerait la file derriere lui pour
            // toujours - on le jette en le declarant traite.
            return response.IsSuccessStatusCode || (int)response.StatusCode == 400;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Environment report failed");
            return false;
        }
    }

    private async Task FlushEnvironmentsAsync(string credential, CancellationToken cancellationToken)
    {
        EnvironmentRecord[] batch;
        lock (_sync)
        {
            if (_pendingEnvironments.Count == 0)
            {
                return;
            }
            batch = _pendingEnvironments.ToArray();
        }

        var sent = 0;
        foreach (var record in batch)
        {
            if (!await PostEnvironmentAsync(credential, record, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
            sent++;
        }

        if (sent == 0)
        {
            return;
        }

        lock (_sync)
        {
            for (var i = 0; i < sent && _pendingEnvironments.Count > 0; i++)
            {
                _pendingEnvironments.Dequeue();
            }
        }
    }

    private async Task<bool> PostAsync(string credential, PlayRecord record, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            var fields = new List<KeyValuePair<string, string>>
            {
                new("system_id", record.SystemId ?? string.Empty),
                new("name", record.GameName ?? string.Empty),
                new("seconds", record.Seconds.ToString())
            };
            if (!string.IsNullOrEmpty(record.Md5))
            {
                fields.Add(new KeyValuePair<string, string>("md5", record.Md5));
                if (!string.IsNullOrEmpty(record.Sha1))
                {
                    fields.Add(new KeyValuePair<string, string>("sha1", record.Sha1));
                }
            }
            if (!string.IsNullOrEmpty(record.Emulator))
            {
                fields.Add(new KeyValuePair<string, string>("emulator", record.Emulator));
            }
            if (!string.IsNullOrEmpty(record.Core))
            {
                fields.Add(new KeyValuePair<string, string>("core", record.Core));
            }

            using var form = new FormUrlEncodedContent(fields);
            using var response = await client.PostAsync("/api/v1/agent/play", form, cancellationToken)
                .ConfigureAwait(false);

            // 401 : le secret anonyme ne vaut plus rien (base remise a zero,
            // par exemple). On l'oublie pour en redemander un au prochain tour.
            if ((int)response.StatusCode == 401 && credential == _anonymousCredential)
            {
                _anonymousCredential = null;
                TryDeleteState();
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Play report failed");
            return false;
        }
    }

    /// <summary>
    /// Le secret a presenter : celui de l'appareil appaire, sinon celui de
    /// l'installation anonyme - inscrite au premier besoin.
    /// </summary>
    private async Task<string?> ResolveCredentialAsync(CancellationToken cancellationToken)
    {
        var paired = _devices.GetCredential();
        if (!string.IsNullOrEmpty(paired))
        {
            return paired;
        }

        if (!string.IsNullOrEmpty(_anonymousCredential))
        {
            return _anonymousCredential;
        }

        _anonymousCredential = ReadState();
        if (!string.IsNullOrEmpty(_anonymousCredential))
        {
            return _anonymousCredential;
        }

        try
        {
            using var client = CreateClient();
            using var response = await client
                .PostAsync("/api/v1/agent/register-anonymous", content: null, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("credential", out var element))
            {
                return null;
            }

            _anonymousCredential = element.GetString();
            WriteState(_anonymousCredential);

            return _anonymousCredential;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Anonymous registration failed");
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayPlayReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);

        return client;
    }

    /// <summary>Empreinte MD5, quand le fichier n'est pas trop gros.</summary>
    private static string? HashOf(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxHashBytes)
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var md5 = MD5.Create();

            return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));

            return document.RootElement.TryGetProperty("credential", out var element)
                ? element.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(string? credential)
    {
        try
        {
            if (string.IsNullOrEmpty(credential))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { credential }, JsonOptions));
        }
        catch
        {
            // Sans fichier, l'installation se reinscrira au prochain demarrage :
            // elle comptera comme nouvelle, ce qui est un defaut acceptable pour
            // une mesure d'audience.
        }
    }

    private static void TryDeleteState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch
        {
            // Sans consequence : le secret sera simplement redemande.
        }
    }

    /// <summary>
    /// Le jeu en cours, tel que la charge de l'evenement le transporte.
    ///
    /// La charge est un objet ANONYME - { EventName, RawArgs, Context } - donc
    /// impossible a typer depuis ici : on va chercher sa propriete Context par
    /// reflexion, une fois, et on retombe sur le contexte partage si sa forme
    /// change un jour. Le repli n'est pas de la prudence decorative : c'est ce
    /// qui evite qu'un renommage cote frontend refasse taire le releve sans
    /// prevenir.
    /// </summary>
    private static GameReference? ResolveRunning(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var context = payload.GetType().GetProperty("Context")?.GetValue(payload);

        return context is GameState state ? state.Running : null;
    }

    private sealed record Play(
        string? SystemId,
        string? GameName,
        string? GamePath,
        string? Emulator,
        string? Core,
        Stopwatch Clock);

    private sealed record PlayRecord(
        string? SystemId,
        string? GameName,
        string? Md5,
        string? Sha1,
        int Seconds,
        string? Emulator = null,
        string? Core = null);

    /// <summary>Ce qu'une machine a constate d'un titre Nelfe Play : sur quelle
    /// pile logicielle, et si le lancement a abouti.</summary>
    private sealed record EnvironmentRecord(string Slug, string Emulator, string Core, bool Launched);
}
