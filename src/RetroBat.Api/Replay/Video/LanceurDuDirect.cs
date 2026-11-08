using System.Diagnostics;
using System.Globalization;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE DIRECT FABRIQUE PAR <c>NelfePlay.LiveStreaming.exe</c> (CDC video V2, §9 quater ; decisions du user du 15 octobre
/// 2026 : le direct se fait sur la borne du joueur, sans ffmpeg, par un exe livre avec APIExpose comme l'outil de
/// diagnostic). L'exe lit le film que RetroArch ecrit, le redresse si le jeu est vertical, le reencode avec ce que
/// Windows fournit (la puce graphique si possible) et l'envoie a l'adresse du direct. L'adresse porte la cle de
/// diffusion : elle passe par l'entree standard de l'exe, jamais par sa ligne de commande ni par le journal. Le meme
/// exe fabrique la version YouTube d'un film a la taille d'origine (<see cref="FabriquerLaVersionAsync"/>).
/// </summary>
public static class LanceurDuDirect
{
    public const string Exe = "NelfePlay.LiveStreaming.exe";

    /// <summary>
    /// Apres la fin de la partie, l'exe envoie encore ce qu'il a et s'arrete seul quand le film ne grandit plus (8 s) ;
    /// au-dela de ce delai, on l'arrete.
    /// </summary>
    private static readonly TimeSpan DelaiDeFin = TimeSpan.FromSeconds(25);

    public sealed record Rapport(int Code, string Fin, string? DerniereLigne, TimeSpan Duree);

    /// <summary>L'exe a la racine d'APIExpose, s'il y est (il arrive par l'installeur ou par outils.json).</summary>
    public static string? Chemin(string racine)
    {
        var chemin = Path.Combine(racine, Exe);
        return File.Exists(chemin) ? chemin : null;
    }

    /// <summary>
    /// Les arguments de l'exe : le film, le sens du jeu, suivre le film, l'annonce jouee avant le jeu et le fichier du
    /// classement montre a la fin s'il existe alors. Jamais l'adresse. Pur.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string film, int rotation, string? annonce = null, string? classement = null,
        IReadOnlyList<string>? options = null, double? forme = null, string? fond = null, bool crt = false)
    {
        var arguments = new List<string> { "--film", film, "--adresse-stdin", "--rotation", rotation.ToString(CultureInfo.InvariantCulture), "--suivre" };
        if (!string.IsNullOrEmpty(annonce)) arguments.AddRange(["--annonce", annonce]);
        if (!string.IsNullOrEmpty(classement)) arguments.AddRange(["--classement", classement]);
        // Le direct partage (16 octobre 2026) : debit, fichier de commande, clip et son empreinte, bande sous le jeu.
        if (options is not null) arguments.AddRange(options);
        arguments.AddRange(Forme(forme));
        arguments.AddRange(Fond(fond));
        arguments.AddRange(Crt(crt));
        return arguments;
    }

    /// <summary>
    /// Le fond du cadre 16:9 (28 octobre 2026, exe 0.6.0) : le fanart du jeu, couvrant, flou et assombri derriere lui. Un exe
    /// plus ancien ignore l'option. Pur.
    /// </summary>
    internal static IEnumerable<string> Fond(string? fond)
        => string.IsNullOrWhiteSpace(fond) ? [] : ["--fond", fond];

    /// <summary>
    /// La forme de l'image annoncee par le coeur (25 octobre 2026) : l'exe (0.5.8) rend a cette forme un film aux pixels
    /// non carres, au lieu de l'etirer. Pur.
    /// </summary>
    internal static IEnumerable<string> Forme(double? forme)
        => forme is double f and > 0.2 and < 5 ? ["--forme", f.ToString("0.####", CultureInfo.InvariantCulture)] : [];

    /// <summary>
    /// L'EFFET CRT (demande du user, 29/10) : de legeres lignes de balayage sur un jeu ne sur un ecran a tube. Un exe plus
    /// ancien ignore l'option. Pur.
    /// </summary>
    internal static IEnumerable<string> Crt(bool crt) => crt ? ["--crt"] : [];

    /// <summary>
    /// LE CRT PHOTOGRAPHIQUE (NelfeCRT, exe 0.8.0, demande du user du 04/11) : diffusion, fusion, saturation et lignes de
    /// balayage, pour les films des records et des replays, fabriques hors partie. Pas sur le direct : le filtre tourne sur le
    /// processeur (2,2 coeurs mesures sur la borne contre 1,3 pour le CRT leger) tant qu'il n'a pas son chemin Direct3D. Un exe
    /// plus ancien ignore l'option. Pur.
    /// </summary>
    internal static IEnumerable<string> CrtPhotographique(bool crt) => crt ? ["--crt-photographic"] : [];

    /// <summary>
    /// LES CARTES D'UNE RENCONTRE (decision D-M4, lot M4 du 08/11, exe 0.7.2) : l'ecran de lancement VS avant le jeu et l'ecran
    /// de fin apres, chacun avec sa duree (le nombre suit le chemin). Une carte sans chemin ou sans duree ne part pas. Pur.
    /// </summary>
    internal static IEnumerable<string> Cartes(string? avant, double avantS, string? apres, double apresS)
    {
        static string S(double s) => s.ToString("0.##", CultureInfo.InvariantCulture);
        var a = new List<string>();
        if (!string.IsNullOrWhiteSpace(avant) && avantS > 0) a.AddRange(["--carte-avant", avant, S(avantS)]);
        if (!string.IsNullOrWhiteSpace(apres) && apresS > 0) a.AddRange(["--carte-apres", apres, S(apresS)]);
        return a;
    }

    /// <summary>La premiere version de l'exe qui pose les cartes et laisse finir le film (0.7.2, 08/11).</summary>
    internal static readonly Version VersionDesCartes = new(0, 7, 2);

    /// <summary>
    /// L'exe pose-t-il les cartes ? Un exe plus ancien les ignore en silence : la version YouTube serait plus courte de 9 s que
    /// ce que la borne annonce au site.
    /// </summary>
    internal static bool SaitLesCartes(string? exe) => VersionDe(exe) is { } v && v >= VersionDesCartes;

    private static Version? VersionDe(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Les systemes nes sur un ecran plat (consoles portables, machines HD) : pas d'effet CRT.</summary>
    private static readonly HashSet<string> EcransPlats = new(StringComparer.OrdinalIgnoreCase)
    {
        "gb", "gbc", "gba", "gb2players", "gbc2players", "gba2players", "gbh", "gamegear", "lynx", "ngp", "ngpc", "wswan", "wswanc",
        "pokemini", "virtualboy", "nds", "3ds", "psp", "psvita", "gameandwatch", "gamecom", "supervision", "megaduck", "arduboy",
        "gp32", "gmaster", "switch", "wiiu", "ps3", "ps4", "xbox360", "windows", "steam",
    };

    /// <summary>Le jeu de ce systeme se jouait-il sur un ecran a tube ? Faux pour un systeme inconnu. Pur.</summary>
    public static bool EcranATube(string? systeme)
        => !string.IsNullOrWhiteSpace(systeme) && !EcransPlats.Contains(systeme.Trim());

    /// <summary>
    /// Les arguments de la version YouTube : le film, le fichier a ecrire, le sens du jeu, et la sequence du record
    /// (secondes du film) quand elle est connue. Pur.
    /// </summary>
    internal static IReadOnlyList<string> ArgumentsDeLaVersion(string film, string sortie, int rotation, double? debutS = null,
        double? finS = null, double? forme = null, string? fond = null, bool crt = false, string? carteAvant = null,
        double carteAvantS = 0, string? carteApres = null, double carteApresS = 0)
    {
        var arguments = new List<string>
        {
            "--film", film, "--sortie-flv", sortie, "--depuis-le-debut", "--rotation", rotation.ToString(CultureInfo.InvariantCulture),
        };
        if (debutS is double debut) arguments.AddRange(["--debut", debut.ToString("0.##", CultureInfo.InvariantCulture)]);
        if (finS is double fin) arguments.AddRange(["--fin", fin.ToString("0.##", CultureInfo.InvariantCulture)]);
        arguments.AddRange(Forme(forme));
        arguments.AddRange(Fond(fond));
        // Le CRT leger, comme le direct (04/11) : le CRT photographique sur processeur rendait les films 4 fois plus lents
        // sur la borne (0,72x le temps reel contre 2,9x), et l'exe publie (0.7.1) ignore l'option. Il reviendra avec son
        // chemin Direct3D, au cout du CRT leger.
        arguments.AddRange(Crt(crt));
        // Le film d'une rencontre (lot M4) : 3 s d'ecran VS, la portion, puis 6 s d'ecran de fin.
        arguments.AddRange(Cartes(carteAvant, carteAvantS, carteApres, carteApresS));
        return arguments;
    }

    /// <summary>
    /// LA VERSION YOUTUBE D'UN FILM A LA TAILLE D'ORIGINE (decision du 15 octobre 2026) : l'exe le redresse, l'agrandit
    /// sans lissage jusqu'a 720 lignes au moins et l'ecrit en FLV, aussi vite que la borne le peut (3 a 4 fois le temps
    /// reel avec la puce Intel). Hors partie seulement : <paramref name="ct"/> arrete l'exe (une partie commence), le
    /// fichier inacheve s'efface et l'annulation remonte. Code 0 : le fichier est complet.
    /// </summary>
    public static async Task<Rapport> FabriquerLaVersionAsync(string exe, string film, string sortie, int rotation, ILogger journal,
        CancellationToken ct, double? debutS = null, double? finS = null, double? forme = null, string? fond = null, bool crt = false,
        string? carteAvant = null, double carteAvantS = 0, string? carteApres = null, double carteApresS = 0)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var a in ArgumentsDeLaVersion(film, sortie, rotation, debutS, finS, forme, fond, crt, carteAvant, carteAvantS, carteApres,
                     carteApresS))
            info.ArgumentList.Add(a);

        var chrono = Stopwatch.StartNew();
        using var p = Process.Start(info) ?? throw new InvalidOperationException("l'exe du direct ne demarre pas");
        string? derniere = null;
        p.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            derniere = e.Data;
            journal.LogDebug("Version YouTube (exe) : {Ligne}", e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) journal.LogWarning("Version YouTube (exe) : {Ligne}", e.Data);
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            // La sortie lue en tache de fond peut arriver apres la fin du processus : on l'attend, pour que la derniere
            // ligne soit bien la derniere.
            p.WaitForExit();
            return new Rapport(p.ExitCode, "fin de l'exe", derniere, chrono.Elapsed);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* deja fini */ }
            try { p.WaitForExit(10_000); } catch (Exception) { /* le fichier s'efface au prochain essai */ }
            try { File.Delete(sortie); } catch (Exception) { /* idem */ }
            throw;
        }
    }

    /// <summary>Une ligne de l'exe pour le journal, l'adresse masquee si elle s'y trouvait. Pur.</summary>
    internal static string Masquer(string ligne, string adresse)
        => adresse.Length > 0 ? ligne.Replace(adresse, "<adresse>", StringComparison.Ordinal) : ligne;

    /// <summary>
    /// Lance l'exe et attend sa fin. <paramref name="ct"/> signale la fin de la partie : l'exe a alors
    /// <see cref="DelaiDeFin"/> pour finir d'envoyer avant d'etre arrete.
    /// </summary>
    public static async Task<Rapport> DiffuserAsync(string exe, string film, string adresse, int rotation, ILogger journal,
        CancellationToken ct, string? annonce = null, string? classement = null, IReadOnlyList<string>? options = null,
        TimeSpan? delaiDeFin = null, double? forme = null, string? fond = null, bool crt = false)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var a in Arguments(film, rotation, annonce, classement, options, forme, fond, crt)) info.ArgumentList.Add(a);

        var chrono = Stopwatch.StartNew();
        using var p = Process.Start(info) ?? throw new InvalidOperationException("l'exe du direct ne demarre pas");
        string? derniere = null;
        p.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            derniere = Masquer(e.Data, adresse);
            journal.LogInformation("Direct (exe) : {Ligne}", derniere);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) journal.LogWarning("Direct (exe) : {Ligne}", Masquer(e.Data, adresse));
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.StandardInput.WriteLineAsync(adresse).ConfigureAwait(false);
        p.StandardInput.Close();

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            // La sortie lue en tache de fond peut arriver apres la fin du processus : on l'attend, pour que la derniere
            // ligne soit bien la derniere.
            p.WaitForExit();
            return new Rapport(p.ExitCode, "fin de l'exe", derniere, chrono.Elapsed);
        }
        catch (OperationCanceledException)
        {
            using var delai = new CancellationTokenSource(delaiDeFin ?? DelaiDeFin);
            try
            {
                await p.WaitForExitAsync(delai.Token).ConfigureAwait(false);
                return new Rapport(p.ExitCode, "partie finie", derniere, chrono.Elapsed);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { /* deja fini */ }
                return new Rapport(-1, "arrete apres la partie", derniere, chrono.Elapsed);
            }
        }
    }
}
