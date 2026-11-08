using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.MassRun;

/// <summary>
/// LES JEUX DE MASS RUN (05/11 : Super Mario Bros. et Sonic The Hedgehog ; 06/11 : Super Metroid, un segment par boss). Une salle du noeud nomme son jeu
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
/// Sur SNES (06/11) la meme forme, mais la pose est une IMAGE (les motifs de Samus sont en memoire video, une autre borne ne
/// les a pas) : la piste porte l'EMPREINTE de cette image (24 bits), et l'image passe une fois par le dictionnaire de la salle
/// du noeud (<see cref="ClientDeSalle.EnvoyerLesPosesAsync"/>, <see cref="ClientDeSalle.ChercherLesPosesAsync"/>) :
/// <code>
///   0  x dans la salle (u16)        2  salle du jeu $079B (u16)        4  0        5  empreinte, bits 16-23        6  1
///   8  y, octet bas                 9  empreinte, bits 0-7             10  empreinte, bits 8-15       11  y, octet haut
/// </code>
/// </summary>
public static class JeuxMassRun
{
    /// <summary>
    /// Un jeu de MASS RUN. <c>SuffixeDeLEtat</c> : l'etat d'epreuve d'un niveau s'appelle « &lt;niveau&gt;&lt;suffixe&gt;.state » ;
    /// <c>ImagesParSeconde</c> : la cadence du coeur (le temps de course se compte en images) ; <c>PosesEnImages</c> : la pose
    /// de la piste est l'empreinte d'une image qui passe par le dictionnaire de la salle (SNES).
    /// </summary>
    public sealed record Jeu(string Id, string Systeme, string RomGroup, uint Crc, string Coeur, string RomParDefaut, Regex Niveaux,
        string SuffixeDeLEtat = "-panneau", double ImagesParSeconde = 60.0988, bool PosesEnImages = false);

    public static readonly IReadOnlyList<Jeu> Tous =
    [
        new("nes/super-mario-bros", "nes", "super-mario-bros", 0x393a432f, "fceumm_ghosts", "Super Mario Bros. (World).zip",
            new Regex("^[1-8]-[1-4]$", RegexOptions.CultureInvariant)),
        new("megadrive/sonic-the-hedgehog", "megadrive", "sonic-the-hedgehog", 0xf9394e97, "genesis_plus_gx_ghosts",
            "Sonic The Hedgehog (USA, Europe).zip",
            new Regex("^((green-hill|marble|spring-yard|labyrinth|star-light|scrap-brain)-[1-3]|final-zone)$", RegexOptions.CultureInvariant),
            ImagesParSeconde: 59.922743),
        // Super Metroid : un segment par boss, de la porte du boss a sa mort (les etats d'epreuve « kraid.state », ...)
        new("snes/super-metroid", "snes", "super-metroid", 0xd63ed5f8, "snes9x_ghosts", "Super Metroid (Japan, USA) (En,Ja).zip",
            new Regex("^(kraid|phantoon|draygon|ridley|mother-brain)$", RegexOptions.CultureInvariant),
            SuffixeDeLEtat: "", ImagesParSeconde: 60.098812, PosesEnImages: true),
        // (06/11, jour) Super Mario World (USA) : les niveaux de Yoshi's Island et de Donut Plains, du chargement du niveau a
        // l'arrivee (etats d'epreuve « yi2-entree.state », ...) ; meme coeur hote, poses en images comme Super Metroid
        new("snes/super-mario-world", "snes", "super-mario-world", 0xb19ed489, "snes9x_ghosts", "Super Mario World (USA).zip",
            new Regex("^(yi[1-4]|dp[134]|ds1)$", RegexOptions.CultureInvariant),
            SuffixeDeLEtat: "-entree", ImagesParSeconde: 60.098812, PosesEnImages: true),
        // Sonic 2 (06/11, ouverture preparee le 07/11), version (World) : le coeur lit son profil dans
        // bios/nelfeplay/profils/24ab4c3a.ini (Data Pack). Les actes a panneau et a capsule et le Death Egg, arrivees vues au
        // banc ; ni Wing Fortress (arrivee non vue, retiree) ni Sky Chase (pilotage automatique, aucune epreuve).
        new("megadrive/sonic-the-hedgehog-2", "megadrive", "sonic-the-hedgehog-2", 0x24ab4c3a, "genesis_plus_gx_ghosts",
            "Sonic The Hedgehog 2 (World).zip",
            new Regex("^((emerald-hill|chemical-plant|aquatic-ruin|casino-night|hill-top|mystic-cave|oil-ocean)-[12]|metropolis-[1-3]|death-egg)$",
                RegexOptions.CultureInvariant),
            ImagesParSeconde: 59.922743),
        // Rocket Knight Adventures (06/11, ouverture preparee le 07/11), version USA : profil du coeur en fichier
        // (a6efec47.ini). Le stage 1 seulement : le coeur prend l'arrivee quand l'etat du jeu $B029 tombe de 3 a 0 et une
        // mort quand il passe a 4 ; a la fin des stages 3, 4 et 7 (banc du 07/11) il passe par 4 (3 -> 4 -> 5 -> 0) une image
        // apres le changement de scene : le coureur y serait compte mort. Stages 2, 5, 6 : arrivee non vue.
        new("megadrive/rocket-knight-adventures", "megadrive", "rocket-knight-adventures", 0xa6efec47, "genesis_plus_gx_ghosts",
            "Rocket Knight Adventures (USA).zip", new Regex("^stage-1$", RegexOptions.CultureInvariant),
            ImagesParSeconde: 59.922743),
    ];

