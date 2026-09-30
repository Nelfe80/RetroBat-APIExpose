using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// La borne se met a jour toute seule, au lancement d'APIExpose.
///
/// Le travail lui-meme est fait par <c>RetroBat.Api.Update.exe</c>, a la racine : il telecharge
/// l'archive de la derniere release, verifie son empreinte, sauvegarde ce qu'il remplace, arrete
/// l'API, copie, la relance et remet la sauvegarde si elle ne repond plus. Ce service-ci ne fait
/// que DECIDER : y a-t-il une version plus recente, et est-ce le moment.
///
/// LE MOMENT, c'est tout le sujet. Mettre a jour, c'est arreter l'API : pendant une partie, cela
/// perdrait le score en cours (le passeport est assemble a la fin), pendant un replay cela
/// couperait la lecture, pendant un direct cela viderait la scene. On ne bouge donc que quand
/// RIEN ne tourne : pas d'emulateur, pas de lecture de replay. Sinon on repasse plus tard, et au
/// pire la mise a jour attend le prochain demarrage, ce qui est exactement ce qu'on veut.
///
/// Le lancement est DETACHE : l'updater doit survivre a l'arret de l'API, puisque c'est lui qui
/// l'arrete.
///
/// LES OUTILS HORS ARCHIVE (2026-10-01) : l'outil de diagnostic ne voyage plus dans l'update.7z,
/// dont il faisait presque tout le poids. Chaque release porte `outils.json` et ce service ne
/// telecharge que l'outil qui differe du sien, sans arreter l'API (voir
/// <see cref="MettreLesOutilsAJourAsync"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelfUpdateService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Replay.Playback.ReplayPlaybackService _playback;
    private readonly ILogger<SelfUpdateService> _logger;
    private int _enCours;

    /// <summary>
    /// Les SEULS fichiers qu'un outils.json peut faire ecrire : il dit ou prendre un outil, il ne
    /// choisit pas ce qu'il remplace. L'API et l'updater passent par l'update.7z.
    /// </summary>
    internal static readonly string[] OutilsHorsArchive = ["RetroBat.Api.Diagnostic.exe"];

    /// <summary>Plafond d'un outil : l'outil de diagnostic pese ~66 Mo.</summary>
    private const long TailleMaxOutil = 512L * 1024 * 1024;

    public SelfUpdateService(
        IOptionsMonitor<ApiExposeOptions> options,
        IHttpClientFactory httpFactory,
        Replay.Playback.ReplayPlaybackService playback,
        ILogger<SelfUpdateService> logger)
    {
        _options = options;
        _httpFactory = httpFactory;
        _playback = playback;
        _logger = logger;
    }

    private static string ExeUpdater => Path.Combine(RetroBatPaths.PluginRoot, "RetroBat.Api.Update.exe");

    /// <summary>La version installee, lue sur l'exe de l'API (jamais sur l'assembly : c'est le
    /// FICHIER que l'updater remplacera).</summary>
    public static Version? VersionInstallee()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(RetroBatPaths.PluginRoot, "RetroBat.Api.exe"));
            return LireVersion(info.ProductVersion ?? info.FileVersion ?? "");
        }
        catch { return null; }
    }

    internal static Version? LireVersion(string texte)
    {
        var m = Regex.Match(texte ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0)
            : null;
    }

    /// <summary>Ce qui empeche une mise a jour MAINTENANT, ou une chaine vide si rien ne l'empeche.</summary>
    public string Occupe()
    {
        if (_playback.IsBusy) return "un replay est en lecture";
        if (EmulatorForeground.EmulateurTourne()) return "un jeu est en cours";
        // Le lanceur de RetroBat vit toute la partie, quel que soit l'emulateur : il couvre ceux
        // que la liste ne nomme pas, et le moment ou l'emulateur n'a pas encore demarre.
        if (Process.GetProcessesByName("emulatorLauncher") is { Length: > 0 } lanceurs)
        {
            foreach (var p in lanceurs) p.Dispose();
            return "un jeu est en cours";
        }
        return "";
    }

    /// <summary>Y a-t-il une version plus recente publiee ? Rien n'est telecharge ici.</summary>
    public async Task<SelfUpdateStatus> VerifierAsync(CancellationToken ct)
    {
        var opt = _options.CurrentValue.SelfUpdate;
        var etat = new SelfUpdateStatus
        {
            Installed = VersionInstallee()?.ToString(),
            UpdaterPresent = File.Exists(ExeUpdater),
            Busy = Occupe(),
        };
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("APIExpose-SelfUpdate");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var corps = await client.GetStringAsync(
                $"https://api.github.com/repos/{opt.Repository}/releases/latest", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(corps);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            etat.Latest = LireVersion(tag)?.ToString();
            etat.ToolsManifestUrl = AdresseDesOutils(doc.RootElement);
        }
        catch (Exception ex)
        {
            etat.Error = ex.Message;
            return etat;
        }
        var installee = VersionInstallee();
        var publiee = LireVersion(etat.Latest ?? "");
        etat.UpdateAvailable = publiee is not null && (installee is null || publiee > installee);
        return etat;
    }

    /// <summary>
    /// Lance la mise a jour, si elle a lieu d'etre. Rend ce qui a ete decide : rien n'est attendu
    /// ici, l'updater arretera cette API elle-meme.
    /// </summary>
    public async Task<SelfUpdateStatus> AppliquerAsync(bool force, CancellationToken ct)
    {
        var etat = await VerifierAsync(ct).ConfigureAwait(false);
        if (!etat.UpdaterPresent)
        {
            etat.Error = "RetroBat.Api.Update.exe absent a la racine.";
            return etat;
        }
        if (!force && !etat.UpdateAvailable)
        {
            return etat;
        }
        if (etat.Busy.Length > 0)
        {
            _logger.LogInformation("Mise a jour {Version} remise a plus tard : {Raison}.", etat.Latest, etat.Busy);
            return etat;
        }
        if (Interlocked.Exchange(ref _enCours, 1) == 1)
        {
            etat.Error = "une mise a jour est deja lancee";
            return etat;
        }

        try
        {
            var args = force ? "--yes --force" : "--yes";
            _logger.LogWarning(
                "Mise a jour {De} -> {Vers} : lancement de l'updater, l'API va s'arreter puis revenir.",
                etat.Installed, etat.Latest);
            Process.Start(new ProcessStartInfo(ExeUpdater, args)
            {
                WorkingDirectory = RetroBatPaths.PluginRoot,
                UseShellExecute = true,          // detache : il doit survivre a l'arret de cette API
                WindowStyle = ProcessWindowStyle.Minimized,
            });
            etat.Started = true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _enCours, 0);
            etat.Error = ex.Message;
            _logger.LogWarning(ex, "Mise a jour : l'updater n'a pas demarre.");
        }
        return etat;
    }

    // ── Les outils hors archive ──────────────────────────────────────────────

    /// <summary>L'adresse de l'actif outils.json d'une release lue sur l'API GitHub, ou null.</summary>
    internal static string? AdresseDesOutils(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var actifs) || actifs.ValueKind != JsonValueKind.Array) return null;
        foreach (var actif in actifs.EnumerateArray())
        {
            if (actif.TryGetProperty("name", out var nom) && nom.GetString() == "outils.json"
                && actif.TryGetProperty("browser_download_url", out var url))
            {
                return url.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Ce qu'on retient d'un outils.json : les outils nommes par <see cref="OutilsHorsArchive"/>,
    /// avec une empreinte SHA-256 et une adresse dans les releases du depot. Le reste est ignore.
    /// </summary>
    internal static List<OutilPublie> LireOutils(string json, string depot)
    {
        var retenus = new List<OutilPublie>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("outils", out var liste) || liste.ValueKind != JsonValueKind.Array) return retenus;
        var prefixe = $"https://github.com/{depot}/releases/download/";
        foreach (var o in liste.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) continue;
            string Texte(string cle) => o.TryGetProperty(cle, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var fichier = OutilsHorsArchive.FirstOrDefault(connu => string.Equals(connu, Texte("fichier"), StringComparison.OrdinalIgnoreCase));
            var sha = Texte("sha256").ToLowerInvariant();
            var url = Texte("url");
            var taille = o.TryGetProperty("taille", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var n) ? n : 0;
            if (fichier is null
                || !Regex.IsMatch(sha, "^[0-9a-f]{64}$")
                || !url.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase)
                || taille <= 0 || taille > TailleMaxOutil)
            {
                continue;
            }
            retenus.Add(new OutilPublie(fichier, Texte("version"), sha, taille, url));
        }
        return retenus;
    }

    /// <summary>
    /// Met a jour les outils qui voyagent hors de l'update.7z (l'outil de diagnostic) : lit
    /// outils.json, et ne telecharge que l'outil que la borne n'a pas, ou dans une version plus
    /// ancienne (<see cref="AMettreAJour"/>). L'API ne s'arrete pas : ces outils ne sont pas
    /// charges par elle. Jamais pendant une partie ni un replay ; un outil OUVERT est renomme en
    /// .old et le suivant sera le nouveau. Une erreur n'empeche rien : on repasse au prochain tour.
    /// </summary>
    public async Task MettreLesOutilsAJourAsync(string urlOutils, CancellationToken ct)
    {
        var depot = _options.CurrentValue.SelfUpdate.Repository;
        using var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("APIExpose-SelfUpdate");
        List<OutilPublie> outils;
        try
        {
            outils = LireOutils(await client.GetStringAsync(urlOutils, ct).ConfigureAwait(false), depot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Outils : outils.json illisible.");
            return;
        }

        foreach (var outil in outils)
        {
            var cible = Path.Combine(RetroBatPaths.PluginRoot, outil.Fichier);
            try { File.Delete(cible + ".old"); } catch { }
            if (!AMettreAJour(VersionLocale(cible), outil.Version)) continue;
            var occupe = Occupe();
            if (occupe.Length > 0)
            {
                _logger.LogInformation("Outil {Outil} {Version} remis a plus tard : {Raison}.", outil.Fichier, outil.Version, occupe);
                return;
            }

            var partiel = Path.Combine(RetroBatPaths.PluginRoot, ".temp", "outils", outil.Fichier + ".part");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(partiel)!);
                await TelechargerAsync(client, outil, partiel, ct).ConfigureAwait(false);
                Poser(partiel, cible);
                _logger.LogInformation("Outil {Outil} mis a jour : {Version}.", outil.Fichier, outil.Version);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Outil {Outil} {Version} pas mis a jour : {Erreur}", outil.Fichier, outil.Version, ex.Message);
            }
            finally
            {
                try { File.Delete(partiel); } catch { }
            }
        }
    }

    /// <summary>
    /// Faut-il prendre l'outil publie ? Oui s'il manque ici (<paramref name="versionLocale"/> nulle),
    /// s'il est illisible, ou si le sien est plus ancien. JAMAIS pour redescendre : la borne de
    /// developpement garde l'outil qu'elle vient de construire, et release.ps1 refuse de publier un
    /// outil change sans version plus haute.
    /// </summary>
    internal static bool AMettreAJour(string? versionLocale, string versionPubliee)
    {
        var publiee = LireVersion(versionPubliee);
        if (publiee is null) return false;
        var locale = versionLocale is null ? null : LireVersion(versionLocale);
        return locale is null || locale < publiee;
    }

    /// <summary>La version de l'outil de la borne : null s'il n'y est pas, vide s'il est illisible.</summary>
    private static string? VersionLocale(string chemin)
    {
        if (!File.Exists(chemin)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(chemin);
            return info.ProductVersion ?? info.FileVersion ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Telecharge l'outil et VERIFIE sa taille et son empreinte : rien n'est pose sans.</summary>
    private static async Task TelechargerAsync(HttpClient client, OutilPublie outil, string partiel, CancellationToken ct)
    {
        using var reponse = await client.GetAsync(outil.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        reponse.EnsureSuccessStatusCode();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long recu = 0;
        await using (var source = await reponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fichier = File.Create(partiel))
        {
            var tampon = new byte[81920];
            int lu;
            while ((lu = await source.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
            {
                recu += lu;
                if (recu > outil.Taille) throw new InvalidDataException($"plus gros que les {outil.Taille} octets annonces");
                sha.AppendData(tampon, 0, lu);
                await fichier.WriteAsync(tampon.AsMemory(0, lu), ct).ConfigureAwait(false);
            }
        }
        if (recu != outil.Taille) throw new InvalidDataException($"{recu} octets recus au lieu de {outil.Taille}");
        var empreinte = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (empreinte != outil.Sha256) throw new InvalidDataException($"empreinte {empreinte[..12]} au lieu de {outil.Sha256[..12]}");
    }

    /// <summary>
    /// Met le nouvel outil a sa place. Un exe OUVERT ne s'ecrase pas mais se renomme : l'ancien
    /// part en .old (efface au tour suivant), l'instance ouverte continue, la suivante sera la neuve.
    /// </summary>
    internal static void Poser(string nouveau, string cible)
    {
        try
        {
            File.Move(nouveau, cible, overwrite: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var vieux = cible + ".old";
        if (File.Exists(vieux)) File.Delete(vieux);
        File.Move(cible, vieux);
        File.Move(nouveau, cible);
    }
}

/// <summary>Un outil hors archive, tel que la release le publie dans outils.json.</summary>
public sealed record OutilPublie(string Fichier, string Version, string Sha256, long Taille, string Url);

/// <summary>L'etat d'une verification de mise a jour.</summary>
public sealed class SelfUpdateStatus
{
    public string? Installed { get; set; }
    public string? Latest { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool UpdaterPresent { get; set; }
    /// <summary>Ce qui empeche de bouger maintenant (jeu, replay), ou vide.</summary>
    public string Busy { get; set; } = "";
    public bool Started { get; set; }
    public string? Error { get; set; }
    /// <summary>L'actif outils.json de la derniere release, s'il y en a un (usage interne).</summary>
    [JsonIgnore]
    public string? ToolsManifestUrl { get; set; }
}
