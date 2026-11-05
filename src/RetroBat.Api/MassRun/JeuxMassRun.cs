using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.MassRun;

/// <summary>
/// LES JEUX DE MASS RUN (05/11 : Super Mario Bros. et Sonic The Hedgehog). Une salle du noeud nomme son jeu
/// (« systeme/rom_group ») ; la borne en tire le coeur hote, le contenu attendu (crc32, que le coeur verifie), la ROM, les
/// niveaux admis et l'etat de depart de chacun (l'etat d'epreuve « &lt;niveau&gt;-panneau.state » du Data Pack).
///
/// L'IMAGE DE PISTE (40 octets, relayee telle quelle par le noeud) a la forme NES (camera, scene, controle, sprites). Sur
/// Mega Drive le personnage n'est pas fait de sprites lisibles dans la memoire du jeu : le coeur y range un seul « sprite »
/// qui porte la position et la pose, et que le noeud compacte comme les autres (sa pose = le numero d'image du personnage) :
/// <code>
///   0  x dans le niveau (u16)       2  zone, acte, 0       5  0       6  1 (0 : pas dessine)       7  0
///   8  y, octet bas                 9  image du personnage (son numero dans le jeu)
///  10  drapeaux (bit 0 retourne a l'horizontale, bit 1 a la verticale)              11  y, octet haut
/// </code>
/// </summary>
public static class JeuxMassRun
{
    public sealed record Jeu(string Id, string Systeme, string RomGroup, uint Crc, string Coeur, string RomParDefaut, Regex Niveaux);

    public static readonly IReadOnlyList<Jeu> Tous =
    [
        new("nes/super-mario-bros", "nes", "super-mario-bros", 0x393a432f, "fceumm_ghosts", "Super Mario Bros. (World).zip",
            new Regex("^[1-8]-[1-4]$", RegexOptions.CultureInvariant)),
        new("megadrive/sonic-the-hedgehog", "megadrive", "sonic-the-hedgehog", 0xf9394e97, "genesis_plus_gx_ghosts",
            "Sonic The Hedgehog (USA, Europe).zip",
            new Regex("^((green-hill|marble|spring-yard|labyrinth|star-light|scrap-brain)-[1-3]|final-zone)$", RegexOptions.CultureInvariant)),
    ];

    public static Jeu? Trouver(string id) => Tous.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le jeu d'une salle d'un noeud plus ancien, qui ne nommait que Super Mario Bros.</summary>
    public static Jeu ParDefaut => Tous[0];

    public static bool FormatMegaDrive(string jeu) => jeu.StartsWith("megadrive/", StringComparison.OrdinalIgnoreCase);

    public static string CheminDeLEtat(Jeu jeu, string niveau, string? racine = null)
        => Path.Combine(racine ?? RetroBat.Api.Speedrun.EtatsDesDefis.Racine, jeu.Systeme, jeu.RomGroup, niveau + "-panneau.state");

    /// <summary>Le niveau tel qu'on le dit : « 1-1 » reste, « green-hill-2 » devient « Green Hill 2 ». Pur.</summary>
    public static string NomDuNiveau(string niveau)
    {
        if (string.IsNullOrEmpty(niveau) || Regex.IsMatch(niveau, "^[0-9]")) return niveau ?? "";
        return string.Join(' ', niveau.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => char.ToUpper(m[0], CultureInfo.InvariantCulture) + m[1..]));
    }

    /// <summary>
    /// Le crc32 du contenu tel que le coeur le calcule : le fichier lui-meme, ou l'unique jeu d'une archive .zip (le crc de
    /// son entree). Null si illisible ou si l'archive en porte plusieurs.
    /// </summary>
    public static uint? ContenuDe(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return null;
            if (chemin.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(chemin);
                var entrees = zip.Entries.Where(e => e.Length > 0).ToList();
                return entrees.Count == 1 ? entrees[0].Crc32 : null;
            }
            return Crc32(File.ReadAllBytes(chemin));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly uint[] TableCrc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Le crc32 (zip, png) d'un contenu. Pur.</summary>
    public static uint Crc32(ReadOnlySpan<byte> donnees)
    {
        var c = 0xFFFFFFFFu;
        foreach (var o in donnees) c = TableCrc[(c ^ o) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    public static int XMegaDrive(ReadOnlySpan<byte> image) => BinaryPrimitives.ReadUInt16LittleEndian(image);

    public static int YMegaDrive(ReadOnlySpan<byte> image) => image[8] | (image[11] << 8);

    public static void PlacerMegaDrive(Span<byte> image, int x, int y)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(image, (ushort)Math.Clamp(x, 0, ushort.MaxValue));
        y = Math.Clamp(y, 0, ushort.MaxValue);
        image[8] = (byte)y;
        image[11] = (byte)(y >> 8);
    }
}
