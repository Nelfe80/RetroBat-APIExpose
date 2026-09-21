using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Ce que la borne possede REELLEMENT, vu du scoring : les jeux presents sur le disque, avec
/// leur identite canonique et la definition <c>.MEM</c> qui va avec.
///
/// La presence ne depend pas d'une gamelist : quand un systeme n'en a pas encore, les fichiers
/// du dossier font foi. Un chemin inscrit dans une gamelist mais absent du disque, lui, ne
/// compte pas : une collection qui pointe un fichier disparu donne une tuile qui ne se lance
/// pas.
///
/// Rien de ce qui est lu ici ne sort de la machine.
/// </summary>
public sealed class InstalledGameCatalog
{
    private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".chd", ".cue", ".iso", ".bin", ".rom", ".nes", ".sfc", ".smc", ".md", ".gen",
        ".gb", ".gbc", ".gba", ".n64", ".z64", ".v64", ".nds", ".pce", ".sms", ".gg", ".a26", ".col",
        ".int", ".lnx", ".ws", ".wsc", ".ngp", ".ngc", ".vec", ".d64", ".tap", ".adf", ".dsk", ".st",
        ".m3u", ".cdi", ".gdi", ".pbp", ".rvz", ".wbfs", ".gcm", ".nsp", ".xci", ".32x", ".sc", ".sg",
    };

    private readonly IScoreSlugResolver _canonical;
    private readonly ILogger<InstalledGameCatalog>? _logger;
    private readonly string _romsRoot;
    private readonly string _ramRoot;

    public InstalledGameCatalog(
        IScoreSlugResolver canonical,
        ILogger<InstalledGameCatalog>? logger = null,
        string? romsRoot = null,
        string? ramRoot = null)
    {
        _canonical = canonical;
        _logger = logger;
        _romsRoot = romsRoot ?? RetroBatPaths.RomsRoot;
        _ramRoot = ramRoot ?? RetroBatPaths.RamResourcesRoot;
    }

    /// <summary>
    /// Les jeux installes des systemes demandes (identifiants CANONIQUES de scoring, par
    /// exemple <c>arcade</c> ou <c>megadrive</c>). Limiter le parcours a ce que le serveur
    /// annonce evite de scanner cent systemes pour en retenir trois.
    /// Sans systeme demande, tout ce qui est present est parcouru.
    /// </summary>
    public IReadOnlyList<InstalledGame> Enumerate(IEnumerable<string>? canonicalSystemIds = null)
    {
        if (!Directory.Exists(_romsRoot))
        {
            return [];
        }

        var voulus = canonicalSystemIds?
            .SelectMany(RomCanonicalResolver.FrontendSystemsFor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var jeux = new List<InstalledGame>();
        foreach (var dossier in Directory
                     .EnumerateDirectories(_romsRoot)
                     .OrderBy(chemin => chemin, StringComparer.OrdinalIgnoreCase))
        {
            var frontend = Path.GetFileName(dossier);
            if (string.IsNullOrWhiteSpace(frontend) || frontend.StartsWith('.'))
            {
                continue;
            }

            if (voulus != null && !voulus.Contains(frontend))
            {
                continue;
            }

            try
            {
                jeux.AddRange(EnumerateSystem(frontend, dossier));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Systeme illisible pendant l'inventaire : {Systeme}", frontend);
            }
        }

        return jeux;
    }

    private IEnumerable<InstalledGame> EnumerateSystem(string frontendSystemId, string systemRoot)
    {
        var canonique = RomCanonicalResolver.CanonicalScoringSystem(frontendSystemId);
        var gamelist = Path.Combine(systemRoot, "gamelist.xml");
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jeux = new List<InstalledGame>();

        if (File.Exists(gamelist))
        {
            foreach (var noeud in ReadGamelist(gamelist))
            {
                var brut = noeud.Element("path")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(brut))
                {
                    continue;
                }

                var chemin = ResolveAbsolutePath(systemRoot, brut);
                if (chemin.Length == 0 || !Exists(chemin) || !vus.Add(chemin))
                {
                    continue;
                }

                jeux.Add(Build(
                    frontendSystemId,
                    canonique,
                    chemin,
                    noeud.Element("name")?.Value?.Trim(),
                    Text(noeud, "md5"),
                    Text(noeud, "cheevosHash") ?? Text(noeud, "hash"),
                    fromGamelist: true));
            }
        }

        // Aussi les fichiers que la gamelist ne connait pas encore : un jeu vient parfois
        // d'etre copie et la gamelist ne sera regeneree qu'apres.
        foreach (var chemin in EnumerateRomFiles(systemRoot))
        {
            if (vus.Add(chemin))
            {
                jeux.Add(Build(frontendSystemId, canonique, chemin, null, null, null, fromGamelist: false));
            }
        }

        return jeux;
    }

    private InstalledGame Build(
        string frontendSystemId,
        string canonicalSystemId,
        string absolutePath,
        string? displayName,
        string? md5,
        string? cheevosHash,
        bool fromGamelist)
    {
        var fichier = Path.GetFileName(absolutePath);
        var groupe = _canonical.ResolveScoreSlug(frontendSystemId, fichier, md5, cheevosHash);
        return new InstalledGame(
            frontendSystemId,
            canonicalSystemId,
            groupe ?? string.Empty,
            absolutePath,
            string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(absolutePath) : displayName.Trim(),
            Normalize(md5),
            Normalize(cheevosHash),
            fromGamelist,
            groupe is { Length: > 0 } ? OfficialDefinitionPath(canonicalSystemId, groupe) : null);
    }

    /// <summary>
    /// La definition OFFICIELLE du jeu, jamais la perso ni celle d'un concours : c'est elle
    /// que le profil de scoring homologue, et donc elle seule qui peut porter l'empreinte
    /// attendue.
    /// </summary>
    public string OfficialDefinitionPath(string canonicalSystemId, string romGroup)
        => Path.Combine(_ramRoot, canonicalSystemId, romGroup + ".MEM");

    /// <summary>
    /// L'empreinte de la definition officielle, ou une chaine vide si elle manque. Le profil
    /// publie la sienne : sans egalite, un score serait refuse a la fin de la partie.
    /// </summary>
    public string OfficialDefinitionSha256(string canonicalSystemId, string romGroup)
    {
        var chemin = OfficialDefinitionPath(canonicalSystemId, romGroup);
        try
        {
            if (!File.Exists(chemin))
            {
                return string.Empty;
            }

            using var flux = File.Open(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(flux)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Definition .MEM illisible : {Chemin}", chemin);
            return string.Empty;
        }
    }

    /// <summary>
    /// Le groupe de scoring d'UN fichier installe, resolu comme la collection le resout (md5
    /// et hash de la gamelist quand elle le connait, alias du .MEM, nom). Null quand aucune
    /// definition ne le connait. Sert au replay : le manifeste emporte l'identite du JEU, pas
    /// seulement l'empreinte d'un fichier, pour qu'une autre borne retrouve son propre dump.
    /// </summary>
    public string? RomGroupOf(string frontendSystemId, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(frontendSystemId) || string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        string? md5 = null, cheevos = null;
        // La gamelist vit a la racine du systeme : celle du dossier du fichier d'abord (ROM
        // rangee a la racine), puis celle du dossier roms standard (ROM dans un sous-dossier).
        foreach (var racine in new[] { Path.GetDirectoryName(absolutePath), Path.Combine(_romsRoot, frontendSystemId) })
        {
            if (string.IsNullOrEmpty(racine))
            {
                continue;
            }

            var gamelist = Path.Combine(racine, "gamelist.xml");
            if (!File.Exists(gamelist))
            {
                continue;
            }

            foreach (var noeud in ReadGamelist(gamelist))
            {
                var brut = noeud.Element("path")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(brut))
                {
                    continue;
                }

                if (string.Equals(ResolveAbsolutePath(racine, brut), absolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    md5 = Text(noeud, "md5");
                    cheevos = Text(noeud, "cheevosHash") ?? Text(noeud, "hash");
                    break;
                }
            }

            if (md5 is not null || cheevos is not null)
            {
                break;
            }
        }

        return _canonical.ResolveScoreSlug(frontendSystemId, Path.GetFileName(absolutePath), md5, cheevos);
    }

    /// <summary>
    /// Les dumps installes d'un groupe, tous dossiers frontend du systeme canonique confondus
    /// (un jeu d'arcade vit sous mame comme sous fbneo). Vide quand la borne n'a pas ce jeu.
    /// </summary>
    public IReadOnlyList<InstalledGame> DumpsOf(string canonicalSystemId, string romGroup)
    {
        if (string.IsNullOrWhiteSpace(canonicalSystemId) || string.IsNullOrWhiteSpace(romGroup))
        {
            return [];
        }

        return Enumerate([canonicalSystemId])
            .Where(jeu => string.Equals(jeu.RomGroup, romGroup.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IEnumerable<XElement> ReadGamelist(string path)
    {
        try
        {
            using var flux = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return XDocument.Load(flux).Root?.Elements("game").ToList() ?? [];
        }
        catch (Exception)
        {
            // Gamelist en cours d'ecriture : les fichiers du dossier prennent le relais.
            return [];
        }
    }

    private IEnumerable<string> EnumerateRomFiles(string systemRoot)
    {
        IEnumerable<string> fichiers;
        try
        {
            fichiers = Directory.EnumerateFiles(systemRoot, "*.*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Dossier roms illisible : {Dossier}", systemRoot);
            yield break;
        }

        foreach (var fichier in fichiers.OrderBy(chemin => chemin, StringComparer.OrdinalIgnoreCase))
        {
            if (RomExtensions.Contains(Path.GetExtension(fichier)))
            {
                yield return fichier;
            }
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string ResolveAbsolutePath(string systemRoot, string rawPath)
    {
        var propre = rawPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (propre.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            propre = propre[2..];
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(propre) ? propre : Path.Combine(systemRoot, propre));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static string? Text(XElement node, string name)
    {
        var valeur = node.Element(name)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(valeur) ? null : valeur;
    }

    private static string? Normalize(string? hash)
    {
        var propre = (hash ?? string.Empty).Trim().ToLowerInvariant();
        return propre.Length == 0 ? null : propre;
    }
}

/// <summary>
/// Le service qui sait dire quelle definition de score correspond a un dump.
/// <see cref="RomCanonicalResolver"/> l'implemente ; l'interface existe pour que l'inventaire
/// se teste sans referentiel sur disque.
/// </summary>
public interface IScoreSlugResolver
{
    string? ResolveScoreSlug(string systemId, string? romFileName, string? md5, string? cheevosHash);
}

/// <summary>
/// Un jeu present sur la machine. <see cref="RomGroup"/> vide signifie « aucune definition de
/// score locale » : le jeu existe, mais rien ne peut y etre mesure.
/// </summary>
public sealed record InstalledGame(
    string FrontendSystemId,
    string CanonicalSystemId,
    string RomGroup,
    string AbsolutePath,
    string DisplayName,
    string? Md5,
    string? CheevosHash,
    bool FromGamelist,
    string? OfficialDefinitionPath)
{
    public bool ScorableLocal => RomGroup.Length > 0;
}
