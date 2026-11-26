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
    private readonly ILogger<AdventureSeanceService>? _journal;
    private readonly ConcurrentDictionary<string, Seance> _seances = new(StringComparer.Ordinal);

    internal sealed class Seance
    {
        public required AdventureInfo Aventure { get; init; }
        public required AdventureSauveur Sauveur { get; init; }
        public required DecisionDeReprise Decision { get; init; }
        public MultiplayerStart? Depart { get; init; }
        public required string DossierDesSauvegardes { get; init; }
        public required string NomDeLaRom { get; init; }
        public DateTime Ouverte { get; init; }
        public CancellationTokenSource? Suivi { get; set; }
        public bool Fermee { get; set; }
    }

    public AdventureSeanceService(IAdventureSite site, AdventureStockage stockage, AdventureFileDEnvoi file,
        Func<ICommandesRetroArch>? commandes = null, Func<string?, string?>? empreinteDuCoeur = null, Func<DateTime>? maintenant = null,
        ILogger<AdventureSeanceService>? journal = null, AdventureCatalogueStore? catalogue = null)
    {
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

                depart = DepartDuCatalogue(jeu, joueurs);
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

                depart = DepartDuCatalogue(jeu, joueurs);
                break;
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
    private MultiplayerStart? DepartDuCatalogue(MultiplayerInstalledGame jeu, int joueurs)
    {
        if (jeu.DepartPour(joueurs) is not { } depart || string.IsNullOrWhiteSpace(depart.State) || Path.IsPathRooted(depart.State))
        {
            return null;
        }

        var dossier = _catalogue is null ? Path.Combine(RetroBatPaths.PluginRoot, "resources", "adventure") : Path.GetDirectoryName(_catalogue.Path)!;
        var chemin = Path.GetFullPath(Path.Combine(dossier, depart.State.Replace('/', Path.DirectorySeparatorChar)));
        return chemin.StartsWith(Path.GetFullPath(dossier), StringComparison.OrdinalIgnoreCase) ? depart with { State = chemin } : null;
    }

    /// <summary>
    /// Le depart pret dont le SAVEDATA de la console de la cartouche est celui de l'aventure : un fichier derive, range dans le dossier
    /// d'Adventure (<c>departs\</c>), son empreinte a lui. Seulement pour le .srm de mGBA Splitscreen (<c>gba_srm_slots</c>) ; null sinon.
    /// </summary>
    private MultiplayerStart? DepartAvecLaSauvegarde(MultiplayerStart depart, byte[] sauvegarde, string aventure, AdventureSpec spec)
    {
        if (!string.Equals(spec.Cartridge?.Kind, "gba_srm_slots", StringComparison.OrdinalIgnoreCase))
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
                ["savefile_directory"] = seance.DossierDesSauvegardes,
                // Les ETATS aussi : SAVE_STATE et l'autosave ecrivent dans le dossier de l'aventure, l'emplacement courant est le 0.
                ["savestate_directory"] = EnsureDir(_stockage.DossierDesEtats(seance.Aventure.Id)),
                ["state_slot"] = "0",
                ["savestate_auto_index"] = "false",
                ["sort_savestates_enable"] = "false",
                ["sort_savestates_by_content_enable"] = "false",
                ["sort_savefiles_enable"] = "false",
                ["sort_savefiles_by_content_enable"] = "false",
                ["autosave_interval"] = "10",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ReglagesDeLInvite(MultiplayerRoom room)
        => room.AdventureId is { } id && AdventureStockage.NomSur(id)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["savefile_directory"] = EnsureDir(_stockage.DossierDesSauvegardes(id)),
                ["sort_savefiles_enable"] = "false",
                ["sort_savefiles_by_content_enable"] = "false",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal);

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

    public void Demarree(MultiplayerRoom room, MultiplayerInstalledGame jeu)
    {
        if (De(room.Room) is not { } seance)
        {
            return;
        }

        seance.Suivi?.Cancel();
        seance.Suivi = new CancellationTokenSource();
        var jeton = seance.Suivi.Token;
        _ = Task.Run(() => SuivreAsync(seance, jeton), jeton);
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
