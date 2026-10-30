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

    /// <summary>
    /// LE SPECTATEUR CACHE DE L'HOTE (28/10) : un coeur dont chaque borne n'affiche que sa console fait filmer le direct par
    /// un spectateur qui les montre toutes (<see cref="MultiplayerSpectateur"/>). Absent : l'hote filme sa propre image.
    /// </summary>
    [JsonPropertyName("spectator")]
    public MultiplayerSpectatorView? Spectator { get; init; }
}

/// <summary>Ce que montre le spectateur cache : ses options de coeur (la vue de toutes les consoles) et la grille de son image.</summary>
public sealed record MultiplayerSpectatorView
{
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary><c>auto</c> (cote a cote a deux, 2 x 2 a trois ou quatre) ou « colonnes x lignes ».</summary>
    [JsonPropertyName("grid")]
    public string? Grid { get; init; }
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
    /// PLUSIEURS PORTS PAR PLACE : les ports du coeur que tient chaque place, la place 1 en premier. La borne les demande
    /// tous au netplay (<c>netplay_request_device_pN</c>) ; RetroArch lit chacun sur sa manette locale suivante (joueur 1,
    /// puis 2), vu au banc le 27/10 (« has joined with input devices 2, 4 »). Sans table : le port de sa place. Title Fight
    /// n'en a plus besoin : son bras droit est le stick droit du port du joueur (<see cref="TwinStick"/>).
    /// </summary>
    [JsonPropertyName("seat_ports")]
    public IReadOnlyList<IReadOnlyList<int>>? SeatPorts { get; init; }

    /// <summary>Les ports que tient cette place, dans l'ordre de ses manettes locales.</summary>
    public IReadOnlyList<int> PortsDeLaPlace(int place)
        => SeatPorts is { Count: > 0 } ports && place >= 1 && place <= ports.Count && ports[place - 1].Count > 0 ? ports[place - 1] : [place];

    /// <summary>
    /// DEUX STICKS PAR JOUEUR (Title Fight, 27/10, decision du user) : FBNeo envoie le second stick du joueur sur le stick
    /// analogique droit de son port. La borne pose pour la partie un remappage de RetroArch
    /// (<see cref="MultiplayerLaunchPlan.Remappage"/>) : manettes analogiques (sinon le netplay ne transmet pas les sticks),
    /// boutons de facade en second stick pour une manette qui n'en a qu'un, et deux vrais sticks arcade sur un seul joueur.
    /// </summary>
    [JsonPropertyName("twin_stick")]
    public bool TwinStick { get; init; }

    /// <summary>
    /// Des cles de remappage de RetroArch propres a une place (Title Fight : le START du port 2 est celui de l'ecran 1, la
    /// place 2 ne l'a pas). Elles s'ajoutent a celles de <see cref="TwinStick"/>.
    /// </summary>
    [JsonPropertyName("seat_remap")]
    public IReadOnlyList<MultiplayerSeatRemap>? SeatRemap { get; init; }

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

    /// <summary>
    /// LE DEPART PAR PLACE, CHARGE PAR LE COEUR (29/10, Bomberman PSP) : un etat par place, que chaque borne pose la ou le
    /// coeur le cherche avant le lancement. Le coeur le charge a la fin de son demarrage, puis attend que tous les joueurs
    /// de l'etat soient connectes : RetroArch refuse tout chargement pendant une session netplay a paquets (Netpacket), et
    /// chaque PSP a sa propre memoire. Une cle a part : une API d'avant l'ignore, et le jeu reste en Classique.
    /// </summary>
    [JsonPropertyName("core_start")]
    public MultiplayerCoreStart? CoreStart { get; init; }

    /// <summary>
    /// Pendant une partie seulement : place du jeu vers place de la plateforme, quand le jeu numerote ses joueurs dans
    /// l'ordre du netplay et que l'hote n'a pas la place 1 (<see cref="MultiplayerOrdreNetplay"/>). Null : les memes.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<int, int>? PlacesDuNetplay { get; init; }

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

    /// <summary>LE CLASSEMENT A PLUSIEURS (CDC Classements v1.0 §3) : chaque humain classe, a 2 joueurs ou plus.</summary>
    [JsonPropertyName("ranking_end")]
    public MultiplayerRankingEnd? RankingEnd { get; init; }

    /// <summary>La fin de rencontre se lit-elle (manches, un type de <see cref="MatchEnd"/> connu, ou une regle de <see cref="RankingEnd"/>) ?</summary>
    [JsonIgnore]
    public bool FinLue => End is not null || MatchEnd is { EstConnu: true } || RankingEnd is { EstConnu: true };

