using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ETAT DU FILM DE REPLAYS DE CETTE BORNE (11/11), hors suite : en LECTURE seule (GET videos/state, le jeton de la borne
/// n'est jamais ecrit). NELFE_ETAT_FILMS = fichier d'identifiants (un par ligne) ; NELFE_ETAT_SORTIE = resultat (TSV).
/// </summary>
public sealed class EssaiEtatDesFilmsTests
{
    [Fact]
    public async Task L_etat_des_films_de_replays_de_la_borne()
    {
        var entree = Environment.GetEnvironmentVariable("NELFE_ETAT_FILMS");
        if (string.IsNullOrEmpty(entree) || !File.Exists(entree)) return;
        var magasin = new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance);
        typeof(NelfePlayDeviceStore).GetField("_statePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(magasin, @"E:\RetroBat\plugins\APIExpose\state\nelfeplay\device.json");
        var jeton = magasin.GetCredential();
        Assert.False(string.IsNullOrEmpty(jeton));
        using var http = new HttpClient { BaseAddress = new Uri("https://nelfeplay.com/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("X-NelfePlay-Device", jeton);
        var lignes = new List<string>();
        foreach (var id in File.ReadAllLines(entree).Select(l => l.Trim()).Where(l => l.StartsWith("rp_", StringComparison.Ordinal)))
        {
            using var r = await http.GetAsync("api/v1/agent/videos/state?replay_id=" + id);
            lignes.Add(id + "\t" + (int)r.StatusCode + "\t" + await r.Content.ReadAsStringAsync());
        }
        File.WriteAllLines(Environment.GetEnvironmentVariable("NELFE_ETAT_SORTIE") ?? Path.Combine(Path.GetTempPath(), "etat_des_films.tsv"), lignes);
    }
}
