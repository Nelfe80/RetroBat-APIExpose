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
/// fait reessayer. Pour reessayer a la main, supprimer le fichier.
/// </summary>
public sealed class GardeDesPlantages
{
    /// <summary>La fin de partie arrive avant la fin du jeu vue par ES ; on lui laisse ce delai.</summary>
    public static readonly TimeSpan DelaiDeJugement = TimeSpan.FromSeconds(5);

    public sealed record Renoncement(string Cle, string Jeu, string Coeur, string RetroArch, DateTime Le);

    private sealed record Fichier(string Schema, List<Renoncement> Jeux);

    private sealed class Partie
    {
        public bool EcouteVue;
        public bool FinDePartieRecue;
        public string Coeur = "";
        public string? Cle;
        public string Jeu = "";
        public string RetroArch = "";
        public DateTime FinLe;
    }

    private const string Schema = "nelfe.replay.sans-enregistrement.v1";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _verrou = new();
    private readonly Func<DateTime> _maintenant;
    private Partie? _enCours;
    private Partie? _aJuger;
    private Dictionary<string, Renoncement>? _renoncements;

    public GardeDesPlantages(string chemin, Func<DateTime>? maintenant = null)
    {
        Chemin = chemin;
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
    }

    /// <summary>Le fichier des jeux qu'on n'enregistre plus.</summary>
    public string Chemin { get; }

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
    public Renoncement? Juger()
    {
        lock (_verrou)
        {
            if (_aJuger is not { } partie || _maintenant() - partie.FinLe < DelaiDeJugement) return null;
            _aJuger = null;
            if (partie.Cle is null || !partie.EcouteVue || partie.FinDePartieRecue) return null;

            var renoncement = new Renoncement(partie.Cle, partie.Jeu, partie.Coeur.Length > 0 ? partie.Coeur : "?", partie.RetroArch, _maintenant());
            Charger()[partie.Cle] = renoncement;
            Ecrire();
            return renoncement;
        }
    }

    /// <summary>Le renoncement qui vise cette cle, s'il y en a un.</summary>
    public Renoncement? RenoncementPour(string cle)
    {
        lock (_verrou) return Charger().TryGetValue(cle, out var renoncement) ? renoncement : null;
    }

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
