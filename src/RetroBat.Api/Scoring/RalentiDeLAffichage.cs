using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LE JEU RALENTI PAR L'AFFICHAGE (11/11, decision du user : option A, refus). Une ROM NTSC (60 Hz) sur un ecran regle a
/// 50 Hz avec la synchronisation verticale tourne a 50 images par seconde : le jeu va a 83 % de sa vitesse, il est plus facile.
/// La mesure : la mediane de la cadence observee pendant la partie (images emulees par seconde reelle, pauses ecartees) face a
/// la cadence que le coeur annonce. Une ROM PAL annonce 50 et tourne a 50 : elle passe. Un jeu d'arcade a 59,18 Hz cale sur un
/// ecran a 60 Hz va un peu PLUS vite (1,4 %) : il passe aussi. Les speedruns ont deja leur garde (Evaluateur, « slowdown »).
/// </summary>
public static class RalentiDeLAffichage
{
    /// <summary>Sous cette part de sa vitesse, le jeu est ralenti (meme seuil que les speedruns).</summary>
    public const double VitesseMinimale = 0.97;

    /// <summary>Sous ce nombre de mesures (~12 s de partie), la mediane ne dit rien.</summary>
    public const int MesuresMinimales = 8;

    public const string Raison = "runtime.slow_motion";

    /// <summary>La vitesse du jeu (1 = normale), ou null si on ne sait pas. Pur.</summary>
    public static double? Vitesse(double? coeurFps, double? mediane, int mesures)
        => coeurFps is > 1 && mediane is > 1 && mesures >= MesuresMinimales ? mediane.Value / coeurFps.Value : null;

    public static bool Ralenti(double? vitesse) => vitesse is { } v && v < VitesseMinimale;

    /// <summary>La cle du message dans le dictionnaire d'interface (Data Pack, locales/interface-texts.json).</summary>
    public const string Cle = "scoring.msg.slow_motion";

    /// <summary>Le message en anglais, quand le dictionnaire de la borne n'a pas encore la cle (Data Pack en retard).</summary>
    public const string TexteParDefaut = "Score not sent: the game ran at {speed} of its speed ({fps} frames/s). Your display is "
        + "probably set to 50 Hz: switch it to 60 Hz (PAL60, which most PAL TVs accept over RGB SCART), or play the PAL version of the game.";

    /// <summary>
    /// Ce que le joueur lit : la vitesse mesuree, et les deux gestes qui le remettent au classement. Le texte vient du
    /// dictionnaire d'interface dans <paramref name="langue"/> (celle du joueur, sinon celle de la borne), l'anglais sinon ;
    /// la vitesse et la cadence s'ecrivent a la facon de cette langue.
    /// </summary>
    public static string Message(double vitesse, double? mediane, InterfaceTextService? textes = null, string? langue = null)
    {
        var culture = CabinetLocale.CultureDe(langue ?? "en");
        var modele = textes?.Text(Cle, langue);
        if (string.IsNullOrWhiteSpace(modele) || modele == Cle) modele = TexteParDefaut;
        return modele.Replace("{speed}", vitesse.ToString("P0", culture))
            .Replace("{fps}", mediane is { } m ? m.ToString("0.#", culture) : "?");
    }
}
