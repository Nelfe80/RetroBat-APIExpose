using System.Collections.Concurrent;
using System.Text.Json;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// L'IDENTITE D'UN JEU CONSOLE PAR SON CONTENU (B, 18/11, accord du user : « generaliser tout ca pour ne plus avoir ces
/// probs »). Une seule reponse, calculee une fois par fichier, pour tous ceux qui ont besoin de savoir quel jeu est la ROM :
/// le wrapper (ligne <c>mem=</c> de certified.txt, wrapper 0.347), la cle du panneau de classement, les reglages certifies,
/// le prechargement du Deck. Avant, chacun cherchait par le nom du fichier et ils divergeaient : l'audit du 18/11 sur les
/// 3 015 ROM console de la borne a trouve 208 ROM sans .MEM (noms GoodNES : Mega Man, Metroid, Castlevania, SMB3 PRG 0...)
/// et 60 jeux dont le panneau prenait une autre cle que le .MEM charge.
/// <para>
/// La regle, dans cet ordre :
/// <list type="number">
/// <item>le md5 du fichier connu de l'alias.json du systeme : la reponse du wrapper d'aujourd'hui, gardee telle quelle ;</item>
/// <item>sinon le referentiel des gamelists (<c>resources\gamelist\systems</c>), par l'empreinte sans en-tete puis par le md5
/// du fichier, quand le groupe trouve a son .MEM : c'est la seule reponse a transmettre au wrapper ;</item>
/// <item>sinon rien : chacun garde sa recherche par le nom, comme avant.</item>
/// </list>
/// L'audit n'a trouve aucune ROM dont le .MEM d'aujourd'hui changerait. L'ARCADE N'EST PAS CONCERNEE : son set reste son
/// identite (decision du user).
/// </para>
/// </summary>
public sealed class IdentiteDesJeux
{
    /// <summary>D'ou vient la reponse.</summary>
    public enum Voie
    {
        /// <summary>Le md5 du fichier est dans alias.json : le wrapper trouve seul.</summary>
        AliasParEmpreinte,
        /// <summary>Le referentiel des gamelists : le wrapper ne trouverait pas seul, l'API le lui dit.</summary>
        Referentiel,
    }

    /// <summary>Le .MEM du jeu (son nom sans extension, qui est aussi son groupe), et comment il a ete trouve.</summary>
    public sealed record Identite(string Mem, Voie Voie, string? Md5, string? SansEnTete)
    {
        /// <summary>Vrai quand le wrapper ne le trouverait pas seul : la ligne <c>mem=</c> de certified.txt.</summary>
        public bool AImposerAuWrapper => Voie == Voie.Referentiel;
    }

    /// <summary>Au-dela, le contenu n'est pas lu (la fenetre de rcheevos ; un disque n'a pas d'empreinte de fichier).</summary>
    internal const long Plafond = 64L * 1024 * 1024;

