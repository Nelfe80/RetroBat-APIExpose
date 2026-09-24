using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// CE QUE CHAQUE CŒUR SAIT LIRE, appris par l'observation et retenu d'un lancement à l'autre.
///
/// Un joueur a lancé Altered Beast quatre fois sous <c>mame2003_plus</c> le 24 septembre 2026. Le
/// prévol a dit « Partie certifiable » quatre fois, et rien n'est jamais remonté : ce cœur
/// n'expose pas sa RAM, le wrapper l'enveloppe correctement mais ne lit rien, et personne ne le
/// lui a dit. Il a fini par déplacer sa ROM vers <c>roms/fbneo</c> pour que ça marche.
///
/// EXPOSER SA RAM EST UNE PROPRIÉTÉ DU CŒUR, PAS DU JEU. Les relevés le montrent : FBNeo répond
/// <c>system_ram=OK</c> sur les cinq jeux mesurés, seule la TAILLE change d'un jeu à l'autre. Une
/// entrée par cœur suffit donc, là où un cache par couple jeu-cœur serait du gaspillage.
///
/// Apprise plutôt qu'écrite à la main. Une liste tenue à la main vieillirait à chaque nouveau
/// cœur et personne ne la maintiendrait — c'est le reproche qu'on peut faire à une base de CRC.
/// Ici, la borne écrit une ligne la première fois qu'elle voit un cœur, et n'y revient plus.
/// </summary>
public sealed class CoreMemoryCapability
{
    /// <summary>
    /// Le procès-verbal que le wrapper émet une fois par lancement. Il porte les QUATRE termes de
    /// la décision, et c'est la même condition que le wrapper applique pour lui-même :
    /// <c>(system_ram &amp;&amp; taille &gt; 0) || (arcade &amp;&amp; blocs &gt; 0)</c>.
    /// </summary>
    /// <example>Core=fbneo_libretro arcade=YES system_ram=OK system_ram_size=94208 memory_map_blocks=0</example>
    private static readonly Regex Proces = new(
        @"Core=(?<core>\S+)\s+arcade=(?<arcade>\w+)\s+system_ram=(?<ram>\w+)\s+system_ram_size=(?<taille>\d+)\s+memory_map_blocks=(?<blocs>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="Core">Le nom du cœur, pour que la liste se lise.</param>
    /// <param name="Measures">Vrai quand ce cœur a de quoi être lu.</param>
    /// <param name="Seen">Quand on l'a constaté.</param>
    /// <param name="Evidence">La ligne exacte qui l'a établi : un verdict doit pouvoir se relire.</param>
    public sealed record Verdict(string Core, bool Measures, DateTime Seen, string Evidence);

    private readonly object _sync = new();
    private readonly Dictionary<string, Verdict> _parCoeur = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fichier;
    private readonly ILogger<CoreMemoryCapability>? _logger;
    private IDisposable? _abonnement;
    private LiveContestOverlayService? _bandeau;
    /// <summary>Le dernier coeur annonce muet : on ne repete pas le bandeau a chaque partie.</summary>
    private string? _dernierMuet;

    public CoreMemoryCapability(string dossierEtat, ILogger<CoreMemoryCapability>? logger = null)
    {
        _fichier = Path.Combine(dossierEtat, "core-memory.json");
        _logger = logger;
        Relire();
    }

    /// <summary>
    /// Ecoute les proces-verbaux que le wrapper publie a chaque lancement. Le provider ne juge
    /// pas : il publie sa ligne, et la liste se remplit ici.
    /// </summary>
    public void Ecouter(IEventBus bus, LiveContestOverlayService? bandeau = null)
    {
        _bandeau = bandeau;
        _abonnement?.Dispose();
        _abonnement = bus.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "wrapper.diagnostic", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var ligne = Ligne(e.Payload);
            if (ligne.Length == 0)
            {
                return;
            }

            var verdict = Observer(ligne);

            // RIEN NE SERA MESURE, ET LE JOUEUR L'APPREND MAINTENANT.
            //
            // Ce proces-verbal arrive a la premiere image, donc une seconde apres le lancement :
            // bien avant que le joueur ait mis sa piece. C'est le seul moment ou l'information
            // sert encore a quelque chose -- a la fin de la partie il est trop tard, et c'est
            // exactement ce qui s'est produit quatre fois de suite le 24 septembre 2026.
            //
            // L'etat est LOCAL : il se dit sans reseau, comme l'alerte du wrapper absent.
            if (verdict is { Measures: false } && _dernierMuet != verdict.Core)
            {
                _dernierMuet = verdict.Core;
                _bandeau?.ShowTop(
                    "SCORING",
                    "Aucun score ne sera mesuré",
                    "ce cœur n'expose pas sa mémoire : " + verdict.Core,
                    8000);
            }
            else if (verdict is { Measures: true })
            {
                _dernierMuet = null;
            }
        });
    }

    private static string Ligne(object? payload)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(payload);
            return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("Line", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Range un procès-verbal du wrapper. Rend le verdict quand la ligne en était un, sinon null.
    ///
    /// Le nom du cœur sert de clé faute de mieux ici : l'empreinte n'est pas dans cette ligne. Le
    /// rapporteur, lui, a <c>core_sha256</c> dans son attestation et peut affiner ensuite.
    /// </summary>
    public Verdict? Observer(string ligne)
    {
        var m = Proces.Match(ligne ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        var coeur = m.Groups["core"].Value;
        var arcade = m.Groups["arcade"].Value.Equals("YES", StringComparison.OrdinalIgnoreCase);
        var ram = m.Groups["ram"].Value.Equals("OK", StringComparison.OrdinalIgnoreCase);
        var taille = long.TryParse(m.Groups["taille"].Value, out var t) ? t : 0;
        var blocs = int.TryParse(m.Groups["blocs"].Value, out var b) ? b : 0;

        // La condition du wrapper, mot pour mot. La réécrire autrement ferait diverger les deux
        // jugements le jour où l'un des deux change.
        var mesure = (ram && taille > 0) || (arcade && blocs > 0);
        var verdict = new Verdict(coeur, mesure, DateTime.UtcNow, m.Value);

        lock (_sync)
        {
            // Un cœur déjà vu MESURER ne se déjuge pas sur un jeu : la carte mémoire est annoncée
            // par jeu, donc un titre sans blocs ne prouve rien contre le cœur. L'inverse, en
            // revanche, s'apprend : un cœur qu'on croyait muet et qui lit enfin est une bonne
            // nouvelle, et elle remplace l'ancienne.
            if (_parCoeur.TryGetValue(coeur, out var connu) && connu.Measures && !mesure)
            {
                return connu;
            }

            var nouveau = !_parCoeur.TryGetValue(coeur, out var avant) || avant.Measures != mesure;
            _parCoeur[coeur] = verdict;
            if (nouveau)
            {
                _logger?.LogInformation(
                    "Cœurs : {Coeur} {Verdict} (system_ram={Ram} taille={Taille} blocs={Blocs}).",
                    coeur, mesure ? "lit la mémoire" : "NE LIT RIEN", ram ? "OK" : "NULL", taille, blocs);
                Ecrire();
            }
        }

        return verdict;
    }

    /// <summary>Ce qu'on sait de ce cœur, ou null si on ne l'a jamais vu tourner.</summary>
    public Verdict? Connu(string coeur)
    {
        if (string.IsNullOrWhiteSpace(coeur))
        {
            return null;
        }

        lock (_sync)
        {
            return _parCoeur.GetValueOrDefault(coeur);
        }
    }

    /// <summary>La liste entière, pour l'état de la borne et le diagnostic.</summary>
    public IReadOnlyList<Verdict> Tout()
    {
        lock (_sync)
        {
            return [.. _parCoeur.Values.OrderBy(v => v.Core, StringComparer.OrdinalIgnoreCase)];
        }
    }

    private void Relire()
    {
        try
        {
            if (!File.Exists(_fichier))
            {
                return;
            }

            var lu = JsonSerializer.Deserialize<List<Verdict>>(File.ReadAllText(_fichier));
            if (lu is null)
            {
                return;
            }

            foreach (var v in lu.Where(v => !string.IsNullOrWhiteSpace(v.Core)))
            {
                _parCoeur[v.Core] = v;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Une liste illisible se réapprend au prochain lancement : elle n'a rien d'irremplaçable.
            _logger?.LogDebug(ex, "Cœurs : liste illisible, elle se réapprendra.");
        }
    }

    private void Ecrire()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fichier)!);
            File.WriteAllText(_fichier, JsonSerializer.Serialize(
                _parCoeur.Values.OrderBy(v => v.Core, StringComparer.OrdinalIgnoreCase),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Cœurs : liste non écrite.");
        }
    }
}
