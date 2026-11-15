using System.Text.Json;
using RetroBat.Api.Media;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UN CONCOURS SUR SCORE CERTIFIE (LiveContest, 15/11). Le concours nomme un classement de NelfePlay :
/// <c>/play/contest</c> porte <c>certified: { game, system, ruleset }</c> (null pour un concours declare).
///
/// - <c>game</c> est le rom_group du site (« 19xx-the-war-against-destiny »), jamais le nom d'une ROM ;
/// - <c>system</c> est ce que le streamer a declare (« fbneo », « arcade », « snes »...) ou rien : la machine par defaut
///   du jeu, celle de la collection World Scoring ;
/// - <c>ruleset</c> est le classement : 1cc, 1lc, ou un mode du jeu (« 1cc-normal » de TGM2).
///
/// Le jeu lance est celui que la collection World Scoring a retenu pour ce classement (version de reference, FBNeo avant
/// MAME en arcade) ; son cœur est impose comme pour tout lancement NelfePlay. Le score est celui que le scoring certifie
/// envoie : le client du concours ne compte plus rien lui-meme. Ce qui manque se refuse, au lieu de lancer autre chose.
/// </summary>
public static class LiveContestCertifie
{
    /// <summary>Ce que le concours demande. Les champs sont deja ramenes en minuscules, sans espaces autour.</summary>
    public sealed record Demande(string Jeu, string Systeme, string Regle);

    /// <summary>
    /// Le jeu pret a partir (<see cref="Rom"/> non nulle), ou le refus : un code stable (<see cref="Refus"/>, remonte au
    /// streamer comme phase du joueur) et ce qu'on en dit au joueur.
    /// </summary>
    public sealed record Preparation(string? Rom, string Systeme, string Jeu, string Regle, string? Refus, string? Detail)
    {
        public bool Prete => Rom is not null && Refus is null;
    }

    // Les codes de refus : la phase que le streamer voit pour ce joueur.
    public const string RefusIncomplet = "certified-incomplete";
    public const string RefusSansIndex = "certified-no-index";
    public const string RefusJeuFerme = "certified-game-not-open";
    public const string RefusModeInconnu = "certified-mode-unknown";
    public const string RefusModeNonJouable = "certified-mode-not-playable";
    public const string RefusRomAbsente = "certified-rom-not-found";
    public const string RefusSansCompte = "certified-account-not-linked";

