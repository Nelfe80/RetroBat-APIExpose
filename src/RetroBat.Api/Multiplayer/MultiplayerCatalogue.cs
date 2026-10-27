using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Le catalogue NELFEPLAY MULTIPLAYER, tel que le Data Pack le livre
/// (<c>multiplayer/catalogue.json</c>, installe dans <c>resources/multiplayer/</c>).
///
/// C'est lui qui fait foi, pas le nombre de joueurs du referentiel : un jeu a plusieurs joueurs
/// n'est pas forcement jouable en netplay (TMNT et Gauntlet II divergeaient a la sonde du 21/10),
/// et le compte du referentiel est parfois faux (OutRunners y est note a un joueur).
///
/// L'identite suit la regle des jeux (11/10) : en arcade le NOM DU SET, que FBNeo controle au
/// chargement ; en console le CONTENU mesure (md5). Le CRC et la taille ne decident rien : ils
/// evitent seulement de hacher tout un dossier de ROMs.
/// </summary>
public sealed record MultiplayerCatalogue
{
    public const int SchemaVersionSupportee = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("revision")]
    public string? Revision { get; init; }

    /// <summary>Le coeur impose par systeme, et ses options (identiques sur toutes les bornes, ou propres a la place).</summary>
    [JsonPropertyName("cores")]
    public IReadOnlyDictionary<string, MultiplayerCore> Cores { get; init; } = new Dictionary<string, MultiplayerCore>();

    [JsonPropertyName("games")]
    public IReadOnlyList<MultiplayerGame> Games { get; init; } = [];

    /// <summary>
    /// Le coeur d'un jeu : le sien s'il en nomme un, sinon celui de son systeme ; ses options
    /// propres s'ajoutent a celles du systeme et l'emportent sur elles.
    /// </summary>
    public MultiplayerCore? CoreOf(MultiplayerGame jeu)
    {
        MultiplayerCore? coeur = jeu.Core is { Length: > 0 } propre
            ? new MultiplayerCore { Core = propre }
            : Cores.TryGetValue(jeu.System, out var duSysteme) ? duSysteme : null;
        if (coeur == null || jeu.Options is not { Count: > 0 } options)
        {
            return coeur;
        }

        var fusion = new Dictionary<string, string>(coeur.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (cle, valeur) in options)
        {
            fusion[cle] = valeur;
        }

        return coeur with { Options = fusion };
    }
}

public sealed record MultiplayerCore
{
    [JsonPropertyName("core")]
    public string Core { get; init; } = string.Empty;

    /// <summary>Les memes sur toutes les bornes : le netplay l'exige. <c>{players}</c> = joueurs de la gameroom.</summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Propres a chaque borne (ce qu'elle affiche, ce qu'elle fait entendre). <c>{seat}</c> = sa place.</summary>
    [JsonPropertyName("seat_options")]
    public IReadOnlyDictionary<string, string>? SeatOptions { get; init; }
}

public sealed record MultiplayerGame
{
    /// <summary>Cle stable du jeu dans MULTIPLAYER (<c>arcade/mslug3</c>, <c>gba/doom-ii</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Systeme canonique : <c>arcade</c> pour tous les dossiers d'arcade, sinon le dossier roms.</summary>
    [JsonPropertyName("system")]
    public string System { get; init; } = string.Empty;

    /// <summary>Arcade : le set, tel que FBNeo le nomme.</summary>
    [JsonPropertyName("set")]
    public string? Set { get; init; }

    /// <summary>
    /// Console : les contenus acceptes, le prefere d'abord. Une gameroom ne reunit que des joueurs
    /// au meme contenu.
    /// </summary>
    [JsonPropertyName("content")]
    public MultiplayerContent? Content { get; init; }

    /// <summary>Le nom du jeu, jamais traduit.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; init; } = string.Empty;

    /// <summary>Joueurs en meme temps dans la partie.</summary>
    [JsonPropertyName("players")]
    public int Players { get; init; }

    /// <summary>Joueurs d'un relais (KOF '98 en 3 contre 3), quand le jeu en propose un.</summary>
    [JsonPropertyName("relay_players")]
    public int? RelayPlayers { get; init; }

    /// <summary><c>classique</c> (netplay seul, menus du jeu, sans classement) ou <c>nelfeplay</c>.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = MultiplayerLevels.Classique;

    /// <summary>
    /// Le type versus du jeu (combat, puzzle, sport, course, arene, fps, strategie) : sa cote par type. Null pour la coop,
    /// classee par le score en 1CC MULTI (decisions du 23/10).
    /// </summary>
    [JsonPropertyName("elo_type")]
    public string? EloType { get; init; }

    /// <summary>Un coeur propre a ce jeu, a la place de celui de son systeme.</summary>
    [JsonPropertyName("core")]
    public string? Core { get; init; }

    /// <summary>Options du coeur propres a ce jeu (un reglage DIP d'arcade), les memes sur toutes les bornes.</summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>
    /// LES REGLAGES QUE LE LANCEUR ECRIT LUI-MEME (Micro Mages, 25/10) : pour une option qu'il connait (une « feature »
    /// d'ES), emulatorLauncher ecrit SA valeur dans les options du coeur au lancement, celle d'ES ou son defaut, par-dessus
    /// celle que l'API vient de poser (l'adaptateur 4 joueurs de Nestopia revenait a « auto » : plus de joueurs 3 et 4).
    /// Ces paires passent donc aussi sur sa ligne, qu'il lit en dernier. Jamais une cle en « p » (une manette fantome).
    /// </summary>
    [JsonPropertyName("launcher_options")]
    public IReadOnlyDictionary<string, string>? LauncherOptions { get; init; }

    /// <summary>
    /// Les ecrans que le coeur dessine cote a cote : une borne d'origine a un moniteur par joueur
    /// (World Rally 2, OutRunners). Chaque borne n'en affiche qu'un, celui de sa place.
    /// </summary>
    [JsonPropertyName("screens")]
    public int Screens { get; init; } = 1;

    /// <summary>L'ecran de chaque place, 1 etant le plus a gauche (Hard Dunk : trois joueurs par ecran).</summary>
    [JsonPropertyName("seat_screens")]
    public IReadOnlyList<int>? SeatScreens { get; init; }

    /// <summary>
    /// DEUX MANETTES PAR JOUEUR (Title Fight, 27/10 : un boxeur = un poing par manette, P1 sur les ports 1 et 3, P2 sur 2 et
    /// 4) : les ports du coeur que tient chaque place, la place 1 en premier. La borne les demande tous au netplay
    /// (<c>netplay_request_device_pN</c>) ; RetroArch lit chacun sur sa manette locale suivante (joueur 1, puis 2), vu au banc
    /// le 27/10 (« has joined with input devices 2, 4 »). Sans table : le port de sa place.
    /// </summary>
    [JsonPropertyName("seat_ports")]
    public IReadOnlyList<IReadOnlyList<int>>? SeatPorts { get; init; }

