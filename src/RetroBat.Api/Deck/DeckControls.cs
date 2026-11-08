using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Deck;

/// <summary>
/// LA CORRESPONDANCE EFFECTIVE DES BOUTONS DU JEU LANCE : bouton physique, puis fonction dans le jeu. Trois faits
/// composes, aucun suppose :
///
///   1. l'identite RetroPad du bouton, telle que l'API la lit (<c>panel.input.*</c>, champ <c>Identity</c>) : c'est
///      celle que RetroArch recoit, la cartographie de la borne etant mesuree de bout en bout (cablage, encodeur,
///      SDL, configuration de RetroBat) ;
///   2. le remappage que RetroArch a REELLEMENT charge pour cette partie (son journal dit lequel : jeu, dossier du
///      contenu ou coeur) : <c>input_player{p}_btn_{identite} = "{id}"</c> envoie le bouton sur un autre id RetroPad ;
///      sans ligne, le bouton passe tel quel ;
///   3. ce que le coeur fait de cet id RetroPad pour ce jeu : la sonde des descripteurs du coeur
///      (<c>dynpanels/games/&lt;rom&gt;.json</c>, <c>buttons.*.libretro["&lt;coeur&gt;"].retropad_id</c>).
///
/// Quand un des trois manque, la reponse le dit au lieu de le combler : remappage illisible = « par defaut du coeur »
/// marque <c>Assumed</c>, coeur non sonde = fonction absente (jamais une fonction devinee par la position du bouton :
/// c'est ce qui avait mis la grenade de Metal Slug sur le mauvais bouton).
/// </summary>
public static class DeckControls
{
    /// <summary>Les ids RetroPad de libretro (RETRO_DEVICE_ID_JOYPAD_*).</summary>
    public static readonly IReadOnlyDictionary<string, int> RetroPadIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = 0, ["y"] = 1, ["select"] = 2, ["start"] = 3, ["up"] = 4, ["down"] = 5, ["left"] = 6, ["right"] = 7,
        ["a"] = 8, ["x"] = 9, ["l"] = 10, ["r"] = 11, ["l2"] = 12, ["r2"] = 13, ["l3"] = 14, ["r3"] = 15,
    };

    /// <summary>Les boutons decrits, dans l'ordre d'un panel : faces, gachettes, puis systeme.</summary>
    public static readonly string[] Identites = ["b", "a", "y", "x", "l", "r", "l2", "r2", "l3", "r3", "select", "start"];

    /// <summary>L'id RetroArch « non assigne » (RARCH_UNMAPPED).</summary>
    private const int NonAssigne = 1024;

    private static readonly Regex LigneDeRemap = new(
        "^\\s*input_player(?<p>\\d+)_btn_(?<id>[a-z0-9]+)\\s*=\\s*\"?(?<v>-?\\d+)\"?\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Les lignes de boutons d'un fichier .rmp : (joueur, identite) vers l'id RetroPad envoye au coeur. Les autres
    /// lignes (appareil, ports, turbo, axes) ne changent pas la fonction d'un bouton et sont ignorees.
    /// </summary>
    public static Dictionary<(int Player, string Identity), int> LireRemap(IEnumerable<string> lignes)
    {
        var remap = new Dictionary<(int, string), int>();
        foreach (var ligne in lignes)
        {
            var m = LigneDeRemap.Match(ligne);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups["p"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var joueur)) continue;
            if (!int.TryParse(m.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valeur)) continue;
            remap[(joueur, m.Groups["id"].Value.ToLowerInvariant())] = valeur;
        }
        return remap;
    }

    /// <summary>Un bouton du jeu tel que la sonde du coeur l'a mesure.</summary>
    public sealed record FonctionDuCoeur(string GameButton, string? Function, string? Descriptor, string? Color);

    /// <summary>
    /// Ce que le coeur fait de chaque id RetroPad pour ce jeu, joueur par joueur, d'apres la sonde rangee dans le
    /// dynpanel. Vide quand le coeur n'a pas ete sonde pour ce jeu. <paramref name="bibliotheque"/> est le nom que le
    /// coeur se donne (<c>library_name</c>, « FinalBurn Neo »), cle de la sonde comme du dossier des remappages.
    /// </summary>
    public static Dictionary<int, Dictionary<int, FonctionDuCoeur>> LireSonde(JsonElement dynpanel, string? bibliotheque)
    {
        var parJoueur = new Dictionary<int, Dictionary<int, FonctionDuCoeur>>();
        if (string.IsNullOrWhiteSpace(bibliotheque)
            || !dynpanel.TryGetProperty("players", out var joueurs) || joueurs.ValueKind != JsonValueKind.Object)
        {
            return parJoueur;
        }

        foreach (var joueur in joueurs.EnumerateObject())
        {
            if (!int.TryParse(joueur.Name, out var numero) || numero < 1) continue;
            if (!joueur.Value.TryGetProperty("buttons", out var boutons) || boutons.ValueKind != JsonValueKind.Object) continue;
            var ids = new Dictionary<int, FonctionDuCoeur>();
            foreach (var bouton in boutons.EnumerateObject())
            {
                if (!bouton.Value.TryGetProperty("libretro", out var libretro) || libretro.ValueKind != JsonValueKind.Object
                    || !libretro.TryGetProperty(bibliotheque, out var mesure) || mesure.ValueKind != JsonValueKind.Object
                    || !mesure.TryGetProperty("retropad_id", out var idElement) || !idElement.TryGetInt32(out var id))
                {
                    continue;
                }

                ids[id] = new FonctionDuCoeur(
                    GameButton: bouton.Name,
                    Function: Texte(bouton.Value, "function"),
                    Descriptor: Texte(mesure, "descriptor"),
                    Color: Texte(bouton.Value, "color"));
            }

            if (ids.Count > 0) parJoueur[numero] = ids;
        }

        return parJoueur;
    }

    /// <summary>Les boutons systeme du dynpanel (Start, Coin) : leur libelle et leur couleur.</summary>
    public static Dictionary<string, (string? Label, string? Color)> LireBoutonsSysteme(JsonElement dynpanel)
    {
        var systeme = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!dynpanel.TryGetProperty("players", out var joueurs) || joueurs.ValueKind != JsonValueKind.Object
            || !joueurs.TryGetProperty("1", out var premier)
            || !premier.TryGetProperty("system_inputs", out var entrees) || entrees.ValueKind != JsonValueKind.Object)
        {
            return systeme;
        }

        foreach (var entree in entrees.EnumerateObject())
        {
            // « coin » est sur SELECT dans RetroBat ; « start » sur START
            var identite = entree.Name.ToLowerInvariant() switch
            {
                "coin" => "select",
                "start" => "start",
                _ => null
            };
            if (identite is null) continue;
            systeme[identite] = (Texte(entree.Value, "label"), Texte(entree.Value, "color"));
        }

        return systeme;
    }

    /// <summary>Un bouton physique et ce qu'il fait reellement dans le jeu lance.</summary>
    public sealed record Bouton(
        string Identity,
        string? RetroPad,
        bool Remapped,
        bool Disabled,
        string? GameButton,
        string? Function,
        string? Descriptor,
        string? Color);

    /// <summary>
    /// Compose la correspondance d'un joueur. <paramref name="remap"/> est null quand on ne sait pas quel remappage
    /// RetroArch a charge (repli : chaque bouton passe tel quel, comme sans remappage). Pur.
    /// </summary>
    public static IReadOnlyList<Bouton> Composer(
        int joueur,
        IReadOnlyDictionary<(int Player, string Identity), int>? remap,
        IReadOnlyDictionary<int, FonctionDuCoeur>? sonde,
        IReadOnlyDictionary<string, (string? Label, string? Color)>? systeme)
    {
        var nomsParId = RetroPadIds.ToDictionary(p => p.Value, p => p.Key);
        var boutons = new List<Bouton>(Identites.Length);
        foreach (var identite in Identites)
        {
            var idPhysique = RetroPadIds[identite];
            var remappe = false;
            var idEnvoye = idPhysique;
            if (remap is not null && remap.TryGetValue((joueur, identite), out var valeur))
            {
                idEnvoye = valeur;
                remappe = valeur != idPhysique;
            }

            // RetroArch ne connait pas d'id au-dela de R3 pour un bouton ; « non assigne » et toute valeur hors table
            // coupent le bouton
            var coupe = idEnvoye == NonAssigne || idEnvoye < 0 || !nomsParId.ContainsKey(idEnvoye);
            string? nomEnvoye = coupe ? null : nomsParId[idEnvoye];

            FonctionDuCoeur? fonction = null;
            if (!coupe && sonde is not null) sonde.TryGetValue(idEnvoye, out fonction);

            string? libelleSysteme = null, couleurSysteme = null;
            if (!coupe && fonction is null && systeme is not null && nomEnvoye is not null
                && systeme.TryGetValue(nomEnvoye, out var sys))
            {
                (libelleSysteme, couleurSysteme) = sys;
            }

            // Un bouton gare sur R3 (le remappage de la borne y range les boutons dont le jeu n'a pas l'usage) ne fait
            // rien quand le coeur n'y met aucune fonction : on le dit coupe.
            if (!coupe && remappe && nomEnvoye == "r3" && fonction is null) coupe = true;

            boutons.Add(new Bouton(
                Identity: identite,
                RetroPad: nomEnvoye,
                Remapped: remappe,
                Disabled: coupe,
                GameButton: fonction?.GameButton,
                Function: fonction?.Function ?? libelleSysteme,
                Descriptor: fonction?.Descriptor,
                Color: fonction?.Color ?? couleurSysteme));
        }

        return boutons;
    }

    /// <summary>
    /// Le nom que le coeur se donne (<c>corename</c> de son fichier .info), qui nomme aussi le dossier des remappages ;
    /// null si le fichier manque. Les corrections de dossier heritees de LedPanel-Manager sont reprises.
    /// </summary>
    public static string? NomDeBibliotheque(IEnumerable<string> lignesDuInfo)
    {
        foreach (var ligne in lignesDuInfo)
        {
            var t = ligne.Trim();
            if (!t.StartsWith("corename", StringComparison.OrdinalIgnoreCase)) continue;
            var morceaux = t.Split('=', 2);
            if (morceaux.Length < 2) continue;
            var valeur = morceaux[1].Trim().Trim('"');
            return valeur switch
            {
                "Caprice32" => "cap32",
                "Dolphin" => "dolphin-emu",
                _ => valeur
            };
        }
        return null;
    }

    private static string? Texte(JsonElement element, string propriete)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propriete, out var valeur)
           && valeur.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(valeur.GetString())
            ? valeur.GetString()
            : null;
}
