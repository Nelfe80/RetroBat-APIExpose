namespace RetroBat.Api.Adventure;

/// <summary>Comment une seance repart (CDC v1.0 §2, point 4 ; etude §12).</summary>
public enum ModeDeReprise
{
    /// <summary>Aucune version : une partie neuve (ecran de choix de fichier, consoles vierges).</summary>
    Neuf,

    /// <summary>Meme nombre de joueurs : l'etat lie est recharge, exactement la ou on s'etait arrete.</summary>
    Etat,

    /// <summary>Un autre nombre de joueurs : partie neuve, la sauvegarde de la cartouche de l'aventure posee en console 1.</summary>
    NeufAvecSauvegarde,
}

public sealed record DecisionDeReprise(ModeDeReprise Mode, string Raison);

/// <summary>La regle de reprise, pure : c'est ce qui se teste.</summary>
public static class AdventureReprise
{
    /// <summary>
    /// La derniere version, le nombre de joueurs de la seance et le coeur installe (empreinte de sa dll, ou null s'il est
    /// inconnu) decident. Un etat n'est jamais charge dans un autre coeur ni avec un autre nombre de consoles : le coeur
    /// refuse (« header mismatch, n=3 want n=2 »). La sauvegarde de la cartouche, elle, vaut pour tout nombre de joueurs.
    /// </summary>
    public static DecisionDeReprise Decider(AdventureVersion? version, int joueurs, string? coeurInstalle)
    {
        if (version is null)
        {
            return new(ModeDeReprise.Neuf, "aucune version : partie neuve");
        }

        var etatPossible = !string.IsNullOrEmpty(version.StateSha256);
        if (etatPossible && version.Players == joueurs && CoeurCompatible(version.CoreSha256, coeurInstalle))
        {
            return new(ModeDeReprise.Etat, "meme nombre de joueurs, meme coeur : etat lie");
        }

        var pourquoi = !etatPossible ? "pas d'etat" : version.Players != joueurs ? $"{version.Players} joueurs au lieu de {joueurs}" : "coeur different";
        return !string.IsNullOrEmpty(version.SaveSha256)
            ? new(ModeDeReprise.NeufAvecSauvegarde, pourquoi + " : partie neuve et sauvegarde de la cartouche")
            : new(ModeDeReprise.Neuf, pourquoi + " et pas de sauvegarde : partie neuve");
    }

    /// <summary>Les deux empreintes se valent, ou l'une est inconnue (une version ancienne sans empreinte ne bloque pas).</summary>
    public static bool CoeurCompatible(string? duCoeurDeLaVersion, string? installe)
        => string.IsNullOrEmpty(duCoeurDeLaVersion) || string.IsNullOrEmpty(installe)
           || string.Equals(duCoeurDeLaVersion, installe, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Quand sauver (CDC v1.0 §2, point 3). Pur : l'horloge et l'etat du jeu viennent de l'appelant. Seul l'hote sauve (en netplay
/// toutes les bornes ont le meme etat, une copie suffit).
/// </summary>
public static class AdventureCadence
{
    public static readonly TimeSpan Periode = TimeSpan.FromMinutes(5);

    /// <summary>La periode reguliere est echue, le jeu tourne (ni pause, ni chargement), et rien n'a ete sauve depuis.</summary>
    public static bool RegulierEchu(DateTime maintenant, DateTime derniereSauvegarde, bool jeuEnCours, bool enChargement)
        => jeuEnCours && !enChargement && maintenant - derniereSauvegarde >= Periode;

    /// <summary>
    /// Le jeu a ecrit sa sauvegarde : l'empreinte du fichier a change depuis la derniere vue. La premiere lecture ne declenche
    /// rien (le fichier existait avant la seance : c'est la sauvegarde posee au depart).
    /// </summary>
    public static bool LeJeuASauve(string? empreinteVue, string? empreinteLue)
        => empreinteVue is not null && empreinteLue is not null && !string.Equals(empreinteVue, empreinteLue, StringComparison.OrdinalIgnoreCase);
}
