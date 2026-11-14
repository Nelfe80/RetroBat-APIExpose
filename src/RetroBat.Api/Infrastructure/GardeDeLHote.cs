using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Garde contre le rebond DNS (DNS rebinding).
///
/// Une page servie par un nom de domaine quelconque, dont le DNS bascule ensuite
/// sur 127.0.0.1, devient « meme origine » que l'API pour le navigateur : elle
/// peut ecrire et lire les reponses sans aucune cle. Le seul indice qui la trahit
/// est l'en-tete Host, qui porte encore son nom de domaine.
///
/// On accepte donc, pour une connexion venue de la boucle locale :
///   - toute adresse IP litterale (une IP ne se « rebondit » pas : le navigateur
///     se connecte a cette adresse, pas a un nom) ;
///   - localhost et *.localhost (resolus sans DNS par les navigateurs) ;
///   - le nom de la machine (NetBIOS, DNS, FQDN, .local) ;
///   - les noms de l'adresse d'ecoute configuree et de Security:AllowedHosts.
///
/// Une connexion qui vient du reseau n'est pas concernee ici : la cle de borne
/// la garde deja, et un hub ou un pair peut joindre la borne par le nom qu'on
/// lui a donne.
/// </summary>
public static class GardeDeLHote
{
    public enum Mode
    {
        /// <summary>Refuse un hote inattendu (defaut).</summary>
        Bloquer,
        /// <summary>Journalise sans refuser.</summary>
        Journaliser,
        /// <summary>Aucun controle.</summary>
        Arret,
    }

    public static Mode LireMode(string? valeur) => (valeur ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "log" or "journal" or "observe" => Mode.Journaliser,
        "off" or "none" or "false" or "0" => Mode.Arret,
        _ => Mode.Bloquer,
    };

    /// <summary>Noms acceptes : machine, ecoute configuree, liste de la configuration.</summary>
    public static HashSet<string> NomsAcceptes(string? listeConfiguree, string? adresseDEcoute)
    {
        var noms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };

        void Ajouter(string? nom)
        {
            var propre = Normaliser(nom);
            if (propre.Length > 0)
            {
                noms.Add(propre);
            }
        }

        try { Ajouter(Environment.MachineName); } catch { }
        try
        {
            var dns = Dns.GetHostName();
            Ajouter(dns);
            Ajouter(dns + ".local");
            var domaine = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrWhiteSpace(domaine))
            {
                Ajouter(dns + "." + domaine);
            }
        }
        catch { }
        try { Ajouter(Environment.MachineName + ".local"); } catch { }

        foreach (var url in (adresseDEcoute ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // http://+:12345 et http://*:12345 ne sont pas des Uri valides : on ne retient que les vrais noms.
            if (Uri.TryCreate(url.Replace("://+", "://joker").Replace("://*", "://joker"), UriKind.Absolute, out var u)
                && !string.Equals(u.Host, "joker", StringComparison.OrdinalIgnoreCase))
            {
                Ajouter(u.Host);
            }
        }

        foreach (var nom in (listeConfiguree ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Ajouter(nom);
        }

        return noms;
    }

    /// <summary>
    /// L'en-tete Host est-il acceptable ? <paramref name="hote"/> est la valeur brute
    /// (avec ou sans port, IPv6 entre crochets).
    /// </summary>
    public static bool EstAccepte(string? hote, IReadOnlySet<string> nomsAcceptes)
    {
        // Pas d'en-tete Host (HTTP/1.0) : ce n'est pas un navigateur, qui l'envoie toujours.
        if (string.IsNullOrWhiteSpace(hote))
        {
            return true;
        }

        var nom = SansPort(hote.Trim());
        if (nom.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(nom, out _))
        {
            return true;
        }

        nom = Normaliser(nom);
        if (nom.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || nom.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return nomsAcceptes.Contains(nom);
    }

    /// <summary>Retire le port et les crochets d'une valeur de Host.</summary>
    public static string SansPort(string hote)
    {
        if (hote.StartsWith('['))
        {
            var fin = hote.IndexOf(']');
            return fin > 1 ? hote[1..fin] : string.Empty;
        }

        // Une IPv6 nue (sans crochets) contient plusieurs ':' : on la garde telle quelle.
        var deuxPoints = hote.IndexOf(':');
        if (deuxPoints >= 0 && hote.IndexOf(':', deuxPoints + 1) < 0)
        {
            return hote[..deuxPoints];
        }

        return hote;
    }

    private static string Normaliser(string? nom) => (nom ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// Une ecriture sans Sec-Fetch-Site vient-elle quand meme d'une page web ?
    ///
    /// Les navigateurs n'envoient Sec-Fetch-* qu'aux origines sures (https,
    /// localhost, 127.0.0.1) et les plus anciens jamais. Mais une ecriture de
    /// navigateur porte toujours Origin, qu'aucun client natif de l'API n'envoie.
    /// Une origine de la boucle locale, ou identique a l'hote appele, reste chez
    /// elle ; toute autre (y compris « null ») est une page etrangere.
    /// </summary>
    public static bool EcritureDePageEtrangere(string? origine, string? hote)
    {
        if (string.IsNullOrWhiteSpace(origine))
        {
            return false;
        }

        if (!Uri.TryCreate(origine.Trim(), UriKind.Absolute, out var o))
        {
            return true;
        }

        if (o.IsLoopback)
        {
            return false;
        }

        var appele = SansPort((hote ?? string.Empty).Trim());
        return !string.Equals(o.Host.Trim('[', ']'), appele, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>La requete porte-t-elle une marque de navigateur (Sec-Fetch-*, Origin) ?</summary>
    public static bool MarqueDeNavigateur(Func<string, string?> enTete)
        => !string.IsNullOrEmpty(enTete("Sec-Fetch-Site"))
           || !string.IsNullOrEmpty(enTete("Sec-Fetch-Mode"))
           || !string.IsNullOrEmpty(enTete("Sec-Fetch-Dest"))
           || !string.IsNullOrEmpty(enTete("Origin"));

    /// <summary>
    /// Deux cles egales, comparees en temps constant. Une cle attendue vide n'est jamais egale. Pur.
    /// </summary>
    public static bool MemeCle(string? fournie, string? attendue)
        => fournie is { Length: > 0 } && attendue is { Length: > 0 }
           && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.UTF8.GetBytes(fournie), System.Text.Encoding.UTF8.GetBytes(attendue));

    /// <summary>Hotes refuses deja signales, pour ne journaliser chacun qu'une fois.</summary>
    private static readonly HashSet<string> Signales = new(StringComparer.OrdinalIgnoreCase);

    public static bool PremierSignalement(string hote)
    {
        lock (Signales)
        {
            if (Signales.Count >= 64)
            {
                return false;
            }

            return Signales.Add(hote);
        }
    }
}
