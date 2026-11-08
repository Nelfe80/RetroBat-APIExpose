using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RetroBat.Api.Infrastructure;

/// <summary>La regle d'empreinte d'une console, d'apres celle de RetroAchievements.</summary>
public enum RegleSansEnTete
{
    /// <summary>Le md5 du fichier fait foi (Mega Drive, Master System, Game Boy, arcade...).</summary>
    Aucune,
    /// <summary>NES et Famicom Disk System : l'en-tete iNES, NES 2.0 ou FDS (16 octets, « NES\x1a » ou « FDS\x1a »).</summary>
    Nes,
    /// <summary>Super Nintendo : l'en-tete de copieur (512 octets), quand la taille vaut 512 de plus qu'un multiple de 8 Kio.</summary>
    SuperNes,
    /// <summary>PC Engine : 512 octets d'en-tete quand le bit 512 de la taille est pose.</summary>
    PcEngine,
    /// <summary>Atari Lynx : l'en-tete « LYNX\0 » (64 octets).</summary>
    Lynx,
    /// <summary>Atari 7800 : l'en-tete « ATARI7800 » a l'octet 1 (128 octets).</summary>
    Atari7800,
    /// <summary>Nintendo 64 : remis dans l'ordre .z64 (le .v64 permute les octets par 2, le .n64 par 4).</summary>
    Nintendo64,
    /// <summary>Super Cassette Vision : l'en-tete « EmuSCV » (32 octets).</summary>
    SuperCassetteVision,
}

/// <summary>
/// L'EMPREINTE D'UNE ROM SANS SON EN-TETE (08/11, support de neogeoff : SMB3 et Mega Man 2 No-Intro refuses « pas la version
/// classee »). Decision du user : reconnaitre un jeu sans tenir compte de l'en-tete, comme RetroAchievements, sur toutes les
/// consoles qui en ont un. Les regles sont celles de rcheevos (src/hash/rc_hash_rom.c, commit eea6461f du 08/11/2026) :
/// <list type="bullet">
/// <item>NES, FDS (<c>rc_hash_nes</c>) : 16 octets si le fichier commence par « NES\x1a » ou « FDS\x1a » et en fait plus de 16.</item>
/// <item>Super Nintendo (<c>rc_hash_snes</c>) : 512 octets si taille - (taille / 0x2000) * 0x2000 == 512.</item>
/// <item>PC Engine et SuperGrafx (<c>rc_hash_pce</c>) : 512 octets si (taille &amp; 512) != 0, sans autre marque.</item>
/// <item>Lynx (<c>rc_hash_lynx</c>) : 64 octets si les 5 premiers valent « LYNX\0 » et que le fichier en fait plus de 64.</item>
/// <item>Atari 7800 (<c>rc_hash_7800</c>) : 128 octets si « ATARI7800 » est a l'octet 1 et que le fichier en fait plus de 128.</item>
/// <item>Super Cassette Vision (<c>rc_hash_scv</c>) : 32 octets si le fichier commence par « EmuSCV » et en fait plus de 32.</item>
/// <item>Nintendo 64 (<c>rc_hash_n64</c>) : premier octet 0x80 (z64) ou 0xE8 / 0x22 (ndd) tel quel, 0x37 (v64) permute par 2,
/// 0x40 (n64) permute par 4 ; tout autre premier octet n'est pas une ROM N64 (pas d'empreinte).</item>
/// </list>
/// Comme rcheevos, seuls les 64 premiers Mio comptent (MAX_BUFFER_SIZE) ; l'en-tete s'enleve dans cette fenetre.
/// L'ARCADE N'EST PAS CONCERNEE : son identite reste le set (decision du user). Une console sans regle garde le md5 du fichier.
/// </summary>
public static class EmpreinteSansEnTete
{
    /// <summary>MAX_BUFFER_SIZE de rcheevos : au-dela, seul le debut compte.</summary>
    public const long FenetreMaximale = 64L * 1024 * 1024;

    /// <summary>La regle d'un dossier de RetroBat (son systeme). Pur.</summary>
    public static RegleSansEnTete RegleDu(string? systemId) => (systemId ?? "").Trim().ToLowerInvariant() switch
    {
        "nes" or "famicom" or "fds" => RegleSansEnTete.Nes,
        "snes" or "sfc" or "snes-msu1" => RegleSansEnTete.SuperNes,
        "pcengine" or "tg16" or "supergrafx" => RegleSansEnTete.PcEngine,
        "lynx" => RegleSansEnTete.Lynx,
        "atari7800" => RegleSansEnTete.Atari7800,
        "n64" or "n64dd" => RegleSansEnTete.Nintendo64,
        "scv" => RegleSansEnTete.SuperCassetteVision,
        _ => RegleSansEnTete.Aucune,
    };

    /// <summary>Le md5 a la RetroAchievements d'un contenu en memoire, ou null (pas de regle, pas une ROM N64). Pur.</summary>
    public static string? Md5(RegleSansEnTete regle, ReadOnlySpan<byte> contenu)
    {
        using var flux = new MemoryStream(contenu.ToArray(), writable: false);
        return Empreintes(regle, flux, contenu.Length).SansEnTete;
    }

