using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RetroBat.Api.Multiplayer;

/// <summary>Les commandes reseau de RetroArch (port 55355) dont le depart pret a besoin.</summary>
public interface ICommandesRetroArch
{
    /// <summary>Envoie une commande et rend sa reponse, ou null sans reponse dans le delai.</summary>
    Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500);

    /// <summary>Envoie une commande qui ne repond rien (LOAD_STATE_SLOT, WRITE_CORE_RAM).</summary>
    Task EnvoyerAsync(string commande, CancellationToken ct);
}

/// <summary>Les commandes reseau de la partie en cours, sur cette borne.</summary>
public sealed class CommandesRetroArchUdp : ICommandesRetroArch
{
    private const int Port = 55355;

    public async Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
    {
        try
        {
            using var udp = new UdpClient();
            var octets = Encoding.ASCII.GetBytes(commande);
            await udp.SendAsync(octets, octets.Length, "127.0.0.1", Port).WaitAsync(ct).ConfigureAwait(false);
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(delaiMs);
            var reponse = await udp.ReceiveAsync(delai.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(reponse.Buffer).Trim();
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            return null;
        }
    }

    public async Task EnvoyerAsync(string commande, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            var octets = Encoding.ASCII.GetBytes(commande);
            await udp.SendAsync(octets, octets.Length, "127.0.0.1", Port).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // RetroArch parti : la verification qui suit le dira.
        }
    }
}

/// <summary>
/// LE DEPART PRET CHEZ L'HOTE (CDC MULTIPLAYER 00quater.4, S4 et S5 du socle commun), avant que les autres se
/// connectent : l'etat du Data Pack est charge, le choix de chaque joueur est ecrit, puis relu. Les invites recoivent
/// l'etat de l'hote, choix compris, a la connexion (verifie le 22/10 sur Street Fighter II' CE avec deux RetroArch :
/// Ken et Chun-Li ecrits chez l'hote, relus chez l'invite, combat lance, aucune desynchronisation).
///
/// Rien ne s'ecrit apres : une ecriture faite chez l'hote seul, partie commencee, desynchroniserait les bornes.
/// </summary>
public sealed class MultiplayerDepartPret
{
    /// <summary>L'emplacement reserve, le meme que celui du labo : jamais un etat du joueur.</summary>
    public const int Emplacement = 97;

    private readonly ICommandesRetroArch _commandes;
    private readonly TimeSpan _attenteDuJeu;
    private readonly TimeSpan _apresLeChargement;

    public MultiplayerDepartPret(ICommandesRetroArch commandes, TimeSpan? attenteDuJeu = null, TimeSpan? apresLeChargement = null)
    {
        _commandes = commandes;
        _attenteDuJeu = attenteDuJeu ?? TimeSpan.FromSeconds(30);
        _apresLeChargement = apresLeChargement ?? TimeSpan.FromSeconds(1.5);
    }

    /// <summary>Le resultat : <c>Raison</c> dit pourquoi le depart est annule ; <c>Fichier</c> est l'etat pose, a retirer apres la partie.</summary>
    public sealed record Resultat(bool Ok, string? Raison, string? Fichier);

    /// <summary>
    /// Prepare le depart. <paramref name="choixParPlace"/> donne, pour chaque place de la partie, les choix de son
    /// joueur ; un choix absent prend le defaut du catalogue.
    /// </summary>
    public async Task<Resultat> PreparerAsync(
        MultiplayerGame jeu,
        string cheminDeLEtat,
        string nomDuContenu,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace,
        CancellationToken ct)
    {
        if (jeu.Start is null)
        {
            return new Resultat(false, "pas de depart pret", null);
        }

        byte[] etat;
        try
        {
            etat = await File.ReadAllBytesAsync(cheminDeLEtat, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Resultat(false, "etat introuvable", null);
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(etat)), jeu.Start.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new Resultat(false, "empreinte de l'etat differente du catalogue", null);
        }

        // Le jeu doit tourner : RetroArch ne repond aux commandes qu'une fois le contenu charge.
        var dossier = await DossierDesEtatsAsync(ct).ConfigureAwait(false);
        if (dossier is null)
        {
            return new Resultat(false, "RetroArch ne repond pas", null);
        }

        var fichier = Path.Combine(dossier, nomDuContenu + ".state" + Emplacement.ToString(CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dossier);
            await File.WriteAllBytesAsync(fichier, etat, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Resultat(false, "etat impossible a poser", null);
        }

        await _commandes.EnvoyerAsync("LOAD_STATE_SLOT " + Emplacement.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await Task.Delay(_apresLeChargement, ct).ConfigureAwait(false);

        var ecritures = Ecritures(jeu, choixParPlace);
        foreach (var (decalage, valeur) in ecritures)
        {
            await _commandes.EnvoyerAsync($"WRITE_CORE_RAM {decalage:x} {valeur:x2}", ct).ConfigureAwait(false);
        }

        // Verifiee (S5.4) : chaque valeur relue avant que quiconque se connecte ; un ecart annule le depart.
        foreach (var (decalage, valeur) in ecritures)
        {
            var lu = await LireAsync(decalage, ct).ConfigureAwait(false);
            if (lu != valeur)
            {
                return new Resultat(false, $"relecture differente a 0x{decalage:X} ({(lu is { } v ? v.ToString(CultureInfo.InvariantCulture) : "rien")} au lieu de {valeur})", fichier);
            }
        }

        return new Resultat(true, null, fichier);
    }

    /// <summary>Ce qu'il faut ecrire : pour chaque choix et chaque place de la partie, la valeur du joueur ou le defaut.</summary>
    public static IReadOnlyList<(long Decalage, int Valeur)> Ecritures(
        MultiplayerGame jeu, IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace)
    {
        var ecritures = new List<(long, int)>();
        foreach (var choix in jeu.Choices ?? [])
        {
            foreach (var (place, siens) in choixParPlace.OrderBy(paire => paire.Key))
            {
                var adresse = choix.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage;
                if (adresse is null)
                {
                    continue;
                }

                var valeur = siens is not null && siens.TryGetValue(choix.Key, out var voulu) && choix.Values.Any(v => v.Value == voulu)
                    ? voulu
                    : choix.Default;
                ecritures.Add((adresse.Value, valeur));
            }
        }

        return ecritures;
    }

    private async Task<string?> DossierDesEtatsAsync(CancellationToken ct)
    {
        var limite = DateTime.UtcNow + _attenteDuJeu;
        while (DateTime.UtcNow < limite)
        {
            var statut = await _commandes.DemanderAsync("GET_STATUS", ct).ConfigureAwait(false);
            if (statut is not null && (statut.Contains("PLAYING", StringComparison.Ordinal) || statut.Contains("PAUSED", StringComparison.Ordinal)))
            {
                var reponse = await _commandes.DemanderAsync("GET_CONFIG_PARAM savestate_directory", ct).ConfigureAwait(false);
                const string prefixe = "GET_CONFIG_PARAM savestate_directory ";
                if (reponse is not null && reponse.StartsWith(prefixe, StringComparison.Ordinal) && reponse.Length > prefixe.Length)
                {
                    return reponse[prefixe.Length..].Trim();
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<int?> LireAsync(long decalage, CancellationToken ct)
    {
        var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {decalage:x} 1", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return morceaux is { Length: >= 3 } && morceaux[0] == "READ_CORE_RAM"
            && int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur)
                ? valeur
                : null;
    }
}
