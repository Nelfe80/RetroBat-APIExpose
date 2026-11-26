using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>Ce que la preparation des fantomes du TRAINING a pose, et pourquoi le reste ne l'a pas ete (cles de message).</summary>
public sealed record PreparationDuTraining(int Poses, IReadOnlyList<string> Refus, bool EnRetard = false)
{
    /// <summary>Aucun fantome n'a pu etre pose : la cle du message a dire au joueur (la raison la plus frequente).</summary>
    public string? MessageSansFantome
        => Poses > 0 ? null
            : EnRetard ? "not_ready"
            : Refus.Count == 0 ? "no_record"
            : Refus.GroupBy(r => r).OrderByDescending(g => g.Count()).First().Key;
}

/// <summary>
/// LES FANTOMES DU MODE TRAINING DES SHMUPS D'ARCADE SOUS FBNEO (26/11, decisions du user). Meme logique, memes reglages
/// d'affichage (<see cref="FantomesDuSpeedrun.Choix"/>, opacite, pseudos : le selecteur du panneau, `affichage.ini`) et meme
/// conversion replay vers .nghost que les speedruns ; ce qui change :
/// <list type="bullet">
/// <item>la source : le classement du World Scoring du jeu (<c>scores/board</c>), UNE regle par version du jeu (DaiOuJou :
/// White Label et Black Label), mon record et le record mondial de chacune ;</item>
/// <item>le replay ENTIER (depuis son etat de depart) : le coeur hote retrouve le depart de la partie par le profil du jeu ;</item>
/// <item>le controle de coeur : un replay n'est propose que s'il a ete enregistre par un FBNeo (<c>runtime.core_sha256</c>
/// ecrit a cote du .nghost dans un .coeur, que le coeur hote compare au sien) et sur le MEME contenu d'arcade (crc de la liste
/// triee des crc32 du .zip, <c>runtime.rom_files</c>) ; la version A ou B se filtre dans le coeur hote (octet de version du profil) ;</item>
/// <item>aucune admission du site : rien n'est classe, jamais (la partie part sous l'origine « speedrun », qui n'envoie aucun score).</item>
/// </list>
/// </summary>
public sealed partial class FantomesDuSpeedrun
{
    /// <summary>Le coeur hote du TRAINING (une seule DLL : notre FBNeo et le code des fantomes, decision du 26/11).</summary>
    public const string CoeurDuTraining = "fbneo_ghosts";

    /// <summary>Au plus quatre fantomes (le coeur hote ne court pas plus de copies).</summary>
    internal const int FantomesAuPlus = 4;

    private static readonly TimeSpan BudgetDuTraining = TimeSpan.FromSeconds(10);

    /// <summary>Ce point d'entrainement a des fantomes FBNeo. Pur.</summary>
    public static bool PourLeTraining(Reglement r) => r.EstUnEntrainement && r.FantomesDeTraining is not null;

    /// <summary>
    /// EMPREINTES DES FBNEO OFFICIELS dont le coeur hote est issu : vide, l'API ne filtre pas (le coeur hote ecarte lui-meme un
    /// fantome d'un autre FBNeo, journal « fantome ... ecarte »). (1.9.223) fbneo_ghosts 6518984f est construit sur le FBNeo
    /// 543bb3c de la borne, dont la DLL officielle est abea9d81 (`retro_nelfe_fantome_officiel()` du coeur) : seuls les replays
    /// enregistres par elle sont proposes. Un nouveau fbneo_ghosts sur un autre FBNeo ajoute l'empreinte de son officiel ici.
    /// </summary>
    internal static IReadOnlySet<string> OfficielsAdmis { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "abea9d8195e1435f366aa0def3401edd546ffec85c5fc3e7e8725f600dcc6415",
    };

