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
    /// choisit pas ce qu'il remplace. L'API et l'updater passent par l'update.7z. L'exe du direct
    /// (depot prive NelfePlay-LiveStreaming, decision du user du 15 octobre 2026) suit le meme chemin.
    /// </summary>
    internal static readonly string[] OutilsHorsArchive = ["RetroBat.Api.Diagnostic.exe", "NelfePlay.LiveStreaming.exe",
        // RETROARCH NELFEPLAY (2026-10-16) : l'archive de la nocturne corrigee, que pose install-retroarch-nelfeplay.bat
        // (RetroArchNelfePlayService la lance). Une archive n'a pas de version : elle se reprend sur son empreinte.
        RetroArchNelfePlay.CheminDeLArchive,
        // LES COEURS DE MULTIPLAYER (2026-10-23, NGP Link le 25/10) : mGBA Splitscreen et NGP Link compiles par NelfePlay,
        // et leurs fiches, que MultiplayerCoeursService pose chez RetroArch. Ni RetroBat ni libretro ne les distribuent.
        .. RetroBat.Api.Multiplayer.MultiplayerCoeursService.Coeurs.SelectMany(c => c.CheminsDesOutils)];

    /// <summary>
    /// LE PLAYERDECK (1.9.163) : son exe, sa configuration, ses Decks officiels et ses polices, sous <c>tools/playerdeck</c>.
    /// Les Decks et les polices changent de nom d'une version a l'autre : la liste ne les enumere pas, elle les prend par
    /// leur dossier et leur extension (un nom simple, sans sous-dossier ni « .. »).
    /// </summary>
    internal const string DossierDuDeck = "tools/playerdeck";

    internal const string ExeDuDeck = DossierDuDeck + "/NelfePlay.PlayerDeck.exe";

    /// <summary>La configuration du Deck : posee seulement si elle manque (le joueur peut l'avoir reglee).</summary>
    internal const string ConfigurationDuDeck = DossierDuDeck + "/PlayerDeck.ini";

    private static readonly Regex FichierDuDeck = new(
        @"^tools/playerdeck/(NelfePlay\.PlayerDeck\.exe|PlayerDeck\.ini|themes/[A-Za-z0-9][A-Za-z0-9._-]*\.json|fonts/[A-Za-z0-9][A-Za-z0-9._-]*\.(ttf|otf|txt))$",
        RegexOptions.CultureInvariant);

    /// <summary>Un chemin d'outils.json est-il un fichier du PlayerDeck ? Rend son nom canonique (separateur « / »), ou null.</summary>
    internal static string? FichierDuPlayerDeck(string chemin)
    {
        var c = (chemin ?? "").Replace('\\', '/');
        if (c.Contains("..", StringComparison.Ordinal) || !FichierDuDeck.IsMatch(c)) return null;
        return c;
    }

    internal static bool EstDuPlayerDeck(string fichier) => fichier.StartsWith(DossierDuDeck + "/", StringComparison.OrdinalIgnoreCase);

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
            var fichier = OutilsHorsArchive.FirstOrDefault(connu => string.Equals(connu, Texte("fichier"), StringComparison.OrdinalIgnoreCase))
                ?? FichierDuPlayerDeck(Texte("fichier"))
                // LE VOCAL DISCORD (lot B, 12/11) : l'exe, la DLL du SDK, ses notices et Krisp, a la racine, par leur nom.
                ?? RetroBat.Api.VocalRoom.VocalRoomFichiers.FichierDuVocal(Texte("fichier"));
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
    /// charges par elle, d'ou l'installation tout de suite, partie en cours ou pas ; un outil OUVERT
    /// est renomme en .old et le suivant sera le nouveau. Une erreur n'empeche rien : on repasse au
    /// prochain tour.
    /// </summary>
    /// <returns>Vrai si des fichiers du PlayerDeck sont remis a la fin de la partie en cours (le Deck l'habille).</returns>
    public async Task<bool> MettreLesOutilsAJourAsync(string urlOutils, CancellationToken ct)
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
            return false;
        }

        var aPoser = new List<OutilPublie>();
        foreach (var outil in outils)
        {
            var cible = Path.Combine(RetroBatPaths.PluginRoot, outil.Fichier);
            try { File.Delete(cible + ".old"); } catch { }
            if (!APrendre(outil, cible)) continue;
            aPoser.Add(outil);
        }

        // LE PLAYERDECK OUVERT (1.9.163) : son exe et ses polices (chargees par GDI) sont tenus tant qu'il tourne. Hors partie,
        // on l'arrete : il repartira au prochain lancement avec Deck. Pendant une partie, il habille l'ecran du joueur : ses
        // fichiers attendent la fin du jeu (le service rappelle a la fermeture), les autres outils passent tout de suite.
        var remis = false;
        if (aPoser.Any(o => EstDuPlayerDeck(o.Fichier)))
        {
            switch (DecisionDuDeck(LeDeckTourne(), Occupe().Length > 0))
            {
                case DecisionDeck.Remettre:
                    aPoser.RemoveAll(o => EstDuPlayerDeck(o.Fichier));
                    remis = true;
                    _logger.LogInformation("PlayerDeck : mise a jour remise a la fin de la partie.");
                    break;
                case DecisionDeck.ArreterPuisPoser:
                    ArreterLeDeck();
                    break;
            }
        }

        // LE LOT DU VOCAL (lot B, 12/11 ; CDC NelfeVoice §5) : l'exe, discord_partner_sdk.dll, ses notices et Krisp se posent
        // ENSEMBLE, jamais pendant que l'exe tourne (il tient la DLL du SDK : une session vocale est en cours). Tout est
        // telecharge d'abord ; un seul echec, et rien n'est pose : l'ancien lot reste entier.
        var vocaux = aPoser.Where(o => RetroBat.Api.VocalRoom.VocalRoomFichiers.EstDuVocal(o.Fichier)).ToList();
        if (vocaux.Count > 0)
        {
            aPoser.RemoveAll(o => RetroBat.Api.VocalRoom.VocalRoomFichiers.EstDuVocal(o.Fichier));
            if (DecisionDuVocal(LeVocalTourne(), sessionVocale: false) == DecisionVocal.Remettre)
            {
                remis = true;
                _logger.LogInformation("Vocal : mise a jour remise a la fin de la session vocale.");
            }
            else
            {
                await PoserLeLotDuVocalAsync(client, vocaux, ct).ConfigureAwait(false);
            }
        }

        foreach (var outil in aPoser)
        {
            var cible = Path.Combine(RetroBatPaths.PluginRoot, outil.Fichier);
            // PARTIE EN COURS OU PAS (demande user, 2026-10-16 : « une mise a jour devrait tout faire
            // immediatement »). Un outil n'est pas charge par l'API : le remplacer ne coupe rien, et un
            // outil ouvert est renomme puis remplace (Poser). Il attendait la fin de la partie, puis la
            // verification suivante, six heures plus tard.

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

        return remis;
    }

    /// <summary>
    /// Faut-il prendre cet outil ? Un exe se compare par sa version ; une archive ou un fichier du Deck (sans version) par son
    /// empreinte. La configuration du Deck n'est posee que si elle manque : le joueur peut l'avoir reglee.
    /// </summary>
    private static bool APrendre(OutilPublie outil, string cible)
    {
        if (string.Equals(outil.Fichier, ConfigurationDuDeck, StringComparison.OrdinalIgnoreCase)) return !File.Exists(cible);
        return EstUnExe(outil.Fichier) ? AMettreAJour(VersionLocale(cible), outil.Version) : !MemeEmpreinte(cible, outil.Sha256);
    }

    public enum DecisionDeck { Poser, ArreterPuisPoser, Remettre }

    public enum DecisionVocal { Poser, Remettre }

    /// <summary>
    /// Le lot du vocal se pose-t-il maintenant ? Jamais sous une session vocale ni pendant que l'exe tourne : il tient la DLL
    /// du SDK et l'appel des joueurs. Pur.
    /// </summary>
    internal static DecisionVocal DecisionDuVocal(bool exeOuvert, bool sessionVocale)
        => exeOuvert || sessionVocale ? DecisionVocal.Remettre : DecisionVocal.Poser;

    private static bool LeVocalTourne()
    {
        var p = Process.GetProcessesByName(RetroBat.Api.VocalRoom.VocalRoomFichiers.Processus);
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
    }

    /// <summary>Telecharge tout le lot du vocal, puis le pose en entier ; au premier echec, rien n'est pose.</summary>
    private async Task PoserLeLotDuVocalAsync(HttpClient client, IReadOnlyList<OutilPublie> lot, CancellationToken ct)
    {
        var partiels = new List<(OutilPublie Outil, string Partiel)>();
        try
        {
            foreach (var outil in lot)
            {
                var partiel = Path.Combine(RetroBatPaths.PluginRoot, ".temp", "outils", outil.Fichier + ".part");
                Directory.CreateDirectory(Path.GetDirectoryName(partiel)!);
                partiels.Add((outil, partiel));
                await TelechargerAsync(client, outil, partiel, ct).ConfigureAwait(false);
            }

            foreach (var (outil, partiel) in partiels)
            {
                Poser(partiel, Path.Combine(RetroBatPaths.PluginRoot, outil.Fichier));
                _logger.LogInformation("Vocal : {Outil} mis a jour : {Version}.", outil.Fichier, outil.Version);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Vocal : lot {Version} pas mis a jour, l'ancien reste ({Erreur}).", lot[0].Version, ex.Message);
        }
        finally
        {
            foreach (var (_, partiel) in partiels)
            {
                try { File.Delete(partiel); } catch { }
            }
        }
    }

    /// <summary>Que faire des fichiers du Deck : les poser, arreter le Deck d'abord, ou attendre la fin de la partie. Pur.</summary>
    internal static DecisionDeck DecisionDuDeck(bool deckTourne, bool partieEnCours)
        => !deckTourne ? DecisionDeck.Poser : partieEnCours ? DecisionDeck.Remettre : DecisionDeck.ArreterPuisPoser;

    private static bool LeDeckTourne()
    {
        var p = Process.GetProcessesByName(Deck.DeckLaunchService.NomDuHost);
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
    }

    /// <summary>Arrete le PlayerDeck (fenetre et rendu) avant de remplacer ses fichiers ; il repartira au prochain lancement.</summary>
    internal void ArreterLeDeck()
    {
        foreach (var p in Process.GetProcessesByName(Deck.DeckLaunchService.NomDuHost))
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
                _logger.LogInformation("PlayerDeck arrete pour sa mise a jour (pid {Pid}).", p.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "PlayerDeck : arret impossible.");
            }
            finally
            {
                p.Dispose();
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

    internal static bool EstUnExe(string fichier) => fichier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Le fichier de la borne a-t-il deja l'empreinte publiee ? Faux s'il manque ou se lit mal.</summary>
    private static bool MemeEmpreinte(string chemin, string sha256)
    {
        try
        {
            if (!File.Exists(chemin)) return false;
            using var flux = File.OpenRead(chemin);
            return string.Equals(Convert.ToHexString(SHA256.HashData(flux)), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
        if (Path.GetDirectoryName(cible) is { Length: > 0 } dossier) Directory.CreateDirectory(dossier);
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
