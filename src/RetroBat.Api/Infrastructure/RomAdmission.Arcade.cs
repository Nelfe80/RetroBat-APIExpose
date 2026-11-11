using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES CLONES OFFICIELS D'ARCADE (decision du user du 11/11, apres le rapport de Gogerad). Le set PARENT et ses CLONES
/// OFFICIELS du meme jeu entrent, d'apres le referentiel : le parent d'un clone est son « grp » dans fbneo_lt.json (le
/// « cloneof » des DAT), et la famille est celle d'un set que le .MEM nomme. 19xxu, tokiu et mslug3h entrent ainsi sous
/// 19xx, toki et mslug3.
/// <para>Restent dehors :</para>
/// <list type="bullet">
/// <item>les bootlegs et les hacks (marques du referentiel, ou nom : « bootleg », « hack », « decrypted », « Supercharger »,
/// « C64 Music », « for Bobble Bobble PCB », « Lost Cave »…), les prototypes et les tests de lieu ;</item>
/// <item>les sets d'une autre machine qui n'entraient que par un sha1 partage : ils ne sont pas des clones (doubledr, Double
/// Dragon Neo-Geo, a son propre groupe ; pc_* PlayChoice-10 ; mt_* Mega-Tech) ;</item>
/// <item>un clone au TITRE different du set nomme (Juuouki, JuJu Densetsu, Argus no Senshi, Gryzor, Battle Garegga Type 2,
/// DaiOuJou sans Black Label, TGM2 Plus) : il peut jouer autrement (region, difficulte, regles) ; il n'entre qu'une fois
/// prouve sur les adresses du .MEM, c'est-a-dire nomme par lui.</item>
/// </list>
/// </summary>
public static partial class RomAdmission
{
    /// <summary>Un set d'arcade du referentiel : son titre (« n »), son parent (« grp » d'un clone), ses marques, son nom long.</summary>
    public sealed record SetArcade(string Set, string? Titre, string? Parent, bool EstUnClone, IReadOnlyList<string> Drapeaux, string? Description);

    /// <summary>Le referentiel arcade, injectable pour les tests.</summary>
    public interface IReferentielArcade
    {
        SetArcade? ParSet(string set);

        /// <summary>Le parent et tous ses clones.</summary>
        IReadOnlyList<SetArcade> Famille(string parent);
    }

    private static readonly Regex NomExcluArcade = new(
        @"bootleg|boolteg|\bhack|prototype|\bproto\b|location test|modular system|supercharger|playchoice|mega-tech|mega play|"
        + @"conversion|decrypted|c64 music|lost cave|for bobble bobble pcb|pirate|\bdemo\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Le verdict sur un set d'arcade que le .MEM ne nomme PAS : un clone officiel d'un set nomme, ou rien.
    /// <paramref name="nomme"/> dit si le .MEM nomme un set. Pur hors du referentiel.
    /// </summary>
    public static Decision JugerCloneArcade(string set, Func<string, bool> nomme, IReferentielArcade referentiel)
    {
        var s = referentiel.ParSet(set);
        if (s is null) return new Decision(Statut.Exclue, "set inconnu du référentiel", set, null);
        if (s.Drapeaux.Any(d => d is "bootleg" or "hack" or "pirate"))
            return new Decision(Statut.Exclue, "bootleg ou hack (" + s.Description + ")", set, null);
        if (!s.EstUnClone || string.IsNullOrEmpty(s.Parent))
            return new Decision(Statut.Exclue, "set d'un autre jeu ou d'une autre machine, que la définition de score ne nomme pas", set, null);
        if (NomExcluArcade.IsMatch(s.Description ?? ""))
            return new Decision(Statut.Exclue, "bootleg, hack ou prototype (" + s.Description + ")", set, null);
        var famille = referentiel.Famille(s.Parent);
        var reference = famille.FirstOrDefault(f => nomme(f.Set));
        if (reference is null)
            return new Decision(Statut.Exclue, "clone d'un set que la définition de score ne nomme pas", set, null);
        // Le titre, une fois slugifie : « Ketsui: Kizuna Jigoku Tachi » et « Ketsui Kizuna Jigoku Tachi » sont le meme.
        if (!string.Equals(GamelistIdentity.Slugifier(reference.Titre), GamelistIdentity.Slugifier(s.Titre), StringComparison.Ordinal))
            return new Decision(Statut.Exclue, $"version au titre différent (« {s.Titre} » face à « {reference.Titre} ») : elle peut jouer "
                + "autrement, elle n'entre que prouvée sur la définition de score", set, reference.Set);
        return new Decision(Statut.Admise, $"clone officiel de {reference.Set}", set, reference.Set);
    }

    /// <summary>Le referentiel arcade de la borne (fbneo_lt.json d'abord, puis arcade_lt.json), lu a la demande, en cache.</summary>
    public sealed class ReferentielArcadeSurDisque : IReferentielArcade
    {
        public static ReferentielArcadeSurDisque Borne { get; } = new(Path.Combine(AppContext.BaseDirectory, "resources", "gamelist", "systems"));

        private readonly string _racine;
        private readonly ConcurrentDictionary<string, SetArcade?> _sets = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyList<SetArcade>> _familles = new(StringComparer.OrdinalIgnoreCase);

        public ReferentielArcadeSurDisque(string racine) => _racine = racine;

        public SetArcade? ParSet(string set)
            => _sets.GetOrAdd(set, s => Lignes("\"" + s + "\"").Select(Lire).FirstOrDefault(x => x is not null
                && string.Equals(x.Set, s, StringComparison.OrdinalIgnoreCase)));

        public IReadOnlyList<SetArcade> Famille(string parent)
            => _familles.GetOrAdd(parent, p => Lignes("\"" + p + "\"").Select(Lire)
                .Where(x => x is not null && (string.Equals(x.Set, p, StringComparison.OrdinalIgnoreCase)
                    || (x.EstUnClone && string.Equals(x.Parent, p, StringComparison.OrdinalIgnoreCase))))
                .GroupBy(x => x!.Set, StringComparer.OrdinalIgnoreCase).Select(g => g.First()!).ToList());

        private IEnumerable<string> Lignes(string aiguille)
        {
            foreach (var systeme in new[] { "fbneo", "arcade" })
            {
                var chemin = Path.Combine(_racine, systeme + "_lt.json");
                if (!File.Exists(chemin)) continue;
                IEnumerable<string> lignes;
                try { lignes = File.ReadLines(chemin).Where(l => l.IndexOf(aiguille, StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var l in lignes) yield return l;
            }
        }

        internal static SetArcade? Lire(string ligne)
        {
            try
            {
                using var doc = JsonDocument.Parse(ligne);
                var r = doc.RootElement;
                string? T(string cle) => r.TryGetProperty(cle, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var set = T("set") ?? T("id");
                if (string.IsNullOrEmpty(set)) return null;
                var drapeaux = new List<string>();
                if (r.TryGetProperty("flg", out var flg) && flg.ValueKind == JsonValueKind.Array)
                    drapeaux.AddRange(flg.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant()));
                if (string.Equals(T("t"), "hack", StringComparison.OrdinalIgnoreCase)) drapeaux.Add("hack");
                if (T("rk") is { Length: > 0 } rk) drapeaux.Add(rk.ToLowerInvariant());
                return new SetArcade(set, T("n"), T("grp"), string.Equals(T("role"), "clone", StringComparison.OrdinalIgnoreCase), drapeaux, T("fn"));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
