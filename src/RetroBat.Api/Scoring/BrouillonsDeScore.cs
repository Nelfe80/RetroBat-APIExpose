using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LE BROUILLON SCELLE D'UNE PARTIE (piste B du CDC infra, 2026-10-04).
///
/// A la fin d'une partie, la borne mesurait, demandait un ticket, assemblait le passeport et
/// l'envoyait d'une traite. Un site muet a ce moment-la (profil, ticket ou envoi) et le score etait
/// perdu ; une reponse 503 etait meme rangee comme un verdict. Desormais tout ce qui a ete mesure,
/// avec la VRAIE heure de fin, le joueur de la session et le replay, part d'abord dans un brouillon
/// signe par la cle de l'appareil et pose sur le disque. La file l'envoie aussitot, et le retire
/// seulement a un verdict definitif ; sinon elle reessaie, de 30 s a 30 min.
///
/// Le brouillon garde l'identifiant de session du passeport : un renvoi apres une reponse perdue est
/// reconnu par le site comme le meme passeport, qui rend son verdict d'origine au lieu de compter
/// la partie deux fois.
/// </summary>
public static class BrouillonDeScore
{
    public const string Schema = "nelfe.scoring.brouillon.v1";

    /// <summary>Le contenu signe : tout le brouillon, sans sa signature.</summary>
    private static byte[] Contenu(JsonObject brouillon)
    {
        var corps = brouillon.DeepClone()!.AsObject();
        corps.Remove("signature");
        return Jcs.CanonicalBytes(corps);
    }

    /// <summary>Signe le brouillon avec la cle de l'appareil.</summary>
    public static void Signer(JsonObject brouillon, string keyId, Func<byte[], string> signer)
    {
        brouillon["cle"] = keyId;
        brouillon.Remove("signature");
        brouillon["signature"] = signer(Contenu(brouillon));
    }

    /// <summary>
    /// Vrai si le brouillon porte la signature de cette cle et n'a pas ete touche depuis. Un fichier
    /// modifie a la main (score, trajectoire, heure) est refuse : il ne partira jamais.
    /// </summary>
    public static bool Verifier(JsonObject brouillon, byte[] spkiDer)
    {
        if (brouillon["signature"] is not JsonValue sig || !sig.TryGetValue<string>(out var signature)) return false;
        if (!string.Equals((string?)brouillon["cle"], Crypto.KeyId(spkiDer), StringComparison.Ordinal)) return false;
        return Crypto.Verify(spkiDer, Contenu(brouillon), signature);
    }

    /// <summary>
    /// Ce que vaut une reponse du site. DEFINITIF : un verdict lisible (JSON en 2xx), ou un refus
    /// net de la demande (400, 409, 422) qu'un nouvel essai ne changerait pas. A RETENTER : tout le
    /// reste, pas de reponse, 5xx, 408, 429, une page HTML, et aussi 401, 403 ou 404, qui disent un
    /// probleme d'acces ou d'aiguillage plutot qu'un jugement sur la partie.
    /// </summary>
    public static IssueDEnvoi Classer(int statut, string? corps)
    {
        var json = false;
        try
        {
            json = !string.IsNullOrWhiteSpace(corps) && JsonNode.Parse(corps) is JsonObject;
        }
        catch (JsonException)
        {
            json = false;
        }

        if (!json) return IssueDEnvoi.ARetenter;
        if (statut is >= 200 and < 300) return IssueDEnvoi.Definitif;
        return statut is 400 or 409 or 422 ? IssueDEnvoi.Definitif : IssueDEnvoi.ARetenter;
    }
}

public enum IssueDEnvoi
{
    Definitif,
    ARetenter,
}

/// <summary>
/// La file des brouillons sur le disque : un fichier par partie, ecrit a cote puis renomme. Le
/// plus ancien part le premier. L'espacement des essais vit en memoire : un redemarrage de l'API
/// reessaie tout de suite, ce qui est le bon reflexe.
/// </summary>
public sealed class FileDesBrouillons
{
    public static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DernierDelai = TimeSpan.FromMinutes(30);

    private readonly Func<DateTime> _maintenant;
    private readonly Random _alea;
    private readonly object _verrou = new();
    private readonly Dictionary<string, (int Essais, DateTime Prochain)> _essais = new(StringComparer.Ordinal);

