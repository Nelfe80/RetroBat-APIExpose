using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Redresse une capture prise dans le tampon brut du cœur.
///
/// Une capture « pixel perfect » photographie la mémoire d'image du cœur, à la définition
/// d'origine du jeu. C'est ce qu'on veut, mais ce tampon n'est pas orienté : un shoot vertical
/// comme 19XX y tient en 384×224, couché sur le flanc, et c'est RetroArch qui le redresse à
/// l'affichage. Photographier le tampon donne donc l'image à 90 degrés, score compris.
///
/// Mesuré sur 19XX : l'avion du joueur apparaît à DROITE tirant vers la GAUCHE, là où le jeu le
/// montre en BAS tirant vers le HAUT. Une rotation d'un quart de tour dans le sens des aiguilles
/// remet la droite en bas, donc redresse l'image.
///
/// On ne tourne QUE ce que le référentiel déclare vertical. Un jeu qu'il ne connaît pas est
/// laissé tel quel : tourner au hasard serait pire que ne rien faire.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScoreShotImage
{
    /// <summary>
    /// Redresse le fichier sur place s'il y a lieu. Rend vrai si l'image a été tournée.
    /// Ne jette jamais : une image non redressée reste une image.
    /// </summary>
    public static bool Redresser(string chemin, string? systemId, string? romGroup, ILogger? logger = null)
    {
        try
        {
            if (!GamelistIdentity.EstVertical(systemId, romGroup)) return false;

            // Lire les octets AVANT d'ouvrir l'image : Image.FromFile garde le fichier verrouillé
            // tant que l'objet vit, et on veut réécrire au même endroit.
            var octets = File.ReadAllBytes(chemin);
            using var flux = new MemoryStream(octets);
            using var image = Image.FromStream(flux);

            // Déjà debout ? Un tampon plus haut que large n'a pas besoin d'être tourné, et le
            // tourner le coucherait. Le cas se présentera avec un cœur qui oriente lui-même.
            if (image.Height > image.Width) return false;

            image.RotateFlip(RotateFlipType.Rotate90FlipNone);   // un quart de tour horaire
            using var sortie = new MemoryStream();
            image.Save(sortie, ImageFormat.Png);
            File.WriteAllBytes(chemin, sortie.ToArray());

            logger?.LogInformation("Capture record : image redressée ({Rom}, {L}x{H} -> {H}x{L}).",
                romGroup, image.Height, image.Width, image.Width, image.Height);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Capture record : redressement impossible pour {Rom}.", romGroup);
            return false;
        }
    }

    /// <summary>
    /// L'image montre-t-elle vraiment le jeu ?
    ///
    /// La fin d'une course tombe souvent sur une transition : écran noir, fondu, intro de boss.
    /// Mesuré sur 19XX, la dernière image du replay était une bande de contenu sur un fond noir,
    /// et une autre un ciel vide. Publier ça comme « l'image du record » ne trompe personne mais
    /// ne montre rien.
    ///
    /// On mesure donc deux choses simples sur un échantillon : la part de pixels qui ne sont pas
    /// noirs, et le nombre de teintes distinctes. Un écran de jeu réel passe les deux largement ;
    /// un fondu échoue au premier, un aplat uni au second. Aucune de ces mesures ne juge la
    /// beauté de l'image, elles écartent seulement ce qui est vide.
    /// </summary>
    public static bool MontreLeJeu(string chemin, ILogger? logger = null)
    {
        try
        {
            var octets = File.ReadAllBytes(chemin);
            using var flux = new MemoryStream(octets);
            using var bitmap = new Bitmap(flux);

            // Un pixel sur seize en largeur comme en hauteur : assez pour juger, sans parcourir
            // 86 000 pixels à chaque essai.
            var pasX = Math.Max(1, bitmap.Width / 48);
            var pasY = Math.Max(1, bitmap.Height / 48);
            var total = 0;
            var allumes = 0;
            var teintes = new HashSet<int>();

            for (var y = 0; y < bitmap.Height; y += pasY)
            {
                for (var x = 0; x < bitmap.Width; x += pasX)
                {
                    var p = bitmap.GetPixel(x, y);
                    total++;
                    if (p.R + p.G + p.B > 60) allumes++;
                    teintes.Add((p.R >> 4 << 8) | (p.G >> 4 << 4) | (p.B >> 4));
                }
            }

            if (total == 0) return false;
            var partAllumee = (double)allumes / total;
            var bonne = partAllumee >= 0.35 && teintes.Count >= 12;
            logger?.LogInformation(
                "Capture record : image {Verdict} ({Part:P0} allumée, {Teintes} teintes).",
                bonne ? "retenue" : "écartée", partAllumee, teintes.Count);
            return bonne;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Capture record : image non mesurable, gardée telle quelle.");
            return true;   // on ne jette pas une image sur un doute d'outillage
        }
    }
}
