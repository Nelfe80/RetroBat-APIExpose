using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Controllers;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE DECOR DU PLAYERDECK POSE COMME UN DECOR DE RETROBAT (CDC PlayerDeck H-D15), sur le modele des panneaux du
/// MULTIPLAYER (<c>MultiplayerDecor</c>) : l'API ecrit, pour le jeu qu'elle lance, un PNG plein ecran (le fond exact des
/// rails du Deck, rendu par le Deck lui-meme, avec un trou transparent la ou le jeu s'affiche) et son <c>.info</c> de
/// marges, dans un jeu de decor a elle, <c>decorations/nelfeplay-deck</c>. Puis elle lance avec
/// <c>-bezel nelfeplay-deck -ratio custom</c> : RetroArch dessine le fond, la fenetre du Deck n'ecrit que les valeurs (H-D8).
///
/// POURQUOI « -ratio custom » : sans ratio impose, le lanceur de RetroBat force <c>aspect_ratio_index</c> sur « core » pour
/// mame et fbneo meme quand le decor declare un trou (LibRetro.Generator.cs, ConfigureBezels) : l'image resterait centree
/// et Metal Slug 3 ne serait pas pousse a droite. Avec « custom », le lanceur ecrit <c>aspect_ratio_index</c> = custom,
/// remet le biais a zero et tire <c>custom_viewport_*</c> des marges du <c>.info</c>. Les arguments de ligne de commande
/// du lanceur sont lus en dernier : aucune option d'EmulationStation n'est ecrite, rien n'est demande a RetroBat.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeckDecor
{
    public const string Nom = "nelfeplay-deck";

    /// <summary>
    /// Les arguments du lanceur qui posent le decor et calent le jeu dans son trou. <c>-integerscale 0</c> : un agrandissement
    /// entier redimensionnerait l'image a l'interieur du trou (le lanceur lit la cle « integerscale » du systeme sinon).
    /// </summary>
    public static readonly IReadOnlyList<string> ArgumentsDuLanceur = ["-bezel", Nom, "-ratio", "custom", "-integerscale", "0"];

    /// <summary>
    /// LES NOTIFICATIONS DE RETROARCH COUPEES POUR CE LANCEMENT (demande du user du 08/11) : la fonction « OnScreenMsg » d'ES,
    /// que le lanceur lit en dernier (arguments de la ligne) et ecrit dans video_font_enable (LibRetroGenerator,
    /// BindBoolFeatureOn ; declaree pour libretro dans es_features.cfg). RetroArch 1.22.2 n'active ses widgets que si
    /// video_font_enable est vrai (retroarch.c, « global notifications on/off toggle switch ») et ne tire plus sa file de
    /// messages sans lui (video_driver.c) : plus rien ne parait, ni SHOW_MSG, ni etat, ni netplay, ni succes. Le compteur
    /// d'images et les statistiques restent (reglages a part). Le lancement suivant par ES rend la valeur d'ES.
    /// </summary>
    public static readonly IReadOnlyList<string> ArgumentsSansNotifications = ["-OnScreenMsg", "0"];

    /// <summary>
    /// La valeur de video_font_enable que le lanceur ecrirait sans le Deck : « false » si ES coupe les notifications
    /// (global.OnScreenMsg a faux), « true » sinon (BindBoolFeatureOn : allume par defaut). Pour ce qui lance RetroArch sans le
    /// lanceur (lecture d'un replay) et heriterait sinon de « false » apres une partie avec le Deck. Lecture seule.
    /// </summary>
    public static string NotificationsDEs(string? fichier = null)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(fichier ?? RetroBatPaths.EmulationStationSettingsPath);
            var v = doc.Root?.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == "global.OnScreenMsg")?.Attribute("value")?.Value;
            return v is null || v.Length == 0 || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("enabled", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        }
        catch (Exception)
        {
            return "true";
        }
    }

    public static string Dossier => Path.Combine(RetroBatPaths.RetroBatRoot, "decorations", Nom);

    /// <summary>Le trou du decor : la ou le jeu s'affiche, en pixels de l'ecran (origine en haut a gauche).</summary>
    public sealed record Trou(int X, int Y, int Width, int Height);

    /// <summary>
    /// Le <c>.info</c> d'un decor de RetroBat : la taille de l'image et les marges du trou. La taille est celle de l'ecran
    /// pour lequel l'image a ete rendue : le lanceur n'adapte alors rien (ni etirement ni decalage) et le trou tombe au
    /// pixel pres (<see cref="ViewportDuLanceur"/>).
    /// </summary>
    public static string Info(int largeur, int hauteur, Trou trou)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["width"] = largeur,
            ["height"] = hauteur,
            ["top"] = trou.Y,
            ["left"] = trou.X,
            ["bottom"] = hauteur - trou.Y - trou.Height,
            ["right"] = largeur - trou.X - trou.Width,
            ["opacity"] = 1.0,
            // les messages de RetroArch dans le jeu, pas sous un rail
            ["messagex"] = Math.Round((trou.X + trou.Width * 0.04) / largeur, 3),
            ["messagey"] = 0.05,
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Ecrit l'image et son <c>.info</c> pour ce jeu (<c>games/&lt;systeme&gt;/&lt;jeu&gt;</c>) et pour son systeme (le repli du
    /// lanceur). Rend faux si un nom n'en est pas un ou si l'ecriture echoue.
    /// </summary>
    public static bool Ecrire(string systeme, string cheminDuJeu, string png, string info, string? racine = null)
    {
        var jeu = Path.GetFileNameWithoutExtension(cheminDuJeu);
        if (!CommandsController.NomDeLanceur(systeme) || string.IsNullOrWhiteSpace(jeu) || jeu.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !File.Exists(png))
        {
            return false;
        }

        var dossier = racine ?? Dossier;
        foreach (var (ici, nom) in new[] { (Path.Combine(dossier, "games", systeme), jeu), (Path.Combine(dossier, "systems"), systeme) })
        {
            Directory.CreateDirectory(ici);
            var cible = Path.Combine(ici, nom + ".png");
            var provisoire = cible + ".tmp";
            File.Copy(png, provisoire, overwrite: true);
            File.Move(provisoire, cible, overwrite: true);
            File.WriteAllText(Path.Combine(ici, nom + ".info"), info);
        }

        return true;
    }

    /// <summary>Retire le decor (fin de partie, demarrage de l'API) ; une erreur ne compte pas : le prochain lancement le recrira.</summary>
    public static void Effacer(string? racine = null)
    {
        try
        {
            var dossier = racine ?? Dossier;
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// LE CALCUL DU LANCEUR, REPRODUIT (LibRetro.Generator.cs, ConfigureBezels, RetroBat 2026) : le
    /// <c>custom_viewport_*</c> que le lanceur ecrit pour un decor, quand <c>-ratio custom</c> est passe et qu'aucun fichier de
    /// ratio personnalise ne vise le jeu. Null : le lanceur ne pose pas le decor (ecran plus etroit que 16:10 et image d'une
    /// autre taille). Pur.
    ///
    /// - Image a la taille de l'ecran : x = left, y = top, largeur = width - left - right, hauteur = height - top - bottom.
    /// - Sinon le lanceur adapte : etirement si l'ecran est plus petit que l'image ou de forme voisine (ecart de rapport
    ///   inferieur a 0,2), marges multipliees par le rapport des tailles ; decalage de moitie de la difference sinon.
    /// C'est pourquoi l'API rend l'image a la taille exacte de l'ecran : aucune adaptation, le trou tombe au pixel pres.
    /// </summary>
    public static Trou? ViewportDuLanceur(int infoLargeur, int infoHauteur, int left, int top, int right, int bottom,
        int imageLargeur, int imageHauteur, int ecranLargeur, int ecranHauteur)
    {
        var rapportEcran = ecranLargeur / (float)ecranHauteur;
        var rapportImage = imageLargeur / (float)imageHauteur;
        if (ecranLargeur == infoLargeur && ecranHauteur == infoHauteur)
        {
            return new Trou(left, top, infoLargeur - left - right, infoHauteur - top - bottom);
        }

        if (rapportEcran < 1.6) return null;

        var wratio = ecranLargeur / (float)infoLargeur;
        var hratio = ecranHauteur / (float)infoHauteur;
        var xoffset = ecranLargeur - infoLargeur;
        var yoffset = ecranHauteur - infoHauteur;
        var etirer = ecranLargeur < infoLargeur || ecranHauteur < infoHauteur || Math.Abs(rapportEcran - rapportImage) < 0.2;
        return etirer
            ? new Trou((int)(left * wratio), (int)(top * hratio), (int)((infoLargeur - left - right) * wratio), (int)((infoHauteur - top - bottom) * hratio))
            : new Trou(left + xoffset / 2, top + yoffset / 2, infoLargeur - left - right, infoHauteur - top - bottom);
    }
}
