using System.Diagnostics;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE DIRECT EST LA COPIE DU FILM (CDC video V2, §5.8, proposition validee le 14 octobre 2026). RetroArch ne sort qu'un
/// flux : il ne peut pas filmer et diffuser a la fois. Il filme donc la partie comme d'habitude, et le relais envoie a
/// YouTube Live, au rythme du jeu, chaque grappe du film des qu'elle est ecrite : une seule compression, celle du film,
/// et seulement du debit montant en plus. Le direct part de la grappe suivante (une image cle en tete) et s'arrete
/// quand le film ne grandit plus.
/// </summary>
public static class RelaisDuFilm
{
    /// <summary>Le decalage entre decodage et affichage des images, plus large que celui des images B de x264 (35 ms).</summary>
    public const double RetardB = 100;

    /// <summary>L'avance prise sur la premiere grappe : la suivante arrive d'un bloc, juste a son heure.</summary>
    private static readonly TimeSpan Avance = TimeSpan.FromSeconds(2);

    /// <summary>Un film qui ne grandit plus depuis ce delai est fini.</summary>
    private static readonly TimeSpan FilmArrete = TimeSpan.FromSeconds(8);

    public sealed record Rapport(int Images, int BlocsDeSon, long Octets, double Secondes, string Fin);

    /// <summary>Une etiquette a envoyer : image ou son, instants de decodage et d'affichage (ms).</summary>
    internal sealed record Etiquette(bool Video, double Dts, double Pts, bool Cle, byte[] Donnees);

    /// <summary>
    /// Les etiquettes d'une grappe, dans l'ordre d'envoi. Les images arrivent dans l'ordre de decodage avec leur instant
    /// d'affichage (images B) : l'instant de decodage de la n-ieme est le n-ieme instant d'affichage de la grappe, moins
    /// <see cref="RetardB"/>. Une grappe commence par une image cle (groupe d'images ferme) : elle se suffit. Pur.
    /// </summary>
    internal static List<Etiquette> Etiquettes(IReadOnlyList<FilmMkv.Bloc> grappe, int pisteVideo)
    {
        var images = grappe.Where(b => b.Piste == pisteVideo).ToList();
        var tries = images.Select(b => b.TempsMs).OrderBy(t => t).ToList();
        var sortie = new List<Etiquette>(grappe.Count);
        for (var i = 0; i < images.Count; i++)
            sortie.Add(new Etiquette(true, tries[i] - RetardB, images[i].TempsMs, images[i].Cle, images[i].Donnees));
        sortie.AddRange(grappe.Where(b => b.Piste != pisteVideo).Select(b => new Etiquette(false, b.TempsMs, b.TempsMs, true, b.Donnees)));
        // A instant egal, le son passe avant l'image (comme ffmpeg).
        return sortie.OrderBy(e => e.Dts).ThenBy(e => e.Video ? 1 : 0).ToList();
    }

    /// <summary>
    /// Recopie le film vers l'adresse RTMP jusqu'a ce qu'il ne grandisse plus, ou que <paramref name="ct"/> l'arrete.
    /// L'adresse porte la cle de diffusion : elle ne se journalise pas. <paramref name="depuisLeDebut"/> (essais) :
    /// le film entier, au rythme du jeu, au lieu de la grappe suivante.
    /// </summary>
    public static async Task<Rapport> RecopierAsync(string film, string adresse, ILogger journal, CancellationToken ct,
        bool depuisLeDebut = false)
    {
        var mkv = new FilmMkv(film);
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!mkv.LireEntete())
        {
            if (DateTime.UtcNow > limite) return new Rapport(0, 0, 0, 0, "en-tete du film illisible");
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        if (mkv.Video is not { } video) return new Rapport(0, 0, 0, 0, "film sans image");
        var son = mkv.Son;
        if (!depuisLeDebut) mkv.AllerALaFin();

        await using var rtmp = new EmetteurRtmp(adresse);
        await rtmp.OuvrirAsync(ct).ConfigureAwait(false);
        await rtmp.MetadonneesAsync(video.Largeur, video.Hauteur, 60, son?.Frequence ?? 48000, ct).ConfigureAwait(false);
        await rtmp.EnteteVideoAsync(video.Prive, ct).ConfigureAwait(false);
        if (son is not null) await rtmp.EnteteAudioAsync(son.Prive, ct).ConfigureAwait(false);
        journal.LogInformation("Direct : publication acceptee, {Largeur}x{Hauteur}.", video.Largeur, video.Hauteur);

        var chrono = Stopwatch.StartNew();
        double? debutFilm = null;
        var debutMur = TimeSpan.Zero;
        var derniereCroissance = chrono.Elapsed;
        long tailleVue = -1;
        int images = 0, blocs = 0;
        var fin = "arrete";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (rtmp.Erreur is { } erreur)
                {
                    fin = "connexion perdue : " + erreur;
                    break;
                }
                List<List<FilmMkv.Bloc>> grappes;
                try
                {
                    grappes = mkv.NouvellesGrappes();
                }
                catch (IOException)
                {
                    // Le film a ete range (fin de la capture) : il n'y a plus rien a recopier.
                    fin = "film range";
                    break;
                }
                if (grappes.Count == 0)
                {
                    var taille = TailleDe(film);
                    if (taille != tailleVue)
                    {
                        tailleVue = taille;
                        derniereCroissance = chrono.Elapsed;
                    }
                    else if (chrono.Elapsed - derniereCroissance > FilmArrete)
                    {
                        fin = "le film ne grandit plus";
                        break;
                    }
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }
                derniereCroissance = chrono.Elapsed;
                foreach (var grappe in grappes)
                {
                    foreach (var e in Etiquettes(grappe, video.Numero))
                    {
                        if (debutFilm is null)
                        {
                            debutFilm = e.Dts;
                            debutMur = chrono.Elapsed + Avance;
                        }
                        var attente = debutMur + TimeSpan.FromMilliseconds(e.Dts - debutFilm.Value) - chrono.Elapsed;
                        if (attente > TimeSpan.Zero) await Task.Delay(attente, ct).ConfigureAwait(false);
                        var temps = (uint)Math.Max(0, Math.Round(e.Dts - debutFilm.Value));
                        if (e.Video)
                        {
                            await rtmp.VideoAsync(temps, (int)Math.Round(e.Pts - e.Dts), e.Cle, e.Donnees, ct).ConfigureAwait(false);
                            images++;
                        }
                        else
                        {
                            await rtmp.AudioAsync(temps, e.Donnees, ct).ConfigureAwait(false);
                            blocs++;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            fin = "partie finie";
        }
        return new Rapport(images, blocs, rtmp.OctetsEnvoyes, chrono.Elapsed.TotalSeconds, fin);
    }

    private static long TailleDe(string chemin)
    {
        try { return new FileInfo(chemin).Length; }
        catch (Exception) { return -1; }
    }
}
