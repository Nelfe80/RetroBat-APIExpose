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
            var octets = Encoding.UTF8.GetBytes(commande);
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
            // En UTF-8 : un message a l'ecran (SHOW_MSG) porte des accents ; les commandes, elles, sont en ASCII.
            using var udp = new UdpClient();
            var octets = Encoding.UTF8.GetBytes(commande);
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
        // 60 s pour que le jeu soit pret (demande du user, 25/10 : un lancement a pris 48 s sur sa borne).
        _attenteDuJeu = attenteDuJeu ?? TimeSpan.FromSeconds(60);
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
        MultiplayerStart? depart = null,
        IReadOnlyDictionary<int, string>? pseudos = null)
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

        var raison = await AppliquerAsync(jeu, choixParPlace, pseudos, depart.DelaiDesChoix ?? _apresLeChargement, ct).ConfigureAwait(false);
        return new Resultat(raison is null, raison, fichier);
    }

    /// <summary>Le temps laisse au rechargement du depart, dans la boucle qui attend les invites.</summary>
    internal static TimeSpan ApresUnRechargement { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// LA BOUCLE DU DEPART (1.9.112) : reprend le depart deja pose (l'emplacement <see cref="Emplacement"/>), y reecrit le
    /// choix de chaque place et le relit. Seulement tant qu'aucun invite n'est connecte : un etat charge chez l'hote, invite
    /// connecte, le fait decrocher (« Netplay state load out of order »). Une reprise rapide qui ne se relit pas (un jeu
    /// qui n'accepte l'ecriture qu'un moment apres le chargement) se refait au rythme de la premiere pose, verifiee : le
    /// jeu ne reste jamais sur un depart sans les choix. Vrai si le depart est repris, choix compris.
    ///
    /// Un depart au delai des choix du catalogue (<see cref="MultiplayerStart.WriteAfterFrames"/>, CDC Classements v1.1 §1)
    /// le garde a chaque reprise et a son repli : le jeu n'accepte les choix que dans cette fenetre.
    /// </summary>
    public async Task<bool> ReappliquerAsync(
        MultiplayerGame jeu,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace,
        CancellationToken ct,
        IReadOnlyDictionary<int, string>? pseudos = null,
        MultiplayerStart? depart = null)
        => await AppliquerAsync(jeu, choixParPlace, pseudos, depart?.DelaiDesChoix ?? ApresUnRechargement, ct).ConfigureAwait(false) is null
           || await AppliquerAsync(jeu, choixParPlace, pseudos, depart?.DelaiDesChoix ?? _apresLeChargement, ct).ConfigureAwait(false) is null;

    /// <summary>Charge le depart pose, ecrit les choix, puis les relit. Rend null, ou la raison d'un ecart.</summary>
    private async Task<string?> AppliquerAsync(
        MultiplayerGame jeu,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace,
        IReadOnlyDictionary<int, string>? pseudos,
        TimeSpan apresLeChargement,
        CancellationToken ct)
    {
        await _commandes.EnvoyerAsync("LOAD_STATE_SLOT " + Emplacement.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await Task.Delay(apresLeChargement, ct).ConfigureAwait(false);

        // Les octets qui se suivent partent en une commande (un pseudo : 8 octets) : des centaines d'ecritures d'un octet
        // faisaient attendre la premiere relecture au-dela de son delai (F-Zero a quatre, 24/10).
        var blocs = Blocs(EcrituresParCommande(jeu, choixParPlace, pseudos));
        foreach (var (decalage, octets, carte) in blocs)
        {
            await _commandes.EnvoyerAsync($"{(carte ? "WRITE_CORE_MEMORY" : "WRITE_CORE_RAM")} {decalage:x} {string.Join(' ', octets.Select(o => o.ToString("x2", CultureInfo.InvariantCulture)))}", ct)
                .ConfigureAwait(false);
        }

        // Verifiee (S5.4) : chaque valeur relue avant que quiconque se connecte ; un ecart annule le depart.
        foreach (var (decalage, octets, carte) in blocs)
        {
            var lus = await LireAsync(decalage, octets.Count, ct, carte).ConfigureAwait(false);
            for (var essai = 0; lus is null && essai < 2; essai++)
            {
                lus = await LireAsync(decalage, octets.Count, ct, carte).ConfigureAwait(false);
            }

            for (var i = 0; i < octets.Count; i++)
            {
                var lu = lus is not null && i < lus.Count ? lus[i] : (int?) null;
                if (lu != octets[i])
                {
                    return $"relecture differente a 0x{decalage + i:X} ({(lu is { } v ? v.ToString(CultureInfo.InvariantCulture) : "rien")} au lieu de {octets[i]})";
                }
            }
        }

        return null;
    }

    /// <summary>Les ecritures regroupees en suites d'octets contigus, par la meme commande, dans l'ordre.</summary>
    internal static IReadOnlyList<(long Decalage, IReadOnlyList<int> Octets, bool Carte)> Blocs(IReadOnlyList<(long Decalage, int Valeur, bool Carte)> ecritures)
    {
        var blocs = new List<(long, List<int>, bool)>();
        foreach (var (decalage, valeur, carte) in ecritures)
        {
            if (blocs.Count > 0 && blocs[^1] is var (debut, suite, memeCarte) && memeCarte == carte && debut + suite.Count == decalage && suite.Count < 64)
            {
                suite.Add(valeur);
            }
            else
            {
                blocs.Add((decalage, [valeur], carte));
            }
        }

        return blocs.Select(b => (b.Item1, (IReadOnlyList<int>) b.Item2, b.Item3)).ToList();
    }

    /// <summary>Ce qu'il faut ecrire : pour chaque choix et chaque place de la partie, la valeur du joueur ou le defaut.</summary>
    public static IReadOnlyList<(long Decalage, int Valeur)> Ecritures(
        MultiplayerGame jeu, IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace)
        => EcrituresParCommande(jeu, choixParPlace).Select(e => (e.Decalage, e.Valeur)).ToList();

    /// <summary>Les ecritures, chacune avec sa commande : la carte du coeur (<see cref="MultiplayerChoice.ParLaCarteDuCoeur"/>) ou la memoire systeme.</summary>
    public static IReadOnlyList<(long Decalage, int Valeur, bool Carte)> EcrituresParCommande(
        MultiplayerGame jeu, IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>?> choixParPlace,
        IReadOnlyDictionary<int, string>? pseudos = null, DateTimeOffset? maintenant = null)
    {
        var ecritures = new List<(long, int, bool)>();
        // Une console par joueur de la partie (mGBA Splitscreen) : c'est aussi le nombre de joueurs des adresses par nombre.
        var joueurs = choixParPlace.Count;
        // Les octets que le coeur tient toute la partie (adresse de la carte, valeur), dans l'ordre de sa table de verrous.
        var verrous = new List<(uint Cible, int Valeur)>();
        var choixTenus = new List<(uint Cible, int Valeur)>();
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
                    if (choix.Lock && choix.ParLaCarteDuCoeur)
                    {
                        choixTenus.Add(((uint) (adresse + console * 0x10000000L), valeur));
                    }
                }
            }
        }

        // Le roulement (la coupe) : la valeur de l'heure, ecrite une fois et/ou tenue par le coeur, dans chaque console
        // avec all_consoles.
        if (jeu.Rotation is { } roulement && roulement.A(maintenant ?? DateTimeOffset.UtcNow) is { } enCours)
        {
            var consoles = roulement.AllConsoles && roulement.ParLaCarteDuCoeur ? joueurs : 1;
            for (var console = 0; console < consoles; console++)
            {
                if (roulement.Decalage is { } adresse)
                {
                    ecritures.Add((adresse + console * 0x10000000L, enCours.Valeur.Value, roulement.ParLaCarteDuCoeur));
                }
            }

            if (roulement.DecalageDuVerrou is { } tenue)
            {
                for (var console = 0; console < consoles; console++)
                {
                    verrous.Add(((uint) (tenue + console * 0x10000000L), enCours.Valeur.Value));
                }
            }
        }

        // LES CHOIX TENUS (27/10, Mario Kart : les joueurs changeaient de pilote par rapport au panneau) : apres le roulement,
        // tous ou aucun. Un joueur tenu et l'autre libre fausserait la rencontre ; sans la place (16 entrees : quatre joueurs
        // dans quatre consoles plus la coupe), le choix reste preselectionne, comme avant.
        if (verrous.Count + choixTenus.Count <= MultiplayerRotation.VerrousMax)
        {
            verrous.AddRange(choixTenus);
        }

        // Un verrou : une entree de la table du coeur (adresse sur 4 octets, valeur, actif).
        for (var k = 0; k < verrous.Count; k++)
        {
            var (cible, valeur) = verrous[k];
            var entree = MultiplayerRotation.TableDesVerrous + k * 8L;
            int[] octets = [(int) (cible & 0xFF), (int) ((cible >> 8) & 0xFF), (int) ((cible >> 16) & 0xFF), (int) (cible >> 24), valeur, 1, 0, 0];
            ecritures.AddRange(octets.Select((octet, i) => (entree + i, octet, true)));
        }

        // Le pseudo de chaque joueur, un octet par caractere : la table de chaque console, puis le nom de sa propre console.
        if (jeu.Names is { } noms && pseudos is not null)
        {
            foreach (var (place, pseudo) in pseudos.OrderBy(p => p.Key))
            {
                var octets = noms.Coder(pseudo);
                foreach (var adresse in noms.Seats.Where(s => s.Seat == place && s.Decalage is not null))
                {
                    var consoles = adresse.DansChaqueConsole ? Enumerable.Range(0, joueurs) : [noms.ParLaCarteDuCoeur ? place - 1 : 0];
                    foreach (var console in consoles)
                    {
                        for (var i = 0; i < octets.Length; i++)
                        {
                            ecritures.Add((adresse.Decalage!.Value + console * 0x10000000L + i, octets[i], noms.ParLaCarteDuCoeur));
                        }
                    }
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

    private async Task<IReadOnlyList<int>?> LireAsync(long decalage, int nombre, CancellationToken ct, bool carte = false)
    {
        var commande = carte ? "READ_CORE_MEMORY" : "READ_CORE_RAM";
        var reponse = await _commandes.DemanderAsync($"{commande} {decalage:x} {nombre}", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (morceaux is not { Length: >= 3 } || morceaux[0] != commande)
        {
            return null;
        }

        var lus = new List<int>();
        foreach (var morceau in morceaux.Skip(2).Take(nombre))
        {
            if (!int.TryParse(morceau, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur))
            {
                return null;
            }

            lus.Add(valeur);
        }

        return lus.Count == nombre ? lus : null;
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
    private readonly TimeSpan _periodeDuClassement;

    public MultiplayerFinDeRencontre(ICommandesRetroArch commandes, TimeSpan? periode = null)
    {
        _commandes = commandes;
        _periode = periode ?? TimeSpan.FromMilliseconds(500);
        // Un classement retient une valeur apres settle_ms (une seconde par defaut) : quatre lectures par seconde la voient
        // tenir sans attendre une demi-seconde de plus.
        _periodeDuClassement = periode ?? TimeSpan.FromMilliseconds(250);
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
                if (fin.Duel is { Decalage: { } garde } && await LireAsync(garde, ct, fin.ParLaCarteDuCoeur).ConfigureAwait(false) != fin.Duel.Value)
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

                    if (await LireAsync(adresse.Value, ct, fin.ParLaCarteDuCoeur).ConfigureAwait(false) is { } valeur)
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
    public async Task<int?> AttendreLaFinAsync(MultiplayerGame jeu, IReadOnlyCollection<int> places, CancellationToken ct)
        => (await AttendreLIssueAsync(jeu, places, ct).ConfigureAwait(false))?.Gagnante;

    /// <summary>
    /// L'issue d'une rencontre, selon la fin du jeu : la place gagnante d'un duel (<c>end</c>, <c>match_end</c>), ou le rang
    /// de chaque humain (<c>ranking_end</c>). Null si la partie se ferme avant, ou si la rencontre ne se classe pas a ces
    /// places (moins de deux humains, tous dans le meme camp).
    /// </summary>
    public async Task<MultiplayerIssue?> AttendreLIssueAsync(MultiplayerGame jeu, IReadOnlyCollection<int> places, CancellationToken ct)
    {
        if (jeu.RankingEnd is { EstConnu: true } classement)
        {
            return await AttendreLeClassementAsync(classement, places, ct).ConfigureAwait(false) is { } rangs ? MultiplayerIssue.DesRangs(rangs) : null;
        }

        var gagnante = jeu.End is { } manches ? await AttendreLeVainqueurAsync(manches, places, ct).ConfigureAwait(false)
            : jeu.MatchEnd is { EstConnu: true } autre ? await AttendreLeScoreFinalAsync(autre, places, ct).ConfigureAwait(false)
            : null;
        return gagnante is { } g ? MultiplayerIssue.DuVainqueur(g) : null;
    }

    /// <summary>
    /// LE CLASSEMENT A PLUSIEURS (CDC Classements v1.0 §5) : la memoire lue a chaque passe (garde, signal, et chaque place du
    /// jeu, l'ordinateur compris), confiee a <see cref="MultiplayerClassementEnCours"/>, jusqu'aux rangs des humains. Null
    /// sans classement possible a ces places, ou si la partie se ferme avant.
    /// </summary>
    public async Task<IReadOnlyList<MultiplayerRang>?> AttendreLeClassementAsync(MultiplayerRankingEnd fin, IReadOnlyCollection<int> humains, CancellationToken ct)
    {
        if (!fin.Classable(humains))
        {
            return null;
        }

        var enCours = new MultiplayerClassementEnCours(fin, humains);
        var horloge = System.Diagnostics.Stopwatch.StartNew();
        var carte = fin.ParLaCarteDuCoeur;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var garde = fin.Guard?.Decalage is { } g ? await LireAsync(g, ct, carte).ConfigureAwait(false) : null;
                var signal = fin.Guard is null || garde == fin.Guard.Value
                    ? fin.Over?.Decalage is { } o ? await LireAsync(o, ct, carte).ConfigureAwait(false) : null
                    : null;
                var places = new Dictionary<int, MultiplayerReleveDePlace>();
                // Hors de la garde, les places ne se lisent pas : le classement les ignorerait.
                if (fin.Guard is null || garde == fin.Guard.Value)
                {
                    foreach (var place in fin.Seats)
                    {
                        long? valeur = null;
                        if (place.Decalage is { } adresse && await LireLesOctetsAsync(adresse, place.Bytes, ct, carte).ConfigureAwait(false) is { } octets)
                        {
                            valeur = place.Valeur(octets);
                        }

                        var mort = place.Dead?.Decalage is { } m ? await LireAsync(m, ct, carte).ConfigureAwait(false) : null;
                        var fini = place.Finished?.Decalage is { } f ? await LireAsync(f, ct, carte).ConfigureAwait(false) : null;
                        places[place.Seat] = new MultiplayerReleveDePlace(valeur, mort, fini);
                    }
                }

                if (enCours.Observer(new MultiplayerReleve(garde, signal, places), horloge.ElapsedMilliseconds) is { } rangs)
                {
                    return rangs;
                }

                await Task.Delay(_periodeDuClassement, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

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
                if (fin.Duel is { Decalage: { } garde } && await LireAsync(garde, ct, fin.ParLaCarteDuCoeur).ConfigureAwait(false) != fin.Duel.Value)
                {
                    candidat = null;
                    await Task.Delay(_periode, ct).ConfigureAwait(false);
                    continue;
                }

                var signal = await LireAsync(fin.Over!.Decalage!.Value, ct, fin.ParLaCarteDuCoeur).ConfigureAwait(false);
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
                        if (adresse is not null && await LireLesOctetsAsync(adresse.Value, fin.Score.Bytes, ct, fin.ParLaCarteDuCoeur).ConfigureAwait(false) is { } octets
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

    /// <summary>
    /// Plusieurs octets de la memoire du coeur, dans l'ordre de la memoire, ou null si la reponse est incomplete. Avec
    /// <paramref name="carte"/>, l'adresse est celle de la carte du coeur (READ_CORE_MEMORY, CDC Classements v1.1 §2).
    /// </summary>
    private async Task<IReadOnlyList<int>?> LireLesOctetsAsync(long adresse, int n, CancellationToken ct, bool carte = false)
    {
        var commande = carte ? "READ_CORE_MEMORY" : "READ_CORE_RAM";
        var reponse = await _commandes.DemanderAsync($"{commande} {adresse:x} {n}", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (morceaux is not { Length: >= 3 } || morceaux[0] != commande || morceaux.Length < 2 + n)
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

    /// <summary>Un octet de la memoire du coeur (de sa carte avec <paramref name="carte"/>), ou null si RetroArch ne repond pas.</summary>
    private async Task<int?> LireAsync(long adresse, CancellationToken ct, bool carte = false)
    {
        var commande = carte ? "READ_CORE_MEMORY" : "READ_CORE_RAM";
        var reponse = await _commandes.DemanderAsync($"{commande} {adresse:x} 1", ct).ConfigureAwait(false);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return morceaux is { Length: >= 3 } && morceaux[0] == commande
            && int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur)
                ? valeur
                : null;
    }
}

/// <summary>Ce qu'une place a fait pendant le duel.</summary>
public sealed record StatsDeLaPlace(int Coups, int KO, int Perfects, int Degats);

/// <summary>
/// LES STATISTIQUES D'UN DUEL d'avant les profils (00quater.3, ecran 10) : coups, K.O., perfects et degats de chaque place.
/// Depuis le lot 2 du CDC des statistiques, c'est le profil IMPLICITE du jeu (son bloc <c>stats.life</c>) que suit
/// <see cref="MultiplayerStatsMoteur"/> : memes regles, memes chiffres. Garde pour le banc et les essais.
/// </summary>
public sealed class MultiplayerStatsDuDuel
{
    private readonly MultiplayerStatsMoteur _moteur;

    public MultiplayerStatsDuDuel(ICommandesRetroArch commandes, TimeSpan? periode = null)
        => _moteur = new MultiplayerStatsMoteur(commandes, periode);

    /// <summary>Suit le duel jusqu'a l'annulation (la fin de la partie), puis rend les statistiques de chaque place.</summary>
    public async Task<IReadOnlyDictionary<int, StatsDeLaPlace>> SuivreAsync(MultiplayerGame jeu, IReadOnlyList<int> places, CancellationToken ct)
    {
        if (MultiplayerStatsProfil.Implicite(jeu) is not { } profil || places.Count != 2)
        {
            return places.ToDictionary(p => p, _ => new StatsDeLaPlace(0, 0, 0, 0));
        }

        var resultat = await _moteur.Suivre(profil, places, ct).Fin.ConfigureAwait(false);
        return places.ToDictionary(p => p, resultat.Ancien);
    }
}
