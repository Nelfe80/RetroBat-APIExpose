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

    /// <summary>Les lignes de verrou du manifeste, vide sans verrou.</summary>
    public static IReadOnlyList<string> Lignes(ReplayManifest manifest)
        => manifest.Runtime.Locks is { Length: > 0 } l
            ? l.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

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
        var lignes = Lignes(manifest);
        var sansMasque = mode == ModeDesEntrees.Mode.Touches;
        if (lignes.Count == 0 && !sansMasque) return (coeurReel, ReplayErrorCode.None);

        var proxys = dossierDesProxys ?? Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "core_proxy");
        var wrapper = Path.Combine(proxys, Path.GetFileName(coeurReel));
        var version = File.Exists(wrapper) ? VersionDuWrapper(wrapper) : 0;
        if (sansMasque && version < VersionSansMasque)
        {
            // Pas de wrapper qui sache refuser le masque : sans verrou, la lecture garde le vrai coeur, comme avant ; un film
            // en sortira faux, et son controle de fidelite le refusera.
            if (lignes.Count == 0) return (coeurReel, ReplayErrorCode.None);
            sansMasque = false;
        }
        if (version < VersionMinimale) return (coeurReel, ReplayErrorCode.RuntimeIncompatible);

        var texte = new StringBuilder();
        texte.Append("rom=").Append(Path.GetFileNameWithoutExtension(rom)).Append('\n');
        texte.Append("pipe=off\n");
        RetroBat.Api.Speedrun.VerrousDuLancement.Ecrire(texte, lignes);
        var chemin = fichierCertifie ?? CertifiedSettingsService.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, texte.ToString(), new UTF8Encoding(false));

        psi.Environment["RETROBAT_LIBRETRO_REAL_CORE"] = coeurReel;
        if (sansMasque) psi.Environment[VariableSansMasque] = "off";
        return (wrapper, ReplayErrorCode.None);
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
