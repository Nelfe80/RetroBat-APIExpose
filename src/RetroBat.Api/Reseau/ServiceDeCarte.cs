using System.Security.Cryptography;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Reseau;

/// <summary>
/// LE CLIENT DE CARTE DE LA BORNE (CDC infra §6.4 et §15.4, 2026-10-08).
///
/// La borne tient la carte du reseau : celle qu'elle a sur disque, sinon la carte par defaut livree avec elle.
/// Toutes les six heures (et une demi-minute apres le demarrage), elle en demande une plus recente au central,
/// puis, s'il ne repond pas, aux miroirs de sa carte et aux copies connues (GitHub Pages, GitLab Pages, la copie
/// du central). Elle n'adopte qu'une carte signee par la cle epinglee du site statique, et plus recente que la
/// sienne. Le plan de controle ne coupe rien : si personne ne repond, elle garde la sienne.
/// </summary>
public sealed class ServiceDeCarte : BackgroundService
{
    /// <summary>Les copies du site qui portent aussi la carte, a interroger quand le central ne repond pas.</summary>
    public static readonly string[] CopiesConnues = ["https://miroir.nelfeplay.com", "https://nelfeplay.gitlab.io", "https://nelfeplay.com/copie"];

    private static readonly TimeSpan Periode = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<ServiceDeCarte>? _logger;
    private readonly string _fichier;
    private readonly ClePublique _cleDuSite;
    private readonly Func<string> _central;
    private readonly object _verrou = new();
    private CarteDuReseau _carte = CarteDuReseau.ParDefaut;
    private bool _lue;

    public ServiceDeCarte(IHttpClientFactory http, ILogger<ServiceDeCarte>? logger = null)
        : this(http, logger,
            Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "state", "nelfeplay", "carte.json"),
            CarteDuReseau.CleDuSiteStatique, () => NelfePlayAgentService.BaseUrl)
    {
    }

    internal ServiceDeCarte(IHttpClientFactory http, ILogger<ServiceDeCarte>? logger, string fichier, ClePublique cleDuSite, Func<string> central)
    {
        _http = http;
        _logger = logger;
        _fichier = fichier;
        _cleDuSite = cleDuSite;
        _central = central;
    }

    /// <summary>La carte en vigueur : celle du disque si elle s'ouvre, sinon celle livree avec la borne.</summary>
    public CarteDuReseau Actuelle
    {
        get
        {
            lock (_verrou)
            {
                if (_lue) return _carte;
                _lue = true;
                try
                {
                    if (File.Exists(_fichier) && CarteDuReseau.Ouvrir(File.ReadAllText(_fichier), _cleDuSite) is { } gardee
                        && gardee.Version > _carte.Version)
                    {
                        _carte = gardee;
                    }
                }
                catch (IOException ex)
                {
                    _logger?.LogDebug(ex, "Carte du reseau : copie du disque illisible.");
                }
                return _carte;
            }
        }
    }

    /// <summary>
    /// Les cles qui signent les verdicts : celles de la carte en vigueur. Une carte publiee fait foi ; la carte par
    /// defaut porte la cle de l'emetteur du moment de la version.
    /// </summary>
    public IReadOnlyList<ClePublique> ClesDeVerdict => Actuelle.ClesDeVerdict;

    /// <summary>
    /// Adopte une carte signee si elle est plus recente que la carte en vigueur, et la garde sur disque. Vrai si
    /// elle est adoptee.
    /// </summary>
    public bool Proposer(string enveloppe, string source)
    {
        var carte = CarteDuReseau.Ouvrir(enveloppe, _cleDuSite);
        if (carte is null)
        {
            _logger?.LogWarning("Carte du reseau : celle de {Source} n'est pas signee par la cle du site, ignoree.", source);
            return false;
        }
        lock (_verrou)
        {
            _ = Actuelle;
            if (carte.Version <= _carte.Version) return false;
            _carte = carte;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_fichier)!);
                var temporaire = _fichier + ".tmp";
                File.WriteAllText(temporaire, carte.Enveloppe);
                File.Move(temporaire, _fichier, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger?.LogDebug(ex, "Carte du reseau : copie sur disque impossible.");
            }
        }
        _logger?.LogInformation("Carte du reseau : {Carte} adoptee ({Source}).", carte, source);
        return true;
    }

    /// <summary>
    /// Demande une carte plus recente : au central d'abord, puis aux miroirs de la carte et aux copies connues.
    /// Rend vrai si une nouvelle carte a ete adoptee.
    /// </summary>
    public async Task<bool> RafraichirAsync(CancellationToken ct)
    {
        var sources = new List<string> { _central().TrimEnd('/') };
        var miroirs = Actuelle.AvecLeRole("front").Select(n => n.Url).Where(u => !sources.Contains(u)).ToList();
        // Deux miroirs au hasard : chacun sa part de la charge.
        sources.AddRange(miroirs.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(2));
        sources.AddRange(CopiesConnues.Where(c => !sources.Contains(c)));

        var client = _http.CreateClient(nameof(ServiceDeCarte));
        client.Timeout = TimeSpan.FromSeconds(15);
        foreach (var source in sources)
        {
            try
            {
                using var reponse = await client.GetAsync(source + CarteDuReseau.Chemin, ct).ConfigureAwait(false);
                if (!reponse.IsSuccessStatusCode) continue;
                var enveloppe = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (enveloppe.Length > 2_000_000) continue;
                if (Proposer(enveloppe, source)) return true;
                // Une source a jour arrete la recherche ; une source en retard laisse chercher plus loin.
                if (CarteDuReseau.Ouvrir(enveloppe, _cleDuSite) is { } lue && lue.Version >= Actuelle.Version) return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger?.LogDebug(ex, "Carte du reseau : {Source} ne repond pas.", source);
            }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger?.LogInformation("Carte du reseau : {Carte} en vigueur.", Actuelle);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RafraichirAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Carte du reseau : tour de lecture en echec.");
                }
                var decalage = TimeSpan.FromMinutes(RandomNumberGenerator.GetInt32(-30, 31));
                await Task.Delay(Periode + decalage, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
