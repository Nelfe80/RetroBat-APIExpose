using System.Diagnostics;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES NOTIFICATIONS DE RETROARCH COUPEES POUR TOUTE PARTIE QUE NELFEPLAY LANCE LUI-MEME (demande du user du 11/11 : « pour
/// les speedruns, le multiplayer, les challenges, desactive les notifications retroarch comme pour le playerdeck », puis
/// « pour le worldscoring aussi »).
///
/// LE MECANISME DU PLAYERDECK (08/11), ETENDU : l'argument du lanceur <c>-OnScreenMsg 0</c>. C'est la fonction « OnScreenMsg »
/// d'ES pour libretro, que le lanceur lit en dernier (ses arguments passent apres es_settings.cfg) et ecrit dans
/// <c>video_font_enable</c> (LibRetroGenerator, BindBoolFeatureOn). RetroArch 1.22.2 n'active ses widgets que si
/// video_font_enable est vrai et ne tire plus sa file de messages sans lui : ni « etat sauvegarde », ni avance rapide, ni
/// netplay, ni succes, ni SHOW_MSG. Rien n'est ecrit dans es_settings.cfg ; ce n'est pas une option du coeur : ni
/// <c>core_options_digest</c>, ni les reglages certifies, ni le prevol ne le voient.
///
/// QUI LE PORTE : les lancements que l'API fait elle-meme (emulatorLauncher sans ES) pour une partie NelfePlay : epreuves de
/// speedrun et challenges (fantomes compris), MASS RUN, MULTIPLAYER (hote et invites), defi du World Scoring parti par l'API
/// (avec le Deck), netplay de l'hote et de l'invite quand ES ne repond pas. Une partie NelfePlay lancee par ES (carrousel du
/// World Scoring, defi sans Deck, site, concours) : l'API d'ES ne prend qu'un chemin de ROM, la fonction se pose donc par jeu
/// dans es_settings.cfg au crochet game-start et se rend des que l'emulateur parait (<see cref="NotificationsDesLancementsEs"/>,
/// exception autorisee par le user le 11/11). Une partie hors NelfePlay n'est pas touchee.
///
/// LES MESSAGES DE NELFEPLAY RESTENT VISIBLES : un message que l'API envoyait par SHOW_MSG (speedrun, MULTIPLAYER, filtre
/// d'image, choix du Deck) passe au Deck s'il est la (deck.notice), sinon au bandeau du haut de l'API
/// (<see cref="LiveContestOverlayService.ShowTop"/>, celui du scoring), sinon a RetroArch comme avant (<see cref="AuBandeau"/>).
///
/// LA VALEUR DU JOUEUR EST RENDUE : le lanceur a ecrit video_font_enable = false dans retroarch.cfg ; RetroArch ferme (et le
/// lanceur sorti), la valeur d'ES y revient, pour un RetroArch ouvert ensuite sans le lanceur.
///
/// SAUF RETROACHIEVEMENTS EN WORLD SCORING (14/11) : un joueur qui a RetroAchievements garde ses succes (fenetre de
/// deblocage, indicateurs de challenge, progression) ; tout le reste est coupe par <see cref="AutresNotifications"/> au lieu
/// de -OnScreenMsg 0 (<see cref="AjouterSaufSucces"/>). Les messages de NelfePlay vont toujours au Deck ou au bandeau.
/// </summary>
public static partial class NotificationsRetroArch
{
    /// <summary>L'argument du lanceur, le meme que celui du PlayerDeck (<see cref="RetroBat.Api.Deck.DeckDecor.ArgumentsSansNotifications"/>).</summary>
    public static IReadOnlyList<string> Arguments => RetroBat.Api.Deck.DeckDecor.ArgumentsSansNotifications;

    /// <summary>La cle de RetroArch que le lanceur en tire.</summary>
    public const string CleRetroArch = "video_font_enable";

    private static long _generation;
    private static volatile bool _coupees;

    /// <summary>Le bandeau du haut de l'API, pose par Program.cs (null dans les essais : rien ne s'affiche).</summary>
    public static LiveContestOverlayService? Bandeau { get; set; }

    /// <summary>Couture des essais : ce que <see cref="AuBandeau"/> montre a la place du bandeau.</summary>
    internal static Action<string, string, int, bool>? BandeauDEssai { get; set; }

    /// <summary>Les notifications de RetroArch sont-elles coupees pour le lancement direct en cours ?</summary>
    public static bool Coupees => _coupees;

    /// <summary>
    /// <paramref name="supplements"/> plus l'argument qui coupe les notifications, s'il n'y est pas deja (le Deck le porte
    /// quand il sait montrer les messages). Pur.
    /// </summary>
    public static IReadOnlyList<string> Ajouter(IEnumerable<string>? supplements)
    {
        var liste = (supplements ?? []).ToList();
        if (!liste.Any(a => a.Equals(Arguments[0], StringComparison.OrdinalIgnoreCase))) liste.AddRange(Arguments);
        return liste;
    }

    /// <summary>La ligne du lanceur coupe-t-elle les notifications (« -OnScreenMsg 0 ») ? Pur.</summary>
    public static bool LigneCoupe(string? arguments)
        => !string.IsNullOrEmpty(arguments) && Coupe().IsMatch(arguments);

    [GeneratedRegex(@"(^|\s)-OnScreenMsg\s+(0|false|off)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Coupe();

    // ─────────────────────────── RetroAchievements visibles en World Scoring (14/11) ───────────────────────────

    /// <summary>
    /// RETROACHIEVEMENTS VISIBLES EN WORLD SCORING (demande du user du 14/11, retour de Snapouille sur Shinobi : « petit son
    /// de declenchement de l'achievement mais aucune icone ni pour le deblocage ni indicateur de challenge »). Couper
    /// video_font_enable eteint les widgets de RetroArch 1.22.2, donc aussi la fenetre d'un succes, les indicateurs de
    /// challenge du mode hardcore et la progression (retroarch.c : les widgets ne s'ouvrent que si video_font_enable est vrai).
    /// Pour une partie du World Scoring d'un joueur qui a RetroAchievements, video_font_enable garde la valeur du joueur et ce
    /// sont les reglages notification_show_* de RetroArch qui coupent le reste, un a un (configuration.c, 1.22.2).
    /// notification_show_when_menu_is_alive n'en est pas : vrai, il cache TOUT en jeu, succes compris (video_driver.c).
    /// La valeur 2 de notification_show_screenshot_flash est « OFF » (gfx_widgets.h).
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> AutresNotifications =
    [
        new("notification_show_autoconfig", "false"),
        new("notification_show_autoconfig_fails", "false"),
        new("notification_show_cheats_applied", "false"),
        new("notification_show_patch_applied", "false"),
        new("notification_show_remap_load", "false"),
        new("notification_show_config_override_load", "false"),
        new("notification_show_set_initial_disk", "false"),
        new("notification_show_disk_control", "false"),
        new("notification_show_save_state", "false"),
        new("notification_show_fast_forward", "false"),
        new("notification_show_screenshot", "false"),
        new("notification_show_screenshot_flash", "2"),
        new("notification_show_refresh_rate", "false"),
        new("notification_show_netplay_extra", "false"),
    ];

    /// <summary>
    /// Les arguments du lanceur qui coupent tout sauf RetroAchievements : « -retroarch.cle valeur », que le lanceur ecrit tel
    /// quel dans retroarch.cfg APRES ses propres valeurs (LibRetroGenerator, « Custom overrides » : toute cle « retroarch.* »
    /// de sa configuration, ligne de commande comprise). Aucune cle d'ES n'est ecrite.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsSaufSucces { get; } =
        AutresNotifications.SelectMany(r => new[] { "-retroarch." + r.Key, r.Value }).ToList();

    /// <summary>
    /// <paramref name="supplements"/> sans « -OnScreenMsg 0 » (celui du Deck compris), plus les arguments qui coupent tout
    /// sauf RetroAchievements. Pur.
    /// </summary>
    public static IReadOnlyList<string> AjouterSaufSucces(IEnumerable<string>? supplements)
    {
        var source = (supplements ?? []).ToList();
        var liste = new List<string>(source.Count + ArgumentsSaufSucces.Count);
        for (var i = 0; i < source.Count; i++)
        {
            if (source[i].Equals(Arguments[0], StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < source.Count && !source[i + 1].StartsWith('-')) i++; // sa valeur part avec lui
                continue;
            }

            liste.Add(source[i]);
        }

        liste.AddRange(ArgumentsSaufSucces);
        return liste;
    }

    /// <summary>La ligne du lanceur coupe-t-elle tout sauf RetroAchievements ? Pur.</summary>
    public static bool LigneGardeLesSucces(string? arguments)
        => !string.IsNullOrEmpty(arguments) && SaufSucces().IsMatch(arguments);

    [GeneratedRegex(@"(^|\s)-retroarch\.notification_show_save_state\s+(0|false)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaufSucces();

    /// <summary>
    /// Les parties du World Scoring (<see cref="PartieNelfePlayService.Origine"/>) : carrousel de la collection, defi du panneau
    /// de classement, bouton du site, commands/launch. Les epreuves (speedrun, challenges), le concours, le MASS RUN et le
    /// MULTIPLAYER gardent tout coupe. Pur.
    /// </summary>
    public static bool EstDuWorldScoring(string? origine)
        => origine is not null
           && (origine.StartsWith("collection", StringComparison.Ordinal) || origine is "defi" or "site" or "api");

    /// <summary>
    /// Le joueur a-t-il RetroAchievements pour ce jeu ? Comme le lanceur (SystemConfig) : la cle du jeu, sinon celle du
    /// systeme, sinon la globale, pour « retroachievements » (allume) et « retroachievements.username » (non vide). Lecture
    /// seule d'es_settings.cfg. Pur.
    /// </summary>
    internal static bool SuccesActifs(System.Xml.Linq.XDocument es, string systeme, string? fichier)
    {
        var valeurs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in es.Root?.Elements() ?? [])
        {
            if ((string?)e.Attribute("name") is { } nom && (string?)e.Attribute("value") is { } valeur) valeurs[nom] = valeur;
        }

        var jeu = string.IsNullOrWhiteSpace(fichier)
            ? null
            : systeme + "[\"" + Path.GetFileName(fichier).Replace("=", "").Replace("#", "") + "\"].";
        string? Lire(string cle)
            => (jeu is not null && valeurs.TryGetValue(jeu + cle, out var j) ? j : null)
               ?? (valeurs.TryGetValue(systeme + "." + cle, out var s) ? s : null)
               ?? (valeurs.TryGetValue("global." + cle, out var g) ? g : null);

        var allume = Lire("retroachievements") is { } v
                     && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase)
                         || v.Equals("enabled", StringComparison.OrdinalIgnoreCase));
        return allume && !string.IsNullOrWhiteSpace(Lire("retroachievements.username"));
    }

    /// <summary>Comme <see cref="SuccesActifs(System.Xml.Linq.XDocument, string, string?)"/>, lu sur le disque. Faux si illisible.</summary>
    public static bool SuccesActifs(string systeme, string? fichier, string? esSettings = null)
    {
        try
        {
            return SuccesActifs(System.Xml.Linq.XDocument.Load(esSettings ?? RetroBatPaths.EmulationStationSettingsPath), systeme, fichier);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Le journal des valeurs de retroarch.cfg a rendre apres une partie qui a garde les succes.</summary>
    internal static string JournalDesSucces
        => JournalDesSuccesDEssai ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "notifications-retroarch.json");

    /// <summary>Coutures des essais : le journal, retroarch.cfg et « RetroArch tourne-t-il ? » (jamais ceux de la borne).</summary>
    internal static string? JournalDesSuccesDEssai { get; set; }

    /// <inheritdoc cref="JournalDesSuccesDEssai"/>
    internal static string? RetroArchCfgDEssai { get; set; }

    /// <inheritdoc cref="JournalDesSuccesDEssai"/>
    internal static Func<bool>? RetroArchTourneDEssai { get; set; }

    /// <summary>
    /// AVANT un lancement qui garde les succes : les valeurs de <see cref="AutresNotifications"/> dans retroarch.cfg sont notees.
    /// Le lanceur reecrit a chaque lancement une partie de ces cles (notification_show_save_state, screenshot...), pas les
    /// autres (autoconfig_fails, refresh_rate, netplay_extra, set_initial_disk, screenshot_flash) : sans rendu, une partie du
    /// joueur lancee ensuite les garderait coupees. Un journal deja la (partie precedente non rendue) est garde : il porte la
    /// valeur du joueur. Jamais d'exception.
    /// </summary>
    public static void NoterAvantLesSucces(ILogger? journal = null)
    {
        var cfg = RetroArchCfgDEssai ?? RetroBatPaths.RetroArchConfigPath;
        var chemin = JournalDesSucces;
        try
        {
            if (File.Exists(chemin) || !File.Exists(cfg)) return;
            var avant = AutresNotifications.ToDictionary(r => r.Key,
                r => RetroBat.Api.Multiplayer.RetroArchConfigEdit.Lire(cfg, r.Key), StringComparer.Ordinal);
            RetroBat.Api.Multiplayer.RetroArchConfigEdit.Noter(chemin,
                new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase) { [cfg] = avant });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal?.LogDebug(ex, "Notifications de RetroArch : valeurs de retroarch.cfg non notees.");
        }
    }

    /// <summary>
    /// Rend a retroarch.cfg les valeurs notees par <see cref="NoterAvantLesSucces"/>, si RetroArch est ferme (il reecrit son
    /// fichier en sortant). Rend vrai si un journal a ete rendu. Jamais d'exception.
    /// </summary>
    public static bool RendreLesAutresNotifications(ILogger? journal = null)
    {
        var chemin = JournalDesSucces;
        try
        {
            if (!File.Exists(chemin) || (RetroArchTourneDEssai ?? RetroArchTourne)()) return false;
            if (RetroBat.Api.Multiplayer.RetroArchConfigEdit.RendreLeJournal(chemin) == 0) return false;
            journal?.LogInformation("Notifications de RetroArch : les reglages notification_show_* du joueur rendus a retroarch.cfg.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal?.LogDebug(ex, "Notifications de RetroArch : notification_show_* non rendus, nouvel essai plus tard.");
            return false;
        }
    }

    /// <summary>
    /// Un lancement direct vient de partir (<see cref="RetroBat.Api.Netplay.NetplayLaunch.DemarrerDirectement"/>) : il coupe ou
    /// non les notifications. Coupees, elles le restent jusqu'a la sortie du lanceur ; RetroArch ferme, la valeur d'ES revient
    /// dans retroarch.cfg. Un lancement suivant remplace celui-ci.
    /// </summary>
    internal static void Suivre(string arguments, Process? lanceur, ILogger? journal)
    {
        var generation = Interlocked.Increment(ref _generation);
        var saufSucces = LigneGardeLesSucces(arguments) && !LigneCoupe(arguments);
        _coupees = lanceur is not null && (LigneCoupe(arguments) || saufSucces);
        if (!_coupees) return;
        if (saufSucces)
            journal?.LogInformation("Notifications de RetroArch coupees pour ce lancement sauf RetroAchievements (notification_show_*) : les messages de NelfePlay vont au Deck ou au bandeau.");
        else
            journal?.LogInformation("Notifications de RetroArch coupees pour ce lancement (-OnScreenMsg 0) : les messages de NelfePlay vont au Deck ou au bandeau.");
        _ = Task.Run(async () =>
        {
            try { await lanceur!.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { /* deja parti */ }
            await FinirAsync(generation, journal).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Un lancement par ES va couper les notifications (cle par jeu posee au crochet game-start,
    /// <see cref="NotificationsDesLancementsEs"/>) : les messages de NelfePlay passent au bandeau. Rend la generation, que
    /// <see cref="FinirAsync"/> attend a la fin de la partie.
    /// </summary>
    internal static long Couper()
    {
        var generation = Interlocked.Increment(ref _generation);
        _coupees = true;
        return generation;
    }

    /// <summary>Un lancement par ES qui garde les notifications (partie du joueur) : RetroArch parle.</summary>
    internal static void Parler()
    {
        Interlocked.Increment(ref _generation);
        _coupees = false;
    }

    /// <summary>Le lanceur est sorti : plus rien de coupe, et la valeur d'ES rendue a retroarch.cfg une fois RetroArch ferme.</summary>
    internal static async Task FinirAsync(long generation, ILogger? journal)
    {
        // Le lanceur rend la main jusqu'a 48 s apres RetroArch (09/11), rarement avant : on laisse RetroArch finir d'ecrire.
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite && RetroArchTourne())
        {
            if (Interlocked.Read(ref _generation) != generation) return;
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        if (Interlocked.Read(ref _generation) != generation) return;
        _coupees = false;
        if (RetroArchTourne()) return; // un RetroArch d'ailleurs : le prochain lancement par le lanceur rendra la valeur
        RendreLesAutresNotifications(journal);
        try
        {
            if (Rendre(RetroBatPaths.RetroArchConfigPath, RetroBat.Api.Deck.DeckDecor.NotificationsDEs()))
                journal?.LogInformation("Notifications de RetroArch : la valeur d'ES rendue a retroarch.cfg.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal?.LogDebug(ex, "Notifications de RetroArch : retroarch.cfg non rendu, le prochain lancement le fera.");
        }
    }

    /// <summary>
    /// Rend la valeur d'ES (<paramref name="valeurDEs"/>, « true » ou « false ») a <see cref="CleRetroArch"/> dans
    /// <paramref name="fichier"/>, si le lancement coupe l'y a laissee a « false ». Rend vrai si le fichier a change.
    /// </summary>
    internal static bool Rendre(string fichier, string valeurDEs)
    {
        if (!File.Exists(fichier) || valeurDEs != "true") return false;
        if (RetroBat.Api.Multiplayer.RetroArchConfigEdit.Lire(fichier, CleRetroArch) is not "false") return false;
        RetroBat.Api.Multiplayer.RetroArchConfigEdit.Poser(fichier, new Dictionary<string, string> { [CleRetroArch] = valeurDEs });
        return true;
    }

    private static bool RetroArchTourne()
    {
        try
        {
            var p = Process.GetProcessesByName("retroarch");
            var oui = p.Length > 0;
            foreach (var x in p) x.Dispose();
            return oui;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// UN MESSAGE DE NELFEPLAY, RETROARCH MUET : au bandeau du haut de l'API plutot qu'a SHOW_MSG, que RetroArch ne
    /// montrerait plus. Rend faux quand RetroArch parle (l'appelant envoie SHOW_MSG comme avant) ou sans bandeau. L'appelant
    /// a deja propose le message au Deck (<see cref="RetroBat.Api.Deck.DeckSilence"/>) : le Deck passe avant.
    /// </summary>
    public static bool AuBandeau(string marque, string texte, int dureeMs = 6000, bool alerte = false)
    {
        if (!_coupees || string.IsNullOrWhiteSpace(texte)) return false;
        var ligne = texte.ReplaceLineEndings(" ").Trim();
        if (BandeauDEssai is { } essai)
        {
            essai(marque, ligne, dureeMs, alerte);
            return true;
        }

        if (Bandeau is not { } bandeau) return false;
        try
        {
            bandeau.ShowTop(marque, ligne, null, dureeMs, alerte);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Couture des essais : pose l'etat sans lanceur.</summary>
    internal static void PoserPourEssai(bool coupees)
    {
        Interlocked.Increment(ref _generation);
        _coupees = coupees;
    }
}
