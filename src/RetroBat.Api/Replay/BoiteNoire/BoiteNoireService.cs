using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RetroBat.Api.Deck;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.BoiteNoire;

/// <summary>
/// LA BOITE NOIRE D'UNE PARTIE (CDC AI Commentator v1.1, section 17 ; demande du 11/11). Le passeport ne garde que la
/// courbe du score et le replay que les entrees : les signaux du .MEM (rang cache, vies, bombes, stage, jauges...) ne
/// vivaient qu'en direct. Ce service les ecrit, tels que le bus les publie, dans un fichier compact par replay
/// (<c>state/nelfenet/replay/blackbox/&lt;replay_id&gt;.blackbox.jsonl.gz</c>), avec leur sens du .SMEM quand il est
/// connu, le numero d'image du coeur et l'instant depuis le debut de l'enregistrement.
/// <list type="bullet">
/// <item>LECTURE SEULE DU BUS : rien ici ne touche au score, au passeport, au replay, au film ni a la partie. Le
/// gestionnaire d'evenement ne fait que poser une ligne dans une file bornee ; l'ecriture se fait a part. File pleine :
/// la ligne la plus ancienne part (comptee), jamais d'attente du jeu.</item>
/// <item>Une boite par replay, du <c>replay.recording.started</c> au <c>replay.recording.stopped</c> (ou a la fin du
/// jeu) : meme identifiant que le replay, gardee et purgee avec lui (une boite dont le manifeste a disparu est effacee
/// au demarrage).</item>
/// <item>Coupee par <c>Replay:BlackBox:Enabled = false</c>.</item>
/// </list>
/// Format <c>nelfeplay.blackbox/1</c> : une ligne JSON d'en-tete, puis une ligne par evenement : <c>f</c> image du coeur
/// (null si la source ne la donne pas), <c>t</c> millisecondes depuis le debut, <c>s</c> identifiant standard du .SMEM
/// (absent si le .SMEM ne le nomme pas), <c>k</c> cle du .MEM (CANAL.ACTION), <c>a</c> adresse, <c>p</c> joueur,
/// <c>v</c> valeur brute. Les lignes de score portent <c>s = players.N.score</c>.
/// </summary>
public sealed class BoiteNoireService : IHostedService, IDisposable
{
    public const string Schema = "nelfeplay.blackbox/1";
    public const int FileMax = 65_536;

    private readonly IEventBus _bus;
    private readonly ILogger<BoiteNoireService>? _journal;
    private readonly bool _actif;
    private readonly string _racine;
    private readonly string _manifestes;
    private readonly Channel<Element> _file;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private Task? _ecrivain;
    private CancellationTokenSource? _arret;

    // Etat de la session, tenu sous verrou (le bus appelle depuis n'importe quel fil).
    private string? _replayId;
    private DateTime _debutUtc;
    private DeckSemantics? _sens;
    private long _derniereImage;
    private long _perdues;

    private abstract record Element;
    private sealed record Ouvrir(string ReplayId, string Entete) : Element;
    /// <summary>Les champs d'une ligne : la serialisation se fait chez l'ecrivain, jamais sur le fil du bus.</summary>
    private sealed record Ligne(Dictionary<string, object?> Champs) : Element;
    private sealed record Fermer(string ReplayId, string Pied) : Element;

    public BoiteNoireService(IEventBus bus, IConfiguration? configuration = null, ILogger<BoiteNoireService>? journal = null)
        : this(bus, configuration?.GetValue("Replay:BlackBox:Enabled", true) ?? true,
            Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay"), journal)
    {
    }

    /// <summary>Racine explicite (le magasin des replays) : pour les tests, qui ne touchent jamais au vrai.</summary>
    internal BoiteNoireService(IEventBus bus, bool actif, string racineDesReplays, ILogger<BoiteNoireService>? journal = null)
    {
        _bus = bus;
        _actif = actif;
        _journal = journal;
        _racine = Path.Combine(racineDesReplays, "blackbox");
        _manifestes = Path.Combine(racineDesReplays, "manifests");
        _file = Channel.CreateBounded<Element>(new BoundedChannelOptions(FileMax)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
        }, _ => Interlocked.Increment(ref _perdues));
    }

