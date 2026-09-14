using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Les BIOS d'une partie certifiee, haches pour le passeport.
///
/// Un BIOS modifie peut servir a tricher (Neo-Geo : reglages logiciels, mode debug). Le PROFIL
/// dit quels BIOS le jeu exige (« none » quand il n'en utilise pas) ; la borne n'a aucune liste
/// a elle : elle hache les fichiers que le profil nomme, la ou le coeur les cherche, et le
/// verifieur compare aux empreintes homologuees.
///
/// Le chemin du jeu est retenu au LANCEMENT : a la soumission, l'interface est deja revenue au
/// menu. Un BIOS reecrit apres le lancement ne vaut rien (empreinte nulle), sans quoi on pourrait
/// jouer avec un BIOS modifie et remettre l'original avant l'envoi du passeport.
/// </summary>
public sealed class BiosFingerprintService : IHostedService, IDisposable
{
    /// <summary>Au-dela, ce n'est pas un BIOS : on ne le hache pas pendant une soumission.</summary>
    private const long TailleMax = 128L * 1024 * 1024;

    private readonly IEventBus _bus;
    private readonly ApiContext _context;
    private readonly ILogger<BiosFingerprintService> _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private (string CheminDuJeu, DateTime Debut)? _lancement;

    public BiosFingerprintService(IEventBus bus, ApiContext context, ILogger<BiosFingerprintService> journal)
    {
        _bus = bus;
        _context = context;
        _journal = journal;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal)) return;
            var chemin = (_context.Ui.Running ?? _context.Ui.Selected)?.GamePath;
            lock (_verrou) _lancement = string.IsNullOrEmpty(chemin) ? null : (chemin, DateTime.UtcNow);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// L'objet bios du passeport pour ce profil : { mode: none } si le profil n'exige rien,
    /// sinon { mode: files, files: [{ name, sha256 }] } (sha256 null : absent ou reecrit).
    /// </summary>
    public JsonObject PourLePasseport(JsonElement profil)
    {
        var fichiers = FichiersExiges(profil);
        if (fichiers.Count == 0) return new JsonObject { ["mode"] = "none" };

        (string CheminDuJeu, DateTime Debut)? lancement;
        lock (_verrou) lancement = _lancement;

        var mesures = new JsonArray();
        foreach (var nom in fichiers)
        {
            string? empreinte = null;
            var chemin = Trouver(nom, lancement?.CheminDuJeu);
            if (chemin is not null)
            {
                empreinte = Hacher(chemin, lancement?.Debut);
                _journal.LogInformation("BIOS : {Nom} -> {Chemin} ({Etat}).", nom, chemin, empreinte is null ? "illisible ou reecrit" : "hache");
            }
            else
            {
                _journal.LogInformation("BIOS : {Nom} introuvable.", nom);
            }
            mesures.Add(new JsonObject { ["name"] = nom, ["sha256"] = empreinte });
        }
        return new JsonObject { ["mode"] = "files", ["files"] = mesures };
    }

    /// <summary>Les noms de BIOS exiges par le profil (descripteur de la plateforme).</summary>
    internal static List<string> FichiersExiges(JsonElement profil)
    {
        var noms = new List<string>();
        if (!profil.TryGetProperty("bios", out var bios) || bios.ValueKind != JsonValueKind.Object) return noms;
        if (!bios.TryGetProperty("mode", out var mode) || mode.GetString() != "files") return noms;
        if (!bios.TryGetProperty("files", out var fichiers) || fichiers.ValueKind != JsonValueKind.Array) return noms;
        foreach (var f in fichiers.EnumerateArray())
        {
            if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nom
                && !noms.Contains(nom, StringComparer.OrdinalIgnoreCase))
            {
                noms.Add(nom);
            }
        }
        return noms;
    }

    /// <summary>
    /// Le fichier que le coeur charge, dans son ordre de recherche : a cote de la ROM, puis
    /// bios/fbneo, puis bios, puis bios/mame (ou RetroBat range les BIOS de MAME). Un nom qui
    /// descend hors de ces dossiers n'est jamais suivi.
    /// </summary>
    internal static string? Trouver(string nom, string? cheminDuJeu, string? racineBios = null)
    {
        var relatif = nom.Replace('\\', '/').TrimStart('/');
        if (relatif.Length == 0 || relatif.Split('/').Any(s => s is "" or "." or "..")) return null;
        var bios = racineBios ?? Path.Combine(RetroBatPaths.RetroBatRoot, "bios");

        var dossiers = new List<string>();
        if (!string.IsNullOrEmpty(cheminDuJeu) && Path.GetDirectoryName(cheminDuJeu) is { Length: > 0 } dossierDuJeu) dossiers.Add(dossierDuJeu);
        dossiers.Add(Path.Combine(bios, "fbneo"));
        dossiers.Add(bios);
        dossiers.Add(Path.Combine(bios, "mame"));

        foreach (var dossier in dossiers)
        {
            var candidat = Path.GetFullPath(Path.Combine(dossier, relatif));
            if (!candidat.StartsWith(Path.GetFullPath(dossier), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidat)) return candidat;
        }
        return null;
    }

    private static string? Hacher(string chemin, DateTime? debut)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists || info.Length > TailleMax) return null;
            // Reecrit pendant ou apres la partie : ce n'est pas ce que le coeur a charge.
            if (debut is not null && info.LastWriteTimeUtc > debut.Value) return null;
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(flux)).ToLowerInvariant();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _abonnement?.Dispose();
}
