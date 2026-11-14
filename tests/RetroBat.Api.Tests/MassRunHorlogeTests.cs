using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RetroBat.Api.MassRun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, MASS-02 : l'horloge de depart est verifiee calee. Un noeud d'essai sur la boucle locale : jonction en
/// HTTP (ecrite a la main), PING/PONG en UDP, avec une horloge de noeud en avance de 3 s.
/// </summary>
public class MassRunHorlogeTests
{
    private sealed class NoeudDEssai : IDisposable
    {
        private readonly TcpListener _http = new(IPAddress.Loopback, 0);
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _arret = new();
        private readonly byte[] _cle = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        public bool Repondre { get; init; } = true;
        public long AvanceUs { get; init; } = 3_000_000;
        public int PingsRecus;

        public string Adresse => "http://127.0.0.1:" + ((IPEndPoint)_http.LocalEndpoint).Port;

        public NoeudDEssai()
        {
            _http.Start();
            _ = Task.Run(ServirHttpAsync);
            _ = Task.Run(ServirUdpAsync);
        }

        private async Task ServirHttpAsync()
        {
            while (!_arret.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _http.AcceptTcpClientAsync(_arret.Token); } catch { return; }
                using (tcp)
                {
                    var flux = tcp.GetStream();
                    var tampon = new byte[8192];
                    var lu = 0;
                    var texte = "";
                    // en-tetes puis corps (Content-Length)
                    while (true)
                    {
                        var n = await flux.ReadAsync(tampon.AsMemory(lu));
                        if (n <= 0) break;
                        lu += n;
                        texte = Encoding.UTF8.GetString(tampon, 0, lu);
                        var fin = texte.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (fin < 0) continue;
                        var longueur = 0;
                        foreach (var ligne in texte[..fin].Split("\r\n"))
                        {
                            if (ligne.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) longueur = int.Parse(ligne[15..].Trim());
                        }

                        if (lu >= fin + 4 + longueur) break;
                    }

                    var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
                    var corps = "{\"ok\":true,\"salle\":7,\"place\":3,\"cle\":\"" + Convert.ToBase64String(_cle) + "\",\"go_unix_ms\":"
                                + DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds() + ",\"limite\":300,\"niveau\":\"1-1\",\"jeu\":\"smb\",\"port_udp\":" + port + "}";
                    var octets = Encoding.UTF8.GetBytes(corps);
                    var entete = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + octets.Length + "\r\nConnection: close\r\n\r\n";
                    await flux.WriteAsync(Encoding.ASCII.GetBytes(entete));
                    await flux.WriteAsync(octets);
                }
            }
        }

        private async Task ServirUdpAsync()
        {
            while (!_arret.IsCancellationRequested)
            {
                UdpReceiveResult recu;
                try { recu = await _udp.ReceiveAsync(_arret.Token); } catch { return; }
                if (!MassRunProtocole.Lire(recu.Buffer, out var type, out var salle, out var place, out var seq) || type != MassRunProtocole.Ping) continue;
                Interlocked.Increment(ref PingsRecus);
                if (!Repondre) continue;
                var envoi = BinaryPrimitives.ReadInt64LittleEndian(MassRunProtocole.Contenu(recu.Buffer));
                var contenu = new byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(contenu, envoi);
                BinaryPrimitives.WriteInt64LittleEndian(contenu.AsSpan(8), MassRunProtocole.Maintenant() + AvanceUs);
                var paquet = new byte[MassRunProtocole.Entete + contenu.Length + MassRunProtocole.Sceau];
                var n = MassRunProtocole.Ecrire(paquet, MassRunProtocole.Pong, salle, place, seq, contenu, _cle);
                await _udp.SendAsync(paquet.AsMemory(0, n), recu.RemoteEndPoint);
            }
        }

        public void Dispose()
        {
            _arret.Cancel();
            _http.Stop();
            _udp.Dispose();
        }
    }

    [Fact]
    public async Task Avec_les_PONG_l_horloge_est_calee_sur_celle_du_noeud()
    {
        using var noeud = new NoeudDEssai();
        using var client = await ClientDeSalle.RejoindreAsync(noeud.Adresse, 7, "ESSAI", "A98BFF", CancellationToken.None);
        Assert.True(client.HorlogeCalee);
        Assert.InRange(client.MeilleurAllerRetourMs, 0, 200);
        Assert.InRange(client.EcartHorlogeMs, 2_900, 3_100);
    }

    [Fact]
    public async Task Sans_aucun_PONG_la_jonction_echoue_avec_sa_raison()
    {
        var avant = ClientDeSalle.AttenteDesPong;
        ClientDeSalle.AttenteDesPong = TimeSpan.FromMilliseconds(200);
        try
        {
            using var noeud = new NoeudDEssai { Repondre = false };
            var erreur = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ClientDeSalle.RejoindreAsync(noeud.Adresse, 7, "ESSAI", "A98BFF", CancellationToken.None));
            Assert.Contains("horloge non calee", erreur.Message);
            Assert.Equal(20, noeud.PingsRecus);
        }
        finally
        {
            ClientDeSalle.AttenteDesPong = avant;
        }
    }
}
