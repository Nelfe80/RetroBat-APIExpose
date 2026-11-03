using System.Security.Cryptography;
using System.Text.Json;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// Un evenement du .MEM attendu par le reglement. Les champs absents ne filtrent pas : une cible sans
/// adresse reconnait l'action ou qu'elle soit lue. <c>Valeur</c> porte « value » et « to » (la valeur
/// d'arrivee d'une ligne « change ») ; <c>Quitte</c> porte « leaves » (la valeur d'avant).
/// </summary>
public sealed record CibleEvenement(string? Action, long? Adresse, int? Valeur, int? Quitte);

/// <summary>La derniere valeur connue d'une adresse lue par le .MEM : egale a, ou au moins.</summary>
public sealed record ConditionValeur(long Adresse, int? Egal, int? AuMoins)
{
    public bool Vraie(int valeur) => (Egal is not { } e || valeur == e) && (AuMoins is not { } m || valeur >= m);
}

/// <summary>
/// Une borne de chrono : un evenement, ou l'un de plusieurs (« any_of »), avec ses conditions « when » ; ou
/// un renvoi a l'arrivee du reglement (« finish » : l'arrivee provisoire ; « finish_confirm » : sa
/// confirmation). Sonic quitte un acte par la fin d'acte ou par le Big Ring : deux issues, une borne.
/// </summary>
public sealed record BorneDeChrono(IReadOnlyList<CibleEvenement> Evenements, IReadOnlyList<ConditionValeur> Quand, string? Renvoi)
{
    public CibleEvenement? Evenement => Evenements.Count > 0 ? Evenements[0] : null;
}

public sealed record DefinitionChrono(BorneDeChrono Depart, BorneDeChrono Arrivee);

/// <summary>Une plage retiree des deux chronos (Special Stage du chrono de monde « hors Special Stages », D25).</summary>
public sealed record Exclusion(CibleEvenement De, CibleEvenement A);

/// <summary>
/// Un compteur d'evenements de la tentative ; au-dela de <c>Maximum</c>, elle echoue. LES COUPS RECUS (Metal Slug 3 facilite,
/// 03/11) : <c>GraceFrames</c>, un evenement moins de N images apres le dernier compte ne compte pas (un contact prolonge
/// pose le drapeau du coup plusieurs images de suite) ; <c>PenaliteFrames</c>, chaque evenement compte ajoute N images au
/// temps officiel.
/// </summary>
public sealed record Compteur(string Id, CibleEvenement Evenement, int? Maximum, int? GraceFrames = null, int? PenaliteFrames = null);

public sealed record ValeurFinale(string Id, long Adresse, int? Egal, int? AuMoins);

/// <summary>Les valeurs qu'une adresse doit prendre dans l'ordre (les actes d'un monde).</summary>
public sealed record Etape(long Adresse, IReadOnlyList<int> Suite);

/// <summary>Une ligne du temps affiche par le jeu : minute, second, centisecond ou frame.</summary>
public sealed record LigneTempsDuJeu(long Adresse, string Unite);

public sealed record CritereDeRang(string Mesure, bool PlusPetitMeilleur);

public sealed class ReglementInvalideException(string message) : Exception(message);

/// <summary>L'etat de depart d'un defi (« state ») : son nom, l'empreinte SHA-256 du fichier, le fichier sous resources/challenges/states.</summary>
public sealed record EtatDeDepart(string Nom, string Sha256, string Fichier);

/// <summary>
/// UNE VALEUR TENUE PENDANT TOUTE LA PARTIE (« locks », D18 du CDC v1.4). Le wrapper 0.343 la pose avant chaque image ;
/// <see cref="Ligne"/> est la ligne qu'il lit dans certified.txt et qu'il rend telle quelle a l'attestation et au bilan.
/// Mode : « set_bits » (OU), « clear_bits » (ET NON) ou « value » (valeur entiere), sur un octet.
/// </summary>
public sealed record VerrouDeDefi(long Adresse, string Mode, int Valeur, string Raison)
{
    public string Ligne => $"0x{Adresse:X},1,{Mode},0x{Valeur:X2}";
}

