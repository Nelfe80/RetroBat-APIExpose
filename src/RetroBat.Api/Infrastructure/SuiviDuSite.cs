using System.Text.Json;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// CE QUE LE SITE JOINT AU RELEVE DE LA BORNE (charge du 14/11, points 5 et 6 du site). Sept minuteurs de fond demandaient
/// chacun leur part au site : les directs des joueurs suivis (<c>live/following</c>, 60 s), les planches d'avatar en attente
/// (<c>avatar/pending</c>, 10 min), l'etat d'une reconstruction (<c>scores/recovery-status</c>, 1 h), la boite du relais
/// (<c>relay/inbox</c>, 5 min au repos), le releve <c>agent/work</c>, l'index des jeux ouverts et celui des defis (5 min).
/// Le site les donne maintenant dans <c>agent/work</c> et <c>agent/whats-new</c>.
///
/// Ici, le point de rencontre, sans dependance : chaque service y inscrit le traitement de son champ, le releve y depose ce
/// qu'il a recu, et chaque minuteur demande s'il est COUVERT avant d'appeler le site. Un champ absent (site d'avant), un
/// traitement en erreur ou un releve qui ne revient plus laissent la couverture expirer : les minuteurs reprennent seuls.
/// </summary>
public sealed class SuiviDuSite
{
    public const string DirectsSuivis = "live_following";
    public const string AvatarsEnAttente = "avatar_pending";
    public const string Reconstruction = "recovery";
    public const string BoiteDuRelais = "relay_inbox";

    /// <summary>Les revisions d'<c>agent/whats-new</c>.</summary>
    public const string Travail = "work";
    public const string JeuxOuverts = "open_games";
    public const string DefisOuverts = "challenges_open";

    /// <summary>Les champs que le site joint au releve, dans l'ordre de leur traitement.</summary>
    public static readonly IReadOnlyList<string> ChampsJoints = [DirectsSuivis, AvatarsEnAttente, Reconstruction, BoiteDuRelais];

