using System.Text;
using System.Text.Json;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Pose les réglages certifiés d'un jeu AVANT que l'émulateur ne le charge.
///
/// Un score n'est comparable qu'à réglages égaux. Jusqu'ici la borne le découvrait à la fin de
/// la partie : le profil n'épinglait que des EMPREINTES, et une empreinte ne se remonte pas, donc
/// la plateforme ne pouvait ni nommer le réglage fautif ni le corriger. Le profil publie
/// désormais ses réglages en clair (<c>core_options_expected</c>) ; ce service les dépose dans
/// <c>plugins\APIExpose\wrapper\certified.txt</c>, et le listener répond ces valeurs au cœur au
/// chargement. Les fichiers de RetroArch ne sont pas réécrits : RetroBat réécrit les siens à
/// chaque lancement, la course serait perdue d'avance.
///
/// Le fichier nomme la ROM à laquelle il s'applique et disparaît à la fin de la partie : rien ne
/// se force sur un jeu que la borne n'a pas préparé.
///
/// Les fonctions du frontend qui font refuser un score (rembobinage, run-ahead, sauvegarde
/// automatique) ne sont pas des options de cœur : elles se neutralisent par les réglages
/// RetroBat du jeu, dans es_settings.cfg, dès la sélection dans le menu, pour que le lanceur les
/// lise. Rien ne change pour les autres jeux ni pour les réglages globaux du joueur.
/// </summary>
public sealed class CertifiedSettingsService : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ApiContext _context;
    private readonly RomCanonicalResolver _canonical;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly IEsSettingsStore _esSettings;
    private readonly RetroBat.Api.Replay.Playback.ReplayPlaybackService? _playback;
    private readonly NvramSnapshotService? _nvram;
    // Les NVRAM epinglees par profil (« fbneo/19xx.nv »), apprises avec les reglages attendus.
    private readonly Dictionary<string, IReadOnlyList<string>> _epingles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CertifiedSettingsService>? _logger;
    private IDisposable? _abonnement;
    // Un jeu ouvert se reconnait a la selection dans le menu, ou l'on passe des dizaines de
    // fois par minute : la reponse de la plateforme est gardee dix minutes par jeu.
    private readonly Dictionary<string, (DateTime Jusqua, Dictionary<string, string>? Valeurs)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _verrou = new();
    private string _dernierPrepare = "";

    public CertifiedSettingsService(
        IEventBus bus,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ApiContext context,
        RomCanonicalResolver canonical,
        IOptionsMonitor<ApiExposeOptions> options,
        IEsSettingsStore esSettings,
        ILogger<CertifiedSettingsService>? logger = null,
        RetroBat.Api.Replay.Playback.ReplayPlaybackService? playback = null,
        NvramSnapshotService? nvram = null)
    {
        _playback = playback;
        _nvram = nvram;
        _bus = bus;
        _httpFactory = httpFactory;
        _devices = devices;
        _context = context;
        _canonical = canonical;
        _options = options;
        _esSettings = esSettings;
        _logger = logger;
    }

    /// <summary>
    /// Les fonctions du frontend qui font refuser un score, et la cle RetroBat qui les commande,
    /// par jeu : le lanceur lit <c>systeme["rom"].cle</c> dans es_settings.cfg avant chaque
    /// lancement. Rewind vaut « auto » dans RetroBat, c'est-a-dire ALLUME pour presque tous les
    /// coeurs : chaque nouveau joueur se faisait refuser pour rembobinage sans avoir rien
    /// touche (testeur sur Sonic, 2026-09-17, deux parties perdues avant de trouver l'option).
    /// </summary>
    private static readonly (string Cle, string Valeur, string Nom)[] FonctionsFrontend =
    {
        ("rewind", "0", "rembobinage (Rewind)"),
        ("runahead", "0", "run-ahead"),
        ("autosave", "0", "sauvegarde d'état automatique"),
    };

    /// <summary>La cle par jeu du lanceur : `megadrive["Sonic The Hedgehog (USA, Europe).zip"].rewind`.</summary>
    private static string ClePartie(string systemId, string romFile, string cle)
        => systemId + "[\"" + romFile.Replace("=", "").Replace("#", "") + "\"]." + cle;

    /// <summary>
    /// Ce que le frontend a d'actif pour CE lancement et qui fera refuser le score. Lu dans le
    /// retroarch.cfg que le lanceur vient d'ecrire, pas dans es_settings.cfg : c'est ce que
    /// RetroArch a reellement charge. Une cle neutralisee apres le lancement ne vaut que pour le
    /// suivant, et l'avant-partie doit dire la verite de celui-ci. Vide quand tout est neutre,
    /// ou quand l'emulateur n'est pas RetroArch.
    /// </summary>
    public IReadOnlyList<string> DangersFrontendActifs()
    {
        var jeu = _context.Ui.Running ?? _context.Ui.Selected;
        var emulateur = (jeu?.Launch?.Emulator ?? "").ToLowerInvariant();
        var coeur = jeu?.Launch?.Core ?? "";
        if (emulateur.Length > 0 && !emulateur.Contains("retroarch") && !emulateur.Contains("libretro") && coeur.Length == 0)
            return Array.Empty<string>();
        Dictionary<string, string> cfg;
        try
        {
            cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ligne in File.ReadLines(RetroBatPaths.RetroArchConfigPath))
            {
                var eq = ligne.IndexOf('=');
                if (eq <= 0) continue;
                var cle = ligne[..eq].Trim();
                var valeur = ligne[(eq + 1)..].Trim().Trim('"');
                if (cle.Length > 0) cfg[cle] = valeur;
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        static bool Vrai(Dictionary<string, string> c, string cle) => c.TryGetValue(cle, out var v) && v is "true" or "1";
        var dangers = new List<string>();
        if (Vrai(cfg, "rewind_enable")) dangers.Add(FonctionsFrontend[0].Cle);
        if (Vrai(cfg, "run_ahead_enabled") || Vrai(cfg, "preemptive_frames_enable")) dangers.Add(FonctionsFrontend[1].Cle);
        if (Vrai(cfg, "savestate_auto_load")) dangers.Add(FonctionsFrontend[2].Cle);
        return dangers;
    }

    /// <summary>
    /// Neutralise, pour CE jeu seulement, les fonctions du frontend qui feraient refuser le
    /// score. Ecrit dans es_settings.cfg les cles par jeu que le lanceur lit au lancement ;
    /// rien ne change pour les autres jeux ni pour les reglages globaux du joueur.
    /// </summary>
    private void NeutraliserFrontend(string systemId, string romFile) => NeutraliserPlusieurs([(systemId, romFile)]);

    /// <summary>
    /// Les memes cles pour plusieurs jeux, en une seule ecriture d'es_settings.cfg.
    /// </summary>
    private void NeutraliserPlusieurs(IReadOnlyList<(string Systeme, string Fichier)> jeux)
    {
        if (jeux.Count == 0) return;
        try
        {
            var modifies = new List<string>();
            _esSettings.Update(document =>
            {
                var root = document.Root ?? throw new InvalidOperationException("es_settings.cfg sans racine.");
                var modifie = false;
                foreach (var (systemId, romFile) in jeux)
                {
                    var celuiCi = false;
                    foreach (var (cle, valeur, _) in FonctionsFrontend)
                    {
                        var nom = ClePartie(systemId, romFile, cle);
                        var existant = root.Elements().FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, nom, StringComparison.OrdinalIgnoreCase));
                        if (existant is not null)
                        {
                            if (string.Equals(existant.Attribute("value")?.Value, valeur, StringComparison.Ordinal)) continue;
                            existant.SetAttributeValue("value", valeur);
                            celuiCi = true;
                            continue;
                        }
                        root.Add(new XText(Environment.NewLine + "  "));
                        root.Add(new XElement("string", new XAttribute("name", nom), new XAttribute("value", valeur)));
                        celuiCi = true;
                    }
                    if (celuiCi) modifies.Add(romFile + " (" + systemId + ")");
                    modifie |= celuiCi;
                }
                return modifie;
            });
            if (modifies.Count > 0)
            {
                _logger?.LogInformation("Reglages certifies : rewind, run-ahead et sauvegarde auto neutralises pour {Jeux}.", string.Join(", ", modifies));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reglages certifies : es_settings.cfg non modifiable, le frontend garde ses reglages.");
        }
    }

    /// <summary>
    /// LES JEUX DE LA COLLECTION WORLD SCORING, NEUTRALISES DES LE DEMARRAGE (2026-09-25).
    ///
    /// Les cles par jeu n'etaient posees qu'a la SELECTION dans le menu. FreshOne a lance Sonic
    /// 18 secondes apres le demarrage de son API, sans l'avoir selectionne depuis : le rembobinage
    /// etait actif, l'avant-partie l'a dit, et 56 minutes de jeu ont ete refusees. EmulationStation
    /// peut aussi effacer des cles ajoutees pendant qu'il tourne, en resauvegardant ses reglages.
    ///
    /// Au demarrage de l'API, avant qu'EmulationStation ne s'ouvre (son hook de demarrage attend
    /// l'API), chaque jeu de la collection recoit ses cles : ES les lit en demarrant et les garde.
    /// Refait apres chaque partie, pour les jeux entres dans la collection entre-temps. La liste
    /// vient du fichier de collection deja sur le disque : aucun appel reseau.
    /// </summary>
    private void NeutraliserLaCollection()
    {
        if (!_options.CurrentValue.NelfePlay.ForceCertifiedSettings) return;
        var collection = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "collections",
            "custom-" + NelfePlayScoringCollectionSyncService.CollectionName + ".cfg");
        NeutraliserPlusieurs(JeuxDeLaCollection(collection, RetroBatPaths.RomsRoot,
            Path.GetDirectoryName(RetroBatPaths.EmulationStationConfigRoot) ?? RetroBatPaths.EmulationStationConfigRoot));
    }

    /// <summary>
    /// Les jeux d'un fichier de collection d'EmulationStation : (systeme, fichier), le systeme etant
    /// le dossier de la ROM sous roms/. Un « ~ » designe le home d'EmulationStation.
    /// </summary>
    internal static IReadOnlyList<(string Systeme, string Fichier)> JeuxDeLaCollection(string cheminCollection, string racineRoms, string homeEs)
    {
        var jeux = new List<(string, string)>();
        if (!File.Exists(cheminCollection)) return jeux;
        foreach (var brute in File.ReadLines(cheminCollection))
        {
            var ligne = brute.Trim();
            if (ligne.Length == 0) continue;
            try
            {
                var chemin = ligne.StartsWith('~') ? Path.GetFullPath(homeEs + ligne[1..]) : Path.GetFullPath(ligne);
                var morceaux = Path.GetRelativePath(racineRoms, chemin).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (morceaux.Length < 2 || morceaux[0] == ".." || Path.IsPathRooted(morceaux[0])) continue;
                jeux.Add((morceaux[0], Path.GetFileName(chemin)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Une ligne illisible ne prive pas les autres jeux de leurs cles.
            }
        }
        return jeux;
    }

    /// <summary>Le fichier lu par le listener au chargement.</summary>
    public static string FilePath => Path.Combine(RetroBatPaths.RetroBatRoot, "plugins", "APIExpose", "wrapper", "certified.txt");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Avant l'ouverture d'EmulationStation, qui attend l'API : voir NeutraliserLaCollection.
        NeutraliserLaCollection();
        _abonnement = _bus.Subscribe<EventEnvelope>(e =>
        {
            var type = e.Type?.ToLowerInvariant();
            // A la SELECTION deja : le lanceur lit es_settings.cfg au lancement, il faut que les
            // cles par jeu y soient avant. Au demarrage aussi, pour un lancement venu d'ailleurs.
            if (type == "ui.game.selected" || type == "ui.game.started.raw" || type == "ui.game.started")
            {
                _ = PreparerAsync(type == "ui.game.selected", CancellationToken.None);
            }
            else if (type == "ui.game.ended" || type == "ui.game.ended.raw")
            {
                Effacer();
                NeutraliserLaCollection();
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Effacer();
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    private async Task PreparerAsync(bool selection, CancellationToken ct)
    {
        try
        {
            if (!_options.CurrentValue.NelfePlay.ForceCertifiedSettings)
            {
                Effacer();
                return;
            }
            // La lecture d'un replay n'est pas une partie : rien a forcer, rien a neutraliser.
            if (_playback?.IsBusy == true) return;

            var jeu = selection ? _context.Ui.Selected : (_context.Ui.Running ?? _context.Ui.Selected);
            var chemin = jeu?.GamePath;
            if (string.IsNullOrWhiteSpace(chemin))
            {
                _logger?.LogInformation("Reglages certifies : aucun chemin de jeu au demarrage.");
                return;
            }

            // Le profil d'un jeu d'arcade est depose sous « arcade », quel que soit le dossier
            // RetroBat (mame, fbneo...) : meme regle que le fournisseur wrapper a la soumission.
            var systemeEs = jeu?.SystemId ?? string.Empty;
            var systeme = SystemeDuProfil(systemeEs);
            var fichier = Path.GetFileName(chemin);
            // Une selection qui ne change pas de jeu ne refait rien : le menu en emet beaucoup.
            var empreinte = systemeEs + "|" + fichier;
            lock (_verrou)
            {
                if (selection && _dernierPrepare == empreinte) return;
                _dernierPrepare = empreinte;
            }
            var romGroup = _canonical.ResolveScoreSlug(systeme, fichier, null, null);
            if (string.IsNullOrWhiteSpace(romGroup))
            {
                _logger?.LogInformation("Reglages certifies : {Fichier} ({Systeme}) sans groupe de score, rien a forcer.", fichier, systeme);
                return;
            }

            var attendus = await ValeursAttenduesEnCacheAsync(systeme, romGroup!, ct).ConfigureAwait(false);
            if (attendus is null)
            {
                // Jeu non ouvert au scoring : rien a forcer, rien a neutraliser.
                if (!selection) Effacer();
                return;
            }

            // Jeu ouvert : les fonctions du frontend d'abord, elles se reglent avant le lancement.
            NeutraliserFrontend(systemeEs, fichier);
            // Puis la NVRAM que le profil epingle, A LA SELECTION seulement : au lancement, la
            // capture du depart est deja partie, et l'effacer apres coup ferait mentir le passeport.
            // Apres chaque partie certifiee, le passeport l'efface aussi (NvramSnapshotService).
            IReadOnlyList<string>? epingles;
            lock (_verrou) _epingles.TryGetValue(systeme + "|" + romGroup, out epingles);
            if (selection && epingles is { Count: > 0 } && _nvram is not null)
            {
                _nvram.EffacerEpinglees(systemeEs, Path.GetFileNameWithoutExtension(chemin), epingles);
            }
            if (attendus.Count == 0)
            {
                if (!selection) Effacer();
                return;
            }

            Ecrire(Path.GetFileNameWithoutExtension(chemin), attendus);
            _logger?.LogInformation("Reglages certifies poses pour {Rom} : {Nombre} valeur(s).", romGroup, attendus.Count);
        }
        catch (Exception ex)
        {
            // Jamais au prix de la partie : sans fichier, le listener ne force rien.
            _logger?.LogWarning(ex, "Reglages certifies : preparation impossible.");
            Effacer();
        }
    }

    private async Task<Dictionary<string, string>?> ValeursAttenduesEnCacheAsync(string systemId, string romGroup, CancellationToken ct)
    {
        var cle = systemId + "|" + romGroup;
        lock (_verrou)
        {
            if (_cache.TryGetValue(cle, out var entree) && entree.Jusqua > DateTime.UtcNow) return entree.Valeurs;
        }
        var valeurs = await ValeursAttenduesAsync(systemId, romGroup, ct).ConfigureAwait(false);
        lock (_verrou)
        {
            _cache[cle] = (DateTime.UtcNow.AddMinutes(10), valeurs);
        }
        return valeurs;
    }

    /// <summary>
    /// Les valeurs du profil ouvert : null si le jeu n'est pas ouvert (ou borne sans compte),
    /// un dictionnaire, vide au besoin, s'il l'est.
    /// </summary>
    private async Task<Dictionary<string, string>?> ValeursAttenduesAsync(string systemId, string romGroup, CancellationToken ct)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return null;

        using var client = _httpFactory.CreateClient(nameof(CertifiedSettingsService));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

        var url = $"/api/v1/agent/scores/profile?system_id={Uri.EscapeDataString(systemId)}&rom_group={Uri.EscapeDataString(romGroup)}";
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("profile", out var profil)) return null;
        var epingles = NvramSnapshotService.EpinglesDuProfil(profil);
        lock (_verrou) _epingles[systemId + "|" + romGroup] = epingles;
        if (!profil.TryGetProperty("core_options_expected", out var attendus) || attendus.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // Plusieurs entrées quand un profil sert plusieurs moteurs. Le cœur chargé n'est pas encore
        // connu à ce moment (il lira ses options au chargement), mais chaque moteur a ses propres
        // clés : l'union convient. Une clé donnée par deux entrées avec des valeurs différentes est
        // ambiguë, donc écartée plutôt que devinée.
        var valeurs = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambigues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entree in attendus.EnumerateArray())
        {
            if (!entree.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object) continue;
            foreach (var paire in v.EnumerateObject())
            {
                if (paire.Value.ValueKind != JsonValueKind.String) continue;
                var valeur = paire.Value.GetString() ?? string.Empty;
                if (valeurs.TryGetValue(paire.Name, out var deja) && deja != valeur) ambigues.Add(paire.Name);
                else valeurs[paire.Name] = valeur;
            }
        }
        foreach (var cle in ambigues) valeurs.Remove(cle);
        return valeurs;
    }

    private static string SystemeDuProfil(string systemId)
    {
        var s = systemId.Trim().ToLowerInvariant();
        return s is "mame" or "mame64" or "fbneo" or "fba" or "neogeo" or "cps1" or "cps2" or "cps3" or "cave" or "atomiswave" or "naomi" or "naomi2"
            ? "arcade" : s;
    }

    private static void Ecrire(string romBasename, Dictionary<string, string> valeurs)
    {
        var texte = new StringBuilder();
        texte.Append("rom=").Append(romBasename).Append('\n');
        foreach (var paire in valeurs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // Une valeur multiligne n'existe pas côté libretro ; on s'en assure quand même.
            if (paire.Key.Contains('=') || paire.Value.Contains('\n') || paire.Value.Contains('\r')) continue;
            texte.Append("opt.").Append(paire.Key).Append('=').Append(paire.Value).Append('\n');
        }
        var chemin = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, texte.ToString(), new UTF8Encoding(false));
    }

    private static void Effacer()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // Un fichier verrouillé ne vaut pas d'interrompre quoi que ce soit : il nomme sa ROM,
            // donc il ne peut rien forcer sur un autre jeu.
        }
    }

    private string? ResolveCredential()
    {
        var paired = _devices.GetCredential();
        if (!string.IsNullOrEmpty(paired)) return paired;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }
        }
        catch { }
        return null;
    }
}
