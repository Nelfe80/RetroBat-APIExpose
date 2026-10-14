using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE FACTEUR D'AGRANDISSEMENT DES FILMS, JEU PAR JEU (decision du 14 octobre 2026). YouTube ne diffuse 60 images par
/// seconde qu'a partir de 720 lignes ; en dessous, il passe a 30. Le film d'un jeu doit donc atteindre 720 lignes sur
/// son petit cote, avec le plus petit facteur entier : x4 pour un jeu en 224 lignes, x3 en 240, x5 pour la Game Boy.
/// <list type="bullet">
/// <item>RetroArch lit le facteur (<c>video_record_scale_factor</c>) a son demarrage. Il se pose dans retroarch.cfg
/// depuis le script game-start d'ES, qu'ES attend avant de lancer le jeu, donc avant le lanceur de RetroBat.</item>
/// <item>La definition d'un jeu se lit dans le journal de lancement (« [Core] Geometry: 224x384 ») a la fin de la
/// partie, et se garde par jeu. Un jeu jamais lance prend la definition de sa machine quand elle est connue, sinon
/// x4.</item>
/// </list>
/// </summary>
public sealed class EchelleDesFilms : IHostedService, IDisposable
{
    /// <summary>Le petit cote voulu : YouTube diffuse 60 images par seconde a partir de 720 lignes.</summary>
    public const int LignesVoulues = 720;

    /// <summary>Le facteur d'un jeu dont on ne sait rien (224 a 240 lignes, l'arcade et les consoles de salon).</summary>
    public const int FacteurParDefaut = 4;

    private const int FacteurMax = 8;
    private const string Cle = "video_record_scale_factor";

