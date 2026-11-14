using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using RetroBat.Providers.RetroArchWrapper;
using Xunit;
using Xunit.Abstractions;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, PERF-API-06 : l'index d'alias d'un systeme. Mesure sur les vrais alias.json du Data Pack
/// (NELFEPLAY_DATAPACK, sinon le Data Pack voisin du depot) : chargement avant (expression reguliere) et apres,
/// et cout de la premiere selection une fois l'index prechauffe.
/// </summary>
public class IndexDAliasPrechauffageTests
{
    private readonly ITestOutputHelper _sortie;
    public IndexDAliasPrechauffageTests(ITestOutputHelper sortie) => _sortie = sortie;

    private static string? DataPack()
    {
        var env = Environment.GetEnvironmentVariable("NELFEPLAY_DATAPACK");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(Path.Combine(env, "ram"))) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var voisin = Path.Combine(d.FullName, "RetroBat-DataPack");
            if (Directory.Exists(Path.Combine(voisin, "ram"))) return voisin;
        }

        return null;
    }

    /// <summary>L'ancienne normalisation, telle qu'elle etait avant le brief.</summary>
    private static string AncienneNormalisation(string rawRom)
    {
        if (string.IsNullOrWhiteSpace(rawRom)) return string.Empty;
        var normalized = rawRom.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sonic The Hedgehog (USA, Europe)")]
    [InlineData("--a--b--")]
    [InlineData("Pokémon - Édition Rouge [!]")]
    [InlineData("Street Fighter II' - Champion Edition")]
    [InlineData("  ÆØÅ 12 _ x ")]
    [InlineData("İstanbul ﬁ ß")]
    public void Normalisation_identique_a_l_expression_reguliere(string nom)
    {
        Assert.Equal(AncienneNormalisation(nom), RetroArchWrapperProvider.NormalizeRomName(nom));
    }

    [Theory]
    [InlineData("snes")]
    [InlineData("nes")]
    [InlineData("megadrive")]
    public void Normalisation_identique_sur_toutes_les_entrees_du_Data_Pack_et_chargement_mesure(string systeme)
    {
        var dp = DataPack();
        if (dp is null) { _sortie.WriteLine("Data Pack absent : test saute."); return; }
        var fichier = Path.Combine(dp, "ram", systeme, "alias.json");
        if (!File.Exists(fichier)) { _sortie.WriteLine(fichier + " absent."); return; }

        var entrees = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(fichier))!;
        foreach (var cle in entrees.Keys)
        {
            Assert.Equal(AncienneNormalisation(cle), RetroArchWrapperProvider.NormalizeRomName(cle));
        }

        // Chargement complet (lecture, JSON, table normalisee), 3 fois chacun, meilleure valeur retenue.
        double Avant()
        {
            var chrono = Stopwatch.StartNew();
            var exact = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(fichier))!;
            var normalise = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in exact)
            {
                var c = AncienneNormalisation(e.Key);
                if (c.Length > 0 && !normalise.ContainsKey(c)) normalise[c] = e.Value;
            }

            return chrono.Elapsed.TotalMilliseconds;
        }

        double Apres()
        {
            var wrapper = new RetroArchWrapperProvider(new SimpleEventBus(), new ApiContext(), Arbitrage());
            var chrono = Stopwatch.StartNew();
            Assert.Equal(entrees.Count, wrapper.PrechaufferLeFichierDAlias(fichier));
            var charge = chrono.Elapsed.TotalMilliseconds;
            chrono.Restart();
            wrapper.PrechaufferLeFichierDAlias(fichier);   // ce que paie la premiere selection une fois l'index prechauffe
            _derniereSelection = chrono.Elapsed.TotalMilliseconds;
            return charge;
        }

        Avant(); Apres();   // compilation a la volee hors mesure
        var avant = Enumerable.Range(0, 3).Select(_ => Avant()).Min();
        var apres = Enumerable.Range(0, 3).Select(_ => Apres()).Min();
        _sortie.WriteLine($"{systeme}/alias.json, {entrees.Count} entrees : chargement avant {avant:F0} ms, apres {apres:F0} ms ; "
                          + $"premiere selection apres prechauffage {_derniereSelection:F2} ms");
    }

    private double _derniereSelection;

    [Fact]
    public async Task Le_choix_d_un_systeme_prechauffe_son_index_et_un_defilement_ne_charge_rien()
    {
        var bus = new SimpleEventBus();
        var wrapper = new RetroArchWrapperProvider(bus, new ApiContext(), Arbitrage());
        var appels = new List<string>();
        using var abonnement = bus.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type == "ui.system.selected.raw") appels.Add("vu");
        });
        // Le fournisseur n'est pas demarre : son tube appartiendrait a l'API de la machine. Seul l'abonnement est branche.
        var suivi = wrapper.SuivreLeChoixDuSysteme();
        try
        {
            foreach (var s in new[] { "nes", "snes", "megadrive" })
            {
                await bus.PublishAsync(new EventEnvelope { Type = "ui.system.selected.raw", Payload = new { Selection = new { SystemId = s } } });
                await Task.Delay(30);
            }

            await Task.Delay(RetroArchWrapperProvider.AttenteAvantPrechauffage + TimeSpan.FromMilliseconds(400));
            Assert.Equal(3, appels.Count);
            Assert.Equal(new[] { "megadrive" }, wrapper.SystemesPrechauffes);
        }
        finally
        {
            suivi.Dispose();
        }
    }

    private static RetroBat.Domain.Interfaces.IIngameSourceArbitrationService Arbitrage() =>
        new RetroBat.Api.Infrastructure.IngameSourceArbitrationService();
}
