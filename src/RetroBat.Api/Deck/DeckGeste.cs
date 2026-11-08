namespace RetroBat.Api.Deck;

/// <summary>
/// LE GESTE DU CHOIX DU DECK EN JEU (CDC PlayerDeck H-D20, gestes du CDC E.6) : <b>Select + A, appui court</b>. Select
/// tenu (le bouton hotkey de RetroBat sur la borne), A appuye puis relache en moins de <see cref="AppuiCourt"/> : Deck
/// suivant, puis Off, puis le premier. Lu par l'API dans les appuis du lecteur de la borne (<c>panel.input.*</c>), jamais
/// par le Deck (CDC 0.3, P0-5).
///
/// Pourquoi celui-la : E.6 recense les combinaisons prises par RetroArch et RetroBat (Select + B menu, + Start quitter,
/// + X / Y etats, + R1, + L3 / R3, + croix) ; sur la borne, L1 est aussi pris (<c>input_disk_eject_toggle_btn = "9"</c>).
/// Reste Select + A. E.6 y met, en speedrun, la relance au maintien d'une seconde : l'appui court du Deck et le maintien de
/// la relance ne se confondent pas (au-dela de l'appui court, le Deck ne fait rien). RetroArch bloque les entrees du jeu
/// tant que Select est tenu (delai de blocage des raccourcis) : appuyer Select d'abord, A ensuite.
/// </summary>
public sealed class DeckGeste
{
    public static readonly TimeSpan AppuiCourt = TimeSpan.FromMilliseconds(500);

    private bool _select;
    private DateTime? _aDepuis;

    /// <summary>Un appui ou un relachement du joueur 1. Vrai quand le geste est accompli (au relachement de A).</summary>
    public bool Entree(string? identite, int joueur, bool appuye, DateTime quand)
    {
        if (joueur != 1) return false;
        switch ((identite ?? string.Empty).ToLowerInvariant())
        {
            case "select":
                _select = appuye;
                if (!appuye) _aDepuis = null;
                return false;
            case "a":
                if (appuye)
                {
                    _aDepuis = _select ? quand : null;
                    return false;
                }

                var depuis = _aDepuis;
                _aDepuis = null;
                return _select && depuis is { } d && quand - d <= AppuiCourt;
            default:
                return false;
        }
    }

    /// <summary>Les manettes ont change : rien n'est tenu.</summary>
    public void Oublier()
    {
        _select = false;
        _aDepuis = null;
    }

    /// <summary>
    /// La combinaison est-elle libre dans le retroarch.cfg reel (E.6 : « l'API verifie les combinaisons avant de les
    /// proposer ») ? Le bouton hotkey doit etre le Select du joueur 1, et le bouton A ne doit porter aucun raccourci de
    /// RetroArch. Pur ; <paramref name="raison"/> dit ce qui manque.
    /// </summary>
    public static bool CombinaisonLibre(IEnumerable<string> lignes, out string raison)
    {
        var cfg = DeckViewport.LireCfg(lignes);
        string? Bouton(string cle) => cfg.TryGetValue(cle, out var v) && v.Length > 0 && !string.Equals(v, "nul", StringComparison.OrdinalIgnoreCase) ? v : null;

        var hotkey = Bouton("input_enable_hotkey_btn");
        var select = Bouton("input_player1_select_btn");
        var a = Bouton("input_player1_a_btn");
        if (a is null)
        {
            raison = "a_not_bound";
            return false;
        }

        if (hotkey is null || hotkey != select)
        {
            raison = "hotkey_not_select";
            return false;
        }

        foreach (var (cle, valeur) in cfg)
        {
            if (!cle.StartsWith("input_", StringComparison.Ordinal) || !cle.EndsWith("_btn", StringComparison.Ordinal)
                || cle.StartsWith("input_player", StringComparison.Ordinal) || cle == "input_enable_hotkey_btn") continue;
            if (valeur == a)
            {
                raison = "a_is_hotkey:" + cle;
                return false;
            }
        }

        raison = "";
        return true;
    }
}
