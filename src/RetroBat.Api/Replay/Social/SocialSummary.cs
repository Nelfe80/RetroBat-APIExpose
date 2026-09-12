using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Replay.Social;

/// <summary>
/// Le RESUME signe des reactions d'un replay, tel que la plateforme le publie : la timeline
/// legere (96 tranches de chaleur), les moments forts, et les cameos des performers.
///
/// Une seule signature couvre le tout : on la verifie comme celle d'un evenement (JCS du corps,
/// cle epinglee), et on ne garde rien d'un resume qui ne la porte pas. Le corps arrive DECODE et
/// se recanonicalise ici, exactement comme les evenements.
/// </summary>
public sealed class SocialSummary
{
    public const string Schema = "nelfe.social.summary.v1";

    public sealed record Tranche(int Reactions, int Intensite, int Spectateurs);

    public sealed record Moment(int Bin, long Frame, int Reactions, int Intensite, int Spectateurs, string Reaction);

    public sealed record Avatar(string Pseudo, string Famille, int Variation, IReadOnlyList<string> Palette, string Generateur, string? Planche);

    /// <summary>Un performer qui vient reagir en personne, a la frame exacte de sa reaction.</summary>
    public sealed record Cameo(long Frame, string Reaction, int Niveau, string Nom, string Poignee, int Rang,
        int Premieres, int Podiums, int Scores, Avatar Avatar);

    public required string TargetId { get; init; }
    public required string ObjectSha256 { get; init; }
    public required long ReplayEnd { get; init; }
    public required double Fps { get; init; }
    public required IReadOnlyList<Tranche> Chaleur { get; init; }
    public required IReadOnlyList<Moment> Moments { get; init; }
    public required IReadOnlyList<Cameo> Cameos { get; init; }
    public required int TotalReactions { get; init; }
    public required int TotalSpectateurs { get; init; }
    public required string SummaryId { get; init; }

    /// <summary>Lit un corps deja verifie. Null si la forme n'est pas celle attendue.</summary>
    public static SocialSummary? FromBody(JsonObject body, string summaryId)
    {
        try
        {
            if (Texte(body, "schema") != Schema || Texte(body, "target_type") != "replay") return null;
            var cible = Texte(body, "target_id");
            if (cible.Length == 0) return null;

            var chaleur = new List<Tranche>();
            if (body["heat"] is JsonArray heat)
            {
                foreach (var t in heat)
                {
                    if (t is not JsonArray triplet || triplet.Count < 3) return null;
                    chaleur.Add(new Tranche(Entier(triplet[0]), Entier(triplet[1]), Entier(triplet[2])));
                }
            }
            if (chaleur.Count == 0) return null;

            var moments = new List<Moment>();
            if (body["moments"] is JsonArray lm)
            {
                foreach (var m in lm)
                {
                    if (m is not JsonObject o) continue;
                    moments.Add(new Moment(Entier(o["bin"]), Long(o["frame"]), Entier(o["n"]), Entier(o["i"]),
                        Entier(o["a"]), Texte(o, "reaction")));
                }
            }

            var cameos = new List<Cameo>();
            if (body["cameos"] is JsonArray lc)
            {
                foreach (var c in lc)
                {
                    if (c is not JsonObject o || o["avatar"] is not JsonObject av) continue;
                    var honneurs = o["honors"] as JsonObject;
                    var palette = new List<string>();
                    if (av["palette"] is JsonArray pal)
                    {
                        foreach (var p in pal) if (p is JsonValue v && v.TryGetValue<string>(out var s)) palette.Add(s);
                    }
                    var planche = Texte(av, "sheet");
                    cameos.Add(new Cameo(
                        Long(o["frame"]), Texte(o, "reaction"), Math.Clamp(Entier(o["level"]), 1, 3),
                        Texte(o, "name"), Texte(o, "handle"), Entier(o["rank"]),
                        honneurs is null ? 0 : Entier(honneurs["firsts"]),
                        honneurs is null ? 0 : Entier(honneurs["podiums"]),
                        honneurs is null ? 0 : Entier(honneurs["scores"]),
                        new Avatar(Texte(av, "pseudo"), Texte(av, "family"), Entier(av["variation"]), palette,
                            Texte(av, "generator"), planche.Length == 64 ? planche : null)));
                }
            }
            cameos.Sort((a, b) => a.Frame.CompareTo(b.Frame));

            var totaux = body["totals"] as JsonObject;
            var fpsMilli = Entier(body["fps_milli"]);
            return new SocialSummary
            {
                TargetId = cible,
                ObjectSha256 = Texte(body, "object_sha256"),
                ReplayEnd = Math.Max(1, Long(body["replay_end"])),
                Fps = fpsMilli > 0 ? fpsMilli / 1000.0 : 60.0,
                Chaleur = chaleur,
                Moments = moments,
                Cameos = cameos,
                TotalReactions = totaux is null ? 0 : Entier(totaux["reactions"]),
                TotalSpectateurs = totaux is null ? 0 : Entier(totaux["spectators"]),
                SummaryId = summaryId,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifie l'enveloppe (corps, signature, cle) et rend le resume. Une chaine vide en refus veut
    /// dire « bon » ; sinon le code du refus, et le resume est null.
    /// </summary>
    public static (SocialSummary? Resume, string Refus) Verifier(JsonObject enveloppe, byte[] issuerSpkiDer, string expectedKeyId)
    {
        if (enveloppe["body"] is not JsonObject corps) return (null, "body_missing");
        var signature = Texte(enveloppe, "signature");
        if (signature.Length == 0) return (null, "signature_missing");
        var cle = Texte(corps, "issuer_key_id");
        if (expectedKeyId.Length > 0 && !string.Equals(cle, expectedKeyId, StringComparison.Ordinal)) return (null, "issuer_unknown");

        string canonique;
        try { canonique = Jcs.Canonical(corps); }
        catch (Exception) { return (null, "body_not_canonical"); }
        var octets = System.Text.Encoding.UTF8.GetBytes(canonique);
        if (!Crypto.Verify(issuerSpkiDer, octets, signature)) return (null, "signature_invalid");

        var attendu = Texte(enveloppe, "summary_id");
        var calcule = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(octets)).ToLowerInvariant();
        if (attendu.Length > 0 && !string.Equals(attendu, calcule, StringComparison.Ordinal)) return (null, "summary_id_mismatch");

        var resume = FromBody(corps, calcule);
        return resume is null ? (null, "body_invalid") : (resume, string.Empty);
    }

    private static string Texte(JsonObject o, string nom)
        => o[nom] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static int Entier(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : (n is JsonValue w && w.TryGetValue<long>(out var l) ? (int) Math.Clamp(l, int.MinValue, int.MaxValue) : 0);

    private static long Long(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0L;
}
