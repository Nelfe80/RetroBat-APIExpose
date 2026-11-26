using System.IO.Compression;
using System.Xml;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LE BIOS QUE FBNEO EXIGE ET QUI MANQUE (26/11, Rainbow Islands). `rbisland` se lance sous FBNeo seulement avec
/// `cchip.zip` (C-Chip de Taito, `cchip_upd78c11.bin` crc 43021521) : sans lui le coeur refuse le set, RetroArch se ferme et le
/// joueur n'a aucune explication. Le DAT de FBNeo que RetroBat livre (`bios\fbneo`) dit, set par set, son BIOS (`romof`, jeu
/// `isbios="yes"` : cchip, neogeo, pgm...). Ce controle lit ce DAT, cherche le zip du BIOS la ou FBNeo le cherche et, s'il
/// manque, le dit au joueur (plaque du Deck ou bandeau du haut). On NE DISTRIBUE AUCUN fichier de BIOS ni de ROM : le message
/// nomme le fichier et le dossier. Il n'EMPECHE rien : un faux positif ne coute qu'un message, jamais un lancement.
/// Les calculs sont purs (<see cref="Chercher"/>), le DAT se lit une fois (cache sur la date du fichier).
/// </summary>
public sealed class ControleBiosFbneo
{
    public const string Cle = "scoring.msg.bios_missing";

    /// <summary>Le message quand le dictionnaire de la borne n'a pas encore la cle (Data Pack en retard).</summary>
    public const string TexteParDefaut = "This game needs the BIOS file {file} ({content}), which was not found. Copy it into {folder} "
        + "and start the game again. NelfePlay does not provide BIOS files.";

    public const string DatArcade = "FinalBurn Neo (ClrMame Pro XML, Arcade only).dat";

    /// <summary>Un BIOS exige par un set et absent de tous les dossiers ou FBNeo le cherche.</summary>
    public sealed record BiosAbsent(string Set, string Bios, string Fichier, string Contenu, string Dossier);

    /// <summary>Ce que le DAT dit d'un jeu : son parent (`romof`) et, pour un BIOS, ses fichiers (nom, crc).</summary>
    internal sealed record Entree(string? RomOf, bool EstBios, List<(string Nom, string Crc)> Roms);

    private static readonly object _verrouDat = new();
    private static string? _datCharge;
    private static DateTime _datDate;
    private static Dictionary<string, Entree>? _dat;

    private readonly InterfaceTextService? _textes;
    private readonly IEsSettingsStore? _es;
    private readonly PartieNelfePlayService? _partie;
    private readonly ILogger<ControleBiosFbneo>? _journal;
    private readonly object _verrou = new();
    private readonly Dictionary<string, DateTime> _dit = new(StringComparer.OrdinalIgnoreCase);

    public ControleBiosFbneo(InterfaceTextService? textes = null, IEsSettingsStore? es = null, PartieNelfePlayService? partie = null,
        ILogger<ControleBiosFbneo>? journal = null)
    {
        _textes = textes;
        _es = es;
        _partie = partie;
        _journal = journal;
    }

    /// <summary>Couture des essais : le dossier du DAT, les dossiers de BIOS, l'annonce, le verdict NelfePlay.</summary>
    internal string? DossierDuDat { get; set; }
    internal IReadOnlyList<string>? DossiersDeBios { get; set; }
    internal Action<RetroBat.Api.Deck.DeckNotice, string>? Annonce { get; set; }
    internal Func<string?, string?>? Verdict { get; set; }

    /// <summary>Les dossiers ou FBNeo cherche le zip d'un BIOS : celui de la ROM, roms\fbneo, bios\fbneo, bios. Pur.</summary>
    internal static IReadOnlyList<string> Dossiers(string cheminRom, string racine)
    {
        var liste = new List<string>();
        void Ajouter(string? d)
        {
            if (!string.IsNullOrWhiteSpace(d) && !liste.Contains(d, StringComparer.OrdinalIgnoreCase)) liste.Add(d);
        }

        Ajouter(Path.GetDirectoryName(cheminRom));
        Ajouter(Path.Combine(racine, "roms", "fbneo"));
        Ajouter(Path.Combine(racine, "bios", "fbneo"));
        Ajouter(Path.Combine(racine, "bios"));
        return liste;
    }

