using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Extend;

/// <summary>
/// EXTEND MODE (28/11, CDC Extend Mode v0.1 partie C) : derive de la ROM DU JOUEUR la ROM multijoueur d'une conversion.
/// Pour chaque fiche de <c>extend/catalogue.json</c> du Data Pack : cherche la source dans <c>roms\&lt;systeme&gt;</c> (fichier nu,
/// ou fichier unique d'un .zip ou d'un .7z : meme lecteur que la collection MULTIPLAYER, <see cref="ContenuDUneRom"/>), applique
/// le patch EN MEMOIRE, verifie le sha256 de la source, du patch et du resultat, ecrit dans un temporaire puis renomme en
/// <c>roms\&lt;systeme&gt;\&lt;result.file&gt;</c>, et inscrit le jeu dans la gamelist du systeme (<c>emulator</c> libretro, <c>core</c> de la fiche).
/// <para>
/// REGLES. Un ecart d'empreinte = rien n'est depose et la raison est journalisee. La ROM du joueur n'est jamais modifiee, deplacee
/// ni renommee. Une seule version d'une conversion a la fois : la ROM derivee d'une version anterieure (que NOUS avons ecrite,
/// retenue dans <c>state\nelfeplay\extend.json</c>) est remplacee ou retiree ; un fichier du meme nom qui n'est pas de nous n'est
/// jamais ecrase. Aucune ecriture hors de <c>roms\&lt;systeme&gt;</c> et de sa gamelist. Rien ne se fait pendant une partie.
/// </para>
/// <para>
/// DECLENCHEURS : 45 s apres le demarrage ; fin de partie ; changement vu par un sondage de 60 s (catalogue du Data Pack, fin d'une
/// synchronisation du Data Pack, contenu du dossier des ROMs du systeme : un depot de ROM, une mise a jour des gamelists qui en
/// apporte) ; et une passe de securite toutes les 30 min. Une passe sans changement ne lit que des listes de fichiers.
/// </para>
/// </summary>
public sealed class ExtendRomsService : BackgroundService
{
    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Sondage = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Securite = TimeSpan.FromMinutes(30);

    /// <summary>Ce que les essais remplacent : les chemins, l'etat des lieux et la gamelist.</summary>
    public sealed class Reglages
    {
        public string? RacineResources { get; init; }
        public string? RacineRoms { get; init; }
        public string? FichierEtat { get; init; }
        public Func<bool>? PartieEnCours { get; init; }
        public Func<bool>? Actif { get; init; }
        public Version? Api { get; init; }
        public Func<string, string, string, string, bool>? Declarer { get; init; }
        public Func<string, string, bool>? Retirer { get; init; }
        public Func<string, bool>? CoeurPose { get; init; }
    }

    private sealed class Retenue
    {
        public string? Version { get; set; }
        public string? File { get; set; }
        public string? Sha256 { get; set; }
        public string? System { get; set; }
    }

    private readonly IOptionsMonitor<ApiExposeOptions>? _options;
    private readonly DataPackSyncService? _dataPack;
    private readonly GamelistUpdateService? _gamelists;
    private readonly MultiplayerCollectionSyncService? _collection;
    private readonly IEventBus? _bus;
    private readonly ILogger<ExtendRomsService>? _logger;
    private readonly Reglages _reglages;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private readonly object _verrouEtat = new();
    private string? _derniereSignature;
    private IDisposable? _abonnement;
    private IReadOnlyList<ExtendEtat> _etats = [];
    private string? _raison = "pas encore passe";
    private DateTime? _dernierePasseUtc;

    public ExtendRomsService(IOptionsMonitor<ApiExposeOptions>? options = null, DataPackSyncService? dataPack = null,
        GamelistUpdateService? gamelists = null, MultiplayerCollectionSyncService? collection = null, IEventBus? bus = null,
        ILogger<ExtendRomsService>? logger = null, Reglages? reglages = null)
    {
        _options = options;
        _dataPack = dataPack;
        _gamelists = gamelists;
        _collection = collection;
        _bus = bus;
        _logger = logger;
        _reglages = reglages ?? new Reglages();
    }