    /// <summary>Le delai avant de fermer le jeu, la rencontre finie, le temps de voir le resultat.</summary>
    [JsonIgnore]
    public int DelaiDeFermeture => End?.CloseAfterS ?? MatchEnd?.CloseAfterS ?? RankingEnd?.CloseAfterS ?? 5;

    /// <summary>
    /// Une rencontre a ces places humaines est-elle classee ? Le jeu l'est, et sa fin a plusieurs la classe a ce nombre
    /// d'humains (deux au moins, face a face dans un jeu en equipes).
    /// </summary>
    public bool ClassePour(IReadOnlyCollection<int> humains)
        => EstClasse && (RankingEnd is not { } classement || classement.Classable(humains));

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

    /// <summary>
    /// LE DELAI DES CHOIX (CDC Classements v1.1 §1, 29/10) : les images entre le chargement de l'etat et l'ecriture des
    /// choix, a chaque ecriture (la premiere, chaque tour de la boucle du depart, le repli). Magical Drop III n'accepte
    /// l'ecriture qu'entre 50 et 300 ms apres le chargement : 1,5 s arrivait trop tard. Absent : le delai habituel.
    /// </summary>
    [JsonPropertyName("write_after_frames")]
    public int? WriteAfterFrames { get; init; }

    /// <summary>Le delai d'ecriture du catalogue, en temps (60 images par seconde), ou null.</summary>
    [JsonIgnore]
    public TimeSpan? DelaiDesChoix => WriteAfterFrames is { } images ? TimeSpan.FromMilliseconds(images * 1000.0 / 60) : null;

    /// <summary>
    /// LES VERROUS DU DEPART (CDC Classements v1.1 §10.3, 30/10) : des octets que le coeur mGBA Splitscreen de NelfePlay tient
    /// toute la partie, depuis sa table de verrous (<see cref="MultiplayerRotation.TableDesVerrous"/>), comme un choix tenu.
    /// Sonic Battle : a l'ecran des personnages, R ouvre le menu RULE, ou un joueur passerait en Time ou changerait le nombre
    /// de K.O. Seulement sous <c>start</c> : ils valent pour tous les departs du jeu.
    /// </summary>
    [JsonPropertyName("locks")]
    public IReadOnlyList<MultiplayerStartLock>? Locks { get; init; }
}

/// <summary>Un octet tenu toute la partie : son adresse dans la carte du coeur (console 1), sa valeur, dans chaque console ou non.</summary>
public sealed record MultiplayerStartLock
{
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonPropertyName("all_consoles")]
    public bool AllConsoles { get; init; }

    [JsonIgnore]
    public long? Decalage => CoreRam.Length == 0 ? null : new MultiplayerChoiceSeat { CoreRam = CoreRam }.Decalage;
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

    /// <summary>Les valeurs que voit cette place : les siennes quand elle en a, sinon celles du choix (place inconnue aussi).</summary>
    public IReadOnlyList<MultiplayerChoiceValue> ValeursDe(int? place)
        => place is { } p && Seats.FirstOrDefault(s => s.Seat == p && s.Values is { Count: > 0 })?.Values is { } siennes ? siennes : Values;

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

/// <summary>Le depart par place d'un jeu (<see cref="MultiplayerGame.CoreStart"/>).</summary>
public sealed record MultiplayerCoreStart
{
    /// <summary>Ou le coeur cherche l'etat, relatif au dossier systeme de RetroArch (le bios de RetroBat).</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>Le nombre de joueurs des etats : une partie a un autre nombre part sans eux.</summary>
    [JsonPropertyName("players")]
    public int Players { get; init; } = 2;

    /// <summary>Le md5 du contenu sur lequel les etats ont ete faits.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerCoreStartSeat> Seats { get; init; } = [];

    public bool VautPour(int joueurs, string? contenu)
        => joueurs == Players && (Content is null || string.Equals(Content, contenu, StringComparison.OrdinalIgnoreCase));

    public MultiplayerCoreStartSeat? DeLaPlace(int place) => Seats.FirstOrDefault(s => s.Seat == place);
}

public sealed record MultiplayerCoreStartSeat
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    /// <summary>Chemin relatif au dossier du catalogue : <c>states/psp/bomberman/manche1-p1-ppsspp.state</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
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

    /// <summary>
    /// LA CARTE DU COEUR (CDC Classements v1.1 §2) : <c>core_map</c>, chaque adresse de la fin (places et garde) se lit par
    /// READ_CORE_MEMORY, comme les choix. Absent : READ_CORE_RAM.
    /// </summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.Ordinal);
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

    /// <summary><c>core_map</c> : le signal, les scores et la garde se lisent par READ_CORE_MEMORY (CDC Classements v1.1 §2).</summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.Ordinal);

    [JsonIgnore]
    public bool EstConnu => Type == ScoreFinal;
}

