using System.Globalization;
using System.Text.Json;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LES PARTIES DE CETTE BORNE, pour l'onglet MES RECORDS (demande user 2026-10-03) : chaque partie
/// que la borne a soumise et que la plateforme a retenue, du meilleur score au moins bon, avec sa
/// date et son replay local quand la borne l'a enregistre. Le classement mondial ne donnait que le
/// meilleur score du joueur ; ses autres parties n'apparaissaient nulle part.
///
/// La source est le passeport garde par le rapporteur dans state/nelfeplay/certified, un fichier
/// par partie soumise. Chaque fichier n'est lu qu'une fois : on garde son resume, et on ne relit
/// que ceux qui ont change (le dossier passe vite plusieurs centaines de fichiers, 10 Mo sur la
/// borne de reference).
/// </summary>
public sealed class LocalPlaysIndex
{
    /// <summary>Ce qu'on garde d'un passeport.</summary>
    public sealed record Partie(
        string SessionId,
        string RomGroup,
        string Regle,
        long Score,
        bool PlusBasEstMieux,
        DateTime DebutUtc,
        DateTime FinUtc,
        string Verdict,
        string Monde,
        string JoueurDeSession,
        bool Labo);

    /// <summary>Un replay enregistre sur cette borne : sa fin, sa duree, et le score qui lui a ete attache.</summary>
    public sealed record ReplayLocal(string ReplayId, DateTime FinUtc, TimeSpan Duree, long? Score);

    private readonly string _dossier;
    private readonly object _verrou = new();
    private readonly Dictionary<string, (DateTime Ecrit, Partie? Partie)> _resumes = new(StringComparer.OrdinalIgnoreCase);

