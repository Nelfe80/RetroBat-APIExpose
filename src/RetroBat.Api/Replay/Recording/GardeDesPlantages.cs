using System.Text.Json;

namespace RetroBat.Api.Replay.Recording;

/// <summary>
/// UN REPLAY NE DOIT PAS COUTER LA PARTIE (2026-10-03).
///
/// RetroArch 1.22.2 peut planter en enregistrant un replay : son encodeur de points de controle
/// lit au-dela de la fin de l'etat du jeu, et il ne tombe que si cette lecture touche une page
/// memoire non allouee. Cela depend de la taille de l'etat (donc du coeur et de sa version) et de
/// la machine. Chez un testeur, 1942 sous FBNeo est mort deux fois quelques secondes apres le
/// debut du replay, au premier point de controle (`replay_checkpoint_interval = 5`) ; la borne de
/// developpement enregistre le meme jeu sans rien. Le joueur voit le jeu se figer, HOTKEY+START
/// ne repond plus, et la partie est perdue avec son score : le wrapper n'a pas pu envoyer la fin
/// de partie.
///
/// LE SIGNE : le wrapper envoie la fin de partie en dechargeant le coeur, ce que RetroArch fait a
/// toute sortie propre (le plantage connu a la fermeture du pilote de manettes vient APRES : la
/// fin de partie est deja partie). Un jeu NelfePlay dont l'ecoute s'est annoncee (attestation),
/// qui a enregistre un replay et qui se termine sans fin de partie, c'est RetroArch mort en jeu.
///
/// LA PARADE : des le premier plantage, ce jeu ne s'enregistre plus sur cette borne, avec ce coeur
/// et ce RetroArch. Un film qui manque ne coute presque rien ; une partie perdue coute la partie.
/// La cle porte l'empreinte du coeur et celle de RetroArch : une mise a jour de l'un ou de l'autre
/// fait reessayer.
///
/// L'INSCRIPTION EXPIRE (16/11, decision user). Chez Old AF Gamer, 19XX et Gunlock sont entres le 15/11 (msvcrt.dll) alors
/// que 19XX s'enregistre tres bien ailleurs le meme jour : le plantage tient a une machine et a un moment (Vulkan, shaders
/// en essai). Inscrits pour une cle qui ne change jamais en pratique, ils ne s'enregistraient plus jamais. Une inscription
/// dure maintenant <see cref="DelaiDeBase"/> (Replay:Record:CrashSkipDays, 7 jours par defaut), puis le jeu est retente.
/// S'il replante, il revient pour un delai qui double a chaque recidive (7, 14, 28 jours...), plafonne a
/// <see cref="DelaiMaximum"/>. Le fichier garde l'historique des plantages de chaque cle, meme une fois l'inscription
/// expiree ou levee : une recidive se compte toujours. Une inscription se leve a la main par la route locale
/// POST /api/v1/replay/skip-list/reset (outil de diagnostic), qui garde aussi cet historique.
/// </summary>
public sealed class GardeDesPlantages
{
    /// <summary>La fin de partie arrive avant la fin du jeu vue par ES ; on lui laisse ce delai.</summary>
    public static readonly TimeSpan DelaiDeJugement = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Une inscription. <see cref="Le"/> : le dernier plantage ; <see cref="Jusqua"/> : le prochain essai (null dans un
    /// fichier v1, lu comme <see cref="Le"/> + le delai de base) ; <see cref="Plantages"/> : chaque plantage de cette cle ;
    /// <see cref="LeveeLe"/> : la levee a la main, s'il y en a eu une depuis le dernier plantage.
    /// </summary>
    public sealed record Renoncement(string Cle, string Jeu, string Coeur, string RetroArch, DateTime Le)
    {
        public DateTime? Jusqua { get; init; }
        public List<DateTime>? Plantages { get; init; }
        public DateTime? LeveeLe { get; init; }

        /// <summary>Le nombre de plantages connus (un au moins : celui de <see cref="Le"/>).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int NombreDePlantages => Math.Max(1, Plantages?.Count ?? 0);

        /// <summary>Le systeme, lu dans la cle (« systeme/jeu|... »).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Systeme => Cle.IndexOf('/') is var i and > 0 ? Cle[..i] : "";
    }

    private sealed record Fichier(string Schema, List<Renoncement> Jeux);

    /// <summary>Le delai par defaut d'une premiere inscription, en jours (Replay:Record:CrashSkipDays).</summary>
    public const int JoursParDefaut = 7;

    /// <summary>Le plafond du delai, quelle que soit la recidive.</summary>
    public static readonly TimeSpan DelaiMaximum = TimeSpan.FromDays(365);

    private sealed class Partie
    {
        public bool EcouteVue;
        public bool FinDePartieRecue;
        public string Coeur = "";
        public string? Cle;
        public string Jeu = "";
        public string RetroArch = "";
        public DateTime DebutLe;
        public DateTime FinLe;
    }

    private const string Schema = "nelfe.replay.sans-enregistrement.v2";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _verrou = new();
    private readonly Func<DateTime> _maintenant;
    private Partie? _enCours;
    private Partie? _aJuger;
    private Dictionary<string, Renoncement>? _renoncements;
    private readonly Func<DateTime, DateTime, bool?>? _plantageWindows;

    /// <param name="plantageWindows">
    /// (17/11) Windows a-t-il vu RetroArch planter entre ces deux instants UTC (<see cref="PlantagesWindows.EnJeuEntre"/>) ?
    /// Faux : la fin sans fin de partie n'est pas un plantage, rien n'est inscrit. Null (journal illisible) ou sans
    /// fonction (essais) : l'ancien jugement, la fin de partie seule.
    /// </param>
    public GardeDesPlantages(string chemin, Func<DateTime>? maintenant = null, TimeSpan? delaiDeBase = null,
        Func<DateTime, DateTime, bool?>? plantageWindows = null)
    {
        Chemin = chemin;
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
        _plantageWindows = plantageWindows;
        DelaiDeBase = delaiDeBase is { } d && d > TimeSpan.Zero ? (d > DelaiMaximum ? DelaiMaximum : d) : TimeSpan.FromDays(JoursParDefaut);
    }

    /// <summary>La garde de cette borne : a cote de l'enregistrement en cours, delai lu dans Replay:Record:CrashSkipDays.</summary>
    public static GardeDesPlantages Pour(RetroBat.Api.Replay.Storage.ReplayStore store, IConfiguration? config)
    {
        double jours = JoursParDefaut;
        try { if (config is not null) jours = config.GetValue("Replay:Record:CrashSkipDays", (double)JoursParDefaut); }
        catch (Exception) { /* valeur illisible : le defaut */ }
        return new GardeDesPlantages(Path.Combine(Path.GetDirectoryName(store.ActiveRecordingPath) ?? ".", "sans-enregistrement.json"),
            delaiDeBase: TimeSpan.FromDays(Math.Clamp(jours, 0, DelaiMaximum.TotalDays)),
            plantageWindows: PlantagesWindows.EnJeuEntre);
    }

    /// <summary>Le fichier des jeux qu'on n'enregistre plus.</summary>
    public string Chemin { get; }

    /// <summary>Le delai d'une premiere inscription ; il double a chaque recidive.</summary>
    public TimeSpan DelaiDeBase { get; }

    /// <summary>
    /// Le delai d'une inscription apres <paramref name="plantages"/> plantages : la base, doublee a chaque recidive, au plus
    /// <see cref="DelaiMaximum"/>. Pur.
    /// </summary>
    public static TimeSpan Delai(TimeSpan baseDuDelai, int plantages)
    {
        var delai = baseDuDelai;
        for (var i = 1; i < plantages && delai < DelaiMaximum; i++) delai += delai;
        return delai > DelaiMaximum ? DelaiMaximum : delai;
    }

    /// <summary>Le prochain essai d'une inscription (une inscription v1 n'en a pas : son plantage + la base).</summary>
    public DateTime ProchainEssai(Renoncement r) => r.Jusqua ?? r.Le + DelaiDeBase;

    /// <summary>L'inscription tient-elle encore a cet instant ?</summary>
    public bool Active(Renoncement r) => _maintenant() < ProchainEssai(r);

    /// <summary>La cle d'un jeu sous un coeur et un RetroArch donnes.</summary>
    public static string Cle(string systeme, string jeu, string? crc, string coeur, string retroArch)
        => $"{systeme}/{jeu}|crc={(string.IsNullOrWhiteSpace(crc) ? "?" : crc.Trim())}|coeur={coeur}|retroarch={retroArch}";

    /// <summary>
    /// L'empreinte du coeur d'une attestation : celle du binaire, sinon son nom et sa version.
    /// </summary>
    public static string CoeurDeLAttestation(JsonElement attestation)
    {
        static string? Lire(JsonElement e, string nom)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        if (Lire(attestation, "CoreSha256") is { Length: >= 16 } sha) return sha[..16].ToLowerInvariant();
        var nom = Lire(attestation, "CoreName");
        var version = Lire(attestation, "CoreVersion");
        return string.IsNullOrWhiteSpace(nom) && string.IsNullOrWhiteSpace(version)
            ? "?"
            : $"{nom?.Trim()} {version?.Trim()}".Trim();
    }

    /// <summary>Le coeur du jeu en cours, d'apres son attestation ; « ? » sans elle.</summary>
    public string CoeurEnCours
    {
        get { lock (_verrou) return _enCours?.Coeur is { Length: > 0 } coeur ? coeur : "?"; }
    }

    /// <summary>ES lance un jeu.</summary>
    public void JeuLance()
    {
        lock (_verrou) _enCours = new Partie();
    }

    /// <summary>L'ecoute du jeu s'est annoncee (attestation du wrapper ou du pont Lua).</summary>
    public void EcouteVue(string coeur)
    {
        lock (_verrou)
        {
            _enCours ??= new Partie();
            _enCours.EcouteVue = true;
            if (!string.IsNullOrWhiteSpace(coeur)) _enCours.Coeur = coeur.Trim();
        }
    }

    /// <summary>La fin de partie est arrivee : RetroArch a decharge le coeur.</summary>
    public void FinDePartieRecue()
    {
        lock (_verrou)
        {
            if (_enCours is { } enCours) enCours.FinDePartieRecue = true;
            else if (_aJuger is { } aJuger) aJuger.FinDePartieRecue = true;
        }
    }

    /// <summary>RetroArch a commence a enregistrer ce jeu.</summary>
    public void EnregistrementLance(string cle, string jeu, string retroArch)
    {
        lock (_verrou)
        {
            _enCours ??= new Partie();
            _enCours.Cle = cle;
            _enCours.Jeu = jeu;
            _enCours.RetroArch = retroArch;
            if (_enCours.DebutLe == default) _enCours.DebutLe = _maintenant();
        }
    }

    /// <summary>ES a repris la main : le jeu est fini, il sera juge apres <see cref="DelaiDeJugement"/>.</summary>
    public void JeuTermine()
    {
        lock (_verrou)
        {
            if (_enCours is null) return;
            _enCours.FinLe = _maintenant();
            _aJuger = _enCours;
            _enCours = null;
        }
    }

    /// <summary>
    /// Juge le jeu termine une fois le delai passe. Rend le renoncement prononce, sinon null.
    /// </summary>
    public Renoncement? Juger() => Juger(out _);

    /// <summary>
    /// Comme <see cref="Juger()"/> ; <paramref name="sortieSansPlantage"/> nomme le jeu fini sans fin de partie que le
    /// journal Windows disculpe (aucun plantage de RetroArch pendant son enregistrement) : rien n'est inscrit.
    /// </summary>
    public Renoncement? Juger(out string? sortieSansPlantage)
    {
        sortieSansPlantage = null;
        lock (_verrou)
        {
            if (_aJuger is not { } partie || _maintenant() - partie.FinLe < DelaiDeJugement) return null;
            _aJuger = null;
            if (partie.Cle is null || !partie.EcouteVue || partie.FinDePartieRecue) return null;
            if (_plantageWindows is { } plantageWindows)
            {
                var debut = (partie.DebutLe == default ? partie.FinLe - FenetreSansDebut : partie.DebutLe) - TimeSpan.FromMinutes(1);
                if (plantageWindows(debut, _maintenant()) == false)
                {
                    sortieSansPlantage = partie.Jeu;
                    return null;
                }
            }

            // Une recidive (la cle a deja plante, inscription expiree ou levee) : l'historique s'allonge, le delai double.
            var maintenant = _maintenant();
            var plantages = Charger().TryGetValue(partie.Cle, out var avant) ? Historique(avant) : new List<DateTime>();
            plantages.Add(maintenant);
            var renoncement = new Renoncement(partie.Cle, partie.Jeu, partie.Coeur.Length > 0 ? partie.Coeur : "?", partie.RetroArch, maintenant)
            {
                Plantages = plantages,
                Jusqua = maintenant + Delai(DelaiDeBase, plantages.Count),
            };
            Charger()[partie.Cle] = renoncement;
            Ecrire();
            return renoncement;
        }
    }

    /// <summary>Sans heure de debut d'enregistrement, la fenetre ou chercher le plantage avant la fin vue par ES.</summary>
    public static readonly TimeSpan FenetreSansDebut = TimeSpan.FromMinutes(30);

    /// <summary>
    /// REVOIR LES INSCRIPTIONS AU JOURNAL WINDOWS (17/11). Avant la confirmation par le journal, une sortie sans fin de
    /// partie suffisait : les bornes portent des inscriptions sans plantage. Chaque plantage d'une inscription (active ou
    /// non : une recidive se compte sur l'historique) se cherche dans les <see cref="FenetreSansDebut"/> qui le precedent ; ceux que le journal dement s'effacent
    /// (un journal illisible ou trop court ne dement rien). Sans plantage restant, l'inscription disparait ; sinon son
    /// delai se recalcule sur ce qui reste. Rend les inscriptions revues, avant revision. Sans fonction : rien.
    /// </summary>
    public IReadOnlyList<Renoncement> RevoirAuJournalWindows()
    {
        if (_plantageWindows is not { } plantageWindows) return Array.Empty<Renoncement>();
        // Le journal se lit HORS du verrou (plusieurs secondes sur un gros journal) : la garde continue de juger.
        List<Renoncement> inscrites;
        lock (_verrou) inscrites = Charger().Values.ToList();
        var dementis = new Dictionary<string, HashSet<DateTime>>(StringComparer.Ordinal);
        foreach (var r in inscrites)
        {
            var faux = Historique(r).Where(le => plantageWindows(le - FenetreSansDebut, le) == false).ToHashSet();
            if (faux.Count > 0) dementis[r.Cle] = faux;
        }
        if (dementis.Count == 0) return Array.Empty<Renoncement>();

        lock (_verrou)
        {
            var revues = new List<Renoncement>();
            foreach (var (cle, faux) in dementis)
            {
                if (!Charger().TryGetValue(cle, out var r)) continue;
                var confirmes = Historique(r).Where(le => !faux.Contains(le)).ToList();
                revues.Add(r);
                if (confirmes.Count == 0)
                {
                    Charger().Remove(cle);
                    continue;
                }
                var dernier = confirmes.Max();
                // Une inscription expiree ou levee garde son prochain essai : seul son historique se corrige.
                Charger()[cle] = r with
                {
                    Le = dernier, Plantages = confirmes,
                    Jusqua = Active(r) ? dernier + Delai(DelaiDeBase, confirmes.Count) : r.Jusqua,
                };
            }
            if (revues.Count > 0) Ecrire();
            return revues;
        }
    }

    /// <summary>Le renoncement qui vise cette cle, s'il tient encore (une inscription expiree fait reessayer).</summary>
    public Renoncement? RenoncementPour(string cle)
    {
        lock (_verrou) return Charger().TryGetValue(cle, out var renoncement) && Active(renoncement) ? renoncement : null;
    }

    /// <summary>L'inscription de cette cle, qu'elle tienne encore ou non (l'historique de ses plantages).</summary>
    public Renoncement? InscriptionPour(string cle)
    {
        lock (_verrou) return Charger().TryGetValue(cle, out var renoncement) ? renoncement : null;
    }

    /// <summary>Toutes les inscriptions du fichier, actives ou non, la plus recente en tete.</summary>
    public IReadOnlyList<Renoncement> Inscriptions()
    {
        lock (_verrou) return Charger().Values.OrderByDescending(r => r.Le).ToList();
    }

    /// <summary>
    /// Leve a la main les inscriptions actives (toutes, ou celle de <paramref name="cle"/>) : le jeu se retente des sa
    /// prochaine partie. L'historique reste, un nouveau plantage compte donc comme une recidive. Rend les inscriptions levees.
    /// </summary>
    public IReadOnlyList<Renoncement> Lever(string? cle = null)
    {
        lock (_verrou)
        {
            var maintenant = _maintenant();
            var levees = Charger().Values
                .Where(r => (cle is null || string.Equals(r.Cle, cle, StringComparison.Ordinal)) && Active(r))
                .Select(r => r with { Jusqua = maintenant, LeveeLe = maintenant, Plantages = Historique(r) })
                .ToList();
            foreach (var r in levees) Charger()[r.Cle] = r;
            if (levees.Count > 0) Ecrire();
            return levees;
        }
    }

    private static List<DateTime> Historique(Renoncement r)
        => r.Plantages is { Count: > 0 } connus ? new List<DateTime>(connus) : new List<DateTime> { r.Le };

    private Dictionary<string, Renoncement> Charger()
    {
        if (_renoncements is not null) return _renoncements;
        _renoncements = new Dictionary<string, Renoncement>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(Chemin) && JsonSerializer.Deserialize<Fichier>(File.ReadAllText(Chemin), Options) is { Jeux: { } jeux })
            {
                foreach (var jeu in jeux.Where(j => !string.IsNullOrWhiteSpace(j.Cle))) _renoncements[jeu.Cle] = jeu;
            }
        }
        catch (Exception)
        {
            // Illisible : on repart d'une liste vide, au pire un jeu se reenregistre.
        }
        return _renoncements;
    }

    private void Ecrire()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Chemin)!);
            var temporaire = Chemin + ".tmp";
            File.WriteAllText(temporaire, JsonSerializer.Serialize(new Fichier(Schema, Charger().Values.OrderBy(r => r.Le).ToList()), Options));
            File.Move(temporaire, Chemin, overwrite: true);
        }
        catch (Exception)
        {
            // La liste reste en memoire jusqu'au redemarrage de l'API.
        }
    }
}
