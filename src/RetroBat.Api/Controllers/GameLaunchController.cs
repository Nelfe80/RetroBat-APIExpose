using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Controllers;

/// <summary>
/// « Lancer ce jeu » depuis le site, par NAVIGATION.
///
/// Pourquoi une route de plus alors que <c>POST /api/v1/commands/launch</c> existe : ce
/// dernier attend un CHEMIN de ROM absolu sur cette machine, et une page web ne le connaît
/// pas — elle ne connaît qu'une identité de scoring, <c>système + rom_group</c>. Elle ne peut
/// pas non plus l'appeler : un fetch HTTPS vers le loopback est bloqué par le navigateur
/// (Local Network Access). Une NAVIGATION, elle, passe : c'est déjà par là que passent la
/// sonde d'installation et la lecture d'un replay.
///
/// La borne fait donc les deux choses que le site ne peut pas faire : elle RÉSOUT le
/// rom_group en ROM installée, puis elle lance.
///
/// GARDE : un jeton de lancement à usage unique, le même que pour la lecture d'un replay.
/// Sans lui, n'importe quelle page pourrait, d'un simple lien, démarrer un jeu sur la borne
/// de qui la visite. Le jeton n'est émis qu'au handshake de détection, et seule une page du
/// site peut le récupérer.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
[SupportedOSPlatform("windows")]
public sealed class GameLaunchController : ControllerBase
{
    private static readonly Uri EmulationStationBaseUri = new("http://127.0.0.1:1234");

    /// <summary>
    /// Le scoring range tout l'arcade sous un seul système, « arcade », parce qu'un même jeu
    /// s'y joue sous plusieurs émulateurs. RetroBat, lui, le range par émulateur. On cherche
    /// donc dans les dossiers qui peuvent le porter, dans l'ordre où on préfère les trouver.
    /// </summary>
    private static readonly string[] DossiersArcade = ["fbneo", "mame", "mame64", "fba", "arcade"];

    private readonly IGamelistStore _gamelists;
    private readonly RomCanonicalResolver _canonical;
    private readonly ReplayLaunchTokenStore _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GameLaunchController> _logger;

    public GameLaunchController(
        IGamelistStore gamelists,
        RomCanonicalResolver canonical,
        ReplayLaunchTokenStore tokens,
        IHttpClientFactory httpFactory,
        ILogger<GameLaunchController> logger)
    {
        _gamelists = gamelists;
        _canonical = canonical;
        _tokens = tokens;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [HttpGet("/nelfeplay/launch")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Launch(
        [FromQuery] string? system,
        [FromQuery] string? game,
        [FromQuery] string? token,
        [FromQuery(Name = "to")] string? to,
        CancellationToken ct)
    {
        var retour = NelfeReturnUrl.SafeBase(to);

        if (!_tokens.Consume(token))
        {
            return Redirect(Retour(retour, "no_token", null));
        }
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(game))
        {
            return Redirect(Retour(retour, "bad_request", null));
        }

        var rom = Trouver(system, game);
        if (rom is null)
        {
            // Le jeu est au classement mais pas sur CETTE borne : ce n'est pas une panne,
            // c'est une information, et le site sait la dire.
            return Redirect(Retour(retour, "not_installed", null));
        }

        try
        {
            // EmulationStation doit avoir le premier plan AVANT le lancement, sinon il
            // demarre l'emulateur derriere le navigateur - c'est le navigateur qui a le
            // focus au moment ou on arrive ici.
            EmulatorForeground.FocusEmulationStation();

            using var content = new StringContent(rom, Encoding.UTF8, "text/plain");
            var client = _httpFactory.CreateClient();
            client.BaseAddress = EmulationStationBaseUri;
            client.Timeout = TimeSpan.FromSeconds(5);
            using var reponse = await client.PostAsync("/launch", content, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                return Redirect(Retour(retour, "es_refused", null));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lancement refuse : EmulationStation injoignable.");
            return Redirect(Retour(retour, "es_unreachable", null));
        }

        // Puis l'emulateur, des que sa fenetre existe. Sans attendre : la reponse HTTP n'a
        // pas a patienter le temps qu'un coeur charge une ROM d'arcade.
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();

        return Redirect(Retour(retour, null, game));
    }

    private static string Retour(string racine, string? raison, string? jeu)
    {
        var q = raison is null
            ? "?launched=1"
            : "?launched=0&reason=" + Uri.EscapeDataString(raison);
        if (jeu is { Length: > 0 })
        {
            q += "&game=" + Uri.EscapeDataString(jeu);
        }
        return racine + q;
    }

    /// <summary>
    /// La ROM installée dont l'identité de SCORING est ce rom_group, ou null.
    ///
    /// On compare des identités de scoring et non des noms : c'est la même résolution qui
    /// décide, à la fin d'une partie, sous quel groupe le score est publié. Comparer des
    /// libellés ferait diverger le jeu lancé et le classement affiché.
    /// </summary>
    private string? Trouver(string system, string romGroup)
    {
        var cible = romGroup.Trim().ToLowerInvariant();
        var systeme = system.Trim().ToLowerInvariant();
        var dossiers = systeme is "arcade" ? DossiersArcade : [systeme];

        foreach (var dossier in dossiers)
        {
            // Un identifiant de système ne doit jamais devenir un chemin : seul un nom de
            // dossier simple est accepte.
            if (dossier.Length == 0 || !string.Equals(Path.GetFileName(dossier), dossier, StringComparison.Ordinal))
            {
                continue;
            }

            var racine = Path.Combine(RetroBatPaths.RomsRoot, dossier);
            var chemin = Path.Combine(racine, "gamelist.xml");
            if (!System.IO.File.Exists(chemin))
            {
                continue;
            }

            XDocument? doc;
            lock (_gamelists.GetLock(chemin))
            {
                doc = _gamelists.Load(chemin, LoadOptions.None);
            }
            if (doc?.Root is null)
            {
                continue;
            }

            foreach (var jeu in doc.Root.Elements("game"))
            {
                var brut = ((string?)jeu.Element("path") ?? "").Trim().Replace('\\', '/');
                var fichier = Path.GetFileName(brut);
                if (fichier.Length == 0)
                {
                    continue;
                }

                var md5 = ((string?)jeu.Element("md5") ?? "").Trim().ToLowerInvariant();
                var cheevos = ((string?)jeu.Element("cheevosHash") ?? "").Trim();
                var slug = _canonical.ResolveScoreSlug(dossier, fichier, md5, cheevos);
                if (slug is null || !string.Equals(slug, cible, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var complet = Path.GetFullPath(Path.Combine(racine, fichier));
                if (System.IO.File.Exists(complet) || Directory.Exists(complet))
                {
                    return complet;
                }
            }
        }

        return null;
    }
}
