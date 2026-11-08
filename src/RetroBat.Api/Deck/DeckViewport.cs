using System.Globalization;

namespace RetroBat.Api.Deck;

/// <summary>Un rectangle en pixels physiques (ecran ou zone cliente).</summary>
public readonly record struct DeckRect(int X, int Y, int Width, int Height)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Vide => Width <= 0 || Height <= 0;
    public DeckRect Decale(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
}

/// <summary>Les reglages video de RetroArch qui placent l'image dans sa fenetre.</summary>
public sealed record DeckVideoSettings(
    int? AspectRatioIndex,
    double? VideoAspectRatio,
    bool AspectRatioAuto,
    DeckRect? CustomViewport,
    bool ScaleInteger,
    double BiasX,
    double BiasY,
    bool AllowRotate,
    bool Fullscreen,
    bool WindowedFullscreen)
{
    /// <summary>Les cles lues dans un fichier de configuration de RetroArch (cle = "valeur"), absentes = defauts de RetroArch.</summary>
    public static DeckVideoSettings Depuis(IReadOnlyDictionary<string, string> cfg)
    {
        int? Entier(string cle) => cfg.TryGetValue(cle, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        double? Reel(string cle) => cfg.TryGetValue(cle, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        bool Vrai(string cle, bool defaut) => cfg.TryGetValue(cle, out var v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : defaut;

        DeckRect? perso = null;
        if (Entier("custom_viewport_width") is not null || Entier("custom_viewport_height") is not null)
        {
            perso = new DeckRect(Entier("custom_viewport_x") ?? 0, Entier("custom_viewport_y") ?? 0,
                Entier("custom_viewport_width") ?? 0, Entier("custom_viewport_height") ?? 0);
        }

        return new DeckVideoSettings(
            AspectRatioIndex: Entier("aspect_ratio_index"),
            VideoAspectRatio: Reel("video_aspect_ratio"),
            AspectRatioAuto: Vrai("video_aspect_ratio_auto", false),
            CustomViewport: perso,
            ScaleInteger: Vrai("video_scale_integer", false),
            BiasX: Reel("video_viewport_bias_x") ?? 0.5,
            BiasY: Reel("video_viewport_bias_y") ?? 0.5,
            AllowRotate: Vrai("video_allow_rotate", true),
            Fullscreen: Vrai("video_fullscreen", false),
            WindowedFullscreen: Vrai("video_windowed_fullscreen", true));
    }
}

/// <summary>
/// OU RETROARCH DESSINE LE JEU DANS SA FENETRE. RetroArch ne publie pas son viewport ; on le recalcule comme lui
/// (<c>video_viewport_get_scaled_aspect</c>, RetroArch 1.22) a partir de la zone cliente mesuree et des reglages qu'il
/// a lus au lancement. Calcul en simple precision et arrondis de RetroArch, pour tomber au pixel pres.
/// </summary>
public static class DeckViewport
{
    /// <summary>Les formats fixes de RetroArch, index 0 a 19 de <c>aspect_ratio_index</c>.</summary>
    private static readonly float[] FormatsFixes =
    [
        4f / 3f, 16f / 9f, 16f / 10f, 16f / 15f, 21f / 9f, 1f, 2f, 3f / 2f, 3f / 4f, 4f,
        9f / 16f, 5f / 4f, 6f / 5f, 7f / 9f, 8f / 3f, 8f / 7f, 19f / 12f, 19f / 14f, 30f / 17f, 32f / 9f,
    ];

    public const int IndexConfig = 20;
    public const int IndexSquare = 21;
    public const int IndexCore = 22;
    public const int IndexCustom = 23;
    public const int IndexFull = 24;

    /// <summary>Le mode de placement, en clair, pour le client.</summary>
    public static string Mode(DeckVideoSettings s)
    {
        if (s.AspectRatioAuto) return "core";
        return s.AspectRatioIndex switch
        {
            IndexConfig => "config",
            IndexSquare => "square",
            IndexCore => "core",
            IndexCustom => "custom",
            IndexFull => "full",
            >= 0 and < 20 => "fixed",
            _ => "core",
        };
    }

    /// <summary>La forme voulue de l'image, comme RetroArch la choisit ; null pour « custom » et « full ».</summary>
    public static float? FormatVoulu(DeckVideoSettings s, int largeurAffichee, int hauteurAffichee, double? aspectDuCoeur)
    {
        float Coeur()
            => aspectDuCoeur is > 0 ? (float)aspectDuCoeur.Value
               : hauteurAffichee > 0 ? (float)largeurAffichee / hauteurAffichee : 4f / 3f;

        return Mode(s) switch
        {
            "custom" or "full" => null,
            "config" => s.VideoAspectRatio is > 0 ? (float)s.VideoAspectRatio.Value : Coeur(),
            "square" => hauteurAffichee > 0 ? (float)largeurAffichee / hauteurAffichee : Coeur(),
            "fixed" => FormatsFixes[s.AspectRatioIndex!.Value],
            _ => Coeur(),
        };
    }

    /// <summary>
    /// Le rectangle de l'image dans la zone cliente (origine en haut a gauche, pixels physiques). Le second membre dit
    /// si le calcul est approche (agrandissement entier, dont les regles de RetroArch 1.22 ne sont reprises qu'en gros).
    /// </summary>
    public static (DeckRect Rect, bool Approche) Calculer(
        DeckVideoSettings s, int largeurFenetre, int hauteurFenetre, int largeurAffichee, int hauteurAffichee, double? aspectDuCoeur)
    {
        if (largeurFenetre <= 0 || hauteurFenetre <= 0) return (default, false);

        var mode = Mode(s);
        if (mode == "custom")
        {
            var c = s.CustomViewport;
            if (c is null || c.Value.Width <= 0 || c.Value.Height <= 0) return (new DeckRect(0, 0, largeurFenetre, hauteurFenetre), false);
            return (c.Value, false);
        }

        if (mode == "full") return (new DeckRect(0, 0, largeurFenetre, hauteurFenetre), false);

        var voulu = FormatVoulu(s, largeurAffichee, hauteurAffichee, aspectDuCoeur) ?? (float)largeurFenetre / hauteurFenetre;

        if (s.ScaleInteger && largeurAffichee > 0 && hauteurAffichee > 0)
        {
            // Approche : le plus grand multiple entier de la hauteur du jeu qui tient, largeur au format voulu.
            var facteur = Math.Max(1, hauteurFenetre / hauteurAffichee);
            var h = hauteurAffichee * facteur;
            var w = (int)MathF.Round(h * voulu);
            while (w > largeurFenetre && facteur > 1)
            {
                facteur--;
                h = hauteurAffichee * facteur;
                w = (int)MathF.Round(h * voulu);
            }
            var xi = (int)MathF.Round((largeurFenetre - w) * (float)s.BiasX);
            var yi = (int)MathF.Round((hauteurFenetre - h) * (float)s.BiasY);
            return (new DeckRect(xi, yi, w, h), true);
        }

        float ecran = (float)largeurFenetre / hauteurFenetre;
        int x = 0, y = 0, largeur = largeurFenetre, hauteur = hauteurFenetre;
        if (MathF.Abs(ecran - voulu) < 0.0001f)
        {
            // meme forme : toute la fenetre
        }
        else if (ecran > voulu)
        {
            var delta = (voulu / ecran - 1.0f) / 2.0f + 0.5f;
            x = (int)MathF.Round(largeurFenetre * ((0.5f - delta) * ((float)s.BiasX * 2.0f)));
            largeur = (int)MathF.Round(2.0f * largeurFenetre * delta);
        }
        else
        {
            var delta = (ecran / voulu - 1.0f) / 2.0f + 0.5f;
            y = (int)MathF.Round(hauteurFenetre * ((0.5f - delta) * ((float)s.BiasY * 2.0f)));
            hauteur = (int)MathF.Round(2.0f * hauteurFenetre * delta);
        }

        return (new DeckRect(x, y, largeur, hauteur), false);
    }

    /// <summary>Lit un fichier de configuration de RetroArch (cle = "valeur"), la derniere occurrence gagnant.</summary>
    public static Dictionary<string, string> LireCfg(IEnumerable<string> lignes, Dictionary<string, string>? base_ = null)
    {
        var cfg = base_ ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var brute in lignes)
        {
            var ligne = brute.Trim();
            if (ligne.Length == 0 || ligne[0] == '#') continue;
            var egal = ligne.IndexOf('=');
            if (egal <= 0) continue;
            var cle = ligne[..egal].Trim();
            var valeur = ligne[(egal + 1)..].Trim();
            if (valeur.Length >= 2 && valeur[0] == '"' && valeur[^1] == '"') valeur = valeur[1..^1];
            cfg[cle] = valeur;
        }
        return cfg;
    }
}
