using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroBat.Api.Deck;

/// <summary>
/// LES MODES OU LE DECK PART (CDC PlayerDeck H-D16, H-D18, H-D20) : le choix d'un Deck est retenu par mode. Adventure
/// est prevu dans le format et dans le choix, non branche tant que le mode n'existe pas.
/// </summary>
public static class DeckModes
{
    /// <summary>Le bouton CHALLENGE du panneau World Scoring.</summary>
    public const string WorldScoring = "world-scoring";

    /// <summary>LAUNCH THE SPEEDRUN et LAUNCH THE CHALLENGE du panneau Challenges &amp; Speedruns.</summary>
    public const string Speedrun = "speedrun";

    /// <summary>La presence dans une room MULTIPLAYER (le joueur local ; les spectateurs ne voient rien du Deck).</summary>
    public const string Multiplayer = "multiplayer";

    /// <summary>Prevu, non branche (H-D18).</summary>
    public const string Adventure = "adventure";

    public static readonly IReadOnlyList<string> Tous = [WorldScoring, Speedrun, Multiplayer, Adventure];

    public static bool Connu(string? mode) => mode is not null && Tous.Contains(mode);
}

/// <summary>
/// LE CHOIX DU DECK, RETENU PAR JOUEUR, PAR JEU ET PAR MODE (H-D16, H-D20), dans <c>state/deck/choix.json</c>. Le choix
/// se fait en jeu (geste lu par l'API ou raccourci du Deck), jamais dans le panneau d'ES. Il accepte n'importe quel Deck
/// installe (officiel ou d'un joueur, H-D19), ou « off ». Ecrit seulement quand le joueur choisit : jamais pendant une
/// partie sans geste de sa part.
///
/// Resolution au lancement : le choix de ce joueur pour ce jeu dans ce mode ; sinon son dernier choix pour ce jeu dans un
/// autre mode ; sinon le Deck officiel par defaut du jeu ; sinon rien. Un choix qui nomme un Deck desinstalle est ignore.
/// </summary>
public sealed class DeckChoix
{
    public const string Off = "off";
    public const string Schema = "nelfeplay.deck.choices/1";

    public sealed record Entree(string Player, string Game, string Mode, string Deck, DateTime AtUtc);

    /// <summary>Le Deck retenu pour un lancement : un identifiant, ou null (Off ou aucun Deck), et d'ou vient la decision.</summary>
    public sealed record Decision(string? DeckId, string Source);

    private sealed record Fichier(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("choices")] List<Entree> Choices);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _verrou = new();
    private readonly string? _fichier;
    private readonly List<Entree> _entrees = new();

    public DeckChoix(string? fichier)
    {
        _fichier = fichier;
        try
        {
            if (_fichier is not null && File.Exists(_fichier)
                && JsonSerializer.Deserialize<Fichier>(File.ReadAllText(_fichier), Json) is { Choices: { } lus })
            {
                _entrees.AddRange(lus.Where(e => !string.IsNullOrWhiteSpace(e.Player) && !string.IsNullOrWhiteSpace(e.Game) && DeckModes.Connu(e.Mode)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    public IReadOnlyList<Entree> Tous()
    {
        lock (_verrou) return _entrees.ToList();
    }

    /// <summary>Retient le choix (un Deck ou <see cref="Off"/>) pour ce joueur, ce jeu et ce mode.</summary>
    public void Poser(string joueur, string jeu, string mode, string deck)
    {
        lock (_verrou)
        {
            _entrees.RemoveAll(e => Meme(e, joueur, jeu, mode));
            _entrees.Add(new Entree(joueur, jeu, mode, deck, DateTime.UtcNow));
            Enregistrer();
        }
    }

    public Decision Resoudre(string joueur, string jeu, string mode, IReadOnlyList<DeckCatalogue.Entree> installes)
    {
        lock (_verrou) return Resoudre(_entrees, joueur, jeu, mode, installes);
    }

    /// <summary>La resolution, pure (voir la classe).</summary>
    public static Decision Resoudre(IEnumerable<Entree> entrees, string joueur, string jeu, string mode, IReadOnlyList<DeckCatalogue.Entree> installes)
    {
        if (installes.Count == 0) return new Decision(null, "none");
        var duJoueur = entrees.Where(e => Egal(e.Player, joueur) && Egal(e.Game, jeu) && Valable(e.Deck, installes)).ToList();
        if (duJoueur.FirstOrDefault(e => e.Mode == mode) is { } exact) return new Decision(Id(exact.Deck), "choice");
        if (duJoueur.OrderByDescending(e => e.AtUtc).FirstOrDefault() is { } autreMode) return new Decision(Id(autreMode.Deck), "choice-other-mode");
        // ACTIF PAR DEFAUT quand le jeu a un Deck officiel : celui marque « deck.default », sinon le premier officiel.
        // Le Deck d'un joueur n'est jamais pris d'office.
        if ((installes.FirstOrDefault(e => e.Default) ?? installes.FirstOrDefault(e => e.Official)) is { } officiel)
            return new Decision(officiel.Id, "official-default");
        return new Decision(null, "none");

        static string? Id(string deck) => Egal(deck, Off) ? null : deck;
    }

    /// <summary>
    /// Le choix suivant dans l'ordre du choix en jeu : chaque Deck installe pour le jeu, puis Off, puis le premier. Depuis
    /// un choix inconnu (ou aucun Deck en cours), le premier Deck. Pur.
    /// </summary>
    public static string Suivant(string? courant, IReadOnlyList<DeckCatalogue.Entree> installes)
    {
        var tour = installes.Select(e => e.Id).Append(Off).ToList();
        var ici = courant is null ? tour.Count - 1 : tour.FindIndex(x => Egal(x, courant));
        return ici < 0 ? tour[0] : tour[(ici + 1) % tour.Count];
    }

    private static bool Valable(string deck, IReadOnlyList<DeckCatalogue.Entree> installes)
        => Egal(deck, Off) || installes.Any(i => Egal(i.Id, deck));

    private static bool Meme(Entree e, string joueur, string jeu, string mode) => Egal(e.Player, joueur) && Egal(e.Game, jeu) && e.Mode == mode;

    private static bool Egal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Enregistrer()
    {
        if (_fichier is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fichier)!);
            var provisoire = _fichier + ".tmp";
            File.WriteAllText(provisoire, JsonSerializer.Serialize(new Fichier(Schema, _entrees), Json));
            File.Move(provisoire, _fichier, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
