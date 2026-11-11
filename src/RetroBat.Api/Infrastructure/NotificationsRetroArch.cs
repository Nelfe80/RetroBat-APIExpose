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
/// (avec le Deck), netplay de l'hote et de l'invite quand ES ne repond pas. Une partie lancee par ES (carrousel du World
/// Scoring, defi sans Deck) garde les notifications : l'API d'ES ne prend qu'un chemin de ROM, et le seul moyen de les couper
/// serait une cle d'es_settings.cfg (interdite hors <c>global.apiexpose.*</c>). Une partie hors NelfePlay n'est pas touchee.
///
/// LES MESSAGES DE NELFEPLAY RESTENT VISIBLES : un message que l'API envoyait par SHOW_MSG (speedrun, MULTIPLAYER, filtre
/// d'image, choix du Deck) passe au Deck s'il est la (deck.notice), sinon au bandeau du haut de l'API
/// (<see cref="LiveContestOverlayService.ShowTop"/>, celui du scoring), sinon a RetroArch comme avant (<see cref="AuBandeau"/>).
///
/// LA VALEUR DU JOUEUR EST RENDUE : le lanceur a ecrit video_font_enable = false dans retroarch.cfg ; RetroArch ferme (et le
/// lanceur sorti), la valeur d'ES y revient, pour un RetroArch ouvert ensuite sans le lanceur.
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

    /// <summary>
    /// Un lancement direct vient de partir (<see cref="RetroBat.Api.Netplay.NetplayLaunch.DemarrerDirectement"/>) : il coupe ou
    /// non les notifications. Coupees, elles le restent jusqu'a la sortie du lanceur ; RetroArch ferme, la valeur d'ES revient
    /// dans retroarch.cfg. Un lancement suivant remplace celui-ci.
    /// </summary>
    internal static void Suivre(string arguments, Process? lanceur, ILogger? journal)
    {
        var generation = Interlocked.Increment(ref _generation);
        _coupees = lanceur is not null && LigneCoupe(arguments);
        if (!_coupees) return;
        journal?.LogInformation("Notifications de RetroArch coupees pour ce lancement (-OnScreenMsg 0) : les messages de NelfePlay vont au Deck ou au bandeau.");
        _ = Task.Run(async () =>
        {
            try { await lanceur!.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { /* deja parti */ }
            await FinirAsync(generation, journal).ConfigureAwait(false);
        });
    }

    /// <summary>Le lanceur est sorti : plus rien de coupe, et la valeur d'ES rendue a retroarch.cfg une fois RetroArch ferme.</summary>
    private static async Task FinirAsync(long generation, ILogger? journal)
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
