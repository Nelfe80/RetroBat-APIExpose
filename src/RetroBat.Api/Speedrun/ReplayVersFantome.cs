using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// UN REPLAY BSV v2 DE RETROARCH 1.22 EN FANTOME .nghost (NelfePlay-Ghosts, docs/FORMAT.md ; CDC MASS RUN §14.2). Le replay porte
/// son etat de depart (sans compression, zlib ou zstd ; brut ou « statestream » : blocs, superblocs et sequence en msgpack),
/// puis les entrees de chaque image. Un fantome se rejoue par ses seules entrees depuis cet etat : les checkpoints suivants
/// sont sautes. Pur : des octets en entree, des octets en sortie.
/// </summary>
public static class ReplayVersFantome
{
    private const uint MagicBsv = 0x42535632;

    /// <summary>Le .nghost d'un replay (gzip accepte). <paramref name="decalage"/> : voir docs/FORMAT.md.</summary>
    public static byte[] Convertir(byte[] replay, int decalage = 0)
    {
        if (replay.Length > 2 && replay[0] == 0x1f && replay[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(replay), CompressionMode.Decompress);
            using var brut = new MemoryStream();
            gz.CopyTo(brut);
            replay = brut.ToArray();
        }

        var r = new Lecteur(replay);
        if (r.U32() != MagicBsv || r.U32() != 2) throw new InvalidDataException("BSV v2 attendu");
        var crc = r.U32();
        var tailleEtat = r.U32();
        r.Sauter(8);
        r.U32();   // images declarees
        var octetsParBloc = (int)r.U32();
        var blocsParSuperbloc = (int)r.U32();
        r.U32();   // configuration
        var debut = r.Position;
        var etat = LireLEtat(r, octetsParBloc, blocsParSuperbloc);
        if (r.Position - debut != tailleEtat) throw new InvalidDataException("etendue de l'etat de depart");

        using var images = new MemoryStream();
        using var ecriture = new BinaryWriter(images);
        uint nombre = 0;
        while (!r.Fini)
        {
            r.Sauter(4);                 // renvoi arriere
            r.Sauter(r.U8() * 12);       // touches du clavier
            // RetroArch note chaque lecture d'une touche : plusieurs fois la meme dans une image. La derniere vaut.
            var entrees = new SortedDictionary<(byte Port, byte Device, byte Index, ushort Id), short>();
            var n = r.U16();
            for (var i = 0; i < n; i++)
            {
                var (port, device, index) = (r.U8(), r.U8(), r.U8());
                r.U8();
                var id = r.U16();
                entrees[(port, device, index, id)] = (short)r.U16();
            }

            ecriture.Write((ushort)entrees.Count);
            foreach (var (cle, valeur) in entrees)
            {
                ecriture.Write(cle.Port);
                ecriture.Write(cle.Device);
                ecriture.Write(cle.Index);
                ecriture.Write((byte)0);
                ecriture.Write(cle.Id);
                ecriture.Write(valeur);
            }

            nombre++;
            var jeton = (char)r.U8();
            if (jeton == 'c') r.Sauter(checked((int)r.U64()));
            else if (jeton == 'C') SauterLeCheckpoint(r);
            else if (jeton != 'f') throw new InvalidDataException("jeton d'image inconnu");
        }

        ecriture.Flush();
        using var sortie = new MemoryStream();
        using var w = new BinaryWriter(sortie);
        w.Write(Encoding.ASCII.GetBytes("NGHOST1\0"));
        w.Write(crc);
        w.Write(decalage);
        w.Write((uint)etat.Length);
        w.Write(etat);
        w.Write(nombre);
        w.Write(images.GetBuffer(), 0, (int)images.Length);
        w.Flush();
        return sortie.ToArray();
    }

    private static void SauterLeCheckpoint(Lecteur r)
    {
        r.Sauter(2 + 4 + 4);
        r.Sauter(checked((int)r.U32()));
    }

    private static byte[] LireLEtat(Lecteur r, int octetsParBloc, int blocsParSuperbloc)
    {
        var compression = r.U8();
        var encodage = r.U8();
        var taille = checked((int)r.U32());
        r.U32();   // taille encodee
        var stocke = checked((int)r.U32());
        var donnees = r.Lire(stocke);
        donnees = compression switch
        {
            0 => donnees,
            1 => Zlib(donnees),
            2 => Zstd(donnees),
            _ => throw new InvalidDataException("compression inconnue"),
        };
        return encodage switch
        {
            0 => donnees.AsSpan(0, Math.Min(taille, donnees.Length)).ToArray(),
            1 => Statestream(donnees, taille, octetsParBloc, blocsParSuperbloc),
            _ => throw new InvalidDataException("encodage inconnu"),
        };
    }

