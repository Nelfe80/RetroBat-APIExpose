namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// UN NIVEAU SIMULE de DoDonPachi DaiOuJou pour le canal (essais sans jeu, jamais le jeu) : la distance monte d'un pas toutes les
/// 9 images, le vaisseau balaie l'ecran, des ennemis entrent par le haut, descendent et DISPARAISSENT (detruits), un boss arrive a la
/// fin et perd de la vie, la jauge du combo se recharge et se vide. Les valeurs sont ecrites en BRUT, comme le jeu (1/64 de pixel,
/// ecran tourne), pour que la conversion de l'API soit elle aussi essayee. Les memes noms que le .VIEW du labo du 22/11
/// (<c>dodonpachi-daioujou.VIEW</c>) : ses 12 champs ; tables objets (type, vie), affichage (drapeau, ay, ax) et tirs (btype, by, bx).
/// </summary>
public static class CanalScenarioSimule
{
    public static readonly string[] Champs =
        ["temps", "distance", "defil_fin", "camera", "niveau", "vies", "vaisseau_x", "vaisseau_y", "mort_anim", "invincible", "combo", "chaine_bcd"];

    public const int Temps = 0, Distance = 1, DefilFin = 2, Camera = 3, Niveau = 4, Vies = 5, VaisseauX = 6, VaisseauY = 7, Combo = 10;

    public static CanalEcrivainSimule.Table[] Tables() =>
    [
        new("objets", 48, 64, ["type", "vie"]),
        new("affichage", 48, 64, ["drapeau", "ay", "ax"]),
        new("tirs", 66, 64, ["btype", "by", "bx"]),
    ];

    public static CanalEcrivainSimule DaiOuJou(uint pid, string nomVue = "dodonpachi-daioujou") => new(nomVue, Champs, Tables(), pid);

    /// <summary>Longueur du niveau simule en images : au-dela, il recommence (nouvelle epoch).</summary>
    public const int Boucle = 9 * 560;

    /// <summary>Le drapeau d'une case dessinee (bit 0x2000), et celui d'une case qui ne l'est pas.</summary>
    public const int Dessine = 0xA200, NonDessine = 0x8200;

    private static readonly int[] Types = [0x1101, 0x8200, 0x2500, 0x4D00, 0x8B01, 0x2900];

    /// <summary>Le brut du vaisseau (vaisseau_x, vaisseau_y) pour un point d'ecran : x = vx / 64, y = 456 - vy / 64.</summary>
    public static (int Vx, int Vy) BrutDuVaisseau(int x, int y) => (x * 64, (456 - y) * 64);

    /// <summary>Le brut d'une case d'affichage (ay, ax) pour un point d'ecran : x = ax / 64, y = 447 - ay / 64.</summary>
    public static (int Ay, int Ax) BrutDeLAffichage(int x, int y) => ((447 - y) * 64, x * 64);

    /// <summary>Les ennemis du niveau simule a l'image <paramref name="i"/> : emplacement, type, x et y d'ecran, vie.</summary>
    public static IEnumerable<(int Slot, int Type, int X, int Y, int Vie)> Ennemis(int i)
    {
        i %= Boucle;
        for (var j = 0; j < 40; j++)
        {
            var naissance = 60 + 70 * j;
            var age = i - naissance;
            if (age < 0 || age > 330) continue;
            if (j % 7 == 3 && age > 120) continue;           // detruit en vol : disparait de la table
            var y = -16 + (int)(age * 1.45);
            var x = 28 + (j * 53) % 168 + (int)(22 * Math.Sin(age / 30.0 + j));
            yield return (j % 40, Types[j % Types.Length], x, y, 1 + (j % 3) * 4);
        }

        // le boss : de l'image 4600 a la fin, au milieu en haut, qui perd de la vie
        if (i >= 4600)
            yield return (41, 0x8901, 112 + (int)(40 * Math.Sin(i / 50.0)), 90, Math.Max(1, 4000 - (i - 4600) * 3));
    }

    /// <summary>L'image <paramref name="i"/> du niveau simule, au format de l'ecrivain.</summary>
    public static void Ecrire(CanalEcrivainSimule e, int i)
    {
        var im = i % Boucle;
        var distance = Math.Min(525, im / 9);
        var sx = 112 + (int)(84 * Math.Sin(i / 90.0));
        var sy = 380 + (int)(24 * Math.Sin(i / 55.0));
        var (vx, vy) = BrutDuVaisseau(sx, sy);
        var champs = new int[Champs.Length];
        champs[Temps] = im;
        champs[Distance] = distance;
        champs[DefilFin] = (im * 32) & 0x7FF;
        champs[Camera] = (int)(32 * 64 * Math.Sin(i / 120.0));   // s16 / 64 = pixels
        champs[Niveau] = 1;
        champs[Vies] = 2;
        champs[VaisseauX] = vx;
        champs[VaisseauY] = vy;
        champs[Combo] = Math.Max(0, 56 - im % 90);               // jauge : rechargee a 56, -1 par image
        champs[11] = (im / 90) % 100;                            // chaine_bcd (sans importance pour l'API)
        var objets = new List<int[]>();
        var affichage = new List<int[]>();
        foreach (var (slot, type, x, y, vie) in Ennemis(i))
        {
            objets.Add([slot, type, vie]);
            var (ay, ax) = BrutDeLAffichage(x, y);
            affichage.Add([slot, Dessine, ay, ax]);
        }

        // jamais des ennemis : un effet dessine SANS objet (case libre), une case NON dessinee, un objet du decor de vie nulle
        affichage.Add([44, Dessine, 200 * 64, 100 * 64]);
        affichage.Add([45, NonDessine, 180 * 64, 120 * 64]);
        objets.Add([46, 0x0500, 0]);
        affichage.Add([46, Dessine, 160 * 64, 60 * 64]);
        objets.Sort((a, b) => a[0].CompareTo(b[0]));
        affichage.Sort((a, b) => a[0].CompareTo(b[0]));

        // quelques tirs (table lue par le wrapper, ignoree par l'API)
        var tirs = new List<int[]>();
        for (var k = 0; k < 6; k++)
        {
            var (by, bx) = BrutDeLAffichage(40 + 25 * k, (im * 3 + 60 * k) % 448);
            tirs.Add([k, 0x0101, by, bx]);
        }

        if (i > 0 && i % Boucle == 0) e.NouvelleEpoque();
        e.Ecrire((uint)i, champs, [objets.ToArray(), affichage.ToArray(), tirs.ToArray()]);
    }
}
