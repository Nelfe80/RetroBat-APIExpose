using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>Les fantomes d'un speedrun : X les fait defiler sur la fiche (CDC MASS RUN D25).</summary>
public enum ChoixDesFantomes
{
    MonRecordEtMondial,
    MonRecord,
    RecordMondial,
    Aucun,
}

/// <summary>
/// LES FANTOMES D'UN SPEEDRUN DE NIVEAU (demande du user du 04/11, CDC MASS RUN §14). Au lancement, le classement du niveau
/// donne le replay de mon record et celui du record mondial ; chacun est converti une fois en .nghost, pose dans le dossier
/// que lit le coeur hote, et le speedrun part sur ce coeur.
/// <para>
/// LA CERTIFICATION PASSE AVANT : le coeur hote n'est lance que si le site admet son empreinte (`ghost_cores`
/// d'open-games). Sinon, hors ligne comme refuse, le speedrun part sur le coeur d'origine, sans fantome : un temps ne se perd
/// jamais pour un fantome. Un fantome pas encore la au bout de <see cref="Budget"/> arrive pour la tentative suivante.
/// </para>
/// <para>
/// LE MEILLEUR FANTOME A CHAQUE DEPART (demande du user du 04/11), meme tout juste fait : une arrivee qui bat mon temps
/// remplace mon fantome des que son replay est finalise, et le coeur relit le dossier au rechargement de l'etat de depart.
/// Le coeur hote part donc aussi sans aucun fantome, pour accueillir le premier.
/// </para>
/// </summary>
public sealed partial class FantomesDuSpeedrun
{
    /// <summary>
    /// Un jeu dont un coeur hote a le profil : son coeur d'origine, le coeur hote, le crc32 du contenu (EN-TETE COMPRIS : c'est
    /// celui que calcule le coeur hote) et le nom du dump que ce crc designe, pour le dire au joueur.
    /// <para>
    /// <paramref name="SansEnTete"/> (08/11) : le md5 du dump classe SANS SON EN-TETE (regle rcheevos), quand le coeur hote sait
    /// reconnaitre le meme jeu sous un autre en-tete (coeur fceumm_ghosts de la branche en-tete-nes : « crc_sans_en_tete » du
    /// profil). Un tel dump (le No-Intro en NES 2.0) part alors sur le coeur hote, et le jeu y garde le crc <paramref name="Crc"/>
    /// (dossier des fantomes, profil). Null : seul le crc du fichier entier compte.
    /// </para>
    /// </summary>
    public sealed record Hote(string Systeme, string Groupe, string Origine, string Coeur, string Crc, bool SurEtatSeulement, string Dump,
        string? SansEnTete = null);

    private static readonly Hote[] Hotes =
    [
        // (08/11) le meme jeu sans son en-tete : un GoodNES « Super Mario Bros. (JU) [!] » (iNES 1.0) court aussi sur le coeur hote.
        new("nes", "super-mario-bros", "fceumm", "fceumm_ghosts", "393a432f", true, "Super Mario Bros. (World)",
            "8e3630186e35d477231bf8fd50e54cdd"),
        // Super Metroid : aussi le jeu complet, parti du demarrage ; le coeur hote aligne le fantome sur le debut de la partie.
        new("snes", "super-metroid", "snes9x", "snes9x_ghosts", "d63ed5f8", false, "Super Metroid (Japan, USA) (En,Ja)"),
        // Sonic : les niveaux seulement (etat du panneau) ; l'Any% change de niveau, et chaque niveau rearme le depart.
        new("megadrive", "sonic-the-hedgehog", "genesis_plus_gx", "genesis_plus_gx_ghosts", "f9394e97", true, "Sonic The Hedgehog (USA, Europe)"),
        // Mega Man 2 (USA) : les huit stages des Robot Masters (etat d'entree, READY) ; profil en fichier (a9bd44bc.ini).
        // Le dump d'ouverture est le GoodNES « Mega Man 2 (U) » (en-tete iNES 1.0).
        // (08/11) le No-Intro « Mega Man 2 (USA) » (NES 2.0) aussi, par le md5 sans en-tete (cle crc_sans_en_tete de a9bd44bc.ini).
        new("nes", "mega-man-2", "fceumm", "fceumm_ghosts", "a9bd44bc", true, "Mega Man 2 (USA)", "0527a0ee512f69e08b8db6dc97964632"),
        // Super Mario Bros. 3 (USA) (Rev 1) : le monde 1 (etat d'entree de chaque niveau) ; profil compile dans le coeur 0.7.
        // Le dump d'ouverture est le GoodNES « Super Mario Bros 3 (U) (PRG 1) » (en-tete iNES 1.0).
        // (08/11) le No-Intro (NES 2.0) aussi, par le md5 sans en-tete (crc_sans_en_tete du profil compile).
        new("nes", "super-mario-bros-3", "fceumm", "fceumm_ghosts", "0b742b33", true, "Super Mario Bros. 3 (USA) (Rev 1)",
            "55b7111567c1709e849c574078699577"),
        // (06/11, jour) Super Mario World (USA) : les niveaux seulement (etat d'ouverture du niveau), profil en fichier b19ed489.ini.
        new("snes", "super-mario-world", "snes9x", "snes9x_ghosts", "b19ed489", true, "Super Mario World (USA)"),
        // Sonic 2 (06/11) : comme Sonic, les niveaux seulement ; profil du coeur en fichier (24ab4c3a.ini du Data Pack).
        new("megadrive", "sonic-the-hedgehog-2", "genesis_plus_gx", "genesis_plus_gx_ghosts", "24ab4c3a", true, "Sonic The Hedgehog 2 (World)"),
        // Rocket Knight Adventures (06/11) : les stages seulement (etat du portrait) ; profil en fichier (a6efec47.ini).
        new("megadrive", "rocket-knight-adventures", "genesis_plus_gx", "genesis_plus_gx_ghosts", "a6efec47", true, "Rocket Knight Adventures (USA)"),
    ];

