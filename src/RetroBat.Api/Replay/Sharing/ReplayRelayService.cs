using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Avatar;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Le RELAIS vu de la borne (CDC v2.1 §52 « Internet » : known peer + Anchor relay).
///
/// Le problème est posé par le §101.1 : une borne sort toujours, elle n'est presque jamais
/// joignable de l'extérieur. Deux bornes chez des particuliers peuvent donc se connaître sans
/// jamais pouvoir se parler. Ce service est la moitié borne du pont, et il n'ouvre QUE des
/// connexions sortantes, dans les deux rôles.
///
/// Deux rôles, justement, et c'est ce qui rend le mécanisme symétrique :
///
///   • DÉTENTEUR. On relève sa boîte aux lettres, qui ne contient que des objets qu'on a
///     soi-même déclarés partageables au recensement, et on dépose les octets demandés.
///   • DEMANDEUR. On récupère ce qu'on avait demandé, une fois qu'un détenteur a déposé.
///
/// Le relais est ASYNCHRONE par nature : le détenteur doit se réveiller. Une demande ne bloque
/// donc jamais une lecture. Elle est déposée, et l'objet arrive plus tard, exactement comme
/// l'état « replicating » du §86 qui a sorti le réseau du chemin de réponse.
///
/// Rien n'est cru sur parole à l'arrivée : l'objet est importé par son empreinte RECALCULÉE. Des
/// octets qui ne tombent pas sur le hash attendu atterrissent sous un autre nom et sont effacés.
/// Le relais transporte, il ne certifie pas.
/// </summary>
public sealed class ReplayRelayService : BackgroundService
{
    /// <summary>
    /// AU REPOS (lot 4 bis C-3, 14/11 : 9 422 releves par jour) : la boite aux lettres se releve toutes les 5 min. Une partie
    /// en solo ou la lecture d'un replay ne l'accelerent plus.
    /// </summary>
    internal static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

    /// <summary>Cette borne attend un replay demande au relais : elle va le chercher chaque minute, comme avant.</summary>
    internal static readonly TimeSpan CadenceDemandeEnCours = TimeSpan.FromSeconds(60);

    /// <summary>Spectateur d'un direct, salle MULTIPLAYER, ou planches d'avatar attendues. Une foule se
    /// remplit en quelques secondes : une planche arrivee apres la fin du direct n'a plus personne
    /// a qui se montrer.</summary>
    internal static readonly TimeSpan CadenceAvatars = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(45);

    /// <summary>Apres des releves rates d'affilee, l'attente double, jusqu'a 30 min.</summary>
    internal static readonly TimeSpan PlafondDuRecul = TimeSpan.FromMinutes(30);

    /// <summary>
    /// L'attente avant le prochain releve. Pur. 15 s pour un spectateur d'un direct, une borne dans une salle MULTIPLAYER
    /// (la planche de l'adversaire) ou des planches attendues ; 60 s si un replay demande au relais est attendu ; 5 min
    /// sinon, partie en solo comprise. Apres <paramref name="echecs"/> releves rates, l'attente double (30 min au plus).
    /// Les attentes d'au moins 60 s sont dispersees de plus ou moins 10 % (<paramref name="alea"/> de 0 a 1).
    /// </summary>
    internal static TimeSpan Delai(bool spectateur, bool salleMultiplayer, bool planchesAttendues, bool replayAttendu, int echecs, double alea)
    {
        var attente = spectateur || salleMultiplayer || planchesAttendues ? CadenceAvatars
            : replayAttendu ? CadenceDemandeEnCours
            : Cadence;
        if (echecs > 0)
        {
            var recul = TimeSpan.FromTicks(attente.Ticks * (1L << Math.Min(echecs, 10)));
            attente = recul > PlafondDuRecul ? PlafondDuRecul : recul;
        }

        if (attente >= TimeSpan.FromSeconds(60))
        {
            attente = TimeSpan.FromTicks((long)(attente.Ticks * (0.9 + 0.2 * Math.Clamp(alea, 0, 1))));
        }

        return attente;
    }

    /// <summary>Au-delà, on cesse de réclamer : le pont d'en face a expiré depuis longtemps et
    /// personne n'a déposé. Redemander se fera naturellement à la prochaine lecture.</summary>
    private static readonly TimeSpan PatienceDemande = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ReplayStore _store;
    private readonly AvatarSheetStore _avatars;
    private readonly ReplaySharePolicy _policy;
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplayRelayService> _logger;

