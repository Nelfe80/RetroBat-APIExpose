using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE RANG EN DIRECT DU PLAYERDECK (H-D21) : la regle du rang du site (1 + lignes strictement meilleures, sa ligne
/// comprise, une ligne par compte et par monde), les egalites, les deux sens (score, temps), le classement tronque, le
/// cache perime et le hors ligne, puis le service : une seule lecture au lancement, rang recalcule a chaque valeur.
/// </summary>
public sealed class DeckRangTests
{
    private static DeckRankRow L(int rang, string joueur, long valeur, bool moi = false, string monde = "home") => new(rang, joueur, valeur, monde, moi);

    private static readonly DeckRankRow[] Scores =
    [
        L(1, "ace", 1_000_000), L(2, "bob", 800_000), L(3, "cid", 800_000), L(4, "dan", 500_000),
    ];

    // ─────────────────────────── le calcul ───────────────────────────

    [Fact]
    public void Le_rang_vaut_un_plus_les_lignes_strictement_meilleures_et_une_egalite_partage_le_rang()
    {
        var p = DeckRankCalcul.Classer(Scores, 800_000, false, "home", false, null);
        Assert.Equal(2, p.Rang);
        Assert.True(p.Egalite);
        Assert.Equal("ace", p.AuDessus!.Player);
        Assert.Equal(200_000, p.Ecart);
        Assert.Equal(5, p.Effectif);   // la partie s'ajoute : le joueur n'a pas de ligne

        p = DeckRankCalcul.Classer(Scores, 650_000, false, "home", false, null);
        Assert.Equal(4, p.Rang);
        Assert.False(p.Egalite);
        Assert.Equal("cid", p.AuDessus!.Player);   // a valeur egale, la plus basse du site
        Assert.Equal(150_000, p.Ecart);

        p = DeckRankCalcul.Classer(Scores, 1_000_000, false, "home", false, null);
        Assert.Equal(1, p.Rang);
        Assert.True(p.Egalite);
        Assert.Null(p.AuDessus);
        Assert.Null(p.Ecart);

        Assert.Equal(5, DeckRankCalcul.Classer(Scores, 0, false, "home", false, null).Rang);
    }

    [Fact]
    public void Sa_ligne_compte_sous_son_record_et_suit_la_partie_au_dessus_comme_sur_le_site()
    {
        DeckRankRow[] lignes = [L(1, "ace", 1000), L(2, "nelfe", 900, moi: true), L(3, "bob", 800)];
        var sous = DeckRankCalcul.Classer(lignes, 850, false, "home", false, null);
        Assert.Equal(3, sous.Rang);
        Assert.Equal(3, sous.Effectif);   // sa ligne existe deja : pas une de plus
        Assert.True(sous.AuDessus!.IsMe);  // passer son record fait gagner une place
        Assert.Equal(50, sous.Ecart);

        var dessus = DeckRankCalcul.Classer(lignes, 950, false, "home", false, null);
        Assert.Equal(2, dessus.Rang);
        Assert.Equal("ace", dessus.AuDessus!.Player);

        // son record d'un autre monde (la salle) est une autre ligne : il compte, et la partie en ajoute une
        DeckRankRow[] salle = [L(1, "ace", 1000), L(2, "nelfe", 900, moi: true, monde: "station"), L(3, "bob", 800)];
        var maison = DeckRankCalcul.Classer(salle, 850, false, "home", false, null);
        Assert.Equal(3, maison.Rang);
        Assert.Equal(4, maison.Effectif);
        Assert.Equal(900, DeckRankCalcul.Record(salle, "home", false)!.Value);   // a defaut de la maison, le meilleur ailleurs
    }

    [Fact]
    public void Un_classement_au_temps_se_lit_dans_l_autre_sens_et_vise_le_rang_au_dessus_du_record()
    {
        DeckRankRow[] temps = [L(1, "ace", 3000), L(2, "bob", 3500), L(3, "nelfe", 4000, moi: true), L(4, "cid", 4200)];
        Assert.Equal("bob", DeckRankCalcul.Cible(temps, "home", true)!.Player);
        var arrivee = DeckRankCalcul.Classer(temps, 3200, true, "home", false, null);
        Assert.Equal(2, arrivee.Rang);
        Assert.Equal("ace", arrivee.AuDessus!.Player);
        Assert.Equal(200, arrivee.Ecart);
        Assert.Equal(4, DeckRankCalcul.Classer(temps, 4100, true, "home", false, null).Rang);

        // deja premier : la cible est son record ; sans record : la 10e place, ou la derniere
        DeckRankRow[] premier = [L(1, "nelfe", 2900, moi: true), L(2, "ace", 3000)];
        Assert.True(DeckRankCalcul.Cible(premier, "home", true)!.IsMe);
        var douze = Enumerable.Range(1, 12).Select(i => L(i, "p" + i, 1000 + i)).ToArray();
        Assert.Equal(10, DeckRankCalcul.Cible(douze, "home", true)!.Rank);
        Assert.Equal(4, DeckRankCalcul.Cible(Scores, "home", false)!.Rank);
        Assert.Null(DeckRankCalcul.Cible([], "home", true));
    }

    [Fact]
    public void Un_classement_tronque_ne_donne_qu_un_minimum()
    {
        var deux_cents = Enumerable.Range(1, 200).Select(i => L(i, "p" + i, 10_000 - i)).ToArray();
        var loin = DeckRankCalcul.Classer(deux_cents, 5, false, "home", true, null);
        Assert.Equal(201, loin.Rang);
        Assert.True(loin.RangAuMoins);
        Assert.True(loin.EffectifAuMoins);
        var dedans = DeckRankCalcul.Classer(deux_cents, 9_950, false, "home", true, null);
        Assert.Equal(50, dedans.Rang);   // 49 lignes au-dessus de 9 950
        Assert.False(dedans.RangAuMoins);

        // une epreuve dit son total : l'effectif est exact
        var epreuve = DeckRankCalcul.Classer(deux_cents.Take(100).ToArray(), 5, false, "", true, 340);
        Assert.Equal(341, epreuve.Effectif);
        Assert.False(epreuve.EffectifAuMoins);
        Assert.True(epreuve.RangAuMoins);
    }

    private static DeckLancement Lancement(string mode = DeckModes.WorldScoring, string chemin = @"E:\RetroBat\roms\fbneo\19xx.zip", DeckRankBoard? board = null)
        => new(mode, "fbneo", "19xx", "19xx", "arcade/19xx", "nelfe", chemin, "posed", "19xx-hangar", "19XX Hangar", true, "official-default",
            ["19xx-hangar", "off"], null, null, DateTime.UtcNow) { Board = board };

    private static readonly DeckRankBoard Jeu1cc = new(DeckRankBoard.Jeu, "19xx", "1cc", DeckRankBoard.CleScore, false, 1, null, "1CC");

    [Fact]
    public void Hors_ligne_classement_vide_et_cache_perime_se_disent_sans_faux_rang()
    {
        var l = Lancement();
        var horsLigne = DeckRankCalcul.Composer(l, Jeu1cc, DeckRankTable.HorsLigne, "home", 12_000, null, false, "x");
        Assert.Equal("offline", horsLigne.State);
        Assert.Null(horsLigne.Rank);
        Assert.True(horsLigne.Provisional);

        var vide = DeckRankCalcul.Composer(l, Jeu1cc, new DeckRankTable([], DeckRankTable.Frais, DateTime.UtcNow, false, null, null), "home", 12_000, null, false, "x");
        Assert.Equal("first_score", vide.State);
        Assert.Equal(1, vide.Rank);
        Assert.Equal(1, vide.Of);

        var perime = DeckRankCalcul.Composer(l, Jeu1cc, new DeckRankTable(Scores, DeckRankTable.Perime, null, false, null, false), "home", 900_000, null, false, "x");
        Assert.Equal("ranked", perime.State);
        Assert.Equal("stale", perime.Source);
        Assert.Equal(2, perime.Rank);
        Assert.Equal(100_000, perime.Gap);

        var sansClassement = DeckRankCalcul.Composer(l with { Mode = DeckModes.Multiplayer }, null, null, "home", 5, null, false, "x");
        Assert.Equal("no_board", sansClassement.State);
        Assert.Null(sansClassement.Rank);
        Assert.Equal("loading", DeckRankCalcul.Composer(l, Jeu1cc, null, "home", 5, null, false, "x").State);
    }

    [Fact]
    public void Le_record_perso_et_le_nouveau_record_viennent_du_classement_lu()
    {
        DeckRankRow[] lignes = [L(1, "ace", 1000), L(2, "nelfe", 900, moi: true), L(3, "bob", 800)];
        var table = new DeckRankTable(lignes, DeckRankTable.Frais, DateTime.UtcNow, false, null, false);
        var r = DeckRankCalcul.Composer(Lancement(), Jeu1cc, table, "home", 850, null, false, "x");
        Assert.Equal(900, r.PersonalBest!.Value);
        Assert.False(r.NewPersonalBest);
        Assert.True(DeckRankCalcul.Composer(Lancement(), Jeu1cc, table, "home", 901, null, false, "x").NewPersonalBest);
        Assert.Equal("higher", r.Better);
        Assert.Equal("score", r.Key);
    }

    [Fact]
    public void Un_temps_s_ecrit_a_la_cadence_du_jeu()
    {
        var epreuve = new DeckRankBoard(DeckRankBoard.Epreuve, "metal-slug-3", "speedrun-m1", DeckRankBoard.CleTemps, true, 1, 59.18, "SPEEDRUN");
        DeckRankRow[] temps = [L(1, "ace", 5918, monde: ""), L(2, "nelfe", 7102, moi: true, monde: "")];
        var table = new DeckRankTable(temps, DeckRankTable.Frais, DateTime.UtcNow, false, 2, true);
        var course = DeckRankCalcul.Composer(Lancement(DeckModes.Speedrun), epreuve, table, "home", null, null, false, "x");
        Assert.Equal("racing", course.State);
        Assert.Null(course.Rank);
        Assert.Equal("1:40.00", course.Target!.Text);   // 5918 images a 59,18 Hz
        Assert.Equal("2:00.01", course.PersonalBest!.Text);

        var arrivee = DeckRankCalcul.Composer(Lancement(DeckModes.Speedrun), epreuve, table, "home", null, 6509, false, "x");
        Assert.Equal("ranked", arrivee.State);
        Assert.True(arrivee.Final);
        Assert.Equal(2, arrivee.Rank);
        Assert.Equal(2, arrivee.Of);
        Assert.Equal("0:09.99", arrivee.GapText);
        Assert.True(arrivee.NewPersonalBest);
    }

    [Fact]
    public void Sa_ligne_se_reconnait_au_pseudo_ou_a_la_poignee_jamais_pour_local()
    {
        Assert.True(DeckRankService.EstMoi("Nelfe", "", "nelfe"));
        Assert.True(DeckRankService.EstMoi("Nelfe80", "@nelfe", "nelfe"));
        Assert.False(DeckRankService.EstMoi("local", "", "local"));
        Assert.False(DeckRankService.EstMoi("bob", "", "nelfe"));
    }

    // ─────────────────────────── le service ───────────────────────────

    private sealed class Banc : IDisposable
    {
        public readonly SimpleEventBus Bus = new();
        public readonly ApiContext Contexte = new() { PlayerPseudo = "nelfe" };
        public readonly List<DeckRank> Publies = new();
        public readonly DeckRankService Service;
        public readonly DeckStateService Etat;
        public int Lectures;

        public Banc(DeckRankTable table)
        {
            Bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.rank") Publies.Add((DeckRank)e.Payload!); });
            Etat = new DeckStateService(Bus, Contexte);
            Service = new DeckRankService(Bus, Contexte, etat: Etat)
            {
                Lecteur = (_, _, _) =>
                {
                    Interlocked.Increment(ref Lectures);
                    return Task.FromResult(table);
                },
            };
            Etat.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Armer(DeckLancement l)
        {
            Service.Armer(l);
            Service.Chargement?.Wait(TimeSpan.FromSeconds(5));
        }

        public void Demarrer(string chemin = @"E:\RetroBat\roms\fbneo\19xx.zip")
        {
            Contexte.Ui.Running = new GameReference { SystemId = "fbneo", GamePath = chemin };
            Bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = chemin } });
        }

        public void Score(long score, int joueur = 1)
            => Bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new { Player = joueur, Score = score, SystemId = "fbneo", Rom = "19xx" } });

        public void Dispose()
        {
            Service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Etat.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Le_classement_se_lit_une_fois_au_lancement_puis_le_rang_suit_chaque_score_sans_reseau()
    {
        using var banc = new Banc(new DeckRankTable(Scores, DeckRankTable.Frais, DateTime.UtcNow, false, null, false));
        banc.Armer(Lancement(board: Jeu1cc));
        banc.Demarrer();
        Assert.Equal("ranked", banc.Service.Courant!.State);
        Assert.Equal(5, banc.Service.Courant.Rank);

        banc.Score(100);
        banc.Score(600_000);
        banc.Score(600_000);   // meme valeur : rien de publie
        banc.Score(900_000);
        banc.Score(5, joueur: 2);   // un autre joueur : pas notre rang
        Assert.Equal(1, banc.Lectures);
        // le depart du jeu republie le rang une fois (son LaunchId), puis un message par valeur qui change le rang ou l'ecart
        var rangs = banc.Publies.Where(r => r.State == "ranked" && r.LaunchId is not null).Select(r => (r.Rank, r.Value)).ToList();
        Assert.Equal(new (int?, long?)[] { (5, 0), (5, 100), (4, 600_000), (2, 900_000) }, rangs);
        var dernier = banc.Service.Courant;
        Assert.Equal(100_000, dernier.Gap);
        Assert.Equal("ace", dernier.Above!.Player);
        Assert.True(dernier.Provisional);
        Assert.True(banc.Publies.Select(r => r.Revision).SequenceEqual(banc.Publies.Select(r => r.Revision).Order()));

        // l'instantane du Deck le reprend
        var instantane = banc.Etat.Snapshot("test");
        Assert.Equal(2, instantane.Rank!.Rank);
        Assert.Contains("\"Rank\":{", JsonSerializer.Serialize(instantane));

        // fin de partie : un dernier message « ended », puis plus de rang
        banc.Bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
        Assert.Equal("ended", banc.Publies[^1].State);
        Assert.Null(banc.Service.Courant);
        banc.Score(2_000_000);
        Assert.Equal("ended", banc.Publies[^1].State);
    }

    [Fact]
    public void Un_autre_jeu_que_celui_du_lancement_n_a_pas_de_rang()
    {
        using var banc = new Banc(new DeckRankTable(Scores, DeckRankTable.Frais, DateTime.UtcNow, false, null, false));
        banc.Armer(Lancement(board: Jeu1cc));
        banc.Demarrer(@"E:\RetroBat\roms\fbneo\1942.zip");
        Assert.Null(banc.Service.Courant);
        var avant = banc.Publies.Count;
        banc.Score(900_000);
        Assert.Equal(avant, banc.Publies.Count);
    }

    [Fact]
    public void Une_epreuve_au_temps_vise_pendant_la_course_et_fixe_le_rang_a_l_arrivee()
    {
        var epreuve = new DeckRankBoard(DeckRankBoard.Epreuve, "19xx", "speedrun-s1", DeckRankBoard.CleTemps, true, 1, 60, "SPEEDRUN");
        DeckRankRow[] temps = [L(1, "ace", 3000, monde: ""), L(2, "bob", 3500, monde: ""), L(3, "nelfe", 4000, moi: true, monde: "")];
        using var banc = new Banc(new DeckRankTable(temps, DeckRankTable.Frais, DateTime.UtcNow, false, 3, true));
        banc.Armer(Lancement(DeckModes.Speedrun, board: epreuve));
        banc.Demarrer();
        banc.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.started", Payload = new JsonObject { ["sessionId"] = "s" } });
        Assert.Equal("racing", banc.Service.Courant!.State);
        Assert.Equal("bob", banc.Service.Courant.Target!.Player);
        Assert.Equal("0:58.33", banc.Service.Courant.Target.Text);

        // les scores ne classent pas une epreuve au temps
        banc.Score(123);
        Assert.Equal("racing", banc.Service.Courant.State);

        JsonObject Fin(long images, string controle) => new()
        {
            ["state"] = "FINISHED",
            ["official"] = new JsonObject { ["frames"] = images, ["display"] = "x", ["provisional"] = false },
            ["demo"] = new JsonObject { ["gameTimeCheck"] = controle },
        };
        banc.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.attempt.ended", Payload = Fin(3400, "ok") });
        Assert.Equal("ranked", banc.Service.Courant.State);
        Assert.True(banc.Service.Courant.Final);
        Assert.Equal(2, banc.Service.Courant.Rank);
        Assert.Equal(3, banc.Service.Courant.Of);
        Assert.Equal("0:06.67", banc.Service.Courant.GapText);

        // un temps que le temps du jeu contredit ne se classe pas
        banc.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.attempt.opened", Payload = new JsonObject() });
        banc.Bus.PublishAsync(new EventEnvelope { Type = "speedrun.attempt.ended", Payload = Fin(2000, "ecart") });
        Assert.Null(banc.Service.Courant.Rank);
        Assert.Equal(1, banc.Lectures);
    }

    [Fact]
    public void Sans_classement_pour_le_mode_le_rang_le_dit()
    {
        using var banc = new Banc(DeckRankTable.HorsLigne);
        banc.Armer(Lancement(DeckModes.Multiplayer));   // aucun 1CC MULTI connu (pas d'index des jeux ouverts)
        banc.Demarrer();
        Assert.Equal("no_board", banc.Service.Courant!.State);
        Assert.Equal(0, banc.Lectures);

        // un jeu sans Deck desarme le rang du lancement d'avant
        banc.Service.Armer(Lancement() with { Choices = [] });
        Assert.Null(banc.Service.Courant);
    }

    [Fact]
    public void Le_lancement_avec_Deck_arme_le_rang_et_porte_son_classement()
    {
        var bus = new SimpleEventBus();
        var launches = new List<string>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.launch") launches.Add(JsonSerializer.Serialize(e.Payload)); });
        var contexte = new ApiContext { PlayerPseudo = "nelfe" };
        var rang = new DeckRankService(bus, contexte) { Lecteur = (_, _, _) => Task.FromResult(DeckRankTable.HorsLigne) };
        var dossier = Path.Combine(Path.GetTempPath(), "deck-rang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var themes = Path.Combine(dossier, "themes");
            Directory.CreateDirectory(themes);
            File.WriteAllText(Path.Combine(themes, "19xx-hangar.json"),
                """{ "format": "nelfeplay.deck-theme/1", "id": "19xx-hangar", "name": "19XX Hangar", "version": 1, "deck": { "default": true }, "match": { "roms": ["19xx"] } }""");
            var catalogue = DeckCatalogue.Lire(dossier);
            var png = Path.Combine(dossier, "rendu.png");
            File.WriteAllBytes(png, [0x89, 0x50, 0x4e, 0x47]);
            var service = new DeckLaunchService(bus, contexte, fichierDesChoix: Path.Combine(dossier, "choix.json"))
            {
                Catalogue = () => catalogue,
                Ecran = () => (1920, 1200),
                RacineDuDecor = Path.Combine(dossier, "decorations", DeckDecor.Nom),
                DemarrerLeHost = false,
                Rendeur = (e, l, h, _) => new DeckRendu(png, new DeckDecor.Trou(510, 0, 900, 1200), l, h, false, 1),
                Rang = rang,
            };

            var l = service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip", classement: Jeu1cc);
            rang.Chargement?.Wait(TimeSpan.FromSeconds(5));
            Assert.Equal(Jeu1cc, l.Board);
            Assert.Contains("\"Board\":{", launches.Single());
            Assert.Equal("offline", rang.Courant!.State);
            Assert.Equal("1CC", rang.Courant.Label);

            // MULTIPLAYER : aucun Deck en room (decision du user du 09/11)
            Assert.Equal("none", service.Preparer(DeckModes.Multiplayer, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip", place: 3).Status);
        }
        finally
        {
            try { Directory.Delete(dossier, recursive: true); } catch (IOException) { }
        }
    }
}
