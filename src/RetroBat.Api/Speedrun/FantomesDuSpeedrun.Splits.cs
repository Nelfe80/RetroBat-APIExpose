using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>Un fantome publie au Deck (snapshot « ghosts », lot LS2) : son id (le replay), son pseudo, sa couleur, son temps.</summary>
public sealed record FantomePublie(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("tint")] string Tint,
    [property: JsonPropertyName("time_ms")] long? TempsMs,
    [property: JsonPropertyName("status")] string Statut,
    [property: JsonPropertyName("splits")] IReadOnlyList<SplitPublie>? Splits);

/// <summary>Un split d'un fantome : le segment et le temps du run (ms) ou il l'atteint.</summary>
public sealed record SplitPublie(
    [property: JsonPropertyName("segment")] string Segment,
    [property: JsonPropertyName("ms")] long Ms);

/// <summary>Le bloc « ghosts » du snapshot : la reference (celle contre laquelle l'ecart s'affiche d'abord) et la liste.</summary>
public sealed record FantomesPublies(
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("next")] string? Suivant,
    [property: JsonPropertyName("segment_px")] int SegmentPx,
    [property: JsonPropertyName("list")] IReadOnlyList<FantomePublie> Liste);

/// <summary>Un ecart en direct (« speedrun.ghost.gap ») : le joueur atteint un segment, ou ce fantome est passe avant ou apres lui.</summary>
public sealed record EcartAuFantome(
    [property: JsonPropertyName("ghost")] string Ghost,
    [property: JsonPropertyName("ghost_id")] string GhostId,
    [property: JsonPropertyName("reference")] bool Reference,
    [property: JsonPropertyName("segment")] string Segment,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("segments")] int Segments,
    [property: JsonPropertyName("run_ms")] long RunMs,
    [property: JsonPropertyName("gap_ms")] long GapMs);

public sealed partial class FantomesDuSpeedrun
{
    /// <summary>Un fantome pose dans le dossier du coeur hote pour cette partie.</summary>
    internal sealed record FantomePose(string Nom, string Replay, string Etiquette, string Role, long? Images);

    internal const string RoleMien = "me", RoleMondial = "world", RoleReference = "rank";

    /// <summary>Les couleurs du coeur hote (rendu du 11/11) : l'or du Deck, l'argent, le bronze.</summary>
    internal static string TeinteDu(string role) => role switch { RoleMondial => "#FFD23F", RoleReference => "#CD7F32", _ => "#B2B8C4" };

