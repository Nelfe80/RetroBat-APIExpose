using System.Collections.Concurrent;
using System.Globalization;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Adventure;

/// <summary>
/// CE QUE <see cref="MultiplayerLaunchService"/> DEMANDE A ADVENTURE (une gameroom d'aventure se lance comme toutes les
/// gamerooms : meme hote, meme depart, meme netplay). Chaque point d'entree ne fait rien pour une gameroom qui n'est pas une
/// aventure, et ne fait jamais echouer une partie qui a deja commence.
/// </summary>
public interface IAdventureHote
{
    /// <summary>La gameroom est-elle celle d'une aventure ?</summary>
    bool EstUneAventure(MultiplayerRoom room);

    /// <summary>
    /// AVANT le lancement : l'aventure, sa derniere version, la decision de reprise, l'etat ou la sauvegarde de la cartouche
    /// prets sur le disque. Faux : la reprise ne peut pas se faire (objet introuvable, empreinte differente) et la gameroom est quittee.
    /// </summary>
    Task<bool> PreparerAsync(MultiplayerRoom room, MultiplayerInstalledGame jeu, CancellationToken ct);

    /// <summary>L'etat a charger AVANT que les invites se connectent (le depart pret d'une reprise a l'etat), ou null.</summary>
    MultiplayerStart? DepartDe(MultiplayerRoom room);

    /// <summary>Les reglages de RetroArch propres a l'aventure (dossier de sauvegardes de l'aventure), rendus a la fin de la partie.</summary>
    IReadOnlyDictionary<string, string> ReglagesDeLaPlace(MultiplayerRoom room);

    /// <summary>
    /// Les reglages de RetroArch de l'INVITE : ses sauvegardes vont aussi dans le dossier de l'aventure (la sauvegarde personnelle du
    /// joueur n'est jamais ecrasee), sans autosave d'intervalle. L'etat qu'il joue lui vient de l'hote par le netplay.
    /// </summary>
    IReadOnlyDictionary<string, string> ReglagesDeLInvite(MultiplayerRoom room);

    /// <summary>
    /// Les reglages que le LANCEUR ne laisse pas passer par retroarch.cfg (il les reecrit) : des paires <c>retroarch.&lt;cle&gt;</c> pour sa ligne de
    /// commande (<see cref="MultiplayerLaunchPlan.ReglagesRetroArch"/>). Hote : sauvegardes, etats, indexation des etats. Invite : ses sauvegardes.
    /// </summary>
    IReadOnlyDictionary<string, string> ReglagesDuLanceur(MultiplayerRoom room, bool hote);

    /// <summary>
    /// Le jeu tel que cette place le lance : l'invite ne garde jamais l'<c>autosave</c> du catalogue (il chargerait a son depart un etat
    /// de sortie qui n'est pas celui de la partie) ; l'hote le garde, pour la fin de seance quand le joueur ferme lui-meme.
    /// </summary>
    MultiplayerGame JeuPourLaPlace(MultiplayerRoom room, MultiplayerGame jeu, bool hote);

    /// <summary>L'emulateur est la : le suivi demarre (toutes les 5 minutes, et quand le jeu ecrit sa sauvegarde).</summary>
    void Demarree(MultiplayerRoom room, MultiplayerInstalledGame jeu);

    /// <summary>La borne va fermer le jeu : la version de fin de seance (SAVE_STATE avant de fermer).</summary>
    Task AvantDeFermerAsync(MultiplayerRoom room);

    /// <summary>Le jeu est ferme : la version de sortie si RetroArch en a ecrit une, l'envoi, la fin de la seance au site.</summary>
    Task TermineeAsync(MultiplayerRoom room);
}

/// <summary>
/// LA SEANCE D'AVENTURE, chez l'hote (CDC v1.0 §2) : reprise, sauvegardes, envoi, fin. Ne redeveloppe rien de MULTIPLAYER : le
/// depart est un <see cref="MultiplayerStart"/> que <see cref="MultiplayerDepartPret"/> pose avant les invites, comme un depart
/// pret, avec un etat qui est celui de l'aventure.
/// </summary>
public sealed class AdventureSeanceService : BackgroundService, IAdventureHote
{
    /// <summary>Le pas du suivi : le jeu a-t-il ecrit sa sauvegarde ? la periode de 5 minutes est-elle echue ?</summary>
    internal static readonly TimeSpan PasDuSuivi = TimeSpan.FromSeconds(10);