    private string RacineResources => _reglages.RacineResources ?? Path.Combine(RetroBatPaths.PluginRoot, "resources");
    private string RacineRoms => _reglages.RacineRoms ?? RetroBatPaths.RomsRoot;
    private string FichierEtat => _reglages.FichierEtat ?? Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "extend.json");
    private string FichierCatalogue => Path.Combine(RacineResources, "extend", "catalogue.json");

    private Version Api => _reglages.Api
        ?? (Version.TryParse(ApiExposeVersion.Current.Split('+')[0], out var version) ? version : new Version(0, 0));

    /// <summary>Les ROMs derivees ne servent qu'a MULTIPLAYER : rien n'est depose quand sa collection est coupee.</summary>
    private bool Actif => _reglages.Actif?.Invoke()
        ?? (_options?.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true });

    private bool PartieEnCours => _reglages.PartieEnCours?.Invoke() ?? MultiplayerCoeursService.UnePartieTourne();

    /// <summary>L'etat de chaque fiche, tel que la derniere passe l'a laisse.</summary>
    public (IReadOnlyList<ExtendEtat> Fiches, string? Raison, DateTime? PasseUtc) Etat
    {
        get
        {
            lock (_verrouEtat)
            {
                return (_etats, _raison, _dernierePasseUtc);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _abonnement = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken).ConfigureAwait(false);
                    await PasseAsync("fin de partie", stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Extend : passe de fin de partie en echec");
                }
            }, stoppingToken);
        });

        try
        {
            await Task.Delay(PremierDelai, stoppingToken).ConfigureAwait(false);
            var derniere = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var signature = Signature();
                    var echu = DateTime.UtcNow - derniere >= Securite;
                    if (echu || signature != _derniereSignature)
                    {
                        await PasseAsync(echu ? "securite" : "changement", stoppingToken).ConfigureAwait(false);
                        derniere = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Extend : passe en echec");
                }

                await Task.Delay(Sondage, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _abonnement?.Dispose();
        }
    }

    /// <summary>
    /// Ce qui peut appeler une passe : le catalogue, la fin d'une synchronisation du Data Pack, le contenu des dossiers de ROMs
    /// des systemes de la fiche (nombre de fichiers et date du plus recent). Des listes de fichiers, aucune lecture de ROM.
    /// </summary>
    internal string Signature()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var info = new FileInfo(FichierCatalogue);
            sb.Append(info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "absent").Append('|');
            sb.Append(_dataPack?.Dernier?.FinishedUtc.Ticks ?? 0).Append('|');
            if (info.Exists)
            {
                using var flux = File.Open(FichierCatalogue, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var catalogue = ExtendCatalogue.Lire(flux, Api, out _, out _);
                foreach (var systeme in (catalogue?.Conversions ?? []).Select(c => c.System!).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var dossier = new DirectoryInfo(Path.Combine(RacineRoms, systeme));
                    var n = 0;
                    long recent = 0;
                    if (dossier.Exists)
                    {
                        foreach (var fichier in dossier.EnumerateFiles())
                        {
                            n++;
                            recent = Math.Max(recent, fichier.LastWriteTimeUtc.Ticks);
                        }
                    }

                    sb.Append(systeme).Append(':').Append(n).Append(':').Append(recent).Append('|');
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.Append("illisible");
        }

        return sb.ToString();
    }

    /// <summary>Une passe sur toutes les fiches. Une seule a la fois ; jamais pendant une partie.</summary>
    public async Task PasseAsync(string declencheur, CancellationToken ct)
    {
        if (!await _porte.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var changements = await Task.Run(() => Passe(declencheur, ct), ct).ConfigureAwait(false);
            _derniereSignature = Signature();
            if (changements && _collection is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _collection.SynchroniserAsync("extend", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Extend : la collection MULTIPLAYER n'a pas pu etre rafraichie");
                    }
                });
            }
        }
        finally
        {
            _porte.Release();
        }
    }

    /// <summary>Le travail d'une passe, synchrone. Vrai si une ROM derivee a ete deposee ou retiree.</summary>
    internal bool Passe(string declencheur, CancellationToken ct)
    {
        if (!Actif)
        {
            Publier([], "collection MULTIPLAYER coupee");
            return false;
        }

        if (PartieEnCours)
        {
            _logger?.LogDebug("Extend : une partie tourne, passe ({Declencheur}) reportee", declencheur);
            lock (_verrouEtat)
            {
                _raison = "partie en cours";
            }

            return false;
        }

        ExtendCatalogue? catalogue;
        try
        {
            if (!File.Exists(FichierCatalogue))
            {
                Publier([], "catalogue absent (Data Pack pas encore synchronise)");
                return false;
            }

            using var flux = File.Open(FichierCatalogue, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            catalogue = ExtendCatalogue.Lire(flux, Api, out var erreur, out var ecartees);
            foreach (var raison in ecartees)
            {
                _logger?.LogWarning("Extend : fiche ecartee, {Raison}", raison);
            }

            if (catalogue is null)
            {
                _logger?.LogWarning("Extend : catalogue refuse, {Erreur}", erreur);
                Publier([], erreur);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Publier([], "catalogue illisible : " + ex.Message);
            return false;
        }

        var retenues = Charger();
        var etats = new List<ExtendEtat>();
        var changement = false;
        foreach (var fiche in catalogue.Conversions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                etats.Add(Traiter(fiche, retenues, ref changement));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un fichier ouvert, un dossier en lecture seule : la passe suivante reessaie.
                _logger?.LogInformation("Extend : {Id} {Version} refus : {Raison}", fiche.Id, fiche.Version, ex.Message);
                etats.Add(FaireEtat(fiche, "refus", null, false, "ecriture impossible : " + ex.Message));
            }
        }

        Enregistrer(retenues);
        Publier(etats, null);
        return changement;
    }

    private ExtendEtat Traiter(ExtendConversion f, Dictionary<string, Retenue> retenues, ref bool changement)
    {
        var dossier = Path.GetFullPath(Path.Combine(RacineRoms, f.System!)) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(dossier, f.Result!.File!));
        if (!destination.StartsWith(dossier, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dossier))
        {
            return Journal(f, "refus", null, false, "dossier du systeme absent ou nom de fichier hors du dossier");
        }

        retenues.TryGetValue(f.Id!, out var avant);
        var deriveeSha = File.Exists(destination) ? Empreinte(destination) : null;

        // 1. Une ROM derivee d'un AUTRE nom (version anterieure) est retiree, si elle est bien de nous.
        if (avant?.File is { Length: > 0 } ancien && !string.Equals(ancien, f.Result.File, StringComparison.OrdinalIgnoreCase))
        {
            var ancienChemin = Path.GetFullPath(Path.Combine(dossier, ancien));
            if (ancienChemin.StartsWith(dossier, StringComparison.OrdinalIgnoreCase) && File.Exists(ancienChemin)
                && string.Equals(Empreinte(ancienChemin), avant.Sha256, StringComparison.Ordinal))
            {
                File.Delete(ancienChemin);
                Retirer(f.System!, ancienChemin);
                changement = true;
                _logger?.LogInformation("Extend : {Id} version {Version} anterieure retiree ({Fichier})", f.Id, avant.Version, ancien);
            }

            retenues.Remove(f.Id!);
            avant = null;
        }

        // 2. Deja a jour.
        if (string.Equals(deriveeSha, f.Result.Sha256, StringComparison.Ordinal))
        {
            retenues[f.Id!] = new Retenue { Version = f.Version, File = f.Result.File, Sha256 = deriveeSha, System = f.System };
            if (Declarer(f, destination))
            {
                changement = true;
            }

            return Journal(f, "deposee", null, true, "deja presente", destination, silencieux: true);
        }

        // 3. Un fichier de ce nom qui n'est pas celui que nous avions ecrit : celui du joueur, jamais touche.
        var ancienneDeNous = deriveeSha is not null && avant is not null && string.Equals(avant.Sha256, deriveeSha, StringComparison.Ordinal)
            && string.Equals(avant.File, f.Result.File, StringComparison.OrdinalIgnoreCase);
        if (deriveeSha is not null && !ancienneDeNous)
        {
            return Journal(f, "refus", null, true, "un fichier de ce nom existe et n'est pas une ROM derivee par NelfePlay : il n'est pas touche", destination);
        }

        // 4. Le patch : present, de la bonne taille, de la bonne empreinte.
        var cheminPatch = ExtendCatalogue.CheminLocalDuPatch(RacineResources, f.Patch!.Path!);
        if (cheminPatch is null || !File.Exists(cheminPatch))
        {
            return Journal(f, "patch_absent", null, false, "patch absent du Data Pack local");
        }

        var octetsPatch = File.ReadAllBytes(cheminPatch);
        if (octetsPatch.Length != f.Patch.Size || !string.Equals(Sha256Hex(octetsPatch), f.Patch.Sha256, StringComparison.Ordinal))
        {
            return Journal(f, "refus", null, false, "le patch du Data Pack ne correspond pas a l'empreinte de la fiche");
        }

        // 5. La source, chez le joueur.
        var (source, octetsSource) = ChercherLaSource(dossier, destination, f);
        if (source is null || octetsSource is null)
        {
            if (ancienneDeNous)
            {
                File.Delete(destination);
                Retirer(f.System!, destination);
                retenues.Remove(f.Id!);
                changement = true;
                _logger?.LogInformation("Extend : {Id} version {Version} anterieure retiree : la nouvelle version ne peut pas etre derivee (source absente)", f.Id, avant!.Version);
            }

            return Journal(f, "source_absente", null, false, "la ROM d'origine n'est pas dans roms\\" + f.System);
        }

        // 6. Le patch, en memoire.
        byte[]? resultat;
        string? erreur;
        var ok = f.Patch.Format == "ips"
            ? PatchIps.Appliquer(octetsSource, octetsPatch, ExtendCatalogue.PlafondResultat, out resultat, out erreur)
            : PatchBps.Appliquer(octetsSource, octetsPatch, ExtendCatalogue.PlafondResultat, out resultat, out erreur);
        if (!ok || resultat is null)
        {
            return Journal(f, "refus", Path.GetFileName(source), true, erreur ?? "patch inapplicable");
        }

        if (resultat.Length != f.Result.Size || !string.Equals(Sha256Hex(resultat), f.Result.Sha256, StringComparison.Ordinal))
        {
            return Journal(f, "refus", Path.GetFileName(source), true, "le resultat ne correspond pas a l'empreinte de la fiche (rien n'est depose)");
        }

        // 7. Temporaire puis renommage : jamais de ROM a moitie ecrite.
        var temporaire = destination + ".extend-tmp";
        try
        {
            File.WriteAllBytes(temporaire, resultat);
            File.Move(temporaire, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaire))
            {
                File.Delete(temporaire);
            }
        }

        retenues[f.Id!] = new Retenue { Version = f.Version, File = f.Result.File, Sha256 = f.Result.Sha256, System = f.System };
        Declarer(f, destination);
        changement = true;
        return Journal(f, "deposee", Path.GetFileName(source), true,
            ancienneDeNous ? "remplace la version " + avant!.Version : "deposee", destination);
    }

    /// <summary>
    /// La ROM d'origine : un fichier nu de la taille de la source, ou le fichier unique d'une archive de cette taille (et de ce
    /// crc32 quand la fiche le donne), dont le sha256 est celui de la fiche. Les octets sont rendus : on ne relit pas.
    /// </summary>
    private (string? Chemin, byte[]? Octets) ChercherLaSource(string dossier, string destination, ExtendConversion f)
    {
        uint? crcAttendu = uint.TryParse(f.Source!.Crc, System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : null;
        foreach (var chemin in Directory.EnumerateFiles(dossier))
        {
            if (string.Equals(chemin, destination, StringComparison.OrdinalIgnoreCase)
                || chemin.EndsWith(".extend-tmp", StringComparison.OrdinalIgnoreCase)
                || chemin.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || chemin.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var contenu = ContenuDUneRom.Ouvrir(chemin, ExtendCatalogue.PlafondResultat);
                if (contenu is null || contenu.Taille != f.Source.Size || (crcAttendu is { } attendu && contenu.Crc32 is { } declare && declare != attendu))
                {
                    continue;
                }

                var octets = new byte[(int)contenu.Taille];
                contenu.Flux.ReadExactly(octets);
                if (string.Equals(Sha256Hex(octets), f.Source.Sha256, StringComparison.Ordinal))
                {
                    return (chemin, octets);
                }
            }
            catch (Exception ex) when (ContenuDUneRom.EstUneLectureRatee(ex) || ex is EndOfStreamException)
            {
                // Une archive abimee ou ouverte ailleurs n'est pas la source : on passe a la suivante.
            }
        }

        return (null, null);
    }

    private bool Declarer(ExtendConversion f, string destination)
    {
        try
        {
            return _reglages.Declarer?.Invoke(f.System!, destination, f.Title!, f.Core!.Name!)
                ?? _gamelists?.DeclarerJeuDerive(f.System!, destination, f.Title!, f.Core!.Name!) ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _logger?.LogWarning(ex, "Extend : gamelist de {Systeme} non mise a jour", f.System);
            return false;
        }
    }

    private void Retirer(string systeme, string chemin)
    {
        try
        {
            _ = _reglages.Retirer?.Invoke(systeme, chemin) ?? _gamelists?.RetirerJeuDerive(systeme, chemin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _logger?.LogWarning(ex, "Extend : entree de gamelist de {Systeme} non retiree", systeme);
        }
    }

    private ExtendEtat Journal(ExtendConversion f, string etat, string? source, bool sourceTrouvee, string message,
        string? destination = null, bool silencieux = false)
    {
        var resume = etat switch
        {
            "deposee" => "deposee",
            "source_absente" => "source absente",
            _ => "refus : " + message,
        };
        if (!silencieux)
        {
            _logger?.LogInformation("Extend : {Id} {Version} {Resume}{Detail}", f.Id, f.Version, resume,
                etat == "deposee" || etat == "source_absente" ? " (" + message + ")" : "");
        }

        return FaireEtat(f, etat, source, sourceTrouvee, message, destination);
    }

    private ExtendEtat FaireEtat(ExtendConversion f, string etat, string? source, bool sourceTrouvee, string message, string? destination = null)
    {
        var chemin = destination ?? Path.Combine(RacineRoms, f.System!, f.Result!.File!);
        var presente = File.Exists(chemin);
        var coeurPose = _reglages.CoeurPose?.Invoke(f.Core!.Name!)
            ?? (_options is null || MultiplayerCoeursService.EstPose(f.Core!.Name!, _options.CurrentValue));
        return new ExtendEtat(f.Id!, f.System!, f.Title!, f.Version!, etat, sourceTrouvee || etat == "deposee", source, presente,
            f.Result!.File!, f.Core!.Name!, coeurPose, message);
    }

    private void Publier(IReadOnlyList<ExtendEtat> etats, string? raison)
    {
        lock (_verrouEtat)
        {
            _etats = etats;
            _raison = raison;
            _dernierePasseUtc = DateTime.UtcNow;
        }
    }

    private Dictionary<string, Retenue> Charger()
    {
        try
        {
            if (File.Exists(FichierEtat))
            {
                return JsonSerializer.Deserialize<Dictionary<string, Retenue>>(File.ReadAllText(FichierEtat))
                    ?? new Dictionary<string, Retenue>();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogDebug(ex, "Extend : etat des ROMs derivees illisible, repart de zero");
        }

        return new Dictionary<string, Retenue>();
    }

    private void Enregistrer(Dictionary<string, Retenue> retenues)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FichierEtat)!);
            var temporaire = FichierEtat + ".tmp";
            File.WriteAllText(temporaire, JsonSerializer.Serialize(retenues, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaire, FichierEtat, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Extend : etat des ROMs derivees non enregistre");
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> octets) => Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

    private static string Empreinte(string chemin)
    {
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(flux)).ToLowerInvariant();
    }
}

/// <summary>L'etat d'une fiche pour <c>GET /api/v1/extend</c>. Jamais de chemin complet : le nom du fichier source suffit.</summary>
public sealed record ExtendEtat(string Id, string System, string Title, string Version, string State, bool SourceFound,
    string? SourceFile, bool DerivedPresent, string DerivedFile, string Core, bool CorePosed, string? Message);
