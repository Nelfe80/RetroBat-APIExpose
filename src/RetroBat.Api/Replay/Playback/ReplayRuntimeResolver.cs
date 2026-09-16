using System.IO.Compression;
using System.Security.Cryptography;
using RetroBat.Api.Replay.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>Core + ROM résolus sur CETTE machine pour lancer un replay.</summary>
public sealed record ResolvedRuntime(string CoreDll, string RomPath, bool ExactCore);

/// <summary>
/// Résout, sur CETTE machine, le core et la ROM d'un replay À PARTIR DU MANIFESTE (R5). But :
/// que le <see cref="ReplayLaunchHint"/> local ne soit qu'un ACCÉLÉRATEUR (chemin rapide) et
/// jamais une CONDITION de lecture — un replay reçu d'un peer (NelfeNet) n'a pas de hint.
///
/// Politique SOUPLE (cf. compat .bsv, décision produit) : le CONTENU de la ROM (crc32, tel que
/// RetroArch le calcule) est le seul repère DUR ; le core est résolu par EMPREINTE (core_sha256
/// du manifeste, R4) quand elle est disponible, sinon best-effort via le hint. On ne bloque
/// JAMAIS a priori sur la version — la vérité finale de compatibilité reste la lecture elle-même
/// (les checkpoints du .bsv signalent un désync). Renvoie null seulement si on ne trouve
/// physiquement ni core ni ROM utilisables ici.
/// </summary>
public sealed class ReplayRuntimeResolver : IReplayRuntimeResolver
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>
    /// Les cores RetroArch usuels de chaque systeme, du plus fidele au plus tolerant. Ils ne
    /// servent qu'en DERNIER recours : un replay recu d'ailleurs porte l'empreinte du core de
    /// la machine qui l'a enregistre, et une simple recompilation de ce core suffit a la faire
    /// echouer. Refuser la lecture pour cela revenait a exiger la machine d'origine.
    ///
    /// Les cles sont NORMALISEES (lettres et chiffres seulement) et couvrent les deux vocabulaires
    /// que portent les manifestes : l'identifiant de systeme (« mega_drive », « fb_alpha ») et le
    /// dossier d'EmulationStation (« megadrive », « fbneo »). Mesure faite sur les manifestes de
    /// la borne : les deux different, et chercher sur un seul ne trouvait rien.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> CoresStandardsParSysteme =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["megadrive"] = new[] { "genesis_plus_gx", "picodrive" },
            ["genesis"] = new[] { "genesis_plus_gx", "picodrive" },
            ["sega32x"] = new[] { "picodrive" },
            ["segacd"] = new[] { "genesis_plus_gx", "picodrive" },
            ["snes"] = new[] { "snes9x", "bsnes", "snes9x2010" },
            ["nes"] = new[] { "fceumm", "nestopia", "mesen" },
            ["mastersystem"] = new[] { "genesis_plus_gx", "gearsystem", "picodrive" },
            ["gamegear"] = new[] { "genesis_plus_gx", "gearsystem" },
            ["gb"] = new[] { "gambatte", "mgba" },
            ["gbc"] = new[] { "gambatte", "mgba" },
            ["gba"] = new[] { "mgba", "vba_next" },
            ["fbneo"] = new[] { "fbneo" },
            ["fbalpha"] = new[] { "fbneo" },
            ["arcade"] = new[] { "fbneo", "mame2003_plus", "mame" },
            ["mame"] = new[] { "mame", "mame2010", "mame2003_plus" },
            ["neogeo"] = new[] { "fbneo", "geolith" },
            ["pcengine"] = new[] { "mednafen_pce_fast", "mednafen_pce" },
            ["psx"] = new[] { "swanstation", "pcsx_rearmed", "mednafen_psx_hw" },
            ["n64"] = new[] { "mupen64plus_next", "parallel_n64" },
        };

    private readonly EsSystemsRomPaths _romPaths;
    private readonly Storage.IReplayManifestStore _manifests;
    private readonly Storage.IReplayMetadataStore _meta;
    private readonly ILogger<ReplayRuntimeResolver> _logger;

    public ReplayRuntimeResolver(EsSystemsRomPaths romPaths, Storage.IReplayManifestStore manifests,
        Storage.IReplayMetadataStore meta, ILogger<ReplayRuntimeResolver> logger)
    {
        _romPaths = romPaths; _manifests = manifests; _meta = meta; _logger = logger;
    }

    public ResolvedRuntime? Resolve(ReplayManifest manifest, ReplayLaunchHint? hint)
    {
        var core = ResolveCore(manifest, hint, out var exact);
        if (core is null)
        {
            _logger.LogWarning("Replay resolver : aucun core utilisable pour {Id} (runtime {Rt}, core_sha256 {Sha})",
                manifest.ReplayId, manifest.Runtime.RuntimeId, Short(manifest.Runtime.CoreSha256));
            return null;
        }
        var rom = ResolveRom(manifest, hint);
        if (rom is null)
        {
            _logger.LogWarning("Replay resolver : ROM introuvable pour {Id} (crc32 {Crc})", manifest.ReplayId, manifest.Game.Crc32);
            return null;
        }
        if (!exact) _logger.LogInformation("Replay resolver : core NON identique à l'enregistrement pour {Id} — lecture best-effort (désync détecté par checkpoints).", manifest.ReplayId);
        MemoriserLancement(manifest, core, rom);
        return new ResolvedRuntime(core, rom, exact);
    }

    // Core : hint local (rapide, en préférant cores_real sans wrapper scoring) → empreinte
    // core_sha256 (scan cores_real puis cores) → null. Jamais de refus sur la version.
    private string? ResolveCore(ReplayManifest manifest, ReplayLaunchHint? hint, out bool exact)
    {
        exact = false;
        if (hint is not null && !string.IsNullOrEmpty(hint.Core))
        {
            var real = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores_real", hint.Core + "_libretro.dll");
            if (File.Exists(real)) { exact = true; return real; }
            if (!string.IsNullOrEmpty(hint.CoreDll) && File.Exists(hint.CoreDll)) { exact = true; return hint.CoreDll; }
        }

        var wanted = manifest.Runtime.CoreSha256;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            foreach (var sub in new[] { "cores_real", "cores" })
            {
                var root = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", sub);
                if (!Directory.Exists(root)) continue;
                foreach (var dll in Directory.EnumerateFiles(root, "*_libretro.dll"))
                {
                    if (string.Equals(HashFileQuiet(dll), wanted, StringComparison.OrdinalIgnoreCase)) { exact = true; return dll; }
                }
            }
        }
        // Repli PERMISSIF : un core deja employe sur CETTE machine pour le meme systeme. Un
        // replay n'est pas une soumission de score : mieux vaut le montrer avec un core proche
        // que de le refuser. Un desync eventuel se verra a l'ecran, et les checkpoints du .bsv
        // le signalent.
        var apprenti = CoreDejaUtilisePour(manifest.Game.SystemId);
        if (apprenti is not null)
        {
            _logger.LogInformation("Replay : core non identifie pour {Id}, repli sur {Core} deja employe ici pour {System}.",
                manifest.ReplayId, Path.GetFileName(apprenti), manifest.Game.SystemId);
            return apprenti;
        }

        // Derniere chance : le core usuel du systeme, s'il est installe ici. Sans cet etage,
        // une borne qui n'a JAMAIS enregistre de replay sur ce systeme n'avait rien a apprendre
        // de ses propres enregistrements, et la lecture etait refusee alors que le core etait
        // la, a cote.
        var standard = CoreStandardPourSysteme(manifest.Game.SystemId, hint?.SystemFolder ?? manifest.Game.SystemFolder);
        if (standard is not null)
        {
            _logger.LogInformation("Replay : core non identifie pour {Id}, repli sur le core usuel {Core} pour {System}.",
                manifest.ReplayId, Path.GetFileName(standard), manifest.Game.SystemId);
            return standard;
        }

        return null;
    }

    /// <summary>Le premier core usuel de ce systeme present sur le disque, cores_real d'abord
    /// (sans le wrapper de scoring, inutile pour une relecture).
    ///
    /// La liste vient d'abord d'EmulationStation, qui declare pour chacun de ses systemes les
    /// cores libretro dans son ordre de preference : c'est la table exhaustive de la machine, et
    /// elle vieillit avec elle. La table interne ne sert que si ES ne dit rien de ce systeme.</summary>
    private string? CoreStandardPourSysteme(string? systemId, string? systemFolder)
    {
        var declares = _romPaths.LibretroCoresFor(systemFolder);
        if (declares.Count == 0) declares = _romPaths.LibretroCoresFor(systemId);
        var candidats = declares.Count > 0 ? declares : CandidatsPourSysteme(systemId, systemFolder);

        foreach (var nom in candidats)
        {
            var fichier = nom.EndsWith("_libretro.dll", StringComparison.OrdinalIgnoreCase) ? nom : nom + "_libretro.dll";
            foreach (var sub in new[] { "cores_real", "cores" })
            {
                var chemin = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", sub, fichier);
                if (File.Exists(chemin))
                {
                    return chemin;
                }
            }
        }

        return null;
    }

    /// <summary>Les cores usuels a essayer pour ce systeme, dans l'ordre, sans toucher au disque :
    /// le dossier d'EmulationStation d'abord, l'identifiant de systeme ensuite.</summary>
    internal static IReadOnlyList<string> CandidatsPourSysteme(string? systemId, string? systemFolder)
    {
        foreach (var cle in new[] { systemFolder, systemId })
        {
            var normalisee = Normaliser(cle);
            if (normalisee is not null && CoresStandardsParSysteme.TryGetValue(normalisee, out var candidats))
            {
                return candidats;
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>« mega_drive », « Mega-Drive » et « megadrive » designent le meme systeme.</summary>
    internal static string? Normaliser(string? valeur) => string.IsNullOrWhiteSpace(valeur)
        ? null
        : new string(valeur.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Garde le couple core + ROM trouve dans les metadonnees LOCALES, pour que la lecture
    /// suivante du meme replay reparte du chemin rapide au lieu de re-scanner les cores et les
    /// ROMs. Le manifeste, lui, n'est jamais touche : il decrit l'enregistrement, pas cette
    /// machine. Un echec d'ecriture ne doit pas empecher la lecture.
    /// </summary>
    private void MemoriserLancement(ReplayManifest manifest, string core, string rom)
    {
        try
        {
            var courant = _meta.GetMeta(manifest.ReplayId);
            if (courant?.Launch is not null
                && string.Equals(courant.Launch.CoreDll, core, StringComparison.OrdinalIgnoreCase)
                && string.Equals(courant.Launch.RomPath, rom, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var nom = Path.GetFileNameWithoutExtension(core);
            if (nom.EndsWith("_libretro", StringComparison.OrdinalIgnoreCase))
            {
                nom = nom[..^"_libretro".Length];
            }

            var indice = new ReplayLaunchHint(
                manifest.Game.SystemFolder ?? manifest.Game.SystemId ?? string.Empty, nom, core, rom);

            // Un replay recu d'ailleurs n'a pas ete cree ici : ne pas le faire passer pour local.
            _meta.SaveMeta(courant is not null
                ? courant with { Launch = indice }
                : ReplayLocalMetadata.Fresh(manifest.ReplayId, indice) with { CreatedByThisDevice = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Replay : indice de lancement non memorise pour {Id}.", manifest.ReplayId);
        }
    }

    /// <summary>
    /// Quel core cette borne a-t-elle deja employe pour ce systeme ? On l'APPREND de nos propres
    /// enregistrements plutot que de le deviner : chacun porte le chemin du core reellement
    /// lance. Aucune table a maintenir, et la reponse colle a cette installation.
    /// </summary>
    private string? CoreDejaUtilisePour(string? systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId)) return null;
        foreach (var m in _manifests.ListManifests())
        {
            if (!string.Equals(m.Game.SystemId, systemId, StringComparison.OrdinalIgnoreCase)) continue;
            var dll = _meta.GetMeta(m.ReplayId)?.Launch?.CoreDll;
            if (!string.IsNullOrWhiteSpace(dll) && File.Exists(dll)) return dll;
        }
        return null;
    }

    // ROM : hint local (rapide) → scan roms/<système> par crc32 de CONTENU (== celui de RetroArch,
    // décompressé pour un .zip). Le DOSSIER système vient du hint local, sinon du MANIFESTE
    // (`game.system_folder`, identifiant portable posé à l'enregistrement) → un replay reçu d'un
    // peer, sans hint, reste résolvable. On ne scanne jamais globalement (coûteux).
    private string? ResolveRom(ReplayManifest manifest, ReplayLaunchHint? hint)
    {
        if (hint is not null && !string.IsNullOrEmpty(hint.RomPath) && File.Exists(hint.RomPath)) return hint.RomPath;

        var crc = manifest.Game.Crc32;
        if (string.IsNullOrWhiteSpace(crc)) return null;

        // C'EST ES QUI DECIDE ou vivent les ROMs, pas nous. Chaque machine est differente :
        // second disque, partage reseau, arborescence heritee. Supposer « roms/<systeme> »
        // marcherait sur une installation par defaut et echouerait chez tous les autres, en
        // faisant passer une ROM simplement rangee ailleurs pour un replay incompatible.
        var romDir = _romPaths.DirectoryFor(hint?.SystemFolder ?? manifest.Game.SystemFolder)
                     ?? _romPaths.DirectoryFor(manifest.Game.SystemId);
        if (romDir is null || !Directory.Exists(romDir))
        {
            _logger.LogWarning("Replay : aucun dossier de ROMs declare par ES pour {System}.",
                hint?.SystemFolder ?? manifest.Game.SystemFolder ?? manifest.Game.SystemId);
            return null;
        }

        foreach (var f in Directory.EnumerateFiles(romDir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".txt" or ".xml" or ".dat" or ".jpg" or ".png" or ".srm" or ".state" or ".cfg") continue;
            if (string.Equals(ContentCrc32(f), crc, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }

    // crc32 du CONTENU : pour un .zip, l'entrée ROM décompressée (comme RetroArch) ; sinon le fichier.
    private static string? ContentCrc32(string path)
    {
        try
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                ZipArchiveEntry? entry = null;
                foreach (var e in archive.Entries)
                {
                    if (e.Length > 0 && !e.FullName.EndsWith('/')) { entry = e; break; }
                }
                if (entry is null) return null;
                using var s = entry.Open();
                return Crc32Stream(s);
            }
            using var fs = File.OpenRead(path);
            return Crc32Stream(fs);
        }
        catch { return null; }
    }

    private static string Crc32Stream(Stream s)
    {
        var crc = 0xFFFFFFFFu;
        var buf = new byte[81920];
        int r;
        while ((r = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < r; i++) { crc = (crc >> 8) ^ Crc32Table[(crc ^ buf[i]) & 0xFF]; }
        }
        return (crc ^ 0xFFFFFFFFu).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? HashFileQuiet(string path)
    {
        try { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
        catch { return null; }
    }

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "-" : sha[..Math.Min(8, sha.Length)];

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) { crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
            table[i] = crc;
        }
        return table;
    }
}
