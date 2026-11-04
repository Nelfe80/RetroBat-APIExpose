using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace RetroBat.Api.MassRun;

/// <summary>
/// LA BORNE DANS UNE SALLE D'UN NOEUD (CDC MASS RUN §9-§10, lot L3) : jonction en HTTP, horloge par PING/PONG, positions du
/// joueur envoyees en UDP, instantanes recus et refaits (dictionnaire de poses), pseudos et couleurs relus toutes les 2 s.
/// </summary>
public sealed class ClientDeSalle : IDisposable
{
    private readonly HttpClient _http;
    private readonly UdpClient _udp;
    private readonly IPEndPoint _noeud;
    private readonly byte[] _cle;
    private readonly Dictionary<ushort, byte[]> _poses = new();
    private readonly Dictionary<ushort, Coureur> _coureurs = new();
    private readonly object _verrou = new();
    private readonly CancellationTokenSource _arret = new();
    private ushort _sequence;
    private long _ecartHorlogeUs;
    private DateTime _dernierRoster = DateTime.MinValue;

    public uint Salle { get; }
    public ushort Place { get; }
    public string Niveau { get; }
    public string Jeu { get; }
    public long GoUnixMs { get; }
    public int Limite { get; }
    public long InstantanesRecus;
    public long OctetsRecus;
    public long OctetsEnvoyes;

    public sealed class Coureur
    {
        public uint Image;
        public readonly byte[] Piste = new byte[MassRunProtocole.ImageDePiste];
        public string Pseudo = "";
        public uint Couleur = 0xA98BFF;
        public DateTime RecuLe;
    }