/// <summary>
/// LES CLASSEMENTS A PLUSIEURS (CDC Classements v1.0 §3 a §5, 29/10) : une fin qui classe chaque humain, a 2 joueurs ou
/// plus, contre l'ordinateur ou en equipes. Quatre regles : <c>manches</c> (premier a N), <c>survie</c> (le dernier en vie),
/// <c>arrivee</c> (l'ordre d'arrivee d'une course), <c>score_fige</c> (le score d'avant la premiere mort). Une cle a part :
/// une API d'avant l'ignore, et le jeu reste au niveau Classique chez elle ; un jeu qui la porte porte aussi son
/// <c>min_api</c>.
/// </summary>
/// <summary>Le departage d'une fin a plusieurs : par la valeur de chaque place, la plus haute ou la plus basse d'abord.</summary>
public sealed record MultiplayerTieBreak
{
    [JsonPropertyName("better")]
    public string Better { get; init; } = "higher";

    /// <summary>Une adresse a part pour le departage : pas encore lue (la valeur de la place suffit a Sugoi Hebereke).</summary>
    [JsonPropertyName("core_ram")]
    public string? CoreRam { get; init; }

    [JsonIgnore]
    public bool PlusBasDAbord => Better == "lower";
}

public sealed record MultiplayerRankingEnd
{
    public const string Manches = "manches";
    public const string Survie = "survie";
    public const string Arrivee = "arrivee";
    public const string ScoreFige = "score_fige";

    public static readonly IReadOnlySet<string> Regles = new HashSet<string>(StringComparer.Ordinal) { Manches, Survie, Arrivee, ScoreFige };

    [JsonPropertyName("rule")]
    public string Rule { get; init; } = string.Empty;

    /// <summary><c>core_map</c> : toutes les adresses de la fin se lisent par READ_CORE_MEMORY (CDC Classements v1.1 §2).</summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    /// <summary>Une entree par place que le jeu peut avoir, humaine ou non (l'ordinateur se lit aussi).</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerRankingSeat> Seats { get; init; } = [];

    /// <summary><c>manches</c> : le nombre de manches qui ferme la rencontre, atteint par une place humaine ou non.</summary>
    [JsonPropertyName("first_to")]
    public int? FirstTo { get; init; }

    /// <summary><c>survie</c> : une place est eliminee quand sa valeur reste a ce seuil ou en dessous.</summary>
    [JsonPropertyName("out_at")]
    public long OutAt { get; init; }

    /// <summary>Le signal global de fin de rencontre, facultatif.</summary>
    [JsonPropertyName("over")]
    public MultiplayerDuelGuard? Over { get; init; }

    /// <summary>La lecture ne vaut que pendant que cet octet a cette valeur (les menus, la demo et les transitions ne comptent pas).</summary>
    [JsonPropertyName("guard")]
    public MultiplayerDuelGuard? Guard { get; init; }

    /// <summary>
    /// Une valeur n'est retenue qu'apres etre restee la meme ce delai (une vie lue au vol entre deux manches, la vie du
    /// gagnant qui remonte un instant a KOF '98).
    /// </summary>
    [JsonPropertyName("settle_ms")]
    public int SettleMs { get; init; } = 1000;

    [JsonPropertyName("min_humans")]
    public int MinHumans { get; init; } = 2;

    [JsonPropertyName("close_after_s")]
    public int CloseAfterS { get; init; } = 5;

    /// <summary>
    /// <c>arrivee</c> : la base des positions du jeu, 0 ou 1 (CDC Classements v1.1 §7, n° 3 ; Mario Kart SC et F-Zero
    /// comptent depuis 0). Les humains se classent par l'ordre de leurs positions : la base ne change rien au classement,
    /// elle dit seulement ce que le jeu ecrit.
    /// </summary>
    [JsonPropertyName("rank_base")]
    public int RankBase { get; init; } = 1;

    public const string OrdreDArrivee = "finish_order";

    /// <summary>
    /// <c>arrivee</c> sans octet de position (v1.1 §7, n° 4) : <c>finish_order</c>, chaque place prend son rang dans l'ordre
    /// ou son drapeau d'arrivee tient (Kurukuru Kururin, Sonic Advance 3) ; deux arrivees dans la meme fenetre
    /// <see cref="SettleMs"/> partagent leur rang. Absent : la position que lit chaque place.
    /// </summary>
    [JsonPropertyName("position")]
    public string? Position { get; init; }

    /// <summary>
    /// <c>survie</c> : le departage des places encore debout au signal de fin (CDC Classements v1.1 §7, n° 20 ; Sugoi
    /// Hebereke, un combat fini au temps) par la valeur de chaque place, sa vie restante. Absent : elles partagent leur rang.
    /// </summary>
    [JsonPropertyName("tie_break")]
    public MultiplayerTieBreak? TieBreak { get; init; }

