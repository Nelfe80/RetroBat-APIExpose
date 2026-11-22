using System.Text;
using RetroBat.Api.Deck.Canal;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE CANAL D'AFFICHAGE DU DECK (1.9.211, CDC Canal v0.1, decisions du user du 22/11) : lecture du bloc du wrapper 0.348, conversion
/// en pixels d'ecran, message deck.live, allumage par l'API. AFFICHAGE SEULEMENT.
/// </summary>
public class CanalDuDeckTests
{
    // Un pid qui n'est celui d'aucun RetroArch : les essais de memoire partagee ne croisent jamais un vrai canal.
    private static int PidDEssai(int n) => 910_000 + n;

    private static (CanalEcrivainSimule Ecrivain, CanalDescripteur Descripteur) Neuf(uint pid = 1)
    {
        var e = CanalScenarioSimule.DaiOuJou(pid);
        var d = CanalFormat.LireDescripteur(e.Tampon);
        Assert.NotNull(d);
        return (e, d!);
    }

    private static (int[] Champs, int[][][] Tables) Bloc(int distance, int sx, int sy, params (int Slot, int Type, int X, int Y, int Vie, int Drapeau)[] ennemis)
    {
        // les bruts comme le jeu les ecrit (formules du labo du 22/11) : vaisseau x = vx / 64, y = 456 - vy / 64 ; ennemi x = ax / 64,
        // y = 447 - ay / 64 (ay = mot +2, ax = mot +4)
        var champs = new int[CanalScenarioSimule.Champs.Length];
        champs[CanalScenarioSimule.Temps] = 100;
        champs[CanalScenarioSimule.Distance] = distance;
        champs[CanalScenarioSimule.Vies] = 2;
        champs[CanalScenarioSimule.Niveau] = 1;
        (champs[CanalScenarioSimule.VaisseauX], champs[CanalScenarioSimule.VaisseauY]) = CanalScenarioSimule.BrutDuVaisseau(sx, sy);
        champs[CanalScenarioSimule.Combo] = 40;
        var objets = ennemis.Where(e => e.Type != 0).OrderBy(e => e.Slot).Select(e => new[] { e.Slot, e.Type, e.Vie }).ToArray();
        var aff = ennemis.OrderBy(e => e.Slot).Select(e =>
        {
            var (ay, ax) = CanalScenarioSimule.BrutDeLAffichage(e.X, e.Y);
            return new[] { e.Slot, e.Drapeau, ay, ax };
        }).ToArray();
        return (champs, [objets, aff, []]);
    }

    [Fact]
    public void Le_descripteur_et_un_bloc_se_relisent_comme_le_wrapper_les_ecrit()
    {
        var (e, d) = Neuf(4242);
        Assert.Equal("dodonpachi-daioujou", d.Nom);
        Assert.Equal(["temps", "distance", "defil_fin", "camera", "niveau", "vies", "vaisseau_x", "vaisseau_y", "mort_anim", "invincible", "combo", "chaine_bcd"],
            d.Champs.Select(c => c.Nom));
        Assert.Equal(["objets", "affichage", "tirs"], d.Tables.Select(t => t.Nom));
        Assert.Equal(["type", "vie"], d.Tables[0].Colonnes.Select(c => c.Nom));
        Assert.Equal(["drapeau", "ay", "ax"], d.Tables[1].Colonnes.Select(c => c.Nom));

        var (champs, tables) = Bloc(321, 112, 380, (5, 0x8200, 100, 200, 9, 0xA200));
        e.Ecrire(77, champs, tables);
        var bloc = new CanalBloc(d);
        Assert.True(CanalFormat.Lire(e.Tampon, d, bloc));
        Assert.Equal(77u, bloc.Image);
        Assert.Equal(0u, bloc.Etat);
        Assert.Equal(321, bloc.Champs[d.Champ("distance")]);
        var aff = bloc.Tables[d.Table("affichage")];
        Assert.Equal(1, aff.Ecrites);
        Assert.Equal(5, aff.Emplacement(0));
        Assert.Equal(0xA200, aff.Colonne(0, 0));
        Assert.Equal(0u, CanalFormat.Sequence(e.Tampon) & 1);   // paire : stable
    }

