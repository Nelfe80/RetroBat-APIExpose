using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La foule du HUD : une rangee d'avatars, qui entre, sort, reagit et flane.
///
/// Deux choses sont en jeu et toutes deux se voient mal a l'oeil. La SCENE doit rester lisible : pas
/// plus d'avatars que la machine n'en dessine, et qui reagit doit etre vu. Et elle doit rester CALME :
/// le HUD recompose a vingt-cinq images par seconde des qu'un avatar bouge, et ce compositing plein
/// ecran a deja hache le son d'une borne modeste.
/// </summary>
public sealed class LiveCrowdModelTests
{
    private const int Largeur = 1920;

    /// <summary>Des acteurs realistes : des pseudonymes hexadecimaux de 32 caracteres.</summary>
    private static string[] Acteurs(int combien)
    {
        var sortie = new string[combien];
        for (var i = 0; i < combien; i++)
        {
            sortie[i] = (i * 2654435761u).ToString("x8") + new string('0', 24);
        }
        return sortie;
    }

    private static void Derouler(LiveCrowdModel foule, long depuis, long jusqua,
        Action<LiveCrowdModel.Instantane, long>? chaque = null, int pas = 40)
    {
        for (var t = depuis; t <= jusqua; t += pas)
        {
            var etat = foule.Relever(t, Largeur);
            chaque?.Invoke(etat, t);
        }
    }

    [Fact]
    public void L_horloge_de_la_foule_est_celle_du_temps_de_fonctionnement()
    {
        // Le HUD lit `Environment.TickCount64`. Si l'horloge de la foule s'en ecartait, plus aucun
        // emoji ne serait dessine et aucun pseudo ne disparaitrait, SANS erreur pour le signaler.
        var attendu = Environment.TickCount64;
        var obtenu = LiveCrowdModel.Maintenant();
        Assert.InRange(obtenu, attendu - 2000, attendu + 2000);
        Assert.True(obtenu < 1_000_000_000_000L, "l'horloge de la foule ne doit pas etre une horloge epoch");
    }

    [Fact]
    public void La_duree_des_etiquettes_decroit_puis_tombe_a_zero()
    {
        var attendues = new[] { 1800, 1625, 1450, 1275, 1100, 925, 750, 575, 0 };
        var obtenues = Enumerable.Range(0, attendues.Length).Select(LiveCrowdModel.DureeEtiquette).ToArray();
        Assert.Equal(attendues, obtenues);
    }

    [Fact]
    public void Le_saut_suit_l_intensite_et_reste_borne()
    {
        Assert.Equal(8, LiveCrowdModel.HauteurSaut(1));
        Assert.Equal(16, LiveCrowdModel.HauteurSaut(2));
        Assert.Equal(24, LiveCrowdModel.HauteurSaut(3));
        Assert.Equal(8, LiveCrowdModel.HauteurSaut(-3));
        Assert.Equal(24, LiveCrowdModel.HauteurSaut(99));
    }

    [Fact]
    public void La_silhouette_de_remplacement_tombe_juste_dans_le_sprite()
    {
        // Le pixel carre : le gabarit de huit doit se diviser en blocs entiers dans soixante-quatre.
        Assert.Equal(64, LiveCrowdModel.TailleSprite);
        Assert.Equal(0, LiveCrowdModel.TailleSprite % LiveCrowdModel.Gabarit.Length);
    }

    [Fact]
    public void La_scene_tient_sa_capacite_et_pas_plus()
    {
        var foule = new LiveCrowdModel(7);
        foule.Poser(Acteurs(100), 100, 1000);

        var etat = foule.Relever(1000, Largeur);

        Assert.Equal(LiveCrowdModel.CapaciteDepart, etat.Scene.Count);
        Assert.Equal(100, etat.Total);
    }

    [Fact]
    public void Au_premier_releve_la_foule_se_pose_sur_toute_la_largeur_sans_defiler()
    {
        var foule = new LiveCrowdModel(7);
        foule.Poser(Acteurs(24), 24, 1000);

        var etat = foule.Relever(1000, Largeur);

        Assert.False(etat.Bouge);
        Assert.All(etat.Scene, s => Assert.InRange(s.X, 0, Largeur - LiveCrowdModel.TailleSprite));
        // Etalee d'un bord a l'autre, pas tassee d'un cote.
        Assert.True(etat.Scene.Min(s => s.X) < Largeur / 8);
        Assert.True(etat.Scene.Max(s => s.X) > Largeur * 7 / 8 - LiveCrowdModel.TailleSprite);
    }

