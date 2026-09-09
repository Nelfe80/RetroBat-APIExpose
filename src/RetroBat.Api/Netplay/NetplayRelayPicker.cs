using System.Diagnostics;
using System.Net.Sockets;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Choisir le relais netplay le plus proche, par MESURE.
///
/// La liste des relais n'est pas dans RetroArch : c'est une configuration du SERVEUR de lobby
/// (une table `relay:` dans son YAML — source lue). `netplay_mitm_server` contient une POIGNEE
/// que RetroArch resout via `GET /tunnel?name=&lt;poignee&gt;`. D'ou cette table, et la possibilite
/// de la verifier : une poignee inconnue rend 404.
///
/// On mesure au lieu de deduire de la geographie. L'aide de RetroArch dit elle-meme que les
/// emplacements proches « ont TENDANCE a » avoir moins de latence, et le nom des poignees
/// trompe : « madrid » est le relais europeen, hebergé en `europe-west1`.
///
/// On mesure depuis l'HOTE, jamais depuis un spectateur : c'est l'hote qui arbitre le protocole,
/// c'est son run qui peut etre certifie, et un spectateur n'envoie aucune entree — sa latence
/// n'est qu'un retard d'image.
/// </summary>
public sealed class NetplayRelayPicker
{
    /// <summary>
    /// Les relais du lobby libretro, au 2026-09-09. Poignee -> adresse.
    ///
    /// Ecrits ici plutot que redemandes au lobby a chaque fois : la table bouge tres rarement,
    /// et la sonde de latence a besoin des ADRESSES, pas des poignees. Si le lobby en ajoutait
    /// un, on le verrait dans les `mitm_ip` de sa liste.
    /// </summary>
    private static readonly (string Poignee, string Adresse)[] Relais =
    [
        ("madrid", "europe-west1.relay.retroarch.com"),
        ("nyc", "us-east1.relay.retroarch.com"),
        ("singapore", "asia-southeast1.relay.retroarch.com"),
        ("saopaulo", "southamerica-east1.relay.retroarch.com"),
    ];

    private const int Port = 55435;

    /// <summary>Une journee : la position d'une borne ne change pas d'une partie a l'autre.</summary>
    private static readonly TimeSpan Fraicheur = TimeSpan.FromHours(24);

    private readonly ILogger<NetplayRelayPicker> _logger;
    private readonly SemaphoreSlim _verrou = new(1, 1);

    private string? _choix;
    private DateTime _mesureLe = DateTime.MinValue;

    public NetplayRelayPicker(ILogger<NetplayRelayPicker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// La poignee du relais le plus proche. Mesure une fois par jour ; entre-temps, la reponse
    /// est immediate — on ne va pas payer quatre allers-retours avant chaque partie.
    /// </summary>
    public async Task<string> ChoisirAsync(CancellationToken ct = default)
    {
        if (_choix is not null && DateTime.UtcNow - _mesureLe < Fraicheur)
        {
            return _choix;
        }

        await _verrou.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Une seconde requete arrivee pendant la mesure profite du resultat.
            if (_choix is not null && DateTime.UtcNow - _mesureLe < Fraicheur)
            {
                return _choix;
            }

            string? meilleur = null;
            var meilleurTemps = double.MaxValue;

            foreach (var (poignee, adresse) in Relais)
            {
                var temps = await MesurerAsync(adresse, ct).ConfigureAwait(false);
                if (temps is null)
                {
                    continue;
                }
                _logger.LogDebug("Netplay : relais {Poignee} a {Temps} ms.", poignee, (int)temps.Value);
                if (temps.Value < meilleurTemps)
                {
                    meilleurTemps = temps.Value;
                    meilleur = poignee;
                }
            }

            if (meilleur is null)
            {
                // Aucun relais joignable : on garde le dernier choix connu, ou le defaut. Mieux
                // vaut tenter avec un relais peut-etre lointain que de refuser d'heberger.
                _logger.LogWarning("Netplay : aucun relais n'a repondu, on garde {Choix}.", _choix ?? Relais[0].Poignee);
                return _choix ?? Relais[0].Poignee;
            }

            _choix = meilleur;
            _mesureLe = DateTime.UtcNow;
            _logger.LogInformation("Netplay : relais retenu {Poignee} ({Temps} ms).", meilleur, (int)meilleurTemps);
            return meilleur;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>L'adresse d'une poignee, pour la journaliser ou la verifier.</summary>
    public static string? AdresseDe(string poignee)
    {
        foreach (var (p, a) in Relais)
        {
            if (string.Equals(p, poignee, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }
        return null;
    }

    /// <summary>
    /// Le temps d'ouverture d'une connexion TCP, en millisecondes, ou null si injoignable.
    ///
    /// Deux essais, on garde le MEILLEUR : une mesure de latence a un plancher et du bruit
    /// au-dessus ; c'est le plancher qui renseigne.
    /// </summary>
    private static async Task<double?> MesurerAsync(string adresse, CancellationToken ct)
    {
        double? meilleur = null;
        for (var essai = 0; essai < 2; essai++)
        {
            var chrono = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delai.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(adresse, Port, delai.Token).ConfigureAwait(false);
                chrono.Stop();
                var ms = chrono.Elapsed.TotalMilliseconds;
                if (meilleur is null || ms < meilleur.Value)
                {
                    meilleur = ms;
                }
            }
            catch (Exception)
            {
                // Injoignable, ou DNS muet : ce relais ne compte pas.
            }
        }
        return meilleur;
    }
}
