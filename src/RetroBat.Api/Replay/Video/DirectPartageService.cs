using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE DIRECT PARTAGE (CDC video V2.1, §5.2 ; decisions du 15 octobre 2026). Une seule borne a l'antenne a la fois,
/// choisie par le site : la borne qui joue une partie NelfePlay se porte candidate et redemande toutes les 10 secondes.
/// <list type="bullet">
/// <item>A l'antenne, elle envoie la partie que RetroArch enregistre deja, par l'exe du direct, a 3 Mbit/s, avec la
/// bande sous le jeu (joueur, jeu, systeme, mode, record a battre) ; d'office (decision du user : debit reduit).</item>
/// <item>A la fin de la partie, l'exe joue sa fin de passage (clip de transition verifie par son empreinte, carte
/// « UP NEXT » avec la borne suivante que le site a elue) et s'arrete quand le site le dit : la suivante est alors a
/// l'antenne, le flux ne coupe jamais sur les lecteurs du site.</item>
/// <item>Une alert de record sur cette borne termine son passage : elle ne peut pas alimenter deux directs.</item>
/// </list>
/// L'adresse d'entree ne se journalise jamais. Un essai (route locale <c>shared-test</c>, jusqu'au redemarrage) passe
/// en directs d'essai non repertories.
/// </summary>
public sealed class DirectPartageService : IHostedService
{
    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly LeaderboardClient _classement;
    private readonly NelfePlayAgentService _agent;
    private readonly RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider _wrapper;
    private readonly NelfePlayScoringCollectionSyncService? _collection;
    private readonly PartieNelfePlayService? _partie;
    private readonly ReplayCaptureService _capture;
    private readonly ReplayVideoPublisher _site;
    private readonly ILogger<DirectPartageService> _journal;
    private readonly bool _actif;
    private volatile bool _essai;
    private IDisposable? _abonnement;

    /// <summary>La partie en cours, et ce que son passage en sait.</summary>
    private sealed class Partie
    {
        public volatile bool Finie;
        public volatile bool Alert;
        // La partie certifiee s'est fermee (un continue, l'arrivee d'un joueur) : elle cede l'antenne a qui la demande.
        public volatile bool NonCertifiable;
        public long MeilleurScore;
        public readonly DateTime Debut = DateTime.UtcNow;
    }

    private Partie? _enCours;
    private readonly object _verrou = new();

    public DirectPartageService(IEventBus bus, ApiContext contexte, LeaderboardClient classement, NelfePlayAgentService agent,
        RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider wrapper, ReplayCaptureService capture,
        ReplayVideoPublisher site, IConfiguration configuration, ILogger<DirectPartageService> journal,
        NelfePlayScoringCollectionSyncService? collection = null, PartieNelfePlayService? partie = null)
    {
        _bus = bus;
        _contexte = contexte;
        _classement = classement;
        _agent = agent;
        _wrapper = wrapper;
        _capture = capture;
        _site = site;
        _journal = journal;
        _collection = collection;
        _partie = partie;
        _actif = configuration.GetValue("Video:Shared:Enabled", true);
        _essai = configuration.GetValue("Video:Shared:Test", false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_actif)
        {
            _journal.LogInformation("Direct partage : desactive (Video:Shared:Enabled).");
            return Task.CompletedTask;
        }
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        lock (_verrou) if (_enCours is not null) _enCours.Finie = true;
        return Task.CompletedTask;
    }

    /// <summary>L'essai, jusqu'au prochain demarrage : passages d'essai, non repertories, entre bornes en essai.</summary>
    public void Essayer(bool actif)
    {
        _essai = actif;
        _journal.LogInformation("Direct partage : essai {Etat}.", actif ? "arme" : "desarme");
    }

