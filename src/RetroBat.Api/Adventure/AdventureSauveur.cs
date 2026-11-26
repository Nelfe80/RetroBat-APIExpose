using System.Globalization;
using System.Security.Cryptography;
using RetroBat.Api.Multiplayer;

namespace RetroBat.Api.Adventure;

/// <summary>
/// CE QU'UNE SEANCE SAIT POUR SAUVER (CDC v1.0 §2, point 3) : l'aventure, la ROM, le dossier de sauvegardes propre a l'aventure
/// et ce que la version devra dire. Rien ici ne touche aux sauvegardes personnelles du joueur.
/// </summary>
public sealed record AdventureSauvegarde(
    string Aventure,
    string NomDeLaRom,
    string FichierDeLaCartouche,
    int Joueurs,
    string? EmpreinteDuCoeur,
    long DureeDejaJouee,
    Func<IReadOnlyList<AdventurePlace>> Places,
    int? BaseSeq = null,
    string? SessionId = null);

/// <summary>
/// SAUVER, chez l'hote seul : l'etat lie (commande reseau de RetroArch dans l'emplacement reserve a l'aventure), la sauvegarde du
/// jeu de la cartouche de l'aventure (le fichier du dossier de sauvegardes de l'aventure), une capture si RetroArch en rend une.
/// Garde la version sur le disque, l'inscrit dans la file d'envoi, rien de plus : l'envoi est celui de <see cref="AdventureFileDEnvoi"/>.
///
/// L'emplacement 96 est reserve a Adventure (le 97 l'est au depart pret de MULTIPLAYER) : jamais un etat du joueur.
/// </summary>
public sealed class AdventureSauveur
{
    public const int Emplacement = 96;

    private readonly ICommandesRetroArch _commandes;
    private readonly AdventureStockage _stockage;
    private readonly AdventureFileDEnvoi _file;
    private readonly AdventureSauvegarde _seance;
    private readonly Func<DateTime> _maintenant;
    private readonly DateTime _debut;
    private readonly TimeSpan _attenteDeLEtat;

    public AdventureSauveur(ICommandesRetroArch commandes, AdventureStockage stockage, AdventureFileDEnvoi file, AdventureSauvegarde seance,
        Func<DateTime>? maintenant = null, TimeSpan? attenteDeLEtat = null)
    {
        _commandes = commandes;
        _stockage = stockage;
        _file = file;
        _seance = seance;
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
        _debut = _maintenant();
        _attenteDeLEtat = attenteDeLEtat ?? TimeSpan.FromSeconds(4);
        DerniereSauvegarde = _debut;
    }

    /// <summary>La derniere version prise (le delai des 5 minutes court depuis elle).</summary>
    public DateTime DerniereSauvegarde { get; private set; }

    /// <summary>L'empreinte du fichier de la cartouche a la derniere lecture : sa variation dit que le jeu a sauve.</summary>
    public string? EmpreinteVueDeLaCartouche { get; private set; }

    /// <summary>Le fichier de sauvegarde de la cartouche de l'aventure.</summary>
    public string CheminDeLaCartouche => Path.Combine(_stockage.DossierDesSauvegardes(_seance.Aventure), _seance.FichierDeLaCartouche);

    /// <summary>La duree jouee de l'aventure jusqu'ici : ce qu'elle avait deja et cette seance.</summary>
    public long DureeTotale() => _seance.DureeDejaJouee + (long)Math.Max(0, (_maintenant() - _debut).TotalSeconds);

    /// <summary>Lit l'empreinte de la cartouche ; null si le jeu n'a pas encore ecrit de sauvegarde.</summary>
    public string? EmpreinteDeLaCartouche()
    {
        var octets = LireLaCartouche();
        return octets is null ? null : AdventureStockage.Empreinte(octets);
    }

