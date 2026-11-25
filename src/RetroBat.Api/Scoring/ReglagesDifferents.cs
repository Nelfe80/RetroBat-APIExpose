using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LES REGLAGES DE JEU QUI DIFFERENT DU PROFIL (25/11/2026, decision du user « Quorum : ok »). Le site ne recoit de la borne
/// qu'une EMPREINTE des reglages (<c>core_options_digest</c>) : il ne sait pas dire quelle option a change, et ne pouvait
/// qu'attendre un quorum de 2 comptes (<c>settings.unknown</c>). Cette classe PURE calcule la liste claire de ce qui differe
/// des valeurs attendues du profil ouvert, pour que le site tranche seul (sans effet : admis ; reglage de jeu : refus avec
/// la raison ; cle jamais vue : attente + resume a l'admin).
///
/// Le champ est HORS SIGNATURE : le passeport est signe en entier (JCS, tous ses champs au premier niveau) et son schema public
/// (NelfeScoringProtocol) interdit les champs inconnus ; y ajouter une cle casserait la verification des vieux sites et du
/// protocole. La liste est donc AUTOCONTROLEE : le site reconstruit les paires « cle=valeur » (valeurs attendues de l'entree
/// <c>expected_entry</c> du profil, surchargees par la liste), les trie, les joint par « ; » et compare leur SHA-256 au
/// <c>core_options_digest</c> signe du passeport. Une liste qui ne reconstruit pas l'empreinte signee ne vaut rien.
///
/// Elle porte les reglages qui font l'empreinte (apres <c>FilterGameplayCoreOptions</c> : la liste blanche de gameplay),
/// jamais l'affichage, le son ni les manettes. Aucune donnee personnelle : des noms d'options de cœur et leurs valeurs.
/// </summary>
public static class ReglagesDifferents
{
    /// <summary>Version du format du champ.</summary>
    public const int Version = 1;

    /// <summary>Nombre maximal d'entrees portees ; au-dela, <c>truncated</c> vaut vrai (le site ne peut plus reconstruire).</summary>
    public const int MaxEntrees = 96;

    /// <summary>Longueur maximale d'une cle ou d'une valeur (le reste est coupe, <c>truncated</c> vaut vrai).</summary>
    public const int MaxTexte = 160;

    /// <summary>Une difference : <c>Value</c> nulle = cle attendue mais absente de la borne ; <c>Expected</c> nulle = cle inconnue du profil.</summary>
    public sealed record Difference(string Key, string? Value, string? Expected, string? Default, bool Cheat);

    /// <summary>Les valeurs « cle -> valeur » d'une chaine canonique « cle=valeur;cle=valeur ». Pur.</summary>
    public static Dictionary<string, string> Paires(string? options)
    {
        var sortie = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var paire in (options ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = paire.IndexOf('=');
            if (eq <= 0) continue;
            sortie[paire[..eq]] = paire[(eq + 1)..];
        }
        return sortie;
    }

