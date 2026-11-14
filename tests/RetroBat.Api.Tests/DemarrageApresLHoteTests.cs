using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, PERF-API-01 : une verification de demarrage ne retarde plus l'ecoute d'ES. Un vrai hote .NET, les
/// services proteges comme dans Program.cs ; un service « verification » inscrit avant une sonde qui joue le role du
/// fournisseur d'ES. Les durees sont celles du journal de la borne du 14/11 a 18:15 (installeur 5,9 s, menu d'ES 1,3 s),
/// divisees par 10 pour garder le test court.
/// </summary>
public class DemarrageApresLHoteTests
{
    private readonly ITestOutputHelper _sortie;
    public DemarrageApresLHoteTests(ITestOutputHelper sortie) => _sortie = sortie;

    private static readonly TimeSpan Installeur = TimeSpan.FromMilliseconds(590);
    private static readonly TimeSpan MenuDEs = TimeSpan.FromMilliseconds(130);

    private sealed class Sonde : IHostedService
    {
        public Stopwatch Chrono { get; } = Stopwatch.StartNew();
        public TimeSpan? Ecoute { get; private set; }
        public Task StartAsync(CancellationToken ct) { Ecoute = Chrono.Elapsed; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Avant : le travail attendu dans StartAsync.</summary>
    private sealed class VerificationAttendue : IHostedService
    {
        private readonly TimeSpan _duree;
        public VerificationAttendue(TimeSpan duree) => _duree = duree;
        public async Task StartAsync(CancellationToken ct) => await Task.Delay(_duree, ct);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Apres : le meme travail, lance par ApresLeDemarrage.</summary>
    private sealed class VerificationDifferee : IHostedService
    {
        private readonly TimeSpan _duree;
        private readonly IHostApplicationLifetime _vie;
        public TaskCompletionSource Fini { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VerificationDifferee(TimeSpan duree, IHostApplicationLifetime vie) { _duree = duree; _vie = vie; }

        public Task StartAsync(CancellationToken ct)
        {
            ApresLeDemarrage.Lancer(_vie, null, "essai", async arret => { await Task.Delay(_duree, arret); Fini.TrySetResult(); });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<(TimeSpan Ecoute, IHost Hote)> Demarrer(bool differe)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var sonde = new Sonde();
        if (differe)
        {
            builder.Services.AddSingleton<IHostedService>(sp => new VerificationDifferee(MenuDEs, sp.GetRequiredService<IHostApplicationLifetime>()));
            builder.Services.AddSingleton<IHostedService>(sp => new VerificationDifferee(Installeur, sp.GetRequiredService<IHostApplicationLifetime>()));
        }
        else
        {
            builder.Services.AddSingleton<IHostedService>(_ => new VerificationAttendue(MenuDEs));
            builder.Services.AddSingleton<IHostedService>(_ => new VerificationAttendue(Installeur));
        }

        builder.Services.AddSingleton<IHostedService>(sonde);
        builder.Services.ProtegerLesServicesHeberges();
        var hote = builder.Build();
        sonde.Chrono.Restart();
        await hote.StartAsync();
        return (sonde.Ecoute!.Value, hote);
    }

    [Fact]
    public async Task L_ecoute_d_ES_n_attend_plus_les_verifications_et_elles_se_font_quand_meme()
    {
        var (avant, hoteAvant) = await Demarrer(differe: false);
        await hoteAvant.StopAsync();
        hoteAvant.Dispose();

        var (apres, hoteApres) = await Demarrer(differe: true);
        try
        {
            var differees = hoteApres.Services.GetServices<IHostedService>().Count();
            Assert.Equal(3, differees);
            _sortie.WriteLine($"ecoute d'ES (services proteges, durees du 14/11 divisees par 10) : avant {avant.TotalMilliseconds:F0} ms, apres {apres.TotalMilliseconds:F0} ms");
            Assert.True(avant >= Installeur + MenuDEs - TimeSpan.FromMilliseconds(20));
            Assert.True(apres < TimeSpan.FromMilliseconds(100), $"apres = {apres.TotalMilliseconds} ms");
        }
        finally
        {
            await hoteApres.StopAsync();
            hoteApres.Dispose();
        }
    }

    [Fact]
    public async Task Le_travail_differe_part_une_fois_l_hote_demarre_et_s_arrete_avec_lui()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var etapes = new List<string>();
        var fini = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton<IHostedService>(sp => new Differe(sp.GetRequiredService<IHostApplicationLifetime>(), etapes, fini));
        builder.Services.AddSingleton<IHostedService>(new Marqueur(etapes));
        using var hote = builder.Build();
        await hote.StartAsync();
        await fini.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await hote.StopAsync();

        Assert.Equal(new[] { "start differe", "start suivant", "travail" }, etapes);
    }

    private sealed class Differe : IHostedService
    {
        private readonly IHostApplicationLifetime _vie;
        private readonly List<string> _etapes;
        private readonly TaskCompletionSource _fini;
        public Differe(IHostApplicationLifetime vie, List<string> etapes, TaskCompletionSource fini) { _vie = vie; _etapes = etapes; _fini = fini; }

        public Task StartAsync(CancellationToken ct)
        {
            lock (_etapes) _etapes.Add("start differe");
            ApresLeDemarrage.Lancer(_vie, null, "essai", _ => { lock (_etapes) _etapes.Add("travail"); _fini.TrySetResult(); return Task.CompletedTask; });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Marqueur : IHostedService
    {
        private readonly List<string> _etapes;
        public Marqueur(List<string> etapes) => _etapes = etapes;
        public Task StartAsync(CancellationToken ct) { lock (_etapes) _etapes.Add("start suivant"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
