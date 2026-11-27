using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Adventure;

/// <summary>Le genre d'un objet que le site range (CONTRAT.md §4) : il fixe sa taille maximale.</summary>
public static class AdventureObjets
{
    public const string Etat = "state";
    public const string Sauvegarde = "save";
    public const string Capture = "shot";
}

/// <summary>
/// CE QUE L'API DEMANDE AU SITE (CDC v1.0 §3, CONTRAT.md). Le site est proprietaire des routes ; cette interface est le seul
/// endroit ou l'API les connait, et les tests la simulent. Toute methode rend null (ou un echec « a plus tard ») quand le site
/// est injoignable : rien ne se perd pour autant (voir <see cref="AdventureFileDEnvoi"/>).
/// </summary>
public interface IAdventureSite
{
    Task<AdventureInfo?> CreerAsync(string jeu, string contenu, int places, string mode, string? titre, string? coeur, string? empreinteDuCoeur, CancellationToken ct, string? version = null);

    /// <summary>
    /// Comme <see cref="CreerAsync"/>, avec la raison du refus du site (<c>rate_limited</c>, <c>bad_players</c>, <c>unknown_game</c>...) ou <c>offline</c>
    /// (27/11 : le site cree un BROUILLON, un seul par compte et par jeu ; l'aventure ne devient visible qu'a son activation).
    /// </summary>
    async Task<(AdventureInfo? Aventure, string? Raison)> CreerAvecRaisonAsync(string jeu, string contenu, int places, string mode, string? titre, string? coeur,
        string? empreinteDuCoeur, CancellationToken ct, string? version = null)
    {
        var aventure = await CreerAsync(jeu, contenu, places, mode, titre, coeur, empreinteDuCoeur, ct, version).ConfigureAwait(false);
        return (aventure, aventure is null ? "unknown_game" : null);
    }

    /// <summary>
    /// ACTIVATION (27/11) : l'aventure vient d'etre lancee avec <paramref name="joueurs"/> joueurs (deux au moins) ; le site la rend active et lui
    /// donne son nom. Idempotente. Rend la raison d'un refus (jamais bloquant pour la partie) ou null.
    /// </summary>
    Task<(bool Ok, string? Raison)> ActiverAsync(string aventure, int joueurs, CancellationToken ct) => Task.FromResult<(bool, string?)>((true, null));

    Task<AdventureMesAventures?> MesAventuresAsync(CancellationToken ct);

    /// <summary>Les aventures « open » (de ce jeu si precise).</summary>
    Task<IReadOnlyList<AdventureInfo>?> OuvertesAsync(string? jeu, CancellationToken ct);

    Task<AdventureInfo?> DetailAsync(string aventure, CancellationToken ct);

    Task<AdventureDerniere?> DerniereAsync(string aventure, int? joueurs, CancellationToken ct);

    /// <summary>Ouvre une seance : cree ou rattache la gameroom et le salon vocal de l'aventure.</summary>
    Task<AdventureSeance?> OuvrirSeanceAsync(string aventure, int joueurs, CancellationToken ct);

    Task<bool> FermerSeanceAsync(string aventure, string raison, long? dureeS, CancellationToken ct);

    /// <summary>
    /// LA TOUCHE X (28/11) : l'ouvreur de la seance change le nombre de joueurs attendus tant que la salle attend (<c>session/size</c> : met a
    /// jour la seance ET la salle). Rend la raison du site (<c>not_host</c>, <c>no_session</c>, <c>bad_players</c>...) ou null si ok.
    /// </summary>
    Task<(bool Ok, string? Raison)> ReglerLaSeanceAsync(string aventure, int joueurs, CancellationToken ct) => Task.FromResult<(bool, string?)>((false, "unavailable"));

    /// <summary>Tout participant peut inviter (« qui invite decide »). Rend le code du site (<c>not_found</c>, <c>already_member</c>...) ou null si ok.</summary>
    Task<(bool Ok, string? Raison)> InviterAsync(string aventure, string poignee, CancellationToken ct);