    internal static string DossierDuCache { get; set; } = Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "fantomes");

    private readonly List<FantomePose> _poses = new();
    private readonly Dictionary<string, SplitsDuRun?> _splitsLus = new(StringComparer.Ordinal);
    private string? _splitsDuRunRecord;
    private int _runLu = -1, _splitsLusDuJoueur;

    /// <summary>Sous le verrou : une partie neuve, aucun fantome pose.</summary>
    private void OublierLesPoses()
    {
        _poses.Clear();
        _splitsLus.Clear();
        _runLu = -1;
        _splitsLusDuJoueur = 0;
        _splitsDuRunRecord = null;
    }

    private void Poser(FantomePose pose, bool remplacerLeMien = false)
    {
        lock (_verrou)
        {
            if (remplacerLeMien) _poses.RemoveAll(p => p.Nom.StartsWith(PrefixeDuMien, StringComparison.Ordinal));
            _poses.RemoveAll(p => p.Nom == pose.Nom);
            _poses.Add(pose);
            _splitsLus.Remove(pose.Nom);
        }
    }

    /// <summary>Les splits gardes d'un replay posent a cote de son fantome ; sans eux, un vieux fichier du meme nom est retire.</summary>
    private void PoserLesSplits(string dossier, string nom, string replay)
    {
        try
        {
            var cible = Path.Combine(dossier, nom + ".splits");
            var garde = Path.Combine(DossierDuCache, replay + ".splits");
            if (File.Exists(garde)) File.Copy(garde, cible, true);
            else if (File.Exists(cible)) File.Delete(cible);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(e, "Fantomes : splits de {Nom} non poses.", nom);
        }
    }

    /// <summary>Mon record tout juste fait : les splits du joueur relus a l'arrivee deviennent ceux de son fantome (et le cache).</summary>
    private void PoserLesSplitsDuRun(string dossier, string nom, string replay)
    {
        string? texte;
        lock (_verrou) texte = _splitsDuRunRecord;
        if (SplitsDesFantomes.Lire(texte) is not { Splits.Count: > 0 } splits)
        {
            PoserLesSplits(dossier, nom, replay);
            return;
        }
        try
        {
            var contenu = SplitsDesFantomes.Ecrire(splits with { Run = 0 });
            File.WriteAllText(Path.Combine(dossier, nom + ".splits"), contenu);
            Directory.CreateDirectory(DossierDuCache);
            File.WriteAllText(Path.Combine(DossierDuCache, replay + ".splits"), contenu);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(e, "Fantomes : splits du record {Replay} non ecrits.", replay);
        }
    }

    /// <summary>
    /// Les splits qu'un coeur hote a ecrits a la fin de la course d'un fantome (« 1-rp_X.splits ») vont au cache, a cote de son
    /// .nghost, puis sortent du dossier avec lui ; le fichier du joueur aussi. Statique : <see cref="ViderLeDossier"/> l'appelle.
    /// </summary>
    internal static void GarderLesSplits(string dossier)
    {
        foreach (var f in Directory.EnumerateFiles(dossier, "*-rp_*.splits"))
        {
            if (SplitsDesFantomes.ReplayDuNom(Path.GetFileName(f)) is { } replay
                && SplitsDesFantomes.Lire(File.ReadAllText(f)) is { Splits.Count: > 0 })
            {
                var garde = Path.Combine(DossierDuCache, replay + ".splits");
                if (!File.Exists(garde))
                {
                    Directory.CreateDirectory(DossierDuCache);
                    File.Copy(f, garde, false);
                }
            }
            File.Delete(f);
        }
        var joueur = Path.Combine(dossier, SplitsDesFantomes.NomDuJoueur);
        if (File.Exists(joueur)) File.Delete(joueur);
    }

    private string? DossierCourant()
    {
        lock (_verrou) return _hote is { } h ? DossierDe(h) : null;
    }

    private string? LireLesSplitsDuJoueur()
    {
        try
        {
            var dossier = _hote is { } h ? DossierDe(h) : null;
            var chemin = dossier is null ? null : Path.Combine(dossier, SplitsDesFantomes.NomDuJoueur);
            return chemin is not null && File.Exists(chemin) ? File.ReadAllText(chemin) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private SplitsDuRun? SplitsDu(string dossier, FantomePose pose)
    {
        if (_splitsLus.TryGetValue(pose.Nom, out var lus) && lus is not null) return lus;
        SplitsDuRun? splits = null;
        try
        {
            var chemin = Path.Combine(dossier, pose.Nom + ".splits");
            if (File.Exists(chemin)) splits = SplitsDesFantomes.Lire(File.ReadAllText(chemin));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            splits = null;
        }
        _splitsLus[pose.Nom] = splits;
        return splits;
    }

    /// <summary>
    /// Les ecarts nouveaux depuis le dernier appel : chaque segment que le joueur vient d'atteindre (fichier du coeur hote),
    /// compare a chaque fantome qui y est passe. Un run neuf (autre numero) repart du premier segment.
    /// </summary>
    public IReadOnlyList<EcartAuFantome> LireLesEcarts(double cadence)
    {
        var dossier = DossierCourant();
        if (dossier is null) return [];
        string? texte;
        try
        {
            var chemin = Path.Combine(dossier, SplitsDesFantomes.NomDuJoueur);
            texte = File.Exists(chemin) ? File.ReadAllText(chemin) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        if (SplitsDesFantomes.Lire(texte) is not { } joueur) return [];
        var ecarts = new List<EcartAuFantome>();
        lock (_verrou)
        {
            if (joueur.Run != _runLu)
            {
                _runLu = joueur.Run;
                _splitsLusDuJoueur = 0;
            }
            var reference = ReferenceSousVerrou();
            for (var i = _splitsLusDuJoueur; i < joueur.Splits.Count; i++)
            {
                var split = joueur.Splits[i];
                foreach (var pose in _poses)
                {
                    if (SplitsDu(dossier, pose) is not { } fantome || SplitsDesFantomes.EcartEnImages(split, fantome) is not { } ecart) continue;
                    ecarts.Add(new EcartAuFantome(pose.Etiquette, pose.Replay, pose.Nom == reference?.Nom, split.Segment,
                        fantome.Splits.ToList().FindIndex(s => s.Segment == split.Segment), fantome.Splits.Count,
                        SplitsDesFantomes.EnMillisecondes(split.Image, cadence), SplitsDesFantomes.EnMillisecondes(ecart, cadence)));
                }
            }
            _splitsLusDuJoueur = joueur.Splits.Count;
        }
        return ecarts;
    }

    /// <summary>La reference : le record mondial, sinon la reference de rang, sinon mon record.</summary>
    private FantomePose? ReferenceSousVerrou()
        => _poses.FirstOrDefault(p => p.Role == RoleMondial) ?? _poses.FirstOrDefault(p => p.Role == RoleReference) ?? _poses.FirstOrDefault();

    /// <summary>Le bloc « ghosts » du snapshot ; null sans fantome pose.</summary>
    public FantomesPublies? Publies(double cadence)
    {
        var dossier = DossierCourant();
        lock (_verrou)
        {
            if (!_enCourse || _poses.Count == 0 || dossier is null) return null;
            var reference = ReferenceSousVerrou();
            var segmentPx = 0;
            var liste = _poses.Select(p =>
            {
                var splits = SplitsDu(dossier, p);
                if (splits is not null) segmentPx = splits.SegmentPx;
                return new FantomePublie(p.Replay, p.Etiquette, p.Role, TeinteDu(p.Role),
                    p.Images is { } images ? SplitsDesFantomes.EnMillisecondes(images, cadence) : null, "ready",
                    splits?.Splits.Select(s => new SplitPublie(s.Segment, SplitsDesFantomes.EnMillisecondes(s.Image, cadence))).ToList());
            }).ToList();
            return new FantomesPublies(reference?.Replay, null, segmentPx, liste);
        }
    }
}
