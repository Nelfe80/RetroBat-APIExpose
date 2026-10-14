using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// L'EMETTEUR RTMP DU DIRECT D'UN RECORD (CDC video V2, §5.8) : poignee de main simple, commandes AMF0, puis les images
/// et le son du film, tels quels (FLV). Essaye le 14 octobre 2026 vers MediaMTX et vers YouTube Live (prototype).
/// L'adresse porte la cle de diffusion : elle ne se journalise jamais.
/// </summary>
public sealed class EmetteurRtmp : IAsyncDisposable
{
    private const int MorceauSortant = 4096;
    private const byte TypeTailleMorceau = 1;
    private const byte TypeControle = 4;
    private const byte TypeAudio = 8;
    private const byte TypeVideo = 9;
    private const byte TypeDonnees = 18;
    private const byte TypeCommande = 20;

    private readonly string _hote;
    private readonly int _port;
    private readonly bool _securise;
    private readonly string _app;
    private readonly string _cle;
    private readonly string _tcUrl;
    private readonly SemaphoreSlim _ecriture = new(1, 1);
    private readonly Dictionary<int, Entrant> _entrants = new();
    private readonly CancellationTokenSource _fin = new();
    private TcpClient? _tcp;
    private Stream? _flux;
    private int _morceauEntrant = 128;
    private uint _idDuFlux;
    private Task? _lecteur;

    private sealed class Entrant
    {
        public uint Temps;
        public uint Delta;
        public int Longueur;
        public byte Type;
        public uint Flux;
        public bool Etendu;
        public MemoryStream Tampon = new();
    }

    public EmetteurRtmp(string adresse)
    {
        var u = new Uri(adresse);
        _securise = u.Scheme.Equals("rtmps", StringComparison.OrdinalIgnoreCase);
        _hote = u.Host;
        _port = u.IsDefaultPort || u.Port <= 0 ? (_securise ? 443 : 1935) : u.Port;
        var chemin = u.AbsolutePath.TrimStart('/');
        var barre = chemin.IndexOf('/');
        _app = barre < 0 ? chemin : chemin[..barre];
        _cle = barre < 0 ? "" : Uri.UnescapeDataString(chemin[(barre + 1)..]) + u.Query;
        _tcUrl = u.Scheme + "://" + _hote + (u.IsDefaultPort || u.Port <= 0 ? "" : ":" + u.Port) + "/" + _app;
    }

    public List<string> Statuts { get; } = new();
    public long OctetsEnvoyes { get; private set; }
    public string? Erreur { get; private set; }