    /// <summary>L'identite d'arcade d'un jeu : crc32 de la liste TRIEE des crc32 (petit-boutistes) des fichiers du .zip (DaiOuJou : be7be29b). Pur.</summary>
    public static string CrcDArcade(IEnumerable<string> crc32DesFichiers)
    {
        var valeurs = crc32DesFichiers
            .Select(c => uint.Parse(c.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(v => v).ToList();
        var octets = new byte[valeurs.Count * 4];
        for (var i = 0; i < valeurs.Count; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(octets.AsSpan(i * 4, 4), valeurs[i]);
        return RetroBat.Api.MassRun.JeuxMassRun.Crc32(octets).ToString("x8");
    }

    /// <summary>
    /// Ce replay peut-il devenir un fantome de ce jeu ? Rend null s'il le peut, sinon la raison (cle de message) : pas de coeur
    /// FBNeo enregistre, un autre FBNeo que ceux admis, un autre contenu d'arcade ou un contenu inconnu. Pur.
    /// </summary>
    internal static string? ReplayRefuse(ReplayManifest manifeste, string crcDuJeu)
    {
        var sha = (manifeste.Runtime.CoreSha256 ?? "").Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$")) return "no_core";
        if (manifeste.Runtime.CoreName is { Length: > 0 } nom && !nom.Contains("fbneo", StringComparison.OrdinalIgnoreCase)) return "other_core";
        if (OfficielsAdmis.Count > 0 && !OfficielsAdmis.Contains(sha)) return "other_core";
        if (manifeste.Runtime.RomFiles is not { Count: > 0 } fichiers) return "other_game";
        try { return string.Equals(CrcDArcade(fichiers.Select(f => f.Crc32)), crcDuJeu, StringComparison.OrdinalIgnoreCase) ? null : "other_game"; }
        catch (FormatException) { return "other_game"; }
    }

    /// <summary>Un fantome voulu : la regle (la version du jeu), son role, le replay et le detenteur.</summary>
    internal sealed record Voulu(string Regle, string Role, string Replay, string Joueur);

    /// <summary>
    /// LES REPLAYS A ESSAYER : pour chaque regle (une par version du jeu), mon record puis le record mondial (le meilleur du
    /// classement qui a un replay), selon le choix du selecteur ; sans doublon, quatre au plus. Pur.
    /// </summary>
    internal static IReadOnlyList<Voulu> ChoisirPourLeTraining(IReadOnlyList<(string Regle, IReadOnlyList<(string Joueur, string? Replay)> Lignes)> classements,
        string pseudo, ChoixDesFantomes choix)
    {
        var voulus = new List<Voulu>();
        foreach (var (regle, lignes) in classements)
        {
            var mien = lignes.FirstOrDefault(l => pseudo.Length > 0 && l.Replay is { Length: > 0 }
                && string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase));
            var mondial = lignes.FirstOrDefault(l => l.Replay is { Length: > 0 });
            if (choix is ChoixDesFantomes.MonRecordEtMondial or ChoixDesFantomes.MonRecord && mien.Replay is { Length: > 0 } rm)
                voulus.Add(new Voulu(regle, RoleMien, rm, mien.Joueur));
            if (choix is ChoixDesFantomes.MonRecordEtMondial or ChoixDesFantomes.RecordMondial && mondial.Replay is { Length: > 0 } rw)
                voulus.Add(new Voulu(regle, string.Equals(mondial.Replay, mien.Replay, StringComparison.Ordinal) ? RoleMien : RoleMondial, rw, mondial.Joueur));
        }

        return voulus.DistinctBy(v => v.Replay, StringComparer.Ordinal).Take(FantomesAuPlus).ToList();
    }

    internal static string DossierDuTraining(string crc) => Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "nelfeplay", "ghosts", crc);

    /// <summary>
    /// Prepare les fantomes du TRAINING : vide le dossier du jeu, ecrit l'affichage, pose le profil, puis jusqu'a quatre
    /// fantomes (.nghost, .label, .coeur). Attend <see cref="BudgetDuTraining"/> au plus ; en retard, la partie part sans (le
    /// coeur hote charge ses fantomes au demarrage).
    /// </summary>
    public PreparationDuTraining PreparerLeTraining(Reglement r, string pseudo)
    {
        ViderLeDossier();
        lock (_verrou) { _enCourse = false; _fantomeAttendu = null; OublierLesPoses(); }
        if (r.FantomesDeTraining is not { } t) return new(0, []);
        ViderLeDossierDuTraining(t.Crc);
        EcrireLeFichierDuCoeur();
        RetroBat.Api.MassRun.FichiersDesCoeursHotes.PoserLesProfils();
        if (Choix == ChoixDesFantomes.Aucun) return new(0, []);
        var travail = Task.Run(() => PreparerLeTrainingAsync(t, pseudo, CancellationToken.None));
        try
        {
            return travail.Wait(BudgetDuTraining) ? travail.Result : new(0, [], EnRetard: true);
        }
        catch (AggregateException e)
        {
            _journal?.LogWarning(e.InnerException, "Fantomes : preparation du TRAINING {Defi} echouee, la partie part sans.", r.ChallengeId);
            return new(0, ["not_ready"]);
        }
    }

    private async Task<PreparationDuTraining> PreparerLeTrainingAsync(FantomesDEntrainement t, string pseudo, CancellationToken ct)
    {
        var classements = new List<(string Regle, IReadOnlyList<(string Joueur, string? Replay)> Lignes)>();
        foreach (var regle in t.Regles)
        {
            try
            {
                using var client = _http.CreateClient(IndexDesDefisService.HttpClientName);
                var url = $"{NelfePlayAgentService.BaseUrl.TrimEnd('/')}/api/v1/scores/board?game={Uri.EscapeDataString(t.Jeu)}&ruleset={Uri.EscapeDataString(regle)}&limit=100";
                using var json = JsonDocument.Parse(await LecturesConditionnelles.Partagees.LireAsync(client, url, ct).ConfigureAwait(false));
                classements.Add((regle, LignesDuClassement(json.RootElement)));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                _journal?.LogInformation("Fantomes : classement {Regle} de {Jeu} injoignable pour le TRAINING.", regle, t.Jeu);
                classements.Add((regle, []));
            }
        }

        var voulus = ChoisirPourLeTraining(classements, pseudo, Choix);
        var dossier = Directory.CreateDirectory(DossierDuTraining(t.Crc)).FullName;
        var refus = new List<string>();
        var poses = 0;
        foreach (var v in voulus)
        {
            var manifeste = await ManifesteAsync(v.Replay, ct).ConfigureAwait(false);
            if (manifeste is null) { refus.Add("not_ready"); continue; }
            if (ReplayRefuse(manifeste, t.Crc) is { } raison)
            {
                _journal?.LogInformation("Fantomes : {Replay} ({Regle}) ecarte du TRAINING : {Raison}.", v.Replay, v.Regle, raison);
                refus.Add(raison);
                continue;
            }

            if (await FantomeAsync(v.Replay, ct).ConfigureAwait(false) is not { } fantome) { refus.Add("not_ready"); continue; }
            var nom = (v.Role == RoleMien ? PrefixeDuMien : $"{poses + 1}-") + v.Replay;
            await File.WriteAllBytesAsync(Path.Combine(dossier, nom + ".nghost"), AuCrcDuJeu(fantome, t.Crc), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dossier, nom + ".coeur"), manifeste.Runtime.CoreSha256!.Trim().ToLowerInvariant(),
                new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            EcrireLEtiquette(dossier, nom, Etiquette(v.Joueur, v.Role == RoleMondial));
            Poser(new FantomePose(nom, v.Replay, Etiquette(v.Joueur, v.Role == RoleMondial), v.Role, manifeste.Frames.ReplayEnd));
            poses++;
        }

        _journal?.LogInformation("Fantomes : TRAINING {Jeu}, {Poses} fantome(s) sur {Voulus} voulu(s).", t.Jeu, poses, voulus.Count);
        return new(poses, refus);
    }

    private async Task<ReplayManifest?> ManifesteAsync(string replay, CancellationToken ct)
    {
        var manifeste = _manifestes.GetManifest(replay);
        if (manifeste is not null) return manifeste;
        manifeste = await _recherche.FetchAsync(replay, ct).ConfigureAwait(false);
        if (manifeste is not null) _manifestes.SaveManifest(manifeste);
        return manifeste;
    }

    /// <summary>Vide les fantomes que nous avons poses pour une partie precedente (« -rp_ ») ; un fantome pose a la main reste.</summary>
    internal static void ViderLeDossierDuTraining(string crc)
    {
        try
        {
            var dossier = DossierDuTraining(crc);
            if (!Directory.Exists(dossier)) return;
            foreach (var f in Directory.EnumerateFiles(dossier, "*-rp_*")
                         .Where(f => f.EndsWith(".nghost") || f.EndsWith(".label") || f.EndsWith(".coeur") || f.EndsWith(".duree") || f.EndsWith(".splits")))
                File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // un coeur encore ouvert : le prochain depart videra
        }
    }
}