    private static byte[] Zlib(byte[] donnees)
    {
        using var z = new ZLibStream(new MemoryStream(donnees), CompressionMode.Decompress);
        using var brut = new MemoryStream();
        z.CopyTo(brut);
        return brut.ToArray();
    }

    private static byte[] Zstd(byte[] donnees)
    {
        using var d = new ZstdSharp.Decompressor();
        return d.Unwrap(donnees).ToArray();
    }

    /// <summary>L'etat « statestream » : les blocs, les superblocs, puis la sequence de superblocs qui le recompose.</summary>
    private static byte[] Statestream(byte[] encode, int taille, int octetsParBloc, int blocsParSuperbloc)
    {
        var r = new Lecteur(encode);
        if (Msgpack(r) is not 0L) throw new InvalidDataException("jeton de debut statestream");
        Msgpack(r);   // compteur d'images
        // Chaque index commence par un objet de zeros a l'index 0, jamais transmis.
        var blocs = new Dictionary<long, byte[]> { [0] = new byte[octetsParBloc] };
        var superblocs = new Dictionary<long, long[]> { [0] = new long[blocsParSuperbloc] };
        var sortie = new byte[taille];
        while (!r.Fini)
        {
            var jeton = (long)Msgpack(r)!;
            if (jeton == 1)
            {
                var index = (long)Msgpack(r)!;
                var bloc = (byte[])Msgpack(r)!;
                if (bloc.Length != octetsParBloc) throw new InvalidDataException("taille de bloc");
                blocs[index] = bloc;
            }
            else if (jeton == 2)
            {
                var index = (long)Msgpack(r)!;
                superblocs[index] = ((List<object?>)Msgpack(r)!).Select(v => (long)v!).ToArray();
            }
            else if (jeton == 3)
            {
                var sequence = ((List<object?>)Msgpack(r)!).Select(v => (long)v!).ToArray();
                var tailleSuper = (long)octetsParBloc * blocsParSuperbloc;
                for (var i = 0; i < sequence.Length; i++)
                {
                    var sb = superblocs[sequence[i]];
                    for (var j = 0; j < sb.Length; j++)
                    {
                        var a = Math.Min(i * tailleSuper + (long)j * octetsParBloc, taille);
                        var b = Math.Min(a + octetsParBloc, taille);
                        if (b <= a) break;
                        Array.Copy(blocs[sb[j]], 0, sortie, a, b - a);
                    }
                }

                return sortie;
            }
            else
            {
                throw new InvalidDataException("jeton statestream inconnu");
            }
        }

        throw new InvalidDataException("statestream sans sequence");
    }

    /// <summary>Une valeur msgpack : entiers, binaires, tableaux (ce que le statestream emploie).</summary>
    private static object? Msgpack(Lecteur r)
    {
        var t = r.U8();
        if (t <= 0x7f) return (long)t;
        if (t is >= 0x90 and <= 0x9f) return Tableau(r, t & 0x0f);
        if (t >= 0xe0) return (long)(t - 0x100);
        return t switch
        {
            0xcc => (long)r.U8(),
            0xcd => (long)r.U16Be(),
            0xce => (long)r.U32Be(),
            0xcf => (long)r.U64Be(),
            0xd0 => (long)(sbyte)r.U8(),
            0xd1 => (long)(short)r.U16Be(),
            0xd2 => (long)(int)r.U32Be(),
            0xd3 => (long)r.U64Be(),
            0xc4 => r.Lire(r.U8()),
            0xc5 => r.Lire(r.U16Be()),
            0xc6 => r.Lire(checked((int)r.U32Be())),
            0xdc => Tableau(r, r.U16Be()),
            0xdd => Tableau(r, checked((int)r.U32Be())),
            _ => throw new InvalidDataException($"msgpack 0x{t:x2} non pris en charge"),
        };
    }

    private static List<object?> Tableau(Lecteur r, int n)
    {
        var liste = new List<object?>(n);
        for (var i = 0; i < n; i++) liste.Add(Msgpack(r));
        return liste;
    }

    private sealed class Lecteur(byte[] d)
    {
        public int Position { get; private set; }

        public bool Fini => Position >= d.Length;

        public byte[] Lire(int n)
        {
            if (n < 0 || Position + n > d.Length) throw new InvalidDataException("replay tronque");
            var b = d.AsSpan(Position, n).ToArray();
            Position += n;
            return b;
        }

        public void Sauter(int n)
        {
            if (n < 0 || Position + n > d.Length) throw new InvalidDataException("replay tronque");
            Position += n;
        }

        public byte U8() => Lire(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Lire(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Lire(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Lire(8));

        public ushort U16Be() => BinaryPrimitives.ReadUInt16BigEndian(Lire(2));

        public uint U32Be() => BinaryPrimitives.ReadUInt32BigEndian(Lire(4));

        public ulong U64Be() => BinaryPrimitives.ReadUInt64BigEndian(Lire(8));
    }
}
