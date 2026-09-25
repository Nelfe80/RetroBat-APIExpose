using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Les fichiers NVRAM du jeu (EEPROM, RAM de sauvegarde), en octets BRUTS, pour le passeport.
///
/// Les jeux sans DIP switches (CPS-2, Neo-Geo...) gardent difficulte et vies dans leur EEPROM :
/// l'epinglage des options du coeur ne les voit pas. La borne n'a AUCUNE connaissance par jeu :
/// elle joint les octets, et c'est le profil homologue qui dit, par des plages calibrees, ou sont
/// les reglages. Un octet qui n'est pas dans une plage (un compteur de parties) peut bouger.
///
/// DEUX lectures, et leur moment n'est pas un detail :
///   - au LANCEMENT : ce que le coeur va charger ;
///   - apres la FERMETURE de l'emulateur : le wrapper emet sa session AVANT le retro_unload_game
///     du vrai coeur, et c'est dans celui-ci que FBNeo reecrit l'EEPROM. Lire a la reception de la
///     session donnerait l'ancienne version, et un passage par le menu de service pendant la
///     partie passerait inapercu.
/// </summary>
public sealed class NvramSnapshotService : IHostedService, IDisposable
{
    /// <summary>Les extensions de NVRAM connues des coeurs arcade (FBNeo : .nv, et .fs pour la RAM de sauvegarde Neo-Geo ; MAME : .nv, .nvram).</summary>
    private static readonly string[] Extensions = [".nv", ".nvram", ".eeprom", ".fs"];

    /// <summary>Au-dela, ce n'est plus une EEPROM de reglages : on ne l'embarque pas dans un passeport.</summary>
    private const int TailleMax = 65536;

    private readonly IEventBus _bus;
    private readonly ApiContext _context;
    private readonly ILogger<NvramSnapshotService> _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private List<(string Relatif, string Chemin, byte[] Debut)> _lancement = new();
    private string _systeme = "";
    private string _rom = "";