    /// <summary>Les ports que tient cette place, dans l'ordre de ses manettes locales.</summary>
    public IReadOnlyList<int> PortsDeLaPlace(int place)
        => SeatPorts is { Count: > 0 } ports && place >= 1 && place <= ports.Count && ports[place - 1].Count > 0 ? ports[place - 1] : [place];

    /// <summary>
    /// Le format de l'image du jeu, « largeur:hauteur » (« 3:4 » pour un jeu vertical comme 19XX, « 8:3 » pour deux ecrans
    /// cote a cote) ; absent, 4:3. Les panneaux en jeu calent le trou du decor sur ce format (CDC Ecrans v1.0, 26/10).
    /// </summary>
    [JsonPropertyName("aspect")]
    public string? Aspect { get; init; }

    /// <summary>Les reglages des ecrans MULTIPLAYER propres a ce jeu : police imposee, fanart ecarte, sens du portrait.</summary>
    [JsonPropertyName("screen")]
    public MultiplayerEcranDuJeu? Ecran { get; init; }

    /// <summary>Le format de l'image en largeur sur hauteur : celui du catalogue, sinon 4:3 (un format illisible aussi).</summary>
    [JsonIgnore]
    public double Format
    {
        get
        {
            var morceaux = (Aspect ?? string.Empty).Split(':');
            return morceaux.Length == 2
                && double.TryParse(morceaux[0], global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var largeur)
                && double.TryParse(morceaux[1], global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var hauteur)
                && largeur > 0 && hauteur > 0
                ? largeur / hauteur
                : 4.0 / 3.0;
        }
    }

    /// <summary>
    /// L'ECRAN PARTAGE (26/10, demande du user : « il ne faut afficher que la partie splitee qui concerne le joueur ») : un
    /// jeu qui dessine la vue de chaque joueur dans une seule image (Super Mario Kart, GoldenEye 007, Herzog Zwei, Metal
    /// Warriors). Chaque borne ne montre que le rectangle de sa place, a son format. Une vue peut ne valoir que pour un
    /// nombre de joueurs (GoldenEye : deux bandes a deux, quatre quarts au-dela).
    /// </summary>
    [JsonPropertyName("views")]
    public IReadOnlyList<MultiplayerView>? Views { get; init; }

    /// <summary>Le format de l'image entiere sur l'ecran d'origine (4/3 pour une console de salon) : celui d'une vue s'en deduit.</summary>
    [JsonPropertyName("frame_aspect")]
    public double FrameAspect { get; init; } = 4.0 / 3.0;

    /// <summary>
    /// La version d'API a partir de laquelle le jeu entre dans la collection (« 1.9.97 ») : un jeu qui demande ce qu'une
    /// API plus ancienne ne sait pas faire (une vue par place) n'y entre pas chez elle.
    /// </summary>
    [JsonPropertyName("min_api")]
    public string? MinApi { get; init; }

    /// <summary>La vue de cette place pour ce nombre de joueurs, ou null : la borne montre alors toute l'image.</summary>
    public MultiplayerView? VueDeLaPlace(int place, int joueurs)
        => Views?.FirstOrDefault(vue => vue.Seat == place && vue.Players == joueurs)
           ?? Views?.FirstOrDefault(vue => vue.Seat == place && vue.Players is null);

    /// <summary>Le format d'une vue : celui de l'image entiere, ramene a sa largeur et a sa hauteur.</summary>
    public double FormatDeLaVue(MultiplayerView vue)
        => (FrameAspect > 0 ? FrameAspect : 4.0 / 3.0) * vue.W / vue.H;

    /// <summary>Cette API est-elle assez recente pour ce jeu ?</summary>
    public bool AdmisPar(Version api)
        => MinApi is not { Length: > 0 } minimum
           || (Version.TryParse(minimum.Split('+')[0].Trim(), out var requise) && api >= requise);

    /// <summary>
    /// NIVEAU NELFEPLAY : le depart pret (V24-15). Un etat que l'hote charge avant que les autres se connectent ;
    /// ils le recoivent avec la connexion (verifie le 22/10 sur Street Fighter II' CE, deux RetroArch).
    /// </summary>
    [JsonPropertyName("start")]
    public MultiplayerStart? Start { get; init; }

    /// <summary>
    /// Les autres departs, un par nombre de joueurs (mGBA Splitscreen : un etat a deux consoles ne se charge pas a
    /// trois). <see cref="Start"/> reste obligatoire, une API plus ancienne ne lit que lui.
    /// </summary>
    [JsonPropertyName("starts")]
    public IReadOnlyList<MultiplayerStart>? Starts { get; init; }

    /// <summary>NIVEAU NELFEPLAY : ce que chaque joueur choisit (le personnage par ▲▼), ecrit chez l'hote apres l'etat.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<MultiplayerChoice>? Choices { get; init; }

    /// <summary>
    /// NIVEAU NELFEPLAY : le pseudo de chaque joueur ecrit dans le jeu au depart pret, avec les choix (F-Zero, 24/10 :
    /// les noms des coureurs a l'ecran des bolides et en course).
    /// </summary>
    [JsonPropertyName("names")]
    public MultiplayerNames? Names { get; init; }

    /// <summary>
    /// NIVEAU NELFEPLAY : LE ROULEMENT (demande du user du 24/10 : la coupe de Mario Kart, au rythme de Splatoon) : une
    /// valeur du jeu qui change avec l'heure, la meme sur toutes les bornes, ecrite par l'hote au depart pret. Personne
    /// ne la choisit.
    /// </summary>
    [JsonPropertyName("rotation")]
    public MultiplayerRotation? Rotation { get; init; }

    /// <summary>
    /// NIVEAU NELFEPLAY : la fin d'une rencontre (V24-16), lue chez l'hote ; la plateforme ferme alors le jeu, sinon le
    /// gagnant d'un SF2 continuerait seul contre l'ordinateur.
    /// </summary>
    [JsonPropertyName("end")]
    public MultiplayerEnd? End { get; init; }

    /// <summary>
    /// LES AUTRES TYPES DE FIN (decision du user du 23/10) : <c>score_final</c> d'abord (les sports). Une cle a part : une
    /// API d'avant, qui ne connait que <see cref="End"/>, l'ignore et garde le jeu en Classique.
    /// </summary>
    [JsonPropertyName("match_end")]
    public MultiplayerMatchEnd? MatchEnd { get; init; }

    /// <summary>La fin de rencontre se lit-elle (manches, ou un type de <see cref="MatchEnd"/> connu) ?</summary>
    [JsonIgnore]
    public bool FinLue => End is not null || MatchEnd is { EstConnu: true };

