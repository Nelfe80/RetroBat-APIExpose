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

    /// <summary>
    /// LA COUCHE DES CHALLENGES (decision du user du 03/11 : les challenges ne sont pas certifies) : un challenge peut porter
    /// son propre .MEM (le coup recu de Metal Slug 3 facilite), sous <c>ram\.defis\&lt;systeme&gt;\&lt;jeu&gt;.MEM</c>. Le
    /// wrapper ne le charge que pour un lancement de challenge (<c>mem_layer=defis</c> dans certified.txt, wrapper 0.344) :
    /// le World Scoring garde son .MEM officiel. Null sans fichier.
    /// </summary>
    public static string? DefinitionDeChallenge(string systemId, string rom)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom)) return null;
        var chemin = Path.Combine(RetroBatPaths.RamResourcesRoot, ".defis", systemId, rom + ".MEM");
        try { return File.Exists(chemin) ? chemin : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ── Les versions du .MEM des challenges (Metal Slug 3, 15/11, decision du user « voie B ») ──────────────────────────
    //
    // Un reglement cite son .MEM par empreinte. Un nouveau reglement peut demander une ligne de plus (la mort du boss de
    // Metal Slug 3) sans que les autres reglements du jeu, qui citent l'ancien, changent de version. Le Data Pack garde le
    // fichier en vigueur sous son nom (ram\.defis\<systeme>\<jeu>.MEM, le seul que le wrapper ouvre : « jamais un chemin »)
    // et chaque autre version A COTE, sous ram\.defis\<systeme>\<jeu>.<8 premiers hex de l'empreinte>.MEM. Au lancement,
    // l'API pose a la place du fichier en vigueur la version que le reglement cite, l'ancienne gardee sous sa propre version ;
    // la synchronisation du Data Pack remettra plus tard le fichier du depot, le lancement suivant reposera le bon.

    /// <summary>Le fichier d'une version du .MEM des challenges, a cote du fichier en vigueur. Pur.</summary>
    public static string CheminDeLaVersion(string racineRam, string systemId, string rom, string sha256)
        => Path.Combine(racineRam, ".defis", systemId, rom + "." + sha256[..8].ToLowerInvariant() + ".MEM");

    /// <summary>
    /// Le .MEM des challenges dont l'empreinte est <paramref name="sha256"/> : le fichier en vigueur s'il l'a, sinon sa version
    /// a cote ; null si aucun fichier ne l'a. Ne modifie rien.
    /// </summary>
    public static string? VersionDeChallenge(string racineRam, string systemId, string rom, string sha256)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom) || sha256 is not { Length: 64 }) return null;
        var enVigueur = Path.Combine(racineRam, ".defis", systemId, rom + ".MEM");
        if (EmpreinteDe(enVigueur) is { } e && e.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return enVigueur;
        var version = CheminDeLaVersion(racineRam, systemId, rom, sha256);
        return EmpreinteDe(version) is { } v && v.Equals(sha256, StringComparison.OrdinalIgnoreCase) ? version : null;
    }

    /// <summary>La meme chose sous la racine de la borne.</summary>
    public static string? VersionDeChallenge(string systemId, string rom, string sha256)
        => VersionDeChallenge(RetroBatPaths.RamResourcesRoot, systemId, rom, sha256);

    /// <summary>
    /// POSE AU LANCEMENT la version que le reglement cite a la place du fichier en vigueur (le seul que le wrapper ouvre). Le
    /// contenu en vigueur, s'il est autre, est d'abord garde sous sa propre version (jamais perdu) ; l'ecriture est atomique
    /// (fichier temporaire, puis remplacement) et l'empreinte du fichier pose est relue. Rend null si le fichier en vigueur a
    /// l'empreinte demandee, sinon le motif du refus : alors rien n'est lance, jamais avec un autre .MEM.
    /// </summary>
    public static string? PoserLaVersionDeChallenge(string racineRam, string systemId, string rom, string sha256)
    {
        if (sha256 is not { Length: 64 }) return "empreinte du .MEM illisible";
        var enVigueur = Path.Combine(racineRam, ".defis", systemId, rom + ".MEM");
        var court = sha256[..8].ToLowerInvariant();
        try
        {
            var actuelle = EmpreinteDe(enVigueur);
            if (actuelle is not null && actuelle.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return null;
            var version = CheminDeLaVersion(racineRam, systemId, rom, sha256);
            var octets = File.Exists(version) ? File.ReadAllBytes(version) : null;
            if (octets is null || !Hex(octets).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                return $".MEM {court} absent de ram\\.defis\\{systemId} ({rom}.{court}.MEM) : le Data Pack de la borne est a mettre a jour";

            if (actuelle is not null)
            {
                var garde = CheminDeLaVersion(racineRam, systemId, rom, actuelle);
                if (!File.Exists(garde) || EmpreinteDe(garde) is not { } g || !g.Equals(actuelle, StringComparison.OrdinalIgnoreCase))
                    EcrireAtomique(garde, File.ReadAllBytes(enVigueur));
            }

            EcrireAtomique(enVigueur, octets);
            return EmpreinteDe(enVigueur) is { } posee && posee.Equals(sha256, StringComparison.OrdinalIgnoreCase)
                ? null : $".MEM {court} pose mais relu avec une autre empreinte";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $".MEM {court} impossible a poser : {ex.Message}";
        }
    }

    /// <summary>La meme chose sous la racine de la borne.</summary>
    public static string? PoserLaVersionDeChallenge(string systemId, string rom, string sha256)
        => PoserLaVersionDeChallenge(RetroBatPaths.RamResourcesRoot, systemId, rom, sha256);

    private static string? EmpreinteDe(string chemin)
    {
        try { return File.Exists(chemin) ? Hex(File.ReadAllBytes(chemin)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Hex(byte[] octets) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(octets)).ToLowerInvariant();

    private static void EcrireAtomique(string chemin, byte[] octets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var temporaire = chemin + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(temporaire, octets);
            File.Move(temporaire, chemin, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaire)) File.Delete(temporaire); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