    private readonly RomCanonicalResolver _canonique;
    private readonly string? _racineRam;
    private readonly ConcurrentDictionary<string, (string? Md5, string? SansEnTete)> _empreintes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Date, Dictionary<string, string> ParMd5)> _alias = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="racineRam">Le dossier des .MEM par systeme, pour un essai ; sinon celui du plugin.</param>
    public IdentiteDesJeux(RomCanonicalResolver canonique, string? racineRam = null)
    {
        _canonique = canonique;
        _racineRam = racineRam;
    }

    private string RacineRam => _racineRam ?? RetroBatPaths.RamResourcesRoot;

    /// <summary>
    /// Le jeu de ce fichier, ou null (arcade, fichier absent ou illisible, contenu inconnu). Le contenu n'est lu qu'une fois
    /// par fichier (chemin, taille, date) ; la decision, elle, se refait a chaque appel (un .MEM arrive avec le Data Pack).
    /// </summary>
    public Identite? Pour(string? systeme, string? chemin)
    {
        // Le dossier de la ROM sous roms\ d'abord, comme le wrapper : ES nomme parfois le systeme de son menu (une
        // collection), pas celui du jeu (mesure du 17/09, RetroArchWrapperProvider).
        var sys = (SystemeDuChemin(chemin) ?? systeme ?? string.Empty).Trim().ToLowerInvariant();
        if (sys.Length == 0 || string.IsNullOrWhiteSpace(chemin)) return null;
        if (RomCanonicalResolver.CanonicalScoringSystem(sys) == "arcade") return null;
        FileInfo info;
        try
        {
            info = new FileInfo(chemin);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var cle = sys + "|" + info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        var (md5, sansEnTete) = _empreintes.GetOrAdd(cle, _ => Empreintes(sys, info.FullName));
        return Decider(sys, md5, sansEnTete);
    }

    /// <summary>La decision, sur des empreintes deja connues (le rapporteur a celles que le wrapper a mesurees).</summary>
    public Identite? Decider(string systeme, string? md5, string? sansEnTete)
    {
        var sys = (systeme ?? string.Empty).Trim().ToLowerInvariant();
        if (sys.Length == 0 || RomCanonicalResolver.CanonicalScoringSystem(sys) == "arcade") return null;
        md5 = Normaliser(md5);
        sansEnTete = Normaliser(sansEnTete);

        if (md5 is not null && AliasParMd5(sys).TryGetValue(md5, out var parAlias))
        {
            // Le wrapper prend cette entree quoi qu'il arrive : la reponse est la sienne, ou rien s'il ne trouvera pas de .MEM.
            return MemExiste(sys, parAlias) ? new Identite(parAlias, Voie.AliasParEmpreinte, md5, sansEnTete) : null;
        }

        var canon = RomCanonicalResolver.CanonicalScoringSystem(sys);
        foreach (var empreinte in new[] { sansEnTete, md5 })
        {
            if (empreinte is null) continue;
            if (GroupeDuReferentiel(_canonique, canon, empreinte) is { } groupe && MemExiste(sys, groupe))
                return new Identite(groupe, Voie.Referentiel, md5, sansEnTete);
        }

        return null;
    }

    /// <summary>Le groupe que le referentiel des gamelists donne a cette empreinte (« nes/super-mario-bros-3 » donne
    /// « super-mario-bros-3 »), ou null.</summary>
    public static string? GroupeDuReferentiel(RomCanonicalResolver? canonique, string systeme, string empreinte)
    {
        var cle = canonique?.Resolve(systeme, empreinte)?.GameKey;
        var barre = cle?.IndexOf('/') ?? -1;
        return barre > 0 && barre < cle!.Length - 1 ? cle[(barre + 1)..] : null;
    }

    /// <summary>
    /// Un nom de .MEM qu'on peut ecrire dans certified.txt : lettres minuscules, chiffres, « - » et « _ », jamais un chemin.
    /// Le wrapper applique la meme regle a la lecture.
    /// </summary>
    public static bool NomDeMemSur(string? nom)
        => nom is { Length: > 0 and <= 120 } && nom.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>Le dossier qui suit « roms » dans le chemin (« E:\RetroBat\roms\nes\x.nes » donne « nes »), ou null.</summary>
    internal static string? SystemeDuChemin(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return null;
        var morceaux = chemin.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = morceaux.Length - 3; i >= 0; i--)
        {
            if (string.Equals(morceaux[i], "roms", StringComparison.OrdinalIgnoreCase)) return morceaux[i + 1];
        }
        return null;
    }

    private bool MemExiste(string sys, string mem)
        => NomDeMemSur(mem) && File.Exists(Path.Combine(RacineRam, sys, mem + ".MEM"));

    private static string? Normaliser(string? empreinte)
    {
        var e = (empreinte ?? string.Empty).Trim().ToLowerInvariant();
        return e.Length == 32 ? e : null;
    }

    private static (string? Md5, string? SansEnTete) Empreintes(string sys, string chemin)
    {
        try
        {
            using var contenu = ContenuDUneRom.Ouvrir(chemin, Plafond);
            if (contenu is null) return (null, null);
            var (md5, sans) = EmpreinteSansEnTete.Empreintes(EmpreinteSansEnTete.RegleDu(sys), contenu.Flux, contenu.Taille);
            return (Normaliser(md5), Normaliser(sans));
        }
        catch (Exception ex) when (ContenuDUneRom.EstUneLectureRatee(ex))
        {
            return (null, null);
        }
    }

    /// <summary>Les entrees md5 de l'alias.json du systeme, relues quand le fichier change (Data Pack).</summary>
    private Dictionary<string, string> AliasParMd5(string sys)
    {
        var chemin = Path.Combine(RacineRam, sys, "alias.json");
        DateTime date;
        try
        {
            date = File.Exists(chemin) ? File.GetLastWriteTimeUtc(chemin) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            date = DateTime.MinValue;
        }

        if (_alias.TryGetValue(sys, out var connu) && connu.Date == date) return connu.ParMd5;
        var parMd5 = new Dictionary<string, string>(StringComparer.Ordinal);
        if (date != DateTime.MinValue)
        {
            try
            {
                using var flux = File.OpenRead(chemin);
                var tout = JsonSerializer.Deserialize<Dictionary<string, string>>(flux) ?? [];
                foreach (var (k, v) in tout)
                {
                    if (k.Length == 32 && k.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(v)) parMd5.TryAdd(k.ToLowerInvariant(), v.Trim());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // illisible : comme sans alias, le referentiel decide
            }
        }

        _alias[sys] = (date, parMd5);
        return parMd5;
    }
}