    /// <summary>Le delai avant de fermer le jeu, la rencontre finie, le temps de voir le resultat.</summary>
    [JsonIgnore]
    public int DelaiDeFermeture => End?.CloseAfterS ?? MatchEnd?.CloseAfterS ?? 5;

    /// <summary>NIVEAU NELFEPLAY : ce que l'hote mesure pendant un duel pour l'ecran de resultat (00quater.3, ecran 10).</summary>
    [JsonPropertyName("stats")]
    public MultiplayerStats? Stats { get; init; }

    [JsonIgnore]
    public bool EstArcade => string.Equals(System, "arcade", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool EstNelfePlay => Level == MultiplayerLevels.NelfePlay && Start is not null;

    /// <summary>
    /// Le depart pret d'une partie a <paramref name="joueurs"/> sur <paramref name="contenu"/> (le md5 de la gameroom) :
    /// celui de <see cref="Starts"/> a ce nombre, sinon <see cref="Start"/> quand il vaut pour tous les nombres ou pour
    /// celui-la. Un etat fait sur un contenu ne se charge jamais sur un autre. Null : la partie se joue en classique.
    /// </summary>
    public MultiplayerStart? DepartPour(int joueurs, string? contenu = null)
        => Starts?.FirstOrDefault(depart => depart.Players == joueurs && depart.VautPour(contenu))
           ?? (Start is { } commun && (commun.Players is null || commun.Players == joueurs) && commun.VautPour(contenu) ? commun : null);

    /// <summary>Un depart pret existe-t-il pour ce contenu, a un nombre de joueurs au moins ? Sinon, pas de choix a proposer.</summary>
    public bool ADesDepartsPour(string? contenu)
        => (Starts ?? []).Prepend(Start).Any(depart => depart is not null && depart.VautPour(contenu));

    /// <summary>
    /// Classe (23/10) : un jeu versus dont on lit la fin, quel que soit son niveau. Le depart pret du niveau NelfePlay
    /// reste un plus, pas une condition de la cote.
    /// </summary>
    [JsonIgnore]
    public bool EstClasse => FinLue && EloType is { Length: > 0 } type && MultiplayerLevels.TypesVersus.Contains(type);

    /// <summary>L'ecran a afficher pour cette place (1 a <see cref="Screens"/>).</summary>
    public int EcranDeLaPlace(int place)
    {
        if (Screens <= 1)
        {
            return 1;
        }

        if (SeatScreens is { Count: > 0 } ecrans && place >= 1 && place <= ecrans.Count)
        {
            return ecrans[place - 1];
        }

        // Sans table : les places se repartissent dans l'ordre, a parts egales.
        var parEcran = Math.Max(1, (int)Math.Ceiling(Math.Max(Players, 1) / (double)Screens));
        return Math.Clamp((Math.Max(place, 1) - 1) / parEcran + 1, 1, Screens);
    }
}

/// <summary>
/// Le rectangle de l'image que voit une place, en fractions de l'image entiere (0 a 1) : x et y son coin haut gauche, w et h
/// sa largeur et sa hauteur. Super Mario Kart a deux : place 1 { x 0, y 0, w 1, h 0,5 }, place 2 { x 0, y 0,5, w 1, h 0,5 }.
/// </summary>
public sealed record MultiplayerView
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    /// <summary>Le nombre de joueurs pour lequel cette vue vaut, ou null pour tous.</summary>
    [JsonPropertyName("players")]
    public int? Players { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("w")]
    public double W { get; init; } = 1;

    [JsonPropertyName("h")]
    public double H { get; init; } = 1;
}

public sealed record MultiplayerContent
{
    [JsonPropertyName("md5")]
    public IReadOnlyList<string> Md5 { get; init; } = [];

    /// <summary>Prefiltre : le CRC d'une entree de zip se lit sans decompresser.</summary>
    [JsonPropertyName("crc")]
    public IReadOnlyList<string> Crc { get; init; } = [];

    /// <summary>Prefiltre d'un fichier nu.</summary>
    [JsonPropertyName("size")]
    public IReadOnlyList<long> Size { get; init; } = [];
}

/// <summary>L'etat d'un depart pret, livre par le Data Pack sous <c>resources/multiplayer/</c>.</summary>
public sealed record MultiplayerStart
{
    /// <summary>Chemin relatif au dossier du catalogue : <c>states/arcade/sf2ce/selection-2p-fbneo.state</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>L'empreinte de l'etat : un fichier retouche ne se charge pas.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Le nombre de joueurs de l'etat, ou null quand il vaut pour tous (un etat d'arcade).</summary>
    [JsonPropertyName("players")]
    public int? Players { get; init; }

    /// <summary>Console : le md5 du contenu sur lequel l'etat a ete fait (un etat USA casserait la version Europe).</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    public bool VautPour(string? contenu)
        => Content is null || string.Equals(Content, contenu, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Un choix du joueur parmi des valeurs permises (S5 du socle commun : jamais une valeur libre), et l'endroit ou
/// l'ecrire pour chaque place. L'adresse est celle que RetroArch lit et ecrit (<c>READ_CORE_RAM</c>) : le decalage
/// dans la memoire systeme du coeur, deja inverse sur les cartes 68000.
/// </summary>
public sealed record MultiplayerChoice
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("values")]
    public IReadOnlyList<MultiplayerChoiceValue> Values { get; init; } = [];

