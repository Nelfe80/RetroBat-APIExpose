using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelfUpdateService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Replay.Playback.ReplayPlaybackService _playback;
    private readonly ILogger<SelfUpdateService> _logger;
    private int _enCours;

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
}

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
}