    [Fact]
    public void L_espacement_n_est_jamais_regulier()
    {
        var foule = new LiveCrowdModel(7);
        foule.Poser(Acteurs(24), 24, 1000);

        var xs = foule.Relever(1000, Largeur).Scene.Select(s => s.X).OrderBy(x => x).ToArray();
        var ecarts = xs.Zip(xs.Skip(1), (a, b) => b - a).Distinct().Count();

        Assert.True(ecarts > 6, $"seulement {ecarts} ecarts differents : la foule parait alignee");
    }

    [Fact]
    public void La_profondeur_ordonne_le_dessin_du_fond_vers_le_devant()
    {
        var foule = new LiveCrowdModel(7);
        foule.Poser(Acteurs(24), 24, 1000);

        var scene = foule.Relever(1000, Largeur).Scene;

        Assert.All(scene, s => Assert.InRange(s.Y, 0, LiveCrowdModel.Profondeur));
        Assert.True(scene.Select(s => s.Y).Distinct().Count() >= 3, "sans profondeur, personne ne passe devant personne");
        for (var i = 1; i < scene.Count; i++)
        {
            Assert.True(scene[i - 1].Y <= scene[i].Y, "l'ordre de dessin doit aller du fond vers le devant");
        }
    }

    [Fact]
    public void Un_nouveau_venu_entre_par_un_bord()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(10);
        foule.Poser(acteurs, 10, 1000);
        foule.Relever(1000, Largeur);

        var nouveau = "ffffffff" + new string('0', 24);
        foule.Poser(acteurs.Append(nouveau).ToArray(), 11, 2000);
        var etat = foule.Relever(2000, Largeur);