    [Fact]
    public void Un_tampon_qui_n_est_pas_un_canal_est_refuse()
    {
        Assert.Equal(-1, CanalFormat.TailleDeclaree(new byte[100]));
        Assert.Null(CanalFormat.LireDescripteur(new byte[64]));
        var (e, d) = Neuf();
        var tronque = e.Tampon.AsSpan(0, e.Tampon.Length - 10);
        Assert.Null(CanalFormat.LireDescripteur(tronque));
        // un autre .VIEW (empreinte differente) : le bloc ne colle plus au descripteur lu avant
        var autre = CanalScenarioSimule.DaiOuJou(2, "autre");
        Assert.False(CanalFormat.Lire(autre.Tampon, d, new CanalBloc(d)));
    }

    [Fact]
    public void La_conversion_donne_des_pixels_d_ecran_du_jeu()
    {
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        Assert.True(calcul.Utile);
        var (champs, tables) = Bloc(321, 112, 380, (5, 0x8200, 100, 200, 9, 0xA200));
        e.Ecrire(1, champs, tables);
        var bloc = new CanalBloc(d);
        CanalFormat.Lire(e.Tampon, d, bloc);
        var m = calcul.Convertir(bloc);

        Assert.True(m.Active);
        Assert.Equal(321, m.Dist);
        Assert.Equal(224, m.ScreenW);
        Assert.Equal(448, m.ScreenH);
        Assert.Equal([112, 380], m.Ship);
        Assert.Equal([5, 100, 200, 1, 9], m.Enemies);   // emplacement, x, y, classe lourde, vie
        Assert.Equal([40, 56], m.Combo);                // jauge du combo : maximum du profil (le .VIEW n'a pas de combo_max)
    }

    [Fact]
    public void Les_formules_sont_celles_mesurees_par_le_labo()
    {
        // valeurs brutes relevees par le labo (RAPPORT_LABO_VIEW, 22/11) : vaisseau x = vx / 64, y = 456 - vy / 64 (pas de +3) ;
        // ennemi x = ax / 64, y = 447 - ay / 64 (ay = mot +2, ax = mot +4)
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var champs = new int[CanalScenarioSimule.Champs.Length];
        champs[CanalScenarioSimule.Vies] = 2;
        champs[CanalScenarioSimule.VaisseauX] = 6400;    // 100 px
        champs[CanalScenarioSimule.VaisseauY] = 6400;    // 456 - 100 = 356
        e.Ecrire(1, champs, [[[3, 0x1101, 100]], [[3, 0xA201, 12800, 3200]], []]);
        var bloc = new CanalBloc(d);
        CanalFormat.Lire(e.Tampon, d, bloc);
        var m = calcul.Convertir(bloc);
        Assert.Equal([100, 356], m.Ship);
        Assert.Equal([3, 50, 247, 0, 100], m.Enemies);   // x = 3200 / 64, y = 447 - 12800 / 64
    }

    [Fact]
    public void Une_case_non_dessinee_ou_de_vie_nulle_n_est_pas_un_ennemi()
    {
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var bloc = new CanalBloc(d);
        // 0xA200 et 0xA201 (les plus frequents en jeu) sont DESSINES ; 0x8200 ne l'est pas ; un ventilateur 0x0500 a une vie nulle
        var (champs, tables) = Bloc(10, 100, 380,
            (1, 0x1101, 60, 100, 1, 0xA200), (2, 0x1101, 70, 110, 1, 0xA201), (3, 0x1101, 80, 120, 1, 0x8200), (4, 0x0500, 90, 130, 0, 0xA200));
        e.Ecrire(1, champs, tables);
        CanalFormat.Lire(e.Tampon, d, bloc);
        Assert.Equal([1, 60, 100, 0, 1, 2, 70, 110, 0, 1], calcul.Convertir(bloc).Enemies);
    }

    [Fact]
    public void Un_ennemi_detruit_disparait_et_les_effets_ne_sont_pas_des_ennemis()
    {
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var bloc = new CanalBloc(d);

        // trois cases : un ennemi, un effet dessine SANS objet (case libre), une case non dessinee (drapeau sans 0x2000)
        var (champs, tables) = Bloc(10, 100, 380,
            (1, 0x1101, 60, 100, 1, 0xA000), (2, 0, 80, 120, 0, 0xA000), (3, 0x1101, 90, 130, 1, 0x8200));
        e.Ecrire(1, champs, tables);
        CanalFormat.Lire(e.Tampon, d, bloc);
        var avant = calcul.Convertir(bloc);
        Assert.Equal([1, 60, 100, 0, 1], avant.Enemies);

        // l'ennemi est detruit : sa case n'est plus dans la table
        (champs, tables) = Bloc(11, 100, 380);
        e.Ecrire(2, champs, tables);
        CanalFormat.Lire(e.Tampon, d, bloc);
        var apres = calcul.Convertir(bloc);
        Assert.Empty(apres.Enemies);
        Assert.True(CanalAffichageCalcul.Different(avant, apres));
    }

