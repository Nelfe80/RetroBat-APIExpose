using System.IO.Compression;
using System.Security.Cryptography;
using RetroBat.Api.Replay.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Core + ROM résolus sur CETTE machine pour lancer un replay. <paramref name="Avertissement"/>
/// est ce qu'on dit au joueur quand on lance SANS le fichier exact : la lecture part, mais elle
/// peut dériver, et c'est à lui d'en juger.
/// </summary>
public sealed record ResolvedRuntime(string CoreDll, string RomPath, bool ExactCore, bool ExactRom = true, string? Avertissement = null);

/// <summary>
/// Ce que le résolveur a trouvé, ou POURQUOI il n'a rien trouvé. Le code dit quelle moitié
/// manque (le cœur ou la ROM), le détail dit ce qu'on cherchait : un joueur à qui l'on répond
/// « jeu ou cœur absent » ne sait pas quoi corriger, un joueur qui lit « empreinte 3a1f… absente
/// de roms/megadrive » va chercher la bonne version de sa ROM (2026-09-21).
/// </summary>
public sealed record RuntimeResolution(ResolvedRuntime? Runtime, ReplayErrorCode Failure, string? Detail)
{
    public static RuntimeResolution Trouve(ResolvedRuntime runtime) => new(runtime, ReplayErrorCode.None, null);
    public static RuntimeResolution Manque(ReplayErrorCode code, string detail) => new(null, code, detail);
}

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

    private readonly Media.InstalledGameCatalog? _catalogue;
    private readonly Infrastructure.CabinetLocale? _locale;
    private readonly Media.RomCanonicalResolver? _referentiel;

    public ReplayRuntimeResolver(EsSystemsRomPaths romPaths, Storage.IReplayManifestStore manifests,
        Storage.IReplayMetadataStore meta, ILogger<ReplayRuntimeResolver> logger,
        Media.InstalledGameCatalog? catalogue = null,
        Infrastructure.CabinetLocale? locale = null,
        Media.RomCanonicalResolver? referentiel = null)
    {
        _romPaths = romPaths; _manifests = manifests; _meta = meta; _logger = logger; _catalogue = catalogue;
        _locale = locale;
        _referentiel = referentiel;
    }

    /// <summary>
    /// L'avertissement s'affiche dans le bandeau et dans l'etat de lecture : dans la langue de la
    /// borne. Sans langue connue, le francais d'origine.
    /// </summary>
    private string Texte(string cle) => Infrastructure.CabinetAnnounceText.Get(cle, _locale?.Langue ?? "fr");

    public RuntimeResolution Resolve(ReplayManifest manifest, ReplayLaunchHint? hint)
    {
        var core = ResolveCore(manifest, hint, out var exact);
        if (core is null)
        {
            _logger.LogWarning("Replay resolver : aucun core utilisable pour {Id} (runtime {Rt}, core_sha256 {Sha})",
                manifest.ReplayId, manifest.Runtime.RuntimeId, Short(manifest.Runtime.CoreSha256));
            var attendu = string.IsNullOrWhiteSpace(manifest.Runtime.CoreName)
                ? "un cœur RetroArch pour " + (manifest.Game.SystemFolder ?? manifest.Game.SystemId)
                : "le cœur " + manifest.Runtime.CoreName;
            return RuntimeResolution.Manque(ReplayErrorCode.CoreNotFound,
                "Ce record a été enregistré avec " + attendu + " : cette borne ne l'a pas parmi ses cores RetroArch.");
        }
        var rom = ResolveRom(manifest, hint, out var romExacte, out var pourquoi);
        if (rom is null)
        {
            _logger.LogWarning("Replay resolver : ROM introuvable pour {Id} (crc32 {Crc}) : {Pourquoi}", manifest.ReplayId, manifest.Game.Crc32, pourquoi);
            return RuntimeResolution.Manque(ReplayErrorCode.RomNotFound, pourquoi);
        }
        string? avertissement = null;
        if (!exact)
        {
            // ON NOMME LES DEUX COTES. « core non identique » ne disait pas lequel, et il a
            // fallu remonter aux versions a la main pour comprendre qu'un replay MAME 0.289 se
            // rejouait sur un MAME 0.287 (2026-09-23) : vingt secondes de lecture, puis retour
            // a EmulationStation sans un mot.
            var version = VersionDuFichier(core);
            var nom = string.IsNullOrWhiteSpace(manifest.Runtime.CoreName) ? Texte("replay_this_core") : manifest.Runtime.CoreName;
            avertissement = version.Length > 0
                ? string.Format(Texte("replay_core_other_local"), nom, version)
                : string.Format(Texte("replay_core_other"), nom);
            _logger.LogInformation(
                "Replay resolver : core NON identique à l'enregistrement pour {Id} — {Core} local en version {Version}, lecture best-effort.",
                manifest.ReplayId, Path.GetFileName(core), version.Length > 0 ? version : "inconnue");
        }
        if (!romExacte) _logger.LogInformation("Replay resolver : ROM NON identique à l'enregistrement pour {Id} ({Rom}) — lecture best-effort.", manifest.ReplayId, rom);
        // On ne mémorise que le fichier exact : un dump approchant ne doit pas devenir le chemin
        // rapide, sinon la ROM exacte copiée plus tard ne serait plus jamais cherchée.
        if (romExacte) MemoriserLancement(manifest, core, rom);
        // UN SEUL AVERTISSEMENT REMONTE, et la ROM passe devant : quand le dump n'est pas
        // celui de l'enregistrement, c'est la cause la plus concrete et la plus reparable.
        return RuntimeResolution.Trouve(new ResolvedRuntime(
            core, rom, exact, romExacte, romExacte ? avertissement : pourquoi));
    }

    // Core : hint local (rapide, en préférant cores_real sans wrapper scoring) → empreinte
    // core_sha256 (scan cores_real puis cores) → null. Jamais de refus sur la version.
    /// <summary>La version que porte une dll de coeur, vide si elle ne se lit pas.</summary>
    private static string VersionDuFichier(string chemin)
    {
        try
        {
            return (System.Diagnostics.FileVersionInfo.GetVersionInfo(chemin).ProductVersion ?? "").Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private string? ResolveCore(ReplayManifest manifest, ReplayLaunchHint? hint, out bool exact)
    {
        exact = false;
        if (hint is not null && !string.IsNullOrEmpty(hint.Core))
        {
            // Un indice memorise n'est cru que s'il tient debout : le core qu'il nomme porte
            // l'empreinte du manifeste, ou fait partie des cores connus du systeme. Une lecture
            // passee avait memorise 2048 pour un replay de Sonic (2026-09-17), et chaque lecture
            // suivante le reprenait comme exact.
            var real = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores_real", hint.Core + "_libretro.dll");
            var chemin = File.Exists(real) ? real : (!string.IsNullOrEmpty(hint.CoreDll) && File.Exists(hint.CoreDll) ? hint.CoreDll : null);
            if (chemin is not null)
            {
                var parEmpreinte = !string.IsNullOrWhiteSpace(manifest.Runtime.CoreSha256)
                    && string.Equals(HashFileQuiet(chemin), manifest.Runtime.CoreSha256, StringComparison.OrdinalIgnoreCase);
                var connus = _romPaths.LibretroCoresFor(hint.SystemFolder ?? manifest.Game.SystemFolder);
                if (connus.Count == 0) connus = _romPaths.LibretroCoresFor(manifest.Game.SystemId);
                var candidats = connus.Count > 0 ? connus : CandidatsPourSysteme(manifest.Game.SystemId, hint.SystemFolder ?? manifest.Game.SystemFolder);
                var plausible = candidats.Any(c => string.Equals(c.Replace("_libretro.dll", "").Replace("_libretro", ""), hint.Core, StringComparison.OrdinalIgnoreCase));
                if (parEmpreinte || plausible) { exact = parEmpreinte; return chemin; }
                _logger.LogInformation("Replay : indice memorise ecarte pour {Id} ({Core} n'est ni le core du manifeste ni un core de {System}).",
                    manifest.ReplayId, hint.Core, manifest.Game.SystemId);
            }
        }

        // Le manifeste NOMME son core : c'est lui, exact si l'empreinte suit, best-effort sinon
        // (une autre version du meme core rejoue presque toujours, les checkpoints disent le reste).
        var nomme = manifest.Runtime.CoreName;
        if (!string.IsNullOrWhiteSpace(nomme))
        {
            foreach (var sub in new[] { "cores_real", "cores" })
            {
                var chemin = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", sub, nomme + "_libretro.dll");
                if (!File.Exists(chemin)) continue;
                exact = !string.IsNullOrWhiteSpace(manifest.Runtime.CoreSha256)
                    && string.Equals(HashFileQuiet(chemin), manifest.Runtime.CoreSha256, StringComparison.OrdinalIgnoreCase);
                return chemin;
            }
        }

        var wanted = manifest.Runtime.CoreSha256;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            // Dans cores/, chaque dll est le wrapper de scoring quand il est deploye : elles ont
            // TOUTES la meme empreinte, et un manifeste enregistre avec cette empreinte faisait
            // retenir la premiere de la liste (2048 pour un replay de Sonic, 2026-09-17). Jamais une
            // dll qui est le wrapper, donc. cores_real/ d'abord (ancien montage), puis cores/, ou vit
            // le vrai coeur dans le montage core_proxy (2026-10-04) : un cores_real/ reste la ne doit
            // pas cacher cores/.
            var wrapperSha = HashFileQuiet(Path.Combine(RetroBatPaths.RetroBatRoot, "plugins", "APIExpose", "wrapper", "wrapper.dll"));
            if (!string.Equals(wanted, wrapperSha, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var sub in new[] { "cores_real", "cores" })
                {
                    var root = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", sub);
                    if (!Directory.Exists(root)) continue;
                    foreach (var dll in Directory.EnumerateFiles(root, "*_libretro.dll"))
                    {
                        var sha = HashFileQuiet(dll);
                        if (string.Equals(sha, wrapperSha, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(sha, wanted, StringComparison.OrdinalIgnoreCase)) { exact = true; return dll; }
                    }
                }
            }
        }
        // Repli PERMISSIF : un core deja employe sur CETTE machine pour le meme systeme. Un
        // replay n'est pas une soumission de score : mieux vaut le montrer avec un core proche
        // que de le refuser. Un desync eventuel se verra a l'ecran, et les checkpoints du .bsv
        // le signalent.
        var apprenti = CoreDejaUtilisePour(manifest.Game.SystemId, hint?.SystemFolder ?? manifest.Game.SystemFolder);
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
    private string? CoreDejaUtilisePour(string? systemId, string? systemFolder)
    {
        if (string.IsNullOrWhiteSpace(systemId)) return null;
        // Seul un core que le systeme connait compte : une lecture passee qui s'etait trompee
        // (2048 memorise pour la Megadrive) ne doit pas se propager aux replays voisins.
        var connus = _romPaths.LibretroCoresFor(systemFolder);
        if (connus.Count == 0) connus = _romPaths.LibretroCoresFor(systemId);
        var candidats = connus.Count > 0 ? connus : CandidatsPourSysteme(systemId, systemFolder);
        foreach (var m in _manifests.ListManifests())
        {
            if (!string.Equals(m.Game.SystemId, systemId, StringComparison.OrdinalIgnoreCase)) continue;
            var dll = _meta.GetMeta(m.ReplayId)?.Launch?.CoreDll;
            if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) continue;
            var nom = Path.GetFileNameWithoutExtension(dll).Replace("_libretro", "");
            if (candidats.Any(c => string.Equals(c.Replace("_libretro.dll", "").Replace("_libretro", ""), nom, StringComparison.OrdinalIgnoreCase))) return dll;
        }
        return null;
    }

    // ROM : hint local (rapide) → les fichiers qui peuvent être CE jeu, trouvés à leur NOM (dans le dossier du système
    // et dans ses dossiers voisins) et par le RÉFÉRENTIEL des jeux (resources/gamelist/systems : toutes les versions d'un
    // même jeu y portent le même groupe) → parmi eux le FICHIER EXACT, reconnu à son empreinte → sinon le plus proche
    // d'entre eux, en prévenant → et seulement si la borne n'a AUCUN fichier de ce jeu, une recherche par contenu.
    //
    // AVANT LE 2026-10-11, la borne hachait le dossier pendant vingt secondes AVANT de regarder ce qu'elle
    // reconnaissait. Un replay de Double Dragon enregistré sur un autre dump lui a fait lire 190 des 1 960 fichiers de
    // roms\fbneo, pour lancer ensuite le ddragon.zip qu'elle avait sous les yeux. Les noms et le référentiel passent
    // devant : ils ne coûtent que des noms de fichiers.
    //
    // Le fichier exact se reconnaît à l'empreinte que RetroArch a annoncée à l'enregistrement,
    // et RetroArch ne la calcule pas partout pareil : pour une console il extrait la ROM du zip
    // et hache ce contenu (= le CRC que l'archive stocke pour cette entrée) ; pour l'arcade,
    // dont le core charge le zip par chemin, il hache le ZIP ENTIER. On accepte donc l'une ou
    // l'autre, plus le SHA-256 du fichier que le manifeste porte aussi. Avant le 2026-09-21 on
    // ne hachait que la première entrée du zip : aucun replay d'arcade ne se rejouait ailleurs
    // que sur la borne qui l'avait enregistré.
    //
    // Le DOSSIER système vient du hint local, sinon du MANIFESTE (`game.system_folder`,
    // identifiant portable) → un replay reçu d'un peer, sans hint, reste résolvable. C'EST ES QUI
    // DECIDE où vivent les ROMs (second disque, partage réseau) : on ne suppose jamais roms/<x>.
    // Le second résultat dit au joueur CE QU'ON CHERCHAIT, pour qu'il puisse agir.
    private string? ResolveRom(ReplayManifest manifest, ReplayLaunchHint? hint, out bool exacte, out string pourquoi)
    {
        exacte = true;
        pourquoi = string.Empty;
        if (hint is not null && !string.IsNullOrEmpty(hint.RomPath) && File.Exists(hint.RomPath)) return hint.RomPath;

        var crc = (manifest.Game.Crc32 ?? string.Empty).Trim().ToLowerInvariant();
        var sha = (manifest.Runtime.RomSha256 ?? string.Empty).Trim().ToLowerInvariant();
        var systeme = hint?.SystemFolder ?? manifest.Game.SystemFolder ?? manifest.Game.SystemId;
        var romDir = _romPaths.DirectoryFor(hint?.SystemFolder ?? manifest.Game.SystemFolder)
                     ?? _romPaths.DirectoryFor(manifest.Game.SystemId);
        var fichiers = romDir is not null && Directory.Exists(romDir) ? ListerRoms(romDir) : new List<string>();
        if (fichiers.Count == 0)
        {
            _logger.LogWarning("Replay : aucun dossier de ROMs utilisable pour {System} ({Dir}).", systeme, romDir ?? "non déclaré");
        }

        // 1. Les fichiers qui portent le nom du jeu, ici et dans les dossiers voisins du même système (un jeu d'arcade
        //    vit sous fbneo comme sous mame). C'est presque toujours l'un d'eux : quelques fichiers à lire, jamais un
        //    dossier entier.
        var nomAttendu = NomDuJeu(manifest.Game.GameId);
        var partout = fichiers.Concat(FichiersDesDossiersVoisins(systeme, romDir)).ToList();
        var parNom = partout.Where(f => MemeNom(f, nomAttendu)).ToList();
        var aUneEmpreinte = crc.Length > 0 || sha.Length > 0;
        if (aUneEmpreinte)
        {
            foreach (var f in parNom) { if (PorteEmpreinte(f, crc, sha, limiteOctets: 0)) return f; }
        }

        // 2. Le même jeu sous un autre nom : ce que le référentiel range dans le même jeu, puis ce qui en porte le
        //    titre. Parmi eux le fichier exact, sinon le plus proche : le même nom, une autre version du jeu, un dump
        //    du même groupe de scoring, le même titre. En arcade, un fichier du même nom suffit à conclure : le cœur
        //    reconnaît le jeu au nom de son archive, le fichier exact ne peut pas être rangé sous un autre.
        var nomLie = NomLieAuJeu(systeme, manifest.Runtime.CoreName);
        var autre = parNom.FirstOrDefault();
        var comment = "même nom";
        if (autre is null || !nomLie)
        {
            var memeJeu = MemeJeuAuReferentiel(systeme, nomAttendu, crc, partout);
            var memeTitre = fichiers.Where(f => MemeTitre(f, nomAttendu)).ToList();
            if (aUneEmpreinte)
            {
                foreach (var f in Proches(memeJeu, memeTitre).Except(parNom, StringComparer.OrdinalIgnoreCase))
                {
                    if (PorteEmpreinte(f, crc, sha, limiteOctets: 0)) return f;
                }
            }
            if (autre is null && memeJeu.Count > 0)
            {
                (autre, comment) = (memeJeu[0], "même jeu au référentiel");
            }
            if (autre is null)
            {
                var duGroupe = DumpsDuMemeGroupe(manifest);
                if (aUneEmpreinte)
                {
                    foreach (var f in duGroupe) { if (PorteEmpreinte(f, crc, sha, limiteOctets: 0)) return f; }
                }
                (autre, comment) = duGroupe.Count > 0 ? (duGroupe[0], "même groupe de scoring")
                    : memeTitre.Count > 0 ? (memeTitre[0], "même titre")
                    : (autre, comment);
            }
        }

        // 3. Le même jeu, autrement. On lance, et on dit pourquoi ça peut dériver : c'est au joueur de juger, pas à la
        //    borne de refuser.
        if (autre is not null)
        {
            _logger.LogInformation("Replay : {Id} rejoué sur un autre fichier du même jeu ({Comment}) : {Rom}.", manifest.ReplayId, comment, autre);
            exacte = false;
            pourquoi = crc.Length > 0
                ? string.Format(Texte("replay_rom_other_crc"), Path.GetFileName(autre), crc)
                : string.Format(Texte("replay_rom_other"), Path.GetFileName(autre));
            return autre;
        }

        // 4. La borne n'a aucun fichier de ce jeu sous un nom qu'elle reconnaît. Reste le fichier exact rangé sous un
        //    autre nom : il se cherche par son contenu, dans un budget. Jamais en arcade, où le cœur reconnaît le jeu
        //    au nom de son archive : le même contenu sous un autre nom n'y lancerait pas ce jeu.
        if (aUneEmpreinte && !nomLie)
        {
            var trouve = ChercherParContenu(fichiers, crc, sha, romDir);
            if (trouve is not null) return trouve;
        }

        pourquoi = fichiers.Count == 0
            ? "EmulationStation ne déclare aucun dossier de ROMs pour « " + systeme + " » sur cette borne, ou il est vide."
            : "Cette borne n'a pas ce jeu" + (nomAttendu.Length > 0 ? " (« " + nomAttendu + " »)" : string.Empty)
              + " parmi les " + fichiers.Count + " fichiers de " + romDir + ", ni sous aucun autre dossier du même système.";
        return null;
    }

    /// <summary>
    /// Les fichiers à comparer à l'empreinte du replay, du plus sûr au moins sûr et sans doublon : ceux qui portent le
    /// nom du jeu, ceux que le référentiel range dans le même jeu, ceux qui en portent le titre.
    /// </summary>
    internal static List<string> Proches(params IEnumerable<string>[] sources)
        => sources.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Les fichiers des AUTRES dossiers du même système de scoring : un jeu d'arcade vit sous fbneo comme sous mame, et
    /// un replay se lance par le chemin de sa ROM, d'où qu'elle vienne. Un dossier que deux systèmes partagent ne compte
    /// qu'une fois.
    /// </summary>
    private List<string> FichiersDesDossiersVoisins(string? systeme, string? romDir)
    {
        var voisins = new List<string>();
        if (string.IsNullOrWhiteSpace(systeme)) return voisins;
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (romDir is not null) vus.Add(CheminComplet(romDir));
        foreach (var frontend in Media.RomCanonicalResolver.FrontendSystemsFor(Media.RomCanonicalResolver.CanonicalScoringSystem(systeme)))
        {
            var dir = _romPaths.DirectoryFor(frontend);
            if (dir is null || !Directory.Exists(dir) || !vus.Add(CheminComplet(dir))) continue;
            voisins.AddRange(ListerRoms(dir));
        }
        return voisins;
    }

    private static string CheminComplet(string dossier)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dossier)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return dossier; }
    }

    /// <summary>
    /// LES FICHIERS DU MEME JEU, D'APRES LE REFERENTIEL (resources/gamelist/systems). Chaque fiche y porte le groupe de
    /// son jeu : toutes les versions, régions et révisions d'un jeu ont le même. Le jeu du replay s'y retrouve par le
    /// nom de son fichier (ou par son empreinte, pour une console), et chaque fichier de la borne par le sien. Aucune
    /// ROM n'est lue : seuls des noms sont comparés. L'index est celui du dossier (fbneo, megadrive), sinon celui du
    /// système de scoring (arcade) ; le même sert à tous les fichiers, pour que leurs groupes se comparent.
    /// </summary>
    private List<string> MemeJeuAuReferentiel(string? systeme, string nomAttendu, string crc, IReadOnlyList<string> fichiers)
    {
        if (_referentiel is null || string.IsNullOrWhiteSpace(systeme) || fichiers.Count == 0) return new List<string>();
        try
        {
            foreach (var index in new[] { systeme, Media.RomCanonicalResolver.CanonicalScoringSystem(systeme) }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fiche = (nomAttendu.Length > 0 ? _referentiel.ResolveByRomName(index, nomAttendu) : null)
                            ?? (crc.Length > 0 ? _referentiel.Resolve(index, crc) : null);
                if (fiche is null) continue;
                return MemeJeu(fiche.GameKey, fichiers, f => _referentiel.ResolveByRomName(index, Path.GetFileName(f))?.GameKey, nomAttendu);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Replay : référentiel des jeux illisible pour {Systeme}.", systeme);
        }
        return new List<string>();
    }

    /// <summary>Parmi ces fichiers, ceux du jeu <paramref name="jeu"/> ; ceux qui portent le nom du replay d'abord.</summary>
    internal static List<string> MemeJeu(string jeu, IEnumerable<string> fichiers, Func<string, string?> jeuDuFichier, string nomAttendu)
        => fichiers.Where(f => string.Equals(jeuDuFichier(f), jeu, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => MemeNom(f, nomAttendu))
            .ToList();

    /// <summary>
    /// Le cœur reconnaît-il le jeu au NOM de son archive ? C'est le cas de l'arcade (FBNeo, MAME) : le même contenu sous
    /// un autre nom n'y lancerait pas ce jeu, et le chercher par contenu dans le dossier ne sert à rien.
    /// </summary>
    internal static bool NomLieAuJeu(string? systeme, string? coeur)
    {
        if (Media.RomCanonicalResolver.CanonicalScoringSystem(systeme ?? string.Empty) == "arcade") return true;
        var nom = (coeur ?? string.Empty).Trim().ToLowerInvariant();
        return nom.StartsWith("fbneo", StringComparison.Ordinal) || nom.StartsWith("fbalpha", StringComparison.Ordinal)
               || nom.StartsWith("mame", StringComparison.Ordinal) || nom.StartsWith("hbmame", StringComparison.Ordinal);
    }

    /// <summary>
    /// LA RECHERCHE PAR CONTENU A UN BUDGET (2026-10-10), ET NE VIENT QU'EN DERNIER (2026-10-11). Un replay de Ms. Pac-Man
    /// enregistré avec un autre dump : la borne a relu les 1 963 fichiers (4,7 Go) du dossier fbneo pour un fichier
    /// qu'elle n'avait pas, trois minutes quarante sur l'écran de lecture. Le budget a ramené cette attente à vingt
    /// secondes ; elle n'a plus lieu du tout quand la borne reconnaît le jeu à son nom ou par le référentiel, ni en arcade.
    /// </summary>
    internal static readonly TimeSpan BudgetDeRecherche = TimeSpan.FromSeconds(20);

    private volatile RechercheDeRom? _recherche;

    /// <inheritdoc />
    public RechercheDeRom? RechercheEnCours => _recherche;

    /// <summary>
    /// Le fichier exact rangé sous un autre nom, cherché par son contenu, en deux passes dans le même budget. D'abord
    /// les en-têtes des archives : le CRC de chaque entrée s'y lit sans rien décompresser, et c'est lui que RetroArch
    /// annonce pour une console. Puis les fichiers entiers, tant qu'il reste du temps.
    /// </summary>
    private string? ChercherParContenu(IReadOnlyList<string> fichiers, string crc, string sha, string? romDir)
    {
        var montre = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (crc.Length > 0)
            {
                var archives = fichiers.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
                var parEntree = FichierExact(archives, f => PorteEntree(f, crc), BudgetDeRecherche,
                    (lus, total) => _recherche = new RechercheDeRom(lus, total), out _);
                if (parEntree is not null) return parEntree;
            }
            var trouve = FichierExact(fichiers, f => PorteFichier(f, crc, sha, LimiteDUnFichierCherche), BudgetDeRecherche - montre.Elapsed,
                (lus, total) => _recherche = new RechercheDeRom(lus, total), out var entiers);
            if (trouve is null && entiers < fichiers.Count)
            {
                _logger.LogInformation("Replay : fichier exact introuvable en {Secondes} s ({Lus} fichier(s) lus en entier sur {Total} dans {Dir}).",
                    BudgetDeRecherche.TotalSeconds, entiers, fichiers.Count, romDir);
            }
            return trouve;
        }
        finally
        {
            _recherche = null;
        }
    }

    /// <summary>Au-delà, un fichier rangé sous un autre nom n'est pas lu en entier : un .iso de 700 Mo n'est pas un candidat sérieux.</summary>
    private const long LimiteDUnFichierCherche = 256L * 1024 * 1024;

    /// <summary>
    /// Le fichier exact parmi des candidats d'un autre nom, dans le budget ; null sinon. <paramref name="lus"/> dit combien
    /// ont ete lus : moins que le total, le budget s'est epuise. Un fichier commence est toujours lu jusqu'au bout.
    /// </summary>
    internal static string? FichierExact(IReadOnlyList<string> candidats, string crc, string sha, TimeSpan budget,
        Action<int, int>? progression, out int lus)
        => FichierExact(candidats, f => PorteEmpreinte(f, crc, sha, LimiteDUnFichierCherche), budget, progression, out lus);

    /// <summary>La même recherche, avec la façon de reconnaître le fichier (une entrée d'archive, ou le fichier entier).</summary>
    internal static string? FichierExact(IReadOnlyList<string> candidats, Func<string, bool> estLeBon, TimeSpan budget,
        Action<int, int>? progression, out int lus)
    {
        var montre = System.Diagnostics.Stopwatch.StartNew();
        for (lus = 0; lus < candidats.Count; lus++)
        {
            if (montre.Elapsed >= budget) return null;
            progression?.Invoke(lus, candidats.Count);
            if (estLeBon(candidats[lus])) return candidats[lus];
        }
        return null;
    }

    private static readonly HashSet<string> ExtensionsIgnorees = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".xml", ".dat", ".jpg", ".jpeg", ".png", ".gif", ".mp4", ".srm", ".state", ".cfg",
        ".sav", ".ini", ".nfo", ".pdf", ".db", ".bak", ".log", ".json", ".md", ".html",
    };

    // Sous-dossiers compris : les joueurs rangent leurs ROMs par lettre ou par genre.
    private List<string> ListerRoms(string romDir)
    {
        try
        {
            return Directory.EnumerateFiles(romDir, "*", SearchOption.AllDirectories)
                .Where(f => !ExtensionsIgnorees.Contains(Path.GetExtension(f)))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "media" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Replay : dossier de ROMs illisible : {Dir}", romDir);
            return new List<string>();
        }
    }

    // Le manifeste identifie le jeu comme « <système>/<nom-du-fichier-en-slug> ».
    private static string NomDuJeu(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return string.Empty;
        var i = gameId.IndexOf('/');
        return (i >= 0 ? gameId[(i + 1)..] : gameId).Trim();
    }

    private static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    internal static bool MemeNom(string fichier, string nomAttendu)
        => nomAttendu.Length > 0 && string.Equals(Slug(Path.GetFileNameWithoutExtension(fichier)), nomAttendu, StringComparison.Ordinal);

    // Même titre, autre édition : « Sonic The Hedgehog (Japan) » pour « sonic-the-hedgehog-usa-europe ».
    // Le titre est ce qui précède la première parenthèse ou le premier crochet ; trop court, il
    // ne prouve rien (« 1942 » vaut, « a » non).
    internal static bool MemeTitre(string fichier, string nomAttendu)
    {
        if (nomAttendu.Length == 0) return false;
        var nom = Path.GetFileNameWithoutExtension(fichier);
        var coupe = nom.IndexOfAny(new[] { '(', '[' });
        var titre = Slug(coupe > 0 ? nom[..coupe] : nom);
        return titre.Length >= 4 && (nomAttendu == titre || nomAttendu.StartsWith(titre + "-", StringComparison.Ordinal));
    }

    /// <summary>
    /// Les dumps installés du même groupe de SCORING (la définition .MEM du jeu, quand le manifeste en nomme une), tous
    /// dossiers frontend confondus. Plus lent que le référentiel (il relit les gamelists) : consulté seulement quand ni
    /// le nom ni le référentiel n'ont rien donné.
    /// </summary>
    private List<string> DumpsDuMemeGroupe(ReplayManifest manifest)
    {
        var groupe = manifest.Game.RomGroup;
        if (_catalogue is null || string.IsNullOrWhiteSpace(groupe)) return new List<string>();
        try
        {
            var canonique = Media.RomCanonicalResolver.CanonicalScoringSystem(manifest.Game.SystemFolder ?? manifest.Game.SystemId);
            return _catalogue.DumpsOf(canonique, groupe).Select(j => j.AbsolutePath).Where(File.Exists).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : inventaire du groupe {Groupe} impossible.", groupe);
            return new List<string>();
        }
    }

    // Le fichier porte-t-il l'empreinte attendue ? Pour un zip, les CRC de ses entrées se lisent
    // dans l'en-tête sans rien décompresser (c'est ce que RetroArch annonce pour une console) ;
    // le fichier entier n'est lu qu'ensuite (ce que RetroArch annonce pour l'arcade), et
    // seulement s'il reste sous la limite : 0 = pas de limite.
    internal static bool PorteEmpreinte(string path, string crc, string sha, long limiteOctets)
        => PorteEntree(path, crc) || PorteFichier(path, crc, sha, limiteOctets);

    /// <summary>Une entrée de cette archive zip porte-t-elle ce CRC ? Seul l'en-tête de l'archive est lu.</summary>
    internal static bool PorteEntree(string path, string crc)
    {
        if (crc.Length == 0 || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var e in archive.Entries)
            {
                if (e.Length > 0 && e.Crc32.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) == crc) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Le fichier ENTIER porte-t-il ce CRC ou ce SHA-256 ? Au-dessus de la limite il n'est pas lu (0 = pas de limite).</summary>
    internal static bool PorteFichier(string path, string crc, string sha, long limiteOctets)
    {
        try
        {
            var taille = new FileInfo(path).Length;
            if (limiteOctets > 0 && taille > limiteOctets) return false;
            using var fs = File.OpenRead(path);
            var (crcFichier, shaFichier) = Empreintes(fs, calculerSha: sha.Length > 0);
            return (crc.Length > 0 && crcFichier == crc) || (sha.Length > 0 && shaFichier == sha);
        }
        catch { return false; }
    }

    // Une seule lecture pour les deux empreintes.
    private static (string Crc32, string? Sha256) Empreintes(Stream s, bool calculerSha)
    {
        var crc = 0xFFFFFFFFu;
        using var sha = calculerSha ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buf = new byte[81920];
        int r;
        while ((r = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < r; i++) { crc = (crc >> 8) ^ Crc32Table[(crc ^ buf[i]) & 0xFF]; }
            sha?.AppendData(buf, 0, r);
        }
        var crcHex = (crc ^ 0xFFFFFFFFu).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        return (crcHex, sha is null ? null : Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
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