    Task<bool> AccepterAsync(string invitation, bool accepter, CancellationToken ct);

    /// <summary>Parmi ces empreintes, celles que le site n'a pas encore ; null s'il est injoignable.</summary>
    Task<IReadOnlyList<string>?> ObjetsManquantsAsync(IReadOnlyCollection<string> empreintes, CancellationToken ct);

    /// <summary>Depose un objet par l'empreinte de ses octets BRUTS (le client le compresse en gzip).</summary>
    Task<bool> DeposerObjetAsync(string empreinte, string genre, byte[] octets, CancellationToken ct);

    /// <summary>Depose la version (ses objets sont deja la).</summary>
    Task<AdventureDepot> DeposerVersionAsync(AdventureVersion version, CancellationToken ct);

    /// <summary>Un objet de l'aventure, decompresse ; sa verification contre l'empreinte est celle de l'appelant.</summary>
    Task<byte[]?> TelechargerObjetAsync(string aventure, string empreinte, CancellationToken ct);
}

/// <summary>
/// Le client du site : <c>/api/v1/agent/adventure/...</c>, avec l'identifiant de la borne, comme les routes MULTIPLAYER
/// (<see cref="RetroBat.Api.Multiplayer.MultiplayerClient"/>). Les chemins et les corps sont ceux de CONTRAT.md.
/// </summary>
public sealed class AdventureSiteClient : IAdventureSite
{
    private const string Base = "/api/v1/agent/adventure/";

    private readonly IHttpClientFactory? _fabrique;
    private readonly NelfePlayDeviceStore? _machine;
    private readonly Func<HttpClient?>? _clientDeTest;
    private readonly ILogger<AdventureSiteClient>? _journal;

    public AdventureSiteClient(IHttpClientFactory fabrique, NelfePlayDeviceStore machine, ILogger<AdventureSiteClient>? journal = null)
    {
        _fabrique = fabrique;
        _machine = machine;
        _journal = journal;
    }

    internal AdventureSiteClient(Func<HttpClient?> clientDeTest)
    {
        _clientDeTest = clientDeTest;
    }

    private HttpClient? Client(TimeSpan? delai = null)
    {
        if (_clientDeTest is not null)
        {
            return _clientDeTest();
        }

        var identifiant = _machine?.GetCredential();
        if (string.IsNullOrEmpty(identifiant) || _fabrique is null)
        {
            return null;
        }

        var client = _fabrique.CreateClient();
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = delai ?? TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", identifiant);
        return client;
    }

    /// <summary>Un appel JSON : le code HTTP et le corps, ou null quand le site est injoignable (ou la borne non liee).</summary>
    private async Task<(int Statut, JsonObject Corps)?> AppelerAsync(HttpMethod methode, string chemin, object? corps, CancellationToken ct)
    {
        var client = Client();
        if (client is null)
        {
            return null;
        }

        try
        {
            using var requete = new HttpRequestMessage(methode, Base + chemin);
            if (corps is not null)
            {
                requete.Content = JsonContent.Create(corps);
            }

            using var reponse = await client.SendAsync(requete, ct).ConfigureAwait(false);
            var texte = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = texte.Length == 0 ? null : JsonNode.Parse(texte) as JsonObject;
            if (!reponse.IsSuccessStatusCode)
            {
                _journal?.LogInformation("ADVENTURE : {Chemin} refuse ({Statut}, {Raison})", chemin, (int)reponse.StatusCode, json?["reason"]?.ToString() ?? "-");
            }

            return ((int)reponse.StatusCode, json ?? new JsonObject());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(ex, "ADVENTURE : {Chemin} injoignable", chemin);
            return null;
        }
        finally
        {
            client.Dispose();
        }
    }

    private static bool Reussi((int Statut, JsonObject Corps)? r) => r is { Statut: >= 200 and < 300 } rep && rep.Corps["ok"]?.GetValue<bool>() != false;

