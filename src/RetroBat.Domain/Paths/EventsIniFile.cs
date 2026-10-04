using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RetroBat.Domain.Paths;

/// <summary>
/// Shared reader for events.ini. The ES hooks write the file at cursor speed;
/// a reader holding the default FileShare.Read blocks those writes and the
/// selection is silently lost. Every read must go through this tolerant open
/// (writers and the atomic rename replace stay allowed while we read); torn
/// content is handled by the watcher's snapshot/completeness guards.
/// </summary>
public static class EventsIniFile
{
    public static string[] ReadAllLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Le jeu choisi d'apres le dernier evenement ecrit par ES, si c'est un game-selected ecrit
    /// depuis <paramref name="depuisUtc"/> (2026-10-04). Le suivi d'ES n'agit qu'au PROCHAIN
    /// evenement : au demarrage de l'API, un joueur reste sur son jeu sans bouger et l'API ne lui
    /// connaissait aucun jeu choisi. Le fichier plus ancien que le demarrage d'ES vient d'une
    /// session precedente : il ne dit rien de l'ecran actuel.
    /// </summary>
    public static bool TryReadGameSelected(string path, DateTime depuisUtc, out string systemId, out string gamePath, out string name)
    {
        systemId = gamePath = name = "";
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < depuisUtc) return false;
            var lines = ReadAllLines(path);
            if (lines.Length < 2 || !string.Equals(lines[0].Trim(), "event=game-selected", StringComparison.OrdinalIgnoreCase)) return false;

            var arguments = new List<string>();
            var courant = new StringBuilder();
            var entreGuillemets = false;
            foreach (var c in lines[1])
            {
                if (c == '"') entreGuillemets = !entreGuillemets;
                else if (char.IsWhiteSpace(c) && !entreGuillemets)
                {
                    if (courant.Length > 0) { arguments.Add(courant.ToString()); courant.Clear(); }
                }
                else courant.Append(c);
            }
            if (courant.Length > 0) arguments.Add(courant.ToString());
            if (arguments.Count < 2 || string.IsNullOrWhiteSpace(arguments[0]) || string.IsNullOrWhiteSpace(arguments[1])) return false;

            systemId = arguments[0];
            gamePath = arguments[1];
            name = arguments.Count > 2 ? arguments[2] : Path.GetFileNameWithoutExtension(gamePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