    /// <summary>
    /// Les BIOS que le set exige (chaine `romof` du DAT) et qui manquent. Un BIOS est la quand un zip de son nom, dans un des
    /// <paramref name="dossiers"/>, porte au moins un de ses fichiers (par le CRC : Neo-Geo a plusieurs variantes, une
    /// suffit), ou quand le zip du jeu lui-meme les porte. Un zip illisible ou en .7z, un set absent du DAT : rien a dire.
    /// </summary>
    internal static IReadOnlyList<BiosAbsent> Chercher(string set, IReadOnlyList<string> dossiers, string cheminRom,
        Dictionary<string, Entree> dat)
    {
        var absents = new List<BiosAbsent>();
        if (!dat.TryGetValue(set, out var jeu)) return absents;

        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { set };
        var dispoDuJeu = File.Exists(cheminRom) ? CrcsDuZip(cheminRom) : null;
        var membre = jeu;
        for (var i = 0; i < 6 && membre.RomOf is { Length: > 0 } parent && vus.Add(parent) && dat.TryGetValue(parent, out var suivant); i++)
        {
            membre = suivant;
            if (!membre.EstBios || membre.Roms.Count == 0) continue;
            var crcs = membre.Roms.Select(r => r.Crc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var trouve = dispoDuJeu is not null && crcs.Overlaps(dispoDuJeu);
            var illisible = false;
            foreach (var dossier in dossiers)
            {
                if (trouve) break;
                var zip = Path.Combine(dossier, parent + ".zip");
                if (!File.Exists(zip)) continue;
                var dispo = CrcsDuZip(zip);
                if (dispo is null) { illisible = true; continue; }
                trouve = crcs.Overlaps(dispo);
            }

            if (trouve || illisible) continue;
            // FBNeo lit aussi les .7z : on ne tranche pas.
            if (dossiers.Any(d => File.Exists(Path.Combine(d, parent + ".7z")))) continue;
            absents.Add(new BiosAbsent(set, parent, parent + ".zip", string.Join(", ", membre.Roms.Select(r => r.Nom).Take(3)),
                Path.Combine("bios", "fbneo")));
        }

        return absents;
    }

    internal static HashSet<string>? CrcsDuZip(string chemin)
    {
        if (!string.Equals(Path.GetExtension(chemin), ".zip", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(chemin);
            return zip.Entries.Where(e => e.Length > 0).Select(e => e.Crc32.ToString("x8")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Le DAT d'arcade de FBNeo, lu une fois ; null s'il manque ou est illisible.</summary>
    internal static Dictionary<string, Entree>? LireLeDat(string dossier)
    {
        var chemin = Path.Combine(dossier, DatArcade);
        try
        {
            if (!File.Exists(chemin)) return null;
            var date = File.GetLastWriteTimeUtc(chemin);
            lock (_verrouDat)
            {
                if (_dat is null || !string.Equals(_datCharge, chemin, StringComparison.OrdinalIgnoreCase) || _datDate != date)
                {
                    _dat = Parser(chemin);
                    _datCharge = chemin;
                    _datDate = date;
                }

                return _dat;
            }
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, Entree> Parser(string chemin)
    {
        var jeux = new Dictionary<string, Entree>(StringComparer.OrdinalIgnoreCase);
        using var lecteur = XmlReader.Create(chemin, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        Entree? courant = null;
        while (lecteur.Read())
        {
            if (lecteur.NodeType == XmlNodeType.Element)
            {
                if (lecteur.Name == "game" && lecteur.GetAttribute("name") is { Length: > 0 } nom)
                {
                    var bios = lecteur.GetAttribute("isbios") == "yes";
                    courant = new Entree(lecteur.GetAttribute("romof"), bios, []);
                    jeux[nom] = courant;
                    if (lecteur.IsEmptyElement) courant = null;
                }
                else if (lecteur.Name == "rom" && courant is { EstBios: true } && lecteur.GetAttribute("crc") is { Length: > 0 } crc
                         && lecteur.GetAttribute("status") != "nodump")
                {
                    courant.Roms.Add((lecteur.GetAttribute("name") ?? "", crc.ToLowerInvariant()));
                }
            }
            else if (lecteur.NodeType == XmlNodeType.EndElement && lecteur.Name == "game")
            {
                courant = null;
            }
        }

        return jeux;
    }

    /// <summary>La plaque du Deck (texte court, anglais, majuscules a l'ecran). Pur.</summary>
    internal static RetroBat.Api.Deck.DeckNotice Plaque(BiosAbsent a)
        => new("scoring.bios_missing", RetroBat.Api.Deck.DeckSeverity.Alert, "BIOS MISSING",
            ("ADD " + a.Fichier + " TO " + a.Dossier).ToUpperInvariant(), null, RetroBat.Api.Deck.DeckNotices.Long)
        { Source = "platform" };

    /// <summary>Le texte long du bandeau, dans la langue du joueur ; l'anglais si la cle manque au dictionnaire. Pur.</summary>
    internal static string Message(BiosAbsent a, InterfaceTextService? textes, string? langue)
    {
        var modele = textes?.Text(Cle, langue);
        if (string.IsNullOrWhiteSpace(modele) || modele == Cle) modele = TexteParDefaut;
        return modele.Replace("{file}", a.Fichier).Replace("{content}", a.Contenu).Replace("{folder}", a.Dossier);
    }

    /// <summary>
    /// A CHAQUE LANCEMENT D'UNE PARTIE DU WORLD SCORING (hook game-start d'ES, defi lance par l'API) : le jeu exige-t-il un BIOS
    /// que la borne n'a pas ? Rend le BIOS manquant annonce, ou null. Jamais d'exception, jamais de blocage.
    /// </summary>
    public BiosAbsent? Verifier(string? cheminRom)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cheminRom)
                || !string.Equals(Path.GetExtension(cheminRom), ".zip", StringComparison.OrdinalIgnoreCase)) return null;
            if ((Verdict ?? (c => _partie?.Prevoir(c)))(cheminRom) is null) return null;

            var racine = RetroBatPaths.RetroBatRoot;
            var dat = LireLeDat(DossierDuDat ?? Path.Combine(racine, "bios", "fbneo"));
            if (dat is null) return null;
            var set = Path.GetFileNameWithoutExtension(cheminRom);
            var absent = Chercher(set, DossiersDeBios ?? Dossiers(cheminRom, racine), cheminRom, dat).FirstOrDefault();
            if (absent is null) return null;

            lock (_verrou)
            {
                if (_dit.TryGetValue(set, out var quand) && DateTime.UtcNow - quand < TimeSpan.FromSeconds(30)) return absent;
                _dit[set] = DateTime.UtcNow;
            }

            _journal?.LogWarning("Prevol : {Set} exige le BIOS {Fichier} ({Contenu}), introuvable dans le dossier du jeu, roms\\fbneo, bios\\fbneo ni bios : FBNeo refusera le jeu. Joueur prevenu.",
                set, absent.Fichier, absent.Contenu);
            var plaque = Plaque(absent);
            var texte = Message(absent, _textes, Langue());
            if (Annonce is { } essai) { essai(plaque, texte); return absent; }
            NotificationsRetroArch.Bandeau?.ShowTop("BIOS", texte, null, 20000, true, plaque);
            return absent;
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Prevol BIOS FBNeo ignore.");
            return null;
        }
    }

    private string Langue()
    {
        try
        {
            if (_es is not null && _es.ReadAllSettings().TryGetValue("Language", out var l) && !string.IsNullOrWhiteSpace(l)) return l;
        }
        catch (Exception)
        {
            // Des reglages illisibles : l'anglais.
        }

        return "en";
    }
}