    public LocalPlaysIndex(string? dossier = null)
    {
        _dossier = dossier ?? Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "certified");
    }

    /// <summary>Toutes les parties lisibles du dossier. Ne relit que les fichiers nouveaux ou modifies.</summary>
    public IReadOnlyList<Partie> Toutes()
    {
        lock (_verrou)
        {
            if (!Directory.Exists(_dossier)) return Array.Empty<Partie>();
            var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fichier in Directory.EnumerateFiles(_dossier, "*.json"))
            {
                vus.Add(fichier);
                DateTime ecrit;
                try { ecrit = File.GetLastWriteTimeUtc(fichier); }
                catch (IOException) { continue; }
                if (_resumes.TryGetValue(fichier, out var deja) && deja.Ecrit == ecrit) continue;
                Partie? partie;
                try { partie = Lire(File.ReadAllText(fichier)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                _resumes[fichier] = (ecrit, partie);
            }
            foreach (var parti in _resumes.Keys.Where(k => !vus.Contains(k)).ToList()) _resumes.Remove(parti);
            return _resumes.Values.Select(v => v.Partie).OfType<Partie>().ToList();
        }
    }

    /// <summary>Le resume d'un fichier du dossier certified ; null s'il est illisible ou incomplet.</summary>
    internal static Partie? Lire(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var racine = doc.RootElement;
            if (!racine.TryGetProperty("passport", out var passeport) || passeport.ValueKind != JsonValueKind.Object) return null;
            static string Texte(JsonElement e, string nom)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            static JsonElement Objet(JsonElement e, string nom)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
            static DateTime? Date(string texte)
                => DateTime.TryParse(texte, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

            var jeu = Objet(passeport, "game");
            var mesure = Objet(passeport, "metric");
            var temps = Objet(passeport, "timing");
            var contexte = Objet(passeport, "context");
            var identite = Objet(passeport, "identity");
            // Le score du passeport est ecrit en TEXTE (« 10210 ») : le passeport signe garde ses
            // nombres en chaines. Un nombre JSON est accepte aussi.
            if (mesure.ValueKind != JsonValueKind.Object || !mesure.TryGetProperty("value", out var valeur)) return null;
            long score;
            if (valeur.ValueKind == JsonValueKind.Number) { if (!valeur.TryGetInt64(out score)) return null; }
            else if (valeur.ValueKind != JsonValueKind.String
                     || !long.TryParse(valeur.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out score)) return null;
            var romGroup = Texte(jeu, "rom_group");
            if (romGroup.Length == 0) return null;
            var fin = Date(Texte(temps, "ended_at")) ?? Date(Texte(racine, "submitted_at"));
            var debut = Date(Texte(temps, "started_at")) ?? fin;
            if (debut is null || fin is null) return null;
            var labo = contexte.ValueKind == JsonValueKind.Object && contexte.TryGetProperty("lab", out var l) && l.ValueKind == JsonValueKind.True;
            return new Partie(
                Texte(racine, "session_id"),
                romGroup,
                Texte(jeu, "ruleset"),
                score,
                string.Equals(Texte(mesure, "ranking_direction"), "lower_better", StringComparison.OrdinalIgnoreCase),
                debut.Value,
                fin.Value,
                Texte(racine, "verdict"),
                Texte(contexte, "world"),
                Texte(identite, "session_player_id"),
                labo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Une partie retenue par la plateforme : publiee, ou en attente d'une homologation. Un refus
    /// n'est pas un record (entrees impossibles, reglages non conformes...), un doublon est deja
    /// la, et une partie de labo n'est jamais celle d'un joueur.
    /// </summary>
    internal static bool Retenue(Partie p)
        => !p.Labo && p.Verdict.Length > 0
           && !string.Equals(p.Verdict, "refused", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(p.Verdict, "duplicate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Le replay de cette borne qui appartient a la partie. Il s'est arrete pendant la partie, ou
    /// juste apres (la finalisation suit la fermeture du jeu), et il a commence apres son debut.
    /// Celui qui porte le score de la partie gagne (le rapporteur l'y attache a la publication) ;
    /// sinon le plus long, qui contient le plus de jeu.
    /// </summary>
    internal static string? ReplayDeLaPartie(Partie p, IReadOnlyList<ReplayLocal> replays)
    {
        var marge = TimeSpan.FromMinutes(2);
        var candidats = replays
            .Where(r => r.FinUtc >= p.DebutUtc && r.FinUtc <= p.FinUtc + marge && r.FinUtc - r.Duree >= p.DebutUtc - marge)
            .ToList();
        if (candidats.Count == 0) return null;
        return (candidats.FirstOrDefault(r => r.Score == p.Score) ?? candidats.OrderByDescending(r => r.Duree).First()).ReplayId;
    }

    /// <summary>
    /// Les lignes de MES RECORDS : les parties retenues de ce jeu, dans cette regle, du joueur
    /// courant (celui de la session en salle, sinon le proprietaire de la borne), de la meilleure
    /// a la moins bonne, la plus recente d'abord a score egal. Le rang est la place parmi SES
    /// parties ; le nom laisse la place a la date de la partie.
    /// </summary>
    internal static IReadOnlyList<LeaderboardClient.Ligne> MesParties(
        IEnumerable<Partie> parties,
        string romGroup,
        string regle,
        string joueurDeSession,
        IReadOnlyList<ReplayLocal> replays,
        Func<DateTime, string> date)
    {
        var choisies = parties
            .Where(Retenue)
            .Where(p => string.Equals(p.RomGroup, romGroup, StringComparison.OrdinalIgnoreCase))
            .Where(p => regle.Length == 0 || string.Equals(p.Regle, regle, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.Equals(p.JoueurDeSession.Trim(), (joueurDeSession ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var plusBas = choisies.Count > 0 && choisies.All(p => p.PlusBasEstMieux);
        var rangees = (plusBas ? choisies.OrderBy(p => p.Score) : choisies.OrderByDescending(p => p.Score))
            .ThenByDescending(p => p.DebutUtc)
            .ToList();
        return rangees.Select((p, i) => new LeaderboardClient.Ligne(
            Rang: i + 1,
            Joueur: date(p.DebutUtc),
            Valeur: p.Score,
            Ville: "",
            Pays: "",
            Salle: "",
            Scelle: false,
            ReplayId: ReplayDeLaPartie(p, replays),
            Quand: p.DebutUtc.ToString("o", CultureInfo.InvariantCulture),
            CestMoi: true,
            Monde: p.Monde,
            Poignee: "",
            PlusBasEstMieux: plusBas)).ToList();
    }
}