    [Fact]
    public void Le_boss_a_sa_vie_et_sa_vie_la_plus_haute_et_l_epoch_remet_tout_a_zero()
    {
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var bloc = new CanalBloc(d);
        foreach (var vie in new[] { 4000, 3000, 1000 })
        {
            var (c, t) = Bloc(520, 112, 380, (41, 0x8901, 112, 90, vie, 0xA000));
            e.Ecrire(1, c, t);
            CanalFormat.Lire(e.Tampon, d, bloc);
            var m = calcul.Convertir(bloc);
            Assert.Equal([vie, 4000], m.Boss);
            Assert.Equal(2, m.Enemies[3]);
        }

        // un etat charge : l'epoch change, le maximum est oublie
        e.NouvelleEpoque();
        var (c2, t2) = Bloc(520, 112, 380, (41, 0x8901, 112, 90, 500, 0xA000));
        e.Ecrire(2, c2, t2);
        CanalFormat.Lire(e.Tampon, d, bloc);
        Assert.Equal([500, 500], calcul.Convertir(bloc).Boss);
    }

    [Fact]
    public void Hors_partie_le_vaisseau_n_est_pas_publie()
    {
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var bloc = new CanalBloc(d);
        var (c, t) = Bloc(0, 112, 380);
        c[CanalScenarioSimule.Vies] = 255;   // vies : 255 hors partie
        e.Ecrire(1, c, t);
        CanalFormat.Lire(e.Tampon, d, bloc);
        Assert.Null(calcul.Convertir(bloc).Ship);
    }

    [Fact]
    public void Plus_de_soixante_quatre_ennemis_sont_plafonnes()
    {
        var e = new CanalEcrivainSimule("grand.VIEW", CanalScenarioSimule.Champs,
            [new("objets", 100, 100, ["type", "vie"]), new("affichage", 100, 100, ["drapeau", "ay", "ax"])], 3);
        var d = CanalFormat.LireDescripteur(e.Tampon)!;
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var gens = Enumerable.Range(0, 90).Select(i => (i, 0x1101, 10 + i, 50, 1, 0xA000)).ToArray();
        var (c, t) = Bloc(10, 100, 380, gens);
        e.Ecrire(1, c, t);
        var bloc = new CanalBloc(d);
        CanalFormat.Lire(e.Tampon, d, bloc);
        Assert.Equal(CanalAffichageCalcul.MaxEnnemis * 5, calcul.Convertir(bloc).Enemies.Length);
    }

    [Fact]
    public void Un_message_identique_n_est_pas_un_changement_et_un_canal_qui_se_tait_se_dit()
    {
        var (e, d) = Neuf();
        var calcul = new CanalAffichageCalcul(CanalProfilAffichage.DaiOuJou, d, "ddpdojblka");
        var bloc = new CanalBloc(d);
        var (c, t) = Bloc(10, 100, 380, (1, 0x1101, 60, 100, 1, 0xA000));
        e.Ecrire(1, c, t);
        CanalFormat.Lire(e.Tampon, d, bloc);
        var a = calcul.Convertir(bloc);
        e.Ecrire(2, c, t);
        CanalFormat.Lire(e.Tampon, d, bloc);
        var b = calcul.Convertir(bloc);
        Assert.False(CanalAffichageCalcul.Different(a, b));
        Assert.True(b.Seq > a.Seq);

        var tu = calcul.Inactif("netplay");
        Assert.False(tu.Active);
        Assert.Equal("netplay", tu.Reason);
        Assert.True(CanalAffichageCalcul.Different(b, tu));
    }

    [Fact]
    public void Le_profil_se_trouve_par_la_rom_ou_par_la_vue_et_un_jeu_sans_profil_n_a_pas_de_canal()
    {
        Assert.Same(CanalProfilAffichage.DaiOuJou, CanalProfilAffichage.Pour("ddpdojblka"));
        Assert.Same(CanalProfilAffichage.DaiOuJou, CanalProfilAffichage.Pour("DDPDOJBLKA"));
        Assert.Null(CanalProfilAffichage.Pour("sonic"));
        Assert.Null(CanalProfilAffichage.Pour(null));
        Assert.Same(CanalProfilAffichage.DaiOuJou, CanalProfilAffichage.PourLaVue("dodonpachi-daioujou.VIEW"));
        Assert.False(CanalDuDeck.Demander("sonic"));
    }

