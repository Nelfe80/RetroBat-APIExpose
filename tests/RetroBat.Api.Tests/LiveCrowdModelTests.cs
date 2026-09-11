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

    /// <summary>Les avatars de ces acteurs, planche connue : sans elle, ils attendent hors scene.</summary>
    private static void Avatars(LiveCrowdModel foule, IEnumerable<string> acteurs)
    {
        var table = new Dictionary<string, LiveCrowdModel.Avatar>(StringComparer.Ordinal);
        var i = 0;
        foreach (var acteur in acteurs)
        {
            table[acteur] = new LiveCrowdModel.Avatar(
                "joueur" + i, "shmup", 0, new string((char) ('a' + i % 6), 64), "atelier64-v7");
            i++;
        }
        foule.PoserAvatars(table);
    }

    /// <summary>
    /// Fait entrer tout le monde : les entrees sont espacees de 400 ms et chacune est une marche
    /// depuis un bord, donc une foule ne se remplit pas dans la milliseconde.
    /// </summary>
    private static LiveCrowdModel.Instantane Installer(LiveCrowdModel foule, long depuis, int combien)
        => Installer(foule, depuis, combien, out _);

    /// <summary>
    /// Comme ci-dessus, et rend l'instant ou tout le monde est entre ET immobile. Une entree depuis
    /// le bord oppose, a cent pixels par seconde, prend jusqu'a quinze secondes : une duree fixe
    /// laissait parfois un avatar en pleine marche, et un test qui le pilotait lisait une position
    /// qui n'etait deja plus la sienne.
    /// </summary>
    private static LiveCrowdModel.Instantane Installer(LiveCrowdModel foule, long depuis, int combien, out long fin)
    {
        var t = depuis;
        var etat = foule.Relever(t, Largeur);
        var attendus = Math.Min(combien, foule.CapaciteCourante);
        var limite = depuis + combien * 500L + 6000L + 60000L;
        while (t <= limite)
        {
            t += 40;
            etat = foule.Relever(t, Largeur);
            if (t >= depuis + combien * 500L + 6000L
                && etat.Scene.Count(s => !s.Sortant) >= attendus
                && etat.Scene.All(s => s.Image == 0 && !s.Sortant))
            {
                break;
            }
        }
        fin = t;
        return etat;
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
        var acteurs = Acteurs(100);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 100, 1000);

        var etat = Installer(foule, 1000, LiveCrowdModel.CapaciteDepart);

        Assert.Equal(LiveCrowdModel.CapaciteDepart, etat.Scene.Count);
        Assert.Equal(100, etat.Total);
    }

    [Fact]
    public void Au_premier_releve_la_foule_se_pose_sur_toute_la_largeur_sans_defiler()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(24);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 24, 1000);

        var etat = Installer(foule, 1000, 24);

        // Tout le monde est arrive. La foule ne defile pas : au plus UN avatar marche, celui de la
        // flanerie programmee, et personne ne sort.
        Assert.Equal(24, etat.Scene.Count);
        Assert.True(etat.Scene.Count(s => s.Image != 0) <= 1, "plus d'un avatar en marche");
        Assert.DoesNotContain(etat.Scene, s => s.Sortant);
        Assert.All(etat.Scene, s => Assert.InRange(s.X, 0, Largeur - LiveCrowdModel.TailleSprite));
        // Etalee d'un bord a l'autre, pas tassee d'un cote.
        Assert.True(etat.Scene.Min(s => s.X) < Largeur / 8);
        Assert.True(etat.Scene.Max(s => s.X) > Largeur * 7 / 8 - LiveCrowdModel.TailleSprite);
    }

    [Fact]
    public void L_espacement_n_est_jamais_regulier()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(24);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 24, 1000);

        var xs = Installer(foule, 1000, 24).Scene.Select(s => s.X).OrderBy(x => x).ToArray();
        var ecarts = xs.Zip(xs.Skip(1), (a, b) => b - a).Distinct().Count();

        Assert.True(ecarts > 6, $"seulement {ecarts} ecarts differents : la foule parait alignee");
    }

    [Fact]
    public void La_profondeur_ordonne_le_dessin_du_fond_vers_le_devant()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(24);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 24, 1000);

        var scene = Installer(foule, 1000, 24).Scene;

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
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 10, 1000);
        Installer(foule, 1000, 10);

        var nouveau = "ffffffff" + new string('0', 24);
        Avatars(foule, new[] { nouveau });
        foule.Poser(acteurs.Append(nouveau).ToArray(), 11, 30000);
        var etat = foule.Relever(30000, Largeur);

        var s = Assert.Single(etat.Scene, x => x.Acteur == nouveau);
        // A un pas pres : au premier releve il a deja pu poser un pied sur l'ecran.
        Assert.True(s.X <= -LiveCrowdModel.TailleSprite + 8 || s.X >= Largeur - 8, $"entre a {s.X}, pas par un bord");
        Assert.NotEqual(0, s.Image);
        Assert.True(etat.Bouge);
    }

    [Fact]
    public void Sans_planche_on_attend_hors_scene_puis_on_entre_quand_meme()
    {
        // Personne n'est DESSINE sur la scene : tant que la planche n'est pas la, son spectateur
        // reste invisible, donc on ne voit jamais une silhouette se transformer sur place.
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        foule.Poser(acteurs, 3, 1000);

        Assert.Empty(foule.Relever(1000, Largeur).Scene);
        Assert.Empty(foule.Relever(6000, Largeur).Scene);

        // La planche arrive : il entre par un bord, avec son avatar.
        Avatars(foule, new[] { acteurs[0] });
        var avec = foule.Relever(6040, Largeur);
        var entrant = Assert.Single(avec.Scene);
        Assert.Equal(acteurs[0], entrant.Acteur);
        Assert.NotNull(entrant.Avatar);

        // Les deux autres n'attendent pas indefiniment : passe le delai, ils entrent en silhouette.
        var apres = Installer(foule, 20000, 3);
        Assert.Equal(3, apres.Scene.Count);
        Assert.Contains(apres.Scene, x => x.Avatar is null);
    }

    [Fact]
    public void Qui_part_sort_par_un_bord_en_marchant_puis_disparait()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(10);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 10, 1000);
        var partant = Installer(foule, 1000, 10).Scene.OrderBy(s => s.X).First().Acteur;

        foule.Poser(acteurs.Where(a => a != partant).ToArray(), 9, 30000);
        var pendant = foule.Relever(30000, Largeur);

        var s = Assert.Single(pendant.Scene, x => x.Acteur == partant);
        Assert.True(s.Sortant);
        Assert.True(pendant.Bouge);

        LiveCrowdModel.Instantane? fin = null;
        Derouler(foule, 30040, 90000, (etat, _) => fin = etat);
        Assert.DoesNotContain(fin!.Scene, x => x.Acteur == partant);
    }

    [Fact]
    public void Qui_reagit_prend_la_place_du_plus_silencieux()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(40);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 40, 1000);
        var avant = Installer(foule, 1000, LiveCrowdModel.CapaciteDepart);
        var enScene = avant.Scene.Select(s => s.Acteur).ToHashSet(StringComparer.Ordinal);
        var dehors = acteurs.First(a => !enScene.Contains(a));

        // Un present en scene vient de reagir : il est protege, il ne cede pas sa place.
        var protege = avant.Scene[0].Acteur;
        foule.Reagir(protege, "wow", 1, "", 25000);
        foule.Relever(25000, Largeur);   // le HUD la voit : son vol part, et sera fini a 40000
        var attendu = enScene.Where(a => a != protege).OrderBy(a => a, StringComparer.Ordinal).First();

        foule.Reagir(dehors, "hype", 3, "Nelfe80", 40000);
        var apres = foule.Relever(40000, Largeur);

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
        var acteurs = Acteurs(24);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 24, 0);
        // Le temps que tout le monde soit entre : ce sont les entrees, pas des flaneries.
        Installer(foule, 0, 24);

        int images = 0, animees = 0, flaneries = 0;
        var marchait = false;
        Derouler(foule, 40_000, 220_000, (etat, t) =>
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
        var acteurs = Acteurs(60);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 60, 1000);
        Installer(foule, 1000, LiveCrowdModel.CapaciteDepart);

        foule.FixerCapacite(100);
        Assert.Equal(LiveCrowdModel.CapaciteMax, foule.CapaciteCourante);
        Assert.Equal(LiveCrowdModel.CapaciteMax, Installer(foule, 60_000, 6).Scene.Count(s => !s.Sortant));

        foule.FixerCapacite(1);
        Assert.Equal(LiveCrowdModel.CapaciteMin, foule.CapaciteCourante);
        var moins = foule.Relever(120_000, Largeur);
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
        Avatars(foule, acteurs);
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
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        Installer(foule, 1000, 3);

        foule.Reagir("inconnu", "hype", 2, "Quelqu'un", 20000);
        var vide = foule.Relever(20000, Largeur);
        Assert.Empty(vide.Vols);
        Assert.Empty(vide.Etiquettes);
        // Personne ne saute pour un inconnu. Seul un flaneur peut bouger, et il ne quitte pas le sol.
        Assert.All(vide.Scene.Where(x => x.Image == 0), x => Assert.Equal(0, x.Hauteur));

        foule.Reagir(acteurs[0], "hype", 2, "Nelfe80", 20000);
        var plein = foule.Relever(20000, Largeur);
        Assert.Single(plein.Vols);
        Assert.Single(plein.Etiquettes);

        var milieu = foule.Relever(20000 + LiveCrowdModel.DureeSaut / 2, Largeur);
        Assert.Equal(LiveCrowdModel.HauteurSaut(2), milieu.Scene.Single(s => s.Acteur == acteurs[0]).Hauteur);
    }

    [Fact]
    public void Ce_qui_est_fini_disparait()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(2);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 2, 1000);
        Installer(foule, 1000, 2);
        foule.Reagir(acteurs[0], "wow", 1, "Vero", 20000);
        Assert.True(foule.Anime);
        // Le premier releve amorce le saut et le vol : c'est de la que leur duree se compte.
        foule.Relever(20000, Largeur);

        // TROIS durees, voulues : le saut est bref, l'emoji vole plus longtemps, et le nom reste le
        // plus longtemps. On doit pouvoir lire qui a reagi apres que son emoji a disparu.
        var pendant = foule.Relever(20000 + LiveCrowdModel.DureeVol + 1, Largeur);
        Assert.Empty(pendant.Vols);
        Assert.Single(pendant.Etiquettes);
        Assert.True(pendant.Bouge);

        var apres = foule.Relever(20000 + LiveCrowdModel.DureeEtiquette(0) + 1, Largeur);
        Assert.Empty(apres.Vols);
        Assert.Empty(apres.Etiquettes);
        // La reaction ne laisse rien : ni saut, ni vol, ni nom. Le HUD ne reste anime que pour la
        // flanerie programmee, jamais pour une reaction finie.
        var reactant = apres.Scene.Single(x => x.Acteur == acteurs[0]);
        if (reactant.Image == 0)
        {
            // Au repos, il est retombe. En marche, c'est lui le flaneur, et le rebond est normal.
            Assert.Equal(0, reactant.Hauteur);
        }
        Assert.True(apres.Scene.Count(x => x.Image != 0) <= 1, "plus d'un avatar en marche");
    }

    [Fact]
    public void Un_saut_part_au_premier_releve_et_non_a_l_arrivee_de_la_reaction()
    {
        // Au repos le HUD ne se reveille que toutes les 500 ms, et un saut dure 420 ms. Amorce a
        // l'arrivee, un saut etait deja fini quand le HUD le dessinait pour la premiere fois : a
        // l'ecran, personne ne sautait jamais alors que l'emoji et le nom paraissaient.
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        Installer(foule, 1000, 3);

        foule.Reagir(acteurs[0], "hype", 1, "Nelfe80", 20000);

        // Premier regard 480 ms plus tard : le saut COMMENCE, il n'est pas deja retombe.
        var premier = foule.Relever(20480, Largeur);
        Assert.True(premier.Bouge);
        Assert.Single(premier.Vols);
        Assert.Equal(20480, premier.Vols[0].Depuis);

        var milieu = foule.Relever(20480 + LiveCrowdModel.DureeSaut / 2, Largeur);
        Assert.Equal(LiveCrowdModel.HauteurSaut(1), milieu.Scene.Single(x => x.Acteur == acteurs[0]).Hauteur);

        var fin = foule.Relever(20480 + LiveCrowdModel.DureeSaut + 1, Largeur);
        Assert.Equal(0, fin.Scene.Single(x => x.Acteur == acteurs[0]).Hauteur);
    }

    [Fact]
    public void Le_plafond_retire_la_plus_ancienne_etiquette()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(20);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 20, 1000);
        Installer(foule, 1000, 20);

        for (var i = 0; i < 9; i++)
        {
            foule.Reagir(acteurs[i], "laugh", 1, "nom" + i, 30000);
        }

        var etat = foule.Relever(30000, Largeur);
        Assert.True(etat.Etiquettes.Count <= 8);
        Assert.Equal(9, etat.Vols.Count);
    }

    // ── Le spectateur pilote son avatar ───────────────────────────────────────
    //
    // On pilote un avatar IMMOBILE, juste apres son entree : plus tard, une flanerie l'aurait
    // deja emmene ailleurs, et la position lue avant ne serait plus la sienne. Et on mesure
    // avant la fin du repit de flanerie, pendant lequel il reste ou on l'a mis.

    private static string MonActeur(LiveCrowdModel foule, string[] acteurs, int lequel = 0)
    {
        foule.DefinirMoi(acteurs[lequel], "Nelfe80");
        return acteurs[lequel];
    }

    [Fact]
    public void Sans_savoir_qui_je_suis_le_panel_ne_pilote_rien()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        Installer(foule, 1000, 3, out var calme);
        var t0 = calme + 40;

        Assert.False(foule.Piloter("right", t0));
        Assert.Equal("", foule.Moi);

        // Une direction qui n'en est pas une ne fait rien non plus, meme en sachant qui je suis.
        MonActeur(foule, acteurs);
        Assert.False(foule.Piloter("select", t0));
        // Et quelqu'un que la presence ne compte pas ne se dessine pas n'importe ou.
        foule.DefinirMoi("ffffffff" + new string('0', 24), "Fantome");
        Assert.False(foule.Piloter("right", t0));
    }

    [Fact]
    public void Un_pas_a_droite_fait_marcher_d_un_demi_sprite_puis_s_arreter()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        var avant = Installer(foule, 1000, 3, out var calme);
        var t0 = calme + 40;
        var moi = MonActeur(foule, acteurs);
        var x0 = avant.Scene.Single(s => s.Acteur == moi).X;

        Assert.True(foule.Piloter("right", t0));

        // Il MARCHE : au moins un releve le montre en mouvement, tourne vers la droite.
        var enMarche = false;
        LiveCrowdModel.Instantane? fin = null;
        Derouler(foule, t0, t0 + 3000, (etat, _) =>
        {
            var s = etat.Scene.Single(x => x.Acteur == moi);
            if (s.Image != 0) { enMarche = true; Assert.Equal(LiveCrowdModel.Vue.Droite, s.Vue); }
            fin = etat;
        });
        Assert.True(enMarche, "il n'a jamais marche");
        Assert.Equal(x0 + LiveCrowdModel.PasPilote, fin!.Scene.Single(s => s.Acteur == moi).X);
        Assert.Equal(0, fin.Scene.Single(s => s.Acteur == moi).Image);
    }

    [Fact]
    public void Un_maintien_ajoute_les_pas_sans_a_coup_et_le_bord_arrete()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        var avant = Installer(foule, 1000, 3, out var calme);
        var t0 = calme + 40;
        var moi = MonActeur(foule, acteurs);
        var x0 = avant.Scene.Single(s => s.Acteur == moi).X;

        // Trois appuis rapproches, comme un maintien : la cible s'eloigne de trois pas, et
        // l'avatar ne se teleporte pas, il avance pas a pas.
        foule.Piloter("left", t0);
        foule.Piloter("left", t0 + 220);
        foule.Piloter("left", t0 + 440);
        var maxSaut = 0;
        var precedent = x0;
        Derouler(foule, t0, t0 + 3800, (etat, _) =>
        {
            var x = etat.Scene.Single(s => s.Acteur == moi).X;
            maxSaut = Math.Max(maxSaut, Math.Abs(precedent - x));
            precedent = x;
        });
        Assert.Equal(Math.Max(0, x0 - 3 * LiveCrowdModel.PasPilote), precedent);
        Assert.True(maxSaut < LiveCrowdModel.PasPilote, "un bond de " + maxSaut + " px entre deux releves");

        // Cent pas a gauche : il atteint le bord et n'en sort jamais.
        var t1 = t0 + 4000;
        for (var i = 0; i < 100; i++) { foule.Piloter("left", t1 + i * 10); }
        var minX = int.MaxValue;
        Derouler(foule, t1, t1 + 30000, (etat, _) =>
        {
            var s = etat.Scene.Single(x => x.Acteur == moi);
            Assert.True(s.X >= 0, "sorti par la gauche : " + s.X);
            minX = Math.Min(minX, s.X);
        });
        Assert.Equal(0, minX);
    }

    [Fact]
    public void Haut_recule_d_un_rang_bas_avance_et_les_bornes_tiennent()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        var avant = Installer(foule, 1000, 3, out var calme);
        var t0 = calme + 40;
        var moi = MonActeur(foule, acteurs);
        var y0 = avant.Scene.Single(s => s.Acteur == moi).Y;

        Assert.True(foule.Piloter("down", t0));
        var bas = foule.Relever(t0, Largeur).Scene.Single(s => s.Acteur == moi);
        Assert.Equal(Math.Min(LiveCrowdModel.Profondeur, y0 + 1), bas.Y);
        // Un changement de rang ne fait pas marcher : c'est un placement.
        Assert.Equal(0, bas.Image);

        for (var i = 0; i < 20; i++) { foule.Piloter("up", t0 + 100 + i); }
        Assert.Equal(0, foule.Relever(t0 + 200, Largeur).Scene.Single(s => s.Acteur == moi).Y);
        for (var i = 0; i < 20; i++) { foule.Piloter("down", t0 + 300 + i); }
        var scene = foule.Relever(t0 + 400, Largeur).Scene;
        Assert.Equal(LiveCrowdModel.Profondeur, scene.Single(s => s.Acteur == moi).Y);

        // Le rang ordonne le dessin, du fond vers le devant : tout ce qui est dessine apres moi
        // est au moins aussi devant que moi.
        var apresMoi = scene.SkipWhile(s => s.Acteur != moi).Skip(1);
        Assert.All(apresMoi, s => Assert.Equal(LiveCrowdModel.Profondeur, s.Y));
    }

    [Fact]
    public void Piloter_hors_scene_y_fait_entrer_par_un_bord_a_la_place_du_plus_silencieux()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(100);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 100, 1000);
        var plein = Installer(foule, 1000, LiveCrowdModel.CapaciteDepart, out var calme);
        var t0 = calme + 40;
        var dehors = acteurs.First(a => plein.Scene.All(s => s.Acteur != a));
        foule.DefinirMoi(dehors, "Nelfe80");

        Assert.True(foule.Piloter("right", t0));
        var arrive = false;
        Derouler(foule, t0, t0 + 20000, (etat, _) =>
        {
            var s = etat.Scene.FirstOrDefault(x => x.Acteur == dehors);
            if (s is not null && !arrive)
            {
                arrive = true;
                Assert.True(s.X <= -LiveCrowdModel.TailleSprite + 8 || s.X >= Largeur - 8, "entre a " + s.X + ", pas par un bord");
            }
            Assert.True(etat.Scene.Count(x => !x.Sortant) <= LiveCrowdModel.CapaciteDepart);
        });
        Assert.True(arrive, "il n'est jamais entre");
    }

    [Fact]
    public void Piloter_pose_mon_nom_au_dessus_de_moi_une_seule_fois()
    {
        var foule = new LiveCrowdModel(7);
        var acteurs = Acteurs(3);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 3, 1000);
        Installer(foule, 1000, 3, out var calme);
        var t0 = calme + 40;
        var moi = MonActeur(foule, acteurs);

        foule.Piloter("right", t0);
        foule.Piloter("right", t0 + 220);
        var etat = foule.Relever(t0 + 300, Largeur);
        var e = Assert.Single(etat.Etiquettes);
        Assert.Equal(moi, e.Acteur);
        Assert.Equal("Nelfe80", e.Texte);
    }

    [Fact]
    public void Un_avatar_pilote_n_est_pas_emmene_en_flanerie()
    {
        // Un seul en scene : la flanerie ne pourrait choisir que lui. Apres un pilotage, il reste
        // ou son spectateur l'a mis pendant le repit.
        var foule = new LiveCrowdModel(11);
        var acteurs = Acteurs(1);
        Avatars(foule, acteurs);
        foule.Poser(acteurs, 1, 0);
        Installer(foule, 0, 1, out var calme);
        var t0 = calme + 40;
        var moi = MonActeur(foule, acteurs);

        foule.Piloter("right", t0);
        LiveCrowdModel.Instantane? apres = null;
        Derouler(foule, t0, t0 + 1500, (etat, _) => apres = etat);
        var arrive = apres!.Scene.Single(s => s.Acteur == moi);
        Assert.Equal(0, arrive.Image);
        Derouler(foule, t0 + 1500, t0 + LiveCrowdModel.RepitPilote, (etat, t) =>
        {
            Assert.Equal(arrive.X, etat.Scene.Single(s => s.Acteur == moi).X);
        });
    }
}
