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
        lock (_verrou) _lancement = trouves;
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
    public async Task<JsonArray?> PourLePasseportAsync(CancellationToken ct)
    {
        List<(string Relatif, string Chemin, byte[] Debut)> lancement;
        lock (_verrou) lancement = _lancement;
        if (lancement.Count == 0) return null;

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
        lock (_verrou) _lancement = new();
        return entrees;
    }

    /// <summary>Les NVRAM de ce jeu dans le dossier de sauvegarde de son systeme (coeur compris).</summary>
    internal static IEnumerable<string> Chercher(string systeme, string rom)
    {
        if (systeme.Length == 0 || rom.Length == 0 || Path.GetFileName(systeme) != systeme) yield break;
        var racine = Path.Combine(RetroBatPaths.SavesRoot, systeme);
        if (!Directory.Exists(racine)) yield break;
        foreach (var chemin in Directory.EnumerateFiles(racine, rom + ".*", SearchOption.AllDirectories))
        {
            if (Extensions.Contains(Path.GetExtension(chemin), StringComparer.OrdinalIgnoreCase)) yield return chemin;
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