    [Fact]
    public void Le_canal_se_demande_pour_un_deck_et_s_ecrit_dans_certified_txt()
    {
        var t0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        CanalDuDeck.Retirer();
        try
        {
            Assert.False(CanalDuDeck.Voulu("ddpdojblka", t0));
            Assert.True(CanalDuDeck.Demander("ddpdojblka", t0));
            Assert.True(CanalDuDeck.Voulu("DDPDOJBLKA", t0.AddMinutes(1)));
            Assert.False(CanalDuDeck.Voulu("autrejeu", t0.AddMinutes(1)));
            Assert.False(CanalDuDeck.Voulu("ddpdojblka", t0 + CanalDuDeck.Validite + TimeSpan.FromSeconds(1)));   // demande perimee

            var texte = new StringBuilder("rom=ddpdojblka\n");
            CanalDuDeck.Demander("ddpdojblka");
            CanalDuDeck.Ecrire(texte, "ddpdojblka");
            Assert.Equal("rom=ddpdojblka\ncanal=on\n", texte.ToString());

            var autre = new StringBuilder("rom=sonic\n");
            CanalDuDeck.Ecrire(autre, "sonic");
            Assert.Equal("rom=sonic\n", autre.ToString());   // jamais pour un autre jeu

            CanalDuDeck.Retirer();
            var sans = new StringBuilder("rom=ddpdojblka\n");
            CanalDuDeck.Ecrire(sans, "ddpdojblka");
            Assert.Equal("rom=ddpdojblka\n", sans.ToString());
            Assert.Null(CanalDuDeck.RomDemandee());
            Assert.Null(CanalDuDeck.RomEnCours());
        }
        finally
        {
            CanalDuDeck.Retirer();
        }
    }

    [Fact]
    public void Un_canal_ouvert_reste_voulu_toute_la_partie_au_dela_de_la_validite_de_la_demande()
    {
        // relecture d'integration 1.9.211 : la demande vaut 10 min pour la cle de certified.txt, mais une partie dure plus longtemps ;
        // le service ne lache le canal qu'au retrait (fin de partie, Deck retire, MULTIPLAYER)
        var t0 = DateTime.UtcNow - CanalDuDeck.Validite - TimeSpan.FromMinutes(30);
        CanalDuDeck.Retirer();
        try
        {
            Assert.True(CanalDuDeck.Demander("ddpdojblka", t0));
            Assert.Null(CanalDuDeck.RomDemandee());                 // perimee pour la cle et la recherche au lancement
            Assert.Equal("ddpdojblka", CanalDuDeck.RomEnCours());   // mais le canal ouvert reste lu
            CanalDuDeck.Retirer();
            Assert.Null(CanalDuDeck.RomEnCours());
        }
        finally
        {
            CanalDuDeck.Retirer();
        }
    }

    [Fact]
    public void La_memoire_partagee_se_lit_en_lecture_seule_et_jamais_a_moitie()
    {
        var pid = PidDEssai(1);
        using var e = CanalScenarioSimule.DaiOuJou((uint)pid);
        e.Creer(pid);
        using var lecteur = CanalLecteur.Ouvrir(pid);
        Assert.NotNull(lecteur);
        Assert.Equal("dodonpachi-daioujou", lecteur!.Descripteur.Nom);

        // le wrapper ecrit pendant que l'API lit : chaque bloc lu est entier (le champ « temps » vaut le numero d'image)
        using var fin = new CancellationTokenSource();
        var ecrivain = Task.Run(() =>
        {
            var i = 0;
            while (!fin.IsCancellationRequested) { CanalScenarioSimule.Ecrire(e, ++i); Thread.Sleep(1); }
        });
        var iTemps = lecteur.Descripteur.Champ("temps");
        var iDist = lecteur.Descripteur.Champ("distance");
        var lues = 0;
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        while (chrono.ElapsedMilliseconds < 600)
        {
            Assert.True(lecteur.Lire(out var bloc));
            Assert.Equal(0u, bloc.Sequence & 1);
            var image = (int)bloc.Image;
            Assert.Equal(image % CanalScenarioSimule.Boucle, bloc.Champs[iTemps]);
            Assert.Equal(Math.Min(525, image % CanalScenarioSimule.Boucle / 9), bloc.Champs[iDist]);
            lues++;
        }

        fin.Cancel();
        ecrivain.Wait();
        Assert.True(lues > 50);
    }