    private ClientDeSalle(HttpClient http, JsonElement jonction, string hote)
    {
        _http = http;
        Salle = jonction.GetProperty("salle").GetUInt32();
        Place = (ushort)jonction.GetProperty("place").GetInt32();
        _cle = Convert.FromBase64String(jonction.GetProperty("cle").GetString() ?? "");
        GoUnixMs = jonction.GetProperty("go_unix_ms").GetInt64();
        Limite = jonction.GetProperty("limite").GetInt32();
        Niveau = jonction.GetProperty("niveau").GetString() ?? "1-1";
        Jeu = jonction.GetProperty("jeu").GetString() ?? "";
        var adresse = Dns.GetHostAddresses(hote).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _noeud = new IPEndPoint(adresse, jonction.GetProperty("port_udp").GetInt32());
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback.Equals(adresse) ? IPAddress.Loopback : IPAddress.Any, 0));
    }

    /// <summary>Rejoint la salle : place et cle de session, puis horloge calee sur celle du noeud.</summary>
    public static async Task<ClientDeSalle> RejoindreAsync(string noeud, uint salle, string pseudo, string couleur, CancellationToken ct)
    {
        var http = new HttpClient { BaseAddress = new Uri(noeud), Timeout = TimeSpan.FromSeconds(10) };
        var reponse = await http.PostAsJsonAsync($"/massrun/v1/salles/{salle}/rejoindre", new { pseudo, couleur }, ct).ConfigureAwait(false);
        var json = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
        if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            http.Dispose();
            throw new InvalidOperationException("jonction refusee : " + (json.TryGetProperty("error", out var e) ? e.GetString() : reponse.StatusCode.ToString()));
        }
        var client = new ClientDeSalle(http, json.Clone(), new Uri(noeud).Host);
        _ = Task.Run(() => client.RecevoirAsync(client._arret.Token));
        await client.CalerLHorlogeAsync(ct).ConfigureAwait(false);
        return client;
    }

    /// <summary>Dix PING ; l'echange le plus court donne l'ecart d'horloge (CDC §10).</summary>
    private async Task CalerLHorlogeAsync(CancellationToken ct)
    {
        _meilleurAllerRetour = long.MaxValue;
        for (var i = 0; i < 10; i++)
        {
            EnvoyerPing();
            await Task.Delay(60, ct).ConfigureAwait(false);
        }
    }

    public void EnvoyerPing()
    {
        Span<byte> contenu = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(contenu, MassRunProtocole.Maintenant());
        Envoyer(MassRunProtocole.Ping, contenu);
    }

    private long _meilleurAllerRetour = long.MaxValue;

    /// <summary>L'heure du GO! en heure de cette machine.</summary>
    public DateTime GoLocalUtc => DateTime.UnixEpoch.AddTicks(GoUnixMs * 10_000 - Interlocked.Read(ref _ecartHorlogeUs) * 10);

    public long EcartHorlogeMs => Interlocked.Read(ref _ecartHorlogeUs) / 1000;

    public void EnvoyerPosition(uint image, uint mode, ReadOnlySpan<byte> piste)
    {
        Span<byte> contenu = stackalloc byte[8 + MassRunProtocole.ImageDePiste];
        contenu.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(contenu, image);
        contenu[4] = (byte)mode;
        piste[..MassRunProtocole.ImageDePiste].CopyTo(contenu[8..]);
        Envoyer(MassRunProtocole.Pos, contenu);
    }

    public void EnvoyerArrivee(uint images)
    {
        Span<byte> contenu = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(contenu, images);
        Envoyer(MassRunProtocole.Arrivee, contenu);
    }

    private void Envoyer(byte type, ReadOnlySpan<byte> contenu)
    {
        var paquet = new byte[MassRunProtocole.Entete + contenu.Length + MassRunProtocole.Sceau];
        var n = MassRunProtocole.Ecrire(paquet, type, Salle, Place, ++_sequence, contenu, _cle);
        try
        {
            _udp.Send(paquet, n, _noeud);
            Interlocked.Add(ref OctetsEnvoyes, n + 28);
        }
        catch (SocketException)
        {
        }
    }

    private async Task RecevoirAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult recu;
            try
            {
                recu = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            Interlocked.Add(ref OctetsRecus, recu.Buffer.Length + 28);
            if (!MassRunProtocole.Lire(recu.Buffer, out var type, out var salle, out _, out _) || salle != Salle
                || !MassRunProtocole.SceauValide(recu.Buffer, _cle))
            {
                continue;
            }
            Traiter(type, recu.Buffer);
        }
    }

    private void Traiter(byte type, byte[] paquet)
    {
        var contenu = MassRunProtocole.Contenu(paquet);
        if (type == MassRunProtocole.Pong && contenu.Length >= 16)
        {
            var maintenant = MassRunProtocole.Maintenant();
            var envoi = BinaryPrimitives.ReadInt64LittleEndian(contenu);
            var noeud = BinaryPrimitives.ReadInt64LittleEndian(contenu[8..]);
            var allerRetour = maintenant - envoi;
            if (allerRetour >= 0 && allerRetour < Interlocked.Read(ref _meilleurAllerRetour))
            {
                Interlocked.Exchange(ref _meilleurAllerRetour, allerRetour);
                Interlocked.Exchange(ref _ecartHorlogeUs, noeud - (envoi + allerRetour / 2));
            }
            return;
        }
        if (type != MassRunProtocole.Instantane || contenu.Length < 6)
        {
            return;
        }
        Interlocked.Increment(ref InstantanesRecus);
        var base_ = BinaryPrimitives.ReadUInt32LittleEndian(contenu);
        var n = contenu[4];
        var definies = contenu[5];
        var o = 6;
        lock (_verrou)
        {
            for (var k = 0; k < definies && o + 3 <= contenu.Length; k++)
            {
                var numero = BinaryPrimitives.ReadUInt16LittleEndian(contenu[o..]);
                var longueur = 1 + contenu[o + 2] * 4;
                if (o + 2 + longueur > contenu.Length) return;
                _poses[numero] = contenu.Slice(o + 2, longueur).ToArray();
                o += 2 + longueur;
            }
            for (var k = 0; k < n && o + MassRunProtocole.EntreeCompacte <= contenu.Length; k++, o += MassRunProtocole.EntreeCompacte)
            {
                var e = contenu.Slice(o, MassRunProtocole.EntreeCompacte);
                var place = BinaryPrimitives.ReadUInt16LittleEndian(e);
                var pose = BinaryPrimitives.ReadUInt16LittleEndian(e[11..]);
                if (place == Place || !_poses.TryGetValue(pose, out var definition))
                {
                    continue;
                }
                if (!_coureurs.TryGetValue(place, out var coureur))
                {
                    coureur = new Coureur();
                    _coureurs[place] = coureur;
                }
                var image = base_ - e[2];
                if (image < coureur.Image && coureur.RecuLe > DateTime.UtcNow.AddSeconds(-2))
                {
                    continue;   // plus ancien que ce qu'on a deja
                }
                coureur.Image = image;
                MassRunProtocole.Refaire(e, definition, coureur.Piste);
                coureur.RecuLe = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Les coureurs recus depuis moins d'une seconde, avec leur pseudo et leur couleur.</summary>
    public List<(ushort Place, Coureur Coureur)> CoureursFrais()
    {
        if (DateTime.UtcNow - _dernierRoster > TimeSpan.FromSeconds(2))
        {
            _dernierRoster = DateTime.UtcNow;
            _ = Task.Run(RelireLesJoueursAsync);
        }
        lock (_verrou)
        {
            var limite = DateTime.UtcNow.AddSeconds(-1);
            return _coureurs.Where(c => c.Value.RecuLe > limite).Select(c => (c.Key, c.Value)).ToList();
        }
    }

    private async Task RelireLesJoueursAsync()
    {
        try
        {
            var json = JsonDocument.Parse(await _http.GetStringAsync($"/massrun/v1/salles/{Salle}/joueurs").ConfigureAwait(false)).RootElement;
            lock (_verrou)
            {
                foreach (var j in json.GetProperty("joueurs").EnumerateArray())
                {
                    var place = (ushort)j.GetProperty("place").GetInt32();
                    if (!_coureurs.TryGetValue(place, out var coureur))
                    {
                        coureur = new Coureur();
                        _coureurs[place] = coureur;
                    }
                    coureur.Pseudo = j.GetProperty("pseudo").GetString() ?? "";
                    coureur.Couleur = Convert.ToUInt32(j.GetProperty("couleur").GetString() ?? "A98BFF", 16);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or FormatException)
        {
        }
    }

    public void Dispose()
    {
        _arret.Cancel();
        _udp.Dispose();
        _http.Dispose();
    }
}
