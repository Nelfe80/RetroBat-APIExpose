using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

public sealed record ReplayPlayedGame(string RomGroup, string SystemId, DateTime LastPlayedUtc);

public sealed record ReplayPlayedGamesDoc(string Schema, IReadOnlyList<ReplayPlayedGame> Games);

/// <summary>
/// Les jeux que CETTE borne joue vraiment (CDC DEV §101.8, préchargement).
///
/// L'agent de réplication savait déjà quoi garder pour l'essaim : les classements que le
/// propriétaire a explicitement suivis. Il ne savait pas quoi garder pour LUI. Or les deux
/// questions sont différentes : on suit un classement par militantisme de préservation, on
/// regarde un replay parce qu'on vient de jouer au jeu.
///
/// D'où cette liste, qui répond à la seconde. Elle se remplit toute seule à chaque partie
/// certifiée, garde les plus récents, et sert de cible au préchargement : quand quelqu'un
/// termine une partie de 19xx et va voir le record du monde, l'objet est déjà là.
///
/// Ce n'est PAS un contournement du consentement. Le préchargement reste sous le même
/// interrupteur que la réplication et sous le même budget ; cette liste change seulement QUELS
/// objets sont choisis, jamais SI la machine télécharge.
///
/// Le fichier vit dans <c>state/</c>, hors git : ce que quelqu'un joue chez lui ne regarde ni le
/// dépôt ni personne d'autre.
/// </summary>
public sealed class ReplayPlayedGamesStore
{
    /// <summary>Au-delà, ce n'est plus « ce que je joue », c'est un historique. Le préchargement
    /// vise le geste d'après, pas la collection complète.</summary>
    private const int MaxJeux = 8;

    /// <summary>Un jeu auquel on n'a pas touché depuis un mois n'annonce plus rien.</summary>
    private static readonly TimeSpan Peremption = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<ReplayPlayedGamesStore> _logger;
    private readonly object _gate = new();

    public ReplayPlayedGamesStore(ILogger<ReplayPlayedGamesStore> logger) => _logger = logger;

    public string Path => System.IO.Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "played.json");

    /// <summary>Les jeux récents, du plus récent au plus ancien, les périmés écartés.</summary>
    public IReadOnlyList<ReplayPlayedGame> Games
    {
        get
        {
            lock (_gate)
            {
                var doc = Lire();
                var limite = DateTime.UtcNow - Peremption;
                return doc.Games
                    .Where(g => g.LastPlayedUtc >= limite && g.RomGroup.Length > 0)
                    .OrderByDescending(g => g.LastPlayedUtc)
                    .Take(MaxJeux)
                    .ToList();
            }
        }
    }

    /// <summary>Note qu'on vient de jouer à ce jeu. Idempotent : rejouer remonte la date.</summary>
    public void Remember(string? romGroup, string? systemId)
    {
        var rom = (romGroup ?? string.Empty).Trim();
        if (rom.Length == 0) return;

        lock (_gate)
        {
            var doc = Lire();
            var garde = doc.Games
                .Where(g => !string.Equals(g.RomGroup, rom, StringComparison.OrdinalIgnoreCase))
                .ToList();
            garde.Add(new ReplayPlayedGame(rom, (systemId ?? string.Empty).Trim(), DateTime.UtcNow));

            var sortie = garde
                .OrderByDescending(g => g.LastPlayedUtc)
                .Take(MaxJeux)
                .ToList();

            try
            {
                var dossier = System.IO.Path.GetDirectoryName(Path)!;
                Directory.CreateDirectory(dossier);
                // Atomique : un passage de l'agent ne doit jamais lire un fichier à moitié écrit.
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(
                    new ReplayPlayedGamesDoc("nelfe.replay.played.v1", sortie), Json));
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : liste des jeux joués non écrite."); }
        }
    }

    private ReplayPlayedGamesDoc Lire()
    {
        try
        {
            if (File.Exists(Path))
            {
                var doc = JsonSerializer.Deserialize<ReplayPlayedGamesDoc>(File.ReadAllText(Path), Json);
                if (doc?.Games is not null) return doc;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : liste des jeux joués illisible."); }
        return new ReplayPlayedGamesDoc("nelfe.replay.played.v1", Array.Empty<ReplayPlayedGame>());
    }
}
