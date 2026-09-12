using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

namespace RetroBat.Api.Update;

/// <summary>
/// Met APIExpose a jour vers la derniere release publiee, depuis la borne elle-meme.
///
///   RetroBat.Api.Update.exe                   verifie, telecharge, applique, relance (demande avant d'appliquer)
///   RetroBat.Api.Update.exe --check           dit seulement s'il y a une mise a jour (code 10 si oui, 0 sinon)
///   RetroBat.Api.Update.exe --yes             applique sans question (flotte, tache planifiee)
///   RetroBat.Api.Update.exe --force           reapplique meme si la version est deja la
///   RetroBat.Api.Update.exe --root D:\...     le dossier APIExpose (defaut : celui de cet exe)
///   RetroBat.Api.Update.exe --archive X.7z --sha256 HEX   applique une archive locale (test, borne hors ligne)
///   RetroBat.Api.Update.exe --no-data         le programme seul, sans le Data Pack
///   RetroBat.Api.Update.exe --data-only       le Data Pack seul (l'API doit tourner)
///
/// Le DATA PACK (.MEM, dynpanels, gamelists, controles...) ne voyage pas dans l'archive du
/// programme : l'API le tire fichier par fichier depuis le depot RetroBat-DataPack. Dans la
/// foulee d'une mise a jour, cet exe lui demande une synchronisation immediate et rapporte ce
/// qu'elle a repris.
///
/// Ce qu'il fait, dans l'ordre : lit la version installee sur RetroBat.Api.exe, demande a GitHub
/// la derniere release, telecharge `APIExpose-X.Y.Z-update.7z`, VERIFIE son SHA-256 contre ce que
/// la release publie (sans empreinte, rien n'est applique), extrait dans un dossier de travail,
/// SAUVEGARDE ce qu'il va remplacer, arrete l'API, copie, relance l'API si elle tournait, et
/// verifie qu'elle repond. Si elle ne repond plus : il remet la sauvegarde et la relance.
///
/// Ce qu'il ne touche JAMAIS : `appsettings.json` (la configuration de cette borne), `state/`,
/// `.log/`, `media/`, `resources/` (le Data Pack a sa propre distribution). Il ne charge rien de
/// l'API : il doit pouvoir la remplacer entierement.
///
/// Codes de sortie : 0 fait ou deja a jour ; 10 mise a jour disponible (--check) ; 2 argument,
/// reseau ou archive refusee ; 3 applique mais l'API ne repond plus, sauvegarde remise ;
/// 4 applique, API muette ET sauvegarde impossible a remettre (intervention necessaire).
/// </summary>
internal static class Program
{
    internal const string Depot = "Nelfe80/RetroBat-APIExpose";
    private const string NomArchive = "APIExpose";           // le dossier racine DANS l'archive
    private const string ExeApi = "RetroBat.Api.exe";
    private const string ExeMoi = "RetroBat.Api.Update.exe";
    private const string Configuration = "appsettings.json";

    private static readonly string[] JamaisEcrases = { Configuration };

    private static string _journal = "";

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Lire(args);
        if (options is null)
        {
            Console.Error.WriteLine("Usage : RetroBat.Api.Update.exe [--check] [--yes] [--force] [--no-data | --data-only] [--root <dossier>] [--archive <7z> --sha256 <hex>] [--port <n>]");
            return 2;
        }

        var racine = options.Root ?? AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (options.DataOnly)
        {
            _journal = Path.Combine(racine, ".log", "update.log");
            return await SynchroniserDataPackAsync(new HttpClient(), options.Port).ConfigureAwait(false) ? 0 : 2;
        }
        var exeApi = Path.Combine(racine, ExeApi);
        if (!File.Exists(exeApi))
        {
            Console.Error.WriteLine($"Pas d'APIExpose ici : {exeApi} introuvable. Placez cet exe a la racine d'APIExpose ou passez --root.");
            return 2;
        }

        _journal = Path.Combine(racine, ".log", "update.log");
        NettoyerLAncienMoi(racine);

        var installee = VersionInstallee(exeApi);
        Dire($"APIExpose {installee?.ToString() ?? "(version illisible)"} dans {racine}");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RetroBat-APIExpose-Update", installee?.ToString() ?? "0"));
        http.Timeout = TimeSpan.FromMinutes(10);

        // 1. Ce qui est publie (ou l'archive qu'on nous tend).
        Release release;
        try
        {
            release = options.Archive is not null
                // Une archive locale : la version vient de son nom, l'empreinte de la ligne de commande.
                ? Release.Locale(options.Archive, options.Sha256)
                : await GitHub.DerniereAsync(http).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dire("Impossible de connaitre la derniere release : " + ex.Message);
            return 2;
        }
        Dire($"Derniere release : {release.Version} ({release.AssetName}, {Math.Max(1, release.Taille / 1024 / 1024)} Mo)");