    /// <summary>Un chargement (le jeu demarre, l'etat se pose, les invites arrivent) : pas de sauvegarde reguliere pendant ce temps.</summary>
    internal static readonly TimeSpan DelaiDeChargement = TimeSpan.FromSeconds(120);

    /// <summary>Le jeu a ecrit sa sauvegarde : on laisse le temps d'ecrire entierement avant de prendre l'etat.</summary>
    private static readonly TimeSpan SilenceApresUneEcriture = TimeSpan.FromSeconds(2);

    private readonly AdventureCatalogueStore? _catalogue;
    private readonly IAdventureSite _site;
    private readonly AdventureStockage _stockage;
    private readonly AdventureFileDEnvoi _file;
    private readonly Func<ICommandesRetroArch> _commandes;
    private readonly Func<string?, string?> _empreinteDuCoeur;
    private readonly Func<DateTime> _maintenant;
    private readonly Func<string?> _langueDeLaBorne;
    private readonly ILogger<AdventureSeanceService>? _journal;
    private readonly ConcurrentDictionary<string, Seance> _seances = new(StringComparer.Ordinal);

    internal sealed class Seance
    {
        public required AdventureInfo Aventure { get; init; }
        public required AdventureSauveur Sauveur { get; init; }
        public required DecisionDeReprise Decision { get; init; }
        public MultiplayerStart? Depart { get; init; }

        /// <summary>La langue du depart choisi (« fr »), ou null : depart a froid ou version sans langue nommee.</summary>
        public string? Langue { get; init; }
        public required string DossierDesSauvegardes { get; init; }
        public required string NomDeLaRom { get; init; }
        public DateTime Ouverte { get; init; }
        public CancellationTokenSource? Suivi { get; set; }
        public bool Fermee { get; set; }
    }

    public AdventureSeanceService(IAdventureSite site, AdventureStockage stockage, AdventureFileDEnvoi file,
        Func<ICommandesRetroArch>? commandes = null, Func<string?, string?>? empreinteDuCoeur = null, Func<DateTime>? maintenant = null,
        ILogger<AdventureSeanceService>? journal = null, AdventureCatalogueStore? catalogue = null, Func<string?>? langueDeLaBorne = null)
    {
        _langueDeLaBorne = langueDeLaBorne ?? (() => null);
        _catalogue = catalogue;
        _site = site;
        _stockage = stockage;
        _file = file;
        _commandes = commandes ?? (() => new CommandesRetroArchUdp());
        _empreinteDuCoeur = empreinteDuCoeur ?? (coeur => RetroBat.Api.Speedrun.EtatsParCoeur.EmpreinteDuCoeurInstalle(coeur));
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
        _journal = journal;
    }

    public bool EstUneAventure(MultiplayerRoom room) => room.AdventureId is not null;

    internal Seance? De(string room) => _seances.TryGetValue(room, out var seance) ? seance : null;

