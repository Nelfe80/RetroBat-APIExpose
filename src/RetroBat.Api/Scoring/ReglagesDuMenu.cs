using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LES REGLAGES DU MENU DE TEST QUE GARDE UNE NVRAM (2026-11-09, Ketsui et DaiOuJou sur PGM).
///
/// Sur PGM, FBNeo sauve la RAM 68000 entiere (ket.fs, 128 Ko) et les reglages du menu de test y vivent (DIFFICULTY en
/// 0x1005, EXTEND en 0x1004...). Le fichier est charge avant la premiere image : un reglage change dans une session
/// precedente est deja la pendant la prechauffe du wrapper, qui l'enregistre sans rien dire, et une ligne GAME_DIFFICULTY
/// ne parle jamais. Sans signal, la difficulte signee etait la valeur « initial » du profil : un joueur pouvait se donner
/// un EXTEND plus genereux sans que la partie en dise rien.
///
/// Un champ de difficulte du profil peut donc dire OU l'octet est garde : « nvram » = { file, offset }, le fichier etant
/// une NVRAM epinglee (nvram_pins). La borne lit cet octet dans le DEPART (lu au lancement, sauf depart vierge) et dans
/// la FIN (relue apres la fermeture de l'emulateur) ; une valeur hors usine devient la valeur signee du champ, et le
/// serveur refuse la partie par son controle habituel (game.difficulty_not_allowed). Le controle des epingles (sha256 des
/// plages, profile.nvram_mismatch) reste en place a cote : deux verrous independants, le serveur garde le dernier mot.
///
/// Un champ peut aussi porter « name » (« DIFFICULTY », le mot du menu du jeu) : la borne le dit au joueur quand la partie
/// est refusee pour ce reglage. Un profil sans « nvram » ni « name » ne change rien : les autres jeux signent exactement
/// la meme difficulte qu'avant.
/// </summary>
public static class ReglagesDuMenu
{
    /// <summary>Un reglage hors usine : l'adresse du champ (cle de la difficulte signee), son nom et la valeur lue.</summary>
    public sealed record Reglage(string Adresse, string Nom, int Valeur);

    /// <summary>
    /// Les reglages hors usine lus dans les NVRAM jointes au passeport : un par champ au plus (le depart d'abord, puis la
    /// fin). Vide quand le profil ne declare aucun champ « nvram » ou quand rien n'est joint.
    /// </summary>
    public static IReadOnlyList<Reglage> HorsUsineDansLaNvram(JsonElement profil, JsonArray? nvram)
    {
        var sortie = new List<Reglage>();
        if (nvram is null || nvram.Count == 0) return sortie;
        foreach (var champ in Champs(profil))
        {
            if (!champ.TryGetProperty("address", out var a) || a.ValueKind != JsonValueKind.String) continue;
            if (!champ.TryGetProperty("nvram", out var n) || n.ValueKind != JsonValueKind.Object) continue;
            if (!n.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String) continue;
            if (!n.TryGetProperty("offset", out var o) || !o.TryGetInt32(out var offset) || offset < 0) continue;
            var fichier = NvramSnapshotService.EpingleSure(f.GetString());
            if (fichier is null || Entree(nvram, fichier) is not { } entree) continue;
            foreach (var moment in new[] { "start", "end" })
            {
                var octets = Decoder(entree[moment]);
                if (octets is null || octets.Length == 0 || offset >= octets.Length) continue;
                // Un depart VIERGE (fichier absent, ou tout a 0xFF) n'a aucun reglage : le jeu demarre sur les siens.
                if (moment == "start" && octets.All(b => b == 0xFF)) continue;
                int valeur = octets[offset];
                if (Admise(champ, valeur)) continue;
                sortie.Add(new Reglage(ModesDeJeu.Adresse(a.GetString() ?? ""), Nom(champ), valeur));
                break;
            }
        }
        return sortie;
    }

    /// <summary>
    /// Le contexte de la partie avec ces reglages : une valeur hors usine lue dans la NVRAM l'emporte sur la valeur lue en
    /// jeu (une ligne du .MEM qui aurait vu un retour a l'usine avant le run ne cache pas un depart hors usine).
    /// </summary>
    public static ContexteDeJeu Appliquer(ContexteDeJeu contexte, IReadOnlyList<Reglage> horsUsine)
    {
        foreach (var r in horsUsine) contexte = contexte.AvecDifficulte(r.Adresse, r.Valeur);
        return contexte;
    }

