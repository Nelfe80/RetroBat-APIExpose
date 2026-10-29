using System.Security.Cryptography;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE DEPART PAR PLACE (29/10, Bomberman PSP ; <see cref="MultiplayerGame.CoreStart"/>) : avant le lancement, chaque borne pose
/// l'etat de SA place la ou le coeur le cherche (le coeur PPSSPP de NelfePlay : <c>PPSSPP/netplay/ULUS10121.state</c> sous
/// le dossier systeme de RetroArch). Le coeur le charge a la fin de son demarrage et attend tous les joueurs de l'etat.
/// L'etat d'une partie d'avant s'efface toujours : un etat oublie ferait repartir une partie suivante de lui, sur une
/// seule borne.
/// </summary>
public static class MultiplayerDepartDuCoeur
{
    /// <summary>Le dossier systeme de RetroArch, que le lanceur de RetroBat pose a chaque lancement.</summary>
    public static string DossierSysteme => Path.Combine(RetroBatPaths.RetroBatRoot, "bios");

    public sealed record Resultat(bool Ok, string? Fichier, string? Raison);

    /// <summary>
    /// Pose l'etat de <paramref name="place"/> pour une partie a <paramref name="joueurs"/> ; Ok faux si le depart existe
    /// pour cette partie mais que l'etat de la place ne se pose pas (absent, retouche) : la borne ne doit pas partir seule.
    /// </summary>
    public static Resultat Poser(MultiplayerInstalledGame jeu, int joueurs, int place, string dossierDuCatalogue, string dossierSysteme)
    {
        if (jeu.Game.CoreStart is not { } depart)
        {
            return new Resultat(true, null, null);
        }

        var cible = Path.Combine(dossierSysteme, depart.Path.Replace('/', Path.DirectorySeparatorChar));
        Retirer(cible);
        if (jeu.DepartDuCoeurPour(joueurs) is null)
        {
            return new Resultat(true, null, "pas de depart par place pour ce contenu a " + joueurs + " joueurs");
        }

        if (depart.DeLaPlace(place) is not { } etat)
        {
            return new Resultat(false, null, "pas d'etat pour la place " + place);
        }

        byte[] octets;
        try
        {
            octets = File.ReadAllBytes(Path.Combine(dossierDuCatalogue, etat.State.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Resultat(false, null, "etat de la place " + place + " introuvable");
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(octets)), etat.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new Resultat(false, null, "empreinte de l'etat de la place " + place + " differente du catalogue");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
            if (etat.State.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                // Un etat PSP pese 42 Mo, compresse 9 : le Data Pack le livre en gzip, le coeur le lit en clair.
                using var source = new System.IO.Compression.GZipStream(new MemoryStream(octets), System.IO.Compression.CompressionMode.Decompress);
                using var sortie = File.Create(cible);
                source.CopyTo(sortie);
            }
            else
            {
                File.WriteAllBytes(cible, octets);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Retirer(cible);
            return new Resultat(false, null, "etat de la place " + place + " impossible a poser");
        }

        return new Resultat(true, cible, null);
    }

    /// <summary>Efface l'etat pose, s'il est la.</summary>
    public static void Retirer(string? fichier)
    {
        if (fichier is null)
        {
            return;
        }

        try
        {
            File.Delete(fichier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
