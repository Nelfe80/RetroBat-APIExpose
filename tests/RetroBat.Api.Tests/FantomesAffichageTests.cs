using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Speedrun;
using Xunit;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'AFFICHAGE DES FANTOMES (12/11) : le selecteur ouvert par X (choix, opacite, pseudos), les reglages retenus d'un lancement
/// a l'autre, et le fichier lu par le coeur hote. Chaque essai a ses propres fichiers : rien n'est ecrit dans la borne.
/// NELFEPLAY_APERCUS (dossier) dessine aussi le panneau, selecteur ouvert et fiche avec le resume ; NELFEPLAY_APERCUS_TEXTES
/// (un interface-texts.json) le dessine en francais.
/// </summary>
[Collection("FichiersDesFantomes")]
public sealed class FantomesAffichageTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-affichage-" + Guid.NewGuid().ToString("N"));
    private readonly string _avantEtat = FantomesDuSpeedrun.FichierDeLAffichage;
    private readonly string _avantCoeur = FantomesDuSpeedrun.FichierDuCoeur;

    public FantomesAffichageTests()
    {
        FantomesDuSpeedrun.FichierDeLAffichage = Path.Combine(_dossier, "state", "speedrun", "fantomes_affichage.json");
        FantomesDuSpeedrun.FichierDuCoeur = Path.Combine(_dossier, "bios", "nelfeplay", "ghosts", "affichage.ini");
    }

    public void Dispose()
    {
        FantomesDuSpeedrun.FichierDeLAffichage = _avantEtat;
        FantomesDuSpeedrun.FichierDuCoeur = _avantCoeur;
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, true);
    }

    private static FantomesDuSpeedrun Fantomes() => new(null!, null!, null!, null!, null!);

    private static readonly Reglement Mario11 = Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "super-mario-bros.1-1.json")));

    private static DefisPanelService Panneau(FantomesDuSpeedrun fantomes, string langue = "en")
    {
        var textes = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_TEXTES") is { Length: > 0 } chemin && langue == "fr"
            ? new RetroBat.Api.Infrastructure.InterfaceTextService(chemin)
            : null;
        return new DefisPanelService(null!, null!, null!, new L(NullLogger<L>.Instance), null!, textes: textes, fantomes: fantomes)
        {
            LangueDApercu = langue,
        };
    }

    private static string Valeur(L.ContenuDefis c, string etiquette) => c.Fiche.First(f => f.Etiquette == etiquette).Valeur;

    [Fact]
    public void Le_fichier_du_coeur_dit_l_opacite_et_les_pseudos()
    {
        Assert.Contains("opacite=100\r\npseudos=1", FantomesDuSpeedrun.TexteDuCoeur(false, false));
        Assert.Contains("opacite=50\r\npseudos=0", FantomesDuSpeedrun.TexteDuCoeur(true, true));
        Assert.Contains("opacite=50\r\npseudos=1", FantomesDuSpeedrun.TexteDuCoeur(true, false));
        Assert.Contains("opacite=100\r\npseudos=0", FantomesDuSpeedrun.TexteDuCoeur(false, true));
    }

    [Fact]
    public void Sans_reglage_retenu_les_fantomes_sont_opaques_avec_leurs_pseudos()
    {
        var f = Fantomes();
        Assert.False(f.OpaciteReduite);
        Assert.False(f.PseudosMasques);
        f.EcrireLeFichierDuCoeur();
        Assert.Equal(FantomesDuSpeedrun.TexteDuCoeur(false, false), File.ReadAllText(FantomesDuSpeedrun.FichierDuCoeur));
    }

    [Fact]
    public void Les_reglages_sont_retenus_d_un_lancement_a_l_autre_et_ecrits_au_coeur()
    {
        var f = Fantomes();
        f.ReduireLOpacite(true);
        Assert.Equal(FantomesDuSpeedrun.TexteDuCoeur(true, false), File.ReadAllText(FantomesDuSpeedrun.FichierDuCoeur));
        f.MasquerLesPseudos(true);
        Assert.Equal(FantomesDuSpeedrun.TexteDuCoeur(true, true), File.ReadAllText(FantomesDuSpeedrun.FichierDuCoeur));

        // un autre lancement de l'API : les memes reglages
        var apres = Fantomes();
        Assert.True(apres.OpaciteReduite);
        Assert.True(apres.PseudosMasques);

        apres.ReduireLOpacite(false);
        var encore = Fantomes();
        Assert.False(encore.OpaciteReduite);
        Assert.True(encore.PseudosMasques);
        Assert.Equal(FantomesDuSpeedrun.TexteDuCoeur(false, true), File.ReadAllText(FantomesDuSpeedrun.FichierDuCoeur));
    }

    [Fact]
    public void Un_fichier_du_coeur_efface_se_refait_a_la_preparation()
    {
        var f = Fantomes();
        f.MasquerLesPseudos(true);
        File.Delete(FantomesDuSpeedrun.FichierDuCoeur);
        f.EcrireLeFichierDuCoeur();
        Assert.Equal(FantomesDuSpeedrun.TexteDuCoeur(false, true), File.ReadAllText(FantomesDuSpeedrun.FichierDuCoeur));
    }

    [Fact]
    public void Des_reglages_illisibles_rendent_ceux_d_origine()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FantomesDuSpeedrun.FichierDeLAffichage)!);
        File.WriteAllText(FantomesDuSpeedrun.FichierDeLAffichage, "{pas du json");
        var f = Fantomes();
        Assert.False(f.OpaciteReduite);
        Assert.False(f.PseudosMasques);
    }

    [Fact]
    public void Le_selecteur_saute_les_lignes_inactives_et_les_valeurs_bouclent()
    {
        int a = 0, b = 0, c = 0;
        var actif = true;
        IReadOnlyList<ReglageDuSelecteur> r =
        [
            new("A", ["a0", "a1", "a2"], () => a, i => a = i),
            new("B", ["b0", "b1"], () => b, i => b = i, () => actif),
            new("C", ["c0", "c1"], () => c, i => c = i),
        ];
        var s = new SelecteurDeReglages();
        Assert.False(s.Bas(r));
        s.Ouvrir(r);
        Assert.Equal(0, s.Ligne);
        Assert.False(s.Haut(r));
        Assert.True(s.Gauche(r));
        Assert.Equal(2, a);
        Assert.True(s.Droite(r));
        Assert.Equal(0, a);
        Assert.True(s.Bas(r));
        Assert.Equal(1, s.Ligne);
        Assert.True(s.Droite(r));
        Assert.Equal(1, b);
        Assert.True(s.Droite(r));
        Assert.Equal(0, b);

        actif = false;
        s.Recaler(r);
        Assert.Equal(0, s.Ligne);
        Assert.True(s.Bas(r));
        Assert.Equal(2, s.Ligne);
        Assert.False(s.Bas(r));
        Assert.True(s.Haut(r));
        Assert.Equal(0, s.Ligne);
        s.Fermer();
        Assert.False(s.Ouvert);
    }

    [Fact]
    public void X_ouvre_le_selecteur_et_les_directions_y_restent()
    {
        var f = Fantomes();
        var p = Panneau(f);
        var avant = p.OuvrirPourEssai([Mario11]);
        Assert.Null(avant.Selecteur);
        Assert.Contains(avant.Aides, a => a.Mot == "Ghosts");
        Assert.Equal("My record + world record", Valeur(avant, "Ghosts"));

        p.X();
        var s = p.ContenuPourEssai().Selecteur;
        Assert.NotNull(s);
        Assert.Equal(["Ghosts", "Opacity", "Names"], s!.Lignes.Select(l => l.Etiquette));
        Assert.Equal(["My record + world record", "Normal", "Shown"], s.Lignes.Select(l => l.Valeur));
        Assert.Equal(0, s.Courante);
        Assert.Equal(["Line", "Change", "Close"], p.ContenuPourEssai().Aides.Select(a => a.Mot));
        Assert.NotNull(avant.Action);
        Assert.Null(p.ContenuPourEssai().Action);   // rien a lancer tant que le selecteur est ouvert
        Assert.Equal("Ghost settings", s.Titre);

        // gauche et droite changent la valeur, l'onglet reste ; haut et bas changent la ligne, le curseur des defis reste
        var onglet = p.ContenuPourEssai().OngletCourant;
        var ligne = p.ContenuPourEssai().LigneCourante;
        p.Direction("down");
        p.Direction("right");
        Assert.True(f.OpaciteReduite);
        p.Direction("down");
        p.Direction("left");
        Assert.True(f.PseudosMasques);
        p.Direction("down");
        Assert.Equal(2, p.ContenuPourEssai().Selecteur!.Courante);
        p.Direction("up");
        p.Direction("up");
        p.Direction("right");
        Assert.Equal(ChoixDesFantomes.MonRecord, f.Choix);
        Assert.Equal(onglet, p.ContenuPourEssai().OngletCourant);
        Assert.Equal(ligne, p.ContenuPourEssai().LigneCourante);
        Assert.Equal(["My record", "50%", "Hidden, arrow at the edge"], p.ContenuPourEssai().Selecteur!.Lignes.Select(l => l.Valeur));

        // LB et RB ne font rien tant que le selecteur est ouvert
        p.GroupeSuivant();
        p.GroupePrecedent();
        Assert.Equal(ligne, p.ContenuPourEssai().LigneCourante);
        Assert.NotNull(p.ContenuPourEssai().Selecteur);

        // X referme ; la fiche resume
        // Y ne lance rien tant que le selecteur est ouvert
        p.Defier();
        Assert.NotNull(p.ContenuPourEssai().Selecteur);

        p.X();
        var apres = p.ContenuPourEssai();
        Assert.Null(apres.Selecteur);
        Assert.Equal("My record · 50% · no names", Valeur(apres, "Ghosts"));
        Assert.Contains(apres.Aides, a => a.Mot == "Ghosts");
    }

    [Fact]
    public void A_et_B_referment_le_selecteur_sans_quitter_le_panneau()
    {
        var p = Panneau(Fantomes());
        p.OuvrirPourEssai([Mario11]);
        p.X();
        p.Valider(appuye: false);   // le relache seul ne fait rien
        Assert.NotNull(p.ContenuPourEssai().Selecteur);
        p.Valider(appuye: true);
        Assert.Null(p.ContenuPourEssai().Selecteur);
        Assert.True(p.ContenuPourEssai().NousAvonsLaMain);

        p.X();
        p.Annuler(appuye: true);
        Assert.Null(p.ContenuPourEssai().Selecteur);
        Assert.True(p.ContenuPourEssai().NousAvonsLaMain);
        Assert.True(p.Ouvert);
    }

    [Fact]
    public void Sans_fantome_l_opacite_et_les_pseudos_sont_grises_et_sautes()
    {
        var f = Fantomes();
        var p = Panneau(f);
        p.OuvrirPourEssai([Mario11]);
        p.X();
        p.Direction("left");   // les deux -> aucun
        Assert.Equal(ChoixDesFantomes.Aucun, f.Choix);
        var s = p.ContenuPourEssai().Selecteur!;
        Assert.Equal([true, false, false], s.Lignes.Select(l => l.Active));
        p.Direction("down");
        Assert.Equal(0, p.ContenuPourEssai().Selecteur!.Courante);
        p.X();
        Assert.Equal("None", Valeur(p.ContenuPourEssai(), "Ghosts"));
    }

    [Fact]
    public void Le_selecteur_et_la_fiche_se_dessinent()
    {
        var f = Fantomes();
        var sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
        foreach (var langue in new[] { "en", "fr" })
        {
            var p = Panneau(f, langue);
            p.OuvrirPourEssai([Mario11]);
            void Ecrire(string nom)
            {
                var png = L.ApercuDefis(EsMenuStyle.Lire(null), p.ContenuPourEssai(), null!);
                Assert.NotEmpty(png);
                if (sortie is { Length: > 0 }) File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(sortie).FullName, $"selecteur-{langue}-{nom}.png"), png);
            }

            f.Choisir(ChoixDesFantomes.MonRecordEtMondial);
            f.ReduireLOpacite(false);
            f.MasquerLesPseudos(false);
            Ecrire("1-fiche-origine");
            p.X();
            Ecrire("2-selecteur-ouvert");
            p.Direction("down");
            p.Direction("right");
            p.Direction("down");
            p.Direction("right");
            Ecrire("3-selecteur-50-sans-pseudos");
            p.X();
            Ecrire("4-fiche-resume");
            p.X();
            p.Direction("up");
            p.Direction("up");
            p.Direction("left");
            Ecrire("5-selecteur-aucun");
            p.X();
        }
    }
}
