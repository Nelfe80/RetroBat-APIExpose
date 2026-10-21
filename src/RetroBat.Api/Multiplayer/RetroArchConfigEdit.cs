using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Poser des reglages dans un fichier de RetroArch (<c>cle = "valeur"</c>, une par ligne) le temps d'une
/// partie MULTIPLAYER, puis rendre ce qui y etait. Le lanceur de RetroBat relit et reecrit ces fichiers
/// a chaque lancement en gardant les cles qu'il ne gere pas : c'est ce qui permet a la place netplay et
/// aux options d'un coeur qu'il ne connait pas (mGBA Splitscreen) d'arriver jusqu'a RetroArch.
///
/// Ce qui etait la avant est note sur disque : si l'API s'arrete pendant la partie, le demarrage suivant
/// le rend. Une cle absente avant est retiree apres.
/// </summary>
public static partial class RetroArchConfigEdit
{
    /// <summary>
    /// Pose <paramref name="reglages"/> et rend ce qu'il y avait (null = la cle n'existait pas). Rien n'est
    /// ecrit quand tout y est deja.
    /// </summary>
    public static Dictionary<string, string?> Poser(string fichier, IReadOnlyDictionary<string, string> reglages)
    {
        var lignes = File.Exists(fichier) ? File.ReadAllLines(fichier).ToList() : [];
        var avant = new Dictionary<string, string?>(StringComparer.Ordinal);
        var change = false;
        foreach (var (cle, valeur) in reglages)
        {
            var index = Index(lignes, cle);
            avant[cle] = index >= 0 ? Valeur(lignes[index]) : null;
            var ligne = $"{cle} = \"{valeur}\"";
            if (index >= 0)
            {
                if (lignes[index] != ligne)
                {
                    lignes[index] = ligne;
                    change = true;
                }
            }
            else
            {
                lignes.Add(ligne);
                change = true;
            }
        }

        if (change)
        {
            Ecrire(fichier, lignes);
        }

        return avant;
    }

    /// <summary>Rend les valeurs d'avant ; une cle qui n'existait pas s'en va.</summary>
    public static void Rendre(string fichier, IReadOnlyDictionary<string, string?> avant)
    {
        if (!File.Exists(fichier) || avant.Count == 0)
        {
            return;
        }

        var lignes = File.ReadAllLines(fichier).ToList();
        var change = false;
        foreach (var (cle, valeur) in avant)
        {
            var index = Index(lignes, cle);
            if (valeur is null)
            {
                if (index >= 0)
                {
                    lignes.RemoveAt(index);
                    change = true;
                }

                continue;
            }

            var ligne = $"{cle} = \"{valeur}\"";
            if (index < 0)
            {
                lignes.Add(ligne);
                change = true;
            }
            else if (lignes[index] != ligne)
            {
                lignes[index] = ligne;
                change = true;
            }
        }

        if (change)
        {
            Ecrire(fichier, lignes);
        }
    }

    /// <summary>La valeur d'une cle, ou null.</summary>
    public static string? Lire(string fichier, string cle)
    {
        if (!File.Exists(fichier))
        {
            return null;
        }

        var lignes = File.ReadAllLines(fichier).ToList();
        var index = Index(lignes, cle);
        return index >= 0 ? Valeur(lignes[index]) : null;
    }

    /// <summary>Ce qu'il faudra rendre, note a cote : fichier -> (cle -> valeur d'avant).</summary>
    public static void Noter(string journal, IReadOnlyDictionary<string, Dictionary<string, string?>> aRendre)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, JsonSerializer.Serialize(aRendre), new UTF8Encoding(false));
    }

    /// <summary>Rend ce qu'un journal laisse par une partie interrompue, et l'efface. Rend le nombre de fichiers rendus.</summary>
    public static int RendreLeJournal(string journal)
    {
        if (!File.Exists(journal))
        {
            return 0;
        }

        var rendus = 0;
        try
        {
            var aRendre = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(File.ReadAllText(journal)) ?? [];
            foreach (var (fichier, avant) in aRendre)
            {
                Rendre(fichier, avant);
                rendus++;
            }
        }
        catch (JsonException)
        {
            // Un journal illisible ne se rejoue pas : on l'efface quand meme.
        }

        File.Delete(journal);
        return rendus;
    }

    private static int Index(List<string> lignes, string cle)
    {
        for (var i = 0; i < lignes.Count; i++)
        {
            var ligne = lignes[i].TrimStart();
            if (ligne.StartsWith(cle, StringComparison.Ordinal) && ligne.Length > cle.Length
                && (ligne[cle.Length] == ' ' || ligne[cle.Length] == '='))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Valeur(string ligne)
    {
        var m = ValeurRegex().Match(ligne);
        return m.Success ? m.Groups[1].Value : ligne[(ligne.IndexOf('=') + 1)..].Trim();
    }

    private static void Ecrire(string fichier, List<string> lignes)
    {
        var temporaire = fichier + ".nelfeplay.tmp";
        File.WriteAllText(temporaire, string.Join("\n", lignes) + "\n", new UTF8Encoding(false));
        File.Move(temporaire, fichier, overwrite: true);
    }

    [GeneratedRegex("^[^=]*=\\s*\"(.*)\"\\s*$")]
    private static partial Regex ValeurRegex();
}
