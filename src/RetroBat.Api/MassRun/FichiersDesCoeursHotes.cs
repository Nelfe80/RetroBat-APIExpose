using RetroBat.Domain.Paths;

namespace RetroBat.Api.MassRun;

/// <summary>
/// CE QUE LES COEURS HOTES LISENT DANS LE DOSSIER SYSTEME (05/11), tire du Data Pack avant un lancement :
/// <list type="bullet">
/// <item>les PROFILS DES JEUX (un coeur par emulateur, decision du user du 05/11) : <c>resources/challenges/profils/&lt;crc32&gt;.ini</c>
/// vers <c>bios/nelfeplay/profils/</c> ; un jeu s'ajoute sans recompiler le coeur ;</item>
/// <item>le DICTIONNAIRE DES POSES d'un jeu Mega Drive ou SNES en MASS RUN : <c>resources/challenges/states/&lt;systeme&gt;/&lt;groupe&gt;/poses.mdposes</c>
/// vers <c>bios/nelfeplay/massrun/&lt;crc32&gt;/poses-datapack.mdposes</c> (le coeur lit celui-la d'abord, puis le sien, appris en jouant).</item>
/// </list>
/// Un fichier identique n'est pas recopie ; une erreur d'ecriture n'empeche pas le lancement (le coeur garde ce qu'il a).
/// </summary>
public static class FichiersDesCoeursHotes
{
    public static string RacineDesProfils => Path.Combine(RetroBatPaths.PluginRoot, "resources", "challenges", "profils");

    public static string Systeme => Path.Combine(RetroBatPaths.RetroBatRoot, "bios");

    /// <summary>Les profils des jeux ; rend le nombre de fichiers recopies.</summary>
    public static int PoserLesProfils(string? source = null, string? systeme = null)
    {
        var dossier = source ?? RacineDesProfils;
        if (!Directory.Exists(dossier)) return 0;
        var cible = Path.Combine(systeme ?? Systeme, "nelfeplay", "profils");
        var n = 0;
        foreach (var fichier in Directory.EnumerateFiles(dossier, "*.ini"))
        {
            var nom = Path.GetFileName(fichier);
            if (!System.Text.RegularExpressions.Regex.IsMatch(nom, "^[0-9a-f]{8}\\.ini$")) continue;
            if (Copier(fichier, Path.Combine(cible, nom))) n++;
        }
        return n;
    }

    /// <summary>
    /// Le dictionnaire des poses du jeu, s'il y en a un au Data Pack ; vrai s'il a ete recopie. Mega Drive : « poses.mdposes »
    /// (une image par numero d'image de Sonic) ; SNES (06/11) : « poses.snposes » (une image par empreinte).
    /// </summary>
    public static bool PoserLesPoses(JeuxMassRun.Jeu jeu, string? racineDesEtats = null, string? systeme = null)
    {
        var extension = jeu.PosesEnImages ? "snposes" : "mdposes";
        var source = Path.Combine(racineDesEtats ?? RetroBat.Api.Speedrun.EtatsDesDefis.Racine, jeu.Systeme, jeu.RomGroup, "poses." + extension);
        if (!File.Exists(source)) return false;
        return Copier(source, Path.Combine(systeme ?? Systeme, "nelfeplay", "massrun", jeu.Crc.ToString("x8"), "poses-datapack." + extension));
    }

    private static bool Copier(string source, string cible)
    {
        try
        {
            if (File.Exists(cible) && new FileInfo(cible).Length == new FileInfo(source).Length
                && File.ReadAllBytes(cible).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
            var temporaire = cible + ".tmp";
            File.Copy(source, temporaire, true);
            File.Move(temporaire, cible, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