    /// <summary>
    /// En UNE lecture d'un flux de <paramref name="taille"/> octets : le md5 du fichier entier et le md5 a la RetroAchievements
    /// (null sans regle ou pour un contenu que la regle ne reconnait pas, une ROM N64 au premier octet inconnu). Rien n'est
    /// garde au-dela d'un tampon de 64 Kio : la borne est courte en memoire. Un flux plus court ou plus long qu'annonce ne donne
    /// rien (une archive qui ment sur sa taille).
    /// </summary>
    public static (string? Fichier, string? SansEnTete) Empreintes(RegleSansEnTete regle, Stream flux, long taille)
    {
        if (taille < 0) return (null, null);
        var fenetre = Math.Min(taille, FenetreMaximale);
        var tete = new byte[(int)Math.Min(taille, 512)];
        if (LireTout(flux, tete) != tete.Length) return (null, null);

        var (actif, saut, permutation) = Regle(regle, tete, taille, fenetre);
        using var entier = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sansEnTete = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var tampon = new byte[64 * 1024];
        tete.CopyTo(tampon, 0);
        var dansLeTampon = tete.Length;
        long position = 0;
        while (position < taille)
        {
            if (dansLeTampon == 0)
            {
                var voulu = (int)Math.Min(tampon.Length, taille - position);
                dansLeTampon = LireTout(flux, tampon.AsSpan(0, voulu));
                if (dansLeTampon != voulu) return (null, null);
            }
            var bloc = tampon.AsSpan(0, dansLeTampon);
            entier.AppendData(bloc);
            if (actif && position < fenetre)
            {
                var utile = bloc[..(int)Math.Min(bloc.Length, fenetre - position)];
                if (permutation != 0) Permuter(utile, permutation);
                var debut = (int)Math.Clamp(saut - position, 0, utile.Length);
                sansEnTete.AppendData(utile[debut..]);
            }
            position += dansLeTampon;
            dansLeTampon = 0;
        }
        // Rien de plus que la taille annoncee.
        if (flux.Read(tampon, 0, 1) != 0) return (null, null);
        var md5 = Convert.ToHexString(entier.GetHashAndReset()).ToLowerInvariant();
        return (md5, actif ? Convert.ToHexString(sansEnTete.GetHashAndReset()).ToLowerInvariant() : null);
    }

    /// <summary>Ce que la regle retient de ce contenu : s'il a une empreinte, l'en-tete a sauter, la permutation N64. Pur.</summary>
    private static (bool Actif, long Saut, int Permutation) Regle(RegleSansEnTete regle, byte[] tete, long taille, long fenetre)
    {
        switch (regle)
        {
            case RegleSansEnTete.Nes:
                return (true, taille > 16 && (Commence(tete, "NES\x1a"u8) || Commence(tete, "FDS\x1a"u8)) ? 16 : 0, 0);
            case RegleSansEnTete.SuperNes:
                // rcheevos calcule sur son tampon, deja borne a la fenetre.
                return (true, fenetre - (fenetre / 0x2000) * 0x2000 == 512 ? 512 : 0, 0);
            case RegleSansEnTete.PcEngine:
                return (true, (fenetre & 512) != 0 ? 512 : 0, 0);
            case RegleSansEnTete.Lynx:
                return (true, fenetre > 64 && Commence(tete, "LYNX\0"u8) ? 64 : 0, 0);
            case RegleSansEnTete.Atari7800:
                return (true, fenetre > 128 && tete.Length >= 10 && tete.AsSpan(1, 9).SequenceEqual("ATARI7800"u8) ? 128 : 0, 0);
            case RegleSansEnTete.SuperCassetteVision:
                return (true, fenetre > 32 && Commence(tete, "EmuSCV"u8) ? 32 : 0, 0);
            case RegleSansEnTete.Nintendo64:
                if (tete.Length == 0) return (false, 0, 0);
                return tete[0] switch
                {
                    0x80 or 0xE8 or 0x22 => (true, 0, 0),
                    0x37 => (true, 0, 2),
                    0x40 => (true, 0, 4),
                    _ => (false, 0, 0),
                };
            default:
                return (false, 0, 0);
        }
    }

    private static bool Commence(ReadOnlySpan<byte> tete, ReadOnlySpan<byte> marque)
        => tete.Length >= marque.Length && tete[..marque.Length].SequenceEqual(marque);

    /// <summary>
    /// Les mots entiers seulement, comme rcheevos sur une ROM de taille multiple de 4 (toutes les ROM N64). Les blocs commencent
    /// toujours a un multiple de 4 : le premier fait 512 octets, les suivants 64 Kio.
    /// </summary>
    private static void Permuter(Span<byte> bloc, int largeur)
    {
        for (var i = 0; i + 4 <= bloc.Length; i += 4)
        {
            var mot = bloc.Slice(i, 4);
            var valeur = BinaryPrimitives.ReadUInt32LittleEndian(mot);
            valeur = largeur == 2
                ? ((valeur & 0xFF00FF00u) >> 8) | ((valeur & 0x00FF00FFu) << 8)
                : BinaryPrimitives.ReverseEndianness(valeur);
            BinaryPrimitives.WriteUInt32LittleEndian(mot, valeur);
        }
    }

    private static int LireTout(Stream flux, Span<byte> cible)
    {
        var lu = 0;
        while (lu < cible.Length)
        {
            var n = flux.Read(cible[lu..]);
            if (n == 0) break;
            lu += n;
        }
        return lu;
    }
}
