using System.Text.Json;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LES RENDUS QUI N'ABOUTISSENT JAMAIS (27/11, vu sur la borne : le replay d'un record du monde, propose par le site a une
/// station, etait relance a chaque sortie de jeu, interrompu par le jeu suivant, sans fin ni abandon : une fenetre
/// RetroArch en plus a chaque fois). Un rendu interrompu ou en echec est compte par replay ; a
/// <see cref="Seuil"/> essais sans film, le replay est ABANDONNE pour <see cref="Duree"/> : la station ne le rend plus, le
/// site en est prevenu (<c>videos/failed</c>) et le journal le dit. Le compte survit a un redemarrage de l'API (fichier).
/// Calcul pur sur un fichier : testable sans RetroArch.
/// </summary>
public sealed class SuiviDesRendus
{
    /// <summary>Essais sans film avant l'abandon.</summary>
    public const int Seuil = 3;

    /// <summary>Un replay abandonne n'est repris qu'apres ce delai (le compte repart alors de zero).</summary>
    public static readonly TimeSpan Duree = TimeSpan.FromHours(12);

    public sealed record Fiche(int Essais, DateTime? AbandonneLe);

    private readonly string _fichier;
    private readonly Func<DateTime> _maintenant;
    private readonly object _verrou = new();
    private Dictionary<string, Fiche>? _fiches;

    public SuiviDesRendus(string fichier, Func<DateTime>? maintenant = null)
    {
        _fichier = fichier;
        _maintenant = maintenant ?? (() => DateTime.UtcNow);
    }

    /// <summary>Ce replay est-il abandonne en ce moment ?</summary>
    public bool EstAbandonne(string replayId)
    {
        lock (_verrou)
        {
            var fiches = Charger();
            if (!fiches.TryGetValue(replayId, out var f) || f.AbandonneLe is not { } le) return false;
            if (_maintenant() - le < Duree) return true;
            fiches.Remove(replayId);
            Enregistrer(fiches);
            return false;
        }
    }

    /// <summary>
    /// Un rendu de ce replay s'est termine sans film (interrompu ou en echec). Rend vrai quand c'est le dernier essai permis :
    /// le replay vient d'etre abandonne (a signaler au site, une seule fois).
    /// </summary>
    public bool NoterUnEchec(string replayId)
    {
        lock (_verrou)
        {
            var fiches = Charger();
            var essais = (fiches.TryGetValue(replayId, out var f) && f.AbandonneLe is null ? f.Essais : 0) + 1;
            var abandonne = essais >= Seuil;
            fiches[replayId] = new Fiche(essais, abandonne ? _maintenant() : null);
            Enregistrer(fiches);
            return abandonne;
        }
    }

    /// <summary>Le replay a ete rendu : son compte est efface.</summary>
    public void NoterUneReussite(string replayId)
    {
        lock (_verrou)
        {
            var fiches = Charger();
            if (fiches.Remove(replayId)) Enregistrer(fiches);
        }
    }

    private Dictionary<string, Fiche> Charger()
    {
        if (_fiches is not null) return _fiches;
        try
        {
            _fiches = File.Exists(_fichier)
                ? JsonSerializer.Deserialize<Dictionary<string, Fiche>>(File.ReadAllText(_fichier)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _fiches = new();
        }
        return _fiches;
    }

    private void Enregistrer(Dictionary<string, Fiche> fiches)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_fichier))!);
            File.WriteAllText(_fichier, JsonSerializer.Serialize(fiches));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans fichier, le compte reste en memoire jusqu'a l'arret de l'API.
        }
    }
}