    public async Task OuvrirAsync(CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_hote, _port, ct).ConfigureAwait(false);
        Stream s = _tcp.GetStream();
        if (_securise)
        {
            var ssl = new SslStream(s, false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _hote }, ct).ConfigureAwait(false);
            s = ssl;
        }
        _flux = s;

        // Poignee de main simple : C0 et C1, puis S0, S1 et S2, et C2 (l'echo de S1).
        var c1 = new byte[1536];
        BinaryPrimitives.WriteUInt32BigEndian(c1, (uint)Environment.TickCount);
        RandomNumberGenerator.Fill(c1.AsSpan(8));
        await s.WriteAsync(new byte[] { 3 }, ct).ConfigureAwait(false);
        await s.WriteAsync(c1, ct).ConfigureAwait(false);
        var s0 = await RecevoirAsync(1, ct).ConfigureAwait(false);
        if (s0[0] != 3) throw new IOException("version RTMP inattendue");
        var s1 = await RecevoirAsync(1536, ct).ConfigureAwait(false);
        await RecevoirAsync(1536, ct).ConfigureAwait(false);
        await s.WriteAsync(s1, ct).ConfigureAwait(false);

        await EnvoyerAsync(2, TypeTailleMorceau, 0, 0, BigEndian((uint)MorceauSortant), ct).ConfigureAwait(false);
        await CommandeAsync(ct, 0, 3, "connect", 1.0, new Dictionary<string, object?>
        {
            ["app"] = _app, ["type"] = "nonprivate", ["flashVer"] = "FMLE/3.0 (compatible; NelfePlay)", ["tcUrl"] = _tcUrl,
        }).ConfigureAwait(false);
        await AttendreAsync(v => v.Count > 1 && Equals(v[0], "_result") && Equals(v[1], 1.0), ct).ConfigureAwait(false);
        await CommandeAsync(ct, 0, 3, "releaseStream", 2.0, null, _cle).ConfigureAwait(false);
        await CommandeAsync(ct, 0, 3, "FCPublish", 3.0, null, _cle).ConfigureAwait(false);
        await CommandeAsync(ct, 0, 3, "createStream", 4.0, null).ConfigureAwait(false);
        var r = await AttendreAsync(v => v.Count > 3 && Equals(v[0], "_result") && Equals(v[1], 4.0), ct).ConfigureAwait(false);
        _idDuFlux = (uint)Convert.ToDouble(r[3], System.Globalization.CultureInfo.InvariantCulture);
        await CommandeAsync(ct, _idDuFlux, 8, "publish", 5.0, null, _cle, "live").ConfigureAwait(false);
        await AttendreAsync(v => v.Count > 3 && Equals(v[0], "onStatus") && v[3] is Dictionary<string, object?> o
            && (o.GetValueOrDefault("code") as string ?? "").StartsWith("NetStream.Publish.", StringComparison.Ordinal), ct).ConfigureAwait(false);
        if (!Statuts.Contains("NetStream.Publish.Start"))
            throw new IOException("publication refusee : " + string.Join(", ", Statuts));
        _lecteur = Task.Run(() => LecteurAsync(_fin.Token));
    }

    public Task MetadonneesAsync(int largeur, int hauteur, double imagesParSeconde, double frequence, CancellationToken ct)
    {
        var corps = new MemoryStream();
        Amf.Ecrire(corps, "@setDataFrame");
        Amf.Ecrire(corps, "onMetaData");
        Amf.EcrireTableau(corps, new Dictionary<string, object?>
        {
            ["width"] = (double)largeur, ["height"] = (double)hauteur, ["framerate"] = imagesParSeconde, ["videocodecid"] = 7.0,
            ["audiocodecid"] = 10.0, ["audiosamplerate"] = frequence, ["audiosamplesize"] = 16.0, ["stereo"] = true,
            ["encoder"] = "NelfePlay (copie du film de RetroArch)",
        });
        return EnvoyerAsync(4, TypeDonnees, _idDuFlux, 0, corps.ToArray(), ct);
    }

    /// <summary>L'en-tete video : la configuration AVC du film (avcC).</summary>
    public Task EnteteVideoAsync(byte[] avcC, CancellationToken ct)
        => EnvoyerAsync(6, TypeVideo, _idDuFlux, 0, [0x17, 0x00, 0, 0, 0, .. avcC], ct);

    /// <summary>L'en-tete du son : sa configuration AAC (AudioSpecificConfig).</summary>
    public Task EnteteAudioAsync(byte[] asc, CancellationToken ct)
        => EnvoyerAsync(4, TypeAudio, _idDuFlux, 0, [0xAF, 0x00, .. asc], ct);

    /// <summary>Une image : instant de decodage, decalage d'affichage, image cle, donnees (NAL prefixees par leur taille).</summary>
    public Task VideoAsync(uint dts, int cts, bool cle, byte[] donnees, CancellationToken ct)
    {
        cts = Math.Max(0, cts);
        var corps = new byte[5 + donnees.Length];
        corps[0] = (byte)(cle ? 0x17 : 0x27);
        corps[1] = 1;
        corps[2] = (byte)(cts >> 16);
        corps[3] = (byte)(cts >> 8);
        corps[4] = (byte)cts;
        donnees.CopyTo(corps, 5);
        return EnvoyerAsync(6, TypeVideo, _idDuFlux, dts, corps, ct);
    }

    public Task AudioAsync(uint temps, byte[] donnees, CancellationToken ct)
        => EnvoyerAsync(4, TypeAudio, _idDuFlux, temps, [0xAF, 0x01, .. donnees], ct);

    /// <summary>Un message, decoupe en morceaux : en-tete complet, puis en-tetes courts pour la suite.</summary>
    internal static byte[] Morceaux(int csid, byte type, uint flux, uint temps, ReadOnlySpan<byte> charge, int taille)
    {
        var etendu = temps >= 0xFFFFFF;
        var t = etendu ? 0xFFFFFFu : temps;
        var sortie = new MemoryStream(charge.Length + 16 + charge.Length / taille * 5);
        sortie.WriteByte((byte)csid);
        sortie.WriteByte((byte)(t >> 16)); sortie.WriteByte((byte)(t >> 8)); sortie.WriteByte((byte)t);
        var l = charge.Length;
        sortie.WriteByte((byte)(l >> 16)); sortie.WriteByte((byte)(l >> 8)); sortie.WriteByte((byte)l);
        sortie.WriteByte(type);
        Span<byte> f = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(f, flux);
        sortie.Write(f);
        Span<byte> e = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(e, temps);
        if (etendu) sortie.Write(e);
        for (var i = 0; i < charge.Length; i += taille)
        {
            if (i > 0)
            {
                sortie.WriteByte((byte)(0xC0 | csid));
                if (etendu) sortie.Write(e);
            }
            sortie.Write(charge.Slice(i, Math.Min(taille, charge.Length - i)));
        }
        return sortie.ToArray();
    }

    private async Task EnvoyerAsync(int csid, byte type, uint flux, uint temps, byte[] charge, CancellationToken ct)
    {
        var donnees = Morceaux(csid, type, flux, temps, charge, csid == 2 && type == TypeTailleMorceau ? 128 : MorceauSortant);
        await _ecriture.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _flux!.WriteAsync(donnees, ct).ConfigureAwait(false);
            OctetsEnvoyes += donnees.Length;
        }
        finally
        {
            _ecriture.Release();
        }
    }

    private Task CommandeAsync(CancellationToken ct, uint flux, int csid, params object?[] valeurs)
    {
        var corps = new MemoryStream();
        foreach (var v in valeurs) Amf.Ecrire(corps, v);
        return EnvoyerAsync(csid, TypeCommande, flux, 0, corps.ToArray(), ct);
    }

    private async Task<List<object?>> AttendreAsync(Func<List<object?>, bool> voulu, CancellationToken ct)
    {
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var (type, charge) = await LireMessageAsync(delai.Token).ConfigureAwait(false);
            if (type != TypeCommande) continue;
            var valeurs = Amf.LireTout(charge);
            NoterStatut(valeurs);
            if (valeurs.Count > 0 && Equals(valeurs[0], "_error"))
                throw new IOException("refus du serveur RTMP");
            if (voulu(valeurs)) return valeurs;
        }
    }

    private void NoterStatut(List<object?> valeurs)
    {
        if (valeurs.Count > 3 && Equals(valeurs[0], "onStatus") && valeurs[3] is Dictionary<string, object?> o
            && o.GetValueOrDefault("code") is string code)
            lock (Statuts) Statuts.Add(code);
    }

    private async Task LecteurAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, charge) = await LireMessageAsync(ct).ConfigureAwait(false);
                if (type == TypeCommande) NoterStatut(Amf.LireTout(charge));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Erreur = ex.Message;
        }
        catch (Exception)
        {
            // Fermeture demandee.
        }
    }

    /// <summary>Le message suivant du serveur, ses morceaux reassembles. La taille des morceaux et les pings se traitent ici.</summary>
    private async Task<(byte Type, byte[] Charge)> LireMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            var b0 = (await RecevoirAsync(1, ct).ConfigureAwait(false))[0];
            var fmt = b0 >> 6;
            var csid = b0 & 0x3F;
            if (csid == 0) csid = 64 + (await RecevoirAsync(1, ct).ConfigureAwait(false))[0];
            else if (csid == 1)
            {
                var x = await RecevoirAsync(2, ct).ConfigureAwait(false);
                csid = 64 + x[0] + x[1] * 256;
            }
            if (!_entrants.TryGetValue(csid, out var e)) _entrants[csid] = e = new Entrant();
            if (fmt <= 2)
            {
                var tb = await RecevoirAsync(3, ct).ConfigureAwait(false);
                var t = (uint)(tb[0] << 16 | tb[1] << 8 | tb[2]);
                if (fmt <= 1)
                {
                    var lb = await RecevoirAsync(4, ct).ConfigureAwait(false);
                    e.Longueur = lb[0] << 16 | lb[1] << 8 | lb[2];
                    e.Type = lb[3];
                }
                if (fmt == 0) e.Flux = BinaryPrimitives.ReadUInt32LittleEndian(await RecevoirAsync(4, ct).ConfigureAwait(false));
                e.Etendu = t == 0xFFFFFF;
                if (e.Etendu) t = BinaryPrimitives.ReadUInt32BigEndian(await RecevoirAsync(4, ct).ConfigureAwait(false));
                if (e.Tampon.Length == 0)
                {
                    if (fmt == 0) e.Temps = t;
                    else
                    {
                        e.Delta = t;
                        e.Temps += t;
                    }
                }
            }
            else
            {
                if (e.Etendu) await RecevoirAsync(4, ct).ConfigureAwait(false);
                if (e.Tampon.Length == 0) e.Temps += e.Delta;
            }
            var reste = e.Longueur - (int)e.Tampon.Length;
            if (reste > 0) e.Tampon.Write(await RecevoirAsync(Math.Min(reste, _morceauEntrant), ct).ConfigureAwait(false));
            if (e.Tampon.Length < e.Longueur) continue;
            var charge = e.Tampon.ToArray();
            e.Tampon = new MemoryStream();
            if (e.Type == TypeTailleMorceau && charge.Length >= 4)
            {
                _morceauEntrant = (int)(BinaryPrimitives.ReadUInt32BigEndian(charge) & 0x7FFFFFFF);
                continue;
            }
            if (e.Type == TypeControle && charge.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(charge) == 6)
            {
                // Ping du serveur : on repond.
                await EnvoyerAsync(2, TypeControle, 0, 0, [0, 7, .. charge.AsSpan(2, 4)], ct).ConfigureAwait(false);
                continue;
            }
            return (e.Type, charge);
        }
    }

    private async Task<byte[]> RecevoirAsync(int n, CancellationToken ct)
    {
        var tampon = new byte[n];
        var lus = 0;
        while (lus < n)
        {
            var r = await _flux!.ReadAsync(tampon.AsMemory(lus, n - lus), ct).ConfigureAwait(false);
            if (r <= 0) throw new IOException("connexion fermee par le serveur");
            lus += r;
        }
        return tampon;
    }

    private static byte[] BigEndian(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_flux is not null && _idDuFlux != 0)
            {
                using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await CommandeAsync(delai.Token, 0, 3, "FCUnpublish", 6.0, null, _cle).ConfigureAwait(false);
                await CommandeAsync(delai.Token, 0, 3, "deleteStream", 7.0, null, (double)_idDuFlux).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // La connexion est peut-etre deja fermee.
        }
        _fin.Cancel();
        try { _flux?.Dispose(); } catch (Exception) { /* deja ferme */ }
        try { _tcp?.Dispose(); } catch (Exception) { /* deja ferme */ }
        if (_lecteur is not null)
        {
            try { await _lecteur.ConfigureAwait(false); } catch (Exception) { /* fin du lecteur */ }
        }
        _fin.Dispose();
    }

    /// <summary>AMF0, ce qu'il en faut pour les commandes et les metadonnees.</summary>
    internal static class Amf
    {
        public static void Ecrire(Stream s, object? v)
        {
            switch (v)
            {
                case null:
                    s.WriteByte(0x05);
                    break;
                case bool b:
                    s.WriteByte(0x01);
                    s.WriteByte((byte)(b ? 1 : 0));
                    break;
                case string t:
                    s.WriteByte(0x02);
                    Chaine(s, t);
                    break;
                case Dictionary<string, object?> o:
                    s.WriteByte(0x03);
                    Proprietes(s, o);
                    break;
                default:
                    s.WriteByte(0x00);
                    Span<byte> d = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(d, Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
                    s.Write(d);
                    break;
            }
        }

        public static void EcrireTableau(Stream s, Dictionary<string, object?> o)
        {
            s.WriteByte(0x08);
            Span<byte> n = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(n, (uint)o.Count);
            s.Write(n);
            Proprietes(s, o);
        }

        private static void Proprietes(Stream s, Dictionary<string, object?> o)
        {
            foreach (var (k, v) in o)
            {
                Chaine(s, k);
                Ecrire(s, v);
            }
            s.Write([0, 0, 9]);
        }

        private static void Chaine(Stream s, string t)
        {
            var b = Encoding.UTF8.GetBytes(t);
            s.WriteByte((byte)(b.Length >> 8));
            s.WriteByte((byte)b.Length);
            s.Write(b);
        }

        public static List<object?> LireTout(byte[] b)
        {
            var sortie = new List<object?>();
            var i = 0;
            try
            {
                while (i < b.Length) sortie.Add(Lire(b, ref i));
            }
            catch (Exception)
            {
                // Une valeur que l'on ne sait pas lire termine la lecture : les commandes utiles sont avant.
            }
            return sortie;
        }

        private static object? Lire(byte[] b, ref int i)
        {
            var type = b[i++];
            switch (type)
            {
                case 0x00:
                    var d = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(i, 8));
                    i += 8;
                    return d;
                case 0x01:
                    return b[i++] != 0;
                case 0x02:
                    var n = b[i] << 8 | b[i + 1];
                    var t = Encoding.UTF8.GetString(b, i + 2, n);
                    i += 2 + n;
                    return t;
                case 0x03:
                case 0x08:
                    if (type == 0x08) i += 4;
                    var o = new Dictionary<string, object?>();
                    while (true)
                    {
                        var l = b[i] << 8 | b[i + 1];
                        i += 2;
                        if (l == 0 && b[i] == 0x09)
                        {
                            i++;
                            return o;
                        }
                        var k = Encoding.UTF8.GetString(b, i, l);
                        i += l;
                        o[k] = Lire(b, ref i);
                    }
                case 0x05:
                case 0x06:
                    return null;
                case 0x0A:
                    var c = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i, 4));
                    i += 4;
                    var liste = new List<object?>();
                    for (var k = 0; k < c; k++) liste.Add(Lire(b, ref i));
                    return liste;
                default:
                    throw new InvalidDataException("AMF0 " + type);
            }
        }
    }
}