    private readonly object _gate = new();
    private volatile bool _gameActive;
    private volatile bool _replayActive;
    private volatile bool _salleMultiplayer;
    private volatile bool _releveRate;
    private int _echecs;
    private readonly RetroBat.Api.Netplay.LiveSpectateState? _seance;

    public ReplayRelayService(ReplayStore store, AvatarSheetStore avatars, ReplaySharePolicy policy,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IHttpClientFactory httpFactory,
        IConfiguration config, IEventBus bus, ILogger<ReplayRelayService> logger,
        RetroBat.Api.Netplay.LiveSpectateState? seance = null,
        RetroBat.Api.Infrastructure.SuiviDuSite? suivi = null)
    {
        _store = store; _avatars = avatars; _policy = policy; _devices = devices;
        _httpFactory = httpFactory; _config = config; _bus = bus; _logger = logger;
        _seance = seance;
        _suivi = suivi;
        if (_suivi is not null)
        {
            // LA BOITE JOINTE AU RELEVE DE L'AGENT (point 5 du 14/11) : le site la donne a partir du curseur de cette borne.
            _suivi.CurseurDeLaBoite = () => Etat().Since;
            _suivi.Traiter(RetroBat.Api.Infrastructure.SuiviDuSite.BoiteDuRelais, (boite, ct) => TraiterLaBoiteAsync(boite, ct));
        }
    }

    private readonly RetroBat.Api.Infrastructure.SuiviDuSite? _suivi;

    /// <summary>Le minuteur et le releve de l'agent ne traitent pas la boite en meme temps.</summary>
    private readonly SemaphoreSlim _boite = new(1, 1);

    /// <summary>
    /// Le releve du minuteur peut-il se taire ? Oui AU REPOS (ni spectateur, ni salle, ni planche ni replay attendus) quand
    /// le releve de l'agent porte deja la boite. Les modes rapides gardent leur cadence : 15 s, 60 s. Pur.
    /// </summary>
    internal static bool ReleveCouvert(bool couvert, bool spectateur, bool salleMultiplayer, bool planchesAttendues, bool replayAttendu)
        => couvert && !spectateur && !salleMultiplayer && !planchesAttendues && !replayAttendu;

    /// <summary>Cette borne regarde-t-elle un direct ? Sa planche et celles de la foule passent par le relais.</summary>
    private bool Spectateur => _seance is not null && _seance.Courant.Session.Length > 0;

    public sealed record RelayState(long Since, IReadOnlyList<RelayPending> Pending);
    /// <summary>`Kind` vaut « avatar » pour une planche. Absent, c'est un replay : les états écrits
    /// avant les planches se relisent donc tels quels.</summary>
    public sealed record RelayPending(string Sha256, DateTime AskedUtc, string? Kind = null);
    public sealed record RelayReport(bool Ran, int Deposited, int Collected, int Pending, string? Reason);

    private string EtatPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "relay.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _bus.Subscribe<EventEnvelope>(OnBusEvent); }
        catch (Exception ex) { _logger.LogDebug(ex, "Relais : abonnement au bus impossible."); }

