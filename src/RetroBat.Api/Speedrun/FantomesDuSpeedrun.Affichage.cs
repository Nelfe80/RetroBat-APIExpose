using System.Text;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// L'AFFICHAGE DES FANTOMES (demande du user du 12/11) : deux reglages qui se combinent avec le choix des fantomes, l'opacite
/// (normale ou 50 %) et les pseudos (affiches, ou masques avec une fleche au bord de l'image quand un fantome est hors de la
/// vue). Ils sont retenus d'un lancement a l'autre (<see cref="FichierDeLAffichage"/>) et dits au coeur hote par
/// <see cref="FichierDuCoeur"/>, un petit .ini lu au chargement des fantomes.
/// <para>
/// LE CANAL VERS LE COEUR : un fichier a cote des dossiers des fantomes (<c>bios\nelfeplay\ghosts\affichage.ini</c>), commun
/// a tous les jeux. Il ne depend ni du crc du jeu ni du lanceur (une variable d'environnement ne passerait pas par ES et
/// emulatorLauncher), il est reecrit a chaque changement et a chaque preparation, et un coeur sans ce fichier garde le
/// comportement d'avant (opaque, pseudos affiches). Aucun fichier d'un dossier de jeu n'est touche.
/// </para>
/// </summary>
public sealed partial class FantomesDuSpeedrun
{
    /// <summary>Ou les deux reglages sont retenus.</summary>
    internal static string FichierDeLAffichage { get; set; } = Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "fantomes_affichage.json");

    /// <summary>Ce que lit le coeur hote (fceumm_ghosts, snes9x_ghosts, genesis_plus_gx_ghosts).</summary>
    internal static string FichierDuCoeur { get; set; } = Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "nelfeplay", "ghosts", "affichage.ini");

    private bool _affichageLu;
    private bool _opaciteReduite;
    private bool _pseudosMasques;

    /// <summary>Vrai : les fantomes sont meles a moitie a l'image du jeu (50 %).</summary>
    public bool OpaciteReduite
    {
        get { LireLAffichage(); return _opaciteReduite; }
    }

    /// <summary>Vrai : aucun pseudo dessine ; une fleche au bord signale un fantome hors de la vue.</summary>
    public bool PseudosMasques
    {
        get { LireLAffichage(); return _pseudosMasques; }
    }

    public void Choisir(ChoixDesFantomes choix) => Choix = choix;

    public void ReduireLOpacite(bool reduite)
    {
        LireLAffichage();
        if (_opaciteReduite == reduite) return;
        _opaciteReduite = reduite;
        RetenirLAffichage();
    }

    public void MasquerLesPseudos(bool masques)
    {
        LireLAffichage();
        if (_pseudosMasques == masques) return;
        _pseudosMasques = masques;
        RetenirLAffichage();
    }

    /// <summary>Le .ini du coeur, pur : les cles « opacite » (100 ou 50) et « pseudos » (1 ou 0).</summary>
    internal static string TexteDuCoeur(bool opaciteReduite, bool pseudosMasques)
        => "; NelfePlay : affichage des fantomes du speedrun, ecrit par l'API (panneau des defis, X)\r\n"
           + "opacite=" + (opaciteReduite ? "50" : "100") + "\r\n"
           + "pseudos=" + (pseudosMasques ? "0" : "1") + "\r\n";

    /// <summary>Le .ini du coeur, reecrit tel que les reglages le disent (a chaque preparation : un fichier efface ou perime se refait).</summary>
    internal void EcrireLeFichierDuCoeur()
    {
        LireLAffichage();
        try
        {
            var dossier = Path.GetDirectoryName(FichierDuCoeur);
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);
            var texte = TexteDuCoeur(_opaciteReduite, _pseudosMasques);
            if (File.Exists(FichierDuCoeur) && File.ReadAllText(FichierDuCoeur) == texte) return;
            File.WriteAllText(FichierDuCoeur + ".tmp", texte, new UTF8Encoding(false));
            File.Move(FichierDuCoeur + ".tmp", FichierDuCoeur, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(e, "Fantomes : affichage.ini non ecrit.");
        }
    }

    private void LireLAffichage()
    {
        lock (_verrou)
        {
            if (_affichageLu) return;
            _affichageLu = true;
            try
            {
                if (!File.Exists(FichierDeLAffichage)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(FichierDeLAffichage));
                var racine = doc.RootElement;
                _opaciteReduite = racine.TryGetProperty("opacite", out var o) && o.ValueKind == JsonValueKind.Number && o.GetInt32() < 100;
                _pseudosMasques = racine.TryGetProperty("pseudos", out var p) && p.ValueKind == JsonValueKind.False;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException)
            {
                _journal?.LogDebug(e, "Fantomes : reglages d'affichage illisibles, ceux d'origine.");
            }
        }
    }

    private void RetenirLAffichage()
    {
        try
        {
            var dossier = Path.GetDirectoryName(FichierDeLAffichage);
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);
            File.WriteAllText(FichierDeLAffichage + ".tmp",
                JsonSerializer.Serialize(new { opacite = _opaciteReduite ? 50 : 100, pseudos = !_pseudosMasques }), new UTF8Encoding(false));
            File.Move(FichierDeLAffichage + ".tmp", FichierDeLAffichage, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(e, "Fantomes : reglages d'affichage non retenus.");
        }

        EcrireLeFichierDuCoeur();
        _journal?.LogInformation("Fantomes : affichage {Opacite} %, pseudos {Pseudos}.", _opaciteReduite ? 50 : 100,
            _pseudosMasques ? "masques" : "affiches");
    }
}
