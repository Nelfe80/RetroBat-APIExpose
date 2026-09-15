namespace RetroBat.Domain.Paths;

/// <summary>
/// Quel <c>.MEM</c> est REELLEMENT en vigueur pour un jeu : <c>.contest</c> (competition)
/// toujours prioritaire, <c>.user</c> (perso) si le drapeau PERSO du wrapper vaut 1, sinon
/// l'officiel. C'est l'arbitrage du wrapper, et les consommateurs doivent dire la meme chose
/// que lui : l'agregateur cherche la regle du score (encodage BCD, masque) A CETTE ADRESSE
/// dans LE fichier qu'on lui nomme. Quand on lui nommait l'officiel alors qu'une definition
/// perso etait chargee, la regle restait introuvable et un score BCD repartait en binaire
/// (Joust : 13312 publie pour 3400 a l'ecran).
///
/// Le chemin est recalcule a chaque ligne recue du wrapper : tout est donc mis en cache
/// quelques secondes. Une lecture disque par ligne a deja coute cher ici (alias.json).
/// </summary>
public static class RamDefinitionLayers
{
    private static readonly TimeSpan Fraicheur = TimeSpan.FromSeconds(5);
    private static readonly object Verrou = new();
    private static readonly Dictionary<string, (DateTime Vu, string Chemin)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _persoVu = DateTime.MinValue;
    private static bool _perso;

    /// <summary>Le <c>.env</c> du wrapper, ou vivent PERSO, DISCOVERY, PIPE et HZ.</summary>
    public static string WrapperEnvPath => Path.Combine(RetroBatPaths.PluginRoot, "wrapper", ".env");

    /// <summary>Le mode perso est-il actif ? (drapeau PERSO du .env du wrapper)</summary>
    public static bool PersoEnabled()
    {
        lock (Verrou)
        {
            if (DateTime.UtcNow - _persoVu < Fraicheur)
            {
                return _perso;
            }
            _perso = LirePerso();
            _persoVu = DateTime.UtcNow;
            return _perso;
        }
    }

    /// <summary>
    /// Le fichier de definition en vigueur pour ce systeme et cette rom. Rend le chemin
    /// officiel si aucune couche ne s'applique, y compris quand il n'existe pas : c'est au
    /// lecteur de decider ce qu'il fait d'un fichier absent.
    /// </summary>
    public static string PickDefinitionFile(string systemId, string rom)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom))
        {
            return string.Empty;
        }

        var cle = systemId + "/" + rom;
        lock (Verrou)
        {
            if (Cache.TryGetValue(cle, out var vu) && DateTime.UtcNow - vu.Vu < Fraicheur)
            {
                return vu.Chemin;
            }
        }

        var chemin = Resoudre(systemId, rom);
        lock (Verrou)
        {
            Cache[cle] = (DateTime.UtcNow, chemin);
        }
        return chemin;
    }

    private static string Resoudre(string systemId, string rom)
    {
        var racine = RetroBatPaths.RamResourcesRoot;
        var officiel = Path.Combine(racine, systemId, rom + ".MEM");
        try
        {
            var contest = Path.Combine(racine, ".contest", systemId, rom + ".MEM");
            if (File.Exists(contest))
            {
                return contest;
            }

            if (PersoEnabled())
            {
                var perso = Path.Combine(racine, ".user", systemId, rom + ".MEM");
                if (File.Exists(perso))
                {
                    return perso;
                }
            }
        }
        catch (IOException)
        {
            return officiel;
        }
        catch (UnauthorizedAccessException)
        {
            return officiel;
        }

        return officiel;
    }

    private static bool LirePerso()
    {
        try
        {
            var chemin = WrapperEnvPath;
            if (!File.Exists(chemin))
            {
                return false;
            }
            foreach (var ligne in File.ReadAllLines(chemin))
            {
                var t = ligne.Trim();
                if (t.StartsWith('#'))
                {
                    continue;
                }
                var eq = t.IndexOf('=');
                if (eq > 0 && t[..eq].Trim() == "PERSO")
                {
                    return t[(eq + 1)..].Trim() == "1";
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return false;
    }
}