    /// <summary>Les jeux a coeur hote, tels que l'API les connait : l'outil de diagnostic les lit ici (GET /api/v1/speedrun/ghost-hosts).</summary>
    public static IReadOnlyList<Hote> Liste => Hotes;

    private static Hote? HoteDe(Reglement r) => Hotes.FirstOrDefault(h => h.Systeme == r.SystemId && h.Groupe == r.RomGroup && r.Coeurs.Contains(h.Origine)
        && r.Kind.StartsWith("speedrun_", StringComparison.Ordinal) && (r.Etat is not null || !h.SurEtatSeulement));

    /// <summary>Le coeur hote de ce speedrun, ou null. Pur.</summary>
    public static string? CoeurDe(Reglement r) => HoteDe(r)?.Coeur;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan FraicheurDeLAdmission = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _http;
    private readonly IReplayManifestStore _manifestes;
    private readonly ReplayStore _objets;
    private readonly ReplayManifestFetcher _recherche;
    private readonly IReplaySourceResolver _sources;
    private readonly ILogger<FantomesDuSpeedrun>? _journal;
    private readonly Dictionary<string, (DateTime Lu, bool Admis)> _admissions = new(StringComparer.Ordinal);
    private Hote? _hote;
    private readonly object _verrou = new();
    private readonly IDisposable? _abonnement;
    private bool _enCourse;
    private long? _monTemps;
    private long? _tempsAttendu;
    private long? _dureeAttendue;
    private string? _defi;
    private string _pseudo = "";
    private TaskCompletionSource? _fantomeAttendu;

    public FantomesDuSpeedrun(IHttpClientFactory http, IReplayManifestStore manifestes, ReplayStore objets, ReplayManifestFetcher recherche,
        IReplaySourceResolver sources, ILogger<FantomesDuSpeedrun>? journal = null, IEventBus? bus = null)
    {
        _abonnement = bus?.Subscribe<EventEnvelope>(SurEvenement);
        _http = http;
        _manifestes = manifestes;
        _objets = objets;
        _recherche = recherche;
        _sources = sources;
        _journal = journal;
    }

    public ChoixDesFantomes Choix { get; private set; } = ChoixDesFantomes.MonRecordEtMondial;

    public void ChoixSuivant() => Choix = (ChoixDesFantomes)(((int)Choix + 1) % 4);

    /// <summary>Un speedrun sur le coeur d'origine d'un coeur hote, d'un jeu dont il a le profil. Pur.</summary>
    public static bool PourCeDefi(Reglement r) => HoteDe(r) is not null;