    /// <summary>
    /// L'arrivee d'un segment de Super Metroid, comme ses reglements (ch-sm-*-v1) : l'octet du drapeau du boss vaut sa valeur
    /// (Kraid D829 = 1, Phantoon D82B = 1, Draygon D82C = 3, Ridley D82A = 1), ou l'etat du jeu $0998 vaut 0x26 (fin de la
    /// fuite apres Mother Brain). Null : le coeur suit sa propre regle (NES, Mega Drive). Pur.
    /// </summary>
    public static (uint Adresse, uint Valeur)? ArriveeDuNiveau(Jeu jeu, string niveau) => jeu.Id switch
    {
        "snes/super-metroid" => niveau switch
        {
            "kraid" => (0xD829u, 0x01u),
            "phantoon" => (0xD82Bu, 0x01u),
            "draygon" => (0xD82Cu, 0x03u),
            "ridley" => (0xD82Au, 0x01u),
            "mother-brain" => (0x0998u, 0x26u),
            _ => null,
        },
        // Super Mario World : $1493 (minuterie de fin de niveau) prend 0xFF a l'image du ruban d'arrivee (banc du 06/11)
        "snes/super-mario-world" => (0x1493u, 0xFFu),
        _ => null,
    };

    public static Jeu? Trouver(string id) => Tous.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le jeu d'une salle d'un noeud plus ancien, qui ne nommait que Super Mario Bros.</summary>
    public static Jeu ParDefaut => Tous[0];

    public static bool FormatMegaDrive(string jeu) => jeu.StartsWith("megadrive/", StringComparison.OrdinalIgnoreCase);

    /// <summary>L'image de piste porte un seul « sprite » (position sur deux octets, pose aux octets 9 et 10) : Mega Drive et
    /// SNES.</summary>
    public static bool FormatAUnSprite(string jeu) => FormatMegaDrive(jeu) || jeu.StartsWith("snes/", StringComparison.OrdinalIgnoreCase);

    /// <summary>L'empreinte de la pose d'une image de piste SNES (24 bits) ; 0 si rien n'est dessine. Pur.</summary>
    public static uint EmpreinteDeLaPose(ReadOnlySpan<byte> image)
        => image.Length < 11 || image[6] == 0 ? 0u : image[9] | ((uint)image[10] << 8) | ((uint)image[5] << 16);

    public static string CheminDeLEtat(Jeu jeu, string niveau, string? racine = null)
        => Path.Combine(racine ?? RetroBat.Api.Speedrun.EtatsDesDefis.Racine, jeu.Systeme, jeu.RomGroup, niveau + jeu.SuffixeDeLEtat + ".state");

    /// <summary>Le niveau tel qu'on le dit : « 1-1 » reste, « green-hill-2 » devient « Green Hill 2 ». Pur.</summary>
    public static string NomDuNiveau(string niveau)
    {
        if (string.IsNullOrEmpty(niveau) || Regex.IsMatch(niveau, "^[0-9]")) return niveau ?? "";
        return string.Join(' ', niveau.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => char.ToUpper(m[0], CultureInfo.InvariantCulture) + m[1..]));
    }

    /// <summary>
    /// Le crc32 du contenu tel que le coeur le calcule : le fichier lui-meme, ou l'unique jeu d'une archive .zip ou .7z (le crc
    /// que l'archive declare pour son entree, sinon celui de son contenu). Null si illisible, au-dela de 64 Mo ou si l'archive
    /// en porte plusieurs. Le coeur hote des fantomes reconnait son jeu a ce crc, EN-TETE COMPRIS.
    /// </summary>
    public static uint? ContenuDe(string chemin)
    {
        try
        {
            using var contenu = RetroBat.Api.Infrastructure.ContenuDUneRom.Ouvrir(chemin, 64L * 1024 * 1024);
            if (contenu is null) return null;
            if (contenu.Crc32 is { } declare) return declare;
            var c = 0xFFFFFFFFu;
            var tampon = new byte[64 * 1024];
            int lu;
            while ((lu = contenu.Flux.Read(tampon, 0, tampon.Length)) > 0)
            {
                foreach (var o in tampon.AsSpan(0, lu)) c = TableCrc[(c ^ o) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }
        catch (Exception e) when (RetroBat.Api.Infrastructure.ContenuDUneRom.EstUneLectureRatee(e))
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
