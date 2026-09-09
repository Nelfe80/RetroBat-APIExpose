using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroBat.Api.Scoring;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class MameLuaIngameProvider : IProvider
{
    private static readonly string[] DefinitionSystemFallbacks = ["arcade", "mame"];

    private static readonly Regex EntryValueRegex = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>""(?:\\""|[^""])*""|0[xX][0-9A-Fa-f]+|-?\d+(?:\.\d+)?|true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventBus _eventBus;
    private readonly IIngameSourceArbitrationService _arbitration;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MameLuaIngameProvider> _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, MameLuaSessionSnapshot> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private volatile DiscoveryArm? _discoveryArm;

    public MameLuaIngameProvider(
        IEventBus eventBus,
        IIngameSourceArbitrationService arbitration,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<MameLuaIngameProvider> logger)
    {
        _eventBus = eventBus;
        _arbitration = arbitration;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunAsync(_cts.Token);
        _logger.LogInformation("MameLuaIngameProvider bridge starting on 127.0.0.1:{Port}", GetPort());
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
            await Task.WhenAny(_workerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        }
    }

    public bool IsHealthy() => _workerTask != null && !_workerTask.IsCompleted;

    /// <summary>Arme la découverte (MEM Explorer) : au prochain HELLO d'un plugin Lua, on lui répond
    /// <c>DISCOVER|host|port|hz</c> au lieu de la liste WATCH → il streame la RAM principale vers le
    /// listener TCP de l'Explorer (le plugin ne peut être que client sortant).</summary>
    public void ArmDiscovery(string host, int port, int hz) =>
        _discoveryArm = new DiscoveryArm(string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host, port, hz);

    /// <summary>Désarme la découverte : retour au mode WATCH (events runtime).</summary>
    public void DisarmDiscovery() => _discoveryArm = null;

    /// <summary>La découverte est-elle armée ?</summary>
    public bool IsDiscoveryArmed => _discoveryArm != null;

    private int GetPort()
    {
        var port = _options.CurrentValue.GameEventsManager.MameLuaIngamePort;
        return port is > 0 and < 65536 ? port : 12347;
    }

    private async Task RunAsync(CancellationToken token)
    {
        // Le plugin Lua MAME (emu.file "socket.host:port") ne sait etre que
        // CLIENT sortant : APIExpose doit donc tenir l'ecoute locale. Les deux
        // bouts etaient clients jusqu'ici -> aucune session ne s'etablissait.
        while (!token.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, GetPort());
                listener.Start();
                _logger.LogInformation("MAME Lua ingame bridge listening on 127.0.0.1:{Port}", GetPort());

                while (!token.IsCancellationRequested)
                {
                    // chaque client dans sa tache : une session ouverte ne doit
                    // jamais bloquer l'accept (double instance MAME, sonde...)
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleClientAsync(client, token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "MAME Lua ingame session ended.");
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "MAME Lua ingame bridge cannot listen on {Port}; retrying.", GetPort());
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "MAME Lua ingame bridge failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            finally
            {
                listener?.Stop();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var clientScope = client;
        client.NoDelay = true;

        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        MameLuaDefinition? definition = null;
        var previousValues = new Dictionary<int, long>();
        var fired = new Dictionary<int, int>();
        var composedLast = new Dictionary<string, long>();
        var scoringStart = DateTime.UtcNow;   // repère pour monotonic_ms de la session certifiée
        long sensitiveFastForward = 0;        // avance rapide remontée par le plugin Lua (SENSITIVE)
        string sensitiveCoreOptions = "";     // Phase E : réglages (DIP) capturés par le plugin Lua (SETTINGS)

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await writer.WriteLineAsync("HELLO?");
            lock (_sortieGate) { _sortie = writer; }

            while (!token.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split('|');
                var command = parts[0].Trim();
                if (command.Equals("HELLO", StringComparison.OrdinalIgnoreCase))
                {
                    var rom = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    var gameName = parts.Length > 2 ? parts[2].Trim() : rom;

                    // Découverte armée (MEM Explorer) : on demande au plugin de streamer la RAM
                    // principale vers le listener TCP de l'Explorer, au lieu d'écouter des adresses
                    // connues. definition reste null → pas de session d'events ici (le plugin
                    // streame ailleurs), et le finally n'émet pas de "stopped" fantôme.
                    var arm = _discoveryArm;
                    if (arm != null)
                    {
                        await writer.WriteLineAsync($"DISCOVER|{arm.Host}|{arm.Port}|{arm.Hz}");
                        _logger.LogInformation(
                            "MAME Lua découverte : plugin dirigé vers {Host}:{Port} @ {Hz}Hz (rom={Rom})",
                            arm.Host, arm.Port, arm.Hz, rom);
                        continue;
                    }

                    definition = ResolveDefinition(rom);
                    previousValues.Clear();

                    await writer.WriteLineAsync("CLEAR");
                    foreach (var target in definition.Targets)
                    {
                        await writer.WriteLineAsync($"WATCH|{target.Id}|0x{target.RuntimeAddress:X}|{target.Type}");
                    }

                    await writer.WriteLineAsync($"READY|{definition.Targets.Count}");
                    await PublishSessionStartedAsync(definition, gameName);
                    UpdateSession(endpoint, definition, connected: true, lastRawLine: line, fired: fired);
                    scoringStart = DateTime.UtcNow;
                    sensitiveFastForward = 0;
                    await PublishScoringAttestationAsync(definition);

                    _logger.LogInformation(
                        "MAME Lua ingame session started: rom={Rom} resolved={ResolvedRom} watches={WatchCount} definition={DefinitionFile}",
                        rom,
                        definition.Rom,
                        definition.Targets.Count,
                        definition.DefinitionFile);
                }
                else if (command.Equals("VALUE", StringComparison.OrdinalIgnoreCase) && definition != null)
                {
                    if (parts.Length < 3 ||
                        !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId) ||
                        !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        continue;
                    }

                    UpdateSession(endpoint, definition, connected: true, lastRawLine: line, fired: fired);
                    if (!previousValues.TryGetValue(targetId, out var oldValue))
                    {
                        previousValues[targetId] = value;
                        LogInitialState(definition, targetId, value);
                        // Un etat deja etabli au moment ou l'on s'attache doit se dire :
                        // le personnage que le joueur tient deja, le drapeau deja leve.
                        await EvaluateTargetAsync(definition, targetId, value, value, initialized: false);
                        await EvaluateCompositesAsync(definition, targetId, previousValues, composedLast, fired);
                        continue;
                    }

                    if (oldValue == value)
                    {
                        continue;
                    }

                    previousValues[targetId] = value;
                    fired[targetId] = fired.GetValueOrDefault(targetId) + 1;
                    await EvaluateTargetAsync(definition, targetId, oldValue, value, initialized: true);
                    await EvaluateCompositesAsync(definition, targetId, previousValues, composedLast, fired);
                }
                else if (command.Equals("SETTINGS", StringComparison.OrdinalIgnoreCase))
                {
                    // Phase E : le plugin Lua remonte les reglages (DIP/vies/difficulte) sous forme
                    // de chaine canonique triee "name=value;name=value". Digest cote APIExpose ;
                    // le verifieur ne controle QUE si le profil epingle allowed_core_options_digest.
                    sensitiveCoreOptions = parts.Length > 1 ? string.Join("|", parts.Skip(1)).Trim() : "";
                    _logger.LogInformation("MAME Lua reglages (rom={Rom}) : {Options}", definition?.Rom ?? "?", sensitiveCoreOptions);
                }
                else if (command.Equals("SENSITIVE", StringComparison.OrdinalIgnoreCase))
                {
                    // Anti-triche MAME (parite wrapper) : le plugin Lua remonte les vecteurs
                    // detectes cette session (ex. fast_forward via l'etat throttle, que genesis
                    // ne detecte pas). Capture + trace ; alimentera le passeport certifie MAME
                    // (attestation + session) quand ce chemin sera cable.
                    foreach (var kv in parts.Skip(1))
                    {
                        var eq = kv.Split('=');
                        if (eq.Length == 2 && eq[0].Trim().Equals("fast_forward", StringComparison.OrdinalIgnoreCase)
                            && long.TryParse(eq[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ff))
                        {
                            sensitiveFastForward = ff;
                        }
                    }
                    var flags = string.Join(", ", parts.Skip(1).Select(p => p.Trim()).Where(p => p.Length > 0));
                    _logger.LogInformation("MAME Lua anti-triche (rom={Rom}) : {Flags}", definition?.Rom ?? "?", flags);
                }
                else if (command.Equals("SNAPSHOT", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("MAME Lua : capture {Etat} (frame {Frame}).",
                        parts.Length > 1 ? parts[1] : "?", parts.Length > 2 ? parts[2] : "?");
                }
                else if (command.Equals("BYE", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua ingame client disconnected unexpectedly.");
        }
        finally
        {
            lock (_sortieGate) { _sortie = null; }
            if (definition != null)
            {
                UpdateSession(endpoint, definition, connected: false, lastRawLine: string.Empty, fired: fired);
                await PublishScoringSessionAsync(definition, scoringStart, sensitiveFastForward, sensitiveCoreOptions);
                await PublishSessionStoppedAsync(definition);
            }
        }
    }

    // Voie de commande vers le plugin. Elle n'existe que le temps d'une partie : hors partie il
    // n'y a personne au bout, et c'est exactement ce que dit un envoi qui echoue.
    private readonly object _sortieGate = new();
    private System.IO.StreamWriter? _sortie;

    /// <summary>Vrai si une partie MAME est en cours et joignable.</summary>
    public bool EstConnecte
    {
        get { lock (_sortieGate) { return _sortie is not null; } }
    }

    /// <summary>
    /// Demande a MAME de photographier l'ecran (parite avec RetroArch pour la capture du record).
    ///
    /// On passe par MAME plutot que par une capture de fenetre : son snapshot est pris dans le
    /// tampon du jeu, donc a la definition d'origine et deja oriente pour un jeu vertical. Il
    /// ecrit dans son snapshot_directory ; c'est l'appelant qui retrouve le fichier apparu.
    /// </summary>
    public async Task<bool> RequestSnapshotAsync(CancellationToken ct)
    {
        System.IO.StreamWriter? sortie;
        lock (_sortieGate) { sortie = _sortie; }
        if (sortie is null) return false;
        try
        {
            await sortie.WriteLineAsync("SNAPSHOT".AsMemory(), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua : demande de capture impossible.");
            return false;
        }
    }

    /// <summary>
    /// Scores en tuiles (digits=N) : recompose la valeur decimale complete a
    /// chaque octet recu ; une tuile illisible (transition, glyphe non
    /// chiffre) invalide la lecture au lieu d'emettre un delta fantaisiste.
    /// </summary>
    private async Task EvaluateCompositesAsync(
        MameLuaDefinition definition,
        int changedTargetId,
        Dictionary<int, long> previousValues,
        Dictionary<string, long> composedLast,
        Dictionary<int, int> fired)
    {
        foreach (var group in definition.Rules
                     .Where(r => r.Digits > 1 && r.DigitTargetIds.Contains(changedTargetId))
                     .GroupBy(r => r.CompositeKey))
        {
            var first = group.First();
            long composed = 0;
            var valid = true;
            foreach (var id in first.DigitTargetIds)
            {
                if (!previousValues.TryGetValue(id, out var tile))
                {
                    valid = false;
                    break;
                }

                long digit;
                if (first.Blank is long blank && tile == blank)
                {
                    digit = 0;
                }
                else
                {
                    digit = tile - first.DigitZero;
                    if (digit is < 0 or > 9)
                    {
                        valid = false;
                        break;
                    }
                }

                composed = composed * 10 + digit;
            }

            if (!valid)
            {
                composedLast.Remove(group.Key);
                continue;
            }

            composed *= first.Scale;
            if (!composedLast.TryGetValue(group.Key, out var old))
            {
                composedLast[group.Key] = composed;
                continue;
            }

            if (composed == old)
            {
                continue;
            }

            composedLast[group.Key] = composed;
            var delta = composed - old;
            foreach (var rule in group)
            {
                if (rule.NoLog)
                {
                    continue;
                }

                var directionOk = rule.Condition.ToLowerInvariant() switch
                {
                    "decrease" => delta < 0,
                    "increase" => delta > 0,
                    _ => true
                };
                if (!directionOk)
                {
                    continue;
                }

                var magnitude = Math.Abs(delta);
                if (rule.Min.HasValue && magnitude < rule.Min.Value)
                {
                    continue;
                }

                if (rule.Max.HasValue && magnitude > rule.Max.Value)
                {
                    continue;
                }

                fired[-1] = fired.GetValueOrDefault(-1) + 1;
                await PublishRuleAsync(definition, rule, composed, delta);
            }
        }
    }

    /// <summary>
    /// Actions whose value is a STATE, not a change: they name what the player has in
    /// hand right now. Everything else in a .MEM describes something happening - a life
    /// lost, a score rising - and only makes sense as a transition.
    /// </summary>
    private static bool IsStateAnnouncement(string action)
        => action.Equals("CHARACTER_SELECTED", StringComparison.OrdinalIgnoreCase)
           || action.Equals("WEAPON_SELECTED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What an address that NAMES something holds when we attach, said out loud once per
    /// session. Writing a .MEM means guessing where a game keeps its character; without
    /// this line, a wrong guess and a silent game look exactly the same from outside.
    ///
    /// Diagnostic only: the announcement itself now goes through the ordinary rule
    /// evaluation, which no longer requires a transition for a state.
    /// </summary>
    private void LogInitialState(MameLuaDefinition definition, int targetId, long value)
    {
        var naming = definition.Rules
            .Where(rule => rule.TargetId == targetId && rule.TargetId != 0 && IsStateAnnouncement(rule.Action))
            .ToList();
        if (naming.Count == 0) return;

        _logger.LogInformation(
            "MAME Lua: state address 0x{Address:X} first read = 0x{Value:X} - known values {Values}",
            naming[0].Address, value,
            string.Join(", ", naming.Select(r => $"0x{r.Value:X}={r.Description}")));
    }

    private async Task EvaluateTargetAsync(MameLuaDefinition definition, int targetId, long oldValue, long value, bool initialized)
    {
        // An address that names what the player holds moves a handful of times per game:
        // following it costs nothing and shows whether the byte lives at all.
        var named = definition.Rules.FirstOrDefault(rule =>
            rule.TargetId == targetId && rule.TargetId != 0 && IsStateAnnouncement(rule.Action));
        if (named != null)
        {
            _logger.LogInformation("MAME Lua: state address 0x{Address:X} 0x{Old:X} → 0x{New:X}",
                named.Address, oldValue, value);
        }

        foreach (var rule in definition.Rules.Where(rule => rule.TargetId == targetId && rule.TargetId != 0))
        {
            var trigger = ShouldTrigger(rule, oldValue, value, initialized, out var emittedValue, out var rate);
            if (!trigger || rule.NoLog)
            {
                continue;
            }

            await PublishRuleAsync(definition, rule, emittedValue, rate);
        }
    }

    private async Task PublishRuleAsync(MameLuaDefinition definition, MameLuaRule rule, long emittedValue, long rate)
    {
        {
            var payload = new
            {
                Source = "mame.lua",
                SystemId = definition.SystemId,
                definition.Rom,
                definition.DefinitionFile,
                signal = new
                {
                    Key = rule.Action,
                    Channel = "ACTION",
                    Name = rule.Action,
                    SourceDescription = rule.Description,
                    Address = $"0x{rule.Address:X}",
                    RuntimeAddress = $"0x{rule.RuntimeAddress:X}",
                    RawValueHex = $"0x{emittedValue:X}",
                    Value = emittedValue,
                    Rate = rate,
                    Color = rule.Color,
                    Family = rule.Family,
                    Ts = DateTime.UtcNow
                },
                actionType = rule.Action,
                sourceCategory = rule.Description,
                Value = emittedValue,
                Rate = rate,
                Address = $"0x{rule.Address:X}",
                RuntimeAddress = $"0x{rule.RuntimeAddress:X}",
                RawValueHex = $"0x{emittedValue:X}",
                color = rule.Color,
                family = rule.Family,
                player = rule.Player
            };

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "ingame.memory.changed",
                Payload = payload
            });

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = rule.Action.Contains("SCORE", StringComparison.OrdinalIgnoreCase)
                    ? "ingame.score.changed"
                    : "ingame.action",
                Payload = payload
            });
        }
    }

    private static bool ShouldTrigger(MameLuaRule rule, long oldValue, long value, bool initialized, out long emittedValue, out long rate)
    {
        // Aligne sur wrapper.cpp : le mask est applique a la valeur lue pour
        // toutes les conditions (les conditions bit_* comparent sur le mask brut).
        if (rule.Mask is long readMask && rule.Condition is not ("bit_true" or "bit_false"))
        {
            value &= readMask;
            oldValue &= readMask;
        }

        emittedValue = value;
        rate = value - oldValue;

        var condition = rule.Condition.ToLowerInvariant();

        if (rule.HasRange)
        {
            var oldComparable = ToBcdLikeInt(oldValue);
            var newComparable = ToBcdLikeInt(value);
            rate = newComparable - oldComparable;
            emittedValue = newComparable;

            var directionOk = condition switch
            {
                "decrease" => rate < 0,
                "increase" => rate > 0,
                _ => rate != 0
            };
            if (!directionOk)
            {
                return false;
            }

            var delta = Math.Abs(rate);
            if (rule.Min.HasValue && delta < rule.Min.Value)
            {
                return false;
            }

            if (rule.Max.HasValue && delta > rule.Max.Value)
            {
                return false;
            }

            return true;
        }

        // Table alignee sur wrapper.cpp, qui fait contrat. La ligne de partage y est
        // deliberee : une VARIATION (change, increase, decrease) exige une lecture
        // precedente, un ETAT (eq, neq, bit_*) n'en a pas besoin - il est vrai ou faux
        // des la premiere lecture. Le pont exigeait une transition partout, et rendait
        // donc invisible tout etat deja etabli quand la partie commence.
        var magnitude = Math.Abs(rate);
        var deltaInRange = (!rule.Min.HasValue || magnitude >= rule.Min.Value)
                           && (!rule.Max.HasValue || magnitude <= rule.Max.Value);

        switch (condition)
        {
            case "any":
            case "change":
            case "color": // reservee par le contrat : un changement de valeur
                // `value` present et non nul restreint le declenchement a cette valeur
                return initialized && value != oldValue && deltaInRange
                       && (rule.Value is not { } wanted || wanted == 0 || value == wanted);

            case "increase":
                return initialized && value > oldValue && deltaInRange;

            case "decrease":
                return initialized && value < oldValue && deltaInRange;

            case "eq":
            case "equal":
            case "equals":
                return rule.Value.HasValue && value == rule.Value.Value
                                           && (value != oldValue || !initialized);

            case "neq":
            case "ne":
            case "not_equal":
                return rule.Value.HasValue && value != rule.Value.Value
                                           && (value != oldValue || !initialized);

            case "bit_true":
                return rule.Mask is long trueMask && (value & trueMask) == trueMask
                                                  && ((oldValue & trueMask) != trueMask || !initialized);

            case "bit_false":
                return rule.Mask is long falseMask && (value & falseMask) == 0
                                                   && ((oldValue & falseMask) != 0 || !initialized);

            default:
                // Le wrapper ne declenche rien sur une condition qu'il ne connait pas.
                // Se rabattre sur "change" faisait vivre sur arcade des entrees mortes
                // sur console - deux comportements pour un meme fichier.
                return false;
        }
    }

    private static long ToBcdLikeInt(long value)
    {
        var hex = value.ToString("X", CultureInfo.InvariantCulture);
        return long.TryParse(hex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : value;
    }

    private MameLuaDefinition ResolveDefinition(string rawRom)
    {
        MameLuaDefinition? fallback = null;
        foreach (var systemId in DefinitionSystemFallbacks)
        {
            var definition = ResolveDefinition(rawRom, systemId);
            fallback ??= definition;
            if (definition.DefinitionExists)
            {
                return definition;
            }
        }

        return fallback ?? ResolveDefinition(rawRom, "arcade");
    }

    private MameLuaDefinition ResolveDefinition(string rawRom, string systemId)
    {
        var rom = NormalizeRom(rawRom);
        var aliasFile = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, "alias.json");
        var aliasMatched = false;

        if (File.Exists(aliasFile))
        {
            try
            {
                var loadedAliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile))
                    ?? new Dictionary<string, string>();
                // alias.json porte des variantes de casse volontaires : le
                // constructeur insensible a la casse JETTE sur doublons,
                // TryAdd (premiere occurrence gagne) est requis.
                var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in loadedAliases)
                {
                    aliases.TryAdd(pair.Key, pair.Value);
                }
                if (TryResolveAlias(aliases, rawRom, rom, out var alias))
                {
                    rom = NormalizeAliasKey(alias);
                    aliasMatched = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read MAME Lua ingame alias file {AliasFile}", aliasFile);
            }
        }

        var definitionFile = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, rom + ".MEM");
        var rules = File.Exists(definitionFile)
            ? ParseRules(File.ReadAllText(definitionFile))
            : new List<MameLuaRule>();
        var banks = LoadBanks(systemId, rom);

        var targets = new List<MameLuaTarget>();
        var targetMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var runtimeAddress = ApplyBanks(banks, rule.Address);
            rule.RuntimeAddress = runtimeAddress;

            // Les rules no_log/no_survey ne publient jamais : ne pas faire lire
            // leur adresse au pont Lua a chaque frame (TargetId 0 = jamais evaluee).
            if (rule.NoLog || rule.NoSurvey)
            {
                rule.TargetId = 0;
                continue;
            }

            if (rule.Digits > 1)
            {
                // score en tuiles : un watch u8 par chiffre, evaluation composee
                for (var i = 0; i < rule.Digits; i++)
                {
                    var dKey = $"{runtimeAddress + i:X}|u8";
                    if (!targetMap.TryGetValue(dKey, out var dId))
                    {
                        dId = targets.Count + 1;
                        targetMap[dKey] = dId;
                        targets.Add(new MameLuaTarget(dId, rule.Address + i, runtimeAddress + i, "u8"));
                    }

                    rule.DigitTargetIds.Add(dId);
                }

                rule.TargetId = 0; // jamais evaluee en cible simple
                continue;
            }

            var key = $"{runtimeAddress:X}|{rule.Type}";
            if (!targetMap.TryGetValue(key, out var targetId))
            {
                targetId = targets.Count + 1;
                targetMap[key] = targetId;
                targets.Add(new MameLuaTarget(targetId, rule.Address, runtimeAddress, rule.Type));
            }

            rule.TargetId = targetId;
        }

        return new MameLuaDefinition(
            systemId,
            rawRom,
            rom,
            definitionFile,
            aliasFile,
            aliasMatched,
            File.Exists(definitionFile),
            targets,
            rules);
    }

    private static List<MameLuaRule> ParseRules(string text)
    {
        var rules = new List<MameLuaRule>();
        var entries = ExtractEntryTables(text);
        foreach (var entry in entries)
        {
            var values = ParseEntryValues(entry.Table);
            if (!TryParseLong(values.GetValueOrDefault("address"), out var address))
            {
                continue;
            }

            var action = GetString(values, "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                continue;
            }

            var condition = GetString(values, "condition");
            rules.Add(new MameLuaRule
            {
                Address = address,
                Type = GetString(values, "type", "u8"),
                Condition = string.IsNullOrWhiteSpace(condition) ? "change" : condition,
                Action = action.Trim().ToUpperInvariant(),
                Description = GetString(values, "desc", action),
                Family = entry.Family,
                Color = GetString(values, "color"),
                NoLog = TryParseBool(values.GetValueOrDefault("no_log"), out var noLog) && noLog,
                NoSurvey = TryParseBool(values.GetValueOrDefault("no_survey"), out var noSurvey) && noSurvey,
                Min = TryParseLong(values.GetValueOrDefault("min"), out var min) ? min : null,
                Max = TryParseLong(values.GetValueOrDefault("max"), out var max) ? max : null,
                Value = TryParseLong(values.GetValueOrDefault("value"), out var exactValue) ? exactValue : null,
                Mask = TryParseLong(values.GetValueOrDefault("mask"), out var mask) ? mask : null,
                Player = TryParseLong(values.GetValueOrDefault("player"), out var player) ? player : null,
                Digits = TryParseLong(values.GetValueOrDefault("digits"), out var digits) ? (int)digits : 0,
                DigitZero = TryParseLong(values.GetValueOrDefault("digit_zero"), out var dz) ? dz : 0x30,
                Blank = TryParseLong(values.GetValueOrDefault("blank"), out var blank) ? blank : null,
                Scale = TryParseLong(values.GetValueOrDefault("scale"), out var scale) && scale > 0 ? scale : 1
            });
        }

        return rules;
    }

    private static List<MameLuaEntryTable> ExtractEntryTables(string text)
    {
        var results = new List<MameLuaEntryTable>();
        var stack = new Stack<string>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"' )
            {
                i = SkipString(text, i);
                continue;
            }

            var key = TryReadTableKeyBeforeBrace(text, i);
            if (key != null)
            {
                stack.Push(key);
            }

            if (text[i] != '{')
            {
                continue;
            }

            var end = FindMatchingBrace(text, i);
            if (end <= i)
            {
                continue;
            }

            var table = text.Substring(i, end - i + 1);
            if (Regex.IsMatch(table, @"^\{\s*address\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                results.Add(new MameLuaEntryTable(table, string.Join(".", stack.Reverse().Where(s => !IsStructuralKey(s)))));
            }

            if (key != null && stack.Count > 0)
            {
                stack.Pop();
            }
        }

        return results;
    }

    private static string? TryReadTableKeyBeforeBrace(string text, int index)
    {
        if (text[index] != '{')
        {
            return null;
        }

        var prefixStart = Math.Max(0, index - 120);
        var prefix = text.Substring(prefixStart, index - prefixStart);
        var bracket = Regex.Match(prefix, @"\[""(?<key>[^""]+)""\]\s*=\s*$");
        if (bracket.Success)
        {
            return bracket.Groups["key"].Value;
        }

        var plain = Regex.Match(prefix, @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*$");
        return plain.Success ? plain.Groups["key"].Value : null;
    }

    private static bool IsStructuralKey(string key) =>
        key.Equals("events", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("game", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("rom", StringComparison.OrdinalIgnoreCase);

    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int SkipString(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static Dictionary<string, string> ParseEntryValues(string entry)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in EntryValueRegex.Matches(entry))
        {
            values[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return values;
    }

    private static string GetString(Dictionary<string, string> values, string key, string fallback = "")
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"");
        }

        return value;
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        return !string.IsNullOrWhiteSpace(raw) &&
            bool.TryParse(raw, out value);
    }

    private static bool TryParseLong(string? raw, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// mame-banks.json (par systeme) : quand le blob RAM du wrapper concatene
    /// plusieurs banques (1943 : work E000 + video D000 + F000), la table par
    /// jeu traduit l'offset blob en adresse programme MAME. Sans table, le
    /// plugin Lua rebase l'offset sur la RAM principale de la machine.
    /// </summary>
    private IReadOnlyList<(long From, long Size, long Base)> LoadBanks(string systemId, string rom)
    {
        var path = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, "mame-banks.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(rom, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var banks = new List<(long, long, long)>();
            foreach (var entry in list.EnumerateArray())
            {
                if (TryParseLong(entry.GetProperty("from").GetString(), out var from) &&
                    TryParseLong(entry.GetProperty("size").GetString(), out var size) &&
                    TryParseLong(entry.GetProperty("base").GetString(), out var baseAddr))
                {
                    banks.Add((from, size, baseAddr));
                }
            }

            return banks;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "mame-banks.json illisible pour {System}/{Rom}", systemId, rom);
            return [];
        }
    }

    private static long ApplyBanks(IReadOnlyList<(long From, long Size, long Base)> banks, long logicalAddress)
    {
        foreach (var (from, size, baseAddr) in banks)
        {
            if (logicalAddress >= from && logicalAddress < from + size)
            {
                return baseAddr + (logicalAddress - from);
            }
        }

        return logicalAddress;
    }

    private static string NormalizeRom(string rom)
    {
        var normalized = (rom ?? string.Empty).Trim();
        if (normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetFileNameWithoutExtension(normalized);
        }

        return normalized.ToLowerInvariant();
    }

    private static bool TryResolveAlias(
        IReadOnlyDictionary<string, string> aliases,
        string rawRom,
        string normalizedRom,
        out string alias)
    {
        if (!string.IsNullOrWhiteSpace(rawRom) && aliases.TryGetValue(rawRom, out alias!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedRom) && aliases.TryGetValue(normalizedRom, out alias!))
        {
            return true;
        }

        var normalizedKey = NormalizeAliasKey(rawRom);
        if (!string.IsNullOrWhiteSpace(normalizedKey) && aliases.TryGetValue(normalizedKey, out alias!))
        {
            return true;
        }

        foreach (var entry in aliases)
        {
            if (string.Equals(NormalizeAliasKey(entry.Key), normalizedRom, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeAliasKey(entry.Key), normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                alias = entry.Value;
                return true;
            }
        }

        alias = string.Empty;
        return false;
    }

    private static string NormalizeAliasKey(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        var archiveDisplay = Regex.Match(
            normalized,
            @"^(?<name>.+?)\.(?:zip|7z)\s*<",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (archiveDisplay.Success)
        {
            normalized = archiveDisplay.Groups["name"].Value;
        }
        else if (normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetFileNameWithoutExtension(normalized);
        }

        normalized = normalized.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    // ── Scoring certifié MAME (parité wrapper RetroArch) ────────────────────────
    // Le reporter est AGNOSTIQUE : il assemble le passeport à partir des events
    // scoring.listener.attestation + scoring.listener.session (comme pour le wrapper,
    // qui les émet par pipe). Identité = Voie A étendue à MAME (content = md5 gamelist
    // MAME, core = mame64.exe, listener = plugin Lua, mem = .MEM). RESTE : un profil
    // engine=mame_standalone + l'homologation du SHA du plugin + la VALIDATION en jeu.
    private const string MameLuaListenerVersion = "mame-lua-0.2.0";

    private async Task PublishScoringAttestationAsync(MameLuaDefinition definition)
    {
        try
        {
            var (listenerSha, coreSha, memSha, contentSha1) = ResolveMameIdentity(definition);
            if (listenerSha is null)
            {
                return;   // pas de listener mesurable -> pas d'attestation (le reporter n'insiste pas)
            }
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "scoring.listener.attestation",
                Payload = new
                {
                    Source = "mame.lua.ingame",
                    definition.SystemId,
                    definition.Rom,
                    ListenerSha256 = listenerSha,
                    CoreSha256 = coreSha,
                    MemSha256 = memSha,
                    ContentSha256 = (string?)null,
                    ContentMd5 = (string?)null,
                    ContentSha1 = contentSha1,   // MAME : sha1 du set (gamelist) ; intégrité = DAT MAME
                    WrapperVersion = MameLuaListenerVersion,
                    SessionNonce = Guid.NewGuid().ToString("N"),
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua : attestation de scoring non publiée.");
        }
    }

    private async Task PublishScoringSessionAsync(MameLuaDefinition definition, DateTime start, long fastForward, string coreOptions)
    {
        try
        {
            var monotonicMs = (long)Math.Max(0, (DateTime.UtcNow - start).TotalMilliseconds);
            var session = new
            {
                frame_count = 0,
                monotonic_ms = monotonicMs,
                resets = 0,
                save_state_loads = 0,
                cheats = 0,
                rewind = 0,
                runahead = 0,
                fast_forward = fastForward,
                netplay = 0,
                continues = 0,
                core_options = coreOptions,   // Phase E : chaîne canonique des réglages (vide si non captés)
            };
            // Format attendu par le reporter (comme le wrapper) : { SystemId, Rom, Session=<JSON string> }.
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "scoring.listener.session",
                Payload = new
                {
                    definition.SystemId,
                    definition.Rom,
                    Session = JsonSerializer.Serialize(session),
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua : session de scoring non publiée.");
        }
    }

    // Voie A étendue à MAME. Chemins dérivés du .MEM (resources/ram/<sys>/<rom>.MEM) ;
    // repli gracieux si un fichier manque (le champ reste null, le profil le gère).
    private (string? listener, string? core, string? mem, string? contentSha1) ResolveMameIdentity(MameLuaDefinition definition)
    {
        static string? Sha(string? path) =>
            (!string.IsNullOrEmpty(path) && File.Exists(path)) ? Crypto.Sha256Hex(File.ReadAllBytes(path)) : null;

        var memSha = Sha(definition.DefinitionFile);
        string? listenerSha = null, coreSha = null, contentSha1 = null;
        try
        {
            var ramRoot = Path.GetDirectoryName(Path.GetDirectoryName(definition.DefinitionFile));   // .../resources/ram
            if (ramRoot != null)
            {
                listenerSha = Sha(Path.Combine(ramRoot, "tools", "mame_apiexpose_ingame", "init.lua"));
                var resourcesRoot = Path.GetDirectoryName(ramRoot);                                   // .../resources
                if (resourcesRoot != null)
                {
                    // Lookup par RawRom = le NOM DE SET MAME (ex. "19xx"), pas le nom .MEM
                    // (ex. "19xx-the-war-against-destiny") : la gamelist est indexée par set.
                    contentSha1 = LookupMameGamelistSha1(Path.Combine(resourcesRoot, "gamelist", "systems", "mame_lt.json"), definition.RawRom);
                    var pluginRoot = Path.GetDirectoryName(resourcesRoot);                            // .../APIExpose
                    var retrobatRoot = pluginRoot != null ? Path.GetDirectoryName(Path.GetDirectoryName(pluginRoot)) : null;   // <RetroBat>
                    if (retrobatRoot != null)
                    {
                        foreach (var c in new[] { "mame64.exe", "mame.exe" })
                        {
                            var p = Path.Combine(retrobatRoot, "emulators", "mame", c);
                            if (File.Exists(p)) { coreSha = Sha(p); break; }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua : résolution d'identité partielle.");
        }
        return (listenerSha, coreSha, memSha, contentSha1);
    }

    // Gamelist MAME = JSONL. Renvoie le sha1 du set == rom (= ROM programme principal, vérifié
    // présent dans les vrais romsets). L'intégrité du romset est garantie par MAME (DAT).
    private string? LookupMameGamelistSha1(string gamelistPath, string rom)
    {
        if (!File.Exists(gamelistPath)) return null;
        try
        {
            foreach (var line in File.ReadLines(gamelistPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.IndexOf(rom, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                var set = root.TryGetProperty("set", out var sv) ? sv.GetString() : null;
                if (!string.Equals(id, rom, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(set, rom, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (root.TryGetProperty("hsh", out var hsh) && hsh.ValueKind == JsonValueKind.Array && hsh.GetArrayLength() > 0
                    && hsh[0].TryGetProperty("sha1", out var sha1v))
                {
                    return sha1v.GetString()?.ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua : lecture gamelist MAME impossible.");
        }
        return null;
    }

    private async Task PublishSessionStartedAsync(MameLuaDefinition definition, string gameName)
    {
        _arbitration.MarkMameLuaSessionStarted(definition.SystemId, definition.Rom, definition.DefinitionFile);

        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "ingame.mame.session.started",
            Payload = new
            {
                Source = "mame.lua",
                definition.SystemId,
                definition.RawRom,
                definition.Rom,
                GameName = gameName,
                definition.DefinitionFile,
                definition.DefinitionExists,
                WatchCount = definition.Targets.Count
            }
        });
    }

    private async Task PublishSessionStoppedAsync(MameLuaDefinition definition)
    {
        _arbitration.MarkMameLuaSessionStopped(definition.SystemId, definition.Rom, definition.DefinitionFile);

        // Symetrique du demarrage, et pas seulement pour la forme : tant qu'une session Lua est
        // consideree active, l'arbitrage SUPPRIME les signaux du wrapper RetroArch pour tout jeu
        // arcade. Une session qui ne se fermerait pas rendrait donc muet le scoring sous
        // RetroArch, sans la moindre trace. C'est exactement ce qu'on ne veut pas avoir a deviner.
        _logger?.LogInformation(
            "MAME Lua ingame session stopped: rom={Rom} definition={DefinitionFile}",
            definition.Rom,
            definition.DefinitionFile);

        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "ingame.mame.session.stopped",
            Payload = new
            {
                Source = "mame.lua",
                definition.SystemId,
                definition.RawRom,
                definition.Rom,
                definition.DefinitionFile
            }
        });
    }

    private void UpdateSession(string endpoint, MameLuaDefinition definition, bool connected, string lastRawLine,
        IReadOnlyDictionary<int, int>? fired = null)
    {
        var byAddress = new Dictionary<string, int>();
        foreach (var target in definition.Targets)
        {
            byAddress[$"0x{target.RuntimeAddress:X}|{target.Type}"] =
                fired?.GetValueOrDefault(target.Id) ?? 0;
        }

        if (definition.Rules.Any(r => r.Digits > 1))
        {
            byAddress["composite|score"] = fired?.GetValueOrDefault(-1) ?? 0;
        }

        lock (_stateLock)
        {
            _sessions[endpoint] = new MameLuaSessionSnapshot(
                endpoint,
                connected,
                definition.Rom,
                definition.DefinitionFile,
                definition.DefinitionExists,
                definition.Targets.Count,
                DateTime.UtcNow,
                lastRawLine,
                byAddress);
        }
    }

    /// <summary>Instantane des sessions pour /api/v1/mamelua/sessions (banc de validation).</summary>
    public IReadOnlyList<object> SessionsSnapshot()
    {
        lock (_stateLock)
        {
            return _sessions.Values
                .Select(s => (object)new
                {
                    s.Endpoint,
                    s.Connected,
                    s.Rom,
                    s.DefinitionFile,
                    s.DefinitionExists,
                    s.WatchCount,
                    s.LastMessageAt,
                    firedByAddress = s.FiredByAddress,
                    silentTargets = s.FiredByAddress.Count(kv => kv.Value == 0)
                })
                .ToList();
        }
    }

    private sealed record DiscoveryArm(string Host, int Port, int Hz);

    private sealed record MameLuaTarget(int Id, long Address, long RuntimeAddress, string Type);

    private sealed record MameLuaDefinition(
        string SystemId,
        string RawRom,
        string Rom,
        string DefinitionFile,
        string AliasFile,
        bool AliasMatched,
        bool DefinitionExists,
        IReadOnlyList<MameLuaTarget> Targets,
        IReadOnlyList<MameLuaRule> Rules);

    private sealed class MameLuaRule
    {
        public int TargetId { get; set; }
        public long Address { get; init; }
        public long RuntimeAddress { get; set; }
        public string Type { get; init; } = "u8";
        public string Condition { get; init; } = "change";
        public string Action { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Family { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public bool NoLog { get; init; }
        public bool NoSurvey { get; init; }
        public long? Min { get; init; }
        public long? Max { get; init; }
        public long? Value { get; init; }
        public long? Mask { get; init; }
        public long? Player { get; init; }
        public bool HasRange => Min.HasValue || Max.HasValue;

        /// <summary>Score affiche en tuiles : digits octets consecutifs,
        /// chiffre = tuile - DigitZero, Blank = 0 ; valeur composee x Scale.</summary>
        public int Digits { get; init; }
        public long DigitZero { get; init; }
        public long? Blank { get; init; }
        public long Scale { get; init; } = 1;
        public List<int> DigitTargetIds { get; } = [];
        public string CompositeKey => $"{Address:X}*{Digits}";
    }

    private sealed record MameLuaEntryTable(string Table, string Family);

    private sealed record MameLuaSessionSnapshot(
        string Endpoint,
        bool Connected,
        string Rom,
        string DefinitionFile,
        bool DefinitionExists,
        int WatchCount,
        DateTime LastMessageAt,
        string LastRawLine,
        IReadOnlyDictionary<string, int> FiredByAddress);
}
