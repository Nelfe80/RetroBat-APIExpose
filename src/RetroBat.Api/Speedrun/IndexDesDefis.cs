using System.Text.Json;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Speedrun;

/// <summary>Un defi de l'index du site (prototype Sonic, §11) : ce qu'il annonce avant qu'on lise son reglement.</summary>
public sealed record DefiDeLIndex(
    string ChallengeId, int Version, string Ruleset, string Schema, string Kind, string StartPolicy,
    string SystemId, string RomGroup, string MemSha256, IReadOnlyList<string> Coeurs, string DocumentSha256, string DocumentUrl);

/// <summary>L'index lu : sa revision, les defis retenus, et ce qui a ete ecarte avec la raison.</summary>
public sealed record LectureDeLIndex(string Revision, IReadOnlyList<DefiDeLIndex> Defis, IReadOnlyList<string> Ecartes);

/// <summary>
/// L'INDEX DES DEFIS OUVERTS (GET /api/v1/challenges/open, contrat du §11 du prototype Sonic), sans reseau ni disque.
/// Une reponse hors contrat n'est jamais lue comme une liste vide : tous les defis disparaitraient de la borne. Un defi
/// qu'on ne sait pas lire s'ecarte seul ; le reglement fait foi contre l'index, et son empreinte est celle des octets recus.
/// </summary>
public static class IndexDesDefis
{
    private const string PrefixeDesDocuments = "/api/v1/challenges/";

    private static readonly string[] SchemasConnus = ["nelfe.challenge.v1", "nelfe.challenge.v2"];

    /// <summary>L'index, ou null avec la raison si la reponse ne respecte pas le contrat.</summary>
    public static (LectureDeLIndex? Lecture, string? Erreur) Lire(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (null, "reponse illisible"); }

        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return (null, "reponse illisible");
            if (!r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return (null, "reponse refusee");
            if (!r.TryGetProperty("schema_version", out var sv) || !sv.TryGetInt32(out var version) || version != 1) return (null, "schema inconnu");
            if (Texte(r, "revision") is not { Length: > 0 } revision) return (null, "revision absente");
            if (!r.TryGetProperty("challenges", out var defis) || defis.ValueKind != JsonValueKind.Array) return (null, "liste de defis absente");

            var retenus = new List<DefiDeLIndex>();
            var ecartes = new List<string>();
            foreach (var d in defis.EnumerateArray())
            {
                var id = Texte(d, "challenge_id");
                var schema = Texte(d, "schema");
                if (id is not { Length: > 0 } || !d.TryGetProperty("version", out var v) || !v.TryGetInt32(out var versionDuDefi))
                {
                    ecartes.Add("entree sans identifiant ni version");
                    continue;
                }

                if (schema is null || !SchemasConnus.Contains(schema))
                {
                    ecartes.Add($"{id} : schema {schema ?? "absent"} inconnu");
                    continue;
                }

                var url = Texte(d, "document_url");
                var empreinte = Texte(d, "document_sha256");
                // Le document vient du site et de lui seul : jamais un autre hote ni un chemin qui remonte.
                if (url is null || !url.StartsWith(PrefixeDesDocuments, StringComparison.Ordinal) || url.Contains("..", StringComparison.Ordinal)
                    || empreinte is not { Length: 64 } || !empreinte.All(Uri.IsHexDigit))
                {
                    ecartes.Add($"{id} : document mal annonce");
                    continue;
                }

                retenus.Add(new DefiDeLIndex(id, versionDuDefi, Texte(d, "ruleset") ?? "", schema, Texte(d, "kind") ?? "",
                    Texte(d, "start_policy") ?? "", Texte(d, "system_id") ?? "", Texte(d, "rom_group") ?? "", Texte(d, "mem_sha256") ?? "",
                    d.TryGetProperty("cores", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                        : [],
                    empreinte.ToLowerInvariant(), url));
            }

            return (new LectureDeLIndex(revision, retenus, ecartes), null);
        }
    }

    /// <summary>Le reglement de ce defi depuis ses octets, ou la raison de l'ecarter : empreinte, lecture, ecart avec l'index.</summary>
    public static (Reglement? Reglement, string? Erreur) Controler(DefiDeLIndex defi, byte[] octets)
    {
        if (!string.Equals(Crypto.Sha256Hex(octets), defi.DocumentSha256, StringComparison.OrdinalIgnoreCase))
            return (null, "empreinte du document fausse");

        Reglement reglement;
        try { reglement = Reglement.Lire(System.Text.Encoding.UTF8.GetString(octets)); }
        catch (ReglementInvalideException ex) { return (null, ex.Message); }

        var ecart = Ecart(defi, reglement);
        return ecart is null ? (reglement, null) : (null, "l'index contredit le reglement : " + ecart);
    }

    private static string? Ecart(DefiDeLIndex d, Reglement r)
    {
        if (d.ChallengeId != r.ChallengeId) return "challenge_id";
        if (d.Version != r.Version) return "version";
        if (d.Ruleset != r.Ruleset) return "ruleset";
        if (d.Kind != r.Kind) return "kind";
        if (d.StartPolicy != r.StartPolicy) return "start_policy";
        if (d.SystemId != r.SystemId) return "system_id";
        if (d.RomGroup != r.RomGroup) return "rom_group";
        if (!string.Equals(d.MemSha256, r.MemSha256, StringComparison.OrdinalIgnoreCase)) return "mem_sha256";
        return d.Coeurs.SequenceEqual(r.Coeurs) ? null : "cores";
    }

    private static string? Texte(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
