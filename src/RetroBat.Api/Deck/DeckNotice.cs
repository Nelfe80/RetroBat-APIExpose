using System.Globalization;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Deck;

/// <summary>
/// deck.notice : un message utile au joueur, montre par le PlayerDeck dans une plaque de ses rails au lieu de RetroArch ou
/// d'une fenetre de l'API, tant que le Deck est pose (demande du user du 08/11 : rien ne passe devant le Deck). Texte court en
/// anglais, en majuscules a l'ecran ; <c>Value</c> s'ecrit en gros chiffres (score, temps, pourcentage).
/// <c>Severity</c> : info, good, warn, alert, record. <c>DurationMs</c> : combien de temps la plaque reste. <c>Kind</c> : la
/// cle du message (speedrun.msg.finish_best, scoring.continue...), pour les journaux et les essais.
/// </summary>
public sealed record DeckNotice(string Kind, string Severity, string Title, string Text, string? Value, int DurationMs)
{
    /// <summary>D'ou vient le message (speedrun, scoring, deck, multiplayer, platform, video).</summary>
    public string Source { get; init; } = "";

    /// <summary>Numero d'ordre, pose a la publication : une plaque nouvelle remplace la precedente.</summary>
    public long Sequence { get; init; }
}

/// <summary>Les gravites d'une plaque de notification (couleur du bandeau dans le Deck).</summary>
public static class DeckSeverity
{
    public const string Info = "info";
    public const string Good = "good";
    public const string Warn = "warn";
    public const string Alert = "alert";
    public const string Record = "record";
}

/// <summary>
/// CE QUE LE DECK DIT A LA PLACE DE RETROARCH ET DES FENETRES DE L'API. Des textes courts, en anglais, ecrits pour une plaque
/// de rail (titre de 16 caracteres au plus, une ligne de detail) ; les longues phrases de RetroArch restent a RetroArch quand
/// le Deck n'est pas la. Pur : tout se teste sans machine.
/// </summary>
public static class DeckNotices
{
    public const int Court = 3500;
    public const int Moyen = 5000;
    public const int Long = 8000;

    /// <summary>
    /// Un message du speedrun ou du challenge. Null : rien a dire au Deck, sa couche epreuve le montre deja (coup recu : le
    /// compteur, sa jauge et sa penalite bougent).
    /// </summary>
    public static DeckNotice? DuSpeedrun(MessageJoueur m)
    {
        string V(string nom)
        {
            foreach (var (n, v) in m.Valeurs) if (n == nom) return v;
            return "";
        }

        var n = m.Cle switch
        {
            "speedrun.msg.hit" or "speedrun.msg.hit_left" or "speedrun.msg.hit_penalty" => null,
            "speedrun.msg.ready" => new DeckNotice(m.Cle, DeckSeverity.Info, "READY", "HOTKEY+LEFT: RESTART", null, Moyen),
            "speedrun.msg.failed" or "speedrun.msg.failed_restart" => new DeckNotice(m.Cle, DeckSeverity.Alert, "FAILED", "HOTKEY+LEFT: RESTART", null, Moyen),
            "speedrun.msg.voided_pause" => new DeckNotice(m.Cle, DeckSeverity.Alert, "VOIDED", "PAUSE", null, Moyen),
            "speedrun.msg.voided_fast_forward" => new DeckNotice(m.Cle, DeckSeverity.Alert, "VOIDED", "FAST FORWARD", null, Moyen),
            "speedrun.msg.voided_slowdown" => new DeckNotice(m.Cle, DeckSeverity.Alert, "VOIDED", "GAME SLOWED DOWN", null, Moyen),
            "speedrun.msg.voided_debug" => new DeckNotice(m.Cle, DeckSeverity.Alert, "VOIDED", "DEBUG MODE", null, Moyen),
            "speedrun.msg.voided" => new DeckNotice(m.Cle, DeckSeverity.Alert, "VOIDED", "HOTKEY+LEFT: RESTART", null, Moyen),
            "speedrun.msg.finish" => new DeckNotice(m.Cle, DeckSeverity.Good, "FINISH", V("best") is { Length: > 0 } b ? "BEST " + b : "", V("time"), Long),
            "speedrun.msg.finish_best" => new DeckNotice(m.Cle, DeckSeverity.Record, "NEW BEST", "RANKED WHEN YOU QUIT", V("time"), Long),
            "speedrun.msg.finish_unranked" => new DeckNotice(m.Cle, DeckSeverity.Warn, "NOT RANKED", "GAME TIMER DISAGREES", V("time"), Long),
            "speedrun.msg.slow_game" => new DeckNotice(m.Cle, DeckSeverity.Warn, "SLOW GAME", "SET SCREEN TO 60 HZ", V("percent") is { Length: > 0 } p ? p + "%" : null, Long),
            "speedrun.msg.locks_missing" => new DeckNotice(m.Cle, DeckSeverity.Alert, "LOCKS OFF", "NO TIME WILL BE SENT", null, Long),
            "speedrun.msg.state_failed" => new DeckNotice(m.Cle, DeckSeverity.Alert, "NOT STARTED", "START STATE NOT LOADED", null, Long),
            "speedrun.msg.no_pause" => new DeckNotice(m.Cle, DeckSeverity.Warn, "NO PAUSE", "DURING A CHALLENGE", null, Court),
            "speedrun.msg.pause_held" => new DeckNotice(m.Cle, DeckSeverity.Warn, "RESTART", "PAUSE HELD", null, Court),
            "speedrun.msg.debug_blocked" => new DeckNotice(m.Cle, DeckSeverity.Alert, "DEBUG MODE", "BACK TO TITLE SCREEN", null, Long),
            _ => new DeckNotice(m.Cle, DeckSeverity.Info, "CHALLENGE", Court40(Remplir(m)), null, Moyen),
        };
        return n is null ? null : n with { Source = "speedrun" };
    }