        // 2. Decider.
        if (!options.Force && installee is not null && release.Version <= installee)
        {
            Dire("Deja a jour.");
            return 0;
        }
        if (options.Check)
        {
            Dire($"Mise a jour disponible : {installee?.ToString() ?? "?"} -> {release.Version} (empreinte publiee : {(release.Sha256 is null ? "NON, elle ne pourra pas s'appliquer" : "oui")})");
            return 10;
        }
        if (release.Sha256 is null)
        {
            Dire("Pas d'empreinte SHA-256 publiee pour cette archive : on n'applique pas ce qu'on ne peut pas verifier.");
            return 2;
        }

        // 3. Obtenir l'archive.
        string archive;
        try
        {
            archive = options.Archive
                ?? await TelechargerAsync(http, release, Path.Combine(racine, ".temp", "update")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dire("Telechargement impossible : " + ex.Message);
            return 2;
        }

        // 4. L'EMPREINTE, toujours, avant d'ouvrir l'archive.
        var empreinte = await Sha256Async(archive).ConfigureAwait(false);
        if (!string.Equals(empreinte, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Dire($"Empreinte refusee : {empreinte[..12]}… au lieu de {release.Sha256[..12]}… L'archive n'est pas appliquee.");
            return 2;
        }
        Dire("Empreinte SHA-256 verifiee.");

        if (!options.Yes)
        {
            Console.Write($"Mettre a jour {installee} -> {release.Version} ? L'API sera arretee puis relancee. [o/N] ");
            var reponse = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (reponse is not ("o" or "oui" or "y" or "yes"))
            {
                Dire("Annule.");
                return 0;
            }
        }

        return await AppliquerAsync(http, racine, archive, release, installee, options.Port, options.NoData).ConfigureAwait(false);
    }

    // ── Appliquer ────────────────────────────────────────────────────────────

    private static async Task<int> AppliquerAsync(HttpClient http, string racine, string archive, Release release, Version? installee, int port, bool sansData)
    {
        var travail = Path.Combine(racine, ".temp", "update");
        var scene = Path.Combine(travail, "stage-" + release.Version);
        if (Directory.Exists(scene)) Directory.Delete(scene, recursive: true);
        Directory.CreateDirectory(scene);

        // 1. Extraire dans la scene, hors de la racine : rien n'est touche tant que tout n'est pas la.
        var fichiers = Extraire(archive, scene);
        Dire($"Archive extraite : {fichiers.Count} fichiers.");
        if (!fichiers.Contains(ExeApi))
        {
            Dire($"L'archive ne contient pas {ExeApi} : ce n'est pas une mise a jour d'APIExpose.");
            return 2;
        }

        // 2. Ce qui sera remplace part en sauvegarde, avant d'arreter quoi que ce soit.
        var sauvegarde = Path.Combine(racine, ".archive", $"update-backup-{installee?.ToString() ?? "inconnue"}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var remplaces = 0;
        foreach (var rel in fichiers)
        {
            if (EstProtege(rel, racine)) continue;
            var cible = Path.Combine(racine, rel);
            if (!File.Exists(cible)) continue;
            var copie = Path.Combine(sauvegarde, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(copie)!);
            File.Copy(cible, copie, overwrite: true);
            remplaces++;
        }
        Dire($"Sauvegarde de {remplaces} fichiers dans {sauvegarde}");

        // 3. L'API s'arrete si elle tourne ; on la relancera.
        var tournait = ArreterApi(racine);
        if (tournait) Dire("API arretee.");

        // 4. Copier. appsettings.json n'est jamais ecrase ; cet exe se remplace par renommage.
        var copies = 0;
        try
        {
            foreach (var rel in fichiers)
            {
                if (EstProtege(rel, racine)) continue;
                var source = Path.Combine(scene, rel);
                var cible = Path.Combine(racine, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
                if (string.Equals(rel, ExeMoi, StringComparison.OrdinalIgnoreCase) && File.Exists(cible))
                {
                    // Un exe qui tourne ne se reecrit pas, mais il se RENOMME : on le pousse de
                    // cote, le nouveau prend sa place, et l'ancien sera efface au prochain passage.
                    var vieux = cible + ".old";
                    if (File.Exists(vieux)) File.Delete(vieux);
                    File.Move(cible, vieux);
                }
                File.Copy(source, cible, overwrite: true);
                copies++;
            }
        }
        catch (Exception ex)
        {
            Dire("Copie interrompue : " + ex.Message + ". Remise de la sauvegarde.");
            var remis = Restaurer(sauvegarde, racine);
            if (tournait) LancerApi(racine);
            return remis ? 3 : 4;
        }
        Dire($"{copies} fichiers en place.");

        // 5. Relancer, et VERIFIER qu'elle repond : une mise a jour qui laisse la borne sans API
        //    serait pire que pas de mise a jour.
        if (tournait)
        {
            var version = LancerApi(racine)
                ? await AttendreApiAsync(http, port, TimeSpan.FromSeconds(60)).ConfigureAwait(false)
                : null;
            if (version is null)
            {
                Dire("L'API ne repond pas apres la mise a jour. Remise de la sauvegarde.");
                ArreterApi(racine);
                var remis = Restaurer(sauvegarde, racine);
                var retour = LancerApi(racine)
                    ? await AttendreApiAsync(http, port, TimeSpan.FromSeconds(60)).ConfigureAwait(false)
                    : null;
                Dire(retour is null ? "L'API ne repond toujours pas : intervention necessaire." : $"Version {retour} remise en service.");
                return remis && retour is not null ? 3 : 4;
            }
            Dire($"API relancee, version {version}.");
        }
        else
        {
            Dire("L'API ne tournait pas : elle n'est pas relancee (RetroBat la lancera).");
        }

        try { Directory.Delete(scene, recursive: true); } catch { }
        try { if (archive.StartsWith(travail, StringComparison.OrdinalIgnoreCase)) File.Delete(archive); } catch { }
        Dire($"Mise a jour {installee} -> {release.Version} terminee.");
        if (!sansData && tournait)
        {
            await SynchroniserDataPackAsync(http, port).ConfigureAwait(false);
        }
        else if (!sansData)
        {
            Dire("Data Pack : l'API ne tourne pas, elle synchronisera au prochain demarrage.");
        }
        return 0;
    }

    /// <summary>Ce que l'archive ne doit pas remplacer : la configuration de la borne, si elle existe.</summary>
    private static bool EstProtege(string rel, string racine)
    {
        foreach (var p in JamaisEcrases)
        {
            if (string.Equals(rel, p, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(racine, rel)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Restaurer(string sauvegarde, string racine)
    {
        try
        {
            if (!Directory.Exists(sauvegarde)) return false;
            foreach (var f in Directory.EnumerateFiles(sauvegarde, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sauvegarde, f);
                var cible = Path.Combine(racine, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
                if (string.Equals(rel, ExeMoi, StringComparison.OrdinalIgnoreCase)) continue; // celui-ci tourne, et il n'est pas en cause
                File.Copy(f, cible, overwrite: true);
            }
            Dire("Sauvegarde remise.");
            return true;
        }
        catch (Exception ex)
        {
            Dire("Sauvegarde impossible a remettre : " + ex.Message);
            return false;
        }
    }

    // ── Le Data Pack : par l'API, qui sait le faire ──────────────────────────

    /// <summary>
    /// Demande a l'API une synchronisation immediate du Data Pack et rapporte le bilan. C'est
    /// elle qui a la logique (arbre du depot, empreintes, release gamelist) : la refaire ici
    /// serait la faire deux fois.
    /// </summary>
    private static async Task<bool> SynchroniserDataPackAsync(HttpClient http, int port)
    {
        Dire("Data Pack : synchronisation…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var r = await http.PostAsync($"http://127.0.0.1:{port}/api/v1/maintenance/datapack/sync", new StringContent(""), cts.Token).ConfigureAwait(false);
            var corps = await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                Dire($"Data Pack : l'API repond {(int) r.StatusCode}.");
                return false;
            }
            using var doc = JsonDocument.Parse(corps);
            var e = doc.RootElement;
            int Lire(string nom) => e.TryGetProperty(nom, out var v) && v.TryGetInt32(out var n) ? n : 0;
            var erreurs = e.TryGetProperty("errors", out var le) && le.ValueKind == JsonValueKind.Array ? le.GetArrayLength() : 0;
            Dire($"Data Pack : {Lire("updated")} mis a jour, {Lire("added")} ajoute(s), {Lire("systemsUpdated")} base(s) reprise(s), {Lire("unchanged")} inchange(s){(erreurs > 0 ? $", {erreurs} erreur(s)" : "")}.");
            return erreurs == 0;
        }
        catch (Exception ex)
        {
            Dire("Data Pack : " + ex.Message);
            return false;
        }
    }

    // ── L'API : arreter, lancer, attendre ────────────────────────────────────

    private static bool ArreterApi(string racine)
    {
        var exe = Path.Combine(racine, ExeApi);
        var tournait = false;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeApi)))
        {
            try
            {
                // Seulement CETTE installation : une salle peut en avoir plusieurs.
                var chemin = p.MainModule?.FileName;
                if (chemin is not null && !string.Equals(Path.GetFullPath(chemin), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
                tournait = true;
                p.Kill();
                p.WaitForExit(15000);
            }
            catch (Exception ex)
            {
                Dire("Arret de l'API : " + ex.Message);
            }
            finally { p.Dispose(); }
        }
        // Le verrou du fichier tombe un peu apres le processus.
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var fs = File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return tournait;
            }
            catch (IOException) { Thread.Sleep(250); }
        }
        return tournait;
    }

    /// <summary>Lance l'API. Faux si Windows refuse deja l'exe : inutile alors d'attendre qu'elle reponde.</summary>
    private static bool LancerApi(string racine)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(racine, ExeApi))
            {
                WorkingDirectory = racine,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Dire("Lancement de l'API : " + ex.Message);
            return false;
        }
    }

    private static async Task<string?> AttendreApiAsync(HttpClient http, int port, TimeSpan patience)
    {
        var limite = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < limite)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var r = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/status", cts.Token).ConfigureAwait(false);
                if (r.IsSuccessStatusCode)
                {
                    var corps = await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        // { "apiExpose": { "version": "1.8.3+…" }, … }
                        using var doc = JsonDocument.Parse(corps);
                        if (doc.RootElement.TryGetProperty("apiExpose", out var bloc)
                            && bloc.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            return Release.ParseVersion(v.GetString() ?? "").ToString();
                        }
                    }
                    catch { }
                    return "(en ligne)";
                }
            }
            catch { }
            await Task.Delay(500).ConfigureAwait(false);
        }
        return null;
    }

    // ── L'archive ────────────────────────────────────────────────────────────

    /// <summary>Extrait l'archive dans la scene, sans son dossier racine « APIExpose ». Rend les chemins relatifs.</summary>
    private static List<string> Extraire(string archive, string scene)
    {
        var fichiers = new List<string>();
        using var a = SevenZipArchive.OpenArchive(archive);
        foreach (var entree in a.Entries)
        {
            if (entree.IsDirectory || entree.Key is null) continue;
            var rel = entree.Key.Replace('/', '\\');
            if (rel.StartsWith(NomArchive + "\\", StringComparison.OrdinalIgnoreCase)) rel = rel[(NomArchive.Length + 1)..];
            if (rel.Length == 0 || rel.Contains("..\\", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            {
                throw new InvalidDataException("Chemin refuse dans l'archive : " + entree.Key);
            }
            var cible = Path.Combine(scene, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
            using (var s = entree.OpenEntryStream())
            using (var f = File.Create(cible))
            {
                s.CopyTo(f);
            }
            fichiers.Add(rel);
        }
        return fichiers;
    }

    private static async Task<string> TelechargerAsync(HttpClient http, Release release, string dossier)
    {
        Directory.CreateDirectory(dossier);
        var cible = Path.Combine(dossier, release.AssetName);
        if (File.Exists(cible) && release.Sha256 is not null
            && string.Equals(await Sha256Async(cible).ConfigureAwait(false), release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Dire("Archive deja telechargee et verifiee.");
            return cible;
        }
        Dire($"Telechargement de {release.AssetName}…");
        using var r = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        var total = r.Content.Headers.ContentLength ?? release.Taille;
        await using var entree = await r.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var sortie = File.Create(cible);
        var tampon = new byte[1 << 16];
        long lu = 0, dernierPct = -1;
        int n;
        while ((n = await entree.ReadAsync(tampon).ConfigureAwait(false)) > 0)
        {
            await sortie.WriteAsync(tampon.AsMemory(0, n)).ConfigureAwait(false);
            lu += n;
            if (total > 0)
            {
                var pct = lu * 100 / total;
                if (pct / 10 != dernierPct / 10) { Console.Write($"\r  {pct,3} %"); dernierPct = pct; }
            }
        }
        Console.WriteLine();
        return cible;
    }

    private static async Task<string> Sha256Async(string chemin)
    {
        await using var f = File.OpenRead(chemin);
        return Convert.ToHexString(await SHA256.HashDataAsync(f).ConfigureAwait(false)).ToLowerInvariant();
    }

    // ── Divers ───────────────────────────────────────────────────────────────

    private static Version? VersionInstallee(string exeApi)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exeApi);
            return Release.ParseVersion(info.ProductVersion ?? info.FileVersion ?? "");
        }
        catch { return null; }
    }

    private static void NettoyerLAncienMoi(string racine)
    {
        try
        {
            var vieux = Path.Combine(racine, ExeMoi + ".old");
            if (File.Exists(vieux)) File.Delete(vieux);
        }
        catch { /* encore verrouille par le passage precedent : au suivant */ }
    }

    private static void Dire(string message)
    {
        Console.WriteLine(message);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journal)!);
            File.AppendAllText(_journal, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}

