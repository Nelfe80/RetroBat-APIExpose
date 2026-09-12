using System.Collections.Concurrent;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// État RÉSEAU d'un objet replay (CDC §86). Décrit uniquement sa disponibilité et sa durabilité,
/// jamais l'état local d'enregistrement ou de lecture, qui vit dans ReplayPlaybackState.
/// </summary>
public enum ReplayNetworkState
{
    /// <summary>Le manifeste est connu, l'objet n'est pas ici et personne ne sait où le prendre.</summary>
    Unavailable,

    /// <summary>L'objet est ici et n'est proposé à personne : ni partagé, ni annoncé.</summary>
    LocalOnly,

    /// <summary>L'objet est ici et cette borne accepte de le servir.</summary>
    Announced,

    /// <summary>Récupération en cours auprès d'un pair.</summary>
    Replicating,

    /// <summary>L'objet est conservé quoi qu'il arrive : aucun ménage ne peut l'effacer.</summary>
    Pinned,

    /// <summary>Assez de copies dans le réseau pour tenir la perte d'une borne.
    /// PAS ENCORE CALCULABLE : il faudrait un recensement des copies, qui n'existe pas.</summary>
    Durable,

    /// <summary>Moins de copies que la politique n'en demande.
    /// PAS ENCORE CALCULABLE, même raison que <see cref="Durable"/>.</summary>
    Degraded,
}

/// <summary>
/// Calcule l'état réseau à partir de FAITS OBSERVABLES, et de rien d'autre.
///
/// Deux états du CDC ne sont volontairement jamais émis : <c>durable</c> et <c>degraded</c>
/// supposent de savoir combien de copies existent dans le réseau, ce qui demande un recensement
/// des pairs qu'on n'a pas. Les afficher aujourd'hui reviendrait à promettre une durabilité qu'on
/// n'a pas mesurée, et c'est exactement le genre d'affirmation qui se paie le jour où quelqu'un
/// s'y fie pour effacer une copie.
///
/// Ordre de précédence : une récupération en cours prime, puis l'épinglage (durabilité), puis le
/// fait d'être proposé aux autres, puis la simple présence locale.
/// </summary>
public sealed class ReplayNetworkStateService
{
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly ReplaySharePolicy _policy;
    private readonly ConcurrentDictionary<string, byte> _fetching = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Avancement> _avancements = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ou en est un telechargement : de quoi dire combien il reste, et d'ou ca vient.</summary>
    public sealed record FetchProgress(string Sha256, long Received, long Total, double BytesPerSecond,
        double? EtaSeconds, string Source);

    private sealed class Avancement
    {
        public long Recus, Total;
        public string Source = "";
        public DateTime Depuis = DateTime.UtcNow;
    }

    public ReplayNetworkStateService(IReplayObjectStore objects, IReplayMetadataStore meta, ReplaySharePolicy policy)
    {
        _objects = objects; _meta = meta; _policy = policy;
    }

    /// <summary>Marque une récupération en cours ; le jeton la referme.</summary>
    public IDisposable BeginFetch(string sha256)
    {
        _fetching.TryAdd(sha256, 0);
        return new FetchScope(this, sha256);
    }

    /// <summary>
    /// Rapporte les octets recus d'une source. Le chronometre repart quand la source change :
    /// un pair qui a echoue avant le miroir ne doit pas compter dans le debit du miroir.
    /// </summary>
    public void Report(string sha256, long received, long total, string source)
    {
        var a = _avancements.GetOrAdd(sha256, _ => new Avancement());
        lock (a)
        {
            if (!string.Equals(a.Source, source, StringComparison.Ordinal))
            {
                a.Source = source;
                a.Depuis = DateTime.UtcNow;
            }
            a.Recus = received;
            a.Total = total;
        }
    }

    /// <summary>La progression d'un telechargement en cours, ou null s'il n'y en a pas.</summary>
    public FetchProgress? ProgressOf(string sha256)
    {
        if (!_fetching.ContainsKey(sha256) || !_avancements.TryGetValue(sha256, out var a))
        {
            return null;
        }
        lock (a)
        {
            var secondes = Math.Max(0.001, (DateTime.UtcNow - a.Depuis).TotalSeconds);
            var debit = a.Recus / secondes;
            // Pas d'estimation avant une seconde de mesure : un premier paquet donnerait un
            // debit fantaisiste, donc un temps restant qui saute dans tous les sens.
            double? reste = secondes >= 1 && debit > 0 && a.Total > a.Recus
                ? (a.Total - a.Recus) / debit
                : null;
            return new FetchProgress(sha256, a.Recus, a.Total, debit, reste, a.Source);
        }
    }

    public ReplayNetworkState Evaluate(string replayId, string objectSha256)
    {
        if (_fetching.ContainsKey(objectSha256)) return ReplayNetworkState.Replicating;

        if (!File.Exists(_objects.ObjectPath(objectSha256))) return ReplayNetworkState.Unavailable;

        var meta = _meta.GetMeta(replayId);
        if (meta?.Pinned == true) return ReplayNetworkState.Pinned;

        // Publié au miroir : l'objet est offert au monde, que CETTE borne le serve ou non.
        // C'est un fait constaté (on l'y a mis), pas une supposition sur le réseau.
        if (string.Equals(meta?.PublicationState, "mirrored", StringComparison.OrdinalIgnoreCase))
            return ReplayNetworkState.Announced;

        var visibility = meta?.Visibility ?? "private";
        if (_policy.SharingEnabled && string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
            return ReplayNetworkState.Announced;

        return ReplayNetworkState.LocalOnly;
    }

    /// <summary>Forme attendue par l'API : minuscules avec tiret bas (`local_only`).</summary>
    public static string Wire(ReplayNetworkState state) => state switch
    {
        ReplayNetworkState.LocalOnly => "local_only",
        _ => state.ToString().ToLowerInvariant(),
    };

    private sealed class FetchScope : IDisposable
    {
        private readonly ReplayNetworkStateService _owner;
        private readonly string _sha;
        public FetchScope(ReplayNetworkStateService owner, string sha) { _owner = owner; _sha = sha; }
        public void Dispose()
        {
            _owner._fetching.TryRemove(_sha, out _);
            _owner._avancements.TryRemove(_sha, out _);
        }
    }
}
