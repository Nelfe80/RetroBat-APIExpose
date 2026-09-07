using System.Globalization;
using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Social;

/// <summary>
/// Le journal local des événements sociaux VÉRIFIÉS (LOT R9 ; CDC §58, §70).
///
/// Séparé du journal des réactions maison, et il faut que ça le reste : celui-là est ce que CETTE
/// borne a produit et peut remonter, celui-ci est ce qu'elle a reçu d'ailleurs. Les mélanger
/// ferait remonter à la plateforme les réactions d'autrui sous le jeton d'un spectateur d'ici.
///
/// La fusion est une UNION par identifiant, et c'est tout : l'identifiant est le hash du corps
/// signé, donc deux exemplaires du même événement se reconnaissent sans comparaison champ à
/// champ, et rien ne dépend de l'ordre d'arrivée. Un événement en retard de trois jours se range
/// tout seul. C'est ce qui permet de recevoir d'un pair, d'un miroir ou de la plateforme sans
/// jamais avoir à coordonner qui a quoi.
///
/// Rien n'est jamais réécrit ni effacé (§58). Un retrait est un événement de plus, qui désigne
/// celui qu'il retire ; la convergence cesse alors de le compter, sans que la trace disparaisse.
/// </summary>
public sealed class ReplaySocialStore
{
    private readonly ReplayStore _store;
    private readonly ILogger<ReplaySocialStore> _logger;
    private readonly object _gate = new();

    public ReplaySocialStore(ReplayStore store, ILogger<ReplaySocialStore> logger)
    {
        _store = store; _logger = logger;
    }

    public string PathFor(string replayId)
        => Path.Combine(_store.SocialRoot, Assainir(replayId) + ".jsonl");