/// <summary>
/// LE REGLEMENT D'UN DEFI, schema nelfe.challenge.v2 (prototype Sonic, §9.2). Il garde les champs de la v1
/// du pilote (kind, start_policy, mem_sha256, start, finish, fail, void) et ajoute ce que la grille Sonic
/// demande : les deux chronos et leurs bornes, la confirmation de l'arrivee, les plages exclues, les
/// compteurs, les valeurs lues a l'arrivee, les etapes ordonnees et le rang. Il ne cite que des lignes du
/// .MEM (D7) : <see cref="VerifierContre"/> le prouve avant tout lancement.
/// </summary>
public sealed class Reglement
{
    public const string ChronoCale = "game_timer_aligned";
    public const string ChronoIndependant = "independent";

    public string ChallengeId { get; private init; } = "";
    public int Version { get; private init; }
    public string Ruleset { get; private init; } = "";

    /// <summary>Le nom de l'epreuve par langue (« fr », « en ») ; « title » texte simple = toutes les langues.</summary>
    public IReadOnlyDictionary<string, string> Titres { get; private init; } = new Dictionary<string, string>();

    /// <summary>Le nom dans cette langue, sinon en anglais, sinon le premier, sinon l'identifiant.</summary>
    public string Titre(string? langue)
    {
        var court = (langue ?? "").Split('_', '-')[0].ToLowerInvariant();
        return Titres.TryGetValue(court, out var t) ? t
            : Titres.TryGetValue("en", out var en) ? en
            : Titres.Values.FirstOrDefault() ?? ChallengeId;
    }
    /// <summary>
    /// Le resume du defi par langue (« summary », facultatif) : ce que le panneau dit du defi sous son nom. Le genre ne suffit
    /// pas a le dire pour un challenge (« Mission 1 jusqu'a Huge Hermit, au couteau, sans mourir »).
    /// </summary>
    public IReadOnlyDictionary<string, string> Resumes { get; private init; } = new Dictionary<string, string>();

    /// <summary>Le resume dans cette langue, sinon en anglais, sinon le premier ; null sans resume.</summary>
    public string? Resume(string? langue)
    {
        var court = (langue ?? "").Split('_', '-')[0].ToLowerInvariant();
        return Resumes.TryGetValue(court, out var r) ? r
            : Resumes.TryGetValue("en", out var en) ? en
            : Resumes.Values.FirstOrDefault();
    }

    public string Kind { get; private init; } = "";

    /// <summary>Un challenge (regles imposees), non certifie (decision du user du 03/11) ; un speedrun l'est.</summary>
    public bool EstUnChallenge => !Kind.StartsWith("speedrun", StringComparison.Ordinal);

    /// <summary>
    /// Le .MEM que le lancement de ce defi fait charger : celui de la couche des challenges s'il en a un, sinon celui du World
    /// Scoring. Un speedrun reste toujours sur le .MEM certifie du jeu.
    /// </summary>
    public string DefinitionAuLancement()
        => EstUnChallenge && RetroBat.Domain.Paths.RamDefinitionLayers.DefinitionDeChallenge(SystemId, RomGroup) is { } defi
            ? defi : RetroBat.Domain.Paths.RamDefinitionLayers.PickDefinitionFile(SystemId, RomGroup);
    public string SystemId { get; private init; } = "";
    public string RomGroup { get; private init; } = "";
    public string? MemSha256 { get; private init; }
    public string StartPolicy { get; private init; } = "normal_boot";

    /// <summary>L'etat charge au lancement ; null pour un demarrage normal.</summary>
    public EtatDeDepart? Etat { get; private init; }

    /// <summary>
    /// La place du defi dans son groupe du panneau (« order », facultatif) : les niveaux de Sonic dans l'ordre du jeu. Sans
    /// elle, 0 : Green Hill 1 v2, depose avant ce champ et immuable, reste ainsi en tete de ses niveaux (2 a 19).
    /// </summary>
    public int Ordre { get; private init; }