    // ── l'envoi : toujours, hors partie comme en partie ────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_file.EnAttente.Count > 0)
                {
                    var parties = await _file.EnvoyerAsync(_site, _maintenant(), stoppingToken).ConfigureAwait(false);
                    if (parties > 0)
                    {
                        _journal?.LogInformation("ADVENTURE : {Versions} version(s) envoyee(s) au site", parties);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // L'envoi ne fait jamais tomber l'API ni la partie ; la file garde tout.
                _journal?.LogWarning(ex, "ADVENTURE : tour d'envoi en erreur");
            }
        }
    }

    // ── avant le lancement ─────────────────────────────────────────────────────────────────────────

    public async Task<bool> PreparerAsync(MultiplayerRoom room, MultiplayerInstalledGame jeu, CancellationToken ct)
    {
        if (!EstUneAventure(room))
        {
            return true;
        }

        var id = room.AdventureId!;
        if (!AdventureStockage.NomSur(id))
        {
            _journal?.LogWarning("ADVENTURE : identifiant d'aventure refuse pour {Room}", room.Room);
            return false;
        }

        var joueurs = Math.Max(2, room.Launch?.Players ?? 0);
        var aventure = await _site.DetailAsync(id, ct).ConfigureAwait(false);
        // Hors ligne, l'aventure se reprend de la copie locale : rien n'est perdu, l'envoi rattrapera.
        var locale = _stockage.Derniere(id);
        var distante = (await _site.DerniereAsync(id, joueurs, ct).ConfigureAwait(false))?.Version ?? aventure?.Latest;
        var version = distante is not null && (locale is null || distante.CreatedUtc >= locale.CreatedUtc) ? distante : locale ?? distante;
        aventure ??= new AdventureInfo { Id = id, Game = room.Game, Content = room.Content, Seats = joueurs };

        var coeur = _empreinteDuCoeur(jeu.Core?.Core);
        // Le coeur de l'aventure est fige a sa creation : une version sans empreinte de coeur (le site ne la redonne pas) vaut pour lui.
        var decision = AdventureReprise.Decider(version is null ? null : version with { CoreSha256 = version.CoreSha256 ?? aventure.CoreSha256 }, joueurs, coeur);
        var nomDeLaRom = Path.GetFileNameWithoutExtension(jeu.Path);
        var dossier = _stockage.DossierDesSauvegardes(id);
        var specification = Specification(jeu);
        // La langue de l'aventure : celle que le site garde, sinon celle que cette borne a gardee a la premiere seance.
        var langueDeLAventure = aventure.Language ?? _stockage.Langue(id);
        string? langueDuDepart = null;
        var fichier = FichierDeLaCartouche(specification, nomDeLaRom);
        if (fichier is null)
        {
            _journal?.LogWarning("ADVENTURE : nom de sauvegarde refuse pour {Jeu}", jeu.Game.Id);
            return false;
        }

        Directory.CreateDirectory(dossier);
        var sansFichier = fichier.Length == 0;
        var cartouche = sansFichier ? string.Empty : Path.Combine(dossier, fichier);
        // L'etat de sortie d'une seance d'avant ne se charge jamais a ce depart (autosave du catalogue : RetroArch le rechargerait).
        EffacerLEtatDeSortie(id, nomDeLaRom);
        MultiplayerStart? depart = null;
        switch (decision.Mode)
        {
            case ModeDeReprise.Etat:
            {
                var etat = await ObjetAsync(id, version!.StateSha256, ct).ConfigureAwait(false);
                if (etat is null)
                {
                    _journal?.LogWarning("ADVENTURE : l'etat de la version {Version} n'a pas pu etre retrouve", version.Id);
                    return false;
                }

                depart = new MultiplayerStart
                {
                    State = Path.Combine(_stockage.DossierDesObjets, version.StateSha256!.ToLowerInvariant()),
                    Sha256 = version.StateSha256!,
                    Players = joueurs,
                };
                // L'etat porte la sauvegarde de chaque console : celle de la cartouche est posee aussi, pour la relecture du jeu.
                if (!sansFichier && await ObjetAsync(id, version.SaveSha256, ct).ConfigureAwait(false) is { } sauvegarde)
                {
                    File.WriteAllBytes(cartouche, sauvegarde);
                }

                break;
            }
            case ModeDeReprise.NeufAvecSauvegarde:
            {
                var sauvegarde = await ObjetAsync(id, version!.SaveSha256, ct).ConfigureAwait(false);
                if (sauvegarde is null)
                {
                    _journal?.LogWarning("ADVENTURE : la sauvegarde de la version {Version} n'a pas pu etre retrouvee", version.Id);
                    return false;
                }

                if (!sansFichier)
                {
                    File.WriteAllBytes(cartouche, sauvegarde);
                }

                (depart, langueDuDepart) = DepartDuCatalogue(jeu, joueurs, room, langueDeLAventure);
                // CHARGER UN ETAT RESTAURE SON SAVEDATA (prouve au banc le 26/11 : la sauvegarde posee est ecrasee) : la sauvegarde de
                // l'aventure est placee DANS le depart pret avant qu'on le charge. Impossible (autre cœur, format inconnu) : pas de depart
                // pret, un depart a froid, et le jeu relit le .srm pose.
                if (depart is not null && !sansFichier)
                {
                    depart = DepartAvecLaSauvegarde(depart, sauvegarde, id, specification);
                }

                break;
            }
            default:
                // Consoles vierges : aucune sauvegarde ne traine d'une seance d'avant.
                if (!sansFichier)
                {
                    SupprimerSiPresent(cartouche);
                }

                (depart, langueDuDepart) = DepartDuCatalogue(jeu, joueurs, room, langueDeLAventure);
                break;
        }

        // Une aventure garde la langue de sa creation : fixee a la premiere partie neuve, jamais remplacee.
        if (decision.Mode != ModeDeReprise.Etat && depart is not null)
        {
            _stockage.FixerLaLangue(id, langueDuDepart);
        }

        var sauveur = new AdventureSauveur(_commandes(), _stockage, _file,
            new AdventureSauvegarde(id, nomDeLaRom, fichier, joueurs, coeur, version?.DurationS ?? 0, () => PlacesDe(room), version?.Seq, room.Adventure?.Session),
            _maintenant);
        sauveur.NoterLeDepart();
        _seances[room.Room] = new Seance
        {
            Aventure = aventure,
            Sauveur = sauveur,
            Decision = decision,
            Depart = depart,
            Langue = langueDuDepart ?? langueDeLAventure,
            DossierDesSauvegardes = dossier,
            NomDeLaRom = nomDeLaRom,
            Ouverte = _maintenant(),
        };
        _journal?.LogInformation("ADVENTURE : seance de {Aventure} sur {Room} : {Raison}", id, room.Room, decision.Raison);
        return true;
    }

    private async Task<byte[]?> ObjetAsync(string aventure, string? empreinte, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(empreinte))
        {
            return null;
        }

        if (_stockage.LireObjet(empreinte) is { } local)
        {
            return local;
        }

        var telecharge = await _site.TelechargerObjetAsync(aventure, empreinte, ct).ConfigureAwait(false);
        // Un objet qui ne correspond pas a son empreinte n'est jamais garde : on ne charge pas ce qu'on n'a pas verifie.
        if (telecharge is null || !string.Equals(AdventureStockage.Empreinte(telecharge), empreinte, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _stockage.PoserObjet(telecharge);
        return telecharge;
    }

    /// <summary>La part d'Adventure du jeu, pour CE contenu : sa version localisee peut surcharger la sauvegarde de la cartouche.</summary>
    private AdventureSpec Specification(MultiplayerInstalledGame jeu)
        => (_catalogue?.Entrees(out _).FirstOrDefault(e => e.Jeu.Id == jeu.Game.Id)?.Spec ?? SpecificationParDefaut).PourLeContenu(jeu.ContentKey);

    private static readonly AdventureSpec SpecificationParDefaut = new() { Cartridge = new AdventureCartridge() };

    /// <summary>Le nom du fichier de la cartouche : un nom seul, jamais un chemin (la valeur vient du Data Pack).</summary>
    internal static string? FichierDeLaCartouche(AdventureSpec spec, string nomDeLaRom)
    {
        var cartouche = spec.Cartridge ?? new AdventureCartridge();
        // Un jeu a mot de passe (Lost Vikings) n'a aucun fichier : la reprise se fait par l'etat, rien n'est pose ni relu.
        if (cartouche.SansFichier)
        {
            return string.Empty;
        }

        var modele = cartouche.File!.Replace("{rom}", nomDeLaRom, StringComparison.Ordinal);
        return modele.Length > 0 && modele == Path.GetFileName(modele) && !modele.Contains("..", StringComparison.Ordinal)
               && modele.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? modele
            : null;
    }

    /// <summary>
    /// LE DEPART PRET DU CATALOGUE pour ce nombre de joueurs ET ce contenu (la version localisee du jeu) : une partie neuve repart de
    /// l'etat « en jeu » fabrique au banc, pas de l'ecran titre. Son chemin est rendu absolu (dossier du catalogue Adventure) : la
    /// pose de MULTIPLAYER le lit tel quel et verifie son empreinte. Null : le jeu n'en a pas pour ce nombre, partie neuve a l'ecran titre.
    /// </summary>
    private (MultiplayerStart? Depart, string? Langue) DepartDuCatalogue(MultiplayerInstalledGame jeu, int joueurs, MultiplayerRoom room, string? langueDeLAventure)
    {
        if (!jeu.Game.EstNelfePlay)
        {
            return (null, null);
        }

        // LA LANGUE (1.9.223) : la version du contenu lance choisit son depart selon la langue des joueurs (CDC §0, langues melees).
        var version = (_catalogue?.Entrees(out _).FirstOrDefault(e => e.Jeu.Id == jeu.Game.Id)?.Spec ?? SpecificationParDefaut).VersionDuContenu(jeu.ContentKey);
        var choix = AdventureLangues.ChoisirLeDepart(version, jeu.Game, joueurs, jeu.ContentKey, jeu.EmpreinteDuCoeur(), JoueursDeLaSeance(room), langueDeLAventure);
        _journal?.LogInformation("ADVENTURE : depart de {Jeu} a {Joueurs} joueurs : {Raison}", jeu.Game.Id, joueurs, choix.Raison);
        if (choix.Depart is not { } depart || string.IsNullOrWhiteSpace(depart.State) || Path.IsPathRooted(depart.State))
        {
            return (null, null);
        }

        var dossier = _catalogue is null ? Path.Combine(RetroBatPaths.PluginRoot, "resources", "adventure") : Path.GetDirectoryName(_catalogue.Path)!;
        var chemin = Path.GetFullPath(Path.Combine(dossier, depart.State.Replace('/', Path.DirectorySeparatorChar)));
        return chemin.StartsWith(Path.GetFullPath(dossier), StringComparison.OrdinalIgnoreCase) ? (depart with { State = chemin }, choix.Langue) : (null, null);
    }

    /// <summary>
    /// Les joueurs de la seance et ce qu'ils lisent : le joueur de CETTE borne (<c>You</c>, l'hote qui prepare) par la langue d'ES et de l'API,
    /// les autres par ce que le site rend de leur entree (<c>language</c>) ; une langue inconnue ne bloque aucun choix.
    /// </summary>
    internal IReadOnlyList<AdventureJoueurLangue> JoueursDeLaSeance(MultiplayerRoom room)
        => room.Players.Where(j => j.Seat is not null || j.You || j.Host).Select(j => new AdventureJoueurLangue(
            j.Seat,
            j.You || (j.Host && !room.Players.Any(p => p.You)),
            j.You ? AdventureLangues.LanguesDe(_langueDeLaBorne(), j.Languages) : AdventureLangues.LanguesDe(j.Language, j.Languages))).ToList();

    /// <summary>
    /// Le depart pret dont le SAVEDATA de la console de la cartouche est celui de l'aventure : un fichier derive, range dans le dossier
    /// d'Adventure (<c>departs\</c>), son empreinte a lui. Seulement pour le .srm de mGBA Splitscreen (<c>gba_srm_slots</c>) ; null sinon.
    /// </summary>
    private MultiplayerStart? DepartAvecLaSauvegarde(MultiplayerStart depart, byte[] sauvegarde, string aventure, AdventureSpec spec)
    {
        if (spec.Cartridge?.EstUnSrmGba != true)
        {
            _journal?.LogInformation("ADVENTURE : sauvegarde de {Aventure} non placee dans le depart (format {Format}) : depart a froid", aventure, spec.Cartridge?.Kind ?? "inconnu");
            return null;
        }

        try
        {
            var retouche = AdventureEtat.PoserLaSauvegarde(File.ReadAllBytes(depart.State), sauvegarde, spec.Cartridge!.Console, tronquer: true);
            if (retouche is null)
            {
                _journal?.LogWarning("ADVENTURE : sauvegarde de {Aventure} non placee dans l'etat de depart : depart a froid", aventure);
                return null;
            }

            var sha = AdventureStockage.Empreinte(retouche);
            var dossier = Path.Combine(_stockage.Racine, "departs");
            Directory.CreateDirectory(dossier);
            var chemin = Path.Combine(dossier, sha + ".state");
            if (!File.Exists(chemin))
            {
                File.WriteAllBytes(chemin, retouche);
            }

            return depart with { State = chemin, Sha256 = sha };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogWarning(ex, "ADVENTURE : depart de {Aventure} non retouche : depart a froid", aventure);
            return null;
        }
    }

    /// <summary>Efface l'etat de sortie (<c>&lt;rom&gt;.state.auto</c>) d'une seance d'avant : dans le dossier des etats de RetroArch et ses sous-dossiers directs.</summary>
    private void EffacerLEtatDeSortie(string aventure, string nomDeLaRom)
    {
        try
        {
            // Le dossier des etats est celui de l'AVENTURE (reglage de la seance) : RetroArch y ecrit l'etat de sortie a la fermeture (prouve au
            // banc) et le RECHARGE tout seul au lancement suivant tant que l'autosave est allume : on l'efface avant chaque lancement.
            var dossier = _stockage.DossierDesEtats(aventure);
            Directory.CreateDirectory(dossier);
            foreach (var auto in Directory.EnumerateFiles(dossier, nomDeLaRom + ".state.auto*"))
            {
                File.Delete(auto);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(ex, "ADVENTURE : etat de sortie d'avant non efface");
        }
    }

    private static void SupprimerSiPresent(string chemin)
    {
        try
        {
            File.Delete(chemin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La partie neuve partira quand meme : le jeu relit ce qu'il trouve.
        }
    }

    private static IReadOnlyList<AdventurePlace> PlacesDe(MultiplayerRoom room)
        => room.Players.Where(j => j.Seat is not null).GroupBy(j => j.Seat!.Value)
            .Select(g => new AdventurePlace { Seat = g.Key, Handle = g.First().Handle }).OrderBy(p => p.Seat).ToList();

    public MultiplayerStart? DepartDe(MultiplayerRoom room) => De(room.Room)?.Depart;

    /// <summary>
    /// Les reglages de RetroArch de la seance : les sauvegardes du jeu vont dans le dossier de l'aventure (jamais celles du joueur),
    /// sans tri par coeur ni par contenu (le chemin du fichier de la cartouche est alors connu), et RetroArch les ecrit
    /// regulierement (10 s) pour que le jeu qui sauve se voie.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReglagesDeLaPlace(MultiplayerRoom room)
        => De(room.Room) is { } seance
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Seul ce que le lanceur ne reecrit pas passe par retroarch.cfg ; les dossiers passent par sa ligne de commande (ReglagesDuLanceur).
                ["autosave_interval"] = "10",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ReglagesDeLInvite(MultiplayerRoom room) => new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ReglagesDuLanceur(MultiplayerRoom room, bool hote)
    {
        if (room.AdventureId is not { } id || !AdventureStockage.NomSur(id))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var reglages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retroarch.savefile_directory"] = EnsureDir(_stockage.DossierDesSauvegardes(id)),
            ["retroarch.sort_savefiles_enable"] = "false",
            ["retroarch.sort_savefiles_by_content_enable"] = "false",
        };
        if (hote)
        {
            // SAVE_STATE ecrit <rom>.state dans le dossier des etats de l'AVENTURE : l'emplacement courant est le 0 et ne s'indexe pas (le lanceur
            // allume l'auto-index : RetroArch irait au plus haut emplacement existant).
            reglages["retroarch.savestate_directory"] = EnsureDir(_stockage.DossierDesEtats(id));
            reglages["retroarch.savestate_auto_index"] = "false";
            reglages["retroarch.state_slot"] = "0";
            // L'ETAT DE SORTIE (fin quand le joueur ferme lui-meme) : ecrit a la fermeture, JAMAIS recharge au lancement (« -autosave 1 » du
            // catalogue ne passait pas : le lanceur garde le premier -autosave, celui du temps partage, a 0).
            reglages["retroarch.savestate_auto_save"] = "true";
            reglages["retroarch.savestate_auto_load"] = "false";
            reglages["retroarch.sort_savestates_enable"] = "false";
            reglages["retroarch.sort_savestates_by_content_enable"] = "false";
        }

        return reglages;
    }

    private static string EnsureDir(string dossier)
    {
        Directory.CreateDirectory(dossier);
        return dossier;
    }

    public MultiplayerGame JeuPourLaPlace(MultiplayerRoom room, MultiplayerGame jeu, bool hote)
        => hote || jeu.LauncherOptions is not { } options || !options.ContainsKey("autosave")
            ? jeu
            : jeu with { LauncherOptions = options.Where(p => p.Key != "autosave").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };

    // ── pendant ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>L'indice de depart (<c>start_hint</c> du catalogue), dit a la borne qui lance (couture : Program.cs).</summary>
    public AdventureIndice? Indice { get; set; }

    public void Demarree(MultiplayerRoom room, MultiplayerInstalledGame jeu)
    {
        if (De(room.Room) is not { } seance)
        {
            return;
        }

        if (Specification(jeu).StartHint is { Length: > 0 } indice && Indice is { } dit)
        {
            var places = room.Launch?.Players is > 0 and var p ? p : room.Players.Count(j => j.Seat is not null);
            // Le jeu charge pendant quelques secondes : l'indice vient quand le joueur regarde l'ecran.
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false); dit.Dire(indice, places); });
        }

        seance.Suivi?.Cancel();
        seance.Suivi = new CancellationTokenSource();
        var jeton = seance.Suivi.Token;
        _ = Task.Run(() => SuivreAsync(seance, jeton), jeton);

        // ACTIVATION (27/11) : une aventure n'existe pour le joueur qu'une fois lancee avec les autres (deux joueurs au moins) ; le site la rend
        // alors active et lui donne son nom. Lancee seul, le brouillon reste un brouillon.
        var joueurs = room.Launch?.Players is > 0 and var n ? n : room.Players.Count(j => j.Seat is not null);
        if (AdventureRefus.DoitEtreActivee(joueurs, seance.Aventure.State))
        {
            _ = Task.Run(() => ActiverAsync(seance.Aventure.Id, joueurs));
        }
    }

    /// <summary>Demande au site de rendre l'aventure active. Idempotent chez lui ; un refus ou un site muet est journalise et ne bloque jamais la partie.</summary>
    internal async Task<bool> ActiverAsync(string aventure, int joueurs)
    {
        try
        {
            var (ok, raison) = await _site.ActiverAsync(aventure, joueurs, CancellationToken.None).ConfigureAwait(false);
            if (!ok && raison == "not_member" && await AccepterLInvitationAsync(aventure).ConfigureAwait(false))
            {
                // L'hote est arrive dans la salle par l'invitation de gameroom, sans etre participant de l'aventure (28/11) : son invitation du
                // site, si elle existe, est acceptee, puis l'activation est redemandee une fois.
                (ok, raison) = await _site.ActiverAsync(aventure, joueurs, CancellationToken.None).ConfigureAwait(false);
            }

            if (ok)
            {
                _journal?.LogInformation("ADVENTURE : aventure {Aventure} activee ({Joueurs} joueurs)", aventure, joueurs);
            }
            else
            {
                _journal?.LogWarning("ADVENTURE : activation de {Aventure} refusee ({Raison}), la partie continue", aventure, raison ?? "-");
            }

            return ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _journal?.LogWarning(ex, "ADVENTURE : activation de {Aventure} impossible, la partie continue", aventure);
            return false;
        }
    }

    private async Task<bool> AccepterLInvitationAsync(string aventure)
    {
        var miennes = await _site.MesAventuresAsync(CancellationToken.None).ConfigureAwait(false);
        var invitation = miennes?.Invitations.FirstOrDefault(i => string.Equals(i.Adventure, aventure, StringComparison.Ordinal));
        return invitation is not null && await _site.AccepterAsync(invitation.Invitation, true, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SuivreAsync(Seance seance, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DelaiDeChargement, ct).ConfigureAwait(false);
            seance.Sauveur.NoterLeDepart();
            DateTime? ecritureVue = null;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PasDuSuivi, ct).ConfigureAwait(false);
                await UnTourAsync(seance, ecritureVue, ct, v => ecritureVue = v).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _journal?.LogWarning(ex, "ADVENTURE : suivi de {Aventure} interrompu", seance.Aventure.Id);
        }
    }

    /// <summary>Un pas du suivi : le jeu a-t-il sauve (une ecriture, puis un court silence) ? la periode reguliere est-elle echue ?</summary>
    internal async Task UnTourAsync(Seance seance, DateTime? ecritureVue, CancellationToken ct, Action<DateTime?> noter)
    {
        var maintenant = _maintenant();
        if (ecritureVue is null && seance.Sauveur.LeJeuASauve())
        {
            ecritureVue = maintenant;
            noter(ecritureVue);
        }

        if (ecritureVue is { } vue && maintenant - vue >= SilenceApresUneEcriture)
        {
            noter(null);
            if (await seance.Sauveur.SauverAsync(AdventureRaisons.SauvegardeDuJeu, ct).ConfigureAwait(false) is { } version)
            {
                _journal?.LogInformation("ADVENTURE : le jeu a sauve, version {Version} de {Aventure}", version.Id, seance.Aventure.Id);
            }

            return;
        }

        var tourne = await seance.Sauveur.LeJeuTourneAsync(ct).ConfigureAwait(false);
        var enChargement = maintenant - seance.Ouverte < DelaiDeChargement;
        if (AdventureCadence.RegulierEchu(maintenant, seance.Sauveur.DerniereSauvegarde, tourne, enChargement)
            && await seance.Sauveur.SauverAsync(AdventureRaisons.Reguliere, ct).ConfigureAwait(false) is { } reguliere)
        {
            _journal?.LogInformation("ADVENTURE : sauvegarde reguliere {Version} de {Aventure}", reguliere.Id, seance.Aventure.Id);
        }
    }

    // ── la fin ─────────────────────────────────────────────────────────────────────────────────────

    public async Task AvantDeFermerAsync(MultiplayerRoom room)
    {
        if (De(room.Room) is not { } seance || seance.Fermee)
        {
            return;
        }

        seance.Suivi?.Cancel();
        try
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (await seance.Sauveur.SauverAsync(AdventureRaisons.Fin, limite.Token).ConfigureAwait(false) is { } version)
            {
                seance.Fermee = true;
                _journal?.LogInformation("ADVENTURE : version de fin de seance {Version} de {Aventure}", version.Id, seance.Aventure.Id);
            }
            else
            {
                _journal?.LogWarning("ADVENTURE : l'etat de fin de seance de {Aventure} n'a pas pu etre pris", seance.Aventure.Id);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _journal?.LogWarning(ex, "ADVENTURE : fin de seance de {Aventure} : etat non pris", seance.Aventure.Id);
        }
    }

    public async Task TermineeAsync(MultiplayerRoom room)
    {
        if (!_seances.TryRemove(room.Room, out var seance))
        {
            return;
        }

        seance.Suivi?.Cancel();
        if (!seance.Fermee && seance.Sauveur.DepuisLEtatDeSortie(_stockage.DossierDesEtats(seance.Aventure.Id)) is { } sortie)
        {
            _journal?.LogInformation("ADVENTURE : version de sortie {Version} de {Aventure}", sortie.Id, seance.Aventure.Id);
        }

        try
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _file.EnvoyerAsync(_site, _maintenant(), limite.Token).ConfigureAwait(false);
            await _site.FermerSeanceAsync(seance.Aventure.Id, "end", seance.Sauveur.DureeTotale(), limite.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Hors ligne : la file garde tout, le site fermera la seance de lui-meme.
        }
    }


    // ── pour les routes locales ────────────────────────────────────────────────────────────────────

    public string? DernierEtatDe(string aventure) => AdventureStockage.NomSur(aventure) ? _stockage.Derniere(aventure)?.Id : null;
}
