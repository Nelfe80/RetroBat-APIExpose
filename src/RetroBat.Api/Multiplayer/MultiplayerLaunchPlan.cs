using System.Globalization;
using RetroBat.Api.Controllers;
using RetroBat.Api.Netplay;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// CE QU'UNE BORNE PREPARE POUR UNE PARTIE MULTIPLAYER (CDC MULTIPLAYER v2.5, lot M4), sans rien
/// lancer : les arguments du lanceur de RetroBat, les options du coeur, la place netplay, et le
/// recadrage d'une borne a un moniteur par joueur. Pur : c'est ce qui se teste.
///
/// Le lancement est DIRECT, comme le lancement a moteur impose : le lanceur importe sa ligne de
/// commande en dernier, apres <c>es_settings.cfg</c>. Tout passe donc en arguments, le coeur impose
/// comme le netplay, et pas une cle d'EmulationStation n'est ecrite. RetroAchievements est coupe
/// pour la seance (V24-24).
/// </summary>
public static class MultiplayerLaunchPlan
{
    public const int PortNetplay = 55435;

    /// <summary>Le dossier des shaders de NelfePlay, sous les shaders de RetroBat.</summary>
    public const string DossierShaders = "nelfeplay";

    /// <summary>
    /// Les options du coeur, communes et propres a la place : <c>{players}</c> devient le nombre de
    /// joueurs de la partie, <c>{seat}</c> la place de cette borne. Les communes sont les memes sur
    /// toutes les bornes (le netplay l'exige).
    /// </summary>
    public static IReadOnlyDictionary<string, string> OptionsDuCoeur(MultiplayerCore? coeur, int joueurs, int place)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string Remplir(string valeur) => valeur
            .Replace("{players}", joueurs.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{seat}", place.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        foreach (var (cle, valeur) in coeur?.Options ?? new Dictionary<string, string>())
        {
            options[cle] = Remplir(valeur);
        }

        foreach (var (cle, valeur) in coeur?.SeatOptions ?? new Dictionary<string, string>())
        {
            options[cle] = Remplir(valeur);
        }

        return options;
    }

    /// <summary>
    /// La place netplay : RetroArch donne a chaque borne la manette qu'elle demande. L'hote demande la
    /// sienne comme un invite : il n'est donc pas forcement P1 (V24-12).
    /// </summary>
    /// <summary>
    /// Le prefixe des DIP d'un jeu d'arcade sous FBNeo (<c>fbneo-dipswitch-sf2ce-</c>), ou null ailleurs. Ceux que le
    /// catalogue ne fixe pas sont retires le temps de la partie : toutes les bornes prennent la valeur par defaut du coeur.
    /// </summary>
    public static string? PrefixeDesDip(MultiplayerGame jeu, string coeur)
        => jeu.EstArcade && jeu.Set is { Length: > 0 } set && string.Equals(coeur, "fbneo", StringComparison.OrdinalIgnoreCase)
            ? "fbneo-dipswitch-" + set + "-"
            : null;

    public static IReadOnlyDictionary<string, string> Place(int place)
    {
        var reglages = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i <= 8; i++)
        {
            reglages["netplay_request_device_p" + i] = i == place ? "true" : "false";
        }