internal sealed record Options(bool Check, bool Yes, bool Force, string? Root, string? Archive, string? Sha256, int Port, bool NoData, bool DataOnly)
{
    public static Options? Lire(string[] args)
    {
        bool check = false, yes = false, force = false, noData = false, dataOnly = false;
        string? root = null, archive = null, sha = null;
        var port = 12345;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--check": check = true; break;
                case "--yes": case "-y": yes = true; break;
                case "--force": force = true; break;
                case "--no-data": noData = true; break;
                case "--data-only": dataOnly = true; break;
                case "--root": if (++i >= args.Length) return null; root = Path.GetFullPath(args[i]); break;
                case "--archive": if (++i >= args.Length) return null; archive = Path.GetFullPath(args[i]); break;
                case "--sha256": if (++i >= args.Length) return null; sha = args[i].Trim().ToLowerInvariant(); break;
                case "--port": if (++i >= args.Length || !int.TryParse(args[i], out port)) return null; break;
                default: return null;
            }
        }
        if (archive is not null && (sha is null || !Regex.IsMatch(sha, "^[0-9a-f]{64}$"))) return null;
        if (noData && dataOnly) return null;
        return new Options(check, yes, force, root, archive, sha, port, noData, dataOnly);
    }
}

/// <summary>Une release : sa version, son archive de mise a jour, l'empreinte publiee.</summary>
internal sealed record Release(Version Version, string AssetName, string AssetUrl, long Taille, string? Sha256)
{
    public static Version ParseVersion(string texte)
    {
        // « v1.8.3 », « 1.8.3+20260909.123216 », « 1.8.3.0 » : on ne garde que les nombres du debut.
        var m = Regex.Match(texte, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return new Version(0, 0);
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    public static Release Locale(string chemin, string? sha)
        => new(ParseVersion(Path.GetFileName(chemin)), Path.GetFileName(chemin), chemin, new FileInfo(chemin).Length, sha);
}

internal static class GitHub
{
    /// <summary>
    /// La derniere release publiee (ni brouillon ni pre-version), son archive `-update.7z`, et
    /// l'empreinte SHA-256 que la release en publie : dans `SHA256SUMS.txt` si l'actif est joint,
    /// sinon dans les notes (« HASH  nom »).
    /// </summary>
    public static async Task<Release> DerniereAsync(HttpClient http)
    {
        using var r = await http.GetAsync($"https://api.github.com/repos/{Program.Depot}/releases/latest").ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub repond {(int) r.StatusCode} pour la derniere release.");
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
        var racine = doc.RootElement;
        var tag = racine.GetProperty("tag_name").GetString() ?? "";
        var version = Release.ParseVersion(tag);

        string? nom = null, url = null, sums = null;
        long taille = 0;
        if (racine.TryGetProperty("assets", out var actifs))
        {
            foreach (var a in actifs.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (n.EndsWith("-update.7z", StringComparison.OrdinalIgnoreCase))
                {
                    nom = n;
                    url = a.GetProperty("browser_download_url").GetString();
                    taille = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                }
                else if (n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = a.GetProperty("browser_download_url").GetString();
                }
            }
        }
        if (nom is null || url is null) throw new InvalidOperationException($"La release {tag} n'a pas d'archive -update.7z.");

        string? sha = null;
        if (sums is not null)
        {
            try
            {
                var texte = await http.GetStringAsync(sums).ConfigureAwait(false);
                sha = TrouverEmpreinte(texte, nom);
            }
            catch { /* on retombe sur les notes */ }
        }
        if (sha is null && racine.TryGetProperty("body", out var corps) && corps.ValueKind == JsonValueKind.String)
        {
            sha = TrouverEmpreinte(corps.GetString() ?? "", nom);
        }
        return new Release(version, nom, url, taille, sha);
    }

    private static string? TrouverEmpreinte(string texte, string nom)
    {
        var m = Regex.Match(texte, @"([0-9A-Fa-f]{64})\s+\*?" + Regex.Escape(nom) + @"\b");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
}
