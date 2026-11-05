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
    /// <summary>La regle de mort de la salle : 0 celle du jeu, 1 retour au debut, 2 elimination.</summary>
    public uint RegleMort { get; }
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
        /// <summary>Les dernieres positions recues (instant en images, piste), de la plus ancienne a la plus recente.</summary>
        internal readonly List<(double T, byte[] Piste)> Echantillons = new();
    }

    /// <summary>
    /// LE RETARD D'AFFICHAGE DES AUTRES COUREURS (retour du user du 04/11 : « saccade ») : les positions arrivent 15 fois par
    /// seconde, le jeu tourne a 60 images. Les autres sont montres 7 images (115 ms) dans le passe, la ou deux positions
    /// recues les encadrent, et glissent de l'une a l'autre ; au-dela de la derniere, leur mouvement est prolonge 8 images.
    /// Les coureurs ne se touchent pas : ce retard ne change rien a la course.
    /// </summary>
    public const double RetardDAffichage = 7;
    private const double Prolongement = 8;

    private readonly System.Diagnostics.Stopwatch _horloge = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>L'horloge locale en images (60,0988 par seconde) : l'instant des positions de la salle d'attente, ou toutes
    /// les bornes sont a l'image de course 0.</summary>
    public double HorlogeEnImages() => _horloge.Elapsed.TotalSeconds * 60.0988;

    /// <summary>La piste d'un coureur a l'instant <paramref name="cible"/> (en images), glissee entre deux positions recues.</summary>
    public bool Composer(Coureur coureur, double cible, Span<byte> sortie)
    {
        lock (_verrou)
        {
            var e = coureur.Echantillons;
            if (e.Count == 0)
            {
                return false;
            }
            if (cible <= e[0].T || e.Count == 1)
            {
                e[cible <= e[0].T ? 0 : e.Count - 1].Piste.CopyTo(sortie);
                return true;
            }
            for (var i = 0; i < e.Count - 1; i++)
            {
                if (cible <= e[i + 1].T)
                {
                    var f = (cible - e[i].T) / Math.Max(1e-6, e[i + 1].T - e[i].T);
                    GlisserSelonLeJeu(e[i].Piste, e[i + 1].Piste, f, sortie);
                    return true;
                }
            }
            // au-dela de la derniere position : le mouvement des deux dernieres, prolonge un peu
            var (ta, a) = e[^2];
            var (tb, b) = e[^1];
            var depasse = Math.Min(cible - tb, Prolongement);
            GlisserSelonLeJeu(a, b, 1 + depasse / Math.Max(1e-6, tb - ta), sortie);
            return true;
        }
    }

    private void GlisserSelonLeJeu(byte[] a, byte[] b, double f, Span<byte> sortie)
    {
        if (JeuxMassRun.FormatMegaDrive(Jeu)) GlisserMegaDrive(a, b, f, sortie);
        else Glisser(a, b, f, sortie);
    }

    /// <summary>
    /// L'image de piste d'un coureur Mega Drive (voir <see cref="JeuxMassRun"/>) : x dans le niveau aux octets 0-1, y en deux
    /// octets (8 et 11), la pose (numero d'image de Sonic et retournement) aux octets 9 et 10. Entre deux positions : la pose de
    /// la plus proche, a une position glissee ; d'un acte a l'autre ou sur un saut (mort, retour au depart), pas de glissement.
    /// </summary>
    internal static void GlisserMegaDrive(byte[] a, byte[] b, double f, Span<byte> sortie)
    {
        var reference = f < 0.5 ? a : b;
        reference.CopyTo(sortie);
        if (MassRunProtocole.Scene(a) != MassRunProtocole.Scene(b) || a[6] == 0 || b[6] == 0)
        {
            return;
        }
        int xa = JeuxMassRun.XMegaDrive(a), xb = JeuxMassRun.XMegaDrive(b), ya = JeuxMassRun.YMegaDrive(a), yb = JeuxMassRun.YMegaDrive(b);
        if (Math.Abs(xb - xa) > 256 || Math.Abs(yb - ya) > 256)
        {
            return;
        }
        JeuxMassRun.PlacerMegaDrive(sortie, (int)Math.Round(xa + (xb - xa) * f), (int)Math.Round(ya + (yb - ya) * f));
    }

    /// <summary>
    /// Entre deux pistes : la silhouette de la plus proche, a une position glissee. La position dans le niveau (camera + x du
    /// premier sprite) passe par la camera de la piste rendue ; la hauteur decale tous les sprites. D'une scene a l'autre, pas
    /// de glissement.
    /// </summary>
    internal static void Glisser(byte[] a, byte[] b, double f, Span<byte> sortie)
    {
        var reference = f < 0.5 ? a : b;
        reference.CopyTo(sortie);
        if (MassRunProtocole.Scene(a) != MassRunProtocole.Scene(b) || a[6] == 0 || b[6] == 0)
        {
            return;
        }
        double xa = MassRunProtocole.XDansLeNiveau(a), xb = MassRunProtocole.XDansLeNiveau(b);
        double ya = a[8], yb = b[8];
        if (Math.Abs(xb - xa) > 128 || Math.Abs(yb - ya) > 96)
        {
            return;   // une teleportation (tuyau, mort) : pas de glissement
        }
        var x = (int)Math.Round(xa + (xb - xa) * f);
        var y = (int)Math.Round(ya + (yb - ya) * f);
        var camera = x - reference[8 + 3];
        BinaryPrimitives.WriteUInt16LittleEndian(sortie, (ushort)Math.Clamp(camera, 0, ushort.MaxValue));
        var dy = y - reference[8];
        for (var s = 0; s < Math.Min((int)reference[6], 8); s++)
        {
            var o = 8 + s * 4;
            if (reference[o] >= 0xEF) continue;
            sortie[o] = (byte)Math.Clamp(reference[o] + dy, 0, 0xEE);
        }
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
        RegleMort = jonction.TryGetProperty("mort", out var mort) ? mort.GetString() switch { "jeu" => 0u, "elimination" => 2u, _ => 1u } : 1u;
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

    /// <summary>L'arrivee, ou l'elimination (bit de poids fort) en MASS SURVIVAL.</summary>
    public void EnvoyerArrivee(uint images, bool elimine = false)
    {
        if (elimine) images |= 0x80000000u;
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
                // l'instant de la position : son image de course ; en salle d'attente (image 0 pour tous), l'heure d'arrivee
                var t = image > 0 ? image : HorlogeEnImages();
                var echantillons = coureur.Echantillons;
                if (echantillons.Count > 0 && t < echantillons[^1].T - 120)
                {
                    echantillons.Clear();   // passage de la salle d'attente a la course
                }
                if (echantillons.Count == 0 || t > echantillons[^1].T)
                {
                    echantillons.Add((t, (byte[])coureur.Piste.Clone()));
                    if (echantillons.Count > 8) echantillons.RemoveAt(0);
                }
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

    public sealed record EtatDeLaSalle(List<MassRunCanal.LigneDeClassement> Lignes, int MonRang, int Arrives, int Inscrits);

    private static readonly HttpClient Lecteur = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Le classement complet d'une salle, pour la page de bilan (la salle est deja quittee) ; null si le noeud ne repond pas.</summary>
    public static async Task<EtatDeLaSalle?> LireLeClassementAsync(string noeud, uint salle, ushort place, CancellationToken ct)
    {
        try
        {
            var json = JsonDocument.Parse(await Lecteur.GetStringAsync(noeud.TrimEnd('/') + $"/massrun/v1/salles/{salle}", ct).ConfigureAwait(false)).RootElement;
            return Classement(json, place, int.MaxValue);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or FormatException or KeyNotFoundException
            or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Le classement de l'arrivee (CDC : « au fur et a mesure des arrivees ») : les arrives dans l'ordre ; en Mass Survival,
    /// les elimines du dernier au premier, sous les joueurs encore en vie.
    /// </summary>
    public async Task<EtatDeLaSalle?> ClassementAsync()
    {
        try
        {
            var json = JsonDocument.Parse(await _http.GetStringAsync($"/massrun/v1/salles/{Salle}").ConfigureAwait(false)).RootElement;
            return Classement(json, Place, MassRunCanal.LignesDeClassement);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Le classement d'apres la salle du noeud : les <paramref name="nombre"/> premieres lignes, puis celle du joueur s'il est
    /// plus loin. Pur.
    /// </summary>
    internal static EtatDeLaSalle Classement(JsonElement json, ushort maPlace, int nombre)
    {
        {
            var inscrits = json.GetProperty("inscrits").GetInt32();
            var lignes = new List<MassRunCanal.LigneDeClassement>();
            var monRang = 0;
            var arrives = 0;
            void Ajouter(JsonElement e, int rang, bool elimine)
            {
                var place = (ushort)e.GetProperty("place").GetInt32();
                var moi = place == maPlace;
                if (moi) monRang = rang;
                lignes.Add(new MassRunCanal.LigneDeClassement(rang, moi, elimine, e.GetProperty("images").GetUInt32(),
                    Convert.ToUInt32(e.TryGetProperty("couleur", out var c) ? c.GetString() ?? "A98BFF" : "A98BFF", 16),
                    e.GetProperty("pseudo").GetString() ?? ""));
            }
            var rang = 0;
            foreach (var e in json.GetProperty("arrivees").EnumerateArray())
            {
                Ajouter(e, ++rang, false);
                arrives++;
            }
            if (json.TryGetProperty("eliminations", out var eliminations))
            {
                var elimines = eliminations.EnumerateArray().ToList();
                // le dernier elimine est derriere tous ceux encore en vie
                var enVie = inscrits - elimines.Count - arrives;
                rang += Math.Max(0, enVie);
                foreach (var e in elimines)
                {
                    Ajouter(e, ++rang, true);
                    arrives++;
                }
            }
            // les premieres lignes, puis celle du joueur s'il est plus loin
            var montrees = lignes.Take(nombre).ToList();
            if (monRang > 0 && !montrees.Any(l => l.Moi))
            {
                montrees[^1] = lignes.First(l => l.Moi);
            }
            return new EtatDeLaSalle(montrees, monRang, arrives, inscrits);
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
