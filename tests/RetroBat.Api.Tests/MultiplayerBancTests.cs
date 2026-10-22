using System.Globalization;
using System.Security.Cryptography;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEPART PRET ET LA FIN DE RENCONTRE CONTRE UN VRAI RETROARCH (banc d'essai, jamais une partie de la borne) :
/// Street Fighter II' CE sous FBNeo, heberge en netplay, l'etat de selection a deux joueurs charge, Ken et Chun-Li
/// ecrits et relus ; puis deux K.O. de P2 provoques par ecriture, et le guet doit designer P1.
///
/// Ne tourne que si le banc est monte (NELFEPLAY_BANC_RA_PORT, NELFEPLAY_BANC_ETAT) ; sinon il passe sans rien faire.
/// </summary>
public class MultiplayerBancTests
{
    private static readonly string? Port = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RA_PORT");
    private static readonly string? Etat = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_ETAT");

    // Vie de P2 (u16be a 0xFF86E8) : octet bas au decalage 0x86E8, octet haut a 0x86E9.
    private const long VieBasseP2 = 0x86E8;
    private const long VieHauteP2 = 0x86E9;

    [Fact]
    public async Task Le_depart_pret_et_la_fin_de_rencontre_tiennent_sur_un_vrai_RetroArch()
    {
        if (Port is null || Etat is null)
        {
            return;   // banc non monte
        }

        var ra = new CommandesRetroArchUdp(int.Parse(Port, CultureInfo.InvariantCulture));
        var jeu = new MultiplayerGame
        {
            Id = "arcade/sf2ce", System = "arcade", Set = "sf2ce", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = new MultiplayerStart { State = "banc", Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Etat))) },
            Choices =
            [
                new MultiplayerChoice
                {
                    Key = "character", Default = 0,
                    Values = [new() { Value = 0, Name = "RYU" }, new() { Value = 4, Name = "KEN" }, new() { Value = 5, Name = "CHUN-LI" }],
                    Seats = [new() { Seat = 1, CoreRam = "0x864E" }, new() { Seat = 2, CoreRam = "0x894E" }],
                },
            ],
            End = new MultiplayerEnd
            {
                FirstTo = 2, CloseAfterS = 5,
                Seats = [new() { Seat = 1, CoreRam = "0x864F" }, new() { Seat = 2, CoreRam = "0x894F" }],
            },
            Stats = new MultiplayerStats
            {
                Life = new MultiplayerStatLife
                {
                    Bytes = 2, Full = 144, Ko = 0xFFFF,
                    Seats = [new() { Seat = 1, CoreRam = "0x83E8" }, new() { Seat = 2, CoreRam = "0x86E8" }],
                },
            },
        };

        var places = new Dictionary<int, IReadOnlyDictionary<string, int>?>
        {
            [1] = new Dictionary<string, int> { ["character"] = 4 },
            [2] = new Dictionary<string, int> { ["character"] = 5 },
        };
        var depart = await new MultiplayerDepartPret(ra).PreparerAsync(jeu, Etat, "sf2ce", places, CancellationToken.None);
        try
        {
            Assert.True(depart.Ok, depart.Raison);

            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var guet = new MultiplayerFinDeRencontre(ra).AttendreLeVainqueurAsync(jeu.End, [1, 2], delai.Token);
            using var finDuSuivi = new CancellationTokenSource();
            var suivi = new MultiplayerStatsDuDuel(ra).SuivreAsync(jeu, [1, 2], finDuSuivi.Token);

            // Le combat part seul a la fin du chrono de la selection : la vie de P2 passe a 144.
            for (var manche = 1; manche <= 2; manche++)
            {
                Assert.True(await AttendreAsync(ra, VieBasseP2, 144, TimeSpan.FromSeconds(60)), $"le combat {manche} ne commence pas");
                await Task.Delay(1500);
                await ra.EnvoyerAsync($"WRITE_CORE_RAM {VieHauteP2:x} ff", CancellationToken.None);
                await ra.EnvoyerAsync($"WRITE_CORE_RAM {VieBasseP2:x} ff", CancellationToken.None);
                await Task.Delay(3000);   // le K.O. pris, la vie ne revient a 144 qu'a la manche suivante
            }

            Assert.Equal(1, await guet);
            await Task.Delay(1500);
            finDuSuivi.Cancel();
            var stats = await suivi;
            // Deux K.O. de P2 a vie pleine : deux coups, deux K.O. et deux perfects pour P1, 288 de degats.
            Assert.Equal(new StatsDeLaPlace(2, 2, 2, 288), stats[1]);
            Assert.Equal(0, stats[2].Coups);
        }
        finally
        {
            if (depart.Fichier is { } pose)
            {
                File.Delete(pose);
            }
        }
    }

    private static async Task<bool> AttendreAsync(ICommandesRetroArch ra, long decalage, int valeur, TimeSpan delai)
    {
        var limite = DateTime.UtcNow + delai;
        while (DateTime.UtcNow < limite)
        {
            var reponse = await ra.DemanderAsync($"READ_CORE_RAM {decalage:x} 1", CancellationToken.None);
            var morceaux = reponse?.Split(' ');
            if (morceaux is { Length: >= 3 } && int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lu) && lu == valeur)
            {
                return true;
            }

            await Task.Delay(300);
        }

        return false;
    }
}