    [JsonPropertyName("default")]
    public int Default { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    /// <summary>
    /// TOUTES LES CONSOLES (F-Zero, 24/10) : avec <c>core_map</c>, la valeur d'une place s'ecrit dans chaque console de la
    /// partie (adresse + 0x10000000 par console). F-Zero garde le bolide de chaque joueur dans toutes les consoles et ne le
    /// transmet pas par la liaison : ecrit dans une seule, les autres le verraient sur Hot Violet. Mario Kart, lui, lit
    /// le choix dans la console du joueur seulement (sans cette cle).
    /// </summary>
    [JsonPropertyName("all_consoles")]
    public bool AllConsoles { get; init; }

    /// <summary>
    /// L'adresse d'une place pour une partie a <paramref name="joueurs"/> : celle de ce nombre (<c>players</c> de la place),
    /// sinon celle qui vaut pour tous. F-Zero range ses joueurs selon leur nombre (a deux, le joueur 2 est en 3e ligne).
    /// </summary>
    public long? AdressePour(int place, int joueurs)
        => (Seats.FirstOrDefault(s => s.Seat == place && s.Players == joueurs)
            ?? Seats.FirstOrDefault(s => s.Seat == place && s.Players is null))?.Decalage;

    /// <summary>
    /// DEUX JOUEURS, DEUX VALEURS (Metal Slug 3, 23/10) : le jeu ne laisse pas deux joueurs sur le meme soldat, et deux
    /// fois la meme valeur ecrite le bloque (« PLEASE WAIT »). Le panneau saute les valeurs prises, et l'hote donne au
    /// joueur arrive le second la suivante libre.
    /// </summary>
    [JsonPropertyName("distinct")]
    public bool Distinct { get; init; }

    /// <summary>
    /// TENU TOUTE LA PARTIE (27/10, Mario Kart : les joueurs changeaient de pilote par rapport au panneau) : avec
    /// <c>core_map</c>, la valeur de chaque place entre aussi dans la table de verrous du coeur mGBA Splitscreen de NelfePlay
    /// (<see cref="MultiplayerRotation.TableDesVerrous"/>), dans chaque console avec <see cref="AllConsoles"/> : le curseur ne
    /// bouge plus. Apres le roulement, et seulement si tout tient dans la table ; sinon le choix n'est que preselectionne.
    /// </summary>
    [JsonPropertyName("lock")]
    public bool Lock { get; init; }

    /// <summary>
    /// LES ADRESSES DE LA CARTE DU COEUR (GBA, 23/10) : avec <c>core_map</c>, chaque <c>core_ram</c> est une adresse de la
    /// carte memoire que le coeur declare (READ_CORE_MEMORY / WRITE_CORE_MEMORY), pas un decalage dans sa memoire systeme
    /// (READ_CORE_RAM). mGBA Splitscreen y range chaque console : la console 1 aux adresses de la GBA (EWRAM 0x02000000),
    /// la console N a +0x10000000 par console, que READ_CORE_RAM ne voit pas.
    /// </summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    public const string CarteDuCoeur = "core_map";

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, CarteDuCoeur, StringComparison.Ordinal);

    /// <summary>
    /// La valeur de chaque place, dans l'ordre des places : un choix distinct deja pris par une place d'avant passe a la
    /// suivante libre. Le meme calcul chez l'hote (ce qu'il ecrit) et dans le panneau (ce qu'il montre).
    /// </summary>
    public IReadOnlyDictionary<int, int> Departager(IEnumerable<(int Place, int Valeur)> voulues)
    {
        var prises = new HashSet<int>();
        var retenues = new Dictionary<int, int>();
        foreach (var (place, voulue) in voulues.OrderBy(v => v.Place))
        {
            var valeur = Values.Any(v => v.Value == voulue) ? voulue : Default;
            if (Distinct && prises.Contains(valeur))
            {
                valeur = VoisineLibre(valeur, 1, prises);
            }

            prises.Add(valeur);
            retenues[place] = valeur;
        }

        return retenues;
    }

    /// <summary>La voisine qu'aucun autre joueur n'a prise ; la voisine tout court si toutes le sont.</summary>
    public int VoisineLibre(int valeur, int pas, IReadOnlySet<int> prises)
    {
        var candidate = valeur;
        for (var essai = 0; essai < Values.Count; essai++)
        {
            candidate = Voisine(candidate, pas);
            if (!prises.Contains(candidate))
            {
                return candidate;
            }
        }

        return Voisine(valeur, pas);
    }

    /// <summary>La valeur suivante (ou precedente) dans la liste, en boucle.</summary>
    public int Voisine(int valeur, int pas)
    {
        if (Values.Count == 0)
        {
            return valeur;
        }

        var index = Values.ToList().FindIndex(v => v.Value == valeur);
        index = index < 0 ? 0 : ((index + pas) % Values.Count + Values.Count) % Values.Count;
        return Values[index].Value;
    }

    public string NomDe(int valeur) => Values.FirstOrDefault(v => v.Value == valeur)?.Name ?? string.Empty;
}

/// <summary>
/// La fin d'une rencontre : la place dont le compteur atteint <see cref="FirstTo"/>, seule en tete, gagne (les manches de
/// SF2CE, mesurees le 22/10 sous FBNeo : P1 0xFF864E, P2 0xFF894E, 2 pour gagner), et seulement pendant le duel quand
/// une garde le dit (<see cref="Duel"/>). Le jeu se ferme <see cref="CloseAfterS"/> secondes plus tard, le temps de voir
/// le vainqueur.
/// </summary>
public sealed record MultiplayerEnd
{
    [JsonPropertyName("first_to")]
    public int FirstTo { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    [JsonPropertyName("close_after_s")]
    public int CloseAfterS { get; init; } = 5;

    /// <summary>
    /// LA GARDE DE DUEL (vague 1 du 23/10) : l'octet qui dit que les deux joueurs sont dans le match, et sa valeur. Sans
    /// elle, une manche gagnee par l'ordinateur contre le joueur 1, avant l'arrivee du joueur 2, comptait dans la case du
    /// joueur 2 (mesure au banc : Windjammers, Magical Drop III, Super Puzzle Fighter II Turbo). Absente : les compteurs
    /// comptent toujours, comme sur SF2CE, ou le depart pret met les deux joueurs en duel.
    /// </summary>
    [JsonPropertyName("duel")]
    public MultiplayerDuelGuard? Duel { get; init; }
}

/// <summary>
/// UNE FIN AU SCORE (<c>score_final</c>, decision du user du 23/10) : quand l'octet <see cref="Over"/> prend sa valeur (fin
/// du match, chronometre a zero), la place au score le plus haut gagne ; a egalite, match nul. Un signal de fin deja la au
/// depart (le match d'avant) ne compte pas : il faut l'avoir vu retomber. Garde de duel facultative, comme <see cref="MultiplayerEnd.Duel"/>.
/// </summary>
public sealed record MultiplayerMatchEnd
{
    public const string ScoreFinal = "score_final";

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("close_after_s")]
    public int CloseAfterS { get; init; } = 5;

    /// <summary>Le signal de fin de match : l'octet et la valeur qu'il prend quand le match est fini.</summary>
    [JsonPropertyName("over")]
    public MultiplayerDuelGuard? Over { get; init; }

    /// <summary>Le score de chaque place (decalage de RetroArch, premier octet lu).</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    [JsonPropertyName("score")]
    public MultiplayerScoreFormat Score { get; init; } = new();

    [JsonPropertyName("duel")]
    public MultiplayerDuelGuard? Duel { get; init; }

    [JsonIgnore]
    public bool EstConnu => Type == ScoreFinal;
}

/// <summary>Comment se lit un score : son nombre d'octets, BCD ou binaire, et l'ordre des octets lus (<c>le</c> ou <c>be</c>).</summary>
public sealed record MultiplayerScoreFormat
{
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    [JsonPropertyName("bcd")]
    public bool Bcd { get; init; }

    [JsonPropertyName("order")]
    public string Order { get; init; } = "le";