    /// <summary>Le fichier de la boite d'un replay.</summary>
    public string Chemin(string replayId) => Path.Combine(_racine, replayId + ".blackbox.jsonl.gz");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_actif)
        {
            _journal?.LogInformation("Boite noire coupee (Replay:BlackBox:Enabled = false).");
            return Task.CompletedTask;
        }
        Directory.CreateDirectory(_racine);
        PurgerLesOrphelines();
        _arret = new CancellationTokenSource();
        _ecrivain = Task.Run(() => EcrireAsync(_arret.Token));
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        string? ouvert;
        lock (_verrou) ouvert = _replayId;
        if (ouvert is not null) Clore("arret de l'API", null);
        _file.Writer.TryComplete();
        if (_ecrivain is not null)
        {
            try { await _ecrivain.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* l'API s'arrete : on ne la retient pas */ }
        }
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _arret?.Cancel();
        _arret?.Dispose();
    }

    // ── bus (rien de lent ici) ────────────────────────────────────────────────────────────────────────────

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "replay.recording.started":
                    Ouvrir_(DeckStateService.Texte(e.Payload, "ReplayId"), e);
                    break;
                case "replay.recording.stopped":
                    Clore("fin de l'enregistrement", DeckStateService.Entier(DeckStateService.Champ(e.Payload, "LastFrame")));
                    break;
                case "ui.game.ended":
                    Clore("fin du jeu", null);
                    break;
                case "deck.semantics":
                    if (e.Payload is DeckSemantics sens)
                    {
                        lock (_verrou) _sens = sens;
                        Poser((new Dictionary<string, object?>
                        {
                            ["t"] = Ms(e.Ts), ["sem"] = sens.Status, ["smem_sha256"] = sens.SmemSha256,
                            ["mem_sha256"] = sens.MemSha256, ["smem_version"] = sens.Version,
                        }));
                    }
                    break;
                case "retroarch.memory.changed":
                case "ingame.memory.changed":
                    NoterSignal(e);
                    break;
                case "score.live.changed":
                    var joueur = DeckStateService.Entier(DeckStateService.Champ(e.Payload, "Player")) ?? 1;
                    Poser((new Dictionary<string, object?>
                    {
                        ["f"] = null, ["t"] = Ms(e.Ts), ["s"] = "players." + joueur.ToString(CultureInfo.InvariantCulture) + ".score",
                        ["v"] = DeckStateService.Entier(DeckStateService.Champ(e.Payload, "Score")),
                    }));
                    break;
                case "scoring.run.ended":
                    Poser((new Dictionary<string, object?> { ["t"] = Ms(e.Ts), ["e"] = "run_end" }));
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Boite noire : evenement {Type} ignore.", e.Type);
        }
    }

    private void NoterSignal(EventEnvelope e)
    {
        var s = DeckStateService.Champ(e.Payload, "signal") ?? DeckStateService.Champ(e.Payload, "Signal");
        if (s is null) return;
        var adresse = DeckStateService.Texte(s, "Address");
        var canal = DeckStateService.Texte(s, "Channel");
        var nom = DeckStateService.Texte(s, "Name");
        var joueur = (int?)DeckStateService.Entier(DeckStateService.Champ(s, "Player"))
                     ?? (int?)DeckStateService.Entier(DeckStateService.Champ(e.Payload, "player"));
        var image = DeckStateService.Entier(DeckStateService.Champ(s, "Frame"));
        DeckSemantics? sens;
        lock (_verrou)
        {
            if (_replayId is null) return;
            sens = _sens;
            if (image is long f) _derniereImage = f;
        }
        Poser((new Dictionary<string, object?>
        {
            ["f"] = image, ["t"] = Ms(e.Ts), ["s"] = IdentifiantStandard(sens, adresse, nom, joueur),
            ["k"] = (canal ?? "") + "." + (nom ?? ""), ["a"] = adresse, ["p"] = joueur,
            ["v"] = DeckStateService.Entier(DeckStateService.Champ(s, "Value")),
        }));
    }

    private void Ouvrir_(string? replayId, EventEnvelope e)
    {
        if (string.IsNullOrWhiteSpace(replayId) || !replayId.StartsWith("rp_", StringComparison.Ordinal)) return;
        string? precedent;
        lock (_verrou) precedent = _replayId;
        if (precedent is not null) Clore("nouvel enregistrement", null);
        lock (_verrou)
        {
            _replayId = replayId;
            _debutUtc = e.Ts == default ? DateTime.UtcNow : e.Ts;
            _derniereImage = 0;
        }
        _file.Writer.TryWrite(new Ouvrir(replayId, Json(new Dictionary<string, object?>
        {
            ["schema"] = Schema, ["replay_id"] = replayId,
            ["session_id"] = DeckStateService.Texte(e.Payload, "SessionId"),
            ["system"] = DeckStateService.Texte(e.Payload, "System"), ["game"] = DeckStateService.Texte(e.Payload, "Game"),
            ["started_utc"] = _debutUtc.ToString("O", CultureInfo.InvariantCulture),
            ["source"] = "live",
        })));
    }

    private void Clore(string raison, long? derniereImageDuReplay)
    {
        string? id;
        long image, perdues;
        lock (_verrou)
        {
            id = _replayId;
            if (id is null) return;
            _replayId = null;
            image = _derniereImage;
            perdues = Interlocked.Exchange(ref _perdues, 0);
        }
        _file.Writer.TryWrite(new Fermer(id, Json(new Dictionary<string, object?>
        {
            ["end"] = raison, ["t"] = Ms(DateTime.UtcNow), ["last_core_frame"] = image,
            ["last_replay_frame"] = derniereImageDuReplay, ["dropped"] = perdues,
        })));
    }

    private void Poser(Dictionary<string, object?> ligne)
    {
        lock (_verrou)
        {
            if (_replayId is null) return;
        }
        _file.Writer.TryWrite(new Ligne(ligne));
    }

    private long Ms(DateTime ts)
    {
        DateTime debut;
        lock (_verrou) debut = _debutUtc;
        var t = (ts == default ? DateTime.UtcNow : ts) - debut;
        return Math.Max(0, (long)t.TotalMilliseconds);
    }

    // ── ecriture (hors du bus) ────────────────────────────────────────────────────────────────────────────

    private async Task EcrireAsync(CancellationToken ct)
    {
        Stream? flux = null;
        StreamWriter? ecrit = null;
        string? enCours = null;
        try
        {
            await foreach (var el in _file.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    switch (el)
                    {
                        case Ouvrir o:
                            ecrit?.Dispose();
                            enCours = Chemin(o.ReplayId) + ".part";
                            flux = new GZipStream(File.Create(enCours), CompressionLevel.Fastest);
                            ecrit = new StreamWriter(flux, new UTF8Encoding(false), 64 * 1024);
                            await ecrit.WriteLineAsync(o.Entete).ConfigureAwait(false);
                            break;
                        case Ligne l when ecrit is not null:
                            await ecrit.WriteLineAsync(Json(l.Champs)).ConfigureAwait(false);
                            break;
                        case Fermer f when ecrit is not null && enCours is not null:
                            await ecrit.WriteLineAsync(f.Pied).ConfigureAwait(false);
                            ecrit.Dispose();
                            ecrit = null;
                            File.Move(enCours, Chemin(f.ReplayId), overwrite: true);
                            enCours = null;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _journal?.LogWarning(ex, "Boite noire : ecriture impossible, la boite en cours est abandonnee.");
                    try { ecrit?.Dispose(); } catch { /* deja casse */ }
                    ecrit = null;
                    if (enCours is not null) { try { File.Delete(enCours); } catch { /* zone de travail */ } }
                    enCours = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { ecrit?.Dispose(); } catch { /* arret */ }
        }
    }

    /// <summary>Une boite sans replay (manifeste efface) et les morceaux d'une boite interrompue s'en vont.</summary>
    internal int PurgerLesOrphelines()
    {
        var n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_racine))
            {
                var nom = Path.GetFileName(f);
                var orpheline = nom.EndsWith(".part", StringComparison.Ordinal)
                    || (nom.EndsWith(".blackbox.jsonl.gz", StringComparison.Ordinal)
                        && !File.Exists(Path.Combine(_manifestes, nom[..nom.IndexOf(".blackbox", StringComparison.Ordinal)] + ".json"))
                        && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-24));
                if (!orpheline) continue;
                try { File.Delete(f); n++; } catch { /* reessaye au prochain demarrage */ }
            }
        }
        catch (DirectoryNotFoundException) { }
        return n;
    }

    // ── pur ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// L'identifiant standard du .SMEM pour une ligne du .MEM (adresse, action, joueur), ou null. Pur. Les adresses se
    /// comparent en valeur (« 0xC9D2 » = « 0XC9D2 » = « 0x00c9d2 »).
    /// </summary>
    internal static string? IdentifiantStandard(DeckSemantics? sens, string? adresse, string? action, int? joueur)
    {
        if (sens is null || sens.Status == "invalid" || adresse is null || action is null) return null;
        var a = Adresse(adresse);
        if (a is null) return null;
        foreach (var s in sens.Signals)
        {
            foreach (var r in s.From)
            {
                if (Adresse(r.Address) == a && string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase)
                    && (r.Player is null || r.Player == joueur))
                    return s.Id;
            }
        }
        return null;
    }

    private static long? Adresse(string texte)
    {
        var t = texte.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Une ligne compacte : les champs nuls ne s'ecrivent pas (la regle d'omission ne vaut pas pour un dictionnaire).</summary>
    private static string Json(Dictionary<string, object?> champs)
        => JsonSerializer.Serialize(champs.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), Options);
}
