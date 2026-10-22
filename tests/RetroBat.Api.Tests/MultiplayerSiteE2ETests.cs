using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// MULTIPLAYER DE BOUT EN BOUT : le vrai client de la borne (C#) contre le vrai code du site (PHP), servi en local
/// sur une base de test, avec deux bornes amies appairees. Verifie le contrat JSON des deux cotes : appui long,
/// decompte, hote elu, session publiee et recue par l'invite seul, fin, resultat, invitation, amis en ligne.
///
/// Ne tourne que si l'essai local est monte (variables NELFEPLAY_E2E_URL, NELFEPLAY_E2E_BORNE_A,
/// NELFEPLAY_E2E_BORNE_B) ; sinon il passe sans rien faire. Jamais contre la production.
/// </summary>
public class MultiplayerSiteE2ETests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("NELFEPLAY_E2E_URL");
    private static readonly string? BorneA = Environment.GetEnvironmentVariable("NELFEPLAY_E2E_BORNE_A");
    private static readonly string? BorneB = Environment.GetEnvironmentVariable("NELFEPLAY_E2E_BORNE_B");

    private static MultiplayerClient Client(string identifiant) => new(
        new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance), journal: null,
        clientDeTest: () =>
        {
            var client = new HttpClient { BaseAddress = new Uri(Url!), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", identifiant);
            return client;
        });

    private static async Task<MultiplayerRoom?> AttendreAsync(MultiplayerClient client, string room, Func<MultiplayerRoom, bool> condition, int secondes = 20)
    {
        var limite = DateTime.UtcNow.AddSeconds(secondes);
        while (DateTime.UtcNow < limite)
        {
            var etat = await client.SonderAsync(true, true, null, CancellationToken.None);
            var vue = etat?.Rooms.FirstOrDefault(r => r.Room == room);
            if (vue is not null && condition(vue))
            {
                return vue;
            }

            await Task.Delay(500);
        }

        return null;
    }

    [Fact]
    public async Task Deux_bornes_amies_jouent_une_partie_de_bout_en_bout()
    {
        if (Url is null || BorneA is null || BorneB is null)
        {
            return;   // essai local non monte
        }

        var a = Client(BorneA);
        var b = Client(BorneB);

        // Le catalogue fait foi : un set inconnu est refuse.
        Assert.Equal("content_refused", (await a.RejoindreAsync("arcade/mslug3", "mslug3x", 80, true, CancellationToken.None))?.Reason);

        // L'appui long de A, puis celui de B : meme gameroom.
        var ra = await a.RejoindreAsync("arcade/mslug3", "mslug3", 80, true, CancellationToken.None);
        Assert.True(ra?.Ok, ra?.Reason);
        var room = ra!.Room!;
        var rb = await b.RejoindreAsync("arcade/mslug3", "mslug3", 25, true, CancellationToken.None);
        Assert.Equal(room, rb?.Room);

        // Le decompte part (5 s a deux), puis le lancement : B, mieux connecte, heberge ; A est P1.
        var decompte = await AttendreAsync(a, room, r => r.State == "countdown", 10);
        Assert.NotNull(decompte);
        Assert.InRange(decompte!.CountdownMs ?? -1, 1, 5000);
        Assert.Equal(2, decompte.Players.Count);
        Assert.Contains(decompte.Players, j => j.You && j.Country == "FR");
        var lancementA = await AttendreAsync(a, room, r => r.State == "launching" && r.Launch is not null);
        var lancementB = await AttendreAsync(b, room, r => r.State == "launching" && r.Launch is not null);
        Assert.Equal("guest", lancementA?.Launch?.Role);
        Assert.Equal("host", lancementB?.Launch?.Role);
        Assert.Equal(1, lancementA!.Launch!.Seat);
        Assert.Equal(2, lancementB!.Launch!.Seat);
        Assert.Null(lancementA.Launch.Netplay);   // rien avant la publication

        // B publie sa session ; A la recoit, B ne recoit pas la sienne.
        var publiee = await b.PublierNetplayAsync(room, new MultiplayerSessionNetplay
        {
            Relay = "europe-west1.relay.retroarch.com", Port = 55435, Session = "e2e5e55", PlayerPassword = "pwjoueur",
            SpectatePassword = "pwspect", Core = "fbneo", CoreVersion = "v1.0.0.03", Crc = "deadbeef",
        }, CancellationToken.None);
        Assert.True(publiee?.Ok, publiee?.Reason);
        var partieA = await AttendreAsync(a, room, r => r.State == "playing" && r.Launch?.Netplay is not null);
        Assert.Equal("pwjoueur", partieA?.Launch?.Netplay?.PlayerPassword);
        Assert.Equal("e2e5e55", partieA?.Launch?.Netplay?.Session);
        Assert.Equal("DEADBEEF", partieA?.Launch?.Netplay?.Crc);
        var partieB = await AttendreAsync(b, room, r => r.State == "playing");
        Assert.Null(partieB?.Launch?.Netplay);

        // L'hote annonce la fin ; les deux voient le resultat.
        Assert.True((await b.TerminerAsync(room, "game_closed", 290, CancellationToken.None))?.Ok);
        var finA = await AttendreAsync(a, room, r => r.State == "finished");
        Assert.Equal(290, finA?.Ended?.DurationS);
        Assert.Equal("game_closed", finA?.Ended?.Reason);

        // Une invitation entre amis : elle porte le contenu, B l'accepte et arrive dans la gameroom de A.
        var rc = await a.RejoindreAsync("arcade/bbmanw", "bbmanw", 80, true, CancellationToken.None);
        Assert.True(rc?.Ok, rc?.Reason);
        Assert.True((await a.InviterAsync(rc!.Room!, new string('b', 16), CancellationToken.None))?.Ok);
        var sondageB = await b.SonderAsync(true, false, 25, CancellationToken.None);
        var invitation = Assert.Single(sondageB!.Invitations);
        Assert.Equal("bbmanw", invitation.Content);
        Assert.Equal("arcade/bbmanw", invitation.Game);
        Assert.Equal("PLAYER A", invitation.From?.Pseudo);
        var accepte = await b.RepondreAsync(invitation.Invitation, true, 25, CancellationToken.None);
        Assert.Equal(rc.Room, accepte?.Room);

        // Les amis en ligne : B est deja avec A.
        var amis = await a.AmisAsync(CancellationToken.None);
        Assert.Equal("with_you", Assert.Single(amis).State);

        // B maintenu : A quitte ; B reste seul, plus de decompte.
        Assert.True((await a.QuitterAsync(rc.Room!, CancellationToken.None))?.Ok);
        var seul = await AttendreAsync(b, rc.Room!, r => r.State == "waiting", 10);
        Assert.NotNull(seul);

        if (BorneC is null)
        {
            return;
        }

        // Un inconnu : C ouvre une gameroom publique, B l'y retrouve ; A n'entre pas dans une gameroom pleine,
        // et ne peut pas inviter C, qui n'est pas son ami.
        var c = Client(BorneC);
        var rp = await c.RejoindreAsync("arcade/mslug3", "mslug3", 40, true, CancellationToken.None);
        Assert.True(rp?.Ok, rp?.Reason);
        var publique = rp!.Room!;
        Assert.Equal(publique, (await b.RejoindreAsync("arcade/mslug3", "mslug3", 25, true, CancellationToken.None))?.Room);
        var ailleurs = await a.RejoindreAsync("arcade/mslug3", "mslug3", 80, true, CancellationToken.None);
        Assert.True(ailleurs?.Ok, ailleurs?.Reason);
        Assert.NotEqual(publique, ailleurs!.Room);
        Assert.Equal("not_a_friend", (await a.InviterAsync(ailleurs.Room!, new string('c', 16), CancellationToken.None))?.Reason);

        // L'hote (B, mieux connecte) part pendant le lancement : la partie s'arrete pour C.
        var lancementC = await AttendreAsync(c, publique, r => r.State == "launching" && r.Launch is not null);
        Assert.Equal("guest", lancementC?.Launch?.Role);
        Assert.Contains(lancementC!.Players, j => j.Host && j.Country == "JP");
        Assert.True((await b.QuitterAsync(publique, CancellationToken.None))?.Ok);
        var abandon = await AttendreAsync(c, publique, r => r.State == "finished", 10);
        Assert.Equal("host_left", abandon?.Ended?.Reason);

        // NIVEAU NELFEPLAY (M6) : A arrive avec Ken, B avec Chun-Li ; un personnage hors liste est refuse ; B passe a Ryu
        // par les fleches ; au lancement, chaque place garde le sien (l'hote les ecrira dans l'etat).
        var sa = await a.RejoindreAsync("arcade/sf2ce", "sf2ce", 80, true, CancellationToken.None, new Dictionary<string, int> { ["character"] = 4 });
        Assert.True(sa?.Ok, sa?.Reason);
        var sb = await b.RejoindreAsync("arcade/sf2ce", "sf2ce", 25, true, CancellationToken.None, new Dictionary<string, int> { ["character"] = 5 });
        Assert.Equal(sa!.Room, sb?.Room);
        Assert.Equal("choice_refused", (await b.ChoisirAsync(sa.Room!, new Dictionary<string, int> { ["character"] = 7 }, CancellationToken.None))?.Reason);
        Assert.True((await b.ChoisirAsync(sa.Room!, new Dictionary<string, int> { ["character"] = 0 }, CancellationToken.None))?.Ok);
        var depart = await AttendreAsync(a, sa.Room!, r => r.State == "launching" && r.Launch is not null);
        Assert.Equal("nelfeplay", depart?.Level);
        var parPlace = depart!.Players.Where(j => j.Seat is not null).ToDictionary(j => j.Seat!.Value, j => j.Choices?["character"]);
        Assert.Equal(4, parPlace[1]);
        Assert.Equal(0, parPlace[2]);
        Assert.Equal("too_late", (await a.ChoisirAsync(sa.Room!, new Dictionary<string, int> { ["character"] = 5 }, CancellationToken.None))?.Reason);

        // La rencontre finie, l'hote annonce la place gagnante : chaque borne la lit pour VICTORY ou DEFEAT.
        var hoteNp = depart.Players.First(j => j.Host).You ? a : b;
        Assert.True((await hoteNp.PublierNetplayAsync(sa.Room!, new MultiplayerSessionNetplay
        {
            Relay = "r", Port = 1, Session = "s", PlayerPassword = "p", SpectatePassword = "q", Core = "fbneo", CoreVersion = "v", Crc = "abcd1234",
        }, CancellationToken.None))?.Ok);
        Assert.True((await hoteNp.TerminerAsync(sa.Room!, "match_over", 61, CancellationToken.None, 2,
            new Dictionary<int, StatsDeLaPlace> { [1] = new(4, 0, 0, 80), [2] = new(9, 2, 1, 288) }))?.Ok);
        var resultatA = await AttendreAsync(a, sa.Room!, r => r.State == "finished");
        Assert.Equal(2, resultatA?.Ended?.WinnerSeat);
        Assert.Equal("match_over", resultatA?.Ended?.Reason);
        Assert.Equal("pending", resultatA?.Ended?.Result);   // l'hote seul ne valide rien
        Assert.Equal(2, resultatA?.Ended?.Stats?["2"].Kos);
        Assert.Equal(9, resultatA?.Ended?.Stats?["2"].Hits);

        // L'invite rapporte la meme place : le resultat est valide, la cote bouge (RANK-03 : 1500 contre 1500, +16 / -16).
        var invite = ReferenceEquals(hoteNp, a) ? b : a;
        Assert.True((await invite.RapporterAsync(sa.Room!, 2, CancellationToken.None))?.Ok);
        var valide = await AttendreAsync(a, sa.Room!, r => r.Ended?.Result == "validated");
        Assert.Equal(1016, Math.Round(valide!.Ended!.Elo!["2"].After));   // echelle de Fightcade (23/10) : depart a 1000
        Assert.Equal(984, Math.Round(valide.Ended.Elo["1"].After));
    }

    private static readonly string? BorneC = Environment.GetEnvironmentVariable("NELFEPLAY_E2E_BORNE_C");

    private sealed class SansFabrique : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("le test passe son propre client");
    }
}