    /// <summary>
    /// L'entree de <c>core_options_expected</c> qui vise ce cœur (<c>cores</c> contient son SHA-256, ou n'est pas dite) et dont
    /// les valeurs sont les plus proches des reglages joues (le profil peut lister plusieurs jeux de reglages d'usine).
    /// Null si aucune entree ne vise ce cœur : tout ce que la borne a alors est « inconnu du profil ».
    /// </summary>
    public static (int Index, Dictionary<string, string> Valeurs)? EntreeAttendue(JsonElement profil, string? coeurSha256, IReadOnlyDictionary<string, string> joues)
    {
        if (profil.ValueKind != JsonValueKind.Object
            || !profil.TryGetProperty("core_options_expected", out var attendus) || attendus.ValueKind != JsonValueKind.Array) return null;
        (int Index, Dictionary<string, string> Valeurs)? meilleure = null;
        var meilleurEcart = int.MaxValue;
        var index = -1;
        foreach (var entree in attendus.EnumerateArray())
        {
            index++;
            if (entree.ValueKind != JsonValueKind.Object
                || !entree.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object) continue;
            if (entree.TryGetProperty("cores", out var coeurs) && coeurs.ValueKind == JsonValueKind.Array && coeurs.GetArrayLength() > 0
                && !coeurs.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String
                    && string.Equals(c.GetString(), coeurSha256, StringComparison.OrdinalIgnoreCase))) continue;
            var valeurs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) valeurs[p.Name] = p.Value.GetString() ?? "";
            var ecart = Ecart(joues, valeurs);
            if (ecart < meilleurEcart) { meilleurEcart = ecart; meilleure = (index, valeurs); }
        }
        return meilleure;
    }

    private static int Ecart(IReadOnlyDictionary<string, string> joues, IReadOnlyDictionary<string, string> attendues)
    {
        var n = 0;
        foreach (var (k, v) in joues) if (!attendues.TryGetValue(k, out var a) || a != v) n++;
        foreach (var k in attendues.Keys) if (!joues.ContainsKey(k)) n++;
        return n;
    }

    /// <summary>
    /// Ce qui differe : chaque cle jouee dont la valeur n'est pas l'attendue (ou que le profil ne connait pas), chaque cle
    /// attendue que la borne n'a pas. <paramref name="defauts"/> : les valeurs par defaut que le cœur declare, quand la borne
    /// les connait (aujourd'hui jamais : le wrapper ne releve que les reponses de GET_VARIABLE). Tri ordinal par cle.
    /// </summary>
    public static IReadOnlyList<Difference> Calculer(IReadOnlyDictionary<string, string> joues, IReadOnlyDictionary<string, string>? attendues,
        IReadOnlyDictionary<string, string>? defauts = null)
    {
        var sortie = new List<Difference>();
        foreach (var cle in joues.Keys.Concat(attendues?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var a_joue = joues.TryGetValue(cle, out var valeur);
            string? attendue = null;
            var a_attendu = attendues is not null && attendues.TryGetValue(cle, out attendue);
            if (a_joue && a_attendu && valeur == attendue) continue;
            string? defaut = null;
            if (defauts is not null && defauts.TryGetValue(cle, out var d)) defaut = d;
            var triche = a_joue && OptionsDeTriche.Actives(cle + "=" + valeur).Count > 0;
            sortie.Add(new Difference(cle, a_joue ? valeur : null, a_attendu ? attendue : null, defaut, triche));
        }
        return sortie;
    }

    /// <summary>
    /// Le champ complet pour le site (voir la carte APIExpose, « Contrat core_options_diff »).
    /// <paramref name="optionsFiltrees"/> : la chaine de <c>FilterGameplayCoreOptions</c>, celle dont l'empreinte est faite.
    /// </summary>
    public static JsonObject EnJson(string? optionsFiltrees, string digest, string? coeurSha256, JsonElement profil,
        IReadOnlyDictionary<string, string>? defauts = null)
    {
        var joues = Paires(optionsFiltrees);
        var entree = EntreeAttendue(profil, coeurSha256, joues);
        var diff = Calculer(joues, entree?.Valeurs, defauts);
        var coupe = diff.Count > MaxEntrees;
        var entrees = new JsonArray();
        foreach (var d in diff.Take(MaxEntrees))
        {
            entrees.Add(new JsonObject
            {
                ["key"] = Borne(d.Key, ref coupe),
                ["value"] = d.Value is null ? null : Borne(d.Value, ref coupe),
                ["expected"] = d.Expected is null ? null : Borne(d.Expected, ref coupe),
                ["default"] = d.Default is null ? null : Borne(d.Default, ref coupe),
                ["cheat"] = d.Cheat,
            });
        }
        return new JsonObject
        {
            ["v"] = Version,
            ["core_sha256"] = string.IsNullOrEmpty(coeurSha256) ? null : coeurSha256!.ToLowerInvariant(),
            ["core_options_digest"] = digest,
            ["expected_entry"] = entree?.Index,
            ["defaults_known"] = defauts is { Count: > 0 },
            ["truncated"] = coupe,
            ["count"] = diff.Count,
            ["entries"] = entrees,
        };
    }

    private static string Borne(string texte, ref bool coupe)
    {
        if (texte.Length <= MaxTexte) return texte;
        coupe = true;
        return texte[..MaxTexte];
    }
}