    /// <summary>Les machines a petit ecran, par leur dossier de ROM : leur definition d'origine (largeur, hauteur).</summary>
    private static readonly Dictionary<string, (int L, int H)> Machines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gb"] = (160, 144), ["gbc"] = (160, 144), ["gb2players"] = (160, 144), ["gbc2players"] = (160, 144),
        ["gamegear"] = (160, 144), ["gba"] = (240, 160), ["ngp"] = (160, 152), ["ngpc"] = (160, 152),
        ["wswan"] = (224, 144), ["wswanc"] = (224, 144), ["lynx"] = (160, 102), ["pokemini"] = (96, 64),
        ["supervision"] = (160, 160), ["gameandwatch"] = (320, 200),
    };

    private static readonly Regex Geometrie = new(@"\[Core\]\s+Geometry:\s*(?<l>\d+)x(?<h>\d+)", RegexOptions.CultureInvariant);

    private readonly IEventBus _bus;
    private readonly ILogger<EchelleDesFilms> _logger;
    private readonly string _fichier;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private DateTime _debutDuJeu = DateTime.MinValue;

    public EchelleDesFilms(IEventBus bus, ILogger<EchelleDesFilms> logger)
    {
        _bus = bus;
        _logger = logger;
        _fichier = Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "definitions.json");
    }

    /// <summary>Le plus petit facteur entier qui porte le petit cote a 720 lignes (1 a 8).</summary>
    public static int Facteur(int largeur, int hauteur)
    {
        var petit = Math.Min(largeur, hauteur);
        if (petit <= 0) return FacteurParDefaut;
        return Math.Clamp((LignesVoulues + petit - 1) / petit, 1, FacteurMax);
    }

    /// <summary>La derniere definition annoncee par le coeur dans le journal de lancement, ou null. Pur.</summary>
    internal static (int L, int H)? GeometrieDuJournal(IEnumerable<string> lignes)
    {
        (int, int)? trouvee = null;
        foreach (var ligne in lignes)
        {
            var m = Geometrie.Match(ligne);
            if (m.Success
                && int.TryParse(m.Groups["l"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var l)
                && int.TryParse(m.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                && l > 0 && h > 0)
                trouvee = (l, h);
        }
        return trouvee;
    }

    /// <summary>La cle d'un jeu : son chemin, sans casse ni separateurs differents.</summary>
    internal static string CleDuJeu(string chemin) => chemin.Trim().Replace('/', '\\').ToLowerInvariant();

    /// <summary>
    /// Le chemin du jeu dans les arguments du script game-start d'ES : le premier argument, ou le second quand le
    /// premier est le systeme. null si rien ne ressemble a un fichier. Pur.
    /// </summary>
    internal static (string? Systeme, string? Chemin) LancementDuScript(string arguments)
    {
        var morceaux = new List<string>();
        var courant = new StringBuilder();
        var guillemets = false;
        foreach (var c in arguments ?? "")
        {
            if (c == '"') { guillemets = !guillemets; continue; }
            if (char.IsWhiteSpace(c) && !guillemets)
            {
                if (courant.Length > 0) { morceaux.Add(courant.ToString()); courant.Clear(); }
                continue;
            }
            courant.Append(c);
        }
        if (courant.Length > 0) morceaux.Add(courant.ToString());
        static bool Fichier(string s) => s.Contains('\\') || s.Contains('/') || Path.HasExtension(s);
        for (var i = 0; i < morceaux.Count; i++)
        {
            if (!Fichier(morceaux[i])) continue;
            // Le dossier de la ROM est le systeme (roms\gb\Tetris.zip).
            var dossier = Path.GetFileName(Path.GetDirectoryName(morceaux[i]) ?? "");
            return (string.IsNullOrEmpty(dossier) ? (i > 0 ? morceaux[0] : null) : dossier, morceaux[i]);
        }
        return (null, null);
    }

    /// <summary>
    /// Avant le lancement (script game-start d'ES) : pose dans retroarch.cfg le facteur du jeu qui va demarrer. Rien si
    /// RetroArch tourne deja (il ne relirait pas le fichier) ou si le facteur est deja le bon.
    /// </summary>
    public void PoserPourLeLancement(string argumentsDuScript)
    {
        try
        {
            _debutDuJeu = DateTime.UtcNow;
            var (systeme, chemin) = LancementDuScript(argumentsDuScript);
            if (chemin is null) return;
            var connue = Lire().TryGetValue(CleDuJeu(chemin), out var d) ? d : ((int L, int H)?)null;
            var machine = systeme is not null && Machines.TryGetValue(systeme, out var m) ? m : ((int L, int H)?)null;
            var facteur = connue is { } c ? Facteur(c.L, c.H) : machine is { } mm ? Facteur(mm.L, mm.H) : FacteurParDefaut;

            var cfg = RetroBatPaths.RetroArchConfigPath;
            if (!File.Exists(cfg) || Process.GetProcessesByName("retroarch").Length > 0) return;
            var contenu = File.ReadAllText(cfg);
            var nouveau = ReplayCaptureService.AvecReglage(contenu, Cle, facteur.ToString(CultureInfo.InvariantCulture));
            if (nouveau is null) return;
            File.WriteAllText(cfg, nouveau, new UTF8Encoding(false));
            _logger.LogInformation("Capture : {Jeu} sera filme en x{Facteur} ({Source}).", Path.GetFileName(chemin), facteur,
                connue is { } k ? "definition " + k.L + "x" + k.H : machine is { } mk ? "machine " + systeme + " " + mk.L + "x" + mk.H : "par defaut");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture : facteur du jeu non pose, le precedent reste.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement); }
        catch (Exception ex) { _logger.LogDebug(ex, "Capture : definitions des jeux non suivies."); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A la fin d'un jeu : sa definition, lue dans le journal de ce lancement, se garde pour la prochaine fois.</summary>
    private void SurEvenement(EventEnvelope e)
    {
        if (e.Type != "ui.game.ended") return;
        try
        {
            var chemin = JsonSerializer.SerializeToElement(e.Payload) is { ValueKind: JsonValueKind.Object } el
                && el.TryGetProperty("GamePath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var journal = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation", "es_launch_stdout.log");
            if (string.IsNullOrWhiteSpace(chemin) || !File.Exists(journal)) return;
            // Un journal d'avant ce lancement n'est pas celui de ce jeu.
            if (File.GetLastWriteTimeUtc(journal) < _debutDuJeu) return;
            if (GeometrieDuJournal(File.ReadLines(journal)) is not { } g) return;
            lock (_verrou)
            {
                var toutes = Lire();
                if (toutes.TryGetValue(CleDuJeu(chemin), out var avant) && avant == g) return;
                toutes[CleDuJeu(chemin)] = g;
                Directory.CreateDirectory(Path.GetDirectoryName(_fichier)!);
                var temporaire = _fichier + ".tmp";
                File.WriteAllText(temporaire, JsonSerializer.Serialize(
                    toutes.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.L, kv.Value.H })), new UTF8Encoding(false));
                File.Move(temporaire, _fichier, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture : definition du jeu non gardee.");
        }
    }

    private Dictionary<string, (int L, int H)> Lire()
    {
        try
        {
            if (!File.Exists(_fichier)) return new(StringComparer.Ordinal);
            var brut = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(_fichier)) ?? new();
            return brut.Where(kv => kv.Value is { Length: 2 } && kv.Value[0] > 0 && kv.Value[1] > 0)
                .ToDictionary(kv => kv.Key, kv => (kv.Value[0], kv.Value[1]), StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new(StringComparer.Ordinal);
        }
    }

    public void Dispose() => _abonnement?.Dispose();
}