    /// <summary>Les replays a courir, du classement du niveau : le mien, puis le record, sans doublon. Pur.</summary>
    public static IReadOnlyList<string> Replays(IReadOnlyList<(string Joueur, string? Replay)> lignes, string pseudo, ChoixDesFantomes choix)
    {
        var mien = lignes.FirstOrDefault(l => pseudo.Length > 0 && string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase)).Replay;
        // LE MEILLEUR FANTOME DISPONIBLE (10/11, demande du user) : un record du monde sans replay (retire, ou d'avant le
        // 08/11) ne laisse plus la course sans reference : le meilleur temps du classement qui a un fantome le remplace.
        var mondial = lignes.Count > 0 && lignes[0].Replay is { Length: > 0 } wr ? wr
            : lignes.FirstOrDefault(l => l.Replay is { Length: > 0 }
                && !(pseudo.Length > 0 && string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase))).Replay;
        IEnumerable<string?> voulus = choix switch
        {
            ChoixDesFantomes.MonRecordEtMondial => [mien, mondial],
            ChoixDesFantomes.MonRecord => [mien],
            ChoixDesFantomes.RecordMondial => [mondial],
            _ => [],
        };
        return voulus.OfType<string>().Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// (10/11) Les fantomes de reference a essayer, dans l'ordre : le record du monde, puis chaque temps du classement qui a un
    /// replay, jamais le mien. Le premier qui se pose court ; un replay introuvable ou trop court laisse sa place au suivant
    /// (SMB 1-2 : le 2e, NeoGeoff, n'avait qu'un replay de 16 images, parti nulle part). Pur.
    /// </summary>
    public static IReadOnlyList<string> CandidatsDeReference(IReadOnlyList<(string Joueur, string? Replay)> lignes, string pseudo)
        => lignes.Where(l => l.Replay is { Length: > 0 } && !(pseudo.Length > 0 && string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase)))
            .Select(l => l.Replay!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Le nombre d'images d'un .nghost (docs/FORMAT.md de NelfePlay-Ghosts), ou null s'il est illisible. Pur.</summary>
    internal static long? ImagesDuFantome(byte[] fantome)
    {
        if (fantome.Length < 20 || !fantome.AsSpan(0, 8).SequenceEqual("NGHOST1\0"u8)) return null;
        var etat = (long)BitConverter.ToUInt32(fantome, 16);
        return 20 + etat + 4 <= fantome.Length ? BitConverter.ToUInt32(fantome, (int)(20 + etat)) : null;
    }

    /// <summary>Le temps du joueur sur ce classement, ou null. Pur.</summary>
    internal static long? MonTemps(JsonElement classement, string pseudo)
    {
        if (pseudo.Length == 0 || !classement.TryGetProperty("rows", out var lignes) || lignes.ValueKind != JsonValueKind.Array) return null;
        foreach (var l in lignes.EnumerateArray())
        {
            if (l.TryGetProperty("player", out var j) && string.Equals(j.GetString(), pseudo, StringComparison.OrdinalIgnoreCase)
                && l.TryGetProperty("value", out var v) && v.TryGetInt64(out var temps)) return temps;
        }

        return null;
    }

    /// <summary>
    /// Une arrivee vient de tomber : vrai si elle bat mon temps et que mon fantome court. Son replay, finalise ensuite, le
    /// remplacera (<see cref="AttendreLeFantomeAsync"/>).
    /// </summary>
    public bool NouveauTemps(long? frames, long? independant = null)
    {
        lock (_verrou)
        {
            if (!_enCourse || frames is not { } t || (_monTemps is { } mien && t >= mien)) return false;
            if (Choix is not (ChoixDesFantomes.MonRecordEtMondial or ChoixDesFantomes.MonRecord)) return false;
            _monTemps = t;
            _tempsAttendu = t;
            _dureeAttendue = independant;
            _splitsDuRunRecord = LireLesSplitsDuJoueur();
            _fantomeAttendu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>Attend que le fantome tout juste fait soit pose. Au bout du delai, il arrivera pour le depart suivant.</summary>
    public async Task AttendreLeFantomeAsync(TimeSpan delai)
    {
        TaskCompletionSource? attendu;
        lock (_verrou) attendu = _fantomeAttendu;
        if (attendu is not null) await Task.WhenAny(attendu.Task, Task.Delay(delai)).ConfigureAwait(false);
    }

    private void SurEvenement(EventEnvelope e)
    {
        if (e.Type != "replay.finalized") return;
        TaskCompletionSource? attendu;
        lock (_verrou) attendu = _fantomeAttendu;
        if (attendu is null) return;
        try
        {
            var replay = (e.Payload is JsonElement j ? j : JsonSerializer.SerializeToElement(e.Payload)).GetProperty("ReplayId").GetString();
            if (replay is { Length: > 0 }) _ = Task.Run(() => PoserMonFantomeAsync(replay, attendu));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException or NotSupportedException)
        {
            _journal?.LogDebug(ex, "Fantomes : replay.finalized illisible.");
        }
    }

    private async Task PoserMonFantomeAsync(string replay, TaskCompletionSource attendu)
    {
        try
        {
            Hote? hote;
            string? defi;
            long? temps, dureeDuRun;
            string pseudo;
            lock (_verrou) { hote = _hote; defi = _defi; temps = _tempsAttendu; dureeDuRun = _dureeAttendue; pseudo = _pseudo; }
            if (hote is null || await FantomeAsync(replay, CancellationToken.None).ConfigureAwait(false) is not { } fantome) return;
            // UN REPLAY PLUS COURT QUE LE RUN NE LE CONTIENT PAS (05/11, Green Hill 1 : 1232 images pour un temps de 1925, le
            // replay parti en retard montrait le debut de la tentative suivante). Ni fantome, ni record local.
            if ((dureeDuRun ?? temps) is { } duree && ImagesDuFantome(fantome) is { } images && images < duree)
            {
                _journal?.LogWarning("Fantomes : {Replay} ne contient pas le run ({Images} images pour {Temps}), il ne devient pas un fantome.",
                    replay, images, duree);
                return;
            }

            var dossier = Directory.CreateDirectory(DossierDe(hote)).FullName;
            foreach (var ancien in Directory.EnumerateFiles(dossier, PrefixeDuMien + "*.nghost")) File.Delete(ancien);
            foreach (var ancien in Directory.EnumerateFiles(dossier, PrefixeDuMien + "*.label")) File.Delete(ancien);
            foreach (var ancien in Directory.EnumerateFiles(dossier, PrefixeDuMien + "*.duree")) File.Delete(ancien);
            foreach (var ancien in Directory.EnumerateFiles(dossier, PrefixeDuMien + "*.splits")) File.Delete(ancien);
            await File.WriteAllBytesAsync(Path.Combine(dossier, $"{PrefixeDuMien}{replay}.nghost"), AuCrcDuJeu(fantome, hote.Crc)).ConfigureAwait(false);
            EcrireLEtiquette(dossier, $"{PrefixeDuMien}{replay}", Etiquette(pseudo, false));
            EcrireLaDuree(dossier, $"{PrefixeDuMien}{replay}", dureeDuRun);
            // (11/11) les splits du run qui vient de battre le record : ceux du joueur, gardes a l'arrivee
            PoserLesSplitsDuRun(dossier, $"{PrefixeDuMien}{replay}", replay);
            Poser(new FantomePose($"{PrefixeDuMien}{replay}", replay, Etiquette(pseudo, false), RoleMien, dureeDuRun ?? temps), remplacerLeMien: true);
            if (defi is not null && temps is { } t) EcrireLeRecordLocal(defi, t, replay, fantome, dureeDuRun);
            _journal?.LogInformation("Fantomes : meilleur temps de la session, {Replay} court des le prochain depart.", replay);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            _journal?.LogWarning("Fantomes : le fantome tout juste fait n'a pas pu se poser : {Message}", e.Message);
        }
        finally
        {
            lock (_verrou)
            {
                if (ReferenceEquals(_fantomeAttendu, attendu)) _fantomeAttendu = null;
            }

            attendu.TrySetResult();
        }
    }

    /// <summary>
    /// Le coeur hote reconnaitrait-il ce contenu ? Il retrouve son profil (et ses fantomes) au crc32 du contenu ENTIER, en-tete
    /// compris, et depuis le 08/11 au crc sans en-tete quand son profil le declare : un « Super Mario Bros. 3 (USA) (Rev 1) »
    /// No-Intro (en-tete NES 2.0) a le meme md5 sans en-tete que le dump classe (<see cref="Hote.SansEnTete"/>). Inconnu (fichier
    /// illisible) : on ne bloque pas. Pur hors lecture du fichier.
    /// </summary>
    public static bool ContenuDuCoeurHote(Reglement r, string? rom)
        => HoteDe(r) is not { } hote || string.IsNullOrEmpty(rom) || ContenuDuHote(hote, rom);

    /// <summary>Le contenu de ce fichier est-il celui du coeur hote : meme crc32 entier, ou meme md5 sans en-tete. Pur hors lecture.</summary>
    internal static bool ContenuDuHote(Hote hote, string rom)
    {
        if (RetroBat.Api.MassRun.JeuxMassRun.ContenuDe(rom) is not { } crc
            || string.Equals(crc.ToString("x8"), hote.Crc, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return hote.SansEnTete is { Length: > 0 } attendu
            && NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(rom, hote.Systeme) is { } sansEnTete
            && string.Equals(sansEnTete, attendu, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// LE FANTOME AU CRC DU JEU (08/11) : un replay porte le crc32 du fichier sur lequel il a ete fait (en-tete compris) ; fait sur
    /// le No-Intro, il ne serait pas lu par le coeur hote d'un joueur du dump classe (le coeur ecarte un fantome d'un autre
    /// contenu). Le replay vient du classement de ce defi, donc de ce jeu : son .nghost prend le crc du coeur hote, celui du
    /// dossier ou il est pose. Une copie ; un contenu qui n'est pas un .nghost est rendu tel quel. Pur.
    /// </summary>
    internal static byte[] AuCrcDuJeu(byte[] fantome, string crc)
    {
        if (fantome.Length < 12 || !fantome.AsSpan(0, 8).SequenceEqual("NGHOST1\0"u8)
            || !uint.TryParse(crc, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var valeur))
        {
            return fantome;
        }

        var copie = (byte[])fantome.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(copie.AsSpan(8, 4), valeur);
        return copie;
    }

    /// <summary>
    /// Vrai si le speedrun doit partir sur le coeur hote. Attend <see cref="Budget"/> au plus. Un contenu que le coeur hote ne
    /// reconnaitrait pas (<see cref="ContenuDuCoeurHote"/>) part sur le coeur d'origine, sans fantome, comme hors ligne.
    /// </summary>
    public bool Preparer(Reglement r, string pseudo, string? rom = null)
    {
        ViderLeDossier();
        lock (_verrou)
        {
            _enCourse = false;
            _monTemps = null;
            _fantomeAttendu = null;
            OublierLesPoses();
        }

        if (HoteDe(r) is not { } hote || Choix == ChoixDesFantomes.Aucun) return false;
        if (!ContenuDuCoeurHote(r, rom))
        {
            _journal?.LogInformation("Fantomes : {Rom} n'est pas le dump du coeur hote (crc {Crc}) pour {Defi}, depart sur le coeur d'origine.",
                Path.GetFileName(rom), hote.Crc, r.ChallengeId);
            return false;
        }
        RetroBat.Api.MassRun.FichiersDesCoeursHotes.PoserLesProfils();
        lock (_verrou) { _hote = hote; _defi = r.ChallengeId; _tempsAttendu = null; _pseudo = pseudo; }
        var admis = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var travail = Task.Run(() => PreparerAsync(r, hote, pseudo, CancellationToken.None, admis));
        try
        {
            if (!travail.Wait(Budget))
            {
                // EN RETARD, PAS SANS (05/11, Mario 1-1 sans aucun fantome : prets 0,2 s apres le delai, la partie etait deja
                // partie sur le coeur d'origine, et le meilleur temps de la session n'est pas devenu un fantome). Le coeur hote
                // admis part quand meme : il relit le dossier au chargement de l'etat de depart, et les fantomes le rejoignent.
                if (admis.Task is { IsCompletedSuccessfully: true, Result: true })
                {
                    _journal?.LogInformation("Fantomes : pas prets en {Budget} s pour {Defi}, ils rejoindront la partie a l'etat de depart.",
                        Budget.TotalSeconds, r.ChallengeId);
                    lock (_verrou) _enCourse = true;
                    _ = travail.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && !t.Result) lock (_verrou) _enCourse = false;
                    }, TaskScheduler.Default);
                    return true;
                }

                _journal?.LogInformation("Fantomes : pas prets en {Budget} s pour {Defi}, ce depart se fait sans.", Budget.TotalSeconds, r.ChallengeId);
                return false;
            }

            lock (_verrou) _enCourse = travail.Result;
            return travail.Result;
        }
        catch (AggregateException e)
        {
            _journal?.LogWarning(e.InnerException, "Fantomes : preparation de {Defi} echouee, le speedrun part sans.", r.ChallengeId);
            return false;
        }
    }

    private async Task<bool> PreparerAsync(Reglement r, Hote hote, string pseudo, CancellationToken ct, TaskCompletionSource<bool>? admis = null)
    {
        var coeurAdmis = false;
        try { coeurAdmis = await CoeurAdmisAsync(hote.Coeur, ct).ConfigureAwait(false); }
        finally { admis?.TrySetResult(coeurAdmis); }
        if (!coeurAdmis)
        {
            _journal?.LogInformation("Fantomes : le coeur hote n'est pas admis par le site, {Defi} part sans.", r.ChallengeId);
            return false;
        }

        // Le classement du site (record mondial, mon record publie) ; injoignable, mon record local suffit.
        IReadOnlyList<(string Joueur, string? Replay)> lignes = [];
        long? monTempsPublie = null;
        IReadOnlyDictionary<string, long> tempsDesReplays = new Dictionary<string, long>();
        IReadOnlyDictionary<string, long> dureesDesReplays = new Dictionary<string, long>();
        try
        {
            using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
            var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/challenges/board?ruleset={Uri.EscapeDataString(r.Ruleset)}&limit=100";
            using var json = JsonDocument.Parse(await client.GetStringAsync(url, ct).ConfigureAwait(false));
            lignes = LignesDuClassement(json.RootElement);
            monTempsPublie = MonTemps(json.RootElement, pseudo);
            tempsDesReplays = TempsDesReplays(json.RootElement);
            dureesDesReplays = TempsDesReplays(json.RootElement, "independent_frames");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogInformation("Fantomes : classement de {Defi} injoignable, mon record local seulement.", r.ChallengeId);
        }

        var replays = Replays(lignes, pseudo, Choix).ToList();
        var mien = lignes.FirstOrDefault(l => pseudo.Length > 0 && string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase)).Replay;
        var poses = 0;
        // MON RECORD LOCAL (05/11, demande du user : « un ghost local independant d'un replay distant ») : garde sur la borne a
        // chaque meilleur temps, il court tout de suite, sans attendre que le site propose son replay ; le record publie ne le
        // remplace que s'il est meilleur (un temps fait sur une autre borne).
        var local = Choix is ChoixDesFantomes.MonRecordEtMondial or ChoixDesFantomes.MonRecord ? LireLeRecordLocal(r.ChallengeId) : null;
        // un record local garde avant la garde du 05/11 peut ne pas contenir son run : il ne court pas
        if (local is { } court && ImagesDuFantome(court.Fantome) is { } n && n < (court.Duree ?? court.Images)) local = null;
        // (05/11) mon temps publie sans replay valide (retire : il ne contenait pas son run) : mon record local court a sa place
        if (local is { } l && (monTempsPublie is not { } publie || l.Images <= publie || mien is null))
        {
            var dossier = Directory.CreateDirectory(DossierDe(hote)).FullName;
            await File.WriteAllBytesAsync(Path.Combine(dossier, $"{PrefixeDuMien}{l.Replay}.nghost"), AuCrcDuJeu(l.Fantome, hote.Crc), ct).ConfigureAwait(false);
            // mon record local est aussi le record mondial quand personne ne fait mieux au classement
            EcrireLEtiquette(dossier, $"{PrefixeDuMien}{l.Replay}",
                Etiquette(pseudo, MeilleurQueLeMondial(l.Images, lignes, tempsDesReplays, pseudo, monTempsPublie)));
            EcrireLaDuree(dossier, $"{PrefixeDuMien}{l.Replay}", l.Duree);
            PoserLesSplits(dossier, $"{PrefixeDuMien}{l.Replay}", l.Replay);
            Poser(new FantomePose($"{PrefixeDuMien}{l.Replay}", l.Replay,
                Etiquette(pseudo, MeilleurQueLeMondial(l.Images, lignes, tempsDesReplays, pseudo, monTempsPublie)), RoleMien, l.Duree ?? l.Images));
            poses++;
            if (mien is not null) replays.Remove(mien);
            mien = null;
            lock (_verrou) _monTemps = l.Images;
        }
        else
        {
            lock (_verrou) _monTemps = monTempsPublie;
        }

        // le mien d'abord, puis la reference : le record du monde, ou a defaut le meilleur fantome qui se pose vraiment
        var avecReference = Choix is ChoixDesFantomes.MonRecordEtMondial or ChoixDesFantomes.RecordMondial;
        var aEssayer = replays.Where(x => x == mien).Concat(avecReference ? CandidatsDeReference(lignes, pseudo) : [])
            .Concat(replays.Where(x => x != mien)).Distinct(StringComparer.Ordinal).ToList();
        var referencePosee = false;
        foreach (var replay in aEssayer)
        {
            if (replay != mien && referencePosee) continue;
            if (await FantomeAsync(replay, ct).ConfigureAwait(false) is not { } fantome) continue;
            // le replay d'un temps publie qui ne contient pas son run (parti en retard, avant le 05/11) ne court pas
            var dureeDuRun = dureesDesReplays.TryGetValue(replay, out var di) ? di : (long?)null;
            if ((dureeDuRun ?? (tempsDesReplays.TryGetValue(replay, out var dv) ? dv : null)) is { } duree
                && ImagesDuFantome(fantome) is { } images && images < duree)
            {
                _journal?.LogWarning("Fantomes : {Replay} ne contient pas le run publie ({Images} images pour {Temps}), il ne court pas.",
                    replay, images, duree);
                continue;
            }
            var dossier = Directory.CreateDirectory(DossierDe(hote)).FullName;
            var nom = (replay == mien ? PrefixeDuMien : $"{poses + 1}-") + replay;
            await File.WriteAllBytesAsync(Path.Combine(dossier, nom + ".nghost"), AuCrcDuJeu(fantome, hote.Crc), ct).ConfigureAwait(false);
            var detenteur = lignes.FirstOrDefault(x => x.Replay == replay).Joueur ?? "";
            // (05/11) mon record qui est aussi le record mondial : dore, « PSEUDO (WR) »
            var mondial = lignes.Count > 0 && lignes[0].Replay == replay;
            // un fantome qui remplace un record du monde sans replay dit son rang : « NEOGEOFF (#2) »
            var rang = lignes.ToList().FindIndex(x => x.Replay == replay) + 1;
            EcrireLEtiquette(dossier, nom, replay == mien ? Etiquette(pseudo, mondial)
                : mondial || rang <= 0 ? Etiquette(detenteur, true) : EtiquetteDuRang(detenteur, rang));
            EcrireLaDuree(dossier, nom, dureeDuRun);
            PoserLesSplits(dossier, nom, replay);
            Poser(new FantomePose(nom, replay, File.Exists(Path.Combine(dossier, nom + ".label"))
                    ? File.ReadAllText(Path.Combine(dossier, nom + ".label")).Trim() : "",
                replay == mien ? (mondial ? RoleMondial : RoleMien) : mondial || rang <= 0 ? RoleMondial : RoleReference,
                dureeDuRun ?? (tempsDesReplays.TryGetValue(replay, out var tr) ? tr : null)));
            poses++;
            if (replay != mien) referencePosee = true;
        }

        _journal?.LogInformation("Fantomes : {Poses} sur {Voulus} pour {Defi}.", poses, replays.Count, r.ChallengeId);
        // Sans fantome, le coeur hote part quand meme si mon record en est : le premier temps doit pouvoir courir au depart suivant.
        return poses > 0 || Choix is ChoixDesFantomes.MonRecordEtMondial or ChoixDesFantomes.MonRecord;
    }

    private async Task<bool> CoeurAdmisAsync(string nom, CancellationToken ct)
    {
        lock (_verrou)
        {
            if (_admissions.TryGetValue(nom, out var connue) && DateTime.UtcNow - connue.Lu < FraicheurDeLAdmission) return connue.Admis;
        }

        var coeur = CheminDuCoeur(nom);
        if (coeur is null) return false;
        var empreinte = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(coeur, ct).ConfigureAwait(false))).ToLowerInvariant();
        try
        {
            using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
            using var json = JsonDocument.Parse(await client.GetStringAsync($"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/scores/open-games", ct).ConfigureAwait(false));
            var admis = Admet(json.RootElement, empreinte);
            lock (_verrou) _admissions[nom] = (DateTime.UtcNow, admis);
            return admis;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _journal?.LogDebug(e, "Fantomes : open-games injoignable.");
            return false;
        }
    }

    /// <summary>L'empreinte du coeur est-elle dans `ghost_cores` ? Pur.</summary>
    internal static bool Admet(JsonElement openGames, string empreinte)
        => openGames.TryGetProperty("ghost_cores", out var coeurs) && coeurs.ValueKind == JsonValueKind.Array
           && coeurs.EnumerateArray().Any(c => string.Equals(c.GetString(), empreinte, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le joueur et le replay de chaque ligne du classement d'un defi. Pur.</summary>
    internal static IReadOnlyList<(string Joueur, string? Replay)> LignesDuClassement(JsonElement classement)
    {
        if (!classement.TryGetProperty("rows", out var lignes) || lignes.ValueKind != JsonValueKind.Array) return [];
        return lignes.EnumerateArray().Select(l => (
            l.TryGetProperty("player", out var j) ? j.GetString() ?? "" : "",
            // « ghost » : le replay public meme hors du miroir (site du 05/11) ; « replay » : seulement s'il est sur le miroir
            Id(l, "ghost") ?? Id(l, "replay"))).ToList();
    }

    /// <summary>Le temps publie (images) de chaque replay du classement. Pur.</summary>
    internal static IReadOnlyDictionary<string, long> TempsDesReplays(JsonElement classement, string champ = "value")
    {
        var temps = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!classement.TryGetProperty("rows", out var lignes) || lignes.ValueKind != JsonValueKind.Array) return temps;
        foreach (var l in lignes.EnumerateArray())
        {
            if ((Id(l, "ghost") ?? Id(l, "replay")) is { Length: > 0 } replay && l.TryGetProperty(champ, out var v) && v.TryGetInt64(out var t))
                temps.TryAdd(replay, t);
        }

        return temps;
    }

    private static string? Id(JsonElement ligne, string champ)
        => ligne.TryGetProperty(champ, out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var id) ? id.GetString() : null;

    /// <summary>Le .nghost d'un replay : converti une fois, garde ensuite ; null si le replay n'est pas joignable.</summary>
    private async Task<byte[]?> FantomeAsync(string replay, CancellationToken ct)
    {
        var cache = Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "fantomes", replay + ".nghost");
        if (File.Exists(cache)) return await File.ReadAllBytesAsync(cache, ct).ConfigureAwait(false);
        var manifeste = _manifestes.GetManifest(replay);
        if (manifeste is null)
        {
            manifeste = await _recherche.FetchAsync(replay, ct).ConfigureAwait(false);
            if (manifeste is null) return null;
            _manifestes.SaveManifest(manifeste);
        }

        if (!_objets.HasObject(manifeste.Object.Sha256)
            && await _sources.FindObjectAsync(manifeste, ct).ConfigureAwait(false) != ReplayObjectSearch.Present)
        {
            return null;
        }

        if (await _objets.EnsureRawAsync(manifeste.Object.Sha256, ct).ConfigureAwait(false) is not { } brut) return null;
        try
        {
            var fantome = ReplayVersFantome.Convertir(await File.ReadAllBytesAsync(brut, ct).ConfigureAwait(false));
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllBytesAsync(cache, fantome, ct).ConfigureAwait(false);
            return fantome;
        }
        catch (InvalidDataException e)
        {
            _journal?.LogWarning("Fantomes : replay {Replay} illisible : {Message}", replay, e.Message);
            return null;
        }
    }

    /// <summary>
    /// L'ETIQUETTE D'UN FANTOME (05/11, demande du user) : le pseudo du joueur pour son record, le pseudo du detenteur suivi de
    /// « (WR) » pour le record mondial ; le coeur la dessine au-dessus du personnage (dore pour le record mondial, gris clair
    /// pour le mien). En majuscules, 20 caracteres au plus. Pur.
    /// </summary>
    internal static string EtiquetteDuRang(string pseudo, int rang)
    {
        var nom = Etiquette(pseudo, false);
        return nom.Length > 0 ? $"{nom} (#{rang})" : $"#{rang}";
    }

    internal static string Etiquette(string pseudo, bool mondial)
    {
        var nom = new string((pseudo ?? "").Where(c => !char.IsControl(c)).Take(20).ToArray()).Trim().ToUpperInvariant();
        return mondial ? (nom.Length > 0 ? nom + " (WR)" : "WR") : nom;
    }

    /// <summary>
    /// (05/11) La duree du run, du depart a l'arrivee (chrono independant), a cote du fantome : le coeur hote l'arrete a
    /// l'arrivee, et ecarte un replay qui ne contient pas tout le run. Inconnue : aucun fichier.
    /// </summary>
    private void EcrireLaDuree(string dossier, string nom, long? duree)
    {
        try
        {
            var chemin = Path.Combine(dossier, nom + ".duree");
            if (duree is > 0) File.WriteAllText(chemin, duree.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (File.Exists(chemin)) File.Delete(chemin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(e, "Fantomes : duree de {Nom} non ecrite.", nom);
        }
    }

    /// <summary>Mon temps bat (ou egale) tout le classement : mon fantome est le record mondial. Pur.</summary>
    internal static bool MeilleurQueLeMondial(long monTemps, IReadOnlyList<(string Joueur, string? Replay)> lignes,
        IReadOnlyDictionary<string, long> tempsDesReplays, string pseudo, long? monTempsPublie = null)
    {
        if (lignes.Count == 0) return false;
        var premier = lignes[0];
        // le record mondial est le mien : ce fantome l'est s'il est ce temps-la (pas un temps plus lent garde sur la borne)
        if (pseudo.Length > 0 && string.Equals(premier.Joueur, pseudo, StringComparison.OrdinalIgnoreCase))
            return monTempsPublie is not { } publie || monTemps <= publie;
        return premier.Replay is { } r && tempsDesReplays.TryGetValue(r, out var t) && monTemps <= t;
    }

    private void EcrireLEtiquette(string dossier, string nom, string etiquette)
    {
        try
        {
            File.WriteAllText(Path.Combine(dossier, nom + ".label"), etiquette, new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(e, "Fantomes : etiquette de {Nom} non ecrite.", nom);
        }
    }

    /// <summary>Le dossier des records locaux : un .nghost et un .json (temps en images, replay) par defi.</summary>
    internal static string DossierDesRecordsLocaux { get; set; } = Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "fantomes", "locaux");

    internal sealed record RecordLocal(long Images, string Replay, byte[] Fantome, long? Duree = null);

    private static string? NomSur(string defi) => System.Text.RegularExpressions.Regex.IsMatch(defi, "^[a-z0-9][a-z0-9.\\-]{0,95}$") ? defi : null;

    /// <summary>Garde mon meilleur temps sur cette borne, s'il bat celui qui y est deja.</summary>
    internal static void EcrireLeRecordLocal(string defi, long images, string replay, byte[] fantome, long? duree = null)
    {
        if (NomSur(defi) is not { } nom) return;
        if (LireLeRecordLocal(defi) is { } deja && deja.Images <= images) return;
        Directory.CreateDirectory(DossierDesRecordsLocaux);
        var chemin = Path.Combine(DossierDesRecordsLocaux, nom);
        File.WriteAllBytes(chemin + ".nghost.tmp", fantome);
        File.Move(chemin + ".nghost.tmp", chemin + ".nghost", true);
        File.WriteAllText(chemin + ".json", JsonSerializer.Serialize(new { images, replay, duree, le = DateTime.UtcNow }));
    }

    internal static RecordLocal? LireLeRecordLocal(string defi)
    {
        if (NomSur(defi) is not { } nom) return null;
        try
        {
            var chemin = Path.Combine(DossierDesRecordsLocaux, nom);
            if (!File.Exists(chemin + ".json") || !File.Exists(chemin + ".nghost")) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(chemin + ".json"));
            return new RecordLocal(doc.RootElement.GetProperty("images").GetInt64(), doc.RootElement.GetProperty("replay").GetString() ?? "",
                File.ReadAllBytes(chemin + ".nghost"),
                doc.RootElement.TryGetProperty("duree", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DossierDe(Hote h) => Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "nelfeplay", "ghosts", h.Crc);

    /// <summary>Mon fantome, le seul a etre remplace en cours de session ; « -rp_ » le fait vider a la fermeture.</summary>
    private const string PrefixeDuMien = "0-moi-";

    /// <summary>
    /// Les fantomes poses ici sortent du dossier que lit le coeur hote : ils ne restent pas pour la partie suivante (replay,
    /// MASS RUN). Seulement les notres (« 1-rp_….nghost ») : un fantome d'essai pose a la main ne se touche pas.
    /// </summary>
    public static void ViderLeDossier()
    {
        foreach (var hote in Hotes)
        {
            try
            {
                var dossier = DossierDe(hote);
                if (!Directory.Exists(dossier)) continue;
                foreach (var f in Directory.EnumerateFiles(dossier, "*-rp_*.nghost")) File.Delete(f);
                foreach (var f in Directory.EnumerateFiles(dossier, "*-rp_*.label")) File.Delete(f);
                foreach (var f in Directory.EnumerateFiles(dossier, "*-rp_*.duree")) File.Delete(f);
                // (11/11) les splits que le coeur a ecrits pour un fantome restent avec son .nghost dans le cache
                GarderLesSplits(dossier);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // un coeur encore ouvert : le prochain depart videra
            }
        }
    }

    private static string? CheminDuCoeur(string nom)
    {
        var retroarch = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");
        return new[] { "cores_real", "cores" }.Select(d => Path.Combine(retroarch, d, nom + "_libretro.dll")).FirstOrDefault(File.Exists);
    }
}
