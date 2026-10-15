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

    /// <summary>Les arguments de l'exe : le film, le sens du jeu, suivre le film. Jamais l'adresse. Pur.</summary>
    internal static IReadOnlyList<string> Arguments(string film, int rotation)
        => ["--film", film, "--adresse-stdin", "--rotation", rotation.ToString(CultureInfo.InvariantCulture), "--suivre"];

    /// <summary>
    /// Les arguments de la version YouTube : le film, le fichier a ecrire, le sens du jeu, et la sequence du record
    /// (secondes du film) quand elle est connue. Pur.
    /// </summary>
    internal static IReadOnlyList<string> ArgumentsDeLaVersion(string film, string sortie, int rotation, double? debutS = null,
        double? finS = null)
    {
        var arguments = new List<string>
        {
            "--film", film, "--sortie-flv", sortie, "--depuis-le-debut", "--rotation", rotation.ToString(CultureInfo.InvariantCulture),
        };
        if (debutS is double debut) arguments.AddRange(["--debut", debut.ToString("0.##", CultureInfo.InvariantCulture)]);
        if (finS is double fin) arguments.AddRange(["--fin", fin.ToString("0.##", CultureInfo.InvariantCulture)]);
        return arguments;
    }

    /// <summary>
    /// LA VERSION YOUTUBE D'UN FILM A LA TAILLE D'ORIGINE (decision du 15 octobre 2026) : l'exe le redresse, l'agrandit
    /// sans lissage jusqu'a 720 lignes au moins et l'ecrit en FLV, aussi vite que la borne le peut (3 a 4 fois le temps
    /// reel avec la puce Intel). Hors partie seulement : <paramref name="ct"/> arrete l'exe (une partie commence), le
    /// fichier inacheve s'efface et l'annulation remonte. Code 0 : le fichier est complet.
    /// </summary>
    public static async Task<Rapport> FabriquerLaVersionAsync(string exe, string film, string sortie, int rotation, ILogger journal,
        CancellationToken ct, double? debutS = null, double? finS = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var a in ArgumentsDeLaVersion(film, sortie, rotation, debutS, finS)) info.ArgumentList.Add(a);

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
        CancellationToken ct)
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
        foreach (var a in Arguments(film, rotation)) info.ArgumentList.Add(a);

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
            using var delai = new CancellationTokenSource(DelaiDeFin);
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