    /// <summary>
    /// Combien de temps un champ recu couvre son minuteur, pour une cadence de releve donnee : deux releves et demi. Un releve
    /// rate ne fait rien reprendre ; deux d'affilee, si.
    /// </summary>
    public static TimeSpan Validite(TimeSpan cadenceDuReleve)
        => TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, cadenceDuReleve.Ticks * 5 / 2));

    private readonly ILogger<SuiviDuSite>? _journal;
    private readonly object _verrou = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task>> _traitements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string, CancellationToken, Task>> _surRevision = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _couvertJusqua = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _revisions = new(StringComparer.Ordinal);
    private DateTime _quoiDeNeufJusqua = DateTime.MinValue;

    public SuiviDuSite(ILogger<SuiviDuSite>? journal = null)
    {
        _journal = journal;
    }

    /// <summary>Le curseur de la boite du relais (null : pas de relais, <c>inbox_since</c> ne part pas).</summary>
    public Func<long?>? CurseurDeLaBoite { get; set; }

    /// <summary>Le curseur a donner au site, ou null.</summary>
    public long? Curseur()
    {
        try
        {
            return CurseurDeLaBoite?.Invoke();
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Suivi du site : curseur du relais illisible.");
            return null;
        }
    }

    /// <summary>Inscrit le traitement d'un champ joint (un seul par champ : le dernier inscrit gagne).</summary>
    public void Traiter(string champ, Func<JsonElement, CancellationToken, Task> traitement)
    {
        lock (_verrou) _traitements[champ] = traitement;
    }

    /// <summary>Inscrit ce qui suit une revision d'<c>agent/whats-new</c> (le service compare a ce qu'il a deja).</summary>
    public void SurRevision(string cle, Func<string, CancellationToken, Task> suite)
    {
        lock (_verrou) _surRevision[cle] = suite;
    }

    /// <summary>
    /// Donne a chaque service le champ qui le concerne, s'il est la et s'il a un traitement. Un champ traite sans erreur couvre
    /// son minuteur pendant <paramref name="validite"/>. Rend les champs traites.
    /// </summary>
    public async Task<IReadOnlyList<string>> DistribuerAsync(JsonElement racine, TimeSpan validite, CancellationToken ct)
    {
        var traites = new List<string>();
        if (racine.ValueKind != JsonValueKind.Object) return traites;
        foreach (var champ in ChampsJoints)
        {
            if (!racine.TryGetProperty(champ, out var valeur) || valeur.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            Func<JsonElement, CancellationToken, Task>? traitement;
            lock (_verrou) _traitements.TryGetValue(champ, out traitement);
            if (traitement is null) continue;
            try
            {
                await traitement(valeur.Clone(), ct).ConfigureAwait(false);
                Couvrir(champ, DateTime.UtcNow + validite);
                traites.Add(champ);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Le minuteur du champ n'est pas couvert : il fera le travail lui-meme.
                _journal?.LogDebug(ex, "Suivi du site : {Champ} non traite.", champ);
            }
        }

        return traites;
    }

    private void Couvrir(string champ, DateTime jusqua)
    {
        lock (_verrou)
        {
            if (!_couvertJusqua.TryGetValue(champ, out var deja) || deja < jusqua) _couvertJusqua[champ] = jusqua;
        }
    }

    /// <summary>Le champ a-t-il ete recu et traite assez recemment pour que son minuteur se taise ?</summary>
    public bool Couvert(string champ, DateTime maintenantUtc)
    {
        lock (_verrou) return _couvertJusqua.TryGetValue(champ, out var jusqua) && maintenantUtc < jusqua;
    }

    /// <summary>Oublie toute couverture et toute revision (site redevenu ancien, borne desappairee).</summary>
    public void Oublier()
    {
        lock (_verrou)
        {
            _couvertJusqua.Clear();
            _revisions.Clear();
            _quoiDeNeufJusqua = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Une reponse d'<c>agent/whats-new</c> : ses revisions, valables <paramref name="validite"/>. Chaque revision qui a un
    /// service inscrit lui est donnee ; c'est lui qui la compare a ce qu'il a et relit si elle a change.
    /// </summary>
    public async Task NoterLesRevisionsAsync(IReadOnlyDictionary<string, string> revisions, TimeSpan validite, CancellationToken ct)
    {
        lock (_verrou)
        {
            _quoiDeNeufJusqua = DateTime.UtcNow + validite;
            _revisions.Clear();
            foreach (var (cle, revision) in revisions) _revisions[cle] = Normaliser(revision);
        }

        foreach (var (cle, revision) in revisions)
        {
            Func<string, CancellationToken, Task>? suite;
            lock (_verrou) _surRevision.TryGetValue(cle, out suite);
            if (suite is null) continue;
            try
            {
                await suite(Normaliser(revision), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _journal?.LogDebug(ex, "Suivi du site : relecture de {Cle} en erreur.", cle);
            }
        }
    }

    /// <summary>Le quoi de neuf ne fait plus foi (site d'avant) ; les champs deja recus gardent leur couverture.</summary>
    public void OublierLeQuoiDeNeuf()
    {
        lock (_verrou)
        {
            _revisions.Clear();
            _quoiDeNeufJusqua = DateTime.MinValue;
        }
    }

    /// <summary><c>agent/whats-new</c> a-t-il repondu assez recemment pour que ses revisions fassent foi ?</summary>
    public bool QuoiDeNeufActif(DateTime maintenantUtc)
    {
        lock (_verrou) return maintenantUtc < _quoiDeNeufJusqua;
    }

    /// <summary>La derniere revision connue de <paramref name="cle"/>, si <c>agent/whats-new</c> fait foi ; null sinon.</summary>
    public string? Revision(string cle, DateTime maintenantUtc)
    {
        lock (_verrou) return maintenantUtc < _quoiDeNeufJusqua && _revisions.TryGetValue(cle, out var r) && r.Length > 0 ? r : null;
    }

    /// <summary>
    /// L'empreinte que garde un service (<paramref name="empreinte"/>, ETag avec ou sans guillemets) est-elle la revision que
    /// donne <c>agent/whats-new</c> ? Oui : inutile de relire. Faux des que le quoi de neuf ne fait plus foi.
    /// </summary>
    public bool RevisionAJour(string cle, string? empreinte, DateTime maintenantUtc)
        => Revision(cle, maintenantUtc) is { } r && string.Equals(r, Normaliser(empreinte), StringComparison.Ordinal);

    /// <summary>Un ETag sans <c>W/</c> ni guillemets, comme les revisions d'<c>agent/whats-new</c>. Pur.</summary>
    public static string Normaliser(string? empreinte)
    {
        var e = (empreinte ?? "").Trim();
        if (e.StartsWith("W/", StringComparison.Ordinal)) e = e[2..];
        return e.Trim('"');
    }
}
