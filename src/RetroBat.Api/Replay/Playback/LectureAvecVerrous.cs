using System.Diagnostics;
using System.Text;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// LA LECTURE D'UN REPLAY DE DEFI A VERROUS (D18). Le replay ne garde que les appuis : le jeu doit recevoir les memes verrous
/// aux memes images, sinon il tire la ou le joueur avait donne un coup de couteau et tout diverge. La lecture passe alors par
/// le wrapper (0.343 au moins) de core_proxy, le vrai coeur designe comme au lancement de RetroBat, avec un certified.txt qui
/// porte les verrous du manifeste et « pipe=off » : les verrous se posent, rien ne part vers l'API, un replay n'est pas une
/// partie. Sans verrou, rien ne change : vrai coeur, sans wrapper.
///
/// LE REPLAY ENREGISTRE TOUCHE PAR TOUCHE (11/11, <see cref="ModeDesEntrees"/>) se relit aussi par le wrapper (0.345 au
/// moins), « pipe=off », avec RETROBAT_INPUT_BITMASKS=off : le wrapper refuse au coeur le masque des touches, et le coeur
/// lit sa manette comme a l'enregistrement. Sans cela il demandait le masque, absent du replay, et la partie rejouee
/// perdait ses entrees (Streets of Rage 2 : 1 090 au lieu de 3 960 ; par le wrapper sans masque, 3 960 exact).
/// </summary>
public static class LectureAvecVerrous
{
    /// <summary>La premiere version du wrapper qui sait tenir des verrous et se taire.</summary>
    public const int VersionMinimale = 343;

    /// <summary>La premiere version du wrapper qui sait refuser le masque des touches au coeur (et se taire vraiment).</summary>
    public const int VersionSansMasque = 345;

    /// <summary>La variable d'environnement que le wrapper 0.345 lit pour refuser le masque.</summary>
    public const string VariableSansMasque = "RETROBAT_INPUT_BITMASKS";

    /// <summary>Les lignes de verrou du manifeste, et la ligne de ses regles (wrapper 0.346) ; vide sans verrou ni regle.</summary>
    public static IReadOnlyList<string> Lignes(ReplayManifest manifest)
    {
        var lignes = manifest.Runtime.Locks is { Length: > 0 } l
            ? l.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();
        if (manifest.Runtime.Rules is { Length: > 0 } r) lignes.Add("rules=" + r);
        return lignes;
    }

    /// <summary>
    /// Le coeur a charger pour lire ce replay et, s'il tient des verrous, la preparation faite (certified.txt ecrit, variable
    /// du vrai coeur posee sur <paramref name="psi"/>). Erreur <see cref="ReplayErrorCode.RuntimeIncompatible"/> si le
    /// wrapper de cette borne ne sait pas tenir les verrous : mieux vaut ne pas lire qu'une lecture qui diverge.
    /// </summary>
    /// <param name="mode">Comment le coeur lisait la manette a l'enregistrement (<see cref="ModeDesEntrees.Lire(string?)"/>).
    /// « Touches » : la lecture passe par le wrapper sans masque ; un wrapper trop ancien laisse le vrai coeur (comme avant).</param>
    public static (string Coeur, ReplayErrorCode Erreur) Preparer(ReplayManifest manifest, string coeurReel, string rom, ProcessStartInfo psi,
        string? dossierDesProxys = null, string? fichierCertifie = null, ModeDesEntrees.Mode mode = ModeDesEntrees.Mode.Inconnu)
    {
        var (coeur, erreur, _) = PreparerLaLecture(manifest, coeurReel, rom, psi, dossierDesProxys, fichierCertifie, mode);
        return (coeur, erreur);
    }

    /// <summary>
    /// Comme <see cref="Preparer"/>, et la ROM a donner a RetroArch. LE NOM QUE LE WRAPPER VOIT (11/11) : RetroArch charge un .zip
    /// de console depuis l'archive et passe au coeur « …\Jeu.zip#Jeu.md » ; le wrapper en tire « Jeu.zip#Jeu », qui n'est pas
    /// le « rom=Jeu » de certified.txt : « pipe=off » ne s'appliquait pas, le wrapper tournait en partie (tube ouvert,
    /// rendu de Streets of Rage 2 a 11 images/s puis arrete). La lecture par le wrapper d'un .zip console passe donc par le
    /// fichier de la ROM, extrait dans <paramref name="dossierDesRoms"/> : son nom est celui du certified.txt. L'arcade garde son
    /// .zip (FBNeo et MAME lisent le set dans l'archive, et le wrapper y voit bien son nom).
    /// </summary>
    public static (string Coeur, ReplayErrorCode Erreur, string Rom) PreparerLaLecture(ReplayManifest manifest, string coeurReel, string rom,
        ProcessStartInfo psi, string? dossierDesProxys = null, string? fichierCertifie = null, ModeDesEntrees.Mode mode = ModeDesEntrees.Mode.Inconnu,
        string? dossierDesRoms = null)
    {
        var lignes = Lignes(manifest);
        var sansMasque = mode == ModeDesEntrees.Mode.Touches;
        if (lignes.Count == 0 && !sansMasque) return (coeurReel, ReplayErrorCode.None, rom);

        var proxys = dossierDesProxys ?? Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "core_proxy");
        var wrapper = Path.Combine(proxys, Path.GetFileName(coeurReel));
        var version = File.Exists(wrapper) ? VersionDuWrapper(wrapper) : 0;
        if (sansMasque && version < VersionSansMasque)
        {
            // Pas de wrapper qui sache refuser le masque : sans verrou, la lecture garde le vrai coeur, comme avant ; un film
            // en sortira faux, et son controle de fidelite le refusera.
            if (lignes.Count == 0) return (coeurReel, ReplayErrorCode.None, rom);
            sansMasque = false;
        }
        if (version < VersionMinimale) return (coeurReel, ReplayErrorCode.RuntimeIncompatible, rom);
        // Des regles demandent le wrapper qui sait les jouer : sans lui la lecture diverge, mieux vaut ne pas lire.
        if (manifest.Runtime.Rules is { Length: > 0 } && version < RetroBat.Api.Speedrun.ReglesDuChallenge.VersionMinimale)
            return (coeurReel, ReplayErrorCode.RuntimeIncompatible, rom);