    /// <summary>Les fichiers que l'API ecrit pour l'exe : la commande (fin, suivante, arret) et la bande sous le jeu.</summary>
    private static string Dossier => Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "state", "nelfenet", "replay");
    internal static string CheminDeLaCommande => Path.Combine(Dossier, "partage-commande.json");
    internal static string CheminDuBandeau => Path.Combine(Dossier, "partage-bandeau.json");

    /// <summary>
    /// Un clip livre par le Data Pack (resources/live) : seulement un nom de fichier, jamais un chemin, et seulement un
    /// MP4. Null sinon. Pur.
    /// </summary>
    internal static string? CheminDuClip(string racine, string? fichier)
    {
        if (string.IsNullOrWhiteSpace(fichier)) return null;
        var nom = Path.GetFileName(fichier.Trim());
        if (nom != fichier.Trim() || !nom.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(racine, "resources", "live", nom);
    }

    /// <summary>
    /// Les options de l'exe pour un passage : debit, commande, clip de transition et son empreinte (sans empreinte, pas de
    /// clip), bande sous le jeu. Pur.
    /// </summary>
    internal static IReadOnlyList<string> OptionsDuPassage(int debitKbps, string commande, string? clip, string? empreinte, string bandeau)
    {
        var o = new List<string> { "--debit", Math.Clamp(debitKbps, 500, 9000).ToString(CultureInfo.InvariantCulture), "--commande", commande,
            "--attente-max", "90", "--bandeau", bandeau };
        if (clip is not null && !string.IsNullOrWhiteSpace(empreinte)) o.AddRange(["--clip", clip, "--clip-sha256", empreinte.Trim()]);
        return o;
    }

    /// <summary>Le record a battre, sur la bande : « RECORD 523 400 · player ». Vide sans record. Pur.</summary>
    internal static string TexteDuRecord(long? valeur, string? joueur)
        => valeur is long v && v > 0
            ? "RECORD " + v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + (string.IsNullOrWhiteSpace(joueur) ? "" : " · " + joueur.Trim())
            : "";

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    var partie = new Partie();
                    lock (_verrou)
                    {
                        if (_enCours is not null) _enCours.Finie = true;
                        _enCours = partie;
                    }
                    _ = Task.Run(() => DeroulerAsync(partie));
                    break;
                case "score.live.changed":
                    if (Nombre(e.Payload, "Score") is not { } score || (Nombre(e.Payload, "Player") ?? 1) != 1) return;
                    lock (_verrou) if (_enCours is { } p) p.MeilleurScore = Math.Max(p.MeilleurScore, score);
                    break;
                case "video.live.record.started":
                    // L'alert de cette borne prend le relais : son passage partage se termine.
                    lock (_verrou) if (_enCours is { } a) a.Alert = true;
                    break;
                case "ui.game.ended":
                    lock (_verrou) if (_enCours is { } f) f.Finie = true;
                    break;
                case "scoring.run.ended":
                    // L'ANTENNE AUX PARTIES QUI COMPTENT (demande user, 2026-10-16) : un joueur qui remet des credits
                    // n'est plus certifiable, et la regie le fait sortir des qu'une autre borne attend.
                    lock (_verrou) if (_enCours is { } c) c.NonCertifiable = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct partage : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>Une partie : candidature, passage a l'antenne s'il vient, fin de passage, arret.</summary>
    private async Task DeroulerAsync(Partie p)
    {
        try
        {
            // La partie NelfePlay se decide au lancement, en quelques secondes.
            for (var i = 0; i < 30 && _partie?.EstNelfePlay != true && !p.Finie; i++) await Task.Delay(500).ConfigureAwait(false);
            if (_partie?.EstNelfePlay != true || p.Finie) return;
            // LA PARTIE D'UN AUTRE (2026-10-16) : une borne spectatrice d'un netplay s'etait portee candidate sous son
            // propre nom, avec la partie de l'hote. Ni spectatrice ni invitee ne se presentent.
            if (!RetroBat.Api.Netplay.NetplayGuestService.PartieALaBorne)
            {
                _journal.LogInformation("Direct partage : partie de l'hote (netplay), la borne ne se porte pas candidate.");
                return;
            }
            // PAS DE CANDIDATURE SANS L'EXE DU DIRECT (2026-10-16) : elue, la borne recevait l'adresse d'envoi,
            // constatait l'absence de l'exe et se taisait ; la diffusion YouTube restait « planifiee », sans image,
            // jusqu'a l'entretien du site. L'exe arrive par outils.json.
            if (LanceurDuDirect.Chemin(AppContext.BaseDirectory) is null)
            {
                _journal.LogInformation("Direct partage : l'exe du direct manque, la borne ne se porte pas candidate.");
                return;
            }
            if (_contexte.Ui.Running is not { } jeu) return;
            var brut = Path.GetFileNameWithoutExtension(jeu.GamePath.Length > 0 ? jeu.GamePath : jeu.GameName);
            if (brut.Length == 0 || jeu.SystemId.Length == 0) return;
            var def = _wrapper.ResolveDefinitionFor(brut, jeu.SystemId);
            if (!(def.AliasMatched || def.DefinitionExists) || string.IsNullOrWhiteSpace(def.Rom)) return;
            var romGroup = def.Rom.Trim().ToLowerInvariant();
            var regle = LeaderboardClient.ReglePrincipale(_collection?.ReglesOuvertes(romGroup) ?? Array.Empty<string>());
            if (regle.Length == 0) regle = "1cc";

            // Le record a battre, pour la bande.
            var record = "";
            try
            {
                var classement = await _classement.MondeAsync(romGroup, _agent.Status.Pseudo ?? "", CancellationToken.None, regle)
                    .ConfigureAwait(false);
                if (classement.Lignes.OrderBy(l => l.Rang).FirstOrDefault() is { } tete) record = TexteDuRecord(tete.Valeur, tete.Joueur);
            }
            catch (Exception)
            {
                // Sans classement, la bande dit nelfeplay.com.
            }
            // Le systeme du classement : le dossier de la definition (arcade, megadrive, mastersystem...), jamais celui d'ES
            // (fbneo, mame). Le site le devinait depuis le 17 octobre 2026 ; le titre du direct disait « (Arcade) » pour un
            // jeu Master System quand il manquait.
            var systeme = (def.SystemId ?? "").Trim().ToLowerInvariant();
            await PasserAsync(p, romGroup, regle, record, systeme).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _journal.LogInformation("Direct partage : passage interrompu ({Type} : {Message}).", ex.GetType().Name, ex.Message);
        }
    }

    private async Task PasserAsync(Partie p, string romGroup, string regle, string record, string systeme)
    {
        Task<LanceurDuDirect.Rapport>? exe = null;
        CancellationTokenSource? secours = null;
        var finAnnoncee = false;
        var essai = _essai;
        try
        {
            while (true)
            {
                // Un role de netplay repris tard (jeu demarre hors ES) retire aussi la candidature.
                var finie = p.Finie || p.Alert || !RetroBat.Api.Netplay.NetplayGuestService.PartieALaBorne;
                var demande = new JsonObject
                {
                    ["rom_group"] = romGroup, ["ruleset"] = regle, ["system_id"] = systeme, ["score"] = Interlocked.Read(ref p.MeilleurScore),
                    ["state"] = finie ? "ended" : "playing", ["elapsed_s"] = (int)(DateTime.UtcNow - p.Debut).TotalSeconds, ["test"] = essai,
                    // Sans envoi en cours, le site redonne l'adresse d'entree d'un passage ouvert : sa premiere reponse a pu
                    // se perdre.
                    ["sending"] = exe is not null,
                    ["certifiable"] = !p.NonCertifiable,
                };
                var r = await _site.SonderLePartageAsync(demande, CancellationToken.None).ConfigureAwait(false);
                var role = (string?)r?["role"];
                var attente = TimeSpan.FromSeconds(Math.Clamp((int?)r?["poll_after"] ?? 10, 3, 60));

                if (role == "on_air" && exe is null && !finie && (string?)r!["ingest"] is { Length: > 0 } adresse)
                {
                    exe = Lancer(r, adresse, regle, record, out secours);
                    if (exe is null)
                    {
                        // ELUE SANS POUVOIR ENVOYER (2026-10-16) : la borne se taisait, et la sortante tenait son clip de fin
                        // en attendant une suivante qui ne venait pas. Le site l'apprend tout de suite (partie finie) et
                        // abandonne le passage.
                        p.Finie = true;
                        continue;
                    }
                    _journal.LogInformation("Direct partage : {Jeu} a l'antenne (passage {Passage}).", romGroup, (string?)r["passage_id"]);
                }
                else if (role == "outro")
                {
                    if (!finAnnoncee) _journal.LogInformation("Direct partage : fin du passage de {Jeu}.", romGroup);
                    finAnnoncee = true;
                    var suivante = r!["next"] as JsonObject;
                    EcrireLaCommande(fin: true, arret: false, (string?)suivante?["player"], (string?)suivante?["game"]);
                }
                else if (role == "stop" || (role == "none" && exe is not null))
                {
                    EcrireLaCommande(fin: true, arret: true, null, null);
                    _journal.LogInformation("Direct partage : {Jeu}, arret demande par le site.", romGroup);
                    return;
                }
                else if (role is "none" or "wait" && finie)
                {
                    return;
                }
                else if (r is null && finie && exe is null)
                {
                    return;
                }
                // L'exe est sorti seul (film perdu, connexion coupee) : le passage se termine aupres du site.
                if (exe is { IsCompleted: true } && !finie) p.Finie = true;
                await Task.Delay(attente).ConfigureAwait(false);
            }
        }
        finally
        {
            if (exe is not null)
            {
                // L'exe s'arrete sur l'ordre ecrit ; au-dela de deux minutes, on l'arrete.
                if (await Task.WhenAny(exe, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false) != exe) secours?.Cancel();
                try
                {
                    var rapport = await exe.ConfigureAwait(false);
                    _journal.LogInformation("Direct partage : passage fini ({Fin}, code {Code}, {Secondes:F0} s).", rapport.Fin, rapport.Code,
                        rapport.Duree.TotalSeconds);
                }
                catch (Exception ex)
                {
                    _journal.LogInformation("Direct partage : l'exe du passage a echoue ({Message}).", ex.Message);
                }
            }
            secours?.Dispose();
        }
    }

    /// <summary>Lance l'exe du passage sur le film en cours ; null s'il n'y a ni film ni exe.</summary>
    private Task<LanceurDuDirect.Rapport>? Lancer(JsonObject r, string adresse, string regle, string record, out CancellationTokenSource? secours)
    {
        secours = null;
        var exe = LanceurDuDirect.Chemin(AppContext.BaseDirectory);
        if (_capture.FilmEnCours() is not { } film || exe is null)
        {
            _journal.LogInformation("Direct partage : a l'antenne, mais {Manque} : pas de passage.", exe is null ? "l'exe du direct manque" : "aucun film ne tourne");
            EcrireLaCommande(fin: true, arret: true, null, null);
            return null;
        }
        var banniere = r["banner"] as JsonObject;
        EcrireLeBandeau((string?)banniere?["player"] ?? _agent.Status.Pseudo, (string?)banniere?["game"], (string?)banniere?["system"],
            regle.ToUpperInvariant(), record);
        EcrireLaCommande(fin: false, arret: false, null, null);
        var transition = (r["clips"] as JsonObject)?["transition"] as JsonObject;
        var clip = CheminDuClip(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, (string?)transition?["file"]);
        if (clip is not null && !File.Exists(clip)) clip = null;
        var options = OptionsDuPassage((int?)r["bitrate_kbps"] ?? 3000, CheminDeLaCommande, clip, (string?)transition?["sha256"], CheminDuBandeau);
        secours = new CancellationTokenSource();
        var jeton = secours.Token;
        return Task.Run(() => LanceurDuDirect.DiffuserAsync(exe, film.Fichier, adresse, film.Rotation ?? 0, _journal, jeton,
            options: options, delaiDeFin: TimeSpan.FromSeconds(10)));
    }

    private void EcrireLaCommande(bool fin, bool arret, string? joueur, string? jeu)
    {
        var commande = new JsonObject
        {
            ["fin"] = fin, ["arret"] = arret,
            ["suivant"] = joueur is null && jeu is null ? null : new JsonObject { ["joueur"] = joueur, ["jeu"] = jeu },
        };
        Ecrire(CheminDeLaCommande, commande.ToJsonString());
    }

    private void EcrireLeBandeau(string? joueur, string? jeu, string? systeme, string mode, string record)
        => Ecrire(CheminDuBandeau, new JsonObject
        {
            ["joueur"] = joueur, ["jeu"] = jeu, ["systeme"] = systeme, ["mode"] = mode, ["record"] = record.Length > 0 ? record : null,
        }.ToJsonString());

    /// <summary>Ecriture atomique : l'exe ne lit jamais un fichier a moitie ecrit.</summary>
    private void Ecrire(string chemin, string texte)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin + ".tmp", texte);
            File.Move(chemin + ".tmp", chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Direct partage : {Fichier} non ecrit.", Path.GetFileName(chemin));
        }
    }

    private static long? Nombre(object? payload, string nom)
    {
        try
        {
            var el = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
            return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(nom, out var v) && v.TryGetInt64(out var n) ? n : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
