using System.Diagnostics;
using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LA DESINSTALLATION PROPRE (demande du user, 2026-10-17 : « rendre le desinstalleur complet, garde les medias et la
/// clef de la borne »). Le desinstalleur d'Inno ne retirait que ses fichiers, le lancement avec ES, le menu d'ES et le
/// montage core_proxy : la collection World Scoring, ses images dans les themes, les reglages d'APIExpose dans
/// es_settings.cfg et toutes les donnees du dossier restaient. Il lance maintenant <c>RetroBat.Api.exe --desinstaller</c>
/// avant de supprimer ses fichiers. Ce mode, sans demarrer l'API :
///
///   1. annule RetroArch NelfePlay s'il est installe (son desinstalleur, sans refus), puis retire le lancement avec ES, le
///      menu d'ES (es_features_apiexpose.cfg) et le montage core_proxy, comme le desinstalleur d'Inno (une borne installee
///      depuis une archive n'a pas de desinstalleur, celui d'une ancienne installation ne lance pas ce mode :
///      desinstaller-apiexpose.bat, a la racine, enchaine tout), et les dossiers d'APIExpose a la racine des themes
///      (.panels, .gameinfos, .medias) ;
///   2. retire la collection World Scoring (fichier custom-*.cfg si c'est le notre, son nom dans CollectionSystemsCustom),
///      ses logos et fonds dans les themes et ses lignes de collections.info, selon les manifestes de propriete ;
///   3. retire les reglages global.apiexpose.* d'es_settings.cfg ;
///   4. vide le dossier du plugin, sauf ce qui se garde (<see cref="Gardes"/>).
///
/// CE QUI SE GARDE : les medias (les gamelists de RetroBat y pointent : les supprimer viderait leurs images), l'identite
/// de la borne (state/nelfeplay/device.json ; la cle Windows NelfePlay.DeviceKey, elle, n'est jamais touchee) et les
/// replays du joueur. Une reinstallation retrouve la meme borne, ses medias et ses replays. Les gamelists ne sont pas
/// modifiees. Le journal va dans %TEMP%, le dossier du plugin disparaissant.
/// </summary>
public static class Desinstallation
{
    public const string Argument = "--desinstaller";

    /// <summary>Chemins relatifs au dossier du plugin qui restent (un dossier garde tout ce qu'il contient).</summary>
    public static readonly string[] Gardes = ["media", "state/nelfeplay/device.json", "state/nelfenet/replay"];

    /// <summary>
    /// Les fichiers du desinstalleur d'Inno (il les retire lui-meme a la fin) et le .bat qui lance ce mode : il s'efface en
    /// dernier, une fois l'exe sorti.
    /// </summary>
    private static readonly string[] DuDesinstalleur = ["unins*.exe", "unins*.dat", "unins*.msg", "desinstaller-apiexpose.bat"];

    /// <summary>
    /// Les fichiers a supprimer, relatifs au dossier du plugin (separateur « / ») : tout, sauf ce qui se garde, les
    /// fichiers du desinstalleur et l'exe qui tourne (<paramref name="exeEnCours"/>, nom de fichier). Pur sur l'arborescence.
    /// </summary>
    internal static IReadOnlyList<string> ASupprimer(string racine, IReadOnlyCollection<string> gardes, string? exeEnCours)
    {
        var sortie = new List<string>();
        var gardesNormalises = gardes.Select(g => g.Replace('\\', '/').Trim('/')).Where(g => g.Length > 0).ToList();
        foreach (var fichier in Directory.EnumerateFiles(racine, "*", SearchOption.AllDirectories))
        {
            var relatif = Path.GetRelativePath(racine, fichier).Replace('\\', '/');
            if (gardesNormalises.Any(g => relatif.Equals(g, StringComparison.OrdinalIgnoreCase)
                    || relatif.StartsWith(g + "/", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var nom = Path.GetFileName(relatif);
            if (!relatif.Contains('/') && DuDesinstalleur.Any(motif => EstDuMotif(nom, motif))) continue;
            if (!relatif.Contains('/') && exeEnCours is not null && nom.Equals(exeEnCours, StringComparison.OrdinalIgnoreCase)) continue;
            sortie.Add(relatif);
        }
        return sortie;
    }

    private static bool EstDuMotif(string nom, string motif)
    {
        var etoile = motif.IndexOf('*');
        return etoile < 0 ? nom.Equals(motif, StringComparison.OrdinalIgnoreCase)
            : nom.StartsWith(motif[..etoile], StringComparison.OrdinalIgnoreCase)
              && nom.EndsWith(motif[(etoile + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Le dossier est-il bien celui du plugin : nomme APIExpose, sous un dossier plugins ? Pur.</summary>
    internal static bool DossierDuPlugin(string racine)
    {
        var dossier = new DirectoryInfo(racine.TrimEnd('\\', '/'));
        return dossier.Name.Equals("APIExpose", StringComparison.OrdinalIgnoreCase)
               && dossier.Parent is { } parent && parent.Name.Equals("plugins", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retire d'es_settings.cfg chaque reglage dont le nom commence par global.apiexpose. Pur sur le document.</summary>
    internal static bool RetirerLesReglages(XDocument document)
    {
        var aRetirer = document.Descendants()
            .Where(e => ((string?)e.Attribute("name") ?? "").StartsWith("global.apiexpose.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var e in aRetirer)
        {
            // Le blanc qui precedait la ligne part avec elle : pas de lignes vides dans le fichier d'ES.
            if (e.PreviousNode is XText blanc && string.IsNullOrWhiteSpace(blanc.Value)) blanc.Remove();
            e.Remove();
        }
        return aRetirer.Count > 0;
    }

    public static int Executer()
    {
        var cheminJournal = Path.Combine(Path.GetTempPath(), "apiexpose-desinstallation.log");
        using var journal = new StreamWriter(cheminJournal, append: true) { AutoFlush = true };
        void Dire(string ligne)
        {
            var l = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ligne;
            journal.WriteLine(l);
            Console.WriteLine(l);
        }

        var racine = RetroBatPaths.PluginRoot;
        Dire("desinstallation d'APIExpose : " + racine);
        // JAMAIS AILLEURS QUE DANS <RetroBat>\plugins\APIExpose : le dossier se vide, une racine mal trouvee viderait autre
        // chose (le dossier courant, RetroBat entier).
        if (!DossierDuPlugin(racine))
        {
            Dire("REFUS : " + racine + " n'est pas un dossier plugins\\APIExpose ; rien n'est touche.");
            return 2;
        }
        if (Process.GetProcessesByName("emulationstation").Length > 0)
        {
            Dire("ATTENTION : EmulationStation tourne ; il reecrira es_settings.cfg en quittant (la collection peut revenir dans la liste).");
        }

        // 1. RetroArch NelfePlay : son desinstalleur remet le RetroArch de RetroBat (sans poser de refus).
        Etape(Dire, "RetroArch NelfePlay", () =>
        {
            var retroarch = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");
            var bat = Path.Combine(racine, RetroArchNelfePlay.Desinstalleur);
            if (!File.Exists(Path.Combine(retroarch, RetroArchNelfePlay.Marque))) return "pas installe";
            if (!File.Exists(bat)) return "installe, mais " + RetroArchNelfePlay.Desinstalleur + " manque";
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" /silencieux /annulation\"")
            {
                WorkingDirectory = racine, CreateNoWindow = true, UseShellExecute = false,
            });
            if (p is null) return "desinstalleur non lance";
            return p.WaitForExit(180_000) ? "remis d'origine (code " + p.ExitCode + ")" : "desinstalleur trop long, abandonne";
        });

        // 1 bis. Les branchements dans RetroBat, que le desinstalleur d'Inno retire aussi : une borne installee depuis une
        // archive n'en a pas, et celui d'une ancienne installation ne connait pas cette etape.
        Etape(Dire, "branchements dans RetroBat", () =>
        {
            var faits = new List<string>();
            var hook = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "scripts", "start", "APIExpose-start-wait.bat");
            if (File.Exists(hook)) { File.Delete(hook); faits.Add("lancement avec ES"); }
            var menu = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_features_apiexpose.cfg");
            if (File.Exists(menu)) { File.Delete(menu); faits.Add("menu d'ES"); }
            var proxy = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "core_proxy");
            if (Directory.Exists(proxy)) { Directory.Delete(proxy, true); faits.Add("core_proxy"); }
            return faits.Count == 0 ? "aucun" : string.Join(", ", faits) + " retire(s)";
        });

        // 1 ter. Les dossiers d'APIExpose a la racine des themes (fiches de jeux, panneaux de commandes, images d'attente),
        // generes par l'API : aucune gamelist ni aucun theme ne les reference.
        Etape(Dire, "donnees d'APIExpose dans les themes", () =>
        {
            var faits = new List<string>();
            foreach (var dossier in new[]
                     {
                         RetroBatPaths.EmulationStationPanelsThemeRoot, RetroBatPaths.EmulationStationGameInfosThemeRoot,
                         RetroBatPaths.EmulationStationThemeMediasRoot, RetroBatPaths.EmulationStationThemeMediaRoot,
                     })
            {
                if (!Directory.Exists(dossier)) continue;
                Directory.Delete(dossier, true);
                faits.Add(Path.GetFileName(dossier));
            }
            return faits.Count == 0 ? "aucune" : string.Join(", ", faits) + " retire(s)";
        });

        // 2. La collection World Scoring, ses images et ses lignes de collections.info : seulement ce qui est a nous.
        Etape(Dire, "collection World Scoring", () =>
        {
            var collection = new EsCustomCollectionWriter(new EsSettingsStore()).Remove(NelfePlayScoringCollectionSyncService.CollectionName);
            var images = new EsCollectionThemeAssets().Remove(NelfePlayScoringCollectionSyncService.CollectionName);
            return (collection.Changed ? "retiree" : "absente") + ", images des themes " + (images.Changed ? "retirees" : "absentes");
        });

        // 3. Les reglages d'APIExpose dans es_settings.cfg.
        Etape(Dire, "reglages global.apiexpose.*", () =>
            new EsSettingsStore().Update(RetirerLesReglages) ? "retires d'es_settings.cfg" : "aucun");

        // 4. Le dossier du plugin, sauf les medias, l'identite de la borne et les replays.
        Etape(Dire, "dossier du plugin", () =>
        {
            var exe = Path.GetFileName(Environment.ProcessPath);
            var fichiers = ASupprimer(racine, Gardes, exe);
            var supprimes = 0;
            var restes = 0;
            foreach (var relatif in fichiers)
            {
                try
                {
                    var chemin = Path.Combine(racine, relatif);
                    File.SetAttributes(chemin, FileAttributes.Normal);
                    File.Delete(chemin);
                    supprimes++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    restes++;
                }
            }
            // Les dossiers vides, des plus profonds aux moins profonds ; jamais un dossier garde ni la racine.
            foreach (var dossier in Directory.EnumerateDirectories(racine, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dossier).Any()) Directory.Delete(dossier);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Un dossier encore ouvert : il reste.
                }
            }
            return supprimes + " fichier(s) supprime(s)" + (restes > 0 ? ", " + restes + " en usage laisse(s)" : "")
                + " ; gardes : " + string.Join(", ", Gardes);
        });

        Dire("desinstallation terminee ; journal : " + cheminJournal);
        return 0;
    }

    private static void Etape(Action<string> dire, string nom, Func<string> faire)
    {
        try
        {
            dire(nom + " : " + faire());
        }
        catch (Exception ex)
        {
            dire(nom + " : ECHEC (" + ex.GetType().Name + " : " + ex.Message + ")");
        }
    }
}