    /// <summary>La valeur des octets lus dans l'ordre de la memoire, ou null si un chiffre BCD est impossible.</summary>
    public long? Valeur(IReadOnlyList<int> octets)
    {
        var ordonnes = Order == "be" ? octets : octets.Reverse().ToList();
        long valeur = 0;
        foreach (var o in ordonnes)
        {
            if (Bcd)
            {
                if ((o >> 4) > 9 || (o & 0x0F) > 9)
                {
                    return null;
                }

                valeur = valeur * 100 + (o >> 4) * 10 + (o & 0x0F);
            }
            else
            {
                valeur = (valeur << 8) | (uint)o;
            }
        }

        return valeur;
    }
}

/// <summary>L'octet de la garde de duel (decalage de RetroArch) et la valeur qu'il a pendant un duel.</summary>
public sealed record MultiplayerDuelGuard
{
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonIgnore]
    public long? Decalage => CoreRam.Trim() is { Length: > 2 } texte && texte.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(texte[2..], global::System.Globalization.NumberStyles.HexNumber, null, out var valeur) && valeur >= 0
            ? valeur
            : null;
}

/// <summary>
/// Les statistiques d'un duel. SF2CE (00quater.3) : un coup reussi est une baisse de la vie adverse, un K.O. une vie a
/// 0xFFFF, un perfect une manche gagnee avec la vie pleine (144).
/// </summary>
public sealed record MultiplayerStats
{
    [JsonPropertyName("life")]
    public MultiplayerStatLife? Life { get; init; }

    /// <summary>
    /// LE PROFIL DE STATISTIQUES du jeu (lot 2 du CDC des statistiques), relatif au dossier du catalogue, sous
    /// <c>stats/</c> (<c>stats/arcade/sf2ce.json</c>). Une API plus ancienne l'ignore et garde <see cref="Life"/>.
    /// </summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }
}

public sealed record MultiplayerStatLife
{
    /// <summary>L'adresse de la vie de chaque place (decalage de RetroArch, premier octet lu).</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    /// <summary>1 ou 2 octets, lus dans l'ordre de la memoire du coeur (petit-boutiste).</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 2;

    [JsonPropertyName("full")]
    public int Full { get; init; }

    /// <summary>La valeur du K.O. (SF2CE : 0xFFFF).</summary>
    [JsonPropertyName("ko")]
    public int Ko { get; init; } = 0xFFFF;
}

public sealed record MultiplayerChoiceValue
{
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary>Le nom a l'ecran (un nom de personnage, jamais traduit).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// L'IMAGE DU CHOIX (personnage, vehicule, equipe), livree par le Data Pack (decision du user du 23/10) : un PNG
    /// relatif au dossier du catalogue, sous <c>images/</c> (<c>images/arcade/sf2ce/character/ryu.png</c>). Elle passe
    /// avant le badge : une borne qui ne joint pas RetroAchievements a quand meme ses portraits.
    /// </summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>
    /// Le portrait de secours : un badge public de RetroAchievements (SF2CE : les succes « finir le jeu avec »), que la
    /// borne telecharge dans son cache quand l'image du Data Pack manque (API 1.9.88 et avant : la seule source).
    /// </summary>
    [JsonPropertyName("ra_badge")]
    public string? RaBadge { get; init; }

    /// <summary>
    /// Le chemin local de l'image sous <paramref name="dossierDuCatalogue"/>, ou null pour un chemin qui sortirait de
    /// <c>images/</c> ou qui n'est pas un PNG. Une image mal nommee se passe de portrait, elle ne refuse pas le catalogue.
    /// </summary>
    public string? CheminDeLImage(string dossierDuCatalogue)
        => Image is { Length: > 0 } image
            && image.StartsWith("images/", StringComparison.Ordinal)
            && image.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && RetroBat.Api.Infrastructure.DataPackPaths.CheminRelatifSur(image)
            ? global::System.IO.Path.Combine(dossierDuCatalogue, image.Replace('/', global::System.IO.Path.DirectorySeparatorChar))
            : null;
}

/// <summary>
/// LE PSEUDO DANS LE JEU (F-Zero, 24/10) : une chaine de longueur fixe, chaque caractere un octet ASCII (majuscules du
/// jeu), completee par <see cref="Pad"/>. Un caractere que le jeu n'a pas devient <see cref="Replacement"/>.
/// </summary>
public sealed record MultiplayerNames
{
    [JsonPropertyName("length")]
    public int Length { get; init; }

    /// <summary>Les caracteres que le jeu sait afficher (son clavier) ; les autres deviennent le remplacant.</summary>
    [JsonPropertyName("allowed")]
    public string Allowed { get; init; } = string.Empty;

    [JsonPropertyName("replacement")]
    public string Replacement { get; init; } = "-";

    [JsonPropertyName("pad")]
    public string Pad { get; init; } = " ";

    /// <summary>Comme pour un choix : <c>core_map</c>, la carte du coeur (READ/WRITE_CORE_MEMORY).</summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerNameSeat> Seats { get; init; } = [];

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.OrdinalIgnoreCase);

    /// <summary>Le pseudo tel que le jeu l'ecrit : majuscules, caracteres permis, coupe ou complete a la longueur.</summary>
    public byte[] Coder(string pseudo)
    {
        var remplacant = Replacement.Length > 0 ? Replacement[0] : '-';
        var bourrage = Pad.Length > 0 ? Pad[0] : ' ';
        // Les accents tombent (REMI pour Rémi) avant que les caracteres absents du jeu deviennent le remplacant.
        var texte = new string((pseudo ?? string.Empty).Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => Allowed.Contains(c, StringComparison.Ordinal) ? c : remplacant).Take(Length).ToArray());
        return texte.PadRight(Length, bourrage).Select(c => (byte) c).ToArray();
    }
}

/// <summary>
/// Une adresse du pseudo d'une place. <c>consoles</c> : <c>all</c> = dans chaque console de la partie (la table des
/// joueurs lies de F-Zero), <c>own</c> = dans la console de cette place seulement (son propre nom).
/// </summary>
public sealed record MultiplayerNameSeat
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("consoles")]
    public string Consoles { get; init; } = "own";

    [JsonIgnore]
    public bool DansChaqueConsole => string.Equals(Consoles, "all", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public long? Decalage => new MultiplayerChoiceSeat { CoreRam = CoreRam }.Decalage;
}

