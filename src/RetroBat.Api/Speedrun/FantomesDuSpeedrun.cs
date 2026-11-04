using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>Les fantomes d'un speedrun : X les fait defiler sur la fiche (CDC MASS RUN D25).</summary>
public enum ChoixDesFantomes
{
    MonRecordEtMondial,
    MonRecord,
    RecordMondial,
    Aucun,
}

/// <summary>
/// LES FANTOMES D'UN SPEEDRUN DE NIVEAU (demande du user du 04/11, CDC MASS RUN §14). Au lancement, le classement du niveau
/// donne le replay de mon record et celui du record mondial ; chacun est converti une fois en .nghost, pose dans le dossier
/// que lit le coeur hote, et le speedrun part sur ce coeur.
/// <para>
/// LA CERTIFICATION PASSE AVANT : le coeur hote n'est lance que si le site admet son empreinte (`ghost_cores`
/// d'open-games). Sinon, hors ligne comme refuse, le speedrun part sur le coeur d'origine, sans fantome : un temps ne se perd
/// jamais pour un fantome. Un fantome pas encore la au bout de <see cref="Budget"/> arrive pour la tentative suivante.
/// </para>
/// </summary>
public sealed class FantomesDuSpeedrun
{
    public const string Coeur = "fceumm_ghosts";

    /// <summary>Super Mario Bros. (World) : le seul contenu dont le coeur hote a le profil.</summary>
    private const string CrcDuContenu = "393a432f";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan FraicheurDeLAdmission = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _http;
    private readonly IReplayManifestStore _manifestes;
    private readonly ReplayStore _objets;
    private readonly ReplayManifestFetcher _recherche;
    private readonly IReplaySourceResolver _sources;
    private readonly ILogger<FantomesDuSpeedrun>? _journal;
    private (DateTime Lu, bool Admis)? _admission;

    public FantomesDuSpeedrun(IHttpClientFactory http, IReplayManifestStore manifestes, ReplayStore objets, ReplayManifestFetcher recherche,
        IReplaySourceResolver sources, ILogger<FantomesDuSpeedrun>? journal = null)
    {
        _http = http;
        _manifestes = manifestes;
        _objets = objets;
        _recherche = recherche;
        _sources = sources;
        _journal = journal;
    }

    public ChoixDesFantomes Choix { get; private set; } = ChoixDesFantomes.MonRecordEtMondial;

    public void ChoixSuivant() => Choix = (ChoixDesFantomes)(((int)Choix + 1) % 4);

    /// <summary>Un speedrun parti sur un etat, sur le coeur d'origine du coeur hote, d'un jeu dont il a le profil. Pur.</summary>
    public static bool PourCeDefi(Reglement r)
        => r.Kind.StartsWith("speedrun_", StringComparison.Ordinal) && r.Etat is not null
           && r.SystemId == "nes" && r.RomGroup == "super-mario-bros" && r.Coeurs.Contains("fceumm");

    /// <summary>Les replays a courir, du classement du niveau : le mien, puis le record, sans doublon. Pur.</summary>
    public static IReadOnlyList<string> Replays(IReadOnlyList<(string Joueur, string? Replay)> lignes, string pseudo, ChoixDesFantomes choix)
    {
        var mien = lignes.FirstOrDefault(l => pseudo.Length > 0 && string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase)).Replay;
        var mondial = lignes.Count > 0 ? lignes[0].Replay : null;
        IEnumerable<string?> voulus = choix switch
        {
            ChoixDesFantomes.MonRecordEtMondial => [mien, mondial],
            ChoixDesFantomes.MonRecord => [mien],
            ChoixDesFantomes.RecordMondial => [mondial],
            _ => [],
        };
        return voulus.OfType<string>().Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Vrai si le speedrun doit partir sur le coeur hote : ses fantomes sont poses. Attend <see cref="Budget"/> au plus.</summary>
    public bool Preparer(Reglement r, string pseudo)
    {
        ViderLeDossier();
        if (!PourCeDefi(r) || Choix == ChoixDesFantomes.Aucun) return false;
        var travail = Task.Run(() => PreparerAsync(r, pseudo, CancellationToken.None));
        try
        {
            if (!travail.Wait(Budget))
            {
                _journal?.LogInformation("Fantomes : pas prets en {Budget} s pour {Defi}, ce depart se fait sans.", Budget.TotalSeconds, r.ChallengeId);
                return false;
            }

            return travail.Result > 0;
        }
        catch (AggregateException e)
        {
            _journal?.LogWarning(e.InnerException, "Fantomes : preparation de {Defi} echouee, le speedrun part sans.", r.ChallengeId);
            return false;
        }
    }