    /// <summary>Les valeurs tenues pendant toute la partie, dans l'ordre du reglement ; vide pour un defi sans verrou.</summary>
    public IReadOnlyList<VerrouDeDefi> Verrous { get; private init; } = [];

    /// <summary>Les verrous tels que le wrapper les rend (« locks » de l'attestation et du bilan) : « » sans verrou.</summary>
    public string LignesDesVerrous => string.Join(";", Verrous.Select(v => v.Ligne));
    public IReadOnlyList<ConditionValeur> Depart { get; private init; } = [];
    public IReadOnlyList<CibleEvenement> Arrivee { get; private init; } = [];
    /// <summary>Les evenements qui confirment l'arrivee provisoire (« finish_confirm », un seul ou « any_of ») ; vide : aucune confirmation.</summary>
    public IReadOnlyList<CibleEvenement> Confirmations { get; private init; } = [];
    public IReadOnlyList<CibleEvenement> EffacePar { get; private init; } = [];
    public string ChronoOfficiel { get; private init; } = ChronoCale;
    public DefinitionChrono? Cale { get; private init; }
    public DefinitionChrono? Independant { get; private init; }
    public IReadOnlyList<LigneTempsDuJeu> TempsDuJeu { get; private init; } = [];
    public int PeriodeDuTimer { get; private init; } = 60;
    public IReadOnlyList<Exclusion> Exclusions { get; private init; } = [];
    public IReadOnlyList<CibleEvenement> Echecs { get; private init; } = [];
    public IReadOnlyList<string> AnnulationsBorne { get; private init; } = [];
    public IReadOnlyList<CibleEvenement> AnnulationsJeu { get; private init; } = [];
    public IReadOnlyList<Compteur> Compteurs { get; private init; } = [];
    public IReadOnlyList<ValeurFinale> ValeursFinales { get; private init; } = [];
    public IReadOnlyList<Etape> Etapes { get; private init; } = [];
    public IReadOnlyList<CritereDeRang> Rang { get; private init; } = [new("frames", true)];
    public IReadOnlyList<string> Coeurs { get; private init; } = [];

    /// <summary>La cadence reelle du coeur, pour afficher le temps (D30) et juger l'ecart horloge/frames.</summary>
    public double Cadence { get; private init; } = 60;

    /// <summary>L'ecart tolere entre l'horloge de la borne et les frames avant de conclure a une pause (§5.5 du CDC v1.4).</summary>
    public int TolerancePauseMs { get; private init; } = 1000;

    /// <summary>
    /// LE BLOC DE MEMOIRE QUE LE COEUR EXPOSE (« runtime.core_ram », facultatif) : la ou READ_CORE_RAM lit une adresse du .MEM.
    /// Sans lui, l'adresse telle quelle (Mega Drive). FBNeo expose la RAM 68000 du Neo-Geo a partir de 0x100000 en mots de
    /// l'hote : « base » 0x100000, « swap16 » vrai (labo du 01/11, RetroAchievements coupe comme au lancement d'un defi).
    /// </summary>
    public long? BaseDuCoeur { get; private init; }

    public bool MotsInversesDuCoeur { get; private init; }

    /// <summary>L'offset a demander a READ_CORE_RAM pour cette adresse du .MEM.</summary>
    public long OffsetDansLeCoeur(long adresse) => BaseDuCoeur is { } b ? (adresse - b) ^ (MotsInversesDuCoeur ? 1L : 0L) : adresse;

    public DefinitionChrono? Officiel => ChronoOfficiel == ChronoIndependant ? Independant : Cale;

