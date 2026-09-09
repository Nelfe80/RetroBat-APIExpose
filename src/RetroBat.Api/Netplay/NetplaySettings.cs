using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Poser les reglages netplay qu'`emulatorLauncher` lira au lancement.
///
/// Il n'y a pas d'autre chemin : pour un HOTE, EmulationStation ne passe que `-netplaymode host`
/// en ligne de commande, et le lanceur va chercher le reste — pseudo, mots de passe, relais — dans
/// `es_settings.cfg`. Les deux mots de passe ne s'expriment donc QUE la.
///
/// ⚠️ Ce fichier appartient a EmulationStation, qui le REECRIT en se fermant. Deux precautions :
/// on ecrit juste avant un lancement qu'on declenche nous-memes (le lanceur relit a froid, dans
/// la foulee), et on garde une COPIE `.bak` avant chaque modification. Si ES ecrase nos valeurs
/// dans l'intervalle, notre jeton n'apparaitra pas au lobby : on le dira, au lieu de faire
/// semblant d'heberger.
/// </summary>
public static class NetplaySettings
{
    public static string Chemin => Path.Combine(
        RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation", "es_settings.cfg");

    /// <summary>Ce qu'on pose pour heberger. Le pseudo porte la marque, le pseudo du joueur et un jeton.</summary>
    public sealed record Hebergement(
        string Pseudo,
        string MotDePasseJoueur,
        string MotDePasseSpectateur,
        string Relais,
        int Port);

    /// <summary>
    /// Le pseudo de lobby : `NELFEPLAY_&lt;pseudo&gt;_&lt;jeton&gt;`.
    ///
    /// La marque pour qu'on se voie sur le lobby, le pseudo du joueur parce que c'est AUSSI le
    /// nom que le spectateur verra en jeu, le jeton pour reconnaitre notre propre entree parmi
    /// celles du monde entier.
    ///
    /// ⚠️ Le protocole plafonne le pseudo a 32 OCTETS. Tronque = jeton perdu = entree
    /// introuvable. Le pseudo du joueur est donc reduit a de l'alphanumerique ASCII et coupe a
    /// douze : un pseudo en japonais ou avec des espaces ferait sauter le budget ou casserait la
    /// correspondance.
    /// </summary>
    public static string ComposerPseudo(string pseudoJoueur, string jeton)
    {
        var propre = new string(pseudoJoueur
            .Where(char.IsAsciiLetterOrDigit)
            .Take(12)
            .ToArray());
        return propre.Length == 0
            ? $"NELFEPLAY_{jeton}"
            : $"NELFEPLAY_{propre}_{jeton}";
    }

    /// <summary>
    /// Ecrit les reglages, apres avoir mis le fichier de cote.
    ///
    /// Rend faux sans rien ecrire si le fichier est illisible : mieux vaut ne pas heberger que
    /// de reecrire par-dessus une configuration qu'on n'a pas comprise.
    /// </summary>
    public static bool Poser(Hebergement h, ILogger? logger = null)
    {
        var chemin = Chemin;
        XDocument doc;
        try
        {
            doc = XDocument.Load(chemin);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Netplay : es_settings.cfg illisible, aucun reglage pose.");
            return false;
        }
        if (doc.Root is null)
        {
            return false;
        }

        Ecrire(doc.Root, "string", "global.netplay.nickname", h.Pseudo);
        Ecrire(doc.Root, "string", "global.netplay.password", h.MotDePasseJoueur);
        Ecrire(doc.Root, "string", "global.netplay.spectatepassword", h.MotDePasseSpectateur);
        Ecrire(doc.Root, "string", "global.netplay.relay", h.Relais);
        Ecrire(doc.Root, "string", "global.netplay.port", h.Port.ToString());
        Ecrire(doc.Root, "bool", "global.netplay", "true");
        // Sans annonce publique, le relais ne rend jamais l'identifiant de session : c'est le
        // lobby qui le porte, et lui seul.
        Ecrire(doc.Root, "bool", "global.netplay_public_announce", "true");

        try
        {
            // La copie de cote AVANT de toucher a quoi que ce soit. Une seule, ecrasee a chaque
            // fois : on veut pouvoir revenir en arriere, pas collectionner.
            var copie = chemin + ".nelfeplay.bak";
            File.Copy(chemin, copie, overwrite: true);

            // Ecriture atomique : une coupure au milieu laisserait ES sans configuration.
            var temporaire = chemin + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            doc.Save(temporaire);
            File.Move(temporaire, chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Netplay : reglages non ecrits.");
            return false;
        }

        return true;
    }

    /// <summary>Restaure la copie de cote. Pour le jour ou l'on doute d'avoir bien fait.</summary>
    public static bool Restaurer()
    {
        var copie = Chemin + ".nelfeplay.bak";
        if (!File.Exists(copie))
        {
            return false;
        }
        try
        {
            File.Copy(copie, Chemin, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Pose une valeur, en creant l'entree si elle manque. Le reste du fichier ne bouge pas.</summary>
    private static void Ecrire(XElement racine, string type, string nom, string valeur)
    {
        var entree = racine.Elements()
            .FirstOrDefault(e => (string?)e.Attribute("name") == nom);

        if (entree is null)
        {
            racine.Add(new XElement(type,
                new XAttribute("name", nom),
                new XAttribute("value", valeur)));
            return;
        }
        entree.SetAttributeValue("value", valeur);
    }
}
