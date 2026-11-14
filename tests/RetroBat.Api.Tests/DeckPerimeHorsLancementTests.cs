using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, ECR-03 : le Deck d'avant une mise a jour n'est plus arrete au milieu d'un lancement (page de depart
/// ouverte depuis 3 s) ; il l'est au demarrage de l'API, ou apres la partie. Aucun processus : les coutures du service.
/// </summary>
public class DeckPerimeHorsLancementTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-deck-perime-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _actes = new();

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private DeckLaunchService Service(SimpleEventBus bus, ApiContext contexte, params DateTime[] demarrages)
    {
        var exeEcrit = new DateTime(2026, 11, 13, 20, 39, 0, DateTimeKind.Utc);
        return new DeckLaunchService(bus, contexte, fichierDesChoix: Path.Combine(_dossier, "choix.json"))
        {
            RacineDuDecor = Path.Combine(_dossier, "decorations", DeckDecor.Nom),
            DossierImpose = _dossier,
            DateDeLExe = _ => exeEcrit,
            DecksResidents = () => demarrages.Select(d => new DeckLaunchService.DeckResident(d, () => { lock (_actes) _actes.Add("arret " + d.ToString("HH:mm")); })).ToList(),
            LancerLeDeck = _ => { lock (_actes) _actes.Add("demarrage"); },
            AttenteApresLaPartie = TimeSpan.FromMilliseconds(50),
        };
    }

    private static readonly DateTime AvantLaMiseAJour = new(2026, 11, 13, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ApresLaMiseAJour = new(2026, 11, 13, 20, 40, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Pendant_un_lancement_le_Deck_perime_sert_puis_il_est_remplace_apres_la_partie()
    {
        var bus = new SimpleEventBus();
        var contexte = new ApiContext();
        var service = Service(bus, contexte, AvantLaMiseAJour);
        await service.StartAsync(CancellationToken.None);

        contexte.Ui.Running = new GameReference { SystemId = "fbneo", GamePath = "19xx.zip" };
        service.DemarrerLeDeck();   // ce que fait la preparation d'un lancement
        Assert.Empty(_actes);       // ni arret ni demarrage pendant le lancement

        contexte.Ui.Running = null;
        await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended" });
        for (var i = 0; i < 40 && _actes.Count < 2; i++) await Task.Delay(25);
        Assert.Equal(new[] { "arret 18:00", "demarrage" }, _actes);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Au_demarrage_de_l_API_le_Deck_perime_est_remplace()
    {
        var service = Service(new SimpleEventBus(), new ApiContext(), AvantLaMiseAJour);
        Assert.True(service.RemplacerLeDeckPerime("au demarrage de l'API"));
        Assert.Equal(new[] { "arret 18:00", "demarrage" }, _actes);
    }

    [Fact]
    public void Un_Deck_a_jour_n_est_ni_arrete_ni_double()
    {
        var service = Service(new SimpleEventBus(), new ApiContext(), ApresLaMiseAJour);
        Assert.False(service.RemplacerLeDeckPerime("au demarrage de l'API"));
        service.DemarrerLeDeck();
        Assert.Empty(_actes);
    }

    [Fact]
    public void Sans_Deck_resident_le_lancement_le_demarre_comme_avant()
    {
        var service = Service(new SimpleEventBus(), new ApiContext());
        Assert.False(service.RemplacerLeDeckPerime("au demarrage de l'API"));
        service.DemarrerLeDeck();
        Assert.Equal(new[] { "demarrage" }, _actes);
    }
}
