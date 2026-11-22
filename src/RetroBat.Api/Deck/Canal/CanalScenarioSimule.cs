namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// UN NIVEAU SIMULE de DoDonPachi DaiOuJou pour le canal (essais sans jeu, jamais le jeu) : la distance monte d'un pas toutes les
/// 9 images, le vaisseau balaie l'ecran, des ennemis entrent par le haut, descendent et DISPARAISSENT (detruits), un boss arrive a la
/// fin et perd de la vie. Les valeurs sont ecrites en BRUT, comme le jeu (mots 8.8 tournes), pour que la conversion de l'API soit
/// elle aussi essayee. Le meme profil que le .VIEW du labo (<c>dodonpachi-daioujou.VIEW</c>) : champs temps, distance, vies, niveau,
/// camera, vaisseau_x, vaisseau_y ; tables objets (type, script, vie) et affichage (drapeau, x, y), 48 cases chacune.
/// </summary>
public static class CanalScenarioSimule
{
    public static readonly string[] Champs = ["temps", "distance", "vies", "niveau", "camera", "vaisseau_x", "vaisseau_y"];

    public static CanalEcrivainSimule.Table[] Tables() =>
    [
        new("objets", 48, 64, ["type", "script", "vie"]),
        new("affichage", 48, 64, ["drapeau", "x", "y"]),
    ];

    public static CanalEcrivainSimule DaiOuJou(uint pid, string nomVue = "dodonpachi-daioujou.VIEW") => new(nomVue, Champs, Tables(), pid);

    /// <summary>Longueur du niveau simule en images : au-dela, il recommence (nouvelle epoch).</summary>
    public const int Boucle = 9 * 560;

    private static readonly int[] Types = [0x1101, 0x8200, 0x2500, 0x4D00, 0x8B01, 0x2900];

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
        var champs = new[]
        {
            im,                                   // temps
            distance,                             // distance
            2,                                    // vies
            1,                                    // niveau
            (int)(32 * 64 * Math.Sin(i / 120.0)), // camera : s16 / 64 = pixels
            (sx - 3) * 64,                        // vaisseau_x (second mot : l'axe x de l'ecran)
            (446 - sy) * 64,                      // vaisseau_y (premier mot : l'axe y de l'ecran)
        };
        var objets = new List<int[]>();
        var affichage = new List<int[]>();
        foreach (var (slot, type, x, y, vie) in Ennemis(i))
        {
            objets.Add([slot, type, 0x1234, vie]);
            affichage.Add([slot, 0xA000, (446 - y) * 64, (x - 3) * 64]);
        }

        // un effet affiche mais sans objet (une explosion) et un drapeau cache : jamais des ennemis
        affichage.Add([44, 0xA000, 200 * 64, 100 * 64]);
        affichage.Add([45, 0xA200, 180 * 64, 120 * 64]);
        objets.Sort((a, b) => a[0].CompareTo(b[0]));
        affichage.Sort((a, b) => a[0].CompareTo(b[0]));
        if (i > 0 && i % Boucle == 0) e.NouvelleEpoque();
        e.Ecrire((uint)i, champs, [objets.ToArray(), affichage.ToArray()]);
    }
}