    [JsonIgnore]
    public bool ParOrdreDArrivee => string.Equals(Position, OrdreDArrivee, StringComparison.Ordinal)
        || (Rule == Arrivee && Seats.Count > 0 && Seats.All(s => s.CoreRam.Length == 0));

    [JsonIgnore]
    public bool EstConnu => Regles.Contains(Rule);

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.Ordinal);

    /// <summary>Jamais moins de deux humains pour classer (regle du user du 29/10).</summary>
    [JsonIgnore]
    public int HumainsMin => Math.Max(2, MinHumans);

    [JsonIgnore]
    public bool AvecCamps => Seats.Any(s => s.Camp is not null);

    public MultiplayerRankingSeat? Place(int place) => Seats.FirstOrDefault(s => s.Seat == place);

    /// <summary>
    /// Une rencontre a ces places humaines se classe-t-elle ? Au moins <see cref="HumainsMin"/> humains, et, dans un jeu en
    /// equipes, des humains dans deux camps au moins (face a face) : tous du meme cote, personne n'est en face.
    /// </summary>
    public bool Classable(IReadOnlyCollection<int> humains)
        => humains.Count >= HumainsMin
           && (!AvecCamps || humains.Select(h => Place(h)?.Camp).Where(c => c is not null).Distinct().Count() >= 2);
}

/// <summary>Une place d'une fin a plusieurs : sa valeur (compteur, vie, position ou score), son camp, sa mort, son arrivee.</summary>
public sealed record MultiplayerRankingSeat
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    /// <summary>La valeur de la place ; facultative en <c>survie</c> quand <see cref="Dead"/> suffit.</summary>
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    [JsonPropertyName("bcd")]
    public bool Bcd { get; init; }

    /// <summary>
    /// UN CHIFFRE PAR OCTET (CDC Classements v1.1 §7, n° 21 ; Rampage, 7 octets) : chaque octet porte un chiffre decimal
    /// (son quartet bas), jusqu'a 8 octets ; un quartet au-dela de 9 rend la lecture impossible.
    /// </summary>
    [JsonPropertyName("digits")]
    public bool Digits { get; init; }

    /// <summary>« le », « be », ou « 68k » (un nombre du 68000 tel que FBNeo le range, octets permutes deux a deux).</summary>
    [JsonPropertyName("order")]
    public string Order { get; init; } = "le";

    /// <summary>Le camp de la place dans un jeu en equipes ; absent, chacun pour soi.</summary>
    [JsonPropertyName("camp")]
    public int? Camp { get; init; }

    [JsonPropertyName("dead")]
    public MultiplayerDuelGuard? Dead { get; init; }

    [JsonPropertyName("finished")]
    public MultiplayerDuelGuard? Finished { get; init; }

    [JsonIgnore]
    public long? Decalage => new MultiplayerChoiceSeat { CoreRam = CoreRam }.Decalage;

    /// <summary>Les octets lus, dans l'ordre de la memoire, devenus la valeur de la place ; null pour un chiffre BCD impossible.</summary>
    public long? Valeur(IReadOnlyList<int> octets)
    {
        if (octets.Count != Bytes)
        {
            return null;
        }

        var ordonnes = Order switch
        {
            "be" => octets.ToList(),
            "68k" => Enumerable.Range(0, octets.Count).Select(i => (i ^ 1) < octets.Count ? octets[i ^ 1] : octets[i]).ToList(),
            _ => octets.Reverse().ToList(),
        };
        long valeur = 0;
        foreach (var o in ordonnes)
        {
            if (Digits)
            {
                if ((o & 15) > 9)
                {
                    return null;
                }

                valeur = valeur * 10 + (o & 15);
            }
            else if (Bcd)
            {
                if (o >> 4 > 9 || (o & 15) > 9)
                {
                    return null;
                }

                valeur = valeur * 100 + (o >> 4) * 10 + (o & 15);
            }
            else
            {
                valeur = (valeur << 8) | (uint)o;
            }
        }

        return valeur;
    }

    /// <summary>Le nombre d'octets permis : 8 en chiffres, 4 sinon.</summary>
    [JsonIgnore]
    public int OctetsMax => Digits ? 8 : 4;
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

    /// <summary>
    /// LA LARGEUR DU SIGNAL (CDC Classements v1.1 §7, n° 1) : 1 a 4 octets, dans l'ordre <see cref="Order"/> (Mario Kart SC lit
    /// son arrivee sur un mot de 16 bits). Seule une fin a plusieurs (<c>ranking_end</c>) lit plus d'un octet.
    /// </summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    [JsonPropertyName("order")]
    public string Order { get; init; } = "le";

    [JsonIgnore]
    public long? Decalage => CoreRam.Trim() is { Length: > 2 } texte && texte.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(texte[2..], global::System.Globalization.NumberStyles.HexNumber, null, out var valeur) && valeur >= 0
            ? valeur
            : null;

    /// <summary>La valeur des octets lus, dans l'ordre de la memoire, selon <see cref="Order"/> ; null s'ils ne sont pas <see cref="Bytes"/>.</summary>
    public long? Valeur(IReadOnlyList<int> octets)
    {
        if (octets.Count != Bytes)
        {
            return null;
        }

        var ordonnes = Order switch
        {
            "be" => octets.ToList(),
            "68k" => Enumerable.Range(0, octets.Count).Select(i => (i ^ 1) < octets.Count ? octets[i ^ 1] : octets[i]).ToList(),
            _ => octets.Reverse().ToList(),
        };
        return ordonnes.Aggregate(0L, (valeur, o) => (valeur << 8) | (uint)o);
    }
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

    /// <summary>« LE CONSEIL DU COIN » de l'ecran de lancement, par langue (« fr », « en »...) : une phrase courte, vraie pour ce jeu.</summary>
    [JsonPropertyName("tips")]
    public IReadOnlyDictionary<string, string>? Conseils { get; init; }

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

    /// <summary>
    /// Choix seulement : les valeurs de cette place, quand chaque cote a les siennes (Title Fight : quatre boxeurs par
    /// ecran). Les memes nombres que les valeurs du choix, d'autres noms et d'autres images ; la plateforme, qui ne
    /// regarde que les nombres, n'a rien a savoir.
    /// </summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<MultiplayerChoiceValue>? Values { get; init; }

    [JsonIgnore]
    public long? Decalage => CoreRam.Trim() is { Length: > 2 } texte && texte.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(texte[2..], global::System.Globalization.NumberStyles.HexNumber, null, out var valeur) && valeur >= 0
            ? valeur
            : null;
}

