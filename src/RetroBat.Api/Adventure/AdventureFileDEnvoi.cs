using System.Text.Json;

namespace RetroBat.Api.Adventure;

/// <summary>
/// LA FILE D'ENVOI DES VERSIONS (CDC v1.0 §2, point 3) : une coupure reseau ne perd rien. Une version sauvee est d'abord gardee
/// sur le disque (<see cref="AdventureStockage"/>), puis inscrite ici ; l'envoi depose les objets par empreinte d'abord (ceux que
/// le site n'a pas), la version ensuite. Echec : l'entree reste, avec un delai croissant, et survit a un redemarrage de l'API.
/// Jamais d'envoi d'une version dont un objet manque au site : la version qui pointerait dans le vide est pire qu'absente.
/// </summary>
public sealed class AdventureFileDEnvoi
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Delais de reprise apres un echec : 15 s, 1 min, 5 min, puis toutes les 15 min.</summary>
    internal static readonly TimeSpan[] Delais = [TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly AdventureStockage _stockage;
    private readonly string _chemin;
    private readonly object _verrou = new();
    private readonly SemaphoreSlim _porte = new(1, 1);
    private List<Entree> _entrees;

    public sealed record Entree
    {
        public AdventureVersion Version { get; init; } = new();
        public int Essais { get; init; }
        public DateTime ProchainEssaiUtc { get; init; }
    }

    public AdventureFileDEnvoi(AdventureStockage stockage)
    {
        _stockage = stockage;
        _chemin = Path.Combine(stockage.Racine, "envoi.json");
        _entrees = Lire();
    }

    /// <summary>Les versions que le site a refusees pour toujours depuis ce demarrage : (version, raison).</summary>
    public List<(string Version, string Raison)> Abandonnees { get; } = [];

    public IReadOnlyList<Entree> EnAttente
    {
        get { lock (_verrou) { return _entrees.ToList(); } }
    }

    public void Ajouter(AdventureVersion version)
    {
        lock (_verrou)
        {
            if (_entrees.Any(e => e.Version.Id == version.Id))
            {
                return;
            }

            _entrees.Add(new Entree { Version = version, ProchainEssaiUtc = DateTime.MinValue });
            Ecrire();
        }
    }

    /// <summary>Un tour d'envoi : chaque entree echue, la plus ancienne d'abord. Rend le nombre de versions parties.</summary>
    public async Task<int> EnvoyerAsync(IAdventureSite site, DateTime maintenant, CancellationToken ct)
    {
        if (!await _porte.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return 0;
        }

        try
        {
            var envoyees = 0;
            foreach (var entree in EnAttente.OrderBy(e => e.Version.CreatedUtc))
            {
                // Dans l'ordre de leur prise : une version plus ancienne qui attend son delai retient les suivantes.
                if (entree.ProchainEssaiUtc > maintenant)
                {
                    break;
                }

                var depot = await EnvoyerUneAsync(site, entree.Version, ct).ConfigureAwait(false);
                if (depot.Ok)
                {
                    envoyees++;
                    Retirer(entree.Version.Id);
                }
                else if (depot.Definitif)
                {
                    // Le site refuse pour de bon (autre coeur, pas l'hote) : la version reste dans la copie locale, elle ne bloque plus la file.
                    Abandonnees.Add((entree.Version.Id, depot.Raison ?? "refuse"));
                    Retirer(entree.Version.Id);
                }
                else
                {
                    Reporter(entree, maintenant);
                    // Un site injoignable le reste pour les suivantes : on ne les use pas toutes.
                    break;
                }
            }

            return envoyees;
        }
        finally
        {
            _porte.Release();
        }
    }

    private async Task<AdventureDepot> EnvoyerUneAsync(IAdventureSite site, AdventureVersion version, CancellationToken ct)
    {
        var objets = version.ObjetsParGenre().DistinctBy(o => o.Sha, StringComparer.OrdinalIgnoreCase).ToList();
        var manquants = await site.ObjetsManquantsAsync(objets.Select(o => o.Sha).ToList(), ct).ConfigureAwait(false);
        if (manquants is null)
        {
            return AdventureDepot.APlusTard("offline");
        }

        foreach (var empreinte in manquants)
        {
            var octets = _stockage.LireObjet(empreinte);
            var genre = objets.FirstOrDefault(o => string.Equals(o.Sha, empreinte, StringComparison.OrdinalIgnoreCase)).Genre;
            if (octets is null || genre is null || !await site.DeposerObjetAsync(empreinte, genre, octets, ct).ConfigureAwait(false))
            {
                return AdventureDepot.APlusTard("objet");
            }
        }

        return await site.DeposerVersionAsync(version, ct).ConfigureAwait(false);
    }

    private void Retirer(string id)
    {
        lock (_verrou)
        {
            _entrees = _entrees.Where(e => e.Version.Id != id).ToList();
            Ecrire();
        }
    }

    private void Reporter(Entree entree, DateTime maintenant)
    {
        lock (_verrou)
        {
            var delai = Delais[Math.Min(entree.Essais, Delais.Length - 1)];
            _entrees = _entrees.Select(e => e.Version.Id == entree.Version.Id ? e with { Essais = e.Essais + 1, ProchainEssaiUtc = maintenant + delai } : e).ToList();
            Ecrire();
        }
    }

    private List<Entree> Lire()
    {
        try
        {
            return File.Exists(_chemin) ? JsonSerializer.Deserialize<List<Entree>>(File.ReadAllText(_chemin), Format) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Ecrire()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_chemin)!);
            File.WriteAllText(_chemin + ".tmp", JsonSerializer.Serialize(_entrees, Format));
            File.Move(_chemin + ".tmp", _chemin, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Le disque ne repond pas : la file reste en memoire, la prochaine ecriture reessaie.
        }
    }
}
