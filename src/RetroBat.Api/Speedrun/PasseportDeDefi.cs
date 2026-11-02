using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Speedrun;

/// <summary>Le defi tel que l'index du site le publie : ce que le passeport doit citer.</summary>
public sealed record EntreeDeLIndex(string Ruleset, string DocumentSha256, string? MemSha256);

/// <summary>
/// LE PASSEPORT D'UN DEFI (prototype Sonic, §11, valide par le user le 30/10). Il part du passeport d'une partie
/// (attestation, ticket, bilan du wrapper) et y pose la tentative : le jeu cite le reglement, la metrique est le temps
/// en images, la fenetre est celle de la tentative, et le bloc « challenge » porte les deux chronos.
///
/// Aucun nombre decimal : le JCS du passeport les refuse, la cadence s'ecrit donc en millioniemes.
/// </summary>
public static class PasseportDeDefi
{
    /// <summary>Ce qui, au bilan du wrapper, ecarte tout le lancement : ses compteurs ne distinguent pas les tentatives.</summary>
    private static readonly string[] CompteursInterdits = ["cheats", "save_state_loads", "rewind", "runahead", "fast_forward", "netplay"];

    /// <summary>Le compteur qui salit ce lancement, ou null. La relance du joueur (« resets ») n'en est pas un.</summary>
    public static string? Salissure(string sessionJson, int chargementsPermis = 0)
    {
        var session = JsonNode.Parse(sessionJson) as JsonObject;
        return CompteursInterdits.FirstOrDefault(c => session?[c] is JsonValue v && v.TryGetValue<long>(out var n)
            && n > (c == "save_state_loads" ? chargementsPermis : 0));
    }

    /// <summary>
    /// LES VERROUS AU BILAN DU WRAPPER (D18). Le bilan rend les verrous qu'il a lus (« locks ») : ils doivent etre exactement
    /// ceux du reglement, aucun compris. Un verrou que le reglement ne demande pas changerait le jeu sans le dire ; un verrou
    /// demande et absent (wrapper d'avant 0.343, fichier perdu) ne tient pas sa promesse. Chaque verrou demande doit ensuite
    /// avoir ete arme au chargement de l'etat de depart (« from_frame ») et pose sur toutes les images jouees depuis, aucune
    /// manquee. Rend le motif, ou null si le bilan est conforme.
    /// </summary>
    public static string? EcartDesVerrous(string sessionJson, Reglement reglement)
    {
        var session = JsonNode.Parse(sessionJson) as JsonObject;
        var lus = session?["locks"] is JsonValue l && l.TryGetValue<string>(out var s) ? s : "";
        if (!string.Equals(lus, reglement.LignesDesVerrous, StringComparison.Ordinal))
            return $"verrous lus « {lus} » au lieu de « {reglement.LignesDesVerrous} »";
        if (reglement.Verrous.Count == 0) return null;

        var images = session?["frame_count"] is JsonValue f && f.TryGetValue<long>(out var n) ? n : -1;
        var comptes = session?["lock_frames"] as JsonArray;
        foreach (var verrou in reglement.Verrous)
        {
            var compte = comptes?.OfType<JsonObject>().FirstOrDefault(o => (string?)o["lock"] == verrou.Ligne);
            if (compte is null) return $"verrou {verrou.Ligne} absent du bilan";
            if (compte["valid"] is JsonValue v && v.TryGetValue<bool>(out var valide) && !valide) return $"verrou {verrou.Ligne} illisible par le wrapper";
            var manquees = (long?)compte["missed"] ?? -1;
            var posees = (long?)compte["applied"] ?? -1;
            var depuis = (long?)compte["from_frame"] ?? -1;
            if (depuis < 0) return $"verrou {verrou.Ligne} jamais arme (aucun etat de depart charge)";
            if (manquees != 0) return $"verrou {verrou.Ligne} manque sur {manquees} image(s)";
            if (posees != images - depuis) return $"verrou {verrou.Ligne} pose sur {posees} image(s) pour {images - depuis} jouee(s) depuis le depart";
        }

        return null;
    }

    /// <summary>
    /// Le depart sur etat pour le passeport, comme celui de MULTIPLAYER : l'empreinte de l'etat (game.start_state_sha256, que le
    /// site compare aux departs admis du defi) et le seul chargement qu'il explique. Null pour un demarrage normal.
    /// </summary>
    public static JsonObject? Depart(Reglement reglement)
        => reglement.Etat is { } etat
            ? new JsonObject { ["etat"] = etat.Sha256, ["choix"] = new JsonArray(), ["chargements_permis"] = 1 }
            : null;

