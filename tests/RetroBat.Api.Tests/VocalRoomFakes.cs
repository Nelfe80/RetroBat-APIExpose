using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NelfePlay.VocalRoom.Protocol;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.VocalRoom;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un faux NelfePlay-VocalRoom.exe, dans le processus des tests : il parle le vrai protocole (trames, poignee de main
/// HMAC, reponses, evenements) sur un vrai named pipe, cote client, pendant que l'API garde son vrai cote pilote
/// (<see cref="PilotConnection"/>). Le test regle ce qu'il annonce et ce qu'il fait.
/// </summary>
internal sealed class FauxVocalRoom : IVocalRoomLanceur
{
    public string Version { get; set; } = "0.2.0+test";
    public int Protocole { get; set; } = 1;
    public bool PreuveFausse { get; set; }
    public bool SansJeton { get; set; }
    public string? SdkVersion { get; set; } = "1.10.19337";

    /// <summary>Le compte Discord que « le SDK » connecte ; un expected_user_id different donne user_mismatch.</summary>
    public string UtilisateurDiscord { get; set; } = "123456789012345678";

    /// <summary>Le faux exe « plante » (ferme le pipe et sort) a cette commande, une fois par lancement si demande.</summary>
    public string? PlanterA { get; set; }

    /// <summary>Combien de lancements plantent a <see cref="PlanterA"/> (les suivants se comportent bien).</summary>
    public int Plantages { get; set; } = int.MaxValue;

    /// <summary>Toutes les commandes recues, tous lancements confondus (le jeton est garde pour la verification).</summary>
    public ConcurrentQueue<(int Lancement, string Commande, JsonObject Charge)> Commandes { get; } = new();

    public int Lancements => _lancements;

    public FauxProcessus? Dernier { get; private set; }

    private int _lancements;

    public Task<IVocalRoomProcessus> LancerAsync(string exe, string? dossierDuJournal, CancellationToken ct)
    {
        var numero = Interlocked.Increment(ref _lancements);
        var nom = Pairing.NewPipeName();
        var serveur = PipePeers.CreateServer(nom);
        var client = PipePeers.CreateClient(nom);
        var attente = serveur.WaitForConnectionAsync(ct);
        client.Connect(5000);
        attente.GetAwaiter().GetResult();
        var secret = Pairing.NewSecret();
        var p = new FauxProcessus(this, numero, serveur, client, secret);
        Dernier = p;
        return Task.FromResult<IVocalRoomProcessus>(p);
    }

    internal sealed class FauxProcessus : IVocalRoomProcessus
    {
        private readonly FauxVocalRoom _faux;
        private readonly int _numero;
        private readonly NamedPipeServerStream _serveur;
        private readonly NamedPipeClientStream _client;
        private readonly byte[] _secret;
        private readonly TaskCompletionSource _sortie = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _ecriture = new(1, 1);
        private readonly string _nonce = Pairing.NewNonce();
        private long _seq;
        private bool _apparie;
        private string _etat = "STARTING";
        private bool _micro = true;
        private bool _casque;

        public FauxProcessus(FauxVocalRoom faux, int numero, NamedPipeServerStream serveur, NamedPipeClientStream client, byte[] secret)
        {
            _faux = faux;
            _numero = numero;
            _serveur = serveur;
            _client = client;
            _secret = secret;
            Connexion = PilotConnection.Start(serveur, secret);
            _ = Task.Run(BoucleAsync);
        }

        public int Pid => 40000 + _numero;

        /// <summary>Ce qui a arrete la boucle du faux exe, s'il y a lieu (diagnostic des tests).</summary>
        public Exception? Erreur { get; private set; }
        public PilotConnection Connexion { get; }
        public Task Sortie => _sortie.Task;
        public int? CodeDeSortie { get; private set; }

        public async Task<int?> AttendreLaSortieAsync(TimeSpan delai)
        {
            try
            {
                await _sortie.Task.WaitAsync(delai).ConfigureAwait(false);
                return CodeDeSortie;
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public void Tuer() => Sortir(-1);

        public async ValueTask DisposeAsync()
        {
            Sortir(CodeDeSortie ?? -1);
            await Connexion.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Le faux exe annonce un etat (comme le SDK le ferait).</summary>
        public Task EmettreAsync(string etat, int participants = 0)
        {
            _etat = etat;
            return EvenementAsync(Events.VoiceState, new JsonObject
            {
                ["state"] = etat, ["connected"] = etat == "IN_CALL", ["mic_muted"] = _micro, ["deafened"] = _casque,
                ["participants"] = participants, ["reason"] = "test",
            });
        }

        public Task ErreurAsync(string code) => EvenementAsync(Events.VoiceError, new JsonObject { ["code"] = code, ["message"] = "test" });

        /// <summary>Plantage : le pipe se ferme, le processus sort avec le code 255.</summary>
        public void Planter() => Sortir(255);

        private void Sortir(int code)
        {
            if (_sortie.Task.IsCompleted) return;
            CodeDeSortie = code;
            try { _client.Dispose(); } catch { }
            _sortie.TrySetResult();
        }

        private async Task BoucleAsync()
        {
            try
            {
                await EvenementAsync(Events.HandshakeChallenge, new JsonObject
                {
                    ["client_nonce"] = _nonce, ["protocol_min"] = _faux.Protocole, ["protocol_max"] = _faux.Protocole,
                    ["app_version"] = _faux.Version, ["pid"] = Pid,
                }, avantAppairage: true).ConfigureAwait(false);
                while (!_sortie.Task.IsCompleted)
                {
                    var m = await Framing.ReadAsync(_client, CancellationToken.None).ConfigureAwait(false);
                    if (m is null) break;
                    await TraiterAsync(m).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Erreur = ex;
            }
            finally
            {
                Sortir(CodeDeSortie ?? 4);
            }
        }

        private async Task TraiterAsync(JsonObject m)
        {
            var id = Messages.Str(m, "request_id");
            var commande = Messages.Str(m, "command") ?? "";
            var charge = Messages.Obj(m, "payload") ?? new JsonObject();
            _faux.Commandes.Enqueue((_numero, commande, (JsonObject)charge.DeepClone()));

            if (commande == _faux.PlanterA && _numero <= _faux.Plantages)
            {
                Planter();
                return;
            }

            JsonObject r;
            switch (commande)
            {
                case Commands.Hello:
                    var serveur = Messages.Str(charge, "server_nonce")!;
                    if (!Pairing.ProofEquals(Pairing.ServerProof(_secret, _nonce, serveur), Messages.Str(charge, "proof")))
                    {
                        r = Messages.Error(id, ErrorCodes.Unauthorized, "non");
                        break;
                    }

                    if (_faux.Protocole != 1)
                    {
                        r = Messages.Error(id, ErrorCodes.UnsupportedVersion, "non");
                        break;
                    }

                    _apparie = true;
                    _etat = "AUTH_REQUIRED";
                    var caps = new JsonArray("hello", "health", "session.attach", "session.detach", "voice.mute", "voice.deafen", "shutdown");
                    if (!_faux.SansJeton) caps.Add(JsonValue.Create("auth.apply_token"));
                    r = Messages.Ok(id, new JsonObject
                    {
                        ["protocol"] = _faux.Protocole,
                        ["proof"] = _faux.PreuveFausse ? new string('0', 64) : Pairing.ClientProof(_secret, _nonce, serveur),
                        ["app_version"] = _faux.Version,
                        ["sdk_version"] = _faux.SdkVersion,
                        ["sdk_expected"] = "1.10.19337",
                        ["state"] = _etat,
                        ["capabilities"] = caps,
                    });
                    break;
                case Commands.Health:
                    r = Messages.Ok(id, new JsonObject { ["alive"] = true, ["state"] = _etat, ["sdk_available"] = true });
                    break;
                case Commands.AuthApplyToken:
                    await EcrireAsync(Messages.Ok(id, new JsonObject { ["accepted"] = true, ["state"] = _etat })).ConfigureAwait(false);
                    if (Messages.Str(charge, "expected_user_id") is { } attendu && attendu != _faux.UtilisateurDiscord)
                    {
                        await ErreurAsync("user_mismatch").ConfigureAwait(false);
                        await EmettreAsync("AUTH_REQUIRED").ConfigureAwait(false);
                    }
                    else
                    {
                        await EmettreAsync("READY").ConfigureAwait(false);
                    }

                    return;
                case Commands.SessionAttach:
                    _micro = Messages.Bool(charge, "mic_muted") ?? _micro;
                    r = Messages.Ok(id, new JsonObject { ["accepted"] = true, ["state"] = _etat });
                    break;
                case Commands.SessionDetach:
                    r = Messages.Ok(id, new JsonObject { ["detached"] = true, ["state"] = "READY" });
                    break;
                case Commands.VoiceMute:
                    _micro = Messages.Bool(charge, "muted") ?? _micro;
                    r = Messages.Ok(id, new JsonObject { ["mic_muted"] = _micro, ["deafened"] = _casque });
                    break;
                case Commands.VoiceDeafen:
                    _casque = Messages.Bool(charge, "deafened") ?? _casque;
                    r = Messages.Ok(id, new JsonObject { ["mic_muted"] = _micro, ["deafened"] = _casque });
                    break;
                case Commands.Shutdown:
                    await EcrireAsync(Messages.Ok(id, new JsonObject { ["stopping"] = true })).ConfigureAwait(false);
                    Sortir(0);
                    return;
                default:
                    r = Messages.Error(id, ErrorCodes.UnknownCommand, "non");
                    break;
            }

            await EcrireAsync(r).ConfigureAwait(false);
        }

        private Task EvenementAsync(string nom, JsonObject charge, bool avantAppairage = false)
        {
            if (!avantAppairage && !_apparie) return Task.CompletedTask;
            return EcrireAsync(Messages.Event(nom, Interlocked.Increment(ref _seq), charge));
        }

        private async Task EcrireAsync(JsonObject m)
        {
            await _ecriture.WaitAsync().ConfigureAwait(false);
            try { await Framing.WriteAsync(_client, m, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Erreur ??= ex; }
            finally { _ecriture.Release(); }
        }
    }
}

/// <summary>Le site, simule : chaque route rend ce que le test a pose, et chaque appel est note (sans rien filtrer).</summary>
internal sealed class FauxSiteVocal : HttpMessageHandler
{
    public ConcurrentQueue<(string Route, JsonObject? Corps)> Appels { get; } = new();

    public JsonObject Profil { get; set; } = new()
    {
        ["ok"] = true, ["voice_supported"] = true, ["discord_linked"] = true, ["voice_consent"] = true,
        ["voice_preference"] = "eligible_auto", ["mic_default"] = "muted", ["consent_version"] = 1,
    };

    public Func<string, JsonObject> Session { get; set; } = room => Voix(room, 1, "ready", "auto");

    public JsonObject Jeton { get; set; } = new()
    {
        ["ok"] = true, ["access_token"] = "jeton-secret-de-test-123", ["token_type"] = "Bearer", ["expires_at"] = 1794700000,
        ["scopes"] = "openid sdk.social_layer", ["discord_user_id"] = "123456789012345678",
    };

    public Func<string, JsonObject> Stats { get; set; } = _ => new JsonObject { ["ok"] = true, ["lobby"] = "open", ["remaining"] = 2, ["detach"] = false };

    public HttpStatusCode? Statut { get; set; }

    public static JsonObject Voix(string room, long generation, string statut, string join, string? raison = null) => new()
    {
        ["ok"] = true,
        ["voice"] = new JsonObject
        {
            ["room"] = room, ["generation"] = generation, ["status"] = statut,
            ["lobby_id"] = statut == "ready" ? "123456789012345678" : null, ["join"] = join, ["mic_muted"] = true, ["reason"] = raison,
        },
    };

    public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("https://nelfeplay.test") };

    public int Nombre(string route) => Appels.Count(a => a.Route == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath.Replace("/api/v1/agent/voice/", "", StringComparison.Ordinal);
        JsonObject? corps = null;
        if (request.Content is not null)
        {
            corps = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
        }

        Appels.Enqueue((route, corps));
        if (Statut is { } statut)
        {
            return new HttpResponseMessage(statut) { Content = new StringContent(statut == HttpStatusCode.NotFound ? "{\"error\":\"not_found\"}" : "") };
        }

        var room = Messages.Str(corps, "room") ?? "";
        var reponse = route switch
        {
            "profile" => Profil,
            "session" => Session(room),
            "token" => Jeton,
            "stats" => Stats(Messages.Str(corps, "status") ?? ""),
            "leave" => new JsonObject { ["ok"] = true },
            _ => new JsonObject { ["ok"] = false, ["reason"] = "bad_request" },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reponse.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

internal sealed class OptionsDuVocal(ApiExposeOptions valeur) : IOptionsMonitor<ApiExposeOptions>
{
    public ApiExposeOptions CurrentValue => valeur;
    public ApiExposeOptions Get(string? name) => valeur;
    public IDisposable? OnChange(Action<ApiExposeOptions, string?> listener) => null;
}

/// <summary>Un journal qui garde tout, pour verifier que le jeton n'y passe jamais.</summary>
internal sealed class JournalCapture<T> : ILogger<T>
{
    public ConcurrentQueue<string> Lignes { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lignes.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
}

/// <summary>Un dossier d'APIExpose de test, avec le lot VocalRoom (fichiers vides : le faux exe ne les lit pas).</summary>
internal sealed class RacineDuVocal : IDisposable
{
    public string Chemin { get; } = Path.Combine(Path.GetTempPath(), "vocalroom-test-" + Guid.NewGuid().ToString("N"));

    public RacineDuVocal(bool avecLeLot = true)
    {
        Directory.CreateDirectory(Chemin);
        if (avecLeLot)
        {
            foreach (var f in VocalRoomFichiers.Obligatoires) File.WriteAllText(Path.Combine(Chemin, f), "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Chemin, true); } catch { }
    }
}

internal static class Attendre
{
    public static async Task QueAsync(Func<bool> condition, int ms = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("condition non atteinte");
            await Task.Delay(20);
        }
    }
}