    public static Reglement Lire(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ReglementInvalideException("JSON illisible : " + ex.Message); }
        using (doc)
        {
            var r = doc.RootElement;
            var schema = Texte(r, "schema");
            if (schema is not ("nelfe.challenge.v1" or "nelfe.challenge.v2"))
                throw new ReglementInvalideException($"schema inconnu : {schema ?? "absent"}");

            var timing = r.TryGetProperty("timing", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            var aTiming = timing.ValueKind == JsonValueKind.Object;
            var officiel = aTiming ? Texte(timing, "official") ?? ChronoCale : ChronoCale;
            if (officiel is not (ChronoCale or ChronoIndependant))
                throw new ReglementInvalideException($"chrono officiel inconnu : {officiel}");
            var cale = aTiming && timing.TryGetProperty(ChronoCale, out var c) ? Chrono(c) : null;
            var independant = aTiming && timing.TryGetProperty(ChronoIndependant, out var i) ? Chrono(i) : null;
            var chronoOfficiel = (officiel == ChronoIndependant ? independant : cale)
                ?? throw new ReglementInvalideException($"le chrono officiel ({officiel}) n'a pas de bornes");

            // Sans « finish », l'arrivee provisoire est l'arrivee du chrono officiel quand c'est un evenement.
            var arrivee = Liste(r, "finish").Select(EvenementDe).ToList();
            if (arrivee.Count == 0)
            {
                arrivee = chronoOfficiel.Arrivee.Evenements.Count > 0
                    ? chronoOfficiel.Arrivee.Evenements.ToList()
                    : throw new ReglementInvalideException("aucune arrivee : ni « finish » ni evenement d'arrivee du chrono officiel");
            }

            long? baseDuCoeur = null;
            var motsInverses = false;
            if (r.TryGetProperty("runtime", out var rtc) && rtc.TryGetProperty("core_ram", out var cr) && cr.ValueKind == JsonValueKind.Object)
            {
                if (cr.TryGetProperty("base", out var cb)) baseDuCoeur = Nombre(cb);
                motsInverses = cr.TryGetProperty("swap16", out var sw) && sw.ValueKind == JsonValueKind.True;
            }

            var reglement = new Reglement
            {
                BaseDuCoeur = baseDuCoeur,
                MotsInversesDuCoeur = motsInverses,
                ChallengeId = Texte(r, "challenge_id") ?? throw new ReglementInvalideException("challenge_id absent"),
                Version = r.TryGetProperty("version", out var v) ? v.GetInt32() : throw new ReglementInvalideException("version absente"),
                Ruleset = Texte(r, "ruleset") ?? "",
                Titres = LireTitres(r),
                Resumes = LireTitres(r, "summary"),
                Etat = LireEtat(r),
                Kind = Texte(r, "kind") ?? "",
                SystemId = r.TryGetProperty("game", out var g) ? Texte(g, "system_id") ?? "" : "",
                RomGroup = r.TryGetProperty("game", out var g2) ? Texte(g2, "rom_group") ?? "" : "",
                MemSha256 = Texte(r, "mem_sha256"),
                StartPolicy = Texte(r, "start_policy") ?? "normal_boot",
                Depart = Liste(r, "start").Select(ConditionDeDepart).ToList(),
                Arrivee = arrivee,
                Confirmations = r.TryGetProperty("finish_confirm", out var fc) ? Evenements(fc) : [],
                EffacePar = r.TryGetProperty("finish_confirm", out var fc2) ? Liste(fc2, "cleared_by").Select(EvenementDe).ToList() : [],
                ChronoOfficiel = officiel,
                Cale = cale,
                Independant = independant,
                TempsDuJeu = Liste(timing, "game_time").Select(e => new LigneTempsDuJeu(Adresse(e), Texte(e, "unit") ?? "second")).ToList(),
                PeriodeDuTimer = aTiming && timing.TryGetProperty("game_timer_period_frames", out var p) ? p.GetInt32() : 60,
                Exclusions = Liste(timing, "exclude").Select(e => new Exclusion(EvenementDe(Propriete(e, "from")), EvenementDe(Propriete(e, "to")))).ToList(),
                Echecs = Liste(r, "fail").Select(EvenementDe).ToList(),
                AnnulationsBorne = Liste(r, "void").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
                AnnulationsJeu = Liste(r, "void").Where(e => e.ValueKind == JsonValueKind.Object).Select(EvenementDe).ToList(),
                Compteurs = Liste(r, "counters").Select(e => new Compteur(
                    Texte(e, "id") ?? throw new ReglementInvalideException("compteur sans id"),
                    EvenementDe(e), Entier(e, "max"), Entier(e, "grace_frames"), Entier(e, "penalty_frames"))).ToList(),
                ValeursFinales = Liste(r, "finish_values").Select(e => new ValeurFinale(
                    Texte(e, "id") ?? throw new ReglementInvalideException("valeur finale sans id"),
                    Adresse(e), Hexa(e, "equals"), Hexa(e, "at_least"))).ToList(),
                Etapes = Liste(r, "milestones").Select(e => new Etape(
                    Adresse(e), Liste(e, "sequence").Select(s => (int)Nombre(s)).ToList())).ToList(),
                Rang = LireRang(r),
                Coeurs = Liste(r, "cores").Select(e => e.GetString() ?? "").ToList(),
                Cadence = r.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("frame_rate", out var fr) ? fr.GetDouble()
                    : r.TryGetProperty("runtime", out var rt2) && rt2.TryGetProperty("refresh_hz", out var hz) ? hz.GetDouble() : 60,
                TolerancePauseMs = r.TryGetProperty("runtime", out var rt3) && rt3.TryGetProperty("pause_tolerance_ms", out var tol) ? tol.GetInt32() : 1000,
                Verrous = Liste(r, "locks").Select(LireVerrou).ToList(),
                Ordre = Entier(r, "order") ?? 0,
            };
            reglement.ControlerLesVerrous();
            return reglement;
        }
    }

