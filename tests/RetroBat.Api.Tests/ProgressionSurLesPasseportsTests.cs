using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA REGLE DE PROGRESSION DE LA BORNE, REJOUEE SUR DE VRAIS PASSEPORTS (13/11). Ne joue que si on lui donne les donnees, hors
/// git (lecture seule) :
/// <c>NELFE_PASSEPORTS_BORNE</c> = le dossier <c>state\nelfeplay\certified</c> d'une borne ;
/// <c>NELFE_GRAINE_GAINS</c> = <c>E:\claude_travail\temp\certification\graine_gains.json</c> ;
/// <c>NELFE_RECORDS_PUBLICS</c> = <c>E:\claude_travail\temp\certification\public\lignes.json</c>.
/// Aucune partie publiee, hors les cas connus de l'audit du 12/11, ne doit etre un saut impossible.
/// </summary>
public class ProgressionSurLesPasseportsTests
{
    private readonly ITestOutputHelper _sortie;

    public ProgressionSurLesPasseportsTests(ITestOutputHelper sortie) => _sortie = sortie;

    private static readonly string[] Connus = { "3d38b6a0", "91b7ed68", "f18810af" };   // Ms. Pac-Man 906 030 (lecture parasite)

    [Fact]
    public void Aucune_vraie_partie_publiee_de_la_borne_n_est_un_saut_impossible()
    {
        var dossier = Environment.GetEnvironmentVariable("NELFE_PASSEPORTS_BORNE");
        var graine = Environment.GetEnvironmentVariable("NELFE_GRAINE_GAINS");
        var publics = Environment.GetEnvironmentVariable("NELFE_RECORDS_PUBLICS");
        if (string.IsNullOrEmpty(dossier) || !Directory.Exists(dossier) || !File.Exists(graine) || !File.Exists(publics))
        {
            _sortie.WriteLine("donnees absentes : rien a rejouer");
            return;
        }
        var gains = new Dictionary<string, long>();
        foreach (var c in JsonDocument.Parse(File.ReadAllText(graine)).RootElement.GetProperty("caps").EnumerateArray())
        {
            var jeu = c.GetProperty("rom_group").GetString()!;
            gains[jeu] = Math.Max(gains.GetValueOrDefault(jeu), c.GetProperty("gain_1s").GetInt64());
        }
        var records = new Dictionary<string, List<long>>();
        foreach (var l in JsonDocument.Parse(File.ReadAllText(publics)).RootElement.EnumerateArray())
        {
            var jeu = l.GetProperty("game").GetString()!;
            if (!records.TryGetValue(jeu, out var liste)) records[jeu] = liste = new List<long>();
            liste.Add(l.GetProperty("value").ValueKind == JsonValueKind.Number ? l.GetProperty("value").GetInt64() : long.Parse(l.GetProperty("value").GetString()!));
        }

        int publies = 0, signales = 0;
        var aTort = new List<string>();
        foreach (var f in Directory.GetFiles(dossier, "*.json"))
        {
            JsonElement p;
            string verdict, session;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                verdict = doc.RootElement.GetProperty("verdict").GetString() ?? "";
                session = doc.RootElement.GetProperty("session_id").GetString() ?? "";
                p = doc.RootElement.GetProperty("passport").Clone();
            }
            catch
            {
                continue;
            }
            var regle = p.GetProperty("game").GetProperty("ruleset").GetString() ?? "";
            if (verdict != "published" || regle.StartsWith("ch-", StringComparison.Ordinal)
                || p.GetProperty("metric").GetProperty("type").GetString() != "score") continue;
            if (p.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("lab", out var lab) && lab.ValueKind == JsonValueKind.True) continue;
            publies++;
            var jeuDuPasseport = p.GetProperty("game").GetProperty("rom_group").GetString()!;
            var score = long.Parse(p.GetProperty("metric").GetProperty("value").GetString()!);
            var pas = p.GetProperty("metric").TryGetProperty("max_step", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt64() : 0;
            var images = p.GetProperty("timing").TryGetProperty("frame_count", out var fc) && fc.ValueKind == JsonValueKind.Number ? fc.GetInt64() : 0;
            var points = p.GetProperty("progression").GetProperty("checkpoints").EnumerateArray()
                .Select(c => (c.TryGetProperty("frame", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetInt64() : 0L,
                    long.Parse(c.GetProperty("metric").GetString() ?? "0"))).ToList();
            var autres = records.GetValueOrDefault(jeuDuPasseport)?.Where(v => v != score).ToList();
            var reperes = new ProgressionDuScore.Reperes(gains.TryGetValue(jeuDuPasseport, out var g) ? g : null, autres is { Count: > 0 } ? autres.Max() : null);
            if (ProgressionDuScore.Juger(points, score, pas, images, reperes) is { } raison)
            {
                signales++;
                _sortie.WriteLine($"{session[..8]} {jeuDuPasseport} {regle} {score} : {raison}");
                if (!Connus.Contains(session[..8])) aTort.Add($"{session} {jeuDuPasseport} {score}");
            }
        }
        _sortie.WriteLine($"{publies} parties de score publiees rejouees, {signales} saut(s) impossible(s), {aTort.Count} hors cas connus");
        Assert.True(publies > 0);
        Assert.Empty(aTort);
    }
}