    private static string? Raison((int Statut, JsonObject Corps)? r) => r?.Corps["reason"]?.ToString() ?? r?.Corps["error"]?.ToString();

    // ── lecture des formes du site ─────────────────────────────────────────────────────────────────

    private static string? Texte(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Une empreinte : la chaine, ou l'objet <c>{sha256, size}</c> de <c>latest.objects</c>.</summary>
    private static string? Empreinte(JsonNode? n) => n is JsonObject o ? Texte(o["sha256"]) : Texte(n);

    private static int? Entier(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    internal static AdventureVersion? LireVersion(JsonNode? n, string aventure = "")
    {
        if (n is not JsonObject v)
        {
            return null;
        }

        return new AdventureVersion
        {
            Id = Texte(v["id"]) ?? string.Empty,
            Adventure = Texte(v["adventure"]) ?? aventure,
            Seq = Entier(v["seq"]) ?? 0,
            BaseSeq = Entier(v["base_seq"]),
            StateSha256 = Empreinte(v["state"]) ?? Texte(v["state_sha256"]),
            SaveSha256 = Empreinte(v["save"]) ?? Texte(v["save_sha256"]),
            CaptureSha256 = Empreinte(v["shot"]) ?? Texte(v["shot_sha256"]),
            Players = Entier(v["players"]) ?? 0,
            Seats = (v["seats"] as JsonArray)?.OfType<JsonObject>()
                .Select(p => new AdventurePlace { Seat = Entier(p["seat"]) ?? 0, Handle = Texte(p["handle"]) }).ToList() ?? [],
            DurationS = v["duration_s"] is JsonValue d && d.TryGetValue<long>(out var duree) ? duree : 0,
            Reason = Texte(v["reason"]) ?? AdventureRaisons.Reguliere,
            CoreSha256 = Texte(v["core_sha256"]),
            CreatedUtc = DateTime.TryParse(Texte(v["created_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date : DateTime.MinValue,
        };
    }

    private static DateTime? Date(string? texte)
        => DateTime.TryParse(texte, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>
    /// Les participants d'une LISTE du site (27/11) : <c>participants</c>, un tableau de poignees dans l'ordre des places (le detail d'un participant
    /// aussi). <c>members</c> est un nombre dans les listes : le detail le rend en objets, lus ailleurs.
    /// </summary>
    internal static IReadOnlyList<string> LirePseudos(JsonObject a)
        => (a["participants"] as JsonArray)?.Select(Texte).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList() ?? [];

    /// <summary>
    /// Le pseudo d'affichage du createur (28/11) : le membre de role <c>creator</c> du detail (<c>members</c> en objets). Les listes du site
    /// ne rendent que sa poignee (<c>creator</c>) : le champ <c>creator_pseudo</c> est a ajouter cote site, la borne lit le detail en attendant.
    /// </summary>
    private static string? PseudoDuCreateur(JsonObject a)
        => (a["members"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => Texte(m["role"]) == "creator") is { } m && Texte(m["pseudo"]) is { Length: > 0 } p ? p : null;

    /// <summary>
    /// Les pseudos d'affichage des participants (28/11) : <c>participant_pseudos</c> d'une liste (meme ordre que <c>participants</c>), sinon
    /// les <c>pseudo</c> des <c>members</c> du detail. Une poignee n'est jamais prise pour un pseudo.
    /// </summary>
    internal static IReadOnlyList<string> LireNomsDesJoueurs(JsonObject a)
    {
        if ((a["participants_names"] ?? a["participant_pseudos"]) is JsonArray liste)
        {
            return liste.Select(Texte).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        }

        return (a["members"] as JsonArray)?.OfType<JsonObject>().Select(m => Texte(m["pseudo"])).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList() ?? [];
    }

    internal static AdventureInfo? LireAventure(JsonNode? n)
    {
        if (n is not JsonObject a)
        {
            return null;
        }

        var id = Texte(a["id"]) ?? string.Empty;
        return new AdventureInfo
        {
            Id = id,
            Game = Texte(a["game"]) ?? string.Empty,
            Content = Texte(a["content"]) ?? string.Empty,
            Name = Texte(a["title"]),
            Core = Texte(a["core"]),
            CoreSha256 = Texte(a["core_sha256"]) ?? Texte(a["core_version"]),
            Seats = Entier(a["places"]) ?? 0,
            Mode = Texte(a["mode"]) ?? AdventureModes.Exclusive,
            Creator = a["creator"] is JsonObject c ? Texte(c["handle"]) : Texte(a["creator"]),
            CreatorPseudo = Texte(a["creator_name"]) ?? Texte(a["creator_pseudo"]) ?? (a["creator"] is JsonObject cp ? Texte(cp["pseudo"]) : null) ?? PseudoDuCreateur(a),
            NomsDesJoueurs = LireNomsDesJoueurs(a),
            Participants = (a["members"] as JsonArray)?.OfType<JsonObject>().Select(m => Texte(m["handle"])).OfType<string>().ToList() ?? [],
            Room = a["session"] is JsonObject s ? Texte(s["room"]) : null,
            LobbyId = a["voice"] is JsonObject voix ? Texte(voix["lobby_id"]) : null,
            Language = AdventureLangues.Normaliser(Texte(a["language"])),
            State = Texte(a["state"]),
            LastPlayedUtc = Date(Texte(a["last_played_at"]) ?? Texte(a["last_session_at"]) ?? Texte(a["last_played"])),
            Pseudos = LirePseudos(a),
            Latest = LireVersion(a["latest_version"] ?? a["latest"], id),
        };
    }

    // ── les routes ─────────────────────────────────────────────────────────────────────────────────

    public async Task<AdventureInfo?> CreerAsync(string jeu, string contenu, int places, string mode, string? titre, string? coeur, string? empreinteDuCoeur, CancellationToken ct, string? version = null)
        => (await CreerAvecRaisonAsync(jeu, contenu, places, mode, titre, coeur, empreinteDuCoeur, ct, version).ConfigureAwait(false)).Aventure;

    public async Task<(bool Ok, string? Raison)> ActiverAsync(string aventure, int joueurs, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Post, aventure + "/activate", new { players = joueurs }, ct).ConfigureAwait(false);
        return (Reussi(r), r is null ? "offline" : Raison(r) ?? (Reussi(r) ? null : "other"));
    }

    public async Task<(AdventureInfo? Aventure, string? Raison)> CreerAvecRaisonAsync(string jeu, string contenu, int places, string mode, string? titre, string? coeur,
        string? empreinteDuCoeur, CancellationToken ct, string? version = null)
    {
        var r = await AppelerAsync(HttpMethod.Post, "create", new
        {
            game = jeu,
            content = contenu,
            places,
            mode,
            title = titre,
            // La version localisee (USA, Europe, France...) : informative, le contenu (md5) fait l'identite (CONTRAT.md, proposition du lot 0).
            version,
            core = coeur,
            // Le « core_version » du site : la borne n'en connait pas d'autre que l'empreinte de la dll installee (CONTRAT.md, questions).
            core_version = empreinteDuCoeur,
            core_sha256 = empreinteDuCoeur,
            start = new { kind = "fresh" },
            client_ref = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        }, ct).ConfigureAwait(false);
        return Reussi(r) ? (LireAventure(r!.Value.Corps["adventure"]), null) : (null, r is null ? "offline" : Raison(r) ?? "other");
    }

    public async Task<AdventureMesAventures?> MesAventuresAsync(CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Get, "mine", null, ct).ConfigureAwait(false);
        if (!Reussi(r))
        {
            return null;
        }

        var corps = r!.Value.Corps;
        return new AdventureMesAventures(
            (corps["adventures"] as JsonArray)?.Select(LireAventure).OfType<AdventureInfo>().ToList() ?? [],
            (corps["invitations"] as JsonArray)?.OfType<JsonObject>().Select(i => new AdventureInvitation(
                Texte(i["invitation"]) ?? string.Empty, Texte(i["adventure"]) ?? string.Empty, Texte(i["game"]), Texte(i["title"]),
                i["from"] is JsonObject de ? Texte(de["handle"]) : null)).ToList() ?? []);
    }

    public async Task<IReadOnlyList<AdventureInfo>?> OuvertesAsync(string? jeu, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Get, "open" + (jeu is { Length: > 0 } ? "?game=" + Uri.EscapeDataString(jeu) : string.Empty), null, ct).ConfigureAwait(false);
        return Reussi(r) ? (r!.Value.Corps["adventures"] as JsonArray)?.Select(LireAventure).OfType<AdventureInfo>().ToList() ?? [] : null;
    }

    public async Task<AdventureInfo?> DetailAsync(string aventure, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Get, aventure, null, ct).ConfigureAwait(false);
        return Reussi(r) ? LireAventure(r!.Value.Corps["adventure"]) : null;
    }

    public async Task<AdventureDerniere?> DerniereAsync(string aventure, int? joueurs, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Get, aventure + "/latest" + (joueurs is { } n ? "?players=" + n.ToString(CultureInfo.InvariantCulture) : string.Empty), null, ct).ConfigureAwait(false);
        return Reussi(r) ? new AdventureDerniere(LireVersion(r!.Value.Corps["version"], aventure), Texte(r.Value.Corps["resume"]) ?? "fresh") : null;
    }

    public async Task<AdventureSeance?> OuvrirSeanceAsync(string aventure, int joueurs, CancellationToken ct)
    {
        // Le site exige de 2 a « places » joueurs (une salle d'amis ne part jamais a moins de deux, CONTRAT.md §8).
        var r = await AppelerAsync(HttpMethod.Post, aventure + "/session/open", new { players = Math.Max(2, joueurs), rtt_ms = (int?)null, panel = false }, ct).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var corps = r.Value.Corps;
        var session = corps["session"] as JsonObject;
        var voix = corps["voice"] as JsonObject;
        return new AdventureSeance
        {
            Ok = Reussi(r),
            Reason = Raison(r),
            Attached = corps["attached"]?.GetValue<bool>() ?? false,
            Session = Texte(session?["id"]),
            Room = Texte(corps["room"]) ?? Texte(session?["room"]),
            Resume = Texte(session?["resume"]),
            BaseSeq = Entier(session?["base_seq"]),
            Latest = LireVersion(corps["latest"], aventure),
            VoiceSupported = voix?["supported"]?.GetValue<bool>() ?? false,
            LobbyId = Texte(voix?["lobby_id"]),
        };
    }

    public async Task<bool> FermerSeanceAsync(string aventure, string raison, long? dureeS, CancellationToken ct)
        => Reussi(await AppelerAsync(HttpMethod.Post, aventure + "/session/close", new { reason = raison, duration_s = dureeS }, ct).ConfigureAwait(false));

    public async Task<(bool Ok, string? Raison)> ReglerLaSeanceAsync(string aventure, int joueurs, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Post, aventure + "/session/size", new { players = joueurs }, ct).ConfigureAwait(false);
        return (Reussi(r), r is null ? "offline" : Reussi(r) ? null : Raison(r) ?? "other");
    }

    public async Task<(bool Ok, string? Raison)> InviterAsync(string aventure, string poignee, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Post, aventure + "/invite", new { handle = poignee }, ct).ConfigureAwait(false);
        return (Reussi(r), r is null ? "offline" : Raison(r));
    }

