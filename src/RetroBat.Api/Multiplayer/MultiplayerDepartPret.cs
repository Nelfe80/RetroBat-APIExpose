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
    private readonly int _port;

    /// <summary>Le port des commandes de RetroArch : 55355 pour une partie, un autre sur un banc d'essai.</summary>
    public CommandesRetroArchUdp(int port = 55355) => _port = port;

    public async Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
    {
        try
        {
            using var udp = new UdpClient();
            var octets = Encoding.ASCII.GetBytes(commande);
            await udp.SendAsync(octets, octets.Length, "127.0.0.1", _port).WaitAsync(ct).ConfigureAwait(false);
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
            await udp.SendAsync(octets, octets.Length, "127.0.0.1", _port).WaitAsync(ct).ConfigureAwait(false);
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
    /// joueur ; un choix absent prend le defaut du catalogue. <paramref name="depart"/> est l'etat retenu pour le nombre
    /// de joueurs (<see cref="MultiplayerGame.DepartPour"/>), <see cref="MultiplayerGame.Start"/> sans lui.
    /// </summary>
    public async Task<Resultat> PreparerAsync(
        MultiplayerGame jeu,
        string cheminDeLEtat,
        string nomDuContenu,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace,
        CancellationToken ct,
        MultiplayerStart? depart = null)
    {
        depart ??= jeu.Start;
        if (depart is null)
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

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(etat)), depart.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
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

        var ecritures = EcrituresParCommande(jeu, choixParPlace);
        foreach (var (decalage, valeur, carte) in ecritures)
        {
            await _commandes.EnvoyerAsync($"{(carte ? "WRITE_CORE_MEMORY" : "WRITE_CORE_RAM")} {decalage:x} {valeur:x2}", ct).ConfigureAwait(false);
        }

        // Verifiee (S5.4) : chaque valeur relue avant que quiconque se connecte ; un ecart annule le depart.
        foreach (var (decalage, valeur, carte) in ecritures)
        {
            var lu = await LireAsync(decalage, ct, carte).ConfigureAwait(false);
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
        => EcrituresParCommande(jeu, choixParPlace).Select(e => (e.Decalage, e.Valeur)).ToList();

    /// <summary>Les ecritures, chacune avec sa commande : la carte du coeur (<see cref="MultiplayerChoice.ParLaCarteDuCoeur"/>) ou la memoire systeme.</summary>
    public static IReadOnlyList<(long Decalage, int Valeur, bool Carte)> EcrituresParCommande(
        MultiplayerGame jeu, IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace)
    {
        var ecritures = new List<(long, int, bool)>();
        // Une console par joueur de la partie (mGBA Splitscreen) : c'est aussi le nombre de joueurs des adresses par nombre.
        var joueurs = choixParPlace.Count;
        foreach (var choix in jeu.Choices ?? [])
        {
            // Un choix distinct deja pris par une place d'avant passe a la suivante libre (Metal Slug 3).
            var retenues = choix.Departager(choixParPlace.Select(paire =>
                (paire.Key, paire.Value is not null && paire.Value.TryGetValue(choix.Key, out var voulu) ? voulu : choix.Default)));
            foreach (var (place, valeur) in retenues)
            {
                if (choix.AdressePour(place, joueurs) is not { } adresse)
                {
                    continue;
                }

                // F-Zero : le bolide de chaque joueur dans chaque console (la console N a +0x10000000 par console).
                var consoles = choix.AllConsoles && choix.ParLaCarteDuCoeur ? joueurs : 1;
                for (var console = 0; console < consoles; console++)
                {
                    ecritures.Add((adresse + console * 0x10000000L, valeur, choix.ParLaCarteDuCoeur));
                }
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

    private async Task<int?> LireAsync(long decalage, CancellationToken ct, bool carte = false)
    {
        var commande = carte ? "READ_CORE_MEMORY" : "READ_CORE_RAM";
        var reponse = await _commandes.DemanderAsync($"{commande} {decalage:x} 1", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return morceaux is { Length: >= 3 } && morceaux[0] == commande
            && int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur)
                ? valeur
                : null;
    }
}

/// <summary>
/// LA FIN D'UNE RENCONTRE AU NIVEAU NELFEPLAY (V24-16), guettee chez l'hote : la premiere place dont le compteur atteint
/// le seuil du catalogue gagne. Deux lectures de suite le confirment : une valeur lue au vol pendant une ecriture du jeu
/// ne termine rien. Le guet ne commence qu'une fois le depart pret pose ; avant, la demo du jeu pourrait faire monter
/// les compteurs.
/// </summary>
public sealed class MultiplayerFinDeRencontre
{
    private readonly ICommandesRetroArch _commandes;
    private readonly TimeSpan _periode;

    public MultiplayerFinDeRencontre(ICommandesRetroArch commandes, TimeSpan? periode = null)
    {
        _commandes = commandes;
        _periode = periode ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// La place gagnante, ou null si la partie se ferme avant (annulation). Un vainqueur ne compte qu'apres avoir vu
    /// tous les compteurs sous le seuil : la demo du jeu, chez un invite qui n'a pas encore recu l'etat de l'hote,
    /// ne termine rien.
    /// </summary>
    public async Task<int?> AttendreLeVainqueurAsync(MultiplayerEnd fin, IReadOnlyCollection<int> places, CancellationToken ct)
    {
        int? candidat = null;
        var departVu = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // HORS DUEL, RIEN NE COMPTE (garde du 23/10) : le joueur 1 seul contre l'ordinateur, la manche que gagne la
                // machine tombe dans la case du joueur 2. Le depart se voit quand le duel commence, compteurs remis a zero.
                if (fin.Duel is { Decalage: { } garde } && await LireAsync(garde, ct).ConfigureAwait(false) != fin.Duel.Value)
                {
                    candidat = null;
                    await Task.Delay(_periode, ct).ConfigureAwait(false);
                    continue;
                }

                var lues = 0;
                var auSeuil = new List<(int Place, int Valeur)>();
                foreach (var place in places.OrderBy(p => p))
                {
                    var adresse = fin.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage;
                    if (adresse is null)
                    {
                        continue;
                    }

                    if (await LireAsync(adresse.Value, ct).ConfigureAwait(false) is { } valeur)
                    {
                        lues++;
                        if (valeur >= fin.FirstTo && valeur < 0xFF)
                        {
                            auSeuil.Add((place, valeur));
                        }
                    }
                }

                // UN SEUL VAINQUEUR : la place la plus haute parmi celles qui ont atteint le seuil. Deux places a egalite (un
                // set nul de Windjammers donne un point a chacun, 2-2) ne designent personne : le set suivant tranche.
                int? vu = null;
                if (auSeuil.Count > 0)
                {
                    var haut = auSeuil.Max(p => p.Valeur);
                    var enTete = auSeuil.Where(p => p.Valeur == haut).ToList();
                    vu = enTete.Count == 1 ? enTete[0].Place : null;
                }

                if (auSeuil.Count == 0 && lues == places.Count)
                {
                    departVu = true;
                }

                if (departVu && vu is not null && vu == candidat)
                {
                    return vu;
                }

                candidat = vu;
                await Task.Delay(_periode, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>La fin d'une rencontre, selon le type du jeu : la place gagnante, <see cref="MultiplayerResultats.MatchNul"/>, ou null.</summary>
    public Task<int?> AttendreLaFinAsync(MultiplayerGame jeu, IReadOnlyCollection<int> places, CancellationToken ct)
        => jeu.End is { } manches ? AttendreLeVainqueurAsync(manches, places, ct)
            : jeu.MatchEnd is { EstConnu: true } autre ? AttendreLeScoreFinalAsync(autre, places, ct)
            : Task.FromResult<int?>(null);

    /// <summary>
    /// LA FIN AU SCORE (score_final, 23/10) : le signal de fin, deja vu retombe au moins une fois sous la garde, puis le
    /// score de chaque place. Le plus haut, seul, gagne ; une egalite est un match nul. Deux lectures de suite concordent.
    /// </summary>
    public async Task<int?> AttendreLeScoreFinalAsync(MultiplayerMatchEnd fin, IReadOnlyCollection<int> places, CancellationToken ct)
    {
        int? candidat = null;
        var departVu = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (fin.Duel is { Decalage: { } garde } && await LireAsync(garde, ct).ConfigureAwait(false) != fin.Duel.Value)
                {
                    candidat = null;
                    await Task.Delay(_periode, ct).ConfigureAwait(false);
                    continue;
                }

                var signal = await LireAsync(fin.Over!.Decalage!.Value, ct).ConfigureAwait(false);
                if (signal != fin.Over.Value)
                {
                    // Le match n'est pas fini : un signal vu ensuite sera le sien, pas celui du match d'avant.
                    departVu = signal is not null || departVu;
                    candidat = null;
                    await Task.Delay(_periode, ct).ConfigureAwait(false);
                    continue;
                }

                int? resultat = null;
                if (departVu)
                {
                    var scores = new List<(int Place, long Score)>();
                    foreach (var place in places.OrderBy(p => p))
                    {
                        var adresse = fin.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage;
                        if (adresse is not null && await LireLesOctetsAsync(adresse.Value, fin.Score.Bytes, ct).ConfigureAwait(false) is { } octets
                            && fin.Score.Valeur(octets) is { } score)
                        {
                            scores.Add((place, score));
                        }
                    }

                    if (scores.Count == places.Count)
                    {
                        var haut = scores.Max(s => s.Score);
                        var enTete = scores.Where(s => s.Score == haut).ToList();
                        resultat = enTete.Count == 1 ? enTete[0].Place : MultiplayerResultats.MatchNul;
                    }
                }

                if (resultat is not null && resultat == candidat)
                {
                    return resultat;
                }

                candidat = resultat;
                await Task.Delay(_periode, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>Plusieurs octets de la memoire du coeur, dans l'ordre de la memoire, ou null si la reponse est incomplete.</summary>
    private async Task<IReadOnlyList<int>?> LireLesOctetsAsync(long adresse, int n, CancellationToken ct)
    {
        var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {adresse:x} {n}", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (morceaux is not { Length: >= 3 } || morceaux[0] != "READ_CORE_RAM" || morceaux.Length < 2 + n)
        {
            return null;
        }

        var octets = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            if (!int.TryParse(morceaux[2 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var o))
            {
                return null;
            }

            octets.Add(o);
        }

        return octets;
    }

    /// <summary>Un octet de la memoire du coeur, ou null si RetroArch ne repond pas.</summary>
    private async Task<int?> LireAsync(long adresse, CancellationToken ct)
    {
        var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {adresse:x} 1", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return morceaux is { Length: >= 3 } && morceaux[0] == "READ_CORE_RAM"
            && int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur)
                ? valeur
                : null;
    }
}

/// <summary>Ce qu'une place a fait pendant le duel.</summary>
public sealed record StatsDeLaPlace(int Coups, int KO, int Perfects, int Degats);

/// <summary>
/// LES STATISTIQUES D'UN DUEL, suivies chez l'hote pendant la rencontre (00quater.3, ecran 10) : la vie des deux places,
/// lue une douzaine de fois par seconde, et leurs compteurs de manches. Une baisse de la vie adverse est un coup reussi
/// (un combo en compte plusieurs, les degats de garde aussi), un passage a la valeur du K.O. un K.O., une manche gagnee
/// avec la vie pleine un perfect. Une lecture manquee ne compte rien : on ne devine pas.
/// </summary>
public sealed class MultiplayerStatsDuDuel
{
    private readonly ICommandesRetroArch _commandes;
    private readonly TimeSpan _periode;

    public MultiplayerStatsDuDuel(ICommandesRetroArch commandes, TimeSpan? periode = null)
    {
        _commandes = commandes;
        _periode = periode ?? TimeSpan.FromMilliseconds(80);
    }

    /// <summary>Suit le duel jusqu'a l'annulation (la fin de la partie), puis rend les statistiques de chaque place.</summary>
    public async Task<IReadOnlyDictionary<int, StatsDeLaPlace>> SuivreAsync(MultiplayerGame jeu, IReadOnlyList<int> places, CancellationToken ct)
    {
        var vie = jeu.Stats?.Life;
        var resultat = places.ToDictionary(p => p, _ => new int[4]);   // coups, K.O., perfects, degats
        if (vie is null || places.Count != 2)
        {
            return Rendre(resultat);
        }

        var avant = new Dictionary<int, int?>();          // la derniere vie CONFIRMEE (lue deux fois de suite)
        var brut = new Dictionary<int, int?>();           // la derniere lecture, confirmee ou non
        var manchesAvant = new Dictionary<int, int?>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var vies = new Dictionary<int, int?>();
                var manches = new Dictionary<int, int?>();
                foreach (var place in places)
                {
                    vies[place] = await LireAsync(vie.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage, vie.Bytes, ct).ConfigureAwait(false);
                    manches[place] = await LireAsync(jeu.End?.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage, 1, ct).ConfigureAwait(false);
                }

                // Une vie n'est retenue que lue deux fois de suite : une lecture au vol (le jeu remet la vie a zero entre
                // deux manches, octet par octet) ne compte pas un coup fantome.
                var confirmees = new Dictionary<int, int?>();
                foreach (var place in places)
                {
                    confirmees[place] = vies[place] is { } lue && lue == brut.GetValueOrDefault(place) ? lue : null;
                    brut[place] = vies[place];
                }

                foreach (var place in places)
                {
                    var adversaire = places.First(p => p != place);
                    if (avant.GetValueOrDefault(place) is { } precedente && confirmees[place] is { } actuelle && actuelle != precedente && precedente != vie.Ko)
                    {
                        if (actuelle == vie.Ko)
                        {
                            resultat[adversaire][0]++;
                            resultat[adversaire][1]++;
                            resultat[adversaire][3] += Math.Clamp(precedente, 0, vie.Full);
                        }
                        else if (actuelle < precedente)
                        {
                            resultat[adversaire][0]++;
                            resultat[adversaire][3] += precedente - actuelle;
                        }
                    }

                    // Une manche gagnee, la vie pleine : un perfect.
                    if (manchesAvant.GetValueOrDefault(place) is { } m && manches[place] is { } n && n == m + 1 && (confirmees[place] ?? avant.GetValueOrDefault(place)) == vie.Full)
                    {
                        resultat[place][2]++;
                    }
                }

                foreach (var place in places)
                {
                    avant[place] = confirmees[place] ?? avant.GetValueOrDefault(place);
                    manchesAvant[place] = manches[place] ?? manchesAvant.GetValueOrDefault(place);
                }

                await Task.Delay(_periode, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return Rendre(resultat);
    }

    private static IReadOnlyDictionary<int, StatsDeLaPlace> Rendre(Dictionary<int, int[]> brut)
        => brut.ToDictionary(paire => paire.Key, paire => new StatsDeLaPlace(paire.Value[0], paire.Value[1], paire.Value[2], paire.Value[3]));

    private async Task<int?> LireAsync(long? decalage, int octets, CancellationToken ct)
    {
        if (decalage is null)
        {
            return null;
        }

        var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {decalage.Value:x} {octets}", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (morceaux is null || morceaux.Length < 2 + octets || morceaux[0] != "READ_CORE_RAM")
        {
            return null;
        }

        var valeur = 0;
        for (var i = 0; i < octets; i++)
        {
            if (!int.TryParse(morceaux[2 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var o))
            {
                return null;
            }

            valeur |= o << (8 * i);
        }

        return valeur;
    }
}