/// <summary>
/// LE ROULEMENT (24/10) : les valeurs du catalogue tour a tour, une par tranche de <see cref="PeriodH"/> heures comptees
/// depuis le 1er janvier 1970 a minuit UTC (2 h, comme les stages de Splatoon : le changement tombe aux heures paires
/// UTC). Elle ne depend que de l'heure, toutes les bornes ont donc la meme. Ecrite au depart pret a une seule adresse,
/// dans chaque console avec <see cref="AllConsoles"/>. Le jeu la presente ; Mario Kart laisse encore le joueur 1 changer
/// de coupe a l'ecran des circuits (preselection).
/// </summary>
/// <summary>
/// LES ECRANS D'UN JEU (CDC Ecrans v1.0, lot E4) : ce qui ne se deduit pas de son type. La police du titre de fin, un
/// fanart a ecarter (un fanart suggestif, Final Fight), le sens dans lequel regardent les personnages de ses images.
/// </summary>
public sealed record MultiplayerEcranDuJeu
{
    /// <summary>Une police des ecrans (« Bangers », « Titan One »...) a la place de celle du type.</summary>
    [JsonPropertyName("font")]
    public string? Police { get; init; }

    /// <summary>Faux : ni l'ecran de fin ni les panneaux en jeu ne prennent son fanart.</summary>
    [JsonPropertyName("fanart")]
    public bool Fanart { get; init; } = true;

    /// <summary>
    /// Ou regardent ses personnages : « right » (le defaut) ou « left ». Le panneau de droite retourne le portrait pour qu'il
    /// regarde le jeu ; regardant deja a gauche, il ne se retourne pas, et celui de gauche si.
    /// </summary>
    [JsonPropertyName("portrait_faces")]
    public string? Regard { get; init; }

    [JsonIgnore]
    public bool RegardeAGauche => string.Equals(Regard, "left", StringComparison.OrdinalIgnoreCase);
}

public sealed record MultiplayerRotation
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("period_h")]
    public int PeriodH { get; init; } = 2;

    [JsonPropertyName("values")]
    public IReadOnlyList<MultiplayerChoiceValue> Values { get; init; } = [];

    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    /// <summary>Ecrite une fois au depart (Mario Kart : la coupe que l'ecran des circuits reprend). Facultative.</summary>
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    /// <summary>
    /// TENUE toute la partie (demande du user du 24/10 : la coupe ne se choisit pas) : le coeur mGBA Splitscreen de
    /// NelfePlay la repose avant chaque pas d'emulation, depuis sa table de verrous (<see cref="TableDesVerrous"/>),
    /// rangee dans l'etat : l'invite du netplay la recoit en se connectant. Facultative.
    /// </summary>
    [JsonPropertyName("lock_core_ram")]
    public string LockCoreRam { get; init; } = string.Empty;

    [JsonPropertyName("all_consoles")]
    public bool AllConsoles { get; init; }

    /// <summary>La table de verrous du coeur dans sa carte : 16 entrees de 8 octets (adresse, valeur, actif).</summary>
    public const long TableDesVerrous = 0x7F000000;
    public const int VerrousMax = 16;

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.Ordinal);

    [JsonIgnore]
    public long? Decalage => CoreRam.Length == 0 ? null : new MultiplayerChoiceSeat { CoreRam = CoreRam }.Decalage;

    [JsonIgnore]
    public long? DecalageDuVerrou => LockCoreRam.Length == 0 ? null : new MultiplayerChoiceSeat { CoreRam = LockCoreRam }.Decalage;

    /// <summary>Le numero de la tranche ou tombe <paramref name="instant"/>.</summary>
    public long Tranche(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / (PeriodH * 3600L);

    /// <summary>La valeur de la tranche en cours, et l'heure ou la suivante prend sa place.</summary>
    public (MultiplayerChoiceValue Valeur, DateTimeOffset Jusqua)? A(DateTimeOffset instant)
    {
        if (Values.Count == 0 || PeriodH < 1)
        {
            return null;
        }

        var tranche = Tranche(instant);
        return (Values[(int) (tranche % Values.Count)], DateTimeOffset.FromUnixTimeSeconds((tranche + 1) * PeriodH * 3600L));
    }
}

public sealed record MultiplayerChoiceSeat
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    /// <summary>Le decalage en hexadecimal (<c>0x864E</c>).</summary>
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    /// <summary>Choix seulement : l'adresse ne vaut que pour une partie a ce nombre de joueurs (null : pour tous).</summary>
    [JsonPropertyName("players")]
    public int? Players { get; init; }

    [JsonIgnore]
    public long? Decalage => CoreRam.Trim() is { Length: > 2 } texte && texte.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(texte[2..], global::System.Globalization.NumberStyles.HexNumber, null, out var valeur) && valeur >= 0
            ? valeur
            : null;
}

/// <summary>Les resultats d'une rencontre, tels que la plateforme les recoit.</summary>
public static class MultiplayerResultats
{
    /// <summary>
    /// LE MATCH NUL (decision du user du 23/10) : la place gagnante 0. Un score egal au coup de sifflet, une arrivee ex
    /// aequo : 0,5 chacun pour la cote, une rencontre menee a son terme, jamais un abandon.
    /// </summary>
    public const int MatchNul = 0;
}

public static class MultiplayerLevels
{
    /// <summary>Les types versus de la v1 (champ <c>elo_type</c>) : la cote par type du joueur.</summary>
    public static readonly IReadOnlySet<string> TypesVersus = new HashSet<string>(StringComparer.Ordinal)
        { "combat", "puzzle", "sport", "course", "arene", "fps", "strategie" };

    public const string Classique = "classique";
    public const string NelfePlay = "nelfeplay";
}

/// <summary>Lit le catalogue livre par le Data Pack, et le refuse s'il n'est pas sain.</summary>
public sealed class MultiplayerCatalogueStore
{
    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<MultiplayerCatalogueStore>? _logger;
    private readonly object _verrou = new();
    private (DateTime Ecrit, long Taille, MultiplayerCatalogue? Catalogue, string? Erreur) _cache;

    public MultiplayerCatalogueStore(ILogger<MultiplayerCatalogueStore>? logger = null, string? path = null)
    {
        _logger = logger;
        Path = path ?? System.IO.Path.Combine(RetroBatPaths.PluginRoot, "resources", "multiplayer", "catalogue.json");
    }

    public string Path { get; }