    public FileDesBrouillons(string dossier, Func<DateTime>? maintenant = null, Random? alea = null)
    {
        Dossier = dossier;
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
        _alea = alea ?? new Random();
    }

    public string Dossier { get; }

    /// <summary>Le delai avant le prochain essai : 30 s, doublé a chaque echec, 30 min au plus, a 20 % pres.</summary>
    public static TimeSpan Delai(int essais, double alea)
    {
        var base_ = PremierDelai.TotalSeconds * Math.Pow(2, Math.Max(0, essais - 1));
        var secondes = Math.Min(base_, DernierDelai.TotalSeconds);
        return TimeSpan.FromSeconds(secondes * (0.8 + 0.4 * Math.Clamp(alea, 0, 1)));
    }

    private string Chemin(string id) => Path.Combine(Dossier, id + ".json");

    /// <summary>Pose le brouillon. Faux si le disque refuse (plein, droits) : l'appelant envoie alors directement.</summary>
    public bool Poser(JsonObject brouillon)
    {
        try
        {
            var id = (string?)brouillon["id"];
            if (string.IsNullOrEmpty(id)) return false;
            Directory.CreateDirectory(Dossier);
            var temporaire = Chemin(id) + ".tmp";
            File.WriteAllText(temporaire, brouillon.ToJsonString(), new System.Text.UTF8Encoding(false));
            File.Move(temporaire, Chemin(id), overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Les brouillons en attente, du plus ancien au plus recent (heure de fin de partie). Un fichier
    /// illisible part dans « illisibles » : il ne bloque pas les autres.
    /// </summary>
    public IReadOnlyList<(string Id, JsonObject Brouillon)> EnAttente()
    {
        var liste = new List<(string Id, JsonObject Brouillon, string Fin)>();
        if (!Directory.Exists(Dossier)) return [];
        foreach (var fichier in Directory.EnumerateFiles(Dossier, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(fichier);
            try
            {
                if (JsonNode.Parse(File.ReadAllText(fichier)) is JsonObject brouillon && (string?)brouillon["schema"] == BrouillonDeScore.Schema)
                {
                    liste.Add((id, brouillon, (string?)brouillon["fin_le"] ?? ""));
                    continue;
                }
            }
            catch (Exception)
            {
                // illisible : mis de cote ci-dessous
            }
            Ecarter(id, "illisibles");
        }
        return liste.OrderBy(b => b.Fin, StringComparer.Ordinal).ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => (b.Id, b.Brouillon)).ToList();
    }

    /// <summary>Le brouillon a eu son verdict : il quitte la file.</summary>
    public void Retirer(string id)
    {
        try { File.Delete(Chemin(id)); } catch (Exception) { }
        lock (_verrou) _essais.Remove(id);
    }

    /// <summary>Met le brouillon de cote (modifie, illisible) dans un sous-dossier, pour l'enquete.</summary>
    public void Ecarter(string id, string sousDossier)
    {
        try
        {
            var cible = Path.Combine(Dossier, sousDossier);
            Directory.CreateDirectory(cible);
            File.Move(Chemin(id), Path.Combine(cible, id + ".json"), overwrite: true);
        }
        catch (Exception)
        {
            // Rien de mieux a faire : il sera represente au tour suivant.
        }
        lock (_verrou) _essais.Remove(id);
    }

    /// <summary>Vrai si le brouillon peut etre tente maintenant.</summary>
    public bool EstDu(string id)
    {
        lock (_verrou) return !_essais.TryGetValue(id, out var e) || _maintenant() >= e.Prochain;
    }

    /// <summary>L'essai a echoue : le prochain attendra. Rend le delai choisi.</summary>
    public TimeSpan Reporter(string id)
    {
        lock (_verrou)
        {
            var essais = (_essais.TryGetValue(id, out var e) ? e.Essais : 0) + 1;
            var delai = Delai(essais, _alea.NextDouble());
            _essais[id] = (essais, _maintenant() + delai);
            return delai;
        }
    }

    /// <summary>Combien d'essais ont echoue pour ce brouillon.</summary>
    public int Essais(string id)
    {
        lock (_verrou) return _essais.TryGetValue(id, out var e) ? e.Essais : 0;
    }
}
