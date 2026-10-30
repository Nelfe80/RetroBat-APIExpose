using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// Une ligne d'evenement du .MEM, telle que le wrapper la surveille. <paramref name="Type"/> : u8, u16le,
/// u16be... ; <paramref name="Masque"/> : applique a la valeur lue (bit_true) ; <paramref name="Score"/> : une
/// ligne de la section « scoring » (ou is_score=true), que l'auto-mute du wrapper ne coupe jamais.
/// </summary>
public sealed record LigneDuMem(long Adresse, string Condition, int? Valeur, string Action, bool Muette,
    string Type = "u8", long? Masque = null, bool Score = false);

/// <summary>
/// LES LIGNES DU .MEM QU'UN REGLEMENT PEUT CITER (CDC Speedruns v1.4, D7 et 3.2). Un defi ne lit que des
/// lignes deja surveillees : une ligne n'a pas d'identifiant, le reglement la designe par son action, son
/// adresse et, pour une condition « eq », sa valeur. Une ligne no_log ou no_survey n'est jamais emise par le
/// wrapper : citee par un reglement, elle ne parlerait jamais.
/// </summary>
public sealed class LignesDuMem
{
    private static readonly Regex Adresse = new(@"\baddress\s*=\s*(0[xX][0-9A-Fa-f]+)");
    private static readonly Regex Condition = new(@"\bcondition\s*=\s*[""']([a-z_]+)[""']", RegexOptions.IgnoreCase);
    private static readonly Regex Valeur = new(@"\bvalue\s*=\s*(0[xX][0-9A-Fa-f]+|\d+)");
    private static readonly Regex Action = new(@"\baction\s*=\s*[""']([A-Za-z0-9_]+)[""']");
    private static readonly Regex Muette = new(@"\bno_(log|survey)\s*=\s*true", RegexOptions.IgnoreCase);
    private static readonly Regex Type = new(@"\btype\s*=\s*[""'](u(?:8|16|32)(?:le|be)?)[""']", RegexOptions.IgnoreCase);
    private static readonly Regex Masque = new(@"\bmask\s*=\s*(0[xX][0-9A-Fa-f]+|\d+)");
    private static readonly Regex EstScore = new(@"\bis_score\s*=\s*true", RegexOptions.IgnoreCase);
    private static readonly Regex Section = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{\s*$");

    public IReadOnlyList<LigneDuMem> Lignes { get; }

    private LignesDuMem(IReadOnlyList<LigneDuMem> lignes) => Lignes = lignes;

    public static LignesDuMem Lire(string? mem)
    {
        var lignes = new List<LigneDuMem>();
        if (string.IsNullOrEmpty(mem)) return new LignesDuMem(lignes);
        // Les sections ouvertes : le wrapper prend pour categorie celle qui suit « events » (flow, scoring...).
        var sections = new List<string>();
        foreach (var brute in mem.Split('\n'))
        {
            var ligne = brute.Split("--", 2)[0];
            if (Section.Match(ligne) is { Success: true } ouverte) { sections.Add(ouverte.Groups[1].Value); continue; }
            if (ligne.Trim() is "}" or "},") { if (sections.Count > 0) sections.RemoveAt(sections.Count - 1); continue; }
            var adresse = Adresse.Match(ligne);
            var action = Action.Match(ligne);
            var condition = Condition.Match(ligne);
            if (!adresse.Success || !action.Success || !condition.Success) continue;
            var valeur = Valeur.Match(ligne);
            var type = Type.Match(ligne);
            var masque = Masque.Match(ligne);
            var evenements = sections.IndexOf("events");
            var categorie = evenements >= 0 && evenements + 1 < sections.Count ? sections[evenements + 1] : null;
            lignes.Add(new LigneDuMem(
                Hex.Lire(adresse.Groups[1].Value),
                condition.Groups[1].Value.ToLowerInvariant(),
                valeur.Success ? (int)Hex.Lire(valeur.Groups[1].Value) : null,
                action.Groups[1].Value,
                Muette.IsMatch(ligne),
                type.Success ? type.Groups[1].Value.ToLowerInvariant() : "u8",
                masque.Success ? Hex.Lire(masque.Groups[1].Value) : null,
                EstScore.IsMatch(ligne) || categorie == "scoring"));
        }

        return new LignesDuMem(lignes);
    }

    /// <summary>Les lignes emises (ni no_log ni no_survey) de cette adresse.</summary>
    public IEnumerable<LigneDuMem> Emises(long adresse) => Lignes.Where(l => l.Adresse == adresse && !l.Muette);

    /// <summary>L'adresse est-elle lue par au moins une ligne, emise ou non ?</summary>
    public bool Lit(long adresse) => Lignes.Any(l => l.Adresse == adresse);
}

/// <summary>Les nombres hexadecimaux du .MEM, des reglements et du wrapper : « 0XF00A », « 0x00F00A » et « 61450 » se valent.</summary>
public static class Hex
{
    public static long Lire(string texte)
    {
        var t = texte.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public static string Ecrire(long valeur) => "0x" + valeur.ToString("X", CultureInfo.InvariantCulture);
}