        return reglages;
    }

    /// <summary>
    /// Le nom du shader qui ne montre que l'ecran de cette place (moniteurs cote a cote d'une borne d'origine) ou sa vue (ecran
    /// partage d'un jeu de salon), ou null pour un jeu a un seul ecran.
    /// </summary>
    public static string? ShaderDeLaPlace(MultiplayerGame jeu, int place, int joueurs = 2)
        => jeu.VueDeLaPlace(place, joueurs) is { } vue
            ? $"{DossierShaders}/{NomDeLaVue(vue)}"
            : jeu.Screens <= 1 ? null : $"{DossierShaders}/half-{jeu.EcranDeLaPlace(place)}-of-{jeu.Screens}";

    /// <summary>
    /// Les arguments du lanceur. <paramref name="netplay"/> est la partie netplay de la ligne,
    /// propre a l'hote ou a l'invite (<see cref="ArgumentsHote"/>, <see cref="ArgumentsInvite"/>).
    /// Null quand un nom n'est pas un nom (meme garde que le lancement a moteur impose). <paramref name="decor"/> : le jeu de
    /// decor qui porte les panneaux en jeu (<see cref="MultiplayerDecor"/>), ou null.
    /// </summary>
    public static string? Arguments(string manettes, string systeme, string coeur, string rom, MultiplayerGame jeu, int place, IEnumerable<string> netplay,
        int joueurs = 2, string? decor = null)
    {
        if (!CommandsController.NomDeLanceur(systeme) || !CommandsController.NomDeLanceur(coeur))
        {
            return null;
        }

        var morceaux = new List<string> { manettes, "-system", systeme, "-emulator", "libretro", "-core", coeur, "-retroachievements", "0" };
        morceaux.AddRange(ArgumentsDuTempsPartage);
        morceaux.AddRange(ArgumentsDuJeu(jeu));
        if (ShaderDeLaPlace(jeu, place, joueurs) is { } shader)
        {
            // Un seul ecran, au format de la borne d'origine, sans habillage qui decalerait l'image. Une vue d'ecran partage
            // prend son propre format : « config » fait lire a RetroArch video_aspect_ratio, que l'API pose (le lanceur n'y
            // touche pas), sinon la moitie d'un Herzog Zwei s'etirait au 4/3.
            var format = jeu.VueDeLaPlace(place, joueurs) is null ? "4/3" : "config";
            morceaux.AddRange(["-shaderset", "none", "-shader", shader, "-ratio", format, "-bezel", "none"]);
        }
        else if (decor is not null && CommandsController.NomDeLanceur(decor))
        {
            // Les panneaux en jeu : le lanceur affiche notre decor et cale le jeu dans son trou (CDC Ecrans v1.0, 3.6).
            morceaux.AddRange(["-bezel", decor]);
        }

        morceaux.AddRange(netplay);
        morceaux.AddRange(["-rom", '"' + rom.Replace("\"", "") + '"']);
        return string.Join(' ', morceaux.Where(morceau => morceau.Length > 0));
    }

    /// <summary>
    /// Ce qui pourrait alterer le temps d'une partie partagee, coupe le temps du lancement (retour du user du 22/10) : le
    /// retour arriere (que le lanceur allume d'office sur les coeurs qui le permettent), la sauvegarde et le chargement
    /// automatiques, l'execution anticipee et les trames preemptives ; l'avance rapide ramenee a la vitesse normale.
    /// Le lanceur lit ces paires en dernier, comme le reste de la ligne. Restent ses raccourcis de chargement d'etat : il les
    /// reecrit a chaque lancement et les retire des surcharges de RetroArch.
    ///
    /// JAMAIS DE CLE EN « -p » (vu le 23/10 : plus aucun netplay ne partait depuis la 1.9.83) : le lanceur prend tout argument
    /// qui commence par « -p » pour une manette (« -p1name »...). « -preemptive_frames » y devenait une manette fantome de
    /// joueur 0, sans identifiant, rangee avant celle du joueur 1, et la lecture des manettes cassait le lancement. Les trames
    /// preemptives restent coupees : le lanceur ne les allume qu'avec l'execution anticipee, que « -runahead 0 » coupe.
    /// </summary>
    internal static readonly IReadOnlyList<string> ArgumentsDuTempsPartage =
        ["-rewind", "0", "-autosave", "0", "-runahead", "0", "-fastforward_ratio", "1.000000"];

    /// <summary>
    /// Les reglages du jeu que le lanceur reecrirait sinon (<see cref="MultiplayerGame.LauncherOptions"/>), en paires
    /// « -cle valeur », dans l'ordre des cles. Une cle en « p » (une manette pour le lanceur) ou un nom douteux est ecarte.
    /// </summary>
    internal static IEnumerable<string> ArgumentsDuJeu(MultiplayerGame jeu)
    {
        foreach (var (cle, valeur) in (jeu.LauncherOptions ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (cle.StartsWith('p') || cle.StartsWith('P') || !CommandsController.NomDeLanceur(cle) || !CommandsController.NomDeLanceur(valeur))
            {
                continue;
            }

            yield return "-" + cle;
            yield return valeur;
        }
    }

    /// <summary>
    /// Les noms des manettes d'une ligne de lancement d'ES (« P1 Generic USB Joystick, P2 vJoy Device »), sans leur
    /// chemin ni leur identifiant ; « aucune » quand rien n'est repris : le lanceur range alors les manettes lui-meme.
    /// </summary>
    public static string NomsDesManettes(string manettes)
    {
        var noms = System.Text.RegularExpressions.Regex.Matches(manettes ?? string.Empty, @"-p(\d+)name ""([^""]*)""")
            .Select(m => "P" + m.Groups[1].Value + " " + m.Groups[2].Value)
            .ToList();
        return noms.Count == 0 ? "aucune (le lanceur range les manettes lui-meme)" : string.Join(", ", noms);
    }

    /// <summary>L'hote : ses reglages netplay passent tous en arguments, mots de passe compris.</summary>
    public static IReadOnlyList<string> ArgumentsHote(string pseudo, string motDePasseJoueur, string motDePasseSpectateur, string relais)
    {
        foreach (var valeur in new[] { pseudo, motDePasseJoueur, motDePasseSpectateur, relais })
        {
            Exiger(valeur);
        }

        return
        [
            "-netplay", "true",
            "-netplaymode", "host",
            "-netplay.nickname", pseudo,
            "-netplay.password", motDePasseJoueur,
            "-netplay.spectatepassword", motDePasseSpectateur,
            "-netplay.relay", relais,
            "-netplay.port", PortNetplay.ToString(CultureInfo.InvariantCulture),
            // Sans annonce publique, le relais ne rend jamais l'identifiant de session.
            "-netplay_public_announce", "true",
        ];
    }

    /// <summary>L'invite : la session de l'hote, et son propre pseudo.</summary>
    public static IReadOnlyList<string> ArgumentsInvite(string pseudo, MultiplayerSessionNetplay session)
    {
        foreach (var valeur in new[] { pseudo, session.Relay, session.Session, session.PlayerPassword })
        {
            Exiger(valeur);
        }

        return
        [
            "-netplay", "true",
            "-netplaymode", "client",
            "-netplayip", session.Relay,
            "-netplayport", (session.Port > 0 ? session.Port : PortNetplay).ToString(CultureInfo.InvariantCulture),
            "-netplaysession", session.Session,
            "-netplaypass", session.PlayerPassword,
            "-netplay.nickname", pseudo,
        ];
    }

    /// <summary>Le pseudo de lobby, alphanumerique ASCII (le protocole plafonne a 32 octets).</summary>
    public static string Pseudo(string pseudoJoueur, string jeton) => NetplaySettings.ComposerPseudo(pseudoJoueur, jeton);

    /// <summary>
    /// Une valeur passee au lanceur : non vide, sans espace ni guillemet, et sans tiret en tete (le
    /// lanceur la prendrait pour une cle). Les mots de passe et sessions qu'on fabrique y satisfont ;
    /// une valeur venue d'ailleurs qui n'y satisfait pas est refusee plutot que glissee.
    /// </summary>
    private static void Exiger(string valeur)
    {
        if (string.IsNullOrEmpty(valeur) || valeur[0] == '-' || valeur.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            throw new ArgumentException("valeur netplay refusee pour la ligne du lanceur");
        }
    }

    /// <summary>
    /// Le shader qui ne lit que l'ecran <paramref name="ecran"/> sur <paramref name="ecrans"/>, en slang
    /// (d3d11, vulkan, glcore) et en GLSL (pilote gl). Une seule passe, a la taille de l'affichage.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FichiersDeShader(int ecran, int ecrans)
        => Fichiers($"half-{ecran}-of-{ecrans}", $"seul l'ecran {ecran} sur {ecrans}", 1.0 / ecrans, (ecran - 1) / (double)ecrans, 1, 0);

    /// <summary>Le nom du shader d'une vue : son rectangle en millieme de l'image (view-x-y-w-h).</summary>
    public static string NomDeLaVue(MultiplayerView vue)
    {
        static string Millieme(double fraction) => ((int)Math.Round(fraction * 1000)).ToString("0000", CultureInfo.InvariantCulture);
        return $"view-{Millieme(vue.X)}-{Millieme(vue.Y)}-{Millieme(vue.W)}-{Millieme(vue.H)}";
    }

    /// <summary>Le shader qui ne lit que le rectangle d'une vue (l'ecran partage d'un jeu de salon).</summary>
    public static IReadOnlyDictionary<string, string> FichiersDeLaVue(MultiplayerView vue)
        => Fichiers(NomDeLaVue(vue), "seule la vue " + NomDeLaVue(vue) + " de l'ecran partage", vue.W, vue.X, vue.H, vue.Y);

    /// <summary>Le format a donner a RetroArch (video_aspect_ratio) pour une vue, ecrit a la decimale anglaise.</summary>
    public static string FormatPourRetroArch(MultiplayerGame jeu, MultiplayerView vue)
        => jeu.FormatDeLaVue(vue).ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Le shader qui ne lit qu'un rectangle de l'image : largeur <paramref name="ex"/> a partir de <paramref name="ox"/>,
    /// hauteur <paramref name="ey"/> a partir de <paramref name="oy"/>, en fractions. Slang et GLSL, une passe.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Fichiers(string nom, string quoi, double ex, double ox, double ey, double oy)
    {
        static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
        var (echelleX, decalageX, echelleY, decalageY) = (F(ex), F(ox), F(ey), F(oy));
        var slang = $$"""
            #version 450
            // NelfePlay MULTIPLAYER : {{quoi}}, celui de la place de cette borne.
            layout(push_constant) uniform Push
            {
               vec4 SourceSize;
               vec4 OriginalSize;
               vec4 OutputSize;
               uint FrameCount;
            } params;

            layout(std140, set = 0, binding = 0) uniform UBO
            {
               mat4 MVP;
            } global;

            #pragma stage vertex
            layout(location = 0) in vec4 Position;
            layout(location = 1) in vec2 TexCoord;
            layout(location = 0) out vec2 vTexCoord;

            void main()
            {
               gl_Position = global.MVP * Position;
               vTexCoord = vec2(TexCoord.x * {{echelleX}} + {{decalageX}}, TexCoord.y * {{echelleY}} + {{decalageY}});
            }

            #pragma stage fragment
            layout(location = 0) in vec2 vTexCoord;
            layout(location = 0) out vec4 FragColor;
            layout(set = 0, binding = 2) uniform sampler2D Source;

            void main()
            {
               FragColor = texture(Source, vTexCoord);
            }
            """;
        var glsl = $$"""
            // NelfePlay MULTIPLAYER : {{quoi}}, celui de la place de cette borne.
            #if defined(VERTEX)
            #if __VERSION__ >= 130
            #define COMPAT_VARYING out
            #define COMPAT_ATTRIBUTE in
            #else
            #define COMPAT_VARYING varying
            #define COMPAT_ATTRIBUTE attribute
            #endif
            COMPAT_ATTRIBUTE vec4 VertexCoord;
            COMPAT_ATTRIBUTE vec4 TexCoord;
            COMPAT_VARYING vec4 TEX0;
            uniform mat4 MVPMatrix;
            uniform vec2 TextureSize;
            uniform vec2 InputSize;
            void main()
            {
                gl_Position = MVPMatrix * VertexCoord;
                vec2 plein = InputSize / TextureSize;
                TEX0.xy = vec2(TexCoord.x * {{echelleX}} + plein.x * {{decalageX}}, TexCoord.y * {{echelleY}} + plein.y * {{decalageY}});
            }
            #elif defined(FRAGMENT)
            #if __VERSION__ >= 130
            #define COMPAT_VARYING in
            #define COMPAT_TEXTURE texture
            out vec4 FragColor;
            #else
            #define COMPAT_VARYING varying
            #define FragColor gl_FragColor
            #define COMPAT_TEXTURE texture2D
            #endif
            #ifdef GL_ES
            precision mediump float;
            #endif
            uniform sampler2D Texture;
            COMPAT_VARYING vec4 TEX0;
            void main()
            {
                FragColor = COMPAT_TEXTURE(Texture, TEX0.xy);
            }
            #endif
            """;
        return new Dictionary<string, string>
        {
            [nom + ".slang"] = slang + "\n",
            [nom + ".slangp"] = $"shaders = 1\nshader0 = {nom}.slang\nfilter_linear0 = false\nscale_type0 = viewport\nscale0 = 1.000000\n",
            [nom + ".glsl"] = glsl + "\n",
            [nom + ".glslp"] = $"shaders = 1\nshader0 = {nom}.glsl\nfilter_linear0 = false\nscale_type0 = viewport\nscale0 = 1.000000\n",
        };
    }
}
