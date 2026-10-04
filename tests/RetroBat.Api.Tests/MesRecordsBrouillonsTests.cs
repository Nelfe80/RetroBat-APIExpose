using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// MES RECORDS montre tout de suite une partie gardee sur la borne (piste B, 2026-10-04), marquee
/// « en attente d'envoi » ; une fois jugee, c'est sa version du dossier certified qui compte.
/// </summary>
public sealed class MesRecordsBrouillonsTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "nelfe-mesrecords-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    private string Certifies => Path.Combine(_racine, "certified");
    private string Brouillons => Path.Combine(_racine, "brouillons");

    private void Brouillon(string id, long pic, string regle = "1cc")
    {
        Directory.CreateDirectory(Brouillons);
        File.WriteAllText(Path.Combine(Brouillons, id + ".json"),
            "{\"schema\":\"nelfe.scoring.brouillon.v1\",\"id\":\"" + id + "\",\"genre\":\"solo\",\"regle\":\"" + regle + "\","
            + "\"fin_le\":\"2026-10-04T09:30:00.000Z\",\"rom_group\":\"1942\",\"pic\":" + pic + ","
            + "\"session\":\"{\\\"monotonic_ms\\\":120000}\",\"labo\":false}");
    }

    private void Certifie(string id, long score)
    {
        Directory.CreateDirectory(Certifies);
        File.WriteAllText(Path.Combine(Certifies, id + ".json"),
            "{\"session_id\":\"" + id + "\",\"verdict\":\"published\",\"passport\":{\"game\":{\"rom_group\":\"1942\",\"ruleset\":\"1cc\"},"
            + "\"metric\":{\"value\":\"" + score + "\"},\"timing\":{\"started_at\":\"2026-10-04T09:28:00Z\",\"ended_at\":\"2026-10-04T09:30:00Z\"},"
            + "\"context\":{\"world\":\"home\"},\"identity\":{}}}");
    }

    private IReadOnlyList<LeaderboardClient.Ligne> Lignes()
        => LocalPlaysIndex.MesParties(new LocalPlaysIndex(Certifies).Toutes(), "1942", "1cc", "",
            Array.Empty<LocalPlaysIndex.ReplayLocal>(), utc => utc.ToString("dd/MM HH:mm"), "en attente d'envoi");

    [Fact]
    public void Une_partie_gardee_sur_la_borne_apparait_tout_de_suite()
    {
        Certifie("ancienne", 30000);
        Brouillon("nouvelle", 45000);

        var lignes = Lignes();

        Assert.Equal(new long[] { 45000, 30000 }, lignes.Select(l => l.Valeur));
        Assert.EndsWith("(en attente d'envoi)", lignes[0].Joueur);
        Assert.DoesNotContain("attente", lignes[1].Joueur);
    }

    [Fact]
    public void Une_fois_jugee_la_partie_ne_compte_qu_une_fois()
    {
        Brouillon("partie", 45000);
        Certifie("partie", 45000);

        var lignes = Lignes();

        Assert.Single(lignes);
        Assert.DoesNotContain("attente", lignes[0].Joueur);
    }
}
