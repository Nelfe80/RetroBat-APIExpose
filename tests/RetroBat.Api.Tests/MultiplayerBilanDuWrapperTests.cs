using System.Text.Json;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ANNULATION SUR LE BILAN DU WRAPPER (decision du user du 22/10) : retour arriere et avance rapide interdits en
/// MULTIPLAYER, triche et chargement d'etat en trop annulent une rencontre classee.
/// </summary>
public class MultiplayerBilanDuWrapperTests
{
    private static JsonElement Bilan(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Le_depart_pret_de_l_hote_est_le_seul_chargement_permis()
    {
        var hote = Bilan("""{"save_state_loads":1,"rewind":0,"fast_forward":0,"runahead":0,"cheats":0,"netplay_rollbacks":412}""");
        Assert.Equal((false, null), MultiplayerBilanDuWrapper.Juger(hote, 1));
        // Le meme bilan chez l'invite : il n'a rien a charger, le chargement vient de lui.
        Assert.Equal((true, "save_state"), MultiplayerBilanDuWrapper.Juger(hote, 0));
    }

    [Theory]
    [InlineData("""{"cheats":1}""", "cheats")]
    [InlineData("""{"rewind":24}""", "rewind")]
    [InlineData("""{"fast_forward":3}""", "fast_forward")]
    [InlineData("""{"runahead":60}""", "runahead")]
    public void Triche_retour_arriere_avance_rapide_et_execution_anticipee_annulent(string json, string motif)
        => Assert.Equal((true, motif), MultiplayerBilanDuWrapper.Juger(Bilan(json), 1));

    [Fact]
    public void Les_corrections_du_netplay_ne_comptent_pas()
    {
        var invite = Bilan("""{"save_state_loads":0,"netplay_rollbacks":900,"netplay_saves":5000,"netplay_replayed_frames":1200}""");
        Assert.Equal((false, null), MultiplayerBilanDuWrapper.Juger(invite, 0));
    }

    [Fact]
    public async Task Le_bilan_arrive_par_le_bus_et_seulement_celui_de_la_partie()
    {
        var bus = new BusDEssai();
        using var bilans = new MultiplayerBilanDuWrapper(bus);
        var debut = DateTime.UtcNow;
        Assert.Null(await bilans.AttendreAsync(debut, TimeSpan.FromMilliseconds(250)));   // rien encore

        bus.Publier(new EventEnvelope { Type = "scoring.listener.session", Payload = new { Session = """{"cheats":2}""" } });
        var recu = await bilans.AttendreAsync(debut, TimeSpan.FromSeconds(1));
        Assert.Equal((true, "cheats"), MultiplayerBilanDuWrapper.Juger(recu!.Value, 1));
        // Un bilan d'avant la partie ne la juge pas.
        Assert.Null(await bilans.AttendreAsync(DateTime.UtcNow.AddSeconds(5), TimeSpan.FromMilliseconds(250)));
    }

    private sealed class BusDEssai : IEventBus
    {
        private readonly List<Action<EventEnvelope>> _abonnes = [];

        public void Publier(EventEnvelope e)
        {
            foreach (var abonne in _abonnes)
            {
                abonne(e);
            }
        }

        public Task PublishAsync<TEvent>(TEvent evenement) where TEvent : class
        {
            if (evenement is EventEnvelope e)
            {
                Publier(e);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler is Action<EventEnvelope> h)
            {
                _abonnes.Add(h);
            }

            return new Rien();
        }

        private sealed class Rien : IDisposable
        {
            public void Dispose() { }
        }
    }
}