    /// <summary>
    /// LES LIMITES D'UN VERROU (D18). Il part de l'etat de depart, seul instant que le wrapper connait : un defi a verrous part
    /// donc sur un etat. Il ne tient jamais une adresse que le reglement lit (depart, arrivee, echec, annulation, chrono) : le
    /// verrou deciderait alors du resultat qu'il est cense laisser au joueur.
    /// </summary>
    private void ControlerLesVerrous()
    {
        if (Verrous.Count == 0) return;
        if (StartPolicy == "normal_boot" || Etat is null)
            throw new ReglementInvalideException("un defi a verrous part d'un etat (start_policy canonical_state et « state »)");
        var lues = AdressesCitees().ToHashSet();
        foreach (var v in Verrous)
        {
            if (lues.Contains(v.Adresse))
                throw new ReglementInvalideException($"verrou sur {Hex.Ecrire(v.Adresse)} : le reglement lit cette adresse");
        }

        if (Verrous.Select(v => v.Adresse).Distinct().Count() != Verrous.Count)
            throw new ReglementInvalideException("deux verrous sur la meme adresse");
    }

    private static VerrouDeDefi LireVerrou(JsonElement e)
    {
        var type = Texte(e, "type") ?? "u8";
        if (type != "u8") throw new ReglementInvalideException($"verrou de type {type} : seul « u8 » est tenu (wrapper 0.343)");
        var depuis = Texte(e, "from") ?? "attempt_start";
        if (depuis != "attempt_start") throw new ReglementInvalideException($"verrou depuis « {depuis} » : seul « attempt_start » existe");
        if (Entier(e, "player") is { } joueur && joueur != 1) throw new ReglementInvalideException($"verrou du joueur {joueur} : seul le joueur 1 existe");
        var raison = Texte(e, "reason") ?? "";
        if (raison is not ("constraint" or "advantage" or "event"))
            throw new ReglementInvalideException($"verrou sans raison connue ({raison}) : constraint, advantage ou event");
        var modes = new[] { "set_bits", "clear_bits", "value" }.Where(m => e.TryGetProperty(m, out _)).ToList();
        if (modes.Count != 1) throw new ReglementInvalideException("un verrou porte exactement un de set_bits, clear_bits, value");
        var valeur = Nombre(e.GetProperty(modes[0]));
        if (valeur is < 0 or > 0xFF) throw new ReglementInvalideException($"verrou : valeur {valeur} hors d'un octet");
        if (modes[0] != "value" && valeur == 0) throw new ReglementInvalideException("verrou de bits sans aucun bit");
        return new VerrouDeDefi(Adresse(e), modes[0], (int)valeur, raison);
    }