    /// <summary>
    /// La demande certifiee du concours, ou null pour un concours declare (champ absent ou null) : celui-la part comme
    /// avant, par son <c>gameSlug</c>. Un objet <c>certified</c> sans jeu ou sans regle rend une demande incomplete, que
    /// <see cref="Resoudre"/> refuse : un concours certifie ne retombe jamais sur le jeu du nom de la ROM.
    /// </summary>
    public static Demande? Lire(JsonElement concours)
    {
        if (concours.ValueKind != JsonValueKind.Object
            || !concours.TryGetProperty("certified", out var certifie)
            || certifie.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Demande(Texte(certifie, "game"), Texte(certifie, "system"), Texte(certifie, "ruleset"));
    }

    /// <summary>
    /// Le fichier a lancer pour ce classement.
    /// </summary>
    /// <param name="jeuxOuverts">L'index des jeux ouverts garde par la borne ; null sans index.</param>
    /// <param name="cheminsDuScoring">Le fichier retenu par la collection World Scoring, par « systeme/jeu » ; null avant sa
    /// premiere passe.</param>
    /// <param name="repli">Avant la premiere passe seulement : la ROM installee dont l'identite de scoring est ce jeu.</param>
    /// <param name="existe">Le fichier existe-t-il (disque) ?</param>
    /// <param name="manques">Pourquoi chaque jeu ouvert manque a la collection (« 19xx : ROM absente »).</param>
    public static Preparation Resoudre(
        Demande demande,
        IReadOnlyList<OpenGame>? jeuxOuverts,
        IReadOnlyDictionary<string, string>? cheminsDuScoring,
        Func<string, string, string?>? repli,
        Func<string, bool> existe,
        IReadOnlyList<string>? manques = null)
    {
        var jeu = demande.Jeu;
        var regle = demande.Regle;
        var systemeDemande = demande.Systeme.Length == 0 ? "" : RomCanonicalResolver.CanonicalScoringSystem(demande.Systeme);
        Preparation Refuser(string code, string detail, string systeme = "")
            => new(null, systeme.Length > 0 ? systeme : systemeDemande, jeu, regle, code, detail);

        if (jeu.Length == 0 || regle.Length == 0)
        {
            return Refuser(RefusIncomplet, "concours certifie sans jeu ou sans regle");
        }
        if (jeuxOuverts is null)
        {
            return Refuser(RefusSansIndex, "index des jeux ouverts jamais recu par cette borne");
        }

        // Le jeu ouvert : sur la machine demandee, sinon celle que la collection a retenue, sinon la premiere ouverte.
        var candidats = jeuxOuverts
            .Where(g => string.Equals(g.RomGroup, jeu, StringComparison.OrdinalIgnoreCase))
            .Where(g => systemeDemande.Length == 0
                || string.Equals(RomCanonicalResolver.CanonicalScoringSystem(g.SystemId), systemeDemande, StringComparison.Ordinal))
            .ToList();
        if (candidats.Count == 0)
        {
            return Refuser(RefusJeuFerme, systemeDemande.Length == 0
                ? $"« {jeu} » n'est pas ouvert au World Scoring"
                : $"« {jeu} » n'est pas ouvert au World Scoring sur {systemeDemande}");
        }
        var ouvert = candidats.FirstOrDefault(g => Retenu(cheminsDuScoring, g, existe) is not null) ?? candidats[0];
        var systeme = RomCanonicalResolver.CanonicalScoringSystem(ouvert.SystemId);

        var regles = ouvert.Rulesets.Select(r => r.Id).Where(id => id.Length > 0).ToList();
        if (!regles.Contains(regle, StringComparer.OrdinalIgnoreCase))
        {
            return Refuser(RefusModeInconnu,
                $"« {regle} » n'est pas un classement ouvert de « {jeu} » (ouverts : {string.Join(", ", regles)})", systeme);
        }
        // Une partie a plusieurs se monte en MULTIPLAYER, un speedrun par son epreuve : aucun des deux ne part d'un concours.
        if (ModesDeJeu.EstMulti(regle) || ModesDeJeu.Est1LCMulti(regle) || ModesDeJeu.EstSpeedrun(regle))
        {
            return Refuser(RefusModeNonJouable, $"« {regle} » ne se joue pas seul depuis un concours", systeme);
        }

        string? rom;
        if (cheminsDuScoring is null)
        {
            // La collection n'a pas encore fait sa premiere passe depuis le demarrage : la recherche du lancement depuis le
            // site (meme identite de scoring). Le cœur reste impose au lancement, et le site juge la partie.
            rom = repli?.Invoke(systeme, ouvert.RomGroup);
            if (rom is not null && !existe(rom)) rom = null;
        }
        else
        {
            rom = Retenu(cheminsDuScoring, ouvert, existe);
        }

        if (rom is null)
        {
            var raison = manques?.FirstOrDefault(m => m.StartsWith(ouvert.RomGroup + " :", StringComparison.OrdinalIgnoreCase));
            return Refuser(RefusRomAbsente, raison ?? $"« {jeu} » n'est pas dans la collection World Scoring de cette borne", systeme);
        }

        return new Preparation(rom, systeme, ouvert.RomGroup, regle, null, null);
    }

    private static string? Retenu(IReadOnlyDictionary<string, string>? chemins, OpenGame jeu, Func<string, bool> existe)
        => chemins is not null && chemins.TryGetValue(jeu.SystemId + "/" + jeu.RomGroup, out var chemin) && existe(chemin)
            ? chemin
            : null;

    /// <summary>
    /// LE MODE A CHOISIR DANS LE JEU, ou null. Un mode se choisit dans le jeu (octet GAME_MODE du .MEM) : l'API ne peut pas
    /// l'imposer, elle le dit. Le libelle vient du profil de ce classement garde sur disque (« Normal ») ; sans copie, le
    /// nom du mode de la regle (« 1cc-normal » donne « NORMAL »). Un jeu sans modes, ou un profil sans mode : null.
    /// </summary>
    /// <param name="corpsDesProfils">La derniere reponse du site gardee pour ce jeu (<c>profiles</c>), ou null.</param>
    public static string? ModeAChoisir(string? corpsDesProfils, string regle, bool francais)
    {
        if (!string.IsNullOrWhiteSpace(corpsDesProfils)
            && NelfePlayScoringReporter.ProfilsDuCorps(corpsDesProfils) is { Count: > 0 } profils)
        {
            var profil = profils.FirstOrDefault(p => p.TryGetProperty("ruleset", out var r) && r.ValueKind == JsonValueKind.String
                && string.Equals(r.GetString(), regle, StringComparison.OrdinalIgnoreCase));
            if (profil.ValueKind == JsonValueKind.Object)
            {
                if (!profil.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.Object) return null;
                if (mode.TryGetProperty("label", out var libelles))
                {
                    if (libelles.ValueKind == JsonValueKind.String && libelles.GetString() is { Length: > 0 } seul) return seul;
                    if (libelles.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var langue in francais ? new[] { "fr", "en" } : new[] { "en", "fr" })
                        {
                            if (libelles.TryGetProperty(langue, out var l) && l.ValueKind == JsonValueKind.String
                                && l.GetString() is { Length: > 0 } libelle)
                            {
                                return libelle;
                            }
                        }
                    }
                }
            }
        }

        // « 1cc-normal » donne « normal », « 1cc » et « 1lc » rien : la lecture des onglets du panneau de classement.
        var nom = RetroBat.Api.Leaderboard.LeaderboardInputService.FamilleEtMode(regle).Mode;
        return nom.Length == 0 ? null : nom.Replace('-', ' ').ToUpperInvariant();
    }

