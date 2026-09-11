using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Providers.RetroArchWrapper;

public class RetroArchWrapperProvider : IProvider
{
    public const string DefaultPipeName = "RetroBatArcadePipe";

    private static readonly Regex RuntimeRegex = new(
        @"^\[(?<clock>\d{2}:\d{2}:\d{2}\.\d{3})\]\s+\[ADDR:(?<addr>[^\]]+)\]\s+\[VAL:(?<raw>[^\]]+)\]\s+\[UDP_OUT\]\s+(?:TYPE:)?(?<channel>[A-Z]+)\s*:\s*(?<name>[A-Z0-9_]+)\s+\|\s+SOURCE:(?<source>.*?)\s+\|\s+VALUE:(?<value>-?\d+)(?:\s+\|\s+RATE:(?<rate>-?\d+))?(?:\s+\|\s+FAMILY:(?<family>[A-Za-z0-9_.-]+))?(?:\s+\|\s+COLOR:(?<color>[A-Za-z0-9_-]+))?(?:\s+\|\s+PLAYER:(?<player>\d+))?(?:\s+\|\s+FRAME:(?<frame>\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IIngameSourceArbitrationService _arbitration;
    private readonly ILogger<RetroArchWrapperProvider>? _logger;
    private readonly object _stateLock = new();

    /// <summary>
    /// L'index d'alias d'un systeme, charge UNE fois et garde tant que le fichier ne change pas.
    ///
    /// Avant : `ResolveDefinition` relisait et desserialisait alias.json A CHAQUE LIGNE recue du
    /// wrapper, puis parcourait toutes les entrees en les normalisant une a une. Sur la Megadrive
    /// ce fichier fait 1,7 Mo ; un anneau ramasse dans Sonic coutait donc une lecture de 1,7 Mo et
    /// des dizaines de milliers d'allocations. Mesure sur borne pendant un direct : 6 Mo/s lus en
    /// moyenne, pointes a 22 Mo/s, 60 Mo de memoire qui montaient et redescendaient en boucle, et
    /// un jeu qui saccadait. Le profileur a designe cette methode et rien d'autre.
    ///
    /// La table normalisee est construite au chargement, ce qui rend la recherche tolerante en
    /// O(1) au lieu d'un parcours complet ; la PREMIERE entree normalisee gagne, comme le faisait
    /// le parcours. Le fichier est relu si sa date ou sa taille change : un alias depose par le
    /// deploiement de flotte est pris en compte a la ligne suivante, sans redemarrage.
    /// </summary>
    private sealed record AliasIndex(
        DateTime WriteUtc,
        long Length,
        Dictionary<string, string> Exact,
        Dictionary<string, string> Normalized);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AliasIndex> _aliasIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    private AliasIndex? LoadAliasIndex(string aliasFile)
    {
        var info = new FileInfo(aliasFile);
        if (!info.Exists)
        {
            _aliasIndexes.TryRemove(aliasFile, out _);
            return null;
        }
        if (_aliasIndexes.TryGetValue(aliasFile, out var connu)
            && connu.WriteUtc == info.LastWriteTimeUtc
            && connu.Length == info.Length)
        {
            return connu;
        }

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var exact = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile))
            ?? new Dictionary<string, string>();
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in exact)
        {
            var cle = NormalizeRomName(entry.Key);
            if (cle.Length > 0 && !normalized.ContainsKey(cle))
            {
                normalized[cle] = entry.Value;
            }
        }
        var index = new AliasIndex(info.LastWriteTimeUtc, info.Length, exact, normalized);
        _aliasIndexes[aliasFile] = index;
        _logger?.LogInformation(
            "Index d'alias charge : {Fichier}, {Entrees} entrees en {Ms} ms.",
            Path.GetFileName(Path.GetDirectoryName(aliasFile)) + "/" + info.Name, exact.Count, chrono.ElapsedMilliseconds);
        return index;
    }
    private readonly Dictionary<string, RetroArchRuntimeSignal> _signals = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _connected;
    private DateTime? _lastMessageAt;
    private string _lastRawMessage = string.Empty;

    public RetroArchWrapperProvider(
        IEventBus eventBus,
        ApiContext context,
        IIngameSourceArbitrationService arbitration,
        ILogger<RetroArchWrapperProvider>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _arbitration = arbitration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunAsync(_cts.Token);
        _logger?.LogInformation("RetroArchWrapperProvider started for pipe {PipeName}", GetPipePath());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
        {
            _cts.Cancel();
        }

        if (_workerTask != null)
        {
            try
            {
                await Task.WhenAny(_workerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch
            {
                // Ignore shutdown exceptions.
            }
        }
    }

    public bool IsHealthy() => _workerTask != null && !_workerTask.IsCompleted;

    public RetroArchRuntimeSnapshot GetSnapshot()
    {
        var definition = ResolveDefinition();
        lock (_stateLock)
        {
            return new RetroArchRuntimeSnapshot
            {
                Source = "retroarch.wrapper.pipe",
                Pipe = GetPipePath(),
                Connected = _connected,
                SystemId = definition.SystemId,
                Rom = definition.Rom,
                DefinitionFile = definition.DefinitionFile,
                LastMessageAt = _lastMessageAt,
                LastRawMessage = _lastRawMessage,
                Signals = _signals.Values
                    .OrderBy(signal => signal.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(CloneSignal)
                    .ToList()
            };
        }
    }

    public RetroArchDefinitionSnapshot GetDefinitionSnapshot() => ResolveDefinition();

    /// <summary>
    /// Resolves a definition for an EXPLICIT system/rom pair with the exact
    /// same logic as the current-context path: alias.json first, normalized
    /// rom name, and arcade-like system fallback. This is what remote
    /// consumers (tournament manager, Live Contest) must use - a naive
    /// <c>&lt;system&gt;/&lt;rom&gt;.MEM</c> path never matches curated files.
    /// </summary>
    public RetroArchDefinitionSnapshot ResolveDefinitionFor(string rawRom, string systemId)
    {
        RetroArchDefinitionSnapshot? fallback = null;
        foreach (var candidateSystemId in ResolveDefinitionSystemCandidates(systemId))
        {
            var candidate = ResolveDefinition(rawRom, candidateSystemId);
            fallback ??= candidate;
            if (candidate.DefinitionExists)
            {
                return candidate;
            }
        }

        // Repli catalogue (même logique que MarqueeManagerSetup/MemSignalCatalog) :
        // clé tolérante à la ponctuation sur alias.json ET les basenames .MEM,
        // puis nom débarrassé des tags de dump - « Sonic The Hedgehog (Europe) »
        // trouve sonic-the-hedgehog.MEM même sans entrée d'alias.
        foreach (var candidateSystemId in ResolveDefinitionSystemCandidates(systemId))
        {
            var scanned = ResolveByCatalogScan(rawRom, candidateSystemId);
            if (scanned is not null)
            {
                return scanned;
            }
        }

        return fallback ?? new RetroArchDefinitionSnapshot
        {
            SystemId = systemId,
            Rom = NormalizeRomName(rawRom),
            DefinitionFile = string.Empty,
            AliasFile = string.Empty,
            AliasMatched = false,
            DefinitionExists = false
        };
    }

    private RetroArchDefinitionSnapshot? ResolveByCatalogScan(string rawRom, string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rawRom))
        {
            return null;
        }

        var dir = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        static string Key(string value)
            => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // Requêtes : nom brut puis nom coupé aux tags de dump "(...)"/"[...]".
        var queries = new List<string> { rawRom };
        var cut = rawRom.IndexOfAny(new[] { '(', '[' });
        if (cut > 0)
        {
            queries.Add(rawRom[..cut].TrimEnd());
        }

        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliasFile = Path.Combine(dir, "alias.json");
        var aliasMatched = false;
        if (File.Exists(aliasFile))
        {
            try
            {
                var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile));
                foreach (var pair in aliases ?? [])
                {
                    var key = Key(pair.Key);
                    if (key.Length > 0)
                    {
                        index.TryAdd(key, Path.Combine(dir, pair.Value + ".MEM"));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read wrapper alias file {AliasFile}", aliasFile);
            }
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.MEM"))
        {
            var key = Key(Path.GetFileNameWithoutExtension(file));
            if (key.Length > 0)
            {
                index.TryAdd(key, file);
            }
        }

        foreach (var query in queries)
        {
            var key = Key(query);
            if (key.Length > 0 && index.TryGetValue(key, out var memPath) && File.Exists(memPath))
            {
                return new RetroArchDefinitionSnapshot
                {
                    SystemId = systemId,
                    Rom = Path.GetFileNameWithoutExtension(memPath),
                    DefinitionFile = memPath,
                    AliasFile = File.Exists(aliasFile) ? aliasFile : string.Empty,
                    AliasMatched = aliasMatched,
                    DefinitionExists = true
                };
            }
        }

        return null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                DefaultPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                SetConnected(true);
                // Au niveau Information : sans cette trace, un wrapper qui ne se connecte JAMAIS
                // et un wrapper connecte mais muet produisent exactement le meme journal, et on
                // cherche la panne du mauvais cote de la conduite.
                _logger?.LogInformation("Wrapper RetroArch : connecte au pipe {Pipe}.", DefaultPipeName);
                await PublishConnectionEventAsync("retroarch.wrapper.connected", cancellationToken);

                using var reader = new StreamReader(pipe);
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    await ProcessLineAsync(line, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogInformation(ex, "RetroArch wrapper pipe disconnected");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Error while reading RetroArch wrapper pipe");
            }
            finally
            {
                var wasConnected = SetConnected(false);
                if (wasConnected)
                {
                    await PublishConnectionEventAsync("retroarch.wrapper.disconnected", cancellationToken);
                }
            }
        }
    }

    /// <summary>Derniere combinaison systeme/jeu annoncee comme ecartee : on ne repete pas
    /// l'avertissement a chaque ligne, mais on le redit des que le jeu change.</summary>
    private string? _lastSuppressionKey;

    /// <summary>Dernier constat de diagnostic annonce, pour ne pas le repeter a chaque seconde.</summary>
    private string? _lastDiagnosticKey;

    /// <summary>Surveillances deja annoncees comme ayant parle (jeu + cle du signal).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _signauxAnnonces = new(StringComparer.Ordinal);

    /// <summary>Combien de valeurs de score deja annoncees, par jeu et par surveillance.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _scoresAnnonces = new(StringComparer.Ordinal);

    /// <summary>Les prefixes que le wrapper reserve a son proces-verbal de demarrage. On ne
    /// remonte QUE ceux-la : le reste du bavardage non-runtime reste en Debug.</summary>
    private static readonly string[] MarqueursDeDiagnostic =
    {
        "[DEBUG WRAPPER]",   // core, arcade, system_ram, taille, blocs de carte memoire
        "WATCH_RESOLVE",     // comment chaque adresse surveillee s'est resolue
        "[WRAPPER ERROR]",
        "[WRAPPER WARN]",
    };

    private static bool EstUnDiagnosticDuWrapper(string line)
    {
        foreach (var marqueur in MarqueursDeDiagnostic)
        {
            if (line.IndexOf(marqueur, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        // ── Attestation du listener (scoring certifié) ──────────────────────────
        // Ligne additive émise UNE fois au chargement ; format distinct des signaux
        // runtime. On la publie sur le bus pour le service de scoring et on s'arrête.
        const string attestationMarker = "[LISTENER ATTESTATION] ";
        var attestationAt = line.IndexOf(attestationMarker, StringComparison.Ordinal);
        if (attestationAt >= 0)
        {
            await PublishAttestationAsync(line[(attestationAt + attestationMarker.Length)..].Trim());
            return;
        }

        // Procès-verbal de fin de partie (checkpoints + timing + intégrité).
        const string sessionMarker = "[LISTENER SESSION] ";
        var sessionAt = line.IndexOf(sessionMarker, StringComparison.Ordinal);
        if (sessionAt >= 0)
        {
            await PublishListenerSessionAsync(line[(sessionAt + sessionMarker.Length)..].Trim());
            return;
        }

        var definition = ResolveDefinition();
        var parsed = ParseRuntimeSignal(line);

        lock (_stateLock)
        {
            _lastRawMessage = line;
            _lastMessageAt = DateTime.UtcNow;
            if (parsed != null)
            {
                _signals[parsed.Key] = parsed;
            }
        }

        if (parsed == null)
        {
            // Le wrapper emet, une fois par jeu, son propre proces-verbal : quel core, expose-t-il
            // de la RAM systeme, y a-t-il une carte memoire, et comment chaque adresse surveillee
            // s'est resolue. C'est LA reponse a « pourquoi ce jeu est-il muet », et la laisser en
            // Debug revient a la jeter. Elle est rare (une poignee de lignes par lancement), donc
            // la monter en Information ne bruite pas le journal.
            if (EstUnDiagnosticDuWrapper(line))
            {
                // Le wrapper repete son constat d'echec a chaque seconde tant que la partie dure.
                // Le dire une fois suffit a diagnostiquer ; le repeter cinquante fois noie le
                // journal. On ne reparle donc que si le constat CHANGE.
                var cle = definition.SystemId + "/" + definition.Rom + "|" + line.Trim();
                if (Interlocked.Exchange(ref _lastDiagnosticKey, cle) != cle)
                {
                    _logger?.LogInformation(
                        "Wrapper RetroArch [{SystemId}/{Rom}] : {RawLine}",
                        definition.SystemId,
                        definition.Rom,
                        line.Trim());
                }
                return;
            }

            _logger?.LogDebug(
                "Ignoring non-runtime wrapper line for {SystemId}/{Rom}: {RawLine}",
                definition.SystemId,
                definition.Rom,
                line);
            return;
        }

        if (_arbitration.ShouldSuppressRetroArchWrapper(definition.SystemId, definition.Rom, definition.DefinitionFile))
        {
            // ÉCARTER DES SIGNAUX EN SILENCE EST LE PIRE DES COMPORTEMENTS : le scoring parait
            // simplement ne pas marcher, sans erreur ni trace, et on cherche la cause partout
            // ailleurs. On l'annonce donc UNE FOIS par jeu, au niveau Information, avec la raison.
            var cle = definition.SystemId + "/" + definition.Rom;
            if (Interlocked.Exchange(ref _lastSuppressionKey, cle) != cle)
            {
                _logger?.LogInformation(
                    "Signaux du wrapper RetroArch ECARTES pour {SystemId}/{Rom} : une session MAME Lua est consideree active. "
                    + "Si MAME n'est plus lance, cette session ne s'est pas refermee et le scoring restera muet sous RetroArch.",
                    definition.SystemId,
                    definition.Rom);
            }

            _logger?.LogDebug(
                "Suppressing RetroArch wrapper runtime signal because MAME Lua is active for {SystemId}/{Rom}: {RawLine}",
                definition.SystemId,
                definition.Rom,
                line);
            return;
        }

        // Premiere fois qu'une surveillance parle : on le dit, avec son adresse et sa valeur.
        // Les signaux partent sur le bus sans jamais etre journalises, si bien qu'un jeu dont
        // AUCUNE surveillance ne se declenche et un jeu dont elles se declenchent toutes
        // produisent le meme journal - vide. Une ligne par surveillance et par jeu, donc borne,
        // et c'est exactement ce qu'il faut pour calibrer un jeu arcade.
        // Le SCORE fait exception : une seule valeur ne dit pas s'il est JUSTE. Un score lu au
        // mauvais octet reste plausible pris isolement (33 au lieu de 3300), et c'est la
        // progression qui trahit l'erreur. On en annonce donc les premieres valeurs, pas la
        // premiere seulement, puis on se tait.
        var clePremiere = definition.SystemId + "/" + definition.Rom + "|" + parsed.Key;
        if (string.Equals(parsed.Channel, "SCORE", StringComparison.Ordinal))
        {
            var n = _scoresAnnonces.AddOrUpdate(clePremiere, 1, (_, v) => v + 1);
            if (n <= 12)
            {
                _logger?.LogInformation(
                    "Score wrapper [{SystemId}/{Rom}] {Name} #{N} @ {Address} = {Raw} ({Value}) — {Desc}",
                    definition.SystemId, definition.Rom, parsed.Name, n,
                    parsed.Address, parsed.RawValueHex, parsed.Value, parsed.SourceDescription);
            }
        }
        else if (_signauxAnnonces.TryAdd(clePremiere, 0))
        {
            _logger?.LogInformation(
                "Signal wrapper [{SystemId}/{Rom}] {Key} : {Name} @ {Address} = {Raw} ({Value}) — {Desc}",
                definition.SystemId, definition.Rom, parsed.Key, parsed.Name,
                parsed.Address, parsed.RawValueHex, parsed.Value, parsed.SourceDescription);
        }

        var payload = new
        {
            Source = "retroarch.wrapper.pipe",
            Pipe = GetPipePath(),
            definition.SystemId,
            definition.Rom,
            definition.DefinitionFile,
            signal = parsed
        };

        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "retroarch.memory.changed",
            Payload = payload
        });

        var projectedType = parsed.Channel switch
        {
            "ACTION" => "retroarch.action",
            "STATE" => "retroarch.state",
            "SCORE" => "retroarch.score",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(projectedType))
        {
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = projectedType,
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe",
                    Pipe = GetPipePath(),
                    definition.SystemId,
                    definition.Rom,
                    definition.DefinitionFile,
                    actionType = parsed.Name,
                    sourceCategory = parsed.SourceDescription,
                    parsed.Value,
                    parsed.Rate,
                    parsed.Address,
                    parsed.RawValueHex,
                    family = parsed.Family,
                    color = parsed.Color,
                    player = parsed.Player
                }
            });
        }
    }

    // Publie l'attestation du listener (self-mesure : SHA du wrapper, du core réel, du
    // .MEM + nonce) sur le bus. Le service de scoring l'assemble ensuite dans le passeport.
    private async Task PublishAttestationAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;

            var definition = ResolveDefinition();
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "scoring.listener.attestation",
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe",
                    definition.SystemId,
                    definition.Rom,
                    ListenerSha256 = Get("listener_sha256"),
                    CoreSha256 = Get("core_sha256"),
                    MemSha256 = Get("mem_sha256"),
                    ContentSha256 = Get("content_sha256"),
                    ContentMd5 = Get("content_md5"),
                    WrapperVersion = Get("wrapper_version"),
                    SessionNonce = Get("session_nonce"),
                }
            });
            _logger?.LogDebug("Scoring : attestation listener publiée (wrapper {Wrapper})", Get("wrapper_version"));
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Scoring : attestation JSON illisible : {Json}", json);
        }
    }

    // Publie le procès-verbal de session (fin de partie) sur le bus. Le JSON brut est
    // transporté tel quel ; le service de scoring l'agrège dans le passeport.
    private async Task PublishListenerSessionAsync(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json); // valide le JSON avant publication
            var definition = ResolveDefinition();
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "scoring.listener.session",
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe",
                    definition.SystemId,
                    definition.Rom,
                    Session = json,
                }
            });
            _logger?.LogDebug("Scoring : session listener publiée ({Len} octets)", json.Length);
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Scoring : session JSON illisible : {Json}", json);
        }
    }

    private RetroArchDefinitionSnapshot ResolveDefinition()
    {
        var game = _context.Ui.Running ?? _context.Ui.Selected;
        var systemId = ResolveSystemId(game);
        var rawRom = Path.GetFileNameWithoutExtension(game?.GamePath ?? game?.GameName ?? string.Empty);
        RetroArchDefinitionSnapshot? fallback = null;

        foreach (var candidateSystemId in ResolveDefinitionSystemCandidates(systemId))
        {
            var candidate = ResolveDefinition(rawRom, candidateSystemId);
            fallback ??= candidate;
            if (candidate.DefinitionExists)
            {
                return candidate;
            }
        }

        return fallback ?? new RetroArchDefinitionSnapshot
        {
            SystemId = systemId,
            Rom = NormalizeRomName(rawRom),
            DefinitionFile = string.Empty,
            AliasFile = string.Empty,
            AliasMatched = false,
            DefinitionExists = false
        };
    }

    private RetroArchDefinitionSnapshot ResolveDefinition(string rawRom, string systemId)
    {
        var normalizedRom = NormalizeRomName(rawRom);
        var aliasFile = string.IsNullOrWhiteSpace(systemId)
            ? string.Empty
            : Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, "alias.json");

        var aliasMatched = false;
        if (!string.IsNullOrWhiteSpace(aliasFile))
        {
            try
            {
                // L'index est en memoire : cette methode est appelee a chaque ligne du wrapper, et
                // relire le fichier ici a deja fait saccader une partie (voir AliasIndex).
                var aliases = LoadAliasIndex(aliasFile);
                if (aliases is not null)
                {
                    if (!string.IsNullOrWhiteSpace(rawRom) && aliases.Exact.TryGetValue(rawRom, out var aliasTarget))
                    {
                        normalizedRom = aliasTarget;
                        aliasMatched = true;
                    }
                    else if (aliases.Normalized.TryGetValue(normalizedRom, out var aliasNormalise)
                             && !string.IsNullOrWhiteSpace(aliasNormalise))
                    {
                        normalizedRom = aliasNormalise;
                        aliasMatched = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read wrapper alias file {AliasFile}", aliasFile);
            }
        }

        var definitionFile = string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(normalizedRom)
            ? string.Empty
            : Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, normalizedRom + ".MEM");

        return new RetroArchDefinitionSnapshot
        {
            SystemId = systemId,
            Rom = normalizedRom,
            DefinitionFile = definitionFile,
            AliasFile = aliasFile,
            AliasMatched = aliasMatched,
            DefinitionExists = !string.IsNullOrWhiteSpace(definitionFile) && File.Exists(definitionFile)
        };
    }

    private RetroArchRuntimeSignal? ParseRuntimeSignal(string line)
    {
        var match = RuntimeRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        int? value = int.TryParse(match.Groups["value"].Value, out var parsedValue) ? parsedValue : null;
        int? rate = int.TryParse(match.Groups["rate"].Value, out var parsedRate) ? parsedRate : null;
        var channel = match.Groups["channel"].Value.Trim().ToUpperInvariant();
        var name = match.Groups["name"].Value.Trim().ToUpperInvariant();

        return new RetroArchRuntimeSignal
        {
            Key = $"{channel}.{name}",
            Channel = channel,
            Name = name,
            SourceDescription = match.Groups["source"].Value.Trim(),
            Address = match.Groups["addr"].Value.Trim(),
            RawValueHex = match.Groups["raw"].Value.Trim(),
            Value = value,
            Rate = rate,
            Family = match.Groups["family"].Value.Trim().ToLowerInvariant(),
            Color = match.Groups["color"].Value.Trim().ToLowerInvariant(),
            Player = int.TryParse(match.Groups["player"].Value, out var parsedPlayer) ? parsedPlayer : null,
            Frame = long.TryParse(match.Groups["frame"].Value, out var parsedFrame) ? parsedFrame : null,
            RawLine = line,
            Ts = DateTime.UtcNow
        };
    }

    private bool SetConnected(bool connected)
    {
        lock (_stateLock)
        {
            var previous = _connected;
            if (connected && !previous)
            {
                _signals.Clear();
                _lastMessageAt = null;
                _lastRawMessage = string.Empty;
            }
            _connected = connected;
            return previous;
        }
    }

    private async Task PublishConnectionEventAsync(string eventType, CancellationToken cancellationToken)
    {
        var definition = ResolveDefinition();
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = eventType,
            Payload = new
            {
                Source = "retroarch.wrapper.pipe",
                Pipe = GetPipePath(),
                definition.SystemId,
                definition.Rom,
                definition.DefinitionFile
            }
        });
    }

    private static string NormalizeRomName(string rawRom)
    {
        if (string.IsNullOrWhiteSpace(rawRom))
        {
            return string.Empty;
        }

        var normalized = rawRom.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    private static IReadOnlyList<string> ResolveDefinitionSystemCandidates(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string> { systemId.Trim() };
        var normalized = systemId.Trim().ToLowerInvariant();
        if (IsArcadeLikeSystem(normalized) &&
            !string.Equals(normalized, "arcade", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("arcade");
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsArcadeLikeSystem(string systemId)
    {
        return systemId is
            "mame" or
            "fbneo" or
            "fba" or
            "neogeo" or
            "cps1" or
            "cps2" or
            "cps3" or
            "cave" or
            "atomiswave" or
            "naomi" or
            "naomi2";
    }

    private static string ResolveSystemId(GameReference? game)
    {
        if (game == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(game.SystemId) &&
            !string.Equals(game.SystemId, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return game.SystemId;
        }

        if (!string.IsNullOrWhiteSpace(game.Launch?.System))
        {
            return game.Launch.System.Trim();
        }

        var romPath = game.GamePath ?? string.Empty;
        try
        {
            var romsRoot = Path.GetFullPath(RetroBatPaths.RomsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullGamePath = Path.GetFullPath(romPath);
            if (fullGamePath.StartsWith(romsRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullGamePath[romsRoot.Length..];
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 1)
                {
                    return parts[0];
                }
            }
        }
        catch
        {
            // Ignore path inference errors and fall back to unknown.
        }

        return game.SystemId;
    }

    private static string GetPipePath() => @"\\.\pipe\" + DefaultPipeName;

    private static RetroArchRuntimeSignal CloneSignal(RetroArchRuntimeSignal signal)
    {
        return new RetroArchRuntimeSignal
        {
            Key = signal.Key,
            Channel = signal.Channel,
            Name = signal.Name,
            SourceDescription = signal.SourceDescription,
            Address = signal.Address,
            RawValueHex = signal.RawValueHex,
            Value = signal.Value,
            Rate = signal.Rate,
            RawLine = signal.RawLine,
            Ts = signal.Ts
        };
    }
}
