using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE RELEVE FAIT TROP TOT (signalement d'Old AF Gamer du 16/11, API 1.9.193) : en Any% de Super Mario Bros., le chrono ne
/// partait pas au premier Start ; apres Hotkey + Gauche (RESET), il partait. fceumm remplit la RAM de 0xFF au demarrage, le
/// jeu met monde (0x075F) et niveau (0x075C) a 0 dans ses 4 premieres images, le prechauffage du wrapper (120 images) avale
/// ce changement : seuls les releves READ_CORE_RAM disent ces deux adresses. Un releve tombe avant l'initialisation lisait
/// 0xFF, et un releve ne remplacait jamais une valeur connue : le depart (monde 0, niveau 0) n'armait jamais.
/// </summary>
public class SpeedrunReleveProvisoireTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string fichier) => Reglement.Lire(File.ReadAllText(Fichier(fichier)));

    private static Reglement Any => Lire("super-mario-bros.any.json");

    private static LignesDuMem Mem => LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Fichier("super-mario-bros.MEM"))));

    /// <summary>Le prechauffage du wrapper (wrapper.cpp, g_warmup_frames_remaining = 120) : il retient les valeurs sans rien emettre.</summary>
    private const long Prechauffage = 120;

    /// <summary>
    /// LA BORNE, image par image : le wrapper simule avec son prechauffage, la boucle des releves (un quart de seconde, environ
    /// 15 images) a partir de <paramref name="premierReleve"/>, et la tentative. Rend l'evaluateur et la frame du depart.
    /// </summary>
    private static (Evaluateur Ev, long? Depart) Jouer(string trace, Reglement r, long premierReleve)
    {
        var sim = new WrapperSimule(Mem);
        var ev = new Evaluateur(r);
        long? dernierReleve = null;
        foreach (var image in TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))).Lectures.GroupBy(l => l.Frame))
        {
            var frame = image.Key;
            var signaux = image.SelectMany(l => sim.Lire(l.Frame, l.Adresse, l.Type, l.Valeur)).ToList();
            if (frame >= premierReleve && (dernierReleve is null || frame - dernierReleve >= 15))
            {
                dernierReleve = frame;
                foreach (var a in ev.AdressesInconnues)
                    if (sim.Valeur(a) is { } v) ev.Relever(a, v);
            }

            if (frame > Prechauffage && signaux.Count > 0) ev.RecevoirImage(signaux);
        }

        return (ev, ev.Resultat().Journal.FirstOrDefault(c => c.Role == "start")?.Frame);
    }

    [Fact]
    public void Un_releve_lu_avant_l_initialisation_du_jeu_n_empeche_plus_le_depart()
    {
        // Premier releve a l'image 1 : la RAM est encore a 0xFF (trace du banc fceumm du 03/11, Start a l'image 41).
        var (ev, depart) = Jouer("smb-1-1-mat", Any, premierReleve: 1);

        Assert.Equal(196, depart);
        Assert.Equal(EtatTentative.EnCourse, ev.Etat);
        Assert.Equal(0, ev.ValeurConnue(0x075F));
        Assert.Equal(0, ev.ValeurConnue(0x075C));
    }

    [Fact]
    public void Un_releve_lu_apres_l_initialisation_part_comme_avant()
    {
        var (ev, depart) = Jouer("smb-1-1-mat", Any, premierReleve: 10);

        Assert.Equal(196, depart);
        Assert.Equal(EtatTentative.EnCourse, ev.Etat);
    }

    [Fact]
    public void Au_titre_le_releve_provisoire_se_relit_et_le_Start_fait_partir_le_chrono()
    {
        // La sequence de la borne du joueur : releves a 0xFF, titre et demo (DemoTimer), Start, main au joueur.
        var ev = new Evaluateur(Any);
        foreach (var a in ev.AdressesInconnues) ev.Relever(a, 0xFF);
        Assert.True(ev.EstProvisoire(0x075F));

        ev.RecevoirImage([new Signal(130, "DEMO_MODE", 0x07A2, 0x17)]);
        // Le jeu tourne : les valeurs provisoires se relisent.
        Assert.Contains(0x075FL, ev.AdressesInconnues);
        Assert.Contains(0x075CL, ev.AdressesInconnues);
        foreach (var a in ev.AdressesInconnues) ev.Relever(a, 0);
        Assert.False(ev.EstProvisoire(0x075F));
        Assert.Empty(ev.AdressesInconnues);

        ev.RecevoirImage([new Signal(1400, "MODE_STATE", 0x0770, 1)]);
        ev.RecevoirImage([new Signal(1402, "LIVES_STATE", 0x000E, 0)]);
        ev.RecevoirImage([new Signal(1556, "GAME_PLAYING", 0x000E, 8)]);

        Assert.Equal(EtatTentative.EnCourse, ev.Etat);
        Assert.Equal(1556, ev.Resultat().Journal.Single(c => c.Role == "start").Frame);
    }

    [Fact]
    public void Un_releve_sur_n_est_jamais_remplace()
    {
        // La regle du 30/10 tient apres la premiere image : une lecture partie avant un evenement revient parfois apres lui.
        var ev = new Evaluateur(Any);
        ev.RecevoirImage([new Signal(130, "DEMO_MODE", 0x07A2, 0x17)]);
        ev.Relever(0x075F, 0);
        Assert.False(ev.EstProvisoire(0x075F));

        ev.Relever(0x075F, 0xFF);

        Assert.Equal(0, ev.ValeurConnue(0x075F));
        Assert.DoesNotContain(0x075FL, ev.AdressesInconnues);
    }

    [Fact]
    public void Un_evenement_rend_l_adresse_sure()
    {
        var ev = new Evaluateur(Any);
        ev.Relever(0x0770, 0xFF);
        Assert.True(ev.EstProvisoire(0x0770));

        ev.RecevoirImage([new Signal(400, "MODE_STATE", 0x0770, 1)]);
        ev.Relever(0x0770, 0);

        Assert.False(ev.EstProvisoire(0x0770));
        Assert.Equal(1, ev.ValeurConnue(0x0770));
    }

    [Fact]
    public void Un_mauvais_releve_ne_fait_jamais_partir_le_chrono()
    {
        // Monde 1 relu apres la premiere image : la valeur sure (1) tient, le depart reste desarme.
        var ev = new Evaluateur(Any);
        foreach (var a in ev.AdressesInconnues) ev.Relever(a, 0);
        ev.RecevoirImage([new Signal(130, "DEMO_MODE", 0x07A2, 0x17)]);
        ev.Relever(0x075F, 1);
        ev.RecevoirImage([new Signal(1400, "MODE_STATE", 0x0770, 1)]);
        ev.RecevoirImage([new Signal(1556, "GAME_PLAYING", 0x000E, 8)]);

        Assert.Equal(EtatTentative.Preparee, ev.Etat);
    }

    [Fact]
    public void La_valeur_que_quitte_le_depart_ne_se_relit_jamais()
    {
        // Rambo d'Ash (14/11) : relue, l'adresse quittee rendrait la valeur d'apres et le depart ne partirait plus.
        var ev = new Evaluateur(Lire("metal-slug-3.mission-1-rambo.json"));
        foreach (var a in ev.AdressesInconnues) ev.Relever(a, a == 0x10FDB6 ? 1 : 0);
        Assert.True(ev.EstProvisoire(0x1002C1));

        ev.RecevoirImage([new Signal(300, "GENERAL_TIMER", 0x10FDB6, 1)]);

        Assert.DoesNotContain(0x1002C1L, ev.AdressesInconnues);
        ev.RecevoirImage([new Signal(400, "MOUNT_STATE", 0x1002C1, 0x01)]);
        Assert.Equal(EtatTentative.EnCourse, ev.Etat);
    }

    [Fact]
    public void Hotkey_et_gauche_repart_toujours_sur_une_tentative_neuve()
    {
        // Le chemin qui marchait chez le joueur : RESET, tentative neuve, releves, Start.
        var s = new SessionSpeedrun("super-mario-bros.any", Any);
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0xFF);
        s.RecevoirImage([new Signal(130, "DEMO_MODE", 0x07A2, 0x17)]);

        var reponse = s.Commander(new CommandeSpeedrun("r1", s.SessionId, SessionSpeedrun.ActionRelancer), out var effet);
        Assert.Equal("pending", reponse.Statut);
        Assert.Equal(EffetDemande.Relancer, effet);
        s.RelanceFaite();
        Assert.Equal(2, s.Numero);

        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        s.RecevoirImage([new Signal(1894, "LIVES_STATE", 0x000E, 0), new Signal(1894, "MODE_STATE", 0x0770, 1)]);
        s.RecevoirImage([new Signal(2054, "GAME_PLAYING", 0x000E, 8)]);

        Assert.Equal(SessionSpeedrun.Running, s.Etat);
    }

    [Fact]
    public void Le_premier_Start_de_la_session_fait_partir_le_chrono_sans_relance()
    {
        // Le cas du signalement, au niveau de la session : releves trop tot, titre, releve suivant, Start.
        var s = new SessionSpeedrun("super-mario-bros.any", Any);
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0xFF);
        s.RecevoirImage([new Signal(130, "DEMO_MODE", 0x07A2, 0x17)]);
        Assert.Equal(0xFF, s.ProvisoireDifferente(0x075F, 0));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        Assert.Null(s.ProvisoireDifferente(0x075F, 0));

        s.RecevoirImage([new Signal(1400, "MODE_STATE", 0x0770, 1)]);
        s.RecevoirImage([new Signal(1556, "GAME_PLAYING", 0x000E, 8)]);

        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Assert.Equal(1, s.Numero);
    }
}