    private byte[]? LireLaCartouche()
    {
        try
        {
            var chemin = CheminDeLaCartouche;
            return File.Exists(chemin) ? ReadShared(chemin) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] ReadShared(string chemin)
    {
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memoire = new MemoryStream();
        flux.CopyTo(memoire);
        return memoire.ToArray();
    }

    /// <summary>Memorise l'etat de depart de la cartouche : la sauvegarde posee ou presente n'est pas « le jeu a sauve ».</summary>
    public void NoterLeDepart() => EmpreinteVueDeLaCartouche = EmpreinteDeLaCartouche();

    /// <summary>
    /// Le jeu a-t-il ecrit sa sauvegarde depuis la derniere vue ? Rend vrai une fois par ecriture (la nouvelle empreinte devient la
    /// vue). Un fichier qui apparait apres un depart sans sauvegarde compte comme une ecriture.
    /// </summary>
    public bool LeJeuASauve()
    {
        var lue = EmpreinteDeLaCartouche();
        if (lue is null)
        {
            return false;
        }

        var vue = EmpreinteVueDeLaCartouche;
        EmpreinteVueDeLaCartouche = lue;
        return vue is null ? true : AdventureCadence.LeJeuASauve(vue, lue);
    }

    /// <summary>
    /// Prend une version maintenant : demande l'etat a RetroArch (qui tourne), relit la sauvegarde de la cartouche, garde, met en file.
    /// Null quand RetroArch ne repond pas ou n'ecrit pas l'etat dans le delai : on ne garde jamais une version sans etat valide.
    /// </summary>
    public async Task<AdventureVersion?> SauverAsync(string raison, CancellationToken ct)
    {
        var dossier = await DossierDesEtatsAsync(ct).ConfigureAwait(false);
        if (dossier is null)
        {
            return null;
        }

        var fichier = Path.Combine(dossier, _seance.NomDeLaRom + ".state" + Emplacement.ToString(CultureInfo.InvariantCulture));
        var avant = File.Exists(fichier) ? File.GetLastWriteTimeUtc(fichier) : DateTime.MinValue;
        var debutDeLaDemande = DateTime.UtcNow.AddSeconds(-1);
        await _commandes.EnvoyerAsync("SAVE_STATE_SLOT " + Emplacement.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        var etat = await AttendreUnFichierAsync(fichier, avant > debutDeLaDemande ? avant : debutDeLaDemande, ct).ConfigureAwait(false);
        if (etat is null)
        {
            return null;
        }

        var capture = await PrendreUneCaptureAsync(ct).ConfigureAwait(false);
        return Garder(raison, etat, LireLaCartouche(), capture);
    }

    /// <summary>
    /// La fin de seance quand RetroArch s'est ferme de lui-meme : il ecrit alors l'etat de sortie (<c>savestate_auto_save</c>,
    /// <c>&lt;rom&gt;.state.auto</c>) que la borne relit. Null s'il n'a rien ecrit depuis le debut de la seance.
    /// </summary>
    public AdventureVersion? DepuisLEtatDeSortie(string dossierDesEtats, string raison = AdventureRaisons.Fin)
    {
        try
        {
            var fichier = Path.Combine(dossierDesEtats, _seance.NomDeLaRom + ".state.auto");
            if (!File.Exists(fichier) || File.GetLastWriteTimeUtc(fichier) < _debut)
            {
                return null;
            }

            var etat = ReadShared(fichier);
            return etat.Length == 0 ? null : Garder(raison, etat, LireLaCartouche(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private AdventureVersion Garder(string raison, byte[] etat, byte[]? cartouche, byte[]? capture)
    {
        var maintenant = _maintenant();
        var version = new AdventureVersion
        {
            Id = AdventureStockage.NouvelIdentifiant(),
            BaseSeq = _seance.BaseSeq,
            SessionId = _seance.SessionId,
            Adventure = _seance.Aventure,
            StateSha256 = _stockage.PoserObjet(etat),
            SaveSha256 = cartouche is { Length: > 0 } ? _stockage.PoserObjet(cartouche) : null,
            CaptureSha256 = capture is { Length: > 0 } ? _stockage.PoserObjet(capture) : null,
            Players = _seance.Joueurs,
            Seats = _seance.Places(),
            DurationS = _seance.DureeDejaJouee + (long)Math.Max(0, (maintenant - _debut).TotalSeconds),
            Reason = raison,
            CoreSha256 = _seance.EmpreinteDuCoeur,
            CreatedUtc = maintenant,
        };
        _stockage.GarderVersion(version);
        _file.Ajouter(version);
        DerniereSauvegarde = maintenant;
        EmpreinteVueDeLaCartouche = cartouche is { Length: > 0 } ? AdventureStockage.Empreinte(cartouche) : EmpreinteVueDeLaCartouche;
        return version;
    }

    private async Task<byte[]?> AttendreUnFichierAsync(string fichier, DateTime apres, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + _attenteDeLEtat;
        while (DateTime.UtcNow < limite)
        {
            try
            {
                if (File.Exists(fichier) && File.GetLastWriteTimeUtc(fichier) >= apres)
                {
                    // RetroArch finit d'ecrire : une seconde lecture de meme taille la confirme.
                    var premiere = ReadShared(fichier);
                    await Task.Delay(150, ct).ConfigureAwait(false);
                    var seconde = ReadShared(fichier);
                    if (premiere.Length > 0 && premiere.Length == seconde.Length)
                    {
                        return seconde;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Encore ouvert en ecriture : on reessaie.
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<byte[]?> PrendreUneCaptureAsync(CancellationToken ct)
    {
        try
        {
            var reponse = await _commandes.DemanderAsync("GET_CONFIG_PARAM screenshot_directory", ct).ConfigureAwait(false);
            const string prefixe = "GET_CONFIG_PARAM screenshot_directory ";
            if (reponse is null || !reponse.StartsWith(prefixe, StringComparison.Ordinal) || reponse.Length <= prefixe.Length)
            {
                return null;
            }

            var dossier = reponse[prefixe.Length..].Trim();
            var avant = DateTime.UtcNow.AddSeconds(-1);
            await _commandes.EnvoyerAsync("SCREENSHOT", ct).ConfigureAwait(false);
            for (var i = 0; i < 10 && Directory.Exists(dossier); i++)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
                var recent = new DirectoryInfo(dossier).EnumerateFiles("*.png").Where(f => f.LastWriteTimeUtc >= avant)
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (recent is not null)
                {
                    // La capture est un objet de la version, pas un fichier du joueur : la copie gardee, l'original rendu.
                    var octets = ReadShared(recent.FullName);
                    try { recent.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    return octets;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Une capture qui manque n'empeche pas la version.
        }

        return null;
    }

    private async Task<string?> DossierDesEtatsAsync(CancellationToken ct)
    {
        var statut = await _commandes.DemanderAsync("GET_STATUS", ct).ConfigureAwait(false);
        if (statut is null || !(statut.Contains("PLAYING", StringComparison.Ordinal) || statut.Contains("PAUSED", StringComparison.Ordinal)))
        {
            return null;
        }

        var reponse = await _commandes.DemanderAsync("GET_CONFIG_PARAM savestate_directory", ct).ConfigureAwait(false);
        const string prefixe = "GET_CONFIG_PARAM savestate_directory ";
        return reponse is not null && reponse.StartsWith(prefixe, StringComparison.Ordinal) && reponse.Length > prefixe.Length
            ? reponse[prefixe.Length..].Trim()
            : null;
    }

    /// <summary>La partie tourne (pas en pause) : la sauvegarde reguliere ne se prend jamais en pause ni pendant un chargement.</summary>
    public async Task<bool> LeJeuTourneAsync(CancellationToken ct)
    {
        var statut = await _commandes.DemanderAsync("GET_STATUS", ct).ConfigureAwait(false);
        return statut is not null && statut.Contains("PLAYING", StringComparison.Ordinal);
    }
}