    private static string Texte(JsonElement objet, string nom)
        => objet.TryGetProperty(nom, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? (valeur.GetString() ?? "").Trim().ToLowerInvariant()
            : "";
}


/// <summary>
/// LA SEANCE D'UN CONCOURS CERTIFIE : {world: stream, channel, contest} posee sur le joueur lie, pour que le scoring
/// certifie attribue la partie au concours.
///
/// Le brouillon du score lit la seance QUAND IL SE CREE, apres la fermeture de l'emulateur (les NVRAM se lisent apres).
/// La desarmer juste apres avoir ferme le jeu, comme le fait un concours declare, ferait partir le score certifie sans le
/// concours. A la cloture, la seance reste donc armee jusqu'au verdict du classement du concours, au lancement suivant
/// (une autre partie n'est plus le concours) ou, au plus, <see cref="DelaiMaximal"/>.
/// </summary>
public sealed class SeanceDuConcoursCertifie
{
    /// <summary>Le plus long qu'on garde la seance apres la cloture : un brouillon se cree en quelques secondes.</summary>
    public static readonly TimeSpan DelaiMaximal = TimeSpan.FromMinutes(2);

    private readonly NelfePlayScoringSessionService _session;
    private readonly Func<DateTime> _horloge;
    private readonly object _verrou = new();
    private (string Concours, string Jeu, string Regle, DateTime Echeance)? _attente;

    public SeanceDuConcoursCertifie(NelfePlayScoringSessionService session, Func<DateTime>? horloge = null)
    {
        _session = session;
        _horloge = horloge ?? (() => DateTime.UtcNow);
    }

    /// <summary>Arme la seance du concours. Faux sans joueur lie ou sans concours : rien n'est pose.</summary>
    public bool Armer(string? codeDuJoueur, string? chaine, string? concours)
    {
        if (string.IsNullOrWhiteSpace(codeDuJoueur) || string.IsNullOrWhiteSpace(concours)) return false;
        lock (_verrou)
        {
            _attente = null;
            _session.Set(codeDuJoueur, null, null, "stream", chaine, concours);
        }
        return true;
    }

    /// <summary>Le concours est clos : la seance tient jusqu'au verdict de son classement, au lancement suivant ou a l'echeance.</summary>
    public void DesarmerApresLaPartie(string concours, string jeu, string regle)
    {
        lock (_verrou)
        {
            _attente = (concours, jeu, regle, _horloge() + DelaiMaximal);
        }
    }

    /// <summary>Une seance armee attend-elle encore le score du concours clos ?</summary>
    public bool EnAttente
    {
        get
        {
            lock (_verrou) return _attente is not null;
        }
    }

    /// <summary>
    /// Un evenement du bus : le depart d'une partie (<c>ui.game.started</c>) ou le verdict du classement du concours
    /// (<c>scoring.verdict</c> du meme jeu et de la meme regle) desarme. Vrai si la seance vient d'etre desarmee.
    /// </summary>
    public bool SurEvenement(string? type, string? jeu, string? regle)
    {
        lock (_verrou)
        {
            if (_attente is not { } attente) return false;
            var fin = string.Equals(type, "ui.game.started", StringComparison.Ordinal)
                || (string.Equals(type, "scoring.verdict", StringComparison.Ordinal)
                    && string.Equals(jeu, attente.Jeu, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(regle, attente.Regle, StringComparison.OrdinalIgnoreCase));
            return (fin || _horloge() >= attente.Echeance) && Desarmer(attente.Concours);
        }
    }

    /// <summary>L'echeance passee, la seance se desarme. Vrai si elle vient de l'etre.</summary>
    public bool Echeance()
    {
        lock (_verrou)
        {
            return _attente is { } attente && _horloge() >= attente.Echeance && Desarmer(attente.Concours);
        }
    }

    /// <summary>Desarme tout de suite (desinscription, nouveau concours).</summary>
    public void Oublier()
    {
        lock (_verrou) _attente = null;
    }

    // Seule la seance de CE concours se retire : jamais celle d'une salle, ni celle d'un concours arme depuis.
    private bool Desarmer(string concours)
    {
        _attente = null;
        if (_session.Get() is { World: "stream" } seance && string.Equals(seance.ContestId, concours, StringComparison.Ordinal))
        {
            _session.Clear();
        }
        return true;
    }
}