        var romDeLecture = RomExtraite(manifest, coeurReel, rom,
            dossierDesRoms ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "temp", "rom-de-lecture")) ?? rom;
        var texte = new StringBuilder();
        texte.Append("rom=").Append(Path.GetFileNameWithoutExtension(romDeLecture)).Append('\n');
        texte.Append("pipe=off\n");
        RetroBat.Api.Speedrun.VerrousDuLancement.Ecrire(texte, lignes);
        var chemin = fichierCertifie ?? CertifiedSettingsService.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, texte.ToString(), new UTF8Encoding(false));

        psi.Environment["RETROBAT_LIBRETRO_REAL_CORE"] = coeurReel;
        if (sansMasque) psi.Environment[VariableSansMasque] = "off";
        return (wrapper, ReplayErrorCode.None, romDeLecture);
    }

    /// <summary>
    /// Le fichier de la ROM d'un .zip console, extrait dans <paramref name="dossier"/> (meme contenu, nom de son entree), ou null :
    /// pas un .zip, coeur d'arcade, entree introuvable ou illisible (la lecture garde alors le .zip, comme avant).
    /// </summary>
    internal static string? RomExtraite(ReplayManifest manifest, string coeur, string rom, string dossier)
    {
        if (!string.Equals(Path.GetExtension(rom), ".zip", StringComparison.OrdinalIgnoreCase)) return null;
        var nom = Path.GetFileNameWithoutExtension(coeur).ToLowerInvariant();
        if (nom.Contains("fbneo") || nom.Contains("fbalpha") || nom.Contains("mame")) return null;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(rom);
            var fichiers = zip.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/')).ToList();
            var voulu = manifest.Runtime.RomFiles is { Count: > 0 } r ? r[0].Name : null;
            var entree = (voulu is null ? null : fichiers.FirstOrDefault(e => string.Equals(e.Name, voulu, StringComparison.OrdinalIgnoreCase)))
                ?? (fichiers.Count == 1 ? fichiers[0] : null);
            if (entree is null || entree.Name.Length == 0 || entree.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            Directory.CreateDirectory(dossier);
            var chemin = Path.Combine(dossier, entree.Name);
            if (!File.Exists(chemin) || new FileInfo(chemin).Length != entree.Length) System.IO.Compression.ZipFileExtensions.ExtractToFile(entree, chemin, overwrite: true);
            return chemin;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A la fermeture de la lecture, le certified.txt de lecture disparait : une partie lancee ensuite sur ce jeu ne doit pas
    /// relire « pipe=off » et se taire. Seul un fichier de lecture est efface, jamais celui d'une partie preparee depuis.
    /// </summary>
    public static void EffacerALaFin(ReplayManifest manifest, Process lecture, string? fichierCertifie = null)
    {
        // Une lecture par le wrapper (verrous, ou masque refuse) : son RETROBAT_LIBRETRO_REAL_CORE le dit.
        if (Lignes(manifest).Count == 0 && !lecture.StartInfo.Environment.ContainsKey("RETROBAT_LIBRETRO_REAL_CORE")) return;
        var chemin = fichierCertifie ?? CertifiedSettingsService.FilePath;
        lecture.EnableRaisingEvents = true;
        lecture.Exited += (_, _) => EffacerSiLecture(chemin);
        if (lecture.HasExited) EffacerSiLecture(chemin);
    }

    internal static void EffacerSiLecture(string chemin)
    {
        try
        {
            if (File.Exists(chemin) && File.ReadAllLines(chemin).Contains("pipe=off")) File.Delete(chemin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Le fichier nomme sa ROM : la prochaine preparation d'une partie le reecrit.
        }
    }

    /// <summary>Le numero de version du wrapper (0.343.0.0 -> 343, 1.2.0.0 -> 100002), 0 s'il est illisible.</summary>
    public static int VersionDuWrapper(string chemin)
    {
        try { var v = FileVersionInfo.GetVersionInfo(chemin); return v.FileMajorPart * 100000 + v.FileMinorPart; }
        catch (FileNotFoundException) { return 0; }
    }
}