    /// <summary>Le choix du Deck en jeu (Select + A, Ctrl+Alt+D).</summary>
    public static DeckNotice DuChoix(bool off, bool decorPose, string? nom, string maintenant)
    {
        var (titre, texte) = off
            ? ("PLAYERDECK OFF", decorPose ? "FRAME STAYS TILL RELAUNCH" : "")
            : maintenant == "next-launch" ? (Court16(nom ?? "PLAYERDECK"), "FROM NEXT LAUNCH") : (Court16(nom ?? "PLAYERDECK"), "PLAYERDECK ON");
        return new DeckNotice("deck.choice", DeckSeverity.Info, titre, texte, null, Court) { Source = "deck" };
    }

    /// <summary>
    /// LES BANDEAUX DU SCORING (prevol, continue, credit, 1CC, rien de mesure...). <paramref name="cle"/> : la cle de l'annonce
    /// (scoring_continue, scoring_clear...) ; <paramref name="score"/> : le score certifie qu'elle cite ; <paramref name="detail"/> :
    /// le detail anglais quand il porte une information (cause d'un refus, coeur aveugle).
    /// </summary>
    public static DeckNotice Scoring(string cle, long? score = null, string? detail = null)
    {
        var valeur = score is { } s ? s.ToString("N0", CultureInfo.InvariantCulture) : null;
        var n = cle switch
        {
            "scoring_certifiable" => new DeckNotice(cle, DeckSeverity.Good, "CERTIFIABLE", Court40(detail ?? "FOR THE LEADERBOARD"), null, Moyen),
            "scoring_offline_certifiable" => new DeckNotice(cle, DeckSeverity.Good, "CERTIFIABLE", "SENT WHEN NELFEPLAY IS BACK", null, Moyen),
            "scoring_not_certifiable" => new DeckNotice(cle, DeckSeverity.Alert, "NOT CERTIFIABLE", Court40(detail ?? ""), null, Long),
            "scoring_emulator_pending" => new DeckNotice(cle, DeckSeverity.Warn, "EMULATOR PENDING", "SCORE KEPT, RANKED LATER", null, Long),
            "scoring_settings_pending" => new DeckNotice(cle, DeckSeverity.Warn, "SETTINGS PENDING", "SCORE KEPT, RANKED LATER", null, Long),
            "scoring_offline" => new DeckNotice(cle, DeckSeverity.Info, "OFFLINE", "SCORE SENT LATER", null, Moyen),
            "scoring_nothing_measured" => new DeckNotice(cle, DeckSeverity.Alert, "NO SCORE", Court40(detail ?? "NOTHING WILL BE MEASURED"), null, Long),
            "scoring_none_measured" => new DeckNotice(cle, DeckSeverity.Alert, "NO SCORE", "NOTHING WAS MEASURED", null, Long),
            "scoring_continue" => new DeckNotice(cle, DeckSeverity.Warn, "CONTINUE", "CERTIFIED SCORE", valeur, Long),
            "scoring_credit" => new DeckNotice(cle, DeckSeverity.Warn, "CREDIT USED", "CERTIFIED SCORE", valeur, Long),
            "scoring_joined" => new DeckNotice(cle, DeckSeverity.Warn, "PLAYER JOINED", "SOLO SCORE KEPT", valeur, Long),
            "scoring_clear" => new DeckNotice(cle, DeckSeverity.Record, "1CC", "ONE-CREDIT CLEAR", valeur, Long),
            "scoring_uncertified" => new DeckNotice(cle, DeckSeverity.Alert, "NOT CERTIFIABLE", "QUIT AND RELAUNCH", null, Long),
            "scoring_credits_muted" => new DeckNotice(cle, DeckSeverity.Alert, "CREDITS UNREAD", "QUIT AND RELAUNCH", null, Long),
            "scoring_multiplayer" => new DeckNotice(cle, DeckSeverity.Warn, "MULTIPLAYER", "OUT OF SOLO RANKING", null, Long),
            _ => new DeckNotice(cle, DeckSeverity.Info, "SCORING", Court40(detail ?? ""), valeur, Moyen),
        };
        return n with { Source = "scoring" };
    }

    /// <summary>Un message en anglais sans plaque dediee (MULTIPLAYER, filtre d'image) : titre court, detail en une ligne.</summary>
    public static DeckNotice Message(string kind, string severity, string titre, string texte, string source, int dureeMs = Moyen)
        => new DeckNotice(kind, severity, Court16(titre), Court40(texte), null, dureeMs) { Source = source };

    private static string Remplir(MessageJoueur m)
    {
        var t = m.ParDefaut;
        foreach (var (n, v) in m.Valeurs) t = t.Replace("{" + n + "}", v, StringComparison.Ordinal);
        return t;
    }

    /// <summary>En majuscules, sur une ligne, coupe a <paramref name="max"/> caracteres (au dernier espace qui tient).</summary>
    internal static string Couper(string texte, int max)
    {
        var t = string.Join(' ', (texte ?? "").ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        if (t.Length <= max) return t;
        var coupe = t.LastIndexOf(' ', max);
        return (coupe > max / 2 ? t[..coupe] : t[..max]).TrimEnd(',', ';', ':', '.');
    }

    private static string Court16(string t) => Couper(t, 16);
    private static string Court40(string t) => Couper(t, 40);
}