    /// <summary>Ajoute ce qui manque. Rend le nombre d'événements réellement nouveaux.</summary>
    public int Merge(string replayId, IEnumerable<SocialEvent> events)
    {
        var candidats = events.Where(e => e.EventId.Length == 64).ToList();
        if (replayId.Length == 0 || candidats.Count == 0) return 0;

        lock (_gate)
        {
            var connus = Read(replayId).Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
            var lignes = new List<string>();
            foreach (var e in candidats)
            {
                if (!connus.Add(e.EventId)) continue;
                lignes.Add(e.ToTransport().ToJsonString());
            }
            if (lignes.Count == 0) return 0;
            try
            {
                Directory.CreateDirectory(_store.SocialRoot);
                File.AppendAllLines(PathFor(replayId), lignes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replay social : journal non écrit pour {ReplayId}.", replayId);
                return 0;
            }
            return lignes.Count;
        }
    }

    /// <summary>Tout ce qu'on détient pour cette cible, tel qu'arrivé (donc revérifiable).</summary>
    public IReadOnlyList<SocialEvent> Read(string replayId)
    {
        var path = PathFor(replayId);
        var liste = new List<SocialEvent>();
        if (replayId.Length == 0 || !File.Exists(path)) return liste;
        try
        {
            foreach (var ligne in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(ligne)) continue;
                var e = SocialEvent.FromJson(JsonNode.Parse(ligne));
                if (e is not null) liste.Add(e);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay social : journal illisible {Path}.", path); }
        return liste;
    }

    /// <summary>
    /// L'état convergé : ce qui doit être affiché, une fois les règles du §58 appliquées.
    ///
    /// Deux règles, dans cet ordre. Ce qu'un retrait désigne cesse d'être compté. Puis, par
    /// acteur, seule la DERNIÈRE séance compte : quelqu'un qui regarde une seconde fois replace
    /// ses cinq réactions au bon moment, et son total retenu reste cinq. Les séances précédentes
    /// restent dans le journal, elles ne pèsent plus.
    ///
    /// Chaque nœud applique ces deux règles sur l'ensemble qu'il détient, et deux nœuds qui
    /// détiennent les mêmes événements affichent la même chose. Aucun d'eux n'a besoin de
    /// demander à un troisième.
    /// </summary>
    public IReadOnlyList<ReplayReaction> Converged(string replayId) => Converge(replayId, Read(replayId));

    /// <summary>
    /// Les deux regles du 58, appliquees a un ensemble quelconque. Statique et sans etat, pour
    /// que la convergence puisse etre EPROUVEE sans monter un disque : c'est la piece dont
    /// depend l'accord entre deux bornes, elle ne doit pas etre celle qu'on ne teste jamais.
    /// </summary>
    public static IReadOnlyList<ReplayReaction> Converge(string replayId, IReadOnlyList<SocialEvent> events)
    {
        if (events.Count == 0) return Array.Empty<ReplayReaction>();

        var retires = events
            .Where(e => e.Kind == SocialEventVerifier.KindModeration)
            .Select(e => e.ModeratedEventId)
            .Where(id => id.Length == 64)
            .ToHashSet(StringComparer.Ordinal);

        var reactions = events
            .Where(e => e.Kind == SocialEventVerifier.KindReaction && !retires.Contains(e.EventId))
            .ToList();
        if (reactions.Count == 0) return Array.Empty<ReplayReaction>();

        var derniere = reactions
            .GroupBy(e => e.Actor, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(e => e.SessionSeq), StringComparer.Ordinal);

        return reactions
            .Where(e => derniere.TryGetValue(e.Actor, out var seq) && e.SessionSeq == seq)
            .OrderBy(e => e.Frame)
            .Select(e => new ReplayReaction(
                replayId, e.Reaction, e.Level, e.Frame, Horodatage(e), string.Empty, false))
            .ToList();
    }

    /// <summary>
    /// Ce que l'affichage doit montrer : le journal d'ici, plus ce qui vient d'ailleurs.
    ///
    /// Nos propres réactions nous reviennent en événements une fois remontées et signées. On
    /// garde alors la version LOCALE, qui porte le pseudo de la borne, et on écarte son double :
    /// même image (frame), même famille, même intensité. Deux spectateurs différents qui
    /// tomberaient exactement sur ce triplet perdraient une bulle à un endroit qui en a déjà une,
    /// ce qui ne coûte rien ; compter la même réaction deux fois, si.
    /// </summary>
    public IReadOnlyList<ReplayReaction> Display(string replayId, IReadOnlyList<ReplayReaction> locales)
        => Fusionner(locales, Converged(replayId));

    /// <summary>Meme raison : la regle de fusion se verifie sans disque ni reseau.</summary>
    public static IReadOnlyList<ReplayReaction> Fusionner(
        IReadOnlyList<ReplayReaction> locales, IReadOnlyList<ReplayReaction> distantes)
    {
        if (distantes.Count == 0) return locales;

        var vues = locales
            .Select(r => (r.Frame, r.Reaction, r.Level))
            .ToHashSet();

        var sortie = new List<ReplayReaction>(locales);
        foreach (var r in distantes)
        {
            if (vues.Add((r.Frame, r.Reaction, r.Level))) sortie.Add(r);
        }
        return sortie;
    }

    /// <summary>Le nombre d'événements détenus, pour le diagnostic.</summary>
    public int Count(string replayId) => Read(replayId).Count;

    private static long Horodatage(SocialEvent e)
    {
        var brut = string.Empty;
        try { brut = e.Body["issued_at"]?.GetValue<string>() ?? string.Empty; } catch { }
        return DateTime.TryParse(brut, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? (long)(t - DateTime.UnixEpoch).TotalMilliseconds
            : 0;
    }

    /// <summary>Un identifiant de replay ne devient jamais un chemin : on ne garde que ce qui
    /// peut composer un nom de fichier, et rien d'autre.</summary>
    private static string Assainir(string replayId)
    {
        var sb = new System.Text.StringBuilder(replayId.Length);
        foreach (var c in replayId)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : "inconnu";
    }
}