    /// <summary>
    /// LE REGLEMENT NE CITE QUE DES LIGNES QUI PARLENT (CDC v1.4, 3.2 et 3.3). Rend la liste des ecarts, vide
    /// si le reglement est conforme au .MEM fige : empreinte, lignes citees presentes et emises, adresses lues.
    /// </summary>
    public IReadOnlyList<string> VerifierContre(byte[] mem)
    {
        var ecarts = new List<string>();
        if (MemSha256 is { Length: > 0 } attendu)
        {
            var reel = Convert.ToHexString(SHA256.HashData(mem)).ToLowerInvariant();
            if (!reel.Equals(attendu, StringComparison.OrdinalIgnoreCase))
                ecarts.Add($"empreinte du .MEM {reel} au lieu de {attendu}");
        }

        var lignes = LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(mem));
        foreach (var (role, cible) in Cibles()) VerifierCible(lignes, role, cible, ecarts);
        foreach (var (role, adresse) in AdressesLues())
        {
            if (!lignes.Lit(adresse)) ecarts.Add($"{role} : l'adresse {Hex.Ecrire(adresse)} n'est lue par aucune ligne du .MEM");
        }

        foreach (var etape in Etapes)
        {
            var emises = lignes.Emises(etape.Adresse).ToList();
            var parChange = emises.Any(l => l.Condition == "change");
            foreach (var valeur in etape.Suite)
            {
                if (!parChange && !emises.Any(l => l.Condition == "eq" && l.Valeur == valeur))
                    ecarts.Add($"etape {Hex.Ecrire(etape.Adresse)} = {Hex.Ecrire(valeur)} : aucune ligne n'emet cette valeur");
            }
        }