    /// <summary>
    /// Les noms (« DIFFICULTY ») des champs nommes dont la valeur signee n'est pas admise par le profil : ce que la borne
    /// dit au joueur quand la partie est refusee. Seuls les champs qui portent « name » comptent.
    /// </summary>
    public static IReadOnlyList<string> NomsRefuses(JsonElement profil, JsonObject? difficulte)
    {
        var noms = new List<string>();
        if (difficulte is null || difficulte.Count == 0) return noms;
        foreach (var champ in Champs(profil))
        {
            if (!champ.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())) continue;
            if (!champ.TryGetProperty("address", out var a) || a.ValueKind != JsonValueKind.String) continue;
            var cle = ModesDeJeu.Adresse(a.GetString() ?? "");
            if (difficulte[cle] is not JsonValue v || !v.TryGetValue<int>(out var valeur)) continue;
            if (Admise(champ, valeur)) continue;
            var nom = n.GetString()!.Trim();
            if (!noms.Contains(nom, StringComparer.Ordinal)) noms.Add(nom);
        }
        return noms;
    }

    /// <summary>Le motif dit au joueur : « test menu settings changed: DIFFICULTY, EXTEND ».</summary>
    public static string Motif(IReadOnlyList<string> noms, string langue)
        => (string.Equals(langue, "fr", StringComparison.OrdinalIgnoreCase)
            ? "réglages du menu de test changés : "
            : "test menu settings changed: ") + string.Join(", ", noms);

    private static IEnumerable<JsonElement> Champs(JsonElement profil)
    {
        if (profil.ValueKind != JsonValueKind.Object
            || !profil.TryGetProperty("difficulty", out var d) || d.ValueKind != JsonValueKind.Object
            || !d.TryGetProperty("fields", out var champs) || champs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var champ in champs.EnumerateArray())
        {
            if (champ.ValueKind == JsonValueKind.Object) yield return champ;
        }
    }

    /// <summary>Admise : dans « accept » s'il y en a une, sinon egale a « initial » ; sans l'un ni l'autre, tout passe.</summary>
    private static bool Admise(JsonElement champ, int valeur)
    {
        if (champ.TryGetProperty("accept", out var acc) && acc.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in acc.EnumerateArray())
            {
                if (x.TryGetInt32(out var ok) && ok == valeur) return true;
            }
            return false;
        }
        if (champ.TryGetProperty("initial", out var i) && i.TryGetInt32(out var initiale)) return initiale == valeur;
        return true;
    }

    private static string Nom(JsonElement champ)
        => champ.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()!.Trim()
            : champ.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";

    /// <summary>
    /// L'entree de ce fichier, comme le serveur la choisit (CoreVerifier) : celle que le coeur lit
    /// (« saves/&lt;systeme&gt;/&lt;fichier&gt; ») quand elle est jointe, sinon la premiere au nom voulu.
    /// </summary>
    private static JsonObject? Entree(JsonArray nvram, string fichier)
    {
        JsonObject? premiere = null;
        foreach (var noeud in nvram)
        {
            if (noeud is not JsonObject o) continue;
            var nom = ((string?)(o["file"] as JsonValue) ?? "").Replace('\\', '/');
            if (!nom.EndsWith(fichier, StringComparison.OrdinalIgnoreCase)) continue;
            if (nom.Length > fichier.Length && nom[nom.Length - fichier.Length - 1] != '/') continue;
            premiere ??= o;
            var reste = nom[..^fichier.Length];
            if (reste.StartsWith("saves/", StringComparison.OrdinalIgnoreCase) && reste.Count(c => c == '/') == 2) return o;
        }
        return premiere;
    }

    private static byte[]? Decoder(JsonNode? noeud)
    {
        if (noeud is not JsonValue v || !v.TryGetValue<string>(out var texte) || texte.Length == 0) return null;
        try { return Convert.FromBase64String(texte); }
        catch (FormatException) { return null; }
    }
}
