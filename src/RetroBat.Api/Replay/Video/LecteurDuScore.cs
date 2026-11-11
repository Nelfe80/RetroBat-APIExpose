using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE SCORE LU DANS LA MEMOIRE DU COEUR PENDANT LE RENDU D'UN FILM (controle de fidelite, 09/11). Le rendu tourne sans
/// wrapper, donc sans scoring : on lit les lignes de score du .MEM du jeu (section « scoring », joueur 1) par
/// <c>READ_CORE_RAM</c>, et on les compose avec l'agregateur du score en direct (<see cref="LiveScoreAggregatorProvider"/>),
/// le meme qui compose le score de la partie : poids des moities d'un score 32 bits, BCD, chiffres par octet, masques.
/// Une instance d'agregateur a elle, sur un bus a elle : rien ne part dans le scoring de la borne.
///
/// L'ADRESSE DANS LE COEUR : READ_CORE_RAM lit la memoire systeme que le coeur expose, la meme que lit le wrapper sous
/// FBNeo (sonde du 09/11 : Battle Garegga, 0x166C a 0x166F = 01 00 4C 71, soit 94 540, le score certifie) et sous les coeurs
/// NES (memoire de la console a partir de 0), Genesis Plus GX et snes9x (11/11, voir <see cref="CoeurReleve"/>). Les autres
/// coeurs ne sont pas encore releves : pas de lecteur, le film est alors « non verifie ».
/// </summary>
public sealed class LecteurDuScore : IDisposable
{
    /// <summary>Une ligne de score du .MEM : adresse, octets a lire, type et masque.</summary>
    /// <param name="Places">Les index de ses octets dans la memoire que lit READ_CORE_RAM, dans l'ordre de l'adresse
    /// (<see cref="AdressesDuCoeur"/>) ; null : l'adresse telle quelle.</param>
    public sealed record LigneDeScore(long Adresse, int Octets, string Type, long? Masque, string Description,
        IReadOnlyList<long>? Places = null)
    {
        /// <summary>Les index a lire : <see cref="Places"/>, ou l'adresse et les octets qui suivent.</summary>
        public IReadOnlyList<long> Index => Places ?? Enumerable.Range(0, Octets).Select(i => Adresse + i).ToList();
    }

    private readonly SimpleEventBus _bus = new();
    private readonly LiveScoreAggregatorProvider _agregateur;
    private readonly IDisposable _abonnement;
    private readonly string _systeme;
    private readonly string _rom;
    private readonly string _definition;
    private readonly object _verrou = new();
    private long? _score;

    public IReadOnlyList<LigneDeScore> Lignes { get; }

    private LecteurDuScore(string systeme, string rom, string definition, IReadOnlyList<LigneDeScore> lignes)
    {
        _systeme = systeme; _rom = rom; _definition = definition; Lignes = lignes;
        _agregateur = new LiveScoreAggregatorProvider(_bus, NullLogger<LiveScoreAggregatorProvider>.Instance);
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        _agregateur.StartAsync().GetAwaiter().GetResult();
    }