        return ecarts;
    }

    private static void VerifierCible(LignesDuMem lignes, string role, CibleEvenement cible, List<string> ecarts)
    {
        var candidates = lignes.Lignes.Where(l =>
            (cible.Adresse is not { } a || l.Adresse == a)
            && (cible.Action is not { } act || l.Action.Equals(act, StringComparison.OrdinalIgnoreCase))).ToList();
        var nom = $"{role} ({cible.Action ?? "?"} {(cible.Adresse is { } ad ? Hex.Ecrire(ad) : "")})".Replace(" )", ")");
        if (candidates.Count == 0)
        {
            ecarts.Add($"{nom} : aucune ligne du .MEM ne correspond");
            return;
        }

        var emises = candidates.Where(l => !l.Muette).ToList();
        if (emises.Count == 0)
        {
            ecarts.Add($"{nom} : la ligne est no_log ou no_survey, le wrapper ne l'emet jamais");
            return;
        }

        if (cible.Valeur is { } valeur && !emises.Any(l => Peut(l, valeur)))
            ecarts.Add($"{nom} : aucune ligne n'emet la valeur {Hex.Ecrire(valeur)}");
    }

    /// <summary>Une ligne peut-elle emettre cette valeur ? « eq » la sienne, les autres conditions toute valeur.</summary>
    private static bool Peut(LigneDuMem ligne, int valeur) => ligne.Condition switch
    {
        "eq" => ligne.Valeur == valeur,
        "neq" => ligne.Valeur != valeur,
        _ => true,
    };

    private IEnumerable<(string Role, CibleEvenement Cible)> Cibles()
    {
        foreach (var c in Arrivee) yield return ("finish", c);
        foreach (var c in Confirmations) yield return ("finish_confirm", c);
        foreach (var c in EffacePar) yield return ("cleared_by", c);
        foreach (var (nom, chrono) in new[] { (ChronoCale, Cale), (ChronoIndependant, Independant) })
        {
            if (chrono is null) continue;
            foreach (var d in chrono.Depart.Evenements) yield return ($"{nom}.start", d);
            foreach (var f in chrono.Arrivee.Evenements) yield return ($"{nom}.finish", f);
        }

        foreach (var x in Exclusions) { yield return ("exclude.from", x.De); yield return ("exclude.to", x.A); }
        foreach (var c in Echecs) yield return ("fail", c);
        foreach (var c in AnnulationsJeu) yield return ("void", c);
        foreach (var c in Compteurs) yield return ($"counter {c.Id}", c.Evenement);
    }

    private IEnumerable<(string Role, long Adresse)> AdressesLues()
    {
        foreach (var c in Depart) yield return ("start", c.Adresse);
        foreach (var chrono in new[] { Cale, Independant })
        {
            if (chrono is null) continue;
            foreach (var q in chrono.Depart.Quand.Concat(chrono.Arrivee.Quand)) yield return ("when", q.Adresse);
        }

        foreach (var l in TempsDuJeu) yield return ("game_time", l.Adresse);
        foreach (var v in ValeursFinales) yield return ($"finish_value {v.Id}", v.Adresse);
        foreach (var e in Etapes) yield return ("milestone", e.Adresse);
    }

    /// <summary>Toutes les adresses dont l'evaluateur doit connaitre la valeur : celles du depart et des conditions des bornes.</summary>
    /// <summary>
    /// Les adresses a connaitre avant le depart : conditions d'armement, conditions « when » des departs, et l'adresse d'un
    /// evenement de depart qui porte un « leaves ». Metal Slug 3 part quand le vehicule passe de 00 a 01 : ce 00 ne change pas
    /// depuis l'allumage, aucun evenement ne le dit, il se releve.
    /// </summary>
    public IEnumerable<long> AdressesDeDepart()
        => Depart.Select(c => c.Adresse)
            .Concat(new[] { Cale, Independant }.Where(c => c is not null).SelectMany(c => c!.Depart.Quand).Select(q => q.Adresse))
            .Concat(new[] { Cale, Independant }.Where(c => c is not null).SelectMany(c => c!.Depart.Evenements)
                .Where(e => e.Quitte is not null && e.Adresse is not null).Select(e => e.Adresse!.Value))
            .Distinct();

    /// <summary>Toutes les adresses citees, pour reconnaitre un capteur coupe par le wrapper.</summary>
    public IEnumerable<long> AdressesCitees()
        => Cibles().Select(c => c.Cible.Adresse).OfType<long>().Concat(AdressesLues().Select(a => a.Adresse)).Distinct();

    /// <summary>Les adresses des evenements cites sous cette action : le wrapper nomme une ligne coupee par son action, pas son adresse.</summary>
    public IEnumerable<long> AdressesDeLAction(string action)
        => Cibles().Where(c => string.Equals(c.Cible.Action, action, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Cible.Adresse).OfType<long>().Distinct();

    private static DefinitionChrono Chrono(JsonElement e)
        => new(Borne(Propriete(e, "start")), Borne(Propriete(e, "finish")));

    private static BorneDeChrono Borne(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var renvoi = e.GetString();
            if (renvoi is not ("finish" or "finish_confirm"))
                throw new ReglementInvalideException($"borne de chrono inconnue : {renvoi}");
            return new BorneDeChrono([], [], renvoi);
        }

        return new BorneDeChrono(Evenements(e), Liste(e, "when").Select(ConditionDeDepart).ToList(), null);
    }

    /// <summary>« any_of » : une liste d'evenements dont un seul suffit ; sinon l'evenement de l'objet.</summary>
    private static List<CibleEvenement> Evenements(JsonElement e)
    {
        if (!e.TryGetProperty("any_of", out _)) return [EvenementDe(e)];
        var evenements = Liste(e, "any_of").Select(EvenementDe).ToList();
        return evenements.Count > 0 ? evenements : throw new ReglementInvalideException("« any_of » vide");
    }

    /// <summary>
    /// Une condition de depart : « value » directement ; un « event » de la v1 vaut la derniere valeur connue
    /// de son adresse (« GAME_PLAYING 0xF601 = 0x0C » : 0xF601 vaut 0x0C).
    /// </summary>
    private static ConditionValeur ConditionDeDepart(JsonElement e)
    {
        if (e.TryGetProperty("value", out var v))
            return new ConditionValeur(Adresse(v), Hexa(v, "equals"), Hexa(v, "at_least"));
        var ev = EvenementDe(e);
        if (ev.Adresse is not { } a || ev.Valeur is not { } val)
            throw new ReglementInvalideException("une condition de depart « event » doit porter une adresse et une valeur");
        return new ConditionValeur(a, val, null);
    }

    private static CibleEvenement EvenementDe(JsonElement e)
    {
        var ev = e.TryGetProperty("event", out var inner) ? inner : e;
        var cible = new CibleEvenement(
            Texte(ev, "action"),
            ev.TryGetProperty("address", out var a) ? Nombre(a) : null,
            Hexa(ev, "value") ?? Hexa(ev, "to"),
            Hexa(ev, "leaves"));
        if (cible.Action is null && cible.Adresse is null)
            throw new ReglementInvalideException("un evenement doit porter une action ou une adresse");
        return cible;
    }

    private static IReadOnlyList<CritereDeRang> LireRang(JsonElement r)
    {
        if (r.TryGetProperty("ranking", out var rk))
        {
            var criteres = new List<CritereDeRang> { new(Texte(rk, "metric") ?? "frames", Sens(rk)) };
            criteres.AddRange(Liste(rk, "then").Select(e => new CritereDeRang(Texte(e, "metric") ?? "frames", Sens(e))));
            return criteres;
        }

        // v1 : « metric » : { « kind », « direction » }.
        if (r.TryGetProperty("metric", out var m) && m.ValueKind == JsonValueKind.Object)
            return [new CritereDeRang(Texte(m, "kind") ?? "frames", Sens(m))];
        return [new CritereDeRang("frames", true)];
    }

    private static bool Sens(JsonElement e) => Texte(e, "direction") switch
    {
        null or "lower_better" => true,
        "higher_better" => false,
        var autre => throw new ReglementInvalideException($"sens de classement inconnu : {autre}"),
    };

    private static JsonElement Propriete(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var p) ? p : throw new ReglementInvalideException($"« {nom} » absent");

    private static IEnumerable<JsonElement> Liste(JsonElement e, string nom)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().ToList()
            : [];

    private static EtatDeDepart? LireEtat(JsonElement r)
    {
        if (!r.TryGetProperty("state", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        var nom = Texte(e, "name");
        var sha = Texte(e, "sha256");
        var fichier = Texte(e, "file");
        return nom is { Length: > 0 } && sha is { Length: 64 } && fichier is { Length: > 0 } ? new EtatDeDepart(nom, sha.ToLowerInvariant(), fichier) : null;
    }

    private static Dictionary<string, string> LireTitres(JsonElement r, string champ = "title")
    {
        var titres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!r.TryGetProperty(champ, out var t)) return titres;
        if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } seul) titres["*"] = seul;
        if (t.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in t.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) titres[p.Name.ToLowerInvariant()] = v;
            }
        }

        return titres;
    }

    private static string? Texte(JsonElement e, string nom)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Entier(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var p) ? (int)Nombre(p) : null;

    private static int? Hexa(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var p) ? (int)Nombre(p) : null;

    private static long Adresse(JsonElement e) => Nombre(Propriete(e, "address"));

    private static long Nombre(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt64(),
        JsonValueKind.String => Hex.Lire(e.GetString()!),
        _ => throw new ReglementInvalideException($"nombre attendu, {e.ValueKind} trouve"),
    };
}
