using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RetroBat.Api.MassRun;

/// <summary>
/// LE PROTOCOLE MASS RUN (CDC MASS RUN §9, lot L3), copie de NelfePlay-Node/src/NelfeNode/MassRunProtocole.cs (fichier
/// identique a l'espace de noms pres). Paquets UDP, petit-boutistes :
/// <code>
///   0  'M' (0x4D), version (1), type, drapeaux
///   4  salle (u32)
///   8  place du joueur (u16), sequence (u16)
///  12  contenu
///  ..  HMAC-SHA256 du tout, tronque a 8 octets, avec la cle de session du joueur
/// </code>
/// Types : 1 POS (borne : image de course u32, mode u8, 3 reserves, image de piste 40), 2 PING (heure d'envoi i64 en
/// microsecondes Unix), 3 PONG (heure renvoyee i64, heure du noeud i64), 4 ARRIVEE (images u32), 11 INSTANTANE compact.
///
/// L'INSTANTANE COMPACT (CDC §9.4-9.6) : la silhouette d'un coureur (ses 8 sprites, positions relatives au premier) est une
/// POSE du dictionnaire de la salle, envoyee en entier a chaque borne jusqu'a trois fois, puis par son numero.
/// <code>
///   image de base (u32), nombre de coureurs (u8), nombre de poses definies (u8)
///   poses : numero (u16), nombre de sprites (u8), puis par sprite dx (i8), dy (i8), motif, attributs
///   coureurs (13 octets) : place (u16), retard sur l'image de base (u8), camera (u16), scene (3), controle (u8),
///                          x et y du premier sprite (u8, u8), pose (u16)
/// </code>
/// </summary>
public static class MassRunProtocole
{
    public const byte Signature = 0x4D;
    public const byte Version = 1;
    public const int Entete = 12;
    public const int Sceau = 8;
    public const int ImageDePiste = 40;
    public const int EntreeInstantane = 2 + 4 + ImageDePiste;
    public const int MaxParInstantane = 24;

    public const byte Pos = 1, Ping = 2, Pong = 3, Arrivee = 4, Instantane = 11;
    public const int EntreeCompacte = 13;

    /// <summary>La pose d'une image de piste : ses sprites relatifs au premier (dx, dy, motif, attributs).</summary>
    public static byte[] Pose(ReadOnlySpan<byte> image)
    {
        var n = Math.Min((int)image[6], 8);
        var pose = new byte[1 + n * 4];
        pose[0] = (byte)n;
        if (n == 0) return pose;
        int x0 = image[8 + 3], y0 = image[8];
        for (var e = 0; e < n; e++)
        {
            var s = image.Slice(8 + e * 4, 4);
            pose[1 + e * 4] = (byte)(sbyte)(s[3] - x0);
            pose[2 + e * 4] = (byte)(sbyte)(s[0] - y0);
            pose[3 + e * 4] = s[1];
            pose[4 + e * 4] = s[2];
        }
        return pose;
    }

    /// <summary>L'image de piste (40 octets) refaite d'une entree compacte et de sa pose.</summary>
    public static void Refaire(ReadOnlySpan<byte> entree, ReadOnlySpan<byte> pose, Span<byte> image)
    {
        image.Clear();
        entree.Slice(3, 2).CopyTo(image);          // camera
        entree.Slice(5, 3).CopyTo(image[2..]);     // scene
        image[5] = entree[8];                      // controle
        var n = Math.Min((int)pose[0], 8);
        image[6] = (byte)n;
        int x0 = entree[9], y0 = entree[10];
        for (var e = 0; e < n; e++)
        {
            var s = image.Slice(8 + e * 4, 4);
            s[0] = (byte)(y0 + (sbyte)pose[2 + e * 4]);
            s[1] = pose[3 + e * 4];
            s[2] = pose[4 + e * 4];
            s[3] = (byte)(x0 + (sbyte)pose[1 + e * 4]);
        }
    }

    public static int Ecrire(Span<byte> paquet, byte type, uint salle, ushort place, ushort sequence, ReadOnlySpan<byte> contenu, byte[] cle)
    {
        paquet[0] = Signature;
        paquet[1] = Version;
        paquet[2] = type;
        paquet[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(paquet[4..], salle);
        BinaryPrimitives.WriteUInt16LittleEndian(paquet[8..], place);
        BinaryPrimitives.WriteUInt16LittleEndian(paquet[10..], sequence);
        contenu.CopyTo(paquet[Entete..]);
        var n = Entete + contenu.Length;
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(cle, paquet[..n], mac);
        mac[..Sceau].CopyTo(paquet[n..]);
        return n + Sceau;
    }

    /// <summary>L'en-tete d'un paquet, sans verifier le sceau (il faut la cle de la place pour cela).</summary>
    public static bool Lire(ReadOnlySpan<byte> paquet, out byte type, out uint salle, out ushort place, out ushort sequence)
    {
        type = 0; salle = 0; place = 0; sequence = 0;
        if (paquet.Length < Entete + Sceau || paquet[0] != Signature || paquet[1] != Version)
        {
            return false;
        }
        type = paquet[2];
        salle = BinaryPrimitives.ReadUInt32LittleEndian(paquet[4..]);
        place = BinaryPrimitives.ReadUInt16LittleEndian(paquet[8..]);
        sequence = BinaryPrimitives.ReadUInt16LittleEndian(paquet[10..]);
        return true;
    }

    public static bool SceauValide(ReadOnlySpan<byte> paquet, byte[] cle)
    {
        var n = paquet.Length - Sceau;
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(cle, paquet[..n], mac);
        return CryptographicOperations.FixedTimeEquals(mac[..Sceau], paquet[n..]);
    }

    public static ReadOnlySpan<byte> Contenu(ReadOnlySpan<byte> paquet) => paquet[Entete..^Sceau];

    /// <summary>Microsecondes depuis l'epoque Unix : l'horloge des PING et PONG.</summary>
    public static long Maintenant() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    /// <summary>La position dans le niveau d'une image de piste : camera + x du premier sprite (pour trier les coureurs).</summary>
    public static int XDansLeNiveau(ReadOnlySpan<byte> image)
        => BinaryPrimitives.ReadUInt16LittleEndian(image) + (image[6] > 0 ? image[8 + 3] : 0);

    /// <summary>La scene d'une image de piste (monde, niveau, zone), en un entier.</summary>
    public static int Scene(ReadOnlySpan<byte> image) => image[2] | (image[3] << 8) | (image[4] << 16);
}