    public async Task<bool> AccepterAsync(string invitation, bool accepter, CancellationToken ct)
        => Reussi(await AppelerAsync(HttpMethod.Post, "invitation", new { invitation, accept = accepter }, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<string>?> ObjetsManquantsAsync(IReadOnlyCollection<string> empreintes, CancellationToken ct)
    {
        var manquants = new List<string>();
        // Le site en prend 50 au plus par appel.
        foreach (var lot in empreintes.Chunk(50))
        {
            var r = await AppelerAsync(HttpMethod.Post, "objects/missing", new { shas = lot }, ct).ConfigureAwait(false);
            if (!Reussi(r))
            {
                return null;
            }

            manquants.AddRange((r!.Value.Corps["missing"] as JsonArray)?.Select(Texte).OfType<string>() ?? []);
        }

        return manquants;
    }

    public async Task<bool> DeposerObjetAsync(string empreinte, string genre, byte[] octets, CancellationToken ct)
    {
        var client = Client(TimeSpan.FromMinutes(2));
        if (client is null)
        {
            return false;
        }

        try
        {
            using var comprime = new MemoryStream();
            using (var gzip = new GZipStream(comprime, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(octets);
            }

            using var contenu = new ByteArrayContent(comprime.ToArray());
            contenu.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var reponse = await client.PutAsync(Base + "objects/" + empreinte + "?kind=" + genre + "&encoding=gzip", contenu, ct).ConfigureAwait(false);
            return reponse.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _journal?.LogDebug(ex, "ADVENTURE : depot d'objet injoignable");
            return false;
        }
        finally
        {
            client.Dispose();
        }
    }

    public async Task<AdventureDepot> DeposerVersionAsync(AdventureVersion version, CancellationToken ct)
    {
        var r = await AppelerAsync(HttpMethod.Post, version.Adventure + "/versions", new
        {
            // Le meme identifiant a chaque essai : l'appel est rejouable, jamais deux versions (CONTRAT.md §5).
            version_ref = version.Id,
            // La seance d'origine : le site verifie l'hote de CETTE seance, meme apres une coupure de plus de 15 min (grace de 24 h).
            session_id = version.SessionId,
            base_seq = version.BaseSeq,
            state_sha256 = version.StateSha256,
            save_sha256 = version.SaveSha256,
            shot_sha256 = version.CaptureSha256,
            players = version.Players,
            seats = version.Seats.Select(p => new { seat = p.Seat, handle = p.Handle }),
            duration_s = version.DurationS,
            reason = version.Reason,
            core_version = version.CoreSha256,
        }, ct).ConfigureAwait(false);
        if (r is null)
        {
            return AdventureDepot.APlusTard("offline");
        }

        if (Reussi(r))
        {
            return AdventureDepot.Parti;
        }

        var raison = Raison(r);
        return AdventureDepot.RaisonDefinitive(raison) ? new AdventureDepot(false, true, raison) : AdventureDepot.APlusTard(raison);
    }

    public async Task<byte[]?> TelechargerObjetAsync(string aventure, string empreinte, CancellationToken ct)
    {
        var client = Client(TimeSpan.FromMinutes(2));
        if (client is null)
        {
            return null;
        }

        try
        {
            using var reponse = await client.GetAsync(Base + "objects/" + empreinte + "?adventure=" + Uri.EscapeDataString(aventure), ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                return null;
            }

            var octets = await reponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            // Le site rend du gzip (X-Object-Encoding), jamais plus de 16 Mo une fois decompresse.
            if (reponse.Headers.TryGetValues("X-Object-Encoding", out var codage) && codage.Contains("gzip", StringComparer.OrdinalIgnoreCase))
            {
                using var entree = new MemoryStream(octets);
                using var gzip = new GZipStream(entree, CompressionMode.Decompress);
                using var sortie = new MemoryStream();
                var tampon = new byte[81920];
                int lu;
                while ((lu = await gzip.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
                {
                    sortie.Write(tampon, 0, lu);
                    if (sortie.Length > 32L * 1024 * 1024)
                    {
                        return null;
                    }
                }

                return sortie.ToArray();
            }

            return octets;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _journal?.LogDebug(ex, "ADVENTURE : objet injoignable");
            return null;
        }
        finally
        {
            client.Dispose();
        }
    }
}