    /// <summary>La tentative a garder dans le brouillon, sans rien du site : le ruleset et l'empreinte du reglement sont lus a l'envoi.</summary>
    public static JsonObject Decrire(Reglement reglement, TentativeFinie t)
    {
        var r = t.Resultat;
        var officiel = r.ChronoOfficiel == Reglement.ChronoIndependant ? r.Independant : r.Cale;
        return new JsonObject
        {
            ["debut_ms"] = t.DepartMs,
            ["fin_ms"] = t.ArriveeMs,
            ["depart"] = officiel?.Depart,
            ["arrivee"] = officiel?.Arrivee,
            ["frames"] = r.FramesOfficielles,
            ["challenge"] = new JsonObject
            {
                ["challenge_id"] = reglement.ChallengeId,
                ["version"] = reglement.Version,
                ["attempt_number"] = t.Numero,
                ["start_policy"] = reglement.StartPolicy,
                ["timing"] = new JsonObject
                {
                    ["official"] = r.ChronoOfficiel,
                    ["aligned"] = Mesure(r.Cale),
                    ["independent"] = Mesure(r.Independant),
                    ["excluded"] = new JsonArray(r.PlagesExclues
                        .Select(p => (JsonNode)new JsonObject { ["from_frame"] = p.De, ["to_frame"] = p.A }).ToArray()),
                    ["game_time_seconds"] = r.TempsDuJeuSecondes,
                    ["game_time_check"] = r.ControleTempsDuJeu,
                    ["clock_ms"] = t.ArriveeMs - t.DepartMs,
                    ["frame_rate_e6"] = (long)Math.Round(r.Cadence * 1_000_000, MidpointRounding.AwayFromZero),
                },
                ["events"] = new JsonArray(r.Journal.Select(c => (JsonNode)new JsonObject
                {
                    ["role"] = c.Role, ["action"] = c.Action, ["address"] = Hex.Ecrire(c.Adresse), ["value"] = c.Valeur, ["frame"] = c.Frame,
                }).ToArray()),
                ["splits"] = new JsonArray(),
            },
        };

        static JsonObject? Mesure(MesureChrono? m) => m is null
            ? null
            : new JsonObject { ["start_frame"] = m.Depart, ["end_frame"] = m.Arrivee, ["frames"] = m.Frames };
    }

    /// <summary>Pose la tentative sur un passeport assemble, avant sa signature.</summary>
    public static void Appliquer(JsonObject passeport, JsonObject defi, EntreeDeLIndex entree)
    {
        var bloc = defi["challenge"]!.DeepClone().AsObject();
        bloc["boards"] = new JsonArray(entree.Ruleset);
        var frames = (long?)defi["frames"] ?? 0;
        var debut = (long?)defi["debut_ms"] ?? 0;
        var fin = (long?)defi["fin_ms"] ?? 0;
        var depart = (long?)defi["depart"] ?? 0;
        var arrivee = (long?)defi["arrivee"] ?? 0;

        var jeu = passeport["game"]!.AsObject();
        jeu["ruleset"] = entree.Ruleset;
        jeu["profile_version"] = (int?)bloc["version"];
        jeu["profile_document_sha256"] = entree.DocumentSha256;

        passeport["timing"] = new JsonObject
        {
            ["started_at"] = Date(debut),
            ["ended_at"] = Date(fin),
            ["monotonic_ms"] = fin - debut,
            ["frame_count"] = arrivee - depart,
        };
        passeport["metric"] = new JsonObject
        {
            ["type"] = "time", ["unit"] = "frames", ["value"] = frames.ToString(CultureInfo.InvariantCulture),
            ["ranking_direction"] = "lower_better", ["result_source"] = "final",
        };

        var points = new JsonArray(Point("run_start", depart, 0, 0), Point("game_end", arrivee, fin - debut, frames));
        passeport["progression"] = new JsonObject
        {
            ["checkpoints"] = points,
            ["checkpoints_digest"] = Crypto.Sha256Hex(Jcs.CanonicalBytes(points)),
        };
        passeport["challenge"] = bloc;

        static JsonNode Point(string evenement, long frame, long tMs, long metrique) => new JsonObject
        {
            ["t_ms"] = tMs, ["frame"] = frame, ["metric"] = metrique.ToString(CultureInfo.InvariantCulture),
            ["event"] = evenement, ["counters"] = new JsonObject(),
        };
    }

    /// <summary>L'entree d'un defi dans l'index des defis ouverts ; null s'il n'y est pas (ferme, ou autre version).</summary>
    public static EntreeDeLIndex? Lire(string index, string defiId, int version)
    {
        using var doc = JsonDocument.Parse(index);
        if (!doc.RootElement.TryGetProperty("challenges", out var defis) || defis.ValueKind != JsonValueKind.Array) return null;
        foreach (var d in defis.EnumerateArray())
        {
            if (Texte(d, "challenge_id") == defiId && d.TryGetProperty("version", out var v) && v.GetInt32() == version
                && Texte(d, "ruleset") is { Length: > 0 } ruleset && Texte(d, "document_sha256") is { Length: > 0 } empreinte)
            {
                return new EntreeDeLIndex(ruleset, empreinte, Texte(d, "mem_sha256"));
            }
        }

        return null;

        static string? Texte(JsonElement e, string nom) => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Le verdict du site, dit en temps et non en points : la notification que le joueur lit en revenant sur ES.</summary>
    public static string TexteDuVerdict(JsonObject passeport, string statut, int? rang, string raison)
    {
        var frames = long.TryParse((string?)passeport["metric"]?["value"], NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var cadence = ((long?)passeport["challenge"]?["timing"]?["frame_rate_e6"] ?? 60_000_000) / 1_000_000.0;
        var temps = Chronometrage.Afficher(frames, cadence);
        return statut switch
        {
            "published" => rang is { } r ? $"Défi : {temps} classé, {r}{(r == 1 ? "er" : "e")}" : $"Défi : {temps} classé",
            "held" => $"Défi : {temps} signalé, non classé : {raison}",
            "quarantined" => $"Défi : {temps} enregistré, en attente de conformité",
            _ => $"Défi : {temps} non classé : {raison}",
        };
    }

    private static string Date(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
