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

    /// <summary>
    /// Third Strike sous FBNeo (CPS-3, octets inverses par 4) : l'etat de l'ecran VS charge, Ken et Chun-Li ecrits
    /// 1,5 s apres et relus ; puis deux K.O. de P2 (vie de la structure mise a 0, puis un coup de P1 par la manette
    /// reseau 1 : a vie nulle sans coup, le jeu ne met pas K.O.). Banc : NELFEPLAY_BANC_RA_PORT, NELFEPLAY_BANC_SFIII3_ETAT.
    /// </summary>
    [Fact]
    public async Task Third_Strike_part_de_l_ecran_VS_avec_les_personnages_ecrits()
    {
        var etat = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_SFIII3_ETAT");
        if (Port is null || etat is null)
        {
            return;   // banc non monte
        }

        const long VieP2 = 0x691A0;
        var ra = new CommandesRetroArchUdp(int.Parse(Port, CultureInfo.InvariantCulture));
        var jeu = new MultiplayerGame
        {
            Id = "arcade/sfiii3", System = "arcade", Set = "sfiii3", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = new MultiplayerStart { State = "banc", Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(etat))) },
            Choices =
            [
                new MultiplayerChoice
                {
                    Key = "character", Default = 1,
                    Values = [new() { Value = 1, Name = "ALEX" }, new() { Value = 11, Name = "KEN" }, new() { Value = 16, Name = "CHUN-LI" }],
                    Seats = [new() { Seat = 1, CoreRam = "0x11384" }, new() { Seat = 2, CoreRam = "0x1138B" }],
                },
            ],
            End = new MultiplayerEnd
            {
                FirstTo = 2, CloseAfterS = 5,
                Seats = [new() { Seat = 1, CoreRam = "0x11380" }, new() { Seat = 2, CoreRam = "0x11386" }],
            },
            Stats = new MultiplayerStats
            {
                Life = new MultiplayerStatLife
                {
                    Bytes = 1, Full = 160, Ko = 255,
                    Seats = [new() { Seat = 1, CoreRam = "0x68D08" }, new() { Seat = 2, CoreRam = "0x691A0" }],
                },
            },
        };

        var places = new Dictionary<int, IReadOnlyDictionary<string, int>?>
        {
            [1] = new Dictionary<string, int> { ["character"] = 11 },
            [2] = new Dictionary<string, int> { ["character"] = 16 },
        };
        var depart = await new MultiplayerDepartPret(ra).PreparerAsync(jeu, etat, "sfiii3", places, CancellationToken.None);
        using var manette = new ManetteReseau(1);
        try
        {
            Assert.True(depart.Ok, depart.Raison);

            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var guet = new MultiplayerFinDeRencontre(ra).AttendreLeVainqueurAsync(jeu.End, [1, 2], delai.Token);
            using var finDuSuivi = new CancellationTokenSource();
            var suivi = new MultiplayerStatsDuDuel(ra).SuivreAsync(jeu, [1, 2], finDuSuivi.Token);

            for (var manche = 1; manche <= 2; manche++)
            {
                Assert.True(await AttendreAsync(ra, VieP2, 160, TimeSpan.FromSeconds(60)), $"le combat {manche} ne commence pas");
                await Task.Delay(5000);   // la vie se remplit des l'annonce de la manche ; les joueurs ne bougent qu'au FIGHT
                await ra.EnvoyerAsync($"WRITE_CORE_RAM {VieP2:x} 00", CancellationToken.None);
                await manette.TenirAsync(7, TimeSpan.FromSeconds(2.5));   // droite : P1 avance
                for (var coup = 0; coup < 4; coup++)
                {
                    await manette.TenirAsync(1, TimeSpan.FromMilliseconds(100));   // Y : poing faible
                    await Task.Delay(250);
                }

                await Task.Delay(3000);
            }

            Assert.Equal(1, await guet);
            await Task.Delay(1500);
            finDuSuivi.Cancel();
            var stats = await suivi;
            // Par manche : la vie mise a 0 compte un coup de 160, puis le vrai coup met K.O. ; P1 gagne vie pleine.
            Assert.Equal(new StatsDeLaPlace(4, 2, 2, 320), stats[1]);
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

    /// <summary>La manette reseau d'une place (port 55400 + place - 1) : un bouton tenu se renvoie toutes les 18 ms.</summary>
    private sealed class ManetteReseau(int place) : IDisposable
    {
        private readonly System.Net.Sockets.UdpClient _udp = new();
        private readonly int _port = 55400 + place - 1;

        public async Task TenirAsync(int bouton, TimeSpan duree)
        {
            var fin = DateTime.UtcNow + duree;
            while (DateTime.UtcNow < fin)
            {
                await EnvoyerAsync(bouton, true);
                await Task.Delay(18);
            }

            for (var i = 0; i < 3; i++)
            {
                await EnvoyerAsync(bouton, false);
                await Task.Delay(20);
            }
        }

        private Task EnvoyerAsync(int bouton, bool appuye)
        {
            var paquet = new byte[20];
            BitConverter.GetBytes(1).CopyTo(paquet, 4);
            BitConverter.GetBytes(bouton).CopyTo(paquet, 12);
            BitConverter.GetBytes((ushort)(appuye ? 1 : 0)).CopyTo(paquet, 16);
            return _udp.SendAsync(paquet, paquet.Length, "127.0.0.1", _port);
        }

        public void Dispose() => _udp.Dispose();
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
