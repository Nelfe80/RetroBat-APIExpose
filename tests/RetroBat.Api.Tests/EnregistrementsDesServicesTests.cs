using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// DEUX SERVICES D'UN MEME TYPE (26/11) : <c>AddHostedService(sp =&gt; ...)</c> passe par <c>TryAddEnumerable</c>, qui deduit le type
/// d'implementation du type rendu par la fabrique et ECARTE EN SILENCE un second service du meme type. La collection NelfePlay Adventure
/// (un second <c>MultiplayerCollectionSyncService</c>) ne demarrait ainsi jamais : statut fige a « pending », aucune ligne au journal.
/// </summary>
public class EnregistrementsDesServicesTests
{
    private class Service(string nom) : IHostedService
    {
        public string Nom { get; } = nom;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void AddHostedService_ecarte_en_silence_un_second_service_du_meme_type_et_AddSingleton_IHostedService_non()
    {
        var a = new Service("a");
        var b = new Service("b");

        var ecarte = new ServiceCollection();
        ecarte.AddHostedService(_ => a);
        ecarte.AddHostedService(_ => b);
        Assert.Equal(["a"], ecarte.BuildServiceProvider().GetServices<IHostedService>().Cast<Service>().Select(s => s.Nom).ToArray());

        var garde = new ServiceCollection();
        garde.AddSingleton<IHostedService>(_ => a);
        garde.AddSingleton<IHostedService>(_ => b);
        Assert.Equal(["a", "b"], garde.BuildServiceProvider().GetServices<IHostedService>().Cast<Service>().Select(s => s.Nom).ToArray());
    }

    private static string Programme()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "src", "RetroBat.Api", "Program.cs")))
        {
            dossier = dossier.Parent;
        }

        return dossier is null ? string.Empty : File.ReadAllText(Path.Combine(dossier.FullName, "src", "RetroBat.Api", "Program.cs"));
    }

    [Fact]
    public void Program_n_enregistre_jamais_deux_services_hotes_du_meme_type_par_AddHostedService()
    {
        var source = Programme();
        if (source.Length == 0)
        {
            return;
        }

        var types = Regex.Matches(source, @"AddHostedService(?:<([\w.]+)>\(\)|\(sp => sp\.GetRequiredService<([\w.]+)>\(\)\))")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        Assert.NotEmpty(types);
        Assert.Empty(types.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key));

        // Une fabrique qui rend autre chose que le service enregistre (une propriete, un champ) rend peut-etre le type d'un autre : refusee.
        Assert.DoesNotMatch(@"AddHostedService\(sp => sp\.GetRequiredService<[\w.]+>\(\)\.", source);
    }

    [Fact]
    public void La_collection_Adventure_est_enregistree_sans_deduplication()
    {
        var source = Programme();
        if (source.Length == 0)
        {
            return;
        }

        Assert.Contains("AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<RetroBat.Api.Adventure.AdventureCollection>().Service)", source);
        Assert.Contains("AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Multiplayer.MultiplayerCollectionSyncService>())", source);
    }
}