/// <summary>Des cles de remappage de RetroArch pour une place (<see cref="MultiplayerGame.SeatRemap"/>).</summary>
public sealed record MultiplayerSeatRemap
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    [JsonPropertyName("keys")]
    public IReadOnlyDictionary<string, string> Keys { get; init; } = new Dictionary<string, string>();

    /// <summary>Une cle d'un fichier de remappage : bouton, stick, type de manette, port ou mode analogique d'un joueur.</summary>
    public static bool CleDeRemappage(string cle) => CleRegex.IsMatch(cle ?? string.Empty);

    /// <summary>Un numero de bouton ou de stick de RetroArch, -1 pour aucun.</summary>
    public static bool ValeurDeRemappage(string valeur)
        => int.TryParse(valeur, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is >= -1 and <= 1024;

    private static readonly System.Text.RegularExpressions.Regex CleRegex = new(
        "^(input_player[1-8]_(btn|stk)_[a-z0-9_]+[+-]?|input_libretro_device_p[1-8]|input_remap_port_p[1-8]|input_player[1-8]_analog_dpad_mode)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
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

    public MultiplayerCatalogueStore(ILogger<MultiplayerCatalogueStore>? logger = null, string? path = null, Version? api = null)
    {
        _logger = logger;
        Path = path ?? System.IO.Path.Combine(RetroBatPaths.PluginRoot, "resources", "multiplayer", "catalogue.json");
        Api = api ?? (Version.TryParse(RetroBat.Api.Infrastructure.ApiExposeVersion.Current.Split('+')[0], out var version) ? version : new Version(0, 0));
    }

    public string Path { get; }

    /// <summary>La version de cette API : un jeu dont le <c>min_api</c> la depasse quitte le catalogue lu.</summary>
    public Version Api { get; }

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

            var sains = Retenir(catalogue, Api, out erreur, out var ecartes);
            var retenu = sains is null ? null : Nettoyer(sains);
            if (erreur != null)
            {
                _logger?.LogWarning("Catalogue MULTIPLAYER refuse : {Erreur}", erreur);
            }

            foreach (var raison in ecartes)
            {
                _logger?.LogWarning("Catalogue MULTIPLAYER : jeu ecarte, {Raison}", raison);
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

            if (ValiderLeJeu(jeu) is { } erreur)
            {
                return jeu.Id + " : " + erreur;
            }
        }

        return null;
    }

    /// <summary>
    /// UN JEU INVALIDE EST ECARTE SEUL (CDC Classements v1.0 §2, 29/10) : jusqu'a la 1.9.126, un seul jeu au type de fin
    /// inconnu faisait refuser tout le catalogue, et la borne perdait la collection MULTIPLAYER. Le catalogue garde ici ses
    /// jeux sains ; un jeu en double, invalide ou qui demande une API plus recente (<see cref="MultiplayerGame.MinApi"/>)
    /// le quitte, avec sa raison. Null avec une erreur seulement quand le fichier entier ne se lit pas (schema inconnu).
    /// </summary>
    internal static MultiplayerCatalogue? Retenir(MultiplayerCatalogue? catalogue, Version api, out string? erreur, out IReadOnlyList<string> ecartes)
    {
        var raisons = new List<string>();
        ecartes = raisons;
        if (catalogue == null)
        {
            erreur = "catalogue vide";
            return null;
        }

        if (catalogue.SchemaVersion != MultiplayerCatalogue.SchemaVersionSupportee)
        {
            erreur = "schema " + catalogue.SchemaVersion + " inconnu (attendu " + MultiplayerCatalogue.SchemaVersionSupportee + ")";
            return null;
        }

        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retenus = new List<MultiplayerGame>();
        foreach (var jeu in catalogue.Games)
        {
            if (string.IsNullOrWhiteSpace(jeu.Id) || !vus.Add(jeu.Id.Trim()))
            {
                raisons.Add("identifiant vide ou en double : « " + jeu.Id + " »");
                continue;
            }

            // Un jeu fait pour une API plus recente n'est pas juge par celle-ci : il pourrait porter ce qu'elle ne connait pas.
            if (!jeu.AdmisPar(api))
            {
                continue;
            }

            if (ValiderLeJeu(jeu) is { } raison)
            {
                raisons.Add(jeu.Id + " : " + raison);
                continue;
            }

            retenus.Add(jeu);
        }

        erreur = null;
        return catalogue with { Games = retenus };
    }

    /// <summary>Ce qui ne va pas dans un jeu du catalogue, ou null.</summary>
    internal static string? ValiderLeJeu(MultiplayerGame jeu)
    {
        if (string.IsNullOrWhiteSpace(jeu.System))
        {
            return "systeme manquant";
        }

        if (jeu.EstArcade ? string.IsNullOrWhiteSpace(jeu.Set) : (jeu.Content?.Md5.Count ?? 0) == 0)
        {
            // Sans identite, le jeu entrerait sur la foi d'un nom de fichier.
            return jeu.EstArcade ? "set manquant" : "aucun md5 de contenu";
        }

        if (jeu.Players < 2)
        {
            return "moins de deux joueurs";
        }

        if (jeu.Level is not (MultiplayerLevels.Classique or MultiplayerLevels.NelfePlay))
        {
            return "niveau « " + jeu.Level + " » inconnu";
        }

        if (jeu.Screens is < 1 or > 4
            || (jeu.SeatScreens is { Count: > 0 } ecrans
                && (ecrans.Count < jeu.Players || ecrans.Any(ecran => ecran < 1 || ecran > jeu.Screens))))
        {
            // Une place sans ecran, ou un ecran qui n'existe pas : une borne afficherait la vue d'un autre.
            return "ecrans par place incoherents";
        }

        if (jeu.SeatPorts is { Count: > 0 } ports
            && (ports.Count < jeu.Players || ports.Any(p => p.Count == 0 || p.Any(n => n is < 1 or > 8))
                || ports.SelectMany(p => p).Distinct().Count() != ports.Sum(p => p.Count)))
        {
            // Une place sans port, un port hors du netplay ou tenu par deux places : deux bornes joueraient le meme poing.
            return "ports par place incoherents";
        }

        if (jeu.SeatRemap is { Count: > 0 } remaps
            && remaps.Any(r => r.Seat < 1 || r.Seat > jeu.Players || r.Keys.Count == 0
                || r.Keys.Any(p => !MultiplayerSeatRemap.CleDeRemappage(p.Key) || !MultiplayerSeatRemap.ValeurDeRemappage(p.Value))))
        {
            // Une cle qui n'est pas du remappage (un reglage de RetroArch glisse dans le fichier) ou une place qui n'existe pas.
            return "remappage par place incoherent";
        }

        // Le champ « bios » (CDC Classements v1.1 §5) n'est pas lu ici : l'outil de diagnostic dit quel BIOS manque (decision
        // du user du 29/10).
        return ValiderLesVues(jeu) ?? ValiderLeDepart(jeu) ?? ValiderLeDepartDuCoeur(jeu) ?? ValiderLeClassement(jeu);
    }

    /// <summary>
    /// La fin a plusieurs (CDC Classements v1.0 §3) : une regle connue, une adresse lisible pour chaque place du jeu, ce que
    /// la regle demande (le seuil des manches, la vie ou la mort de la survie, la position de l'arrivee, le score et la mort
    /// du score fige), et des camps coherents. Jamais avec <c>end</c> ou <c>match_end</c> : une API d'avant lirait l'autre.
    /// </summary>
    internal static string? ValiderLeClassement(MultiplayerGame jeu)
    {
        if (jeu.RankingEnd is not { } fin)
        {
            return null;
        }

        if (jeu.End is not null || jeu.MatchEnd is not null)
        {
            return "classement : ranking_end avec end ou match_end";
        }

        if (!fin.EstConnu)
        {
            return "classement : regle « " + fin.Rule + " » inconnue";
        }

        if (fin.Memory is not (null or MultiplayerChoice.CarteDuCoeur))
        {
            return "classement : memoire « " + fin.Memory + " » inconnue";
        }

        if (fin.SettleMs is < 0 or > 10000 || fin.CloseAfterS is < 0 or > 60 || fin.MinHumans > 8)
        {
            return "classement : delais ou nombre d'humains incoherents";
        }

        // Un signal de 1 a 4 octets (v1.1 §7, n° 1), une valeur qui tient dans sa largeur.
        static bool Octet(MultiplayerDuelGuard? signal) => signal is null
            || (signal.Decalage is not null && signal.Bytes is >= 1 and <= 4 && signal.Order is "le" or "be" or "68k"
                && signal.Value >= 0 && (signal.Bytes == 4 || signal.Value < 1L << (8 * signal.Bytes)));
        if (!Octet(fin.Over) || !Octet(fin.Guard))
        {
            return "classement : signal de fin ou garde incoherent";
        }

        if (fin.RankBase is not (0 or 1) || fin.Position is not (null or MultiplayerRankingEnd.OrdreDArrivee)
            || (fin.Position is not null && fin.Rule != MultiplayerRankingEnd.Arrivee))
        {
            return "classement : base ou mode des positions incoherent";
        }

        if (fin.Seats.Any(s => s.Seat is < 1 or > 8) || fin.Seats.Select(s => s.Seat).Distinct().Count() != fin.Seats.Count)
        {
            return "classement : place hors de 1 a 8 ou en double";
        }

        for (var place = 1; place <= jeu.Players; place++)
        {
            if (fin.Place(place) is null)
            {
                return "classement : pas d'entree pour la place " + place;
            }
        }

        foreach (var place in fin.Seats)
        {
            var sansValeur = place.CoreRam.Length == 0;
            if ((!sansValeur && place.Decalage is null) || place.Bytes < 1 || place.Bytes > place.OctetsMax || place.Order is not ("le" or "be" or "68k")
                || (place.Digits && place.Bcd) || !Octet(place.Dead) || !Octet(place.Finished))
            {
                return "classement : adresse ou format de la place " + place.Seat + " incoherent";
            }

            // Sans valeur, la survie se lit encore par la mort de la place, et l'arrivee par l'ordre des drapeaux (v1.1 §7).
            var manque = fin.Rule switch
            {
                MultiplayerRankingEnd.Survie => sansValeur && place.Dead is null,
                MultiplayerRankingEnd.ScoreFige => sansValeur || place.Dead is null,
                MultiplayerRankingEnd.Arrivee when fin.ParOrdreDArrivee => place.Finished is null,
                _ => sansValeur,
            };
            if (manque)
            {
                return "classement : la place " + place.Seat + " n'a pas ce que la regle « " + fin.Rule + " » lit";
            }
        }

        if (fin.Rule == MultiplayerRankingEnd.Manches && fin.FirstTo is not (>= 1 and <= 255) && fin.Over is null)
        {
            return "classement : manches sans seuil ni signal de fin";
        }

        if (fin.Rule == MultiplayerRankingEnd.Arrivee && fin.Over is null && fin.Seats.Any(s => s.Finished is null))
        {
            return "classement : arrivee sans drapeau d'arrivee ni signal de fin";
        }

        if (fin.AvecCamps && (fin.Seats.Any(s => s.Camp is not (>= 1 and <= 8)) || fin.Seats.Select(s => s.Camp).Distinct().Count() < 2))
        {
            return "classement : camps incoherents (chaque place en a un, deux camps au moins)";
        }

        // Le departage lit la valeur de chaque place : seulement en survie, chaque place avec sa valeur.
        if (fin.TieBreak is { } departage && (fin.Rule != MultiplayerRankingEnd.Survie || departage.Better is not ("higher" or "lower")
                || !string.IsNullOrEmpty(departage.CoreRam) || fin.Seats.Any(s => s.CoreRam.Length == 0)))
        {
            return "classement : departage incoherent (survie, « higher » ou « lower », par la valeur de chaque place)";
        }

        return null;
    }

    /// <summary>
    /// Le depart par place : un chemin qui reste sous le dossier systeme, un etat sous <c>states/</c> a l'empreinte connue
    /// pour chaque place, exactement une fois. Une place sans etat demarrerait seule au menu pendant que l'autre joue.
    /// </summary>
    internal static string? ValiderLeDepartDuCoeur(MultiplayerGame jeu)
    {
        if (jeu.CoreStart is not { } depart)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(depart.Path) || depart.Path.Contains("..", StringComparison.Ordinal)
            || global::System.IO.Path.IsPathRooted(depart.Path) || depart.Path.Contains(':'))
        {
            return "depart par place : chemin hors du dossier systeme";
        }

        if (depart.Players < 2 || depart.Players > jeu.Players)
        {
            return "depart par place pour " + depart.Players + " joueurs hors de 2 a " + jeu.Players;
        }

        if (depart.Content is { } md5 && !(jeu.Content?.Md5 ?? []).Contains(md5, StringComparer.OrdinalIgnoreCase))
        {
            return "depart par place fait sur un contenu hors du catalogue";
        }

        for (var place = 1; place <= depart.Players; place++)
        {
            var etats = depart.Seats.Where(s => s.Seat == place).ToList();
            if (etats.Count != 1)
            {
                return "depart par place : " + (etats.Count == 0 ? "pas d'etat" : "plusieurs etats") + " pour la place " + place;
            }

            var etat = etats[0];
            if (!etat.State.StartsWith("states/", StringComparison.Ordinal) || etat.State.Contains("..", StringComparison.Ordinal))
            {
                return "depart par place : etat hors du dossier MULTIPLAYER";
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(etat.Sha256.Trim(), "^[0-9a-fA-F]{64}$"))
            {
                return "depart par place : empreinte de l'etat manquante";
            }
        }

        return depart.Seats.All(s => s.Seat >= 1 && s.Seat <= depart.Players) ? null : "depart par place : place hors de la partie";
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

                // Dix secondes au plus : au-dela, le jeu a quitte depuis longtemps l'ecran ou il accepte les choix.
                if (depart.WriteAfterFrames is < 0 or > 600)
                {
                    return "delai d'ecriture des choix hors de 0 a 600 images";
                }

                if (depart.Content is { } md5 && !(jeu.Content?.Md5 ?? []).Contains(md5, StringComparer.OrdinalIgnoreCase))
                {
                    return "etat fait sur un contenu hors du catalogue";
                }

                if (depart.Locks is { Count: > 0 } && !ReferenceEquals(depart, jeu.Start))
                {
                    return "verrous du depart ailleurs que sous start";
                }
            }

            if (jeu.Start.Locks is { Count: > 0 } verrous)
            {
                // La table de verrous n'existe que dans le coeur mGBA Splitscreen de NelfePlay ; les adresses sont celles de la
                // console 1, et tout tient dans la table avec le roulement (les choix tenus passent apres, tous ou aucun).
                var consoles = (MultiplayerStartLock verrou) => verrou.AllConsoles ? jeu.Players : 1;
                var duRoulement = jeu.Rotation is { DecalageDuVerrou: not null } coupe ? (coupe.AllConsoles ? jeu.Players : 1) : 0;
                if (!MultiplayerCoeursService.Coeurs.Any(coeur => coeur.Nom == MultiplayerCoeursService.MgbaSplitscreen && coeur.SesSystemes.Contains(jeu.System))
                    || verrous.Any(v => v.Decalage is not (>= 0 and < 0x10000000) || v.Value is < 0 or > 255)
                    || verrous.Sum(consoles) + duRoulement > MultiplayerRotation.VerrousMax)
                {
                    return "verrous du depart incoherents (coeur mGBA Splitscreen, adresse de la console 1, octet, 16 entrees au plus)";
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
            if (fin.FirstTo is < 1 or > 255 || fin.CloseAfterS is < 0 or > 60 || fin.Memory is not (null or MultiplayerChoice.CarteDuCoeur))
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

            if (fin.Duel is { } garde && (garde.Decalage is null || garde.Value is < 0 or > 255 || garde.Bytes != 1))
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

            if (autre.Over?.Decalage is null || autre.Over.Value is < 0 or > 255 || autre.Over.Bytes != 1 || autre.CloseAfterS is < 0 or > 60
                || autre.Memory is not (null or MultiplayerChoice.CarteDuCoeur))
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

            if (autre.Duel is { } garde && (garde.Decalage is null || garde.Value is < 0 or > 255 || garde.Bytes != 1))
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

            // Les valeurs d'une place : les memes nombres que celles du choix (la plateforme et le departage n'en voient pas
            // d'autres), chacune avec son nom.
            var nombres = choix.Values.Select(v => v.Value).OrderBy(v => v).ToList();
            if (choix.Seats.FirstOrDefault(s => s.Values is { Count: > 0 } siennes
                    && (!siennes.Select(v => v.Value).OrderBy(v => v).SequenceEqual(nombres) || siennes.Any(v => string.IsNullOrWhiteSpace(v.Name))))
                is { } placeAuxValeurs)
            {
                return "choix « " + choix.Key + " » : valeurs de la place " + placeAuxValeurs.Seat + " differentes de celles du choix";
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
