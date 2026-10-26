using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Controllers;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LES PANNEAUX EN JEU POSES COMME UN DECOR DE RETROBAT (CDC Ecrans v1.0, 3.6) : le lanceur de RetroBat sait afficher un
/// decor (un PNG plein ecran et son <c>.info</c>, qui donne les marges du trou ou le jeu s'affiche) et cale l'image du jeu
/// dans ce trou. L'API ecrit donc ses panneaux dans un jeu de decor a elle, <c>decorations/nelfeplay-multiplayer</c>, et
/// lance avec <c>-bezel nelfeplay-multiplayer</c> : RetroArch les dessine lui-meme, rien n'est pose par-dessus le jeu, et
/// pas une option d'EmulationStation n'est ecrite.
///
/// PREMIERE VERSION : panneaux fixes, poses au lancement, et seulement pour les jeux d'arcade horizontaux au format 4:3
/// (duels, versus, equipes). Le catalogue ne dit pas encore l'orientation d'un jeu : des marges de 4:3 etireraient un jeu
/// vertical.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MultiplayerDecor
{
    public const string Nom = "nelfeplay-multiplayer";

    public const double FormatArcade = 4.0 / 3.0;

    /// <summary>Les paliers de Fightcade, comme <see cref="MultiplayerCote.LettreDe"/> : E, D, C, B, A, puis S.</summary>
    private static readonly double[] Paliers = [0, 700, 1000, 1300, 1600, 1900];

    public static string Dossier => Path.Combine(RetroBatPaths.RetroBatRoot, "decorations", Nom);

    /// <summary>Un jeu dont les panneaux se posent : un seul ecran, d'arcade, horizontal (duel, versus, equipes).</summary>
    public static bool Concerne(MultiplayerGame jeu)
        => jeu.EstArcade && jeu.Screens <= 1 && jeu.Family is "duel" or "versus" or "equipes";

    /// <summary>La progression vers la lettre suivante (0 a 1), pleine a S.</summary>
    public static double Palier(double cote)
    {
        for (var i = Paliers.Length - 1; i >= 0; i--)
        {
            if (cote >= Paliers[i])
            {
                return i == Paliers.Length - 1 ? 1 : (cote - Paliers[i]) / (Paliers[i + 1] - Paliers[i]);
            }
        }

        return 0;
    }

    /// <summary>
    /// Ecrit l'image et son <c>.info</c> pour ce jeu (<c>games/&lt;systeme&gt;/&lt;jeu&gt;</c>) et pour son systeme (le repli
    /// du lanceur). Rend faux si un nom n'en est pas un ou si l'ecriture echoue.
    /// </summary>
    public static bool Ecrire(string systeme, string cheminDuJeu, Bitmap image, PanneauxEnJeu.Bandes bandes, string? racine = null)
    {
        var jeu = Path.GetFileNameWithoutExtension(cheminDuJeu);
        if (!CommandsController.NomDeLanceur(systeme) || string.IsNullOrWhiteSpace(jeu) || jeu.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var info = Info(image.Width, image.Height, bandes);
        var dossier = racine ?? Dossier;
        foreach (var (ici, nom) in new[] { (Path.Combine(dossier, "games", systeme), jeu), (Path.Combine(dossier, "systems"), systeme) })
        {
            Directory.CreateDirectory(ici);
            var png = Path.Combine(ici, nom + ".png");
            var provisoire = png + ".tmp";
            image.Save(provisoire, ImageFormat.Png);
            File.Move(provisoire, png, overwrite: true);
            File.WriteAllText(Path.Combine(ici, nom + ".info"), info);
        }

        return true;
    }

    /// <summary>Le <c>.info</c> d'un decor de RetroBat : la taille de l'image et les marges du trou ou le jeu s'affiche.</summary>
    public static string Info(int largeur, int hauteur, PanneauxEnJeu.Bandes bandes)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["width"] = largeur,
            ["height"] = hauteur,
            ["top"] = 0,
            ["left"] = bandes.Gauche,
            ["bottom"] = 0,
            ["right"] = bandes.Droite,
            ["opacity"] = 1.0,
            ["messagex"] = 0.22,
            ["messagey"] = 0.12,
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>La cote d'un joueur, groupee par milliers (« 1 842 »).</summary>
    public static string Cote(double valeur)
        => Math.Round(valeur).ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " ", NumberGroupSizes = [3] });
}