    /// <summary>
    /// Le catalogue courant, relu seulement quand le fichier change. Null avec une raison quand
    /// il manque ou ne se lit pas : la collection garde alors ce qu'elle avait.
    /// </summary>
    public MultiplayerCatalogue? Lire(out string? erreur)
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                erreur = "catalogue absent (Data Pack pas encore synchronise)";
                return null;
            }

            lock (_verrou)
            {
                if (_cache.Ecrit == info.LastWriteTimeUtc && _cache.Taille == info.Length)
                {
                    erreur = _cache.Erreur;
                    return _cache.Catalogue;
                }
            }

            MultiplayerCatalogue? catalogue;
            using (var flux = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                catalogue = JsonSerializer.Deserialize<MultiplayerCatalogue>(flux, Lecture);
            }

            erreur = Valider(catalogue);
            var retenu = erreur == null ? Nettoyer(catalogue!) : null;
            if (erreur != null)
            {
                _logger?.LogWarning("Catalogue MULTIPLAYER refuse : {Erreur}", erreur);
            }

            lock (_verrou)
            {
                _cache = (info.LastWriteTimeUtc, info.Length, retenu, erreur);
            }

            return retenu;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            erreur = "catalogue illisible : " + ex.GetType().Name;
            return null;
        }
    }

    internal static string? Valider(MultiplayerCatalogue? catalogue)
    {
        if (catalogue == null)
        {
            return "catalogue vide";
        }

        if (catalogue.SchemaVersion != MultiplayerCatalogue.SchemaVersionSupportee)
        {
            return "schema " + catalogue.SchemaVersion + " inconnu (attendu " + MultiplayerCatalogue.SchemaVersionSupportee + ")";
        }

        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jeu in catalogue.Games)
        {
            if (string.IsNullOrWhiteSpace(jeu.Id) || !vus.Add(jeu.Id.Trim()))
            {
                return "identifiant vide ou en double : « " + jeu.Id + " »";
            }

            if (string.IsNullOrWhiteSpace(jeu.System))
            {
                return jeu.Id + " : systeme manquant";
            }

            if (jeu.EstArcade ? string.IsNullOrWhiteSpace(jeu.Set) : (jeu.Content?.Md5.Count ?? 0) == 0)
            {
                // Sans identite, le jeu entrerait sur la foi d'un nom de fichier.
                return jeu.Id + (jeu.EstArcade ? " : set manquant" : " : aucun md5 de contenu");
            }

            if (jeu.Players < 2)
            {
                return jeu.Id + " : moins de deux joueurs";
            }

            if (jeu.Level is not (MultiplayerLevels.Classique or MultiplayerLevels.NelfePlay))
            {
                return jeu.Id + " : niveau « " + jeu.Level + " » inconnu";
            }

            if (jeu.Screens is < 1 or > 4
                || (jeu.SeatScreens is { Count: > 0 } ecrans
                    && (ecrans.Count < jeu.Players || ecrans.Any(ecran => ecran < 1 || ecran > jeu.Screens))))
            {
                // Une place sans ecran, ou un ecran qui n'existe pas : une borne afficherait la vue d'un autre.
                return jeu.Id + " : ecrans par place incoherents";
            }

            if (jeu.SeatPorts is { Count: > 0 } ports
                && (ports.Count < jeu.Players || ports.Any(p => p.Count == 0 || p.Any(n => n is < 1 or > 8))
                    || ports.SelectMany(p => p).Distinct().Count() != ports.Sum(p => p.Count)))
            {
                // Une place sans port, un port hors du netplay ou tenu par deux places : deux bornes joueraient le meme poing.
                return jeu.Id + " : ports par place incoherents";
            }

            if (ValiderLesVues(jeu) is { } vues)
            {
                return jeu.Id + " : " + vues;
            }

            if (ValiderLeDepart(jeu) is { } depart)
            {
                return jeu.Id + " : " + depart;
            }
        }

        return null;
    }

    /// <summary>
    /// Chaque vue tient dans l'image et appartient a une place du jeu, et chaque place a la sienne : une place sans vue
    /// verrait l'image entiere, donc l'ecran des autres.
    /// </summary>
    internal static string? ValiderLesVues(MultiplayerGame jeu)
    {
        if (jeu.Views is not { Count: > 0 } vues)
        {
            return null;
        }

        if (jeu.Screens > 1)
        {
            return "ecrans multiples et vues par place a la fois";
        }

        if (jeu.FrameAspect is <= 0 or > 8)
        {
            return "format d'image invalide";
        }

        const double Marge = 1e-6;
        foreach (var vue in vues)
        {
            if (vue.Seat < 1 || vue.Seat > jeu.Players || vue.Players is < 1 || vue.Players > jeu.Players
                || vue.W <= 0 || vue.H <= 0 || vue.X < 0 || vue.Y < 0 || vue.X + vue.W > 1 + Marge || vue.Y + vue.H > 1 + Marge)
            {
                return "vue de la place " + vue.Seat + " hors de l'image ou hors des places";
            }
        }

        for (var joueurs = 2; joueurs <= jeu.Players; joueurs++)
        {
            for (var place = 1; place <= joueurs; place++)
            {
                if (jeu.VueDeLaPlace(place, joueurs) is null)
                {
                    return "la place " + place + " n'a pas de vue a " + joueurs + " joueurs";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Le niveau NelfePlay exige son depart pret (V24-15) ; chaque choix a des valeurs permises distinctes, un defaut
    /// parmi elles, et une adresse pour chaque place. Un choix sans adresse pour une place laisserait ce joueur au
    /// personnage de l'etat sans le lui dire.
    /// </summary>
    internal static string? ValiderLeDepart(MultiplayerGame jeu)
    {
        if (jeu.Level == MultiplayerLevels.NelfePlay)
        {
            if (jeu.Start is null || string.IsNullOrWhiteSpace(jeu.Start.State))
            {
                return "niveau NelfePlay sans depart pret";
            }

            foreach (var depart in (jeu.Starts ?? []).Prepend(jeu.Start))
            {
                if (string.IsNullOrWhiteSpace(depart.State) || depart.State.Contains("..", StringComparison.Ordinal)
                    || global::System.IO.Path.IsPathRooted(depart.State))
                {
                    return "chemin de l'etat hors du dossier MULTIPLAYER";
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(depart.Sha256.Trim(), "^[0-9a-fA-F]{64}$"))
                {
                    return "empreinte de l'etat manquante";
                }

                if (depart.Players is { } joueurs && (joueurs < 2 || joueurs > jeu.Players))
                {
                    return "etat pour " + joueurs + " joueurs hors de 2 a " + jeu.Players;
                }

                if (depart.Content is { } md5 && !(jeu.Content?.Md5 ?? []).Contains(md5, StringComparer.OrdinalIgnoreCase))
                {
                    return "etat fait sur un contenu hors du catalogue";
                }
            }

            if (jeu.Start.Players is not null || jeu.Starts is { Count: > 0 })
            {
                // Les etats propres a un nombre de joueurs couvrent chaque nombre, sans doublon : une gameroom a trois
                // aurait sinon le panneau des choix sans depart pour les appliquer.
                if ((jeu.Starts ?? []).Any(depart => depart.Players is null)
                    || (jeu.Starts ?? []).Prepend(jeu.Start).Where(depart => depart.Players is not null)
                        .GroupBy(depart => (depart.Players, Contenu: depart.Content?.ToLowerInvariant())).Any(groupe => groupe.Count() > 1))
                {
                    return "etats par nombre de joueurs sans nombre ou en double";
                }

                // Chaque contenu qui a des etats les a pour tous les nombres ; un contenu sans etat se joue en classique.
                foreach (var contenu in (jeu.Starts ?? []).Prepend(jeu.Start).Select(depart => depart.Content?.ToLowerInvariant()).Distinct())
                {
                    for (var joueurs = 2; joueurs <= jeu.Players; joueurs++)
                    {
                        if (jeu.DepartPour(joueurs, contenu) is null)
                        {
                            return "pas d'etat pour " + joueurs + " joueurs";
                        }
                    }
                }
            }
        }

        if (jeu.End is { } fin)
        {
            if (fin.FirstTo is < 1 or > 255 || fin.CloseAfterS is < 0 or > 60)
            {
                return "fin de rencontre incoherente";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (fin.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "fin de rencontre : pas d'adresse pour la place " + place;
                }
            }

            if (fin.Duel is { } garde && (garde.Decalage is null || garde.Value is < 0 or > 255))
            {
                return "fin de rencontre : garde de duel incoherente";
            }
        }

        if (jeu.MatchEnd is { } autre)
        {
            if (!autre.EstConnu)
            {
                return "fin de rencontre : type « " + autre.Type + " » inconnu";
            }

            if (autre.Over?.Decalage is null || autre.Over.Value is < 0 or > 255 || autre.CloseAfterS is < 0 or > 60)
            {
                return "fin de rencontre : signal de fin incoherent";
            }

            if (autre.Score.Bytes is < 1 or > 4 || autre.Score.Order is not ("le" or "be"))
            {
                return "fin de rencontre : format du score incoherent";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (autre.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "fin de rencontre : pas de score pour la place " + place;
                }
            }

            if (autre.Duel is { } garde && (garde.Decalage is null || garde.Value is < 0 or > 255))
            {
                return "fin de rencontre : garde de duel incoherente";
            }
        }

        if (jeu.Stats?.Life is { } vie)
        {
            if (vie.Bytes is < 1 or > 2 || vie.Full < 1)
            {
                return "statistiques incoherentes";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (vie.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "statistiques : pas d'adresse de vie pour la place " + place;
                }
            }
        }

        if (jeu.Names is { } noms)
        {
            if (noms.Length is < 1 or > 32 || noms.Allowed.Length == 0 || noms.Allowed.Any(c => c is < ' ' or > '~')
                || noms.Replacement.Length != 1 || noms.Pad.Length != 1 || noms.Replacement[0] > '~' || noms.Pad[0] > '~')
            {
                return "pseudos : longueur ou caracteres incoherents";
            }

            if (noms.Seats.Any(s => s.Decalage is null || s.Consoles is not ("all" or "own"))
                || (noms.Seats.Any(s => s.DansChaqueConsole) && !noms.ParLaCarteDuCoeur))
            {
                return "pseudos : adresse ou consoles incoherentes";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (noms.Seats.All(s => s.Seat != place))
                {
                    return "pseudos : pas d'adresse pour la place " + place;
                }
            }
        }

        if (jeu.Rotation is { } roulement)
        {
            // Une tranche qui ne divise pas la journee changerait a une heure differente chaque jour.
            if (string.IsNullOrWhiteSpace(roulement.Key) || roulement.Values.Count == 0 || roulement.PeriodH is < 1 or > 24
                || 24 % roulement.PeriodH != 0 || roulement.Values.Any(v => v.Value is < 0 or > 255)
                || roulement.Values.Select(v => v.Value).Distinct().Count() != roulement.Values.Count)
            {
                return "roulement : tranche ou valeurs incoherentes";
            }

            // Une adresse au moins ; le verrou n'existe que dans le coeur de NelfePlay (sa carte), et l'adresse est celle
            // de la console 1 (l'API ajoute celle de chaque console).
            if ((roulement.CoreRam.Length > 0 && roulement.Decalage is null)
                || (roulement.LockCoreRam.Length > 0 && roulement.DecalageDuVerrou is not (>= 0 and < 0x10000000))
                || (roulement.Decalage is null && roulement.DecalageDuVerrou is null)
                || ((roulement.AllConsoles || roulement.DecalageDuVerrou is not null) && !roulement.ParLaCarteDuCoeur)
                || (roulement.DecalageDuVerrou is not null && (roulement.AllConsoles ? jeu.Players : 1) > MultiplayerRotation.VerrousMax))
            {
                return "roulement : adresse incoherente";
            }
        }

        foreach (var choix in jeu.Choices ?? [])
        {
            if (string.IsNullOrWhiteSpace(choix.Key) || choix.Values.Count == 0
                || choix.Values.Select(v => v.Value).Distinct().Count() != choix.Values.Count
                || choix.Values.Any(v => v.Value is < 0 or > 255 || string.IsNullOrWhiteSpace(v.Name)))
            {
                return "choix « " + choix.Key + " » sans valeurs permises distinctes";
            }

            if (choix.Values.All(v => v.Value != choix.Default))
            {
                return "choix « " + choix.Key + " » : defaut hors des valeurs";
            }

            // Chaque nombre de joueurs a l'adresse de chacune de ses places (la sienne, ou celle qui vaut pour tous).
            for (var joueurs = 2; joueurs <= jeu.Players; joueurs++)
            {
                for (var place = 1; place <= joueurs; place++)
                {
                    if (choix.AdressePour(place, joueurs) is null)
                    {
                        return "choix « " + choix.Key + " » : pas d'adresse pour la place " + place
                            + (choix.Seats.Any(s => s.Players is not null) ? " a " + joueurs + " joueurs" : string.Empty);
                    }
                }
            }

            if (choix.AllConsoles && !choix.ParLaCarteDuCoeur)
            {
                return "choix « " + choix.Key + " » : toutes les consoles sans la carte du coeur";
            }

            // Seul le coeur mGBA Splitscreen de NelfePlay a une table de verrous, dans sa carte.
            if (choix.Lock && !choix.ParLaCarteDuCoeur)
            {
                return "choix « " + choix.Key + " » : tenu sans la carte du coeur";
            }
        }

        return null;
    }

    /// <summary>Les empreintes en minuscules, sans espace : la comparaison n'a plus a y penser.</summary>
    internal static MultiplayerCatalogue Nettoyer(MultiplayerCatalogue catalogue) => catalogue with
    {
        Games = catalogue.Games.Select(jeu => jeu with
        {
            Id = jeu.Id.Trim(),
            System = jeu.System.Trim().ToLowerInvariant(),
            Set = jeu.Set?.Trim().ToLowerInvariant(),
            Content = jeu.Content is null ? null : jeu.Content with
            {
                Md5 = jeu.Content.Md5.Select(Propre).Where(valeur => valeur.Length > 0).ToList(),
                Crc = jeu.Content.Crc.Select(Propre).Where(valeur => valeur.Length > 0).ToList(),
            },
        }).ToList(),
    };

    private static string Propre(string? valeur) => (valeur ?? string.Empty).Trim().ToLowerInvariant();
}