    [Fact]
    public void Sans_canal_il_n_y_a_rien_a_ouvrir()
    {
        Assert.Null(CanalLecteur.Ouvrir(PidDEssai(2)));
    }

    private sealed class BusDeTest : RetroBat.Domain.Interfaces.IEventBus
    {
        public readonly List<EventEnvelope> Recus = [];
        public Task PublishAsync<TEvent>(TEvent @event) where TEvent : class
        {
            if (@event is EventEnvelope e) lock (Recus) Recus.Add(e);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class => new Rien();
        private sealed class Rien : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Le_service_publie_deck_live_depuis_un_canal_simule_puis_dit_qu_il_se_tait()
    {
        var pid = PidDEssai(3);
        var ancien = (Environment.GetEnvironmentVariable("NELFEPLAY_CANAL_PID"), Environment.GetEnvironmentVariable("NELFEPLAY_CANAL_ROM"));
        Environment.SetEnvironmentVariable("NELFEPLAY_CANAL_PID", pid.ToString());
        Environment.SetEnvironmentVariable("NELFEPLAY_CANAL_ROM", "ddpdojblka");
        try
        {
            using var e = CanalScenarioSimule.DaiOuJou((uint)pid);
            e.Creer(pid);
            for (var i = 1; i <= 700; i++) CanalScenarioSimule.Ecrire(e, i);   // 700 images : des ennemis a l'ecran
            var bus = new BusDeTest();
            using var service = new CanalDuDeckService(bus);
            await service.StartAsync(CancellationToken.None);
            var delai = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < delai && bus.Recus.Count < 1) await Task.Delay(50);
            Assert.NotEmpty(bus.Recus);
            DeckLive premier;
            lock (bus.Recus) premier = Assert.IsType<DeckLive>(bus.Recus[0].Payload);
            Assert.Equal("deck.live", bus.Recus[0].Type);
            Assert.True(premier.Active);
            Assert.Equal(700 / 9, premier.Dist);
            Assert.NotNull(premier.Ship);
            Assert.True(premier.Enemies.Length >= 5 && premier.Enemies.Length % 5 == 0);

            // le jeu se ferme
            e.Couper(CanalFormat.EtatFerme);
            delai = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < delai)
            {
                lock (bus.Recus) if (bus.Recus.Any(r => r.Payload is DeckLive { Active: false })) break;
                await Task.Delay(50);
            }

            lock (bus.Recus) Assert.Contains(bus.Recus, r => r.Payload is DeckLive { Active: false, Reason: "closed" });
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NELFEPLAY_CANAL_PID", ancien.Item1);
            Environment.SetEnvironmentVariable("NELFEPLAY_CANAL_ROM", ancien.Item2);
        }
    }

    [Fact]
    public async Task Sans_deck_ni_simulation_le_service_ne_publie_rien()
    {
        CanalDuDeck.Retirer();
        var bus = new BusDeTest();
        using var service = new CanalDuDeckService(bus);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(bus.Recus);
        Assert.Equal(0, service.Publies);
    }

    [Fact]
    public void Le_data_pack_admet_un_fichier_d_affichage_voisin_du_mem()
    {
        // le .VIEW du canal (wrapper 0.348) se distribue comme un .MEM : ram/<systeme>/<jeu>.VIEW
        var dossiers = new HashSet<string>(["ram"], StringComparer.OrdinalIgnoreCase);
        Assert.True(DataPackPaths.Autorise("ram/arcade/dodonpachi-daioujou.VIEW", dossiers));
        Assert.False(DataPackPaths.Autorise("../ram/arcade/x.VIEW", dossiers));
        Assert.False(DataPackPaths.Autorise("autre/arcade/x.VIEW", dossiers));
    }

    [Fact]
    public void Un_vieux_wrapper_sans_canal_ne_change_pas_certified_txt_des_autres_jeux()
    {
        // aucune demande : le texte de certified.txt d'un jeu quelconque est celui d'avant
        CanalDuDeck.Retirer();
        var texte = new StringBuilder("rom=sonic\nopt.x=1\n");
        CanalDuDeck.Ecrire(texte, "sonic");
        Assert.Equal("rom=sonic\nopt.x=1\n", texte.ToString());
    }
}