        var s = Assert.Single(etat.Scene, x => x.Acteur == nouveau);
        // A un pas pres : au premier releve il a deja pu poser un pied sur l'ecran.
        Assert.True(s.X <= -LiveCrowdModel.TailleSprite + 8 || s.X >= Largeur - 8, $"entre a {s.X}, pas par un bord");
        Assert.NotEqual(0, s.Image);
        Assert.True(etat.Bouge);
    }

    [Fact]
    public void Qui_part_sort_par_un_bord_en_marchant_puis_disparait()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(10);
        foule.Poser(acteurs, 10, 1000);
        var partant = foule.Relever(1000, Largeur).Scene.OrderBy(s => s.X).First().Acteur;

        foule.Poser(acteurs.Where(a => a != partant).ToArray(), 9, 1100);
        var pendant = foule.Relever(1100, Largeur);

        var s = Assert.Single(pendant.Scene, x => x.Acteur == partant);
        Assert.True(s.Sortant);
        Assert.True(pendant.Bouge);

        LiveCrowdModel.Instantane? fin = null;
        Derouler(foule, 1140, 60000, (etat, _) => fin = etat);
        Assert.DoesNotContain(fin!.Scene, x => x.Acteur == partant);
    }

    [Fact]
    public void Qui_reagit_prend_la_place_du_plus_silencieux()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(40);
        foule.Poser(acteurs, 40, 1000);
        var avant = foule.Relever(1000, Largeur);
        var enScene = avant.Scene.Select(s => s.Acteur).ToHashSet(StringComparer.Ordinal);
        var dehors = acteurs.First(a => !enScene.Contains(a));

        // Un present en scene vient de reagir : il est protege, il ne cede pas sa place.
        var protege = avant.Scene[0].Acteur;
        foule.Reagir(protege, "wow", 1, "", 25000);
        foule.Relever(25000, Largeur);   // le HUD la voit : son vol part, et sera fini a 30000
        var attendu = enScene.Where(a => a != protege).OrderBy(a => a, StringComparer.Ordinal).First();

        foule.Reagir(dehors, "hype", 3, "Nelfe80", 30000);
        var apres = foule.Relever(30000, Largeur);

        Assert.Contains(apres.Scene, s => s.Acteur == dehors && !s.Sortant);
        Assert.Contains(apres.Scene, s => s.Acteur == protege && !s.Sortant);
        Assert.Equal(LiveCrowdModel.CapaciteDepart, apres.Scene.Count(s => !s.Sortant));
        Assert.Equal(attendu, Assert.Single(apres.Scene, s => s.Sortant).Acteur);
        Assert.Single(apres.Vols);
    }

    [Fact]
    public void Une_seule_flanerie_a_la_fois_et_la_foule_reste_calme()
    {
        var foule = new LiveCrowdModel(11);
        foule.Poser(Acteurs(24), 24, 0);
        foule.Relever(0, Largeur);

        int images = 0, animees = 0, flaneries = 0;
        var marchait = false;
        Derouler(foule, 40, 180_000, (etat, t) =>
        {
            images++;
            var marcheurs = etat.Scene.Count(s => s.Image != 0);
            Assert.True(marcheurs <= 1, $"{marcheurs} avatars marchent ensemble a {t} ms");
            if (etat.Bouge) animees++;
            if (marcheurs == 1 && !marchait) flaneries++;
            marchait = marcheurs == 1;
        });

        Assert.InRange(flaneries, 6, 30);
        Assert.True(animees < images * 0.35, $"la foule bouge {animees} images sur {images}");
    }

    [Fact]
    public void Chaque_famille_a_son_allure()
    {
        var familles = new[]
        {
            "run-and-gun", "shmup", "beatemup", "fighting", "racing", "platformer", "puzzle",
            "maze", "sports", "pinball", "horror", "rpg", "shooter",
        };
        Assert.Equal(familles.Length, LiveCrowdModel.Allures.Count);
        foreach (var famille in familles)
        {
            var a = LiveCrowdModel.AllureDe(famille);
            Assert.True(a.PixelsParPas > 0 && a.MsParPas > 0 && a.Amplitude > 0, famille);
        }
        Assert.Equal(familles.Length, LiveCrowdModel.Allures.Values.Distinct().Count());
    }

    [Fact]
    public void La_capacite_suit_la_machine_dans_ses_bornes()
    {
        var foule = new LiveCrowdModel(7);
        foule.Poser(Acteurs(60), 60, 1000);
        foule.Relever(1000, Largeur);

        foule.FixerCapacite(100);
        Assert.Equal(LiveCrowdModel.CapaciteMax, foule.CapaciteCourante);
        Assert.Equal(LiveCrowdModel.CapaciteMax, foule.Relever(1100, Largeur).Scene.Count(s => !s.Sortant));

        foule.FixerCapacite(1);
        Assert.Equal(LiveCrowdModel.CapaciteMin, foule.CapaciteCourante);
        var moins = foule.Relever(1200, Largeur);
        Assert.Equal(LiveCrowdModel.CapaciteMin, moins.Scene.Count(s => !s.Sortant));
        // Ceux en trop sortent en marchant : ils ne s'evaporent pas.
        Assert.Contains(moins.Scene, s => s.Sortant);
    }

    [Fact]
    public void Un_avatar_inconnu_se_demande_une_fois_puis_apres_un_delai()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(5);
        foule.Poser(acteurs, 5, 1000);

        Assert.Equal(5, foule.AvatarsManquants(60, 1000).Count);
        Assert.Empty(foule.AvatarsManquants(60, 2000));

        foule.PoserAvatars(new Dictionary<string, LiveCrowdModel.Avatar>
        {
            [acteurs[0]] = new("Nelfe80", "shmup", 0, null, null),
        });
        Assert.Equal(4, foule.AvatarsManquants(60, 40_000).Count);
    }

    [Fact]
    public void Une_reaction_horodatee_par_le_modele_est_visible_tout_de_suite()
    {
        var foule = new LiveCrowdModel();
        var acteurs = Acteurs(4);
        foule.Poser(acteurs, 4);

        foule.Reagir(acteurs[0], "hype", 2, "Nelfe80");

        var etat = foule.Relever(LiveCrowdModel.Maintenant(), Largeur);
        Assert.Single(etat.Vols);
        Assert.Single(etat.Etiquettes);
        var age = LiveCrowdModel.Maintenant() - etat.Vols[0].Depuis;
        Assert.InRange(age, 0, LiveCrowdModel.DureeVol);
    }

    [Fact]
    public void Une_reaction_sans_place_ne_dessine_rien_et_une_reaction_fait_sauter()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        foule.Poser(acteurs, 3, 1000);

        foule.Reagir("inconnu", "hype", 2, "Quelqu'un", 1000);
        var vide = foule.Relever(1000, Largeur);
        Assert.Empty(vide.Vols);
        Assert.Empty(vide.Etiquettes);
        Assert.False(vide.Bouge);

        foule.Reagir(acteurs[0], "hype", 2, "Nelfe80", 1000);
        var plein = foule.Relever(1000, Largeur);
        Assert.Single(plein.Vols);
        Assert.Single(plein.Etiquettes);

        var milieu = foule.Relever(1000 + LiveCrowdModel.DureeSaut / 2, Largeur);
        Assert.Equal(LiveCrowdModel.HauteurSaut(2), milieu.Scene.Single(s => s.Acteur == acteurs[0]).Hauteur);
    }

    [Fact]
    public void Ce_qui_est_fini_disparait()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(2);
        foule.Poser(acteurs, 2, 1000);
        foule.Reagir(acteurs[0], "wow", 1, "Vero", 1000);
        Assert.True(foule.Anime);
        // Le premier releve amorce le saut et le vol : c'est de la que leur duree se compte.
        foule.Relever(1000, Largeur);

        // TROIS durees, voulues : le saut est bref, l'emoji vole plus longtemps, et le nom reste le
        // plus longtemps. On doit pouvoir lire qui a reagi apres que son emoji a disparu.
        var pendant = foule.Relever(1000 + LiveCrowdModel.DureeVol + 1, Largeur);
        Assert.Empty(pendant.Vols);
        Assert.Single(pendant.Etiquettes);
        Assert.True(pendant.Bouge);

        var apres = foule.Relever(1000 + LiveCrowdModel.DureeEtiquette(0) + 1, Largeur);
        Assert.Empty(apres.Vols);
        Assert.Empty(apres.Etiquettes);
        Assert.False(apres.Bouge);
        Assert.False(foule.Anime);
    }

    [Fact]
    public void Un_saut_part_au_premier_releve_et_non_a_l_arrivee_de_la_reaction()
    {
        // Au repos le HUD ne se reveille que toutes les 500 ms, et un saut dure 420 ms. Amorce a
        // l'arrivee, un saut etait deja fini quand le HUD le dessinait pour la premiere fois : a
        // l'ecran, personne ne sautait jamais alors que l'emoji et le nom paraissaient.
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        foule.Poser(acteurs, 3, 1000);
        foule.Relever(1000, Largeur);

        foule.Reagir(acteurs[0], "hype", 1, "Nelfe80", 1000);

        // Premier regard 480 ms plus tard : le saut COMMENCE, il n'est pas deja retombe.
        var premier = foule.Relever(1480, Largeur);
        Assert.True(premier.Bouge);
        Assert.Single(premier.Vols);
        Assert.Equal(1480, premier.Vols[0].Depuis);

        var milieu = foule.Relever(1480 + LiveCrowdModel.DureeSaut / 2, Largeur);
        Assert.Equal(LiveCrowdModel.HauteurSaut(1), milieu.Scene.Single(x => x.Acteur == acteurs[0]).Hauteur);

        var fin = foule.Relever(1480 + LiveCrowdModel.DureeSaut + 1, Largeur);
        Assert.Equal(0, fin.Scene.Single(x => x.Acteur == acteurs[0]).Hauteur);
    }

    [Fact]
    public void Le_plafond_retire_la_plus_ancienne_etiquette()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(20);
        foule.Poser(acteurs, 20, 1000);

        for (var i = 0; i < 9; i++)
        {
            foule.Reagir(acteurs[i], "laugh", 1, "nom" + i, 1000);
        }

        var etat = foule.Relever(1000, Largeur);
        Assert.True(etat.Etiquettes.Count <= 8);
        Assert.Equal(9, etat.Vols.Count);
    }
}