        try { await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var avant = Etat().Pending;
            if (ReleveCouvert(_suivi?.Couvert(RetroBat.Api.Infrastructure.SuiviDuSite.BoiteDuRelais, DateTime.UtcNow) == true,
                    Spectateur, _salleMultiplayer, avant.Any(EstAvatar), avant.Any(p => !EstAvatar(p))))
            {
                _releveRate = false;
            }
            else
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "Relais : passage en erreur."); }
            }

            var attente = Etat().Pending;
            _echecs = _releveRate ? _echecs + 1 : 0;
            var delai = Delai(Spectateur, _salleMultiplayer, attente.Any(EstAvatar), attente.Any(p => !EstAvatar(p)), _echecs,
                Random.Shared.NextDouble());
            try { await Task.Delay(delai, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Un passage : déposer ce qu'on nous demande, récupérer ce qu'on a demandé.</summary>
    public async Task<RelayReport> TickAsync(CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return new RelayReport(false, 0, 0, 0, "device_not_paired");

        // Le relais se tait pendant une partie ou une lecture, comme la file de semis et l'agent
        // de réplication : la bande passante d'une borne appartient d'abord à qui joue dessus.
        //
        // SAUF pour les planches d'avatar, et c'est justement pendant une partie qu'elles servent :
        // un spectateur regarde un direct, et c'est là que sa borne doit recevoir la foule et livrer
        // sa propre planche. Quelques kilo-octets ne disputent rien à la partie.
        var occupee = _gameActive || _replayActive;

        var deposes = await DeposerAsync(credential, occupee, ct).ConfigureAwait(false);
        var recuperes = await RecupererAsync(credential, occupee, ct).ConfigureAwait(false);
        return new RelayReport(true, deposes, recuperes, Etat().Pending.Count, occupee ? "busy" : null);
    }

    /// <summary>
    /// Dépose ce qu'on nous demande. La boîte aux lettres ne contient que des objets que cette
    /// borne a déclarés partageables ; on revérifie quand même auprès de la politique de
    /// partage, parce qu'une déclaration date d'hier et qu'un réglage a pu changer depuis.
    /// </summary>
    private async Task<int> DeposerAsync(string credential, bool avatarsSeulement, CancellationToken ct)
    {
        var doc = await LireJsonAsync("/api/v1/agent/relay/inbox?limit=20&since=" + Etat().Since, credential, ct)
            .ConfigureAwait(false);
        // Un releve rate fait reculer le suivant (C-9) ; un releve abouti efface le recul.
        _releveRate = doc is null;
        if (doc is null) return 0;
        return await DeposerDepuisAsync(credential, avatarsSeulement, doc, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// La boite <c>relay_inbox</c> jointe au releve de l'agent, meme forme que <c>relay/inbox</c> (<c>{ok, next, requests}</c>).
    /// Partie ou lecture en cours : seules les planches partent, comme au passage du minuteur.
    /// </summary>
    internal async Task<int> TraiterLaBoiteAsync(JsonElement boite, CancellationToken ct)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return 0;
        if (JsonNode.Parse(boite.GetRawText()) is not JsonObject doc) return 0;
        return await DeposerDepuisAsync(credential, _gameActive || _replayActive, doc, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Depose ce que demande une boite lue. Une demande deja passee sous le curseur (la meme boite lue par le minuteur et
    /// par le releve de l'agent) est sautee ; le curseur ne recule jamais.
    /// </summary>
    private async Task<int> DeposerDepuisAsync(string credential, bool avatarsSeulement, JsonObject doc, CancellationToken ct)
    {
        await _boite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DeposerDepuisSeulAsync(credential, avatarsSeulement, doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _boite.Release();
        }
    }

    private async Task<int> DeposerDepuisSeulAsync(string credential, bool avatarsSeulement, JsonObject doc, CancellationToken ct)
    {
        var etat = Etat();
        var suivant = Math.Max(etat.Since, doc["next"]?.GetValue<long>() ?? etat.Since);
        var demandes = doc["requests"] as JsonArray ?? new JsonArray();
        var deposes = 0;
        long? premiereLaissee = null;

        foreach (var demande in demandes)
        {
            if (ct.IsCancellationRequested) break;
            var sha = demande?["object_sha256"]?.GetValue<string>() ?? string.Empty;
            if (sha.Length != 64) continue;
            if (demande?["id"]?.GetValue<long>() is { } id && id <= etat.Since) continue;

            string chemin;
            if (_avatars.Has(sha))
            {
                if (!AvatarsPartages) continue;
                chemin = _avatars.ObjectPath(sha);
            }
            else
            {
                if (avatarsSeulement)
                {
                    // Pendant une partie on ne sert que des planches. Une autre demande est LAISSÉE,
                    // pas sautée : le curseur repartira d'elle, sinon ce replay ne serait jamais
                    // redemandé à cette borne.
                    premiereLaissee ??= demande?["id"]?.GetValue<long>() ?? etat.Since + 1;
                    continue;
                }
                // Compresse au repos : le brut est materialise, c'est lui qui part au relais.
                var brut = await _store.EnsureRawAsync(sha, ct).ConfigureAwait(false);
                if (brut is null) continue;
                chemin = brut;
                if (!_policy.Evaluate(sha).Allowed)
                {
                    _logger.LogInformation("Relais : dépôt refusé pour {Sha}, l'objet n'est plus partageable.", Court(sha));
                    continue;
                }
            }

            if (await DeposerUnAsync(credential, sha, chemin, ct).ConfigureAwait(false)) deposes++;
        }

        if (premiereLaissee is long laissee) suivant = Math.Max(etat.Since, laissee - 1);
        Modifier(e => e with { Since = suivant });
        if (deposes > 0) _logger.LogInformation("Relais : {Count} objet(s) déposé(s) pour des bornes injoignables.", deposes);
        return deposes;
    }

    private async Task<bool> DeposerUnAsync(string credential, string sha, string chemin, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;

            await using var flux = File.OpenRead(chemin);
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/agent/relay/deposit?sha256=" + sha)
            {
                Content = new StreamContent(flux),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Add("X-NelfePlay-Device", credential);

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var corps = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Relais : dépôt refusé ({Code}) pour {Sha} - {Corps}",
                    (int)response.StatusCode, Court(sha), corps);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relais : dépôt impossible pour {Sha}.", Court(sha));
            return false;
        }
    }

    /// <summary>Récupère ce qu'on avait demandé, si quelqu'un a déposé entre-temps.</summary>
    private async Task<int> RecupererAsync(string credential, bool avatarsSeulement, CancellationToken ct)
    {
        var etat = Etat();
        if (etat.Pending.Count == 0) return 0;

        var restantes = new List<RelayPending>();
        var recuperes = 0;

        foreach (var demande in etat.Pending)
        {
            var avatar = EstAvatar(demande);
            if (ct.IsCancellationRequested || (avatarsSeulement && !avatar)) { restantes.Add(demande); continue; }

            var dejaLa = avatar ? _avatars.Has(demande.Sha256) : _store.HasObject(demande.Sha256);
            if (dejaLa) continue;   // arrivé par ailleurs
            if (DateTime.UtcNow - demande.AskedUtc > PatienceDemande)
            {
                _logger.LogInformation("Relais : demande abandonnée pour {Sha}, personne n'a déposé.", Court(demande.Sha256));
                continue;
            }

            if (await RecupererUnAsync(credential, demande.Sha256, avatar, ct).ConfigureAwait(false)) recuperes++;
            else restantes.Add(demande);
        }

        // Réécrit en gardant les demandes ajoutées PENDANT ce passage : le relevé de la foule en dépose
        // en continu, et les écraser les ferait attendre le passage suivant pour rien.
        var vues = etat.Pending.Select(p => p.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Modifier(e => e with { Pending = restantes.Concat(e.Pending.Where(p => !vues.Contains(p.Sha256))).ToList() });
        return recuperes;
    }

    private async Task<bool> RecupererUnAsync(string credential, string sha, bool avatar, CancellationToken ct)
    {
        var temporaire = Path.Combine(_store.TempRoot, "relay-" + sha + ".part");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v1/agent/relay/object?sha256=" + sha);
            request.Headers.Add("X-NelfePlay-Device", credential);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;   // pas encore déposé, on repassera

            Directory.CreateDirectory(_store.TempRoot);
            await using (var sortie = File.Create(temporaire))
            {
                await response.Content.CopyToAsync(sortie, cts.Token).ConfigureAwait(false);
            }

            if (avatar)
            {
                // Le magasin des planches recalcule l'empreinte et refuse d'office des octets qui ne
                // tombent pas dessus : rien n'est gardé sous un autre nom.
                var planche = await _avatars.ImporterFichierAsync(temporaire, sha, ct).ConfigureAwait(false);
                if (!planche.Ok)
                {
                    _logger.LogWarning("Relais : planche reçue pour {Attendu} écartée ({Raison}).", Court(sha), planche.Erreur);
                    return false;
                }
                _logger.LogInformation("Relais : planche {Sha} récupérée.", Court(sha));
                return true;
            }

            // L'import RECALCULE l'empreinte et range l'objet dessous. Des octets qui ne tombent
            // pas sur le hash attendu atterrissent donc ailleurs, et on les efface : le relais
            // transporte, il ne certifie pas.
            var obj = await _store.ImportObjectAsync(temporaire, ct).ConfigureAwait(false);
            if (!string.Equals(obj.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Relais : octets reçus pour {Attendu} mais mesurés {Obtenu}. Écartés.",
                    Court(sha), Court(obj.Sha256));
                _store.DeleteObject(obj.Sha256);
                return false;
            }

            _logger.LogInformation("Relais : objet {Sha} récupéré ({Ko} Ko).", Court(sha), obj.Size / 1024);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relais : récupération impossible pour {Sha}.", Court(sha));
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaire)) File.Delete(temporaire); } catch { }
        }
    }

    /// <summary>
    /// Dépose une demande. Appelé quand aucun pair joignable n'a l'objet.
    ///
    /// Ne bloque JAMAIS : le détenteur doit se réveiller, ce qui prend le temps que ça prend. La
    /// lecture qui a déclenché la demande n'attend pas, elle échoue proprement et l'objet sera
    /// là au prochain essai.
    /// </summary>
    public async Task<bool> RequestAsync(string sha256, CancellationToken ct, string kind = "replay")
    {
        var sha = (sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (sha.Length != 64) return false;
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return false;

        var etat = Etat();
        if (etat.Pending.Any(p => string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase)))
            return true;   // déjà demandé, inutile d'insister

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/agent/relay/request?sha256=" + sha);
            request.Headers.Add("X-NelfePlay-Device", credential);

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var corps = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var doc = JsonNode.Parse(corps) as JsonObject;
            var etatDemande = doc?["state"]?.GetValue<string>() ?? "none";
            var detenteurs = doc?["holders"]?.GetValue<int>() ?? 0;

            if (etatDemande == "none")
            {
                // Personne ne détient l'objet, ou personne ne le partage. Ouvrir un pont vers
                // personne ferait attendre indéfiniment quelque chose qui n'arrivera pas.
                _logger.LogInformation("Relais : aucun détenteur joignable pour {Sha}.", Court(sha));
                return false;
            }

            Modifier(e => e.Pending.Any(p => string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                ? e
                : e with { Pending = e.Pending.Append(new RelayPending(sha, DateTime.UtcNow, kind)).ToList() });
            _logger.LogInformation("Relais : demande déposée pour {Sha} ({Detenteurs} détenteur(s) déclaré(s)).",
                Court(sha), detenteurs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relais : demande impossible pour {Sha}.", Court(sha));
            return false;
        }
    }

    private async Task<JsonObject?> LireJsonAsync(string chemin, string credential, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + chemin);
            request.Headers.Add("X-NelfePlay-Device", credential);
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relais : {Chemin} illisible.", chemin);
            return null;
        }
    }

    private string BaseUrl
    {
        get
        {
            var url = _config["Replay:Share:TransitUrl"];
            if (string.IsNullOrWhiteSpace(url)) url = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;
            return (url ?? string.Empty).TrimEnd('/');
        }
    }

    private RelayState Etat()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(EtatPath))
                {
                    var doc = JsonSerializer.Deserialize<RelayState>(File.ReadAllText(EtatPath), Json);
                    if (doc is not null) return doc with { Pending = doc.Pending ?? Array.Empty<RelayPending>() };
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Relais : état illisible."); }
            return new RelayState(0, Array.Empty<RelayPending>());
        }
    }

    private void Ecrire(RelayState etat)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EtatPath)!);
                var tmp = EtatPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(etat, Json));
                File.Move(tmp, EtatPath, overwrite: true);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Relais : état non écrit."); }
        }
    }

    /// <summary>Lit, change et réécrit l'état d'un seul tenant. Le relevé de la foule dépose des
    /// demandes pendant qu'un passage tourne : relire puis écrire en deux fois en perdait.</summary>
    private RelayState Modifier(Func<RelayState, RelayState> changement)
    {
        lock (_gate)
        {
            var nouveau = changement(Etat());
            Ecrire(nouveau);
            return nouveau;
        }
    }

    private static bool EstAvatar(RelayPending p) => string.Equals(p.Kind, "avatar", StringComparison.Ordinal);

    private bool AvatarsPartages => _config.GetValue("Avatar:Share:Enabled", true);

    private void OnBusEvent(EventEnvelope e)
    {
        switch (e.Type)
        {
            case "ui.game.started": _gameActive = true; break;
            case "ui.game.ended": _gameActive = false; break;
            case "replay.launching":
            case "replay.started": _replayActive = true; break;
            case "replay.finished": _replayActive = false; break;
            // La borne attend ou joue dans une salle MULTIPLAYER : la planche de l'adversaire passe par le relais.
            case RetroBat.Api.Multiplayer.MultiplayerSessionService.EvenementEtat: _salleMultiplayer = SalleDans(e.Payload); break;
        }
    }

    /// <summary>L'etat MULTIPLAYER publie sur le bus porte-t-il une salle ? Tolerant : un etat illisible n'en porte pas.</summary>
    internal static bool SalleDans(object? etat)
    {
        try
        {
            var element = etat is JsonElement j ? j : JsonSerializer.SerializeToElement(etat);
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("rooms", out var rooms)
                && rooms.ValueKind == JsonValueKind.Array && rooms.GetArrayLength() > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Court(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