    public NvramSnapshotService(IEventBus bus, ApiContext context, ILogger<NvramSnapshotService> journal)
    {
        _bus = bus;
        _context = context;
        _journal = journal;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(e =>
        {
            if (string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal)) CapturerLeLancement();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Lit les NVRAM du jeu qui demarre, avant que le coeur ne les charge.</summary>
    private void CapturerLeLancement()
    {
        var trouves = new List<(string, string, byte[])>();
        try
        {
            var jeu = _context.Ui.Running ?? _context.Ui.Selected;
            var systeme = jeu?.SystemId ?? "";
            var rom = Path.GetFileNameWithoutExtension(jeu?.GamePath ?? "");
            foreach (var chemin in Chercher(systeme, rom))
            {
                var octets = Lire(chemin);
                if (octets is not null) trouves.Add((Relatif(chemin), chemin, octets));
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "NVRAM : capture du lancement impossible.");
        }
        lock (_verrou)
        {
            _lancement = trouves;
            var jeu = _context.Ui.Running ?? _context.Ui.Selected;
            _systeme = jeu?.SystemId ?? "";
            _rom = Path.GetFileNameWithoutExtension(jeu?.GamePath ?? "");
        }
        if (trouves.Count > 0)
        {
            _journal.LogInformation("NVRAM : {Combien} fichier(s) lu(s) au lancement ({Fichiers}).",
                trouves.Count, string.Join(", ", trouves.Select(t => t.Item1)));
        }
    }

    /// <summary>
    /// Les entrees NVRAM du passeport : octets du lancement et octets APRES la fermeture de
    /// l'emulateur. Null si le jeu n'a pas de NVRAM (le passeport reste alors inchange).
    /// </summary>
    public async Task<JsonArray?> PourLePasseportAsync(CancellationToken ct) => await PourLePasseportAsync(null, ct).ConfigureAwait(false);

    /// <summary>
    /// Idem, avec les NVRAM que le profil epingle. Celles-ci sont effacees avant la partie : absentes
    /// au lancement, elles n'apparaitraient pas dans le passeport, et le serveur refuserait faute de
    /// fichier. On les joint donc depart VIDE (c'est la verite : il n'y avait rien) et fin lue apres
    /// la fermeture. Puis on les efface de nouveau, pour que la partie suivante parte vierge elle aussi.
    /// </summary>
    public async Task<JsonArray?> PourLePasseportAsync(IReadOnlyList<string>? epingles, CancellationToken ct)
    {
        List<(string Relatif, string Chemin, byte[] Debut)> lancement;
        string systeme, rom;
        lock (_verrou)
        {
            lancement = _lancement;
            systeme = _systeme;
            rom = _rom;
        }
        var pins = epingles ?? Array.Empty<string>();
        if (lancement.Count == 0 && pins.Count == 0) return null;

        // FBNeo reecrit l'EEPROM dans retro_unload_game, APRES l'emission de la session : on
        // attend que l'emulateur soit parti pour lire la version finale.
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < limite && EmulatorForeground.EmulateurTourne() && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        var entrees = new JsonArray();
        foreach (var (relatif, chemin, debut) in lancement)
        {
            var fin = Lire(chemin) ?? Array.Empty<byte>();
            entrees.Add(new JsonObject
            {
                ["file"] = relatif,
                ["size"] = debut.Length,
                ["start"] = Convert.ToBase64String(debut),
                ["end"] = Convert.ToBase64String(fin),
            });
        }
        foreach (var epingle in pins)
        {
            if (!SystemeSur(systeme)) break;
            var canonique = Canonique(systeme, epingle);
            if (lancement.Any(l => string.Equals(l.Chemin, canonique, StringComparison.OrdinalIgnoreCase))) continue;
            var fin = Lire(canonique) ?? Array.Empty<byte>();
            entrees.Add(new JsonObject
            {
                ["file"] = Relatif(canonique),
                ["size"] = 0,
                ["start"] = "",
                ["end"] = Convert.ToBase64String(fin),
            });
        }
        lock (_verrou) _lancement = new();
        if (pins.Count > 0) EffacerEpinglees(systeme, rom, pins);
        return entrees.Count > 0 ? entrees : null;
    }

    /// <summary>
    /// Efface la NVRAM epinglee de ce jeu : la partie suivante part d'une EEPROM vierge, que le jeu
    /// initialise lui-meme a ses reglages d'usine, c'est-a-dire l'etat que le profil epingle.
    ///
    /// Premier motif de refus avant la 1.9.2 (19xx, 2026-09-25) : une EEPROM qui n'etait plus d'usine
    /// (menu de service, lecture d'un replay) faisait refuser TOUTES les parties suivantes. Le fichier
    /// ne porte que les reglages de la borne ; les records sont dans le .hi, qui n'est pas touche.
    /// Jamais pendant qu'un emulateur tourne.
    /// </summary>
    public int EffacerEpinglees(string systeme, string rom, IReadOnlyList<string> epingles)
    {
        if (epingles.Count == 0 || EmulatorForeground.EmulateurTourne()) return 0;
        var effaces = 0;
        foreach (var chemin in CopiesEpinglees(systeme, rom, epingles))
        {
            try
            {
                File.Delete(chemin);
                effaces++;
                _journal.LogInformation("NVRAM : {Fichier} effacee, la prochaine partie part des reglages d'usine.", Relatif(chemin));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _journal.LogWarning(ex, "NVRAM : {Fichier} n'a pas pu etre effacee.", Relatif(chemin));
            }
        }
        return effaces;
    }

    /// <summary>Les NVRAM que le profil epingle (« fbneo/19xx.nv », « nvram/19xx/eeprom »), lues dans sa reponse.</summary>
    internal static IReadOnlyList<string> EpinglesDuProfil(JsonElement? profil)
    {
        var liste = new List<string>();
        if (profil is not { ValueKind: JsonValueKind.Object } p) return liste;
        var racine = p.TryGetProperty("profile", out var interne) && interne.ValueKind == JsonValueKind.Object ? interne : p;
        if (!racine.TryGetProperty("nvram_pins", out var pins) || pins.ValueKind != JsonValueKind.Array) return liste;
        foreach (var pin in pins.EnumerateArray())
        {
            if (pin.ValueKind != JsonValueKind.Object || !pin.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String) continue;
            if (EpingleSure(f.GetString()) is { } sure && !liste.Contains(sure, StringComparer.OrdinalIgnoreCase)) liste.Add(sure);
        }
        return liste;
    }

    /// <summary>Un chemin relatif, sans remontee ni racine : une epingle ne designe jamais rien hors des sauvegardes.</summary>
    internal static string? EpingleSure(string? epingle)
    {
        var e = (epingle ?? "").Replace('\\', '/').Trim().Trim('/');
        if (e.Length == 0 || e.Contains("..", StringComparison.Ordinal) || e.Contains(':') || Path.IsPathRooted(e)) return null;
        return e;
    }

    private static bool SystemeSur(string systeme) => systeme.Length > 0 && Path.GetFileName(systeme) == systeme;

    /// <summary>
    /// La ou le coeur range cette NVRAM : saves/mame/nvram/... pour MAME autonome, un dossier par jeu
    /// quel que soit le systeme ; saves/&lt;systeme&gt;/&lt;epingle&gt; sinon (FBNeo : saves/fbneo/fbneo/19xx.nv).
    /// C'est aussi ce chemin que le serveur juge quand plusieurs copies sont jointes.
    /// </summary>
    internal static string Canonique(string systeme, string epingle, string? racineSaves = null)
    {
        var saves = racineSaves ?? RetroBatPaths.SavesRoot;
        var relatif = epingle.Replace('/', Path.DirectorySeparatorChar);
        return epingle.StartsWith("nvram/", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(saves, "mame", relatif)
            : Path.Combine(saves, systeme, relatif);
    }

    /// <summary>Toutes les copies de ces NVRAM epinglees pour ce jeu : celles que la recherche trouve, et l'emplacement du coeur.</summary>
    internal static List<string> CopiesEpinglees(string systeme, string rom, IReadOnlyList<string> epingles, string? racineSaves = null)
    {
        var copies = new List<string>();
        if (epingles.Count == 0 || rom.Length == 0 || !SystemeSur(systeme)) return copies;
        foreach (var chemin in Chercher(systeme, rom, racineSaves))
        {
            var normal = chemin.Replace('\\', '/');
            if (epingles.Any(e => normal.EndsWith("/" + e, StringComparison.OrdinalIgnoreCase))
                && !copies.Contains(chemin, StringComparer.OrdinalIgnoreCase)) copies.Add(chemin);
        }
        foreach (var epingle in epingles)
        {
            var canonique = Canonique(systeme, epingle, racineSaves);
            if (File.Exists(canonique) && !copies.Contains(canonique, StringComparer.OrdinalIgnoreCase)) copies.Add(canonique);
        }
        return copies;
    }

    /// <summary>
    /// Les NVRAM de ce jeu : dans le dossier de sauvegarde de son systeme (coeur compris), et dans
    /// celui de MAME autonome, qui range un DOSSIER par jeu (saves/mame/nvram/19xx/eeprom) quel que
    /// soit le systeme du lancement. Joindre les deux ne coute rien : le profil limite chaque
    /// epingle au coeur qui l'ecrit.
    /// </summary>
    internal static IEnumerable<string> Chercher(string systeme, string rom, string? racineSaves = null)
    {
        var saves = racineSaves ?? RetroBatPaths.SavesRoot;
        if (rom.Length == 0 || Path.GetFileName(rom) != rom) yield break;
        if (systeme.Length > 0 && Path.GetFileName(systeme) == systeme)
        {
            var racine = Path.Combine(saves, systeme);
            if (Directory.Exists(racine))
            {
                foreach (var chemin in Directory.EnumerateFiles(racine, rom + ".*", SearchOption.AllDirectories))
                {
                    if (Extensions.Contains(Path.GetExtension(chemin), StringComparer.OrdinalIgnoreCase)) yield return chemin;
                }
            }
        }
        var mame = Path.Combine(saves, "mame", "nvram", rom);
        if (Directory.Exists(mame))
        {
            foreach (var chemin in Directory.EnumerateFiles(mame, "*", SearchOption.TopDirectoryOnly)) yield return chemin;
        }
    }

    private static string Relatif(string chemin)
        => ("saves/" + Path.GetRelativePath(RetroBatPaths.SavesRoot, chemin)).Replace('\\', '/');

    private static byte[]? Lire(string chemin)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists || info.Length > TailleMax) return null;
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var octets = new byte[info.Length];
            var lu = 0;
            while (lu < octets.Length)
            {
                var n = flux.Read(octets, lu, octets.Length - lu);
                if (n == 0) break;
                lu += n;
            }
            return lu == octets.Length ? octets : octets[..lu];
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _abonnement?.Dispose();
}