    /// <summary>Le lecteur du score de ce replay, ou null (coeur non releve, .MEM absent, aucune ligne de score).</summary>
    public static LecteurDuScore? Pour(ReplayManifest manifest)
    {
        if (!CoeurReleve(manifest.Runtime.CoreName)) return null;
        var rom = manifest.Game.RomGroup;
        if (string.IsNullOrWhiteSpace(rom)) return null;
        foreach (var systeme in SystemesDuMem(manifest.Game.SystemFolder, manifest.Game.SystemId))
        {
            var fichier = RamDefinitionLayers.PickDefinitionFile(systeme, rom);
            string texte;
            try
            {
                if (string.IsNullOrEmpty(fichier) || !File.Exists(fichier)) continue;
                texte = File.ReadAllText(fichier);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            return Depuis(systeme, rom, fichier, texte, manifest.Runtime.CoreName);
        }
        return null;
    }

    /// <summary>Le lecteur d'un .MEM deja lu (<paramref name="mem"/>, fichier <paramref name="fichier"/>), ou null sans ligne de score.</summary>
    internal static LecteurDuScore? Depuis(string systeme, string rom, string fichier, string mem, string? coeur = null)
    {
        // (11/11) une adresse MAME d'arcade se lit ou FBNeo la range ; une ligne qu'on ne sait pas placer est ecartee, et sans
        // aucune ligne lisible, pas de lecteur (« non verifie » plutot qu'un faux « desync »).
        var banques = AdressesDuCoeur.FamilleFbneo(coeur) ? AdressesDuCoeur.BanquesDe(rom) : [];
        var lignes = LignesDuScore(mem)
            .Select(l => l with { Places = AdressesDuCoeur.Places(coeur, l.Adresse, l.Octets, banques) })
            .Where(l => l.Places is not null)
            .ToList();
        return lignes.Count == 0 ? null : new LecteurDuScore(systeme, rom, fichier, lignes);
    }

    /// <summary>
    /// Les coeurs dont l'adresse du .MEM est celle de READ_CORE_RAM : FBNeo (memoire systeme, mots 68000 deja dans l'ordre
    /// que lit le wrapper) et les coeurs NES. Pur.
    /// </summary>
    /// <remarks>
    /// (11/11) Genesis Plus GX et snes9x : sans succes, READ_CORE_RAM lit la memoire systeme du coeur a l'adresse 0
    /// (rcheevos, console 0), le meme repere que les .MEM (index bruts de GPGX, sans ^1 ; WRAM du SNES). Prouve sur la
    /// borne : Streets of Rage 2, 0xEF96/97/99 = 3 960 exact ; Super Metroid, 0x0998 = 0x08 en jeu, salle 0x079B = 0xA59F
    /// (Kraid), energie 0x09C2 jusqu'a la mort. Les coeurs des fantomes (_ghosts) ont la meme memoire que leur coeur
    /// d'origine. MAME n'expose aucune memoire a READ_CORE_RAM et n'enregistre pas de replay (RetroArch 1.22.2 plante en
    /// encodant son etat) : rien a lire.
    /// </remarks>
    internal static bool CoeurReleve(string? coeur)
        => (coeur ?? "").Trim().ToLowerInvariant() is "fbneo" or "fbalpha" or "fbalpha2012"
            or "fceumm" or "fceumm_ghosts" or "nestopia" or "mesen"
            or "genesis_plus_gx" or "genesis_plus_gx_ghosts" or "genesis_plus_gx_wide" or "genesis_plus_gx_expanded"
            or "snes9x" or "snes9x_ghosts";

    /// <summary>Les dossiers de .MEM ou chercher le jeu : l'arcade pour les dossiers d'arcade, sinon le dossier lui-meme. Pur.</summary>
    internal static IEnumerable<string> SystemesDuMem(string? dossier, string? systeme)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var brut in new[] { dossier, systeme })
        {
            var s = (brut ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) continue;
            var d = s is "fbneo" or "fb_alpha" or "fbalpha" or "mame" or "neogeo" or "cps1" or "cps2" or "cps3" or "arcade" ? "arcade" : s;
            if (vus.Add(d)) yield return d;
        }
    }

    /// <summary>Les lignes de score du joueur 1 (section « scoring » ou is_score, action SCORE_STATE). Pur.</summary>
    internal static IReadOnlyList<LigneDeScore> LignesDuScore(string mem)
    {
        var descriptions = new Dictionary<long, string>();
        foreach (var brute in mem.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(brute.Split("--", 2)[0],
                @"\baddress\s*=\s*(0[xX][0-9A-Fa-f]+).*?\bdesc\s*=\s*""([^""]*)""");
            if (m.Success) descriptions.TryAdd(Hex.Lire(m.Groups[1].Value), m.Groups[2].Value);
        }
        return LignesDuMem.Lire(mem).Lignes
            .Where(l => l.Score && string.Equals(l.Action, "SCORE_STATE", StringComparison.OrdinalIgnoreCase) && l.Joueur is null or 1)
            .GroupBy(l => l.Adresse)
            .Select(g => g.First())
            .Select(l => new LigneDeScore(l.Adresse, Octets(l.Type), l.Type, l.Masque, descriptions.GetValueOrDefault(l.Adresse, "")))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l.Description, @"\b(top|high|hi)[\s-]*score\b|leaderboard",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();
    }

    internal static int Octets(string type) => type.ToLowerInvariant() switch
    {
        "u16le" or "u16be" or "u16" => 2,
        "u24le" or "u24be" or "u24" => 3,
        "u32le" or "u32be" or "u32" => 4,
        _ => 1,
    };

    /// <summary>La valeur d'une ligne a partir des octets lus, comme le wrapper la lit (ordre, masque). Pur.</summary>
    internal static long Valeur(LigneDeScore ligne, IReadOnlyList<int> octets)
    {
        long v = 0;
        var grosBoutiste = ligne.Type.EndsWith("be", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Min(ligne.Octets, octets.Count); i++)
        {
            var o = grosBoutiste ? octets[i] : octets[Math.Min(ligne.Octets, octets.Count) - 1 - i];
            v = (v << 8) | (uint)(o & 0xFF);
        }
        return ligne.Masque is long m ? v & m : v;
    }

    /// <summary>La reponse de READ_CORE_RAM (« READ_CORE_RAM 166c 01 00 4C 71 ») en octets, ou null (« -1 », illisible). Pur.</summary>
    internal static IReadOnlyList<int>? OctetsDeLaReponse(string? reponse, int attendus)
    {
        var champs = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (champs is null || champs.Length < 2 + attendus || champs[0] != "READ_CORE_RAM") return null;
        var octets = new List<int>(attendus);
        for (var i = 0; i < attendus; i++)
        {
            if (!int.TryParse(champs[2 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var o) || o is < 0 or > 255)
                return null;
            octets.Add(o);
        }
        return octets;
    }

    /// <summary>
    /// Compose le score a partir des valeurs lues (une par ligne, dans l'ordre de <see cref="Lignes"/>) ; null tant que
    /// l'agregateur n'a rien compose. Une ligne illisible garde sa derniere valeur.
    /// </summary>
    public long? Composer(IReadOnlyList<long?> valeurs)
    {
        for (var i = 0; i < Lignes.Count && i < valeurs.Count; i++)
        {
            if (valeurs[i] is not long v) continue;
            var l = Lignes[i];
            var adresse = "0x" + l.Adresse.ToString("X", CultureInfo.InvariantCulture);
            var brut = "0x" + v.ToString("X", CultureInfo.InvariantCulture);
            var payload = new
            {
                Source = "film.rendu",
                SystemId = _systeme,
                Rom = _rom,
                DefinitionFile = _definition,
                signal = new
                {
                    Key = "SCORE_STATE", Channel = "ACTION", Name = "SCORE_STATE", SourceDescription = l.Description,
                    Address = adresse, RawValueHex = brut, Value = v,
                },
                actionType = "SCORE_STATE",
                sourceCategory = l.Description,
                Value = v,
                Address = adresse,
                RawValueHex = brut,
                player = 1,
            };
            _bus.PublishAsync(new EventEnvelope { Type = "ingame.memory.changed", Payload = payload }).GetAwaiter().GetResult();
        }
        lock (_verrou) return _score;
    }

    private void SurEvenement(EventEnvelope e)
    {
        if (e.Type != "score.live.changed" || e.Payload is not ScoreLiveEvent s || s.Player != 1) return;
        if (!string.Equals(s.ScoreKind, "game", StringComparison.OrdinalIgnoreCase)) return;
        lock (_verrou) _score = s.Score;
    }

    public void Dispose()
    {
        _abonnement.Dispose();
        _agregateur.StopAsync().GetAwaiter().GetResult();
    }
}
