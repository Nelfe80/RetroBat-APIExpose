using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE CADRE QU'UN THEME DU DECK DEMANDE POUR LE JEU : ou l'image doit tomber dans la fenetre de RetroArch, en pixels
/// physiques relatifs a sa zone cliente (en plein ecran, l'ecran du jeu ; CDC PlayerDeck B §10.3).
/// <c>SystemId</c> et <c>Rom</c> vides valent « tout jeu » ; <c>ScreenWidth</c>/<c>ScreenHeight</c> disent la taille
/// de zone cliente pour laquelle le cadre a ete calcule : sur un autre ecran, le cadre ne s'applique pas.
/// </summary>
public sealed record DeckFramePlan
{
    public string? SystemId { get; init; }
    public string? Rom { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }
    /// <summary>Le theme qui demande ce cadre (pour le diagnostic seulement).</summary>
    public string? Theme { get; init; }
    /// <summary>Vrai : garde par l'API jusqu'a son retrait (REST) ; faux : tant que la connexion du Deck vit (WebSocket).</summary>
    public bool Persist { get; init; }
    public DateTime RequestedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonIgnore]
    public DeckRect Rect => new(X, Y, Width, Height);

    /// <summary>Le motif de refus d'un cadre, ou null s'il est recevable. Pur.</summary>
    public string? Refus()
    {
        if (Width < 16 || Height < 16) return "frame_too_small";
        if (X < 0 || Y < 0) return "frame_outside_screen";
        if (Width > 16384 || Height > 16384) return "frame_too_large";
        if (ScreenWidth is { } sw && (sw <= 0 || X + Width > sw)) return "frame_outside_screen";
        if (ScreenHeight is { } sh && (sh <= 0 || Y + Height > sh)) return "frame_outside_screen";
        if (Rom is { Length: > 0 } rom && rom.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "invalid_rom";
        return null;
    }

    /// <summary>Ce cadre vaut pour ce jeu (systeme et rom compares sans casse ; vide = tout).</summary>
    public bool Vise(string? systemId, string? rom)
        => (string.IsNullOrWhiteSpace(SystemId) || string.Equals(SystemId, systemId, StringComparison.OrdinalIgnoreCase))
           && (string.IsNullOrWhiteSpace(Rom) || string.Equals(Rom, rom, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plus le cadre nomme precisement le jeu, plus il gagne : systeme et rom, puis rom, puis systeme, puis tout.</summary>
    [JsonIgnore]
    public int Precision => (string.IsNullOrWhiteSpace(Rom) ? 0 : 2) + (string.IsNullOrWhiteSpace(SystemId) ? 0 : 1);
}

/// <summary>
/// LA CONFIGURATION DE SESSION QUI IMPOSE LE CADRE A RETROARCH (CDC PlayerDeck B §10.1). Un fichier a AJOUTER a la
/// configuration de RetroArch pour un seul lancement (<c>--appendconfig</c>), jamais a recopier dans retroarch.cfg.
///
/// LA GARDE : la borne a <c>config_save_on_exit = "true"</c>, et RetroArch ecrit a la fermeture tout ce qu'il a en
/// memoire, fichiers ajoutes compris : sans garde, le viewport du Deck se retrouverait dans retroarch.cfg et suivrait
/// tous les jeux suivants. Le fichier porte donc TOUJOURS <c>config_save_on_exit = "false"</c>, en premiere ligne utile,
/// et <see cref="Construire"/> est le seul endroit qui l'ecrit. Les cles sont la liste du CDC, rien d'autre : format
/// personnalise, viewport, pas d'agrandissement entier. Ni shader, ni decor, ni ratio du joueur ne sont touches.
/// </summary>
public static class DeckSessionConfig
{
    public const string GardeEnregistrement = "config_save_on_exit";

    /// <summary>Le texte du fichier ; pur. Le cadre doit etre recevable (<see cref="DeckFramePlan.Refus"/>).</summary>
    public static string Construire(DeckFramePlan plan, string systemId, string rom)
    {
        static string L(string cle, string valeur) => cle + " = \"" + valeur + "\"";
        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("# NelfePlay PlayerDeck : cadre de session pour " + Propre(systemId) + "/" + Propre(rom) + ".");
        sb.AppendLine("# Ecrit par APIExpose pour UN lancement (--appendconfig). Ne pas recopier dans retroarch.cfg.");
        sb.AppendLine(L(GardeEnregistrement, "false"));
        sb.AppendLine(L("video_aspect_ratio_auto", "false"));
        sb.AppendLine(L("aspect_ratio_index", N(DeckViewport.IndexCustom)));
        sb.AppendLine(L("custom_viewport_x", N(plan.X)));
        sb.AppendLine(L("custom_viewport_y", N(plan.Y)));
        sb.AppendLine(L("custom_viewport_width", N(plan.Width)));
        sb.AppendLine(L("custom_viewport_height", N(plan.Height)));
        sb.AppendLine(L("video_scale_integer", "false"));
        return sb.ToString();

        // un nom de jeu n'ouvre pas une ligne de configuration
        static string Propre(string valeur) => (valeur ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
    }

    /// <summary>Vrai si le texte porte la garde contre l'enregistrement a la fermeture. Pur.</summary>
    public static bool EstGarde(string texte)
    {
        var cfg = DeckViewport.LireCfg(texte.Split('\n'));
        return cfg.TryGetValue(GardeEnregistrement, out var valeur) && string.Equals(valeur, "false", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Les cadres demandes. Ceux du WebSocket vivent avec la connexion du Deck (« quand le Deck est actif ») ; ceux du REST
/// sont gardes dans <c>state/deck/cadres.json</c> jusqu'a leur retrait. Fil-sur.
/// </summary>
public sealed class DeckFramePlanStore
{
    private readonly object _verrou = new();
    private readonly Dictionary<object, List<DeckFramePlan>> _parConnexion = new(ReferenceEqualityComparer.Instance);
    private readonly List<DeckFramePlan> _gardes = new();
    private readonly string? _fichier;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public DeckFramePlanStore(string? fichier)
    {
        _fichier = fichier;
        try
        {
            if (_fichier is not null && File.Exists(_fichier)
                && JsonSerializer.Deserialize<List<DeckFramePlan>>(File.ReadAllText(_fichier)) is { } lus)
            {
                _gardes.AddRange(lus.Where(p => p.Refus() is null).Select(p => p with { Persist = true }));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // un fichier illisible ne bloque rien : aucun cadre garde
        }
    }

    /// <summary>Ajoute ou remplace le cadre d'un jeu (meme systeme et meme rom). Rend le motif de refus, ou null.</summary>
    public string? Poser(DeckFramePlan plan, object? connexion)
    {
        if (plan.Refus() is { } refus) return refus;
        lock (_verrou)
        {
            var liste = connexion is null || plan.Persist ? _gardes : Liste(connexion);
            liste.RemoveAll(p => MemeCible(p, plan));
            liste.Add(plan with { Persist = connexion is null || plan.Persist });
            if (ReferenceEquals(liste, _gardes)) Ecrire();
        }
        return null;
    }

    /// <summary>Retire le cadre d'un jeu (systeme et rom vides : tous ceux de cette origine).</summary>
    public int Retirer(string? systemId, string? rom, object? connexion)
    {
        lock (_verrou)
        {
            var cible = new DeckFramePlan { SystemId = systemId, Rom = rom };
            var tous = string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(rom);
            var liste = connexion is null ? _gardes : (_parConnexion.TryGetValue(connexion, out var l) ? l : null);
            if (liste is null) return 0;
            var n = liste.RemoveAll(p => tous || MemeCible(p, cible));
            if (n > 0 && ReferenceEquals(liste, _gardes)) Ecrire();
            return n;
        }
    }

    /// <summary>La connexion du Deck s'est fermee : ses cadres partent avec elle.</summary>
    public bool Oublier(object connexion)
    {
        lock (_verrou)
        {
            return _parConnexion.Remove(connexion);
        }
    }

    /// <summary>Le cadre qui vaut pour ce jeu, le plus precis d'abord, puis le plus recent.</summary>
    public DeckFramePlan? Pour(string? systemId, string? rom)
    {
        lock (_verrou)
        {
            return _parConnexion.Values.SelectMany(l => l).Concat(_gardes)
                .Where(p => p.Vise(systemId, rom))
                .OrderByDescending(p => p.Precision)
                .ThenByDescending(p => p.RequestedAtUtc)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<DeckFramePlan> Tous()
    {
        lock (_verrou)
        {
            return _parConnexion.Values.SelectMany(l => l).Concat(_gardes).ToList();
        }
    }

    private List<DeckFramePlan> Liste(object connexion)
    {
        if (!_parConnexion.TryGetValue(connexion, out var liste))
        {
            liste = new List<DeckFramePlan>();
            _parConnexion[connexion] = liste;
        }
        return liste;
    }

    private static bool MemeCible(DeckFramePlan a, DeckFramePlan b)
        => string.Equals(a.SystemId ?? "", b.SystemId ?? "", StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Rom ?? "", b.Rom ?? "", StringComparison.OrdinalIgnoreCase);

    private void Ecrire()
    {
        if (_fichier is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fichier)!);
            var provisoire = _fichier + ".tmp";
            File.WriteAllText(provisoire, JsonSerializer.Serialize(_gardes, Json));
            File.Move(provisoire, _fichier, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // garde en memoire ; le fichier sera reecrit au prochain changement
        }
    }
}
