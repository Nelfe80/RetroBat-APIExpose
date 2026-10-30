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

public sealed record Compteur(string Id, CibleEvenement Evenement, int? Maximum);

public sealed record ValeurFinale(string Id, long Adresse, int? Egal, int? AuMoins);

/// <summary>Les valeurs qu'une adresse doit prendre dans l'ordre (les actes d'un monde).</summary>
public sealed record Etape(long Adresse, IReadOnlyList<int> Suite);

/// <summary>Une ligne du temps affiche par le jeu : minute, second, centisecond ou frame.</summary>
public sealed record LigneTempsDuJeu(long Adresse, string Unite);

public sealed record CritereDeRang(string Mesure, bool PlusPetitMeilleur);

public sealed class ReglementInvalideException(string message) : Exception(message);

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
    public string Kind { get; private init; } = "";
    public string SystemId { get; private init; } = "";
    public string RomGroup { get; private init; } = "";
    public string? MemSha256 { get; private init; }
    public string StartPolicy { get; private init; } = "normal_boot";
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

            return new Reglement
            {
                ChallengeId = Texte(r, "challenge_id") ?? throw new ReglementInvalideException("challenge_id absent"),
                Version = r.TryGetProperty("version", out var v) ? v.GetInt32() : throw new ReglementInvalideException("version absente"),
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
                    EvenementDe(e), Entier(e, "max"))).ToList(),
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
            };
        }
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
    public IEnumerable<long> AdressesDeDepart()
        => Depart.Select(c => c.Adresse)
            .Concat(new[] { Cale, Independant }.Where(c => c is not null).SelectMany(c => c!.Depart.Quand).Select(q => q.Adresse))
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