    private async Task<int> PreparerAsync(Reglement r, string pseudo, CancellationToken ct)
    {
        if (!await CoeurAdmisAsync(ct).ConfigureAwait(false))
        {
            _journal?.LogInformation("Fantomes : le coeur hote n'est pas admis par le site, {Defi} part sans.", r.ChallengeId);
            return 0;
        }

        var replays = Replays(await LireLeClassementAsync(r.Ruleset, ct).ConfigureAwait(false), pseudo, Choix);
        var poses = 0;
        foreach (var replay in replays)
        {
            if (await FantomeAsync(replay, ct).ConfigureAwait(false) is not { } fantome) continue;
            var dossier = Directory.CreateDirectory(Dossier).FullName;
            await File.WriteAllBytesAsync(Path.Combine(dossier, $"{poses + 1}-{replay}.nghost"), fantome, ct).ConfigureAwait(false);
            poses++;
        }

        _journal?.LogInformation("Fantomes : {Poses} sur {Voulus} pour {Defi}.", poses, replays.Count, r.ChallengeId);
        return poses;
    }

    private async Task<bool> CoeurAdmisAsync(CancellationToken ct)
    {
        if (_admission is { } connue && DateTime.UtcNow - connue.Lu < FraicheurDeLAdmission) return connue.Admis;
        var coeur = CheminDuCoeur();
        if (coeur is null) return false;
        var empreinte = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(coeur, ct).ConfigureAwait(false))).ToLowerInvariant();
        try
        {
            using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
            using var json = JsonDocument.Parse(await client.GetStringAsync($"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/scores/open-games", ct).ConfigureAwait(false));
            var admis = Admet(json.RootElement, empreinte);
            _admission = (DateTime.UtcNow, admis);
            return admis;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(e, "Fantomes : open-games injoignable.");
            return false;
        }
    }

    /// <summary>L'empreinte du coeur est-elle dans `ghost_cores` ? Pur.</summary>
    internal static bool Admet(JsonElement openGames, string empreinte)
        => openGames.TryGetProperty("ghost_cores", out var coeurs) && coeurs.ValueKind == JsonValueKind.Array
           && coeurs.EnumerateArray().Any(c => string.Equals(c.GetString(), empreinte, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<(string Joueur, string? Replay)>> LireLeClassementAsync(string regle, CancellationToken ct)
    {
        using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
        var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/challenges/board?ruleset={Uri.EscapeDataString(regle)}&limit=100";
        using var json = JsonDocument.Parse(await client.GetStringAsync(url, ct).ConfigureAwait(false));
        return LignesDuClassement(json.RootElement);
    }

    /// <summary>Le joueur et le replay de chaque ligne du classement d'un defi. Pur.</summary>
    internal static IReadOnlyList<(string Joueur, string? Replay)> LignesDuClassement(JsonElement classement)
    {
        if (!classement.TryGetProperty("rows", out var lignes) || lignes.ValueKind != JsonValueKind.Array) return [];
        return lignes.EnumerateArray().Select(l => (
            l.TryGetProperty("player", out var j) ? j.GetString() ?? "" : "",
            l.TryGetProperty("replay", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var id) ? id.GetString() : null)).ToList();
    }

    /// <summary>Le .nghost d'un replay : converti une fois, garde ensuite ; null si le replay n'est pas joignable.</summary>
    private async Task<byte[]?> FantomeAsync(string replay, CancellationToken ct)
    {
        var cache = Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "fantomes", replay + ".nghost");
        if (File.Exists(cache)) return await File.ReadAllBytesAsync(cache, ct).ConfigureAwait(false);
        var manifeste = _manifestes.GetManifest(replay);
        if (manifeste is null)
        {
            manifeste = await _recherche.FetchAsync(replay, ct).ConfigureAwait(false);
            if (manifeste is null) return null;
            _manifestes.SaveManifest(manifeste);
        }

        if (!_objets.HasObject(manifeste.Object.Sha256)
            && await _sources.FindObjectAsync(manifeste, ct).ConfigureAwait(false) != ReplayObjectSearch.Present)
        {
            return null;
        }

        if (await _objets.EnsureRawAsync(manifeste.Object.Sha256, ct).ConfigureAwait(false) is not { } brut) return null;
        try
        {
            var fantome = ReplayVersFantome.Convertir(await File.ReadAllBytesAsync(brut, ct).ConfigureAwait(false));
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllBytesAsync(cache, fantome, ct).ConfigureAwait(false);
            return fantome;
        }
        catch (InvalidDataException e)
        {
            _journal?.LogWarning("Fantomes : replay {Replay} illisible : {Message}", replay, e.Message);
            return null;
        }
    }

    private static string Dossier => Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "nelfeplay", "ghosts", CrcDuContenu);

    /// <summary>
    /// Les fantomes poses ici sortent du dossier que lit le coeur hote : ils ne restent pas pour la partie suivante (replay,
    /// MASS RUN). Seulement les notres (« 1-rp_….nghost ») : un fantome d'essai pose a la main ne se touche pas.
    /// </summary>
    public static void ViderLeDossier()
    {
        try
        {
            if (!Directory.Exists(Dossier)) return;
            foreach (var f in Directory.EnumerateFiles(Dossier, "*-rp_*.nghost")) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // un coeur encore ouvert : le prochain depart videra
        }
    }

    private static string? CheminDuCoeur()
    {
        var retroarch = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");
        return new[] { "cores_real", "cores" }.Select(d => Path.Combine(retroarch, d, Coeur + "_libretro.dll")).FirstOrDefault(File.Exists);
    }
}
