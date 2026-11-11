using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES NOTIFICATIONS DE RETROARCH COUPEES AUSSI POUR UNE PARTIE NELFEPLAY LANCEE PAR ES (option A, autorisee par le user le
/// 11/11 : EXCEPTION a la regle « jamais hors global.apiexpose.* », limitee a ce reglage et aux seules parties NelfePlay).
///
/// L'API d'ES ne prend qu'un chemin de ROM : un lancement par ES (carrousel du World Scoring, defi du panneau sans Deck, site,
/// concours, netplay par ES) ne peut pas porter « -OnScreenMsg 0 ». Le lanceur lit aussi la fonction par jeu d'es_settings.cfg
/// (<c>systeme["rom"].OnScreenMsg</c>, Program.cs : LoadAll(systeme + "[\"rom\"]")). Donc, au crochet game-start (qu'ES
/// attend avant de lancer, comme les reglages certifies depuis la 1.9.74 : ES efface les cles par jeu qu'il n'a pas chargees
/// quand il resauvegarde ses reglages, une cle posee plus tot pourrait avoir disparu) :
/// <list type="number">
/// <item>la partie est-elle NelfePlay (<see cref="PartieNelfePlayService.Prevoir"/>, le meme verdict que ui.game.started) ? Non :
/// rien n'est ecrit ;</item>
/// <item>oui : la valeur d'avant de CETTE cle est notee sur disque (journal), puis la cle est posee a 0 ;</item>
/// <item>des que l'emulateur parait (le lanceur l'a lue), la valeur d'avant est rendue (la cle retiree si elle n'existait pas),
/// comme le netplay le fait pour global.netplay* (<see cref="RetroBat.Api.Netplay.NetplayLaunch"/>) ; au bout d'une minute
/// sans emulateur, rendue quand meme ;</item>
/// <item>une API arretee entre-temps : le journal est rendu a son demarrage, avant qu'ES s'ouvre (il attend l'API).</item>
/// </list>
/// Aucune autre cle n'est touchee. Pas de rechargement demande a ES : l'ecriture se fait au lancement (ES lance, il ne navigue
/// pas) et le rendu pendant la partie (ES attend le lanceur) ; rien ne peut declencher un reloadgames en pleine navigation.
/// </summary>
public sealed class NotificationsDesLancementsEs : IHostedService, IDisposable
{
    /// <summary>La fonction d'ES pour libretro que le lanceur ecrit dans video_font_enable.</summary>
    public const string Fonction = "OnScreenMsg";

    private static readonly TimeSpan PatienceApparition = TimeSpan.FromSeconds(60);

    private readonly IEsSettingsStore _es;
    private readonly IEventBus _bus;
    private readonly PartieNelfePlayService? _partie;
    private readonly ILogger<NotificationsDesLancementsEs>? _journal;
    private readonly string _cheminDuJournal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private long _generationCoupee = -1;
    // Chaque pose a son jeton : le rendu d'un lancement precedent (emulateur jamais apparu) ne rend pas la cle du suivant.
    private long _pose;

    public NotificationsDesLancementsEs(IEsSettingsStore es, IEventBus bus, PartieNelfePlayService? partie = null,
        ILogger<NotificationsDesLancementsEs>? journal = null)
        : this(es, bus, partie, journal, Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "notifications-es.json"))
    {
    }

    internal NotificationsDesLancementsEs(IEsSettingsStore es, IEventBus bus, PartieNelfePlayService? partie,
        ILogger<NotificationsDesLancementsEs>? journal, string cheminDuJournal)
    {
        _es = es;
        _bus = bus;
        _partie = partie;
        _journal = journal;
        _cheminDuJournal = cheminDuJournal;
    }

    /// <summary>Couture des essais : l'emulateur est-il la ?</summary>
    internal Func<DateTime, bool> EmulateurLa { get; set; } = depart => EmulatorForeground.EmulateurDeJeuTourne(depart);

    /// <summary>Couture des essais : la marge apres l'apparition de l'emulateur.</summary>
    internal TimeSpan Marge { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Couture des essais : le verdict NelfePlay du jeu qui part (par defaut celui de PartieNelfePlayService).</summary>
    internal Func<string?, string?>? Verdict { get; set; }

    /// <summary>Ce qui est note sur disque : la cle posee et sa valeur d'avant (null : elle n'existait pas).</summary>
    internal sealed record Note(string Cle, string? Avant);

    /// <summary>La cle par jeu du lanceur : <c>fbneo["19xx.zip"].OnScreenMsg</c> (meme forme que les reglages certifies). Pur.</summary>
    internal static string CleDuJeu(string systeme, string fichier)
        => systeme + "[\"" + fichier.Replace("=", "").Replace("#", "") + "\"]." + Fonction;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Avant l'ouverture d'ES (il attend l'API) : une cle laissee par une API arretee en pleine partie est rendue.
        Reparer();
        _abonnement = _bus.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type is "ui.game.ended" or "ui.game.ended.raw") Terminer();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    /// <summary>
    /// AU CROCHET GAME-START (<c>rom-packs/on-the-fly/ensure-launch-rom</c>) : pour une partie NelfePlay, la cle par jeu a 0, la
    /// valeur d'avant notee, puis rendue quand l'emulateur parait. Rend vrai si la cle a ete posee. Jamais d'exception.
    /// </summary>
    public bool PoserAvantLeLancement(string argumentsDuScript)
    {
        try
        {
            var (systeme, chemin) = RetroBat.Api.Replay.Video.EchelleDesFilms.LancementDuScript(argumentsDuScript);
            if (string.IsNullOrWhiteSpace(systeme) || string.IsNullOrWhiteSpace(chemin)
                || !RetroBat.Api.Controllers.CommandsController.NomDeLanceur(systeme))
            {
                NotificationsRetroArch.Parler();
                return false;
            }

            var origine = (Verdict ?? (c => _partie?.Prevoir(c)))(chemin);
            if (origine is null)
            {
                // Une partie du joueur : rien n'est ecrit, RetroArch garde ses notifications.
                NotificationsRetroArch.Parler();
                return false;
            }

            // Une cle d'un lancement precedent encore la (emulateur jamais apparu) : rendue d'abord.
            Reparer();
            var cle = CleDuJeu(systeme, Path.GetFileName(chemin));
            string? avant = null;
            var lue = false;
            _es.Update(doc =>
            {
                avant = Valeur(doc, cle);
                lue = true;
                return false;
            });
            if (!lue) return false;
            if (avant is "0" or "false")
            {
                // Le joueur coupe deja les notifications de ce jeu : rien a poser ni a rendre.
                MarquerCoupe();
                return false;
            }

            // Le journal AVANT la cle : une API qui tombe entre les deux rend une cle qui n'a pas change, sans dommage.
            long jeton;
            lock (_verrou) jeton = ++_pose;
            EcrireLeJournal(new Note(cle, avant));
            var posee = _es.Update(doc => Poser(doc, cle, "0"));
            _journal?.LogInformation(
                "Notifications de RetroArch : {Cle} = 0 pour ce lancement par ES (partie NelfePlay, {Origine}), valeur d'avant {Avant}.",
                cle, origine, avant ?? "absente");
            MarquerCoupe();
            var depart = DateTime.Now.AddSeconds(-2);
            _ = Task.Run(() => RendreQuandLEmulateurParaitAsync(depart, jeton));
            return posee;
        }
        catch (Exception ex)
        {
            _journal?.LogWarning(ex, "Notifications de RetroArch : cle par jeu non posee, la partie garde ses notifications.");
            return false;
        }
    }

    private void MarquerCoupe()
    {
        var g = NotificationsRetroArch.Couper();
        lock (_verrou) _generationCoupee = g;
    }

    /// <summary>Fin de la partie : les messages reviennent a RetroArch, et la valeur d'ES a retroarch.cfg une fois RetroArch ferme.</summary>
    private void Terminer()
    {
        long g;
        lock (_verrou)
        {
            g = _generationCoupee;
            _generationCoupee = -1;
        }

        if (g >= 0) _ = NotificationsRetroArch.FinirAsync(g, _journal);
    }

    internal async Task RendreQuandLEmulateurParaitAsync(DateTime depart, long jeton)
    {
        try
        {
            var limite = DateTime.UtcNow + PatienceApparition;
            while (DateTime.UtcNow < limite && !EmulateurLa(depart))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }

            // Le lanceur relit es_settings tout au debut : quand l'emulateur parait, c'est deja fait ; deux secondes de marge.
            await Task.Delay(Marge).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // On rend quoi qu'il arrive.
        }

        lock (_verrou)
        {
            if (jeton != _pose) return; // un autre lancement a pose sa cle : son propre rendu s'en charge
        }

        Reparer();
    }

    /// <summary>Rend la cle notee au journal (valeur d'avant, ou retiree), puis efface le journal. Rend vrai si une cle a ete rendue.</summary>
    internal bool Reparer()
    {
        Note? note;
        lock (_verrou)
        {
            note = LireLeJournal();
            if (note is null) return false;
            try
            {
                _es.Update(doc => Rendre(doc, note.Cle, note.Avant));
                File.Delete(_cheminDuJournal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _journal?.LogWarning(ex, "Notifications de RetroArch : {Cle} non rendue, nouvel essai au prochain lancement.", note.Cle);
                return false;
            }
        }

        _journal?.LogInformation("Notifications de RetroArch : {Cle} rendue ({Avant}).", note.Cle, note.Avant ?? "retiree");
        return true;
    }

    // ─────────────────────────────── es_settings.cfg (pur) ───────────────────────────────

    private static XElement? Element(XDocument doc, string cle)
        => doc.Root?.Elements().FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), cle, StringComparison.OrdinalIgnoreCase));

    internal static string? Valeur(XDocument doc, string cle) => (string?)Element(doc, cle)?.Attribute("value");

    /// <summary>Pose <paramref name="cle"/> = <paramref name="valeur"/> (ajoutee si absente). Rend vrai si le document change. Pur.</summary>
    internal static bool Poser(XDocument doc, string cle, string valeur)
    {
        var root = doc.Root ?? throw new InvalidOperationException("es_settings.cfg sans racine.");
        if (Element(doc, cle) is { } existant)
        {
            if ((string?)existant.Attribute("value") == valeur) return false;
            existant.SetAttributeValue("value", valeur);
            return true;
        }

        root.Add(new XText(Environment.NewLine + "  "));
        root.Add(new XElement("string", new XAttribute("name", cle), new XAttribute("value", valeur)));
        return true;
    }

    /// <summary>
    /// Rend <paramref name="cle"/> : sa valeur d'avant, ou rien (retiree) si elle n'existait pas. Seule cette cle est touchee. Pur.
    /// </summary>
    internal static bool Rendre(XDocument doc, string cle, string? avant)
    {
        var existant = Element(doc, cle);
        if (avant is null)
        {
            if (existant is null) return false;
            if (existant.PreviousNode is XText blanc && string.IsNullOrWhiteSpace(blanc.Value)) blanc.Remove();
            existant.Remove();
            return true;
        }

        if (existant is null)
        {
            doc.Root?.Add(new XText(Environment.NewLine + "  "));
            doc.Root?.Add(new XElement("string", new XAttribute("name", cle), new XAttribute("value", avant)));
            return doc.Root is not null;
        }

        if ((string?)existant.Attribute("value") == avant) return false;
        existant.SetAttributeValue("value", avant);
        return true;
    }

    // ─────────────────────────────── journal ───────────────────────────────

    private void EcrireLeJournal(Note note)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cheminDuJournal)!);
        var provisoire = _cheminDuJournal + ".tmp";
        File.WriteAllText(provisoire, JsonSerializer.Serialize(note), new UTF8Encoding(false));
        File.Move(provisoire, _cheminDuJournal, overwrite: true);
    }

    private Note? LireLeJournal()
    {
        try
        {
            if (!File.Exists(_cheminDuJournal)) return null;
            var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(_cheminDuJournal));
            // Un journal qui ne nomme pas une cle de cette fonction ne touche rien
            return note is { Cle.Length: > 0 } && note.Cle.EndsWith("\"]." + Fonction, StringComparison.Ordinal) ? note : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
