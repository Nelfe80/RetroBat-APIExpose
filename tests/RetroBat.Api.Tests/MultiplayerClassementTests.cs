using System.Globalization;
using System.Text.Json;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES CLASSEMENTS A PLUSIEURS (CDC Classements v1.0 §3 a §5, v1.1 §1 et §2) : le moteur sur des series de releves (valeurs
/// au vol, remontee de vie, egalites, ordinateur vainqueur, camps), la lecture par la carte du coeur, le delai des choix, et
/// le catalogue qui ecarte un jeu invalide sans refuser les autres.
/// </summary>
public class MultiplayerClassementTests
{
    /// <summary>Un releve : la garde, le signal de fin, et la valeur (mort, arrivee) de chaque place.</summary>
    private static MultiplayerReleve R(int? garde, int? signal, params (int Place, long? Valeur, int? Mort, int? Fini)[] places)
        => new(garde, signal, places.ToDictionary(p => p.Place, p => new MultiplayerReleveDePlace(p.Valeur, p.Mort, p.Fini)));

    private static (int, long?, int?, int?) V(int place, long? valeur) => (place, valeur, null, null);

    /// <summary>L'horloge d'une rencontre rejouee : elle continue d'un rejeu a l'autre.</summary>
    private sealed class Horloge
    {
        public long Ms;
    }

    private static IReadOnlyList<MultiplayerRang>? Rejouer(MultiplayerClassementEnCours enCours, params (MultiplayerReleve Releve, int Lectures)[] suite)
        => Rejouer(enCours, new Horloge(), suite);

    /// <summary>Rejoue des releves toutes les 250 ms ; chacun tenu le nombre de lectures donne. Les rangs a la fin, ou null.</summary>
    private static IReadOnlyList<MultiplayerRang>? Rejouer(MultiplayerClassementEnCours enCours, Horloge horloge, params (MultiplayerReleve Releve, int Lectures)[] suite)
    {
        foreach (var (releve, lectures) in suite)
        {
            for (var i = 0; i < lectures; i++)
            {
                if (enCours.Observer(releve, horloge.Ms) is { } rangs)
                {
                    return rangs;
                }

                horloge.Ms += 250;
            }
        }

        return null;
    }

    private static string Dire(IReadOnlyList<MultiplayerRang>? rangs)
        => rangs is null ? "aucun" : string.Join(' ', rangs.Select(r => r.Seat + ":" + r.Rank + (r.Camp is { } c ? "/" + c : string.Empty)));

    /// <summary>Sonic Battle (livraison du 29/10) : quatre places, premier a 5 K.O., garde a 0 (regle K.O.).</summary>
    private static readonly MultiplayerRankingEnd SonicBattle = new()
    {
        Rule = MultiplayerRankingEnd.Manches, Memory = "core_map", FirstTo = 5, SettleMs = 1000,
        Guard = new() { CoreRam = "0x03002600", Value = 0 },
        Seats =
        [
            new() { Seat = 1, CoreRam = "0x03001647" }, new() { Seat = 2, CoreRam = "0x03001648" },
            new() { Seat = 3, CoreRam = "0x03001649" }, new() { Seat = 4, CoreRam = "0x0300164A" },
        ],
    };

    [Fact]
    public void Manches_l_ordinateur_gagne_et_les_humains_sont_classes_entre_eux()
    {
        // Mesure de la livraison : a deux humains, l'ordinateur (P4) gagne 5, P1 a 2 et P2 a 1.
        var rangs = Rejouer(new MultiplayerClassementEnCours(SonicBattle, [1, 2]),
            (R(0, null, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(0, null, V(1, 2), V(2, 1), V(3, 1), V(4, 5)), 6));
        Assert.Equal("1:1 2:2", Dire(rangs));
        Assert.Equal(1, MultiplayerIssue.DesRangs(rangs!).Gagnante);
    }

    [Fact]
    public void Manches_a_quatre_humains_le_rang_suit_le_compteur_et_l_egalite_partage()
    {
        var rangs = Rejouer(new MultiplayerClassementEnCours(SonicBattle, [1, 2, 3, 4]),
            (R(0, null, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(0, null, V(1, 5), V(2, 2), V(3, 3), V(4, 2)), 6));
        Assert.Equal("1:1 3:2 2:3 4:3", Dire(rangs));
    }

    [Fact]
    public void Manches_rien_ne_finit_sans_avoir_vu_le_depart_ni_sur_une_valeur_lue_au_vol()
    {
        var enCours = new MultiplayerClassementEnCours(SonicBattle, [1, 2]);
        var horloge = new Horloge();
        // L'ecran de fin de la rencontre d'avant : un compteur deja a 5 ne termine rien.
        Assert.Null(Rejouer(enCours, horloge, (R(0, null, V(1, 5), V(2, 3), V(3, 0), V(4, 0)), 10)));
        // Le depart : tout retombe a 0. Puis un 5 lu une seule fois (un octet ecrit au vol) ne tient pas une seconde.
        Assert.Null(Rejouer(enCours, horloge,
            (R(0, null, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(0, null, V(1, 5), V(2, 0), V(3, 0), V(4, 0)), 1),
            (R(0, null, V(1, 1), V(2, 0), V(3, 0), V(4, 0)), 6)));
    }

    [Fact]
    public void Manches_hors_de_la_garde_rien_ne_compte()
    {
        // Regle Time (garde a 1) : un compteur qui monte ne ferme rien ; de retour en regle K.O., chaque valeur se relit de zero.
        var rangs = Rejouer(new MultiplayerClassementEnCours(SonicBattle, [1, 2]),
            (R(0, null, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(1, null, V(1, 9), V(2, 0), V(3, 0), V(4, 0)), 10),
            (R(0, null, V(1, 0), V(2, 5), V(3, 0), V(4, 0)), 6));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Manches_un_compteur_a_FF_n_est_pas_une_place()
    {
        // Une place que personne ne tient se lit 0xFF : elle ne gagne pas, et n'empeche pas le depart.
        var rangs = Rejouer(new MultiplayerClassementEnCours(SonicBattle, [1, 2]),
            (R(0, null, V(1, 0), V(2, 0), V(3, 0xFF), V(4, 0xFF)), 2),
            (R(0, null, V(1, 1), V(2, 5), V(3, 0xFF), V(4, 0xFF)), 6));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    private static readonly MultiplayerRankingEnd Survie = new()
    {
        Rule = MultiplayerRankingEnd.Survie, OutAt = 0, SettleMs = 1000,
        Seats = [new() { Seat = 1, CoreRam = "0x10" }, new() { Seat = 2, CoreRam = "0x11" }, new() { Seat = 3, CoreRam = "0x12" }, new() { Seat = 4, CoreRam = "0x13" }],
    };

    [Fact]
    public void Survie_le_dernier_humain_debout_gagne_l_ordinateur_encore_en_jeu()
    {
        var rangs = Rejouer(new MultiplayerClassementEnCours(Survie, [1, 2, 3]),
            (R(null, null, V(1, 5), V(2, 5), V(3, 5), V(4, 5)), 2),
            (R(null, null, V(1, 4), V(2, 0), V(3, 3), V(4, 5)), 8),
            (R(null, null, V(1, 2), V(2, 0), V(3, 0), V(4, 5)), 8));
        Assert.Equal("1:1 3:2 2:3", Dire(rangs));
    }

    [Fact]
    public void Survie_deux_chutes_dans_la_meme_fenetre_partagent_leur_rang()
    {
        var rangs = Rejouer(new MultiplayerClassementEnCours(Survie, [1, 2, 3]),
            (R(null, null, V(1, 5), V(2, 5), V(3, 5)), 2),
            (R(null, null, V(1, 5), V(2, 0), V(3, 5)), 2),
            (R(null, null, V(1, 5), V(2, 0), V(3, 0)), 8));
        Assert.Equal("1:1 2:2 3:2", Dire(rangs));
    }

    [Fact]
    public void Survie_une_vie_lue_a_zero_un_instant_ne_fait_pas_tomber()
    {
        var enCours = new MultiplayerClassementEnCours(Survie, [1, 2]);
        Assert.Null(Rejouer(enCours,
            (R(null, null, V(1, 5), V(2, 5)), 2),
            (R(null, null, V(1, 0), V(2, 5)), 2),
            (R(null, null, V(1, 5), V(2, 5)), 10)));
    }

    [Fact]
    public void Survie_par_l_octet_de_la_mort_seul()
    {
        // Advance Wars 2 en partie rapide (v1.1 §7, n° 8) : pas de vie, un octet « armee detruite ou QG pris ».
        var fin = new MultiplayerRankingEnd
        {
            Rule = MultiplayerRankingEnd.Survie, SettleMs = 500,
            Seats = [new() { Seat = 1, Dead = new() { CoreRam = "0x20", Value = 1 } }, new() { Seat = 2, Dead = new() { CoreRam = "0x21", Value = 1 } }],
        };
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2]),
            (R(null, null, (1, null, 0, null), (2, null, 0, null)), 2),
            (R(null, null, (1, null, 1, null), (2, null, 0, null)), 6));
        Assert.Equal("2:1 1:2", Dire(rangs));
        Assert.Equal(2, MultiplayerIssue.DesRangs(rangs!).Gagnante);
    }

    [Fact]
    public void Survie_en_equipes_un_camp_tient_tant_qu_un_membre_est_debout()
    {
        // 2 contre 2 : un humain par camp, l'ordinateur complete. L'humain du camp 1 tombe le premier, son coequipier tient.
        var fin = Survie with
        {
            Seats = [new() { Seat = 1, CoreRam = "0x10", Camp = 1 }, new() { Seat = 2, CoreRam = "0x11", Camp = 2 },
                new() { Seat = 3, CoreRam = "0x12", Camp = 1 }, new() { Seat = 4, CoreRam = "0x13", Camp = 2 }],
        };
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2]),
            (R(null, null, V(1, 5), V(2, 5), V(3, 5), V(4, 5)), 2),
            (R(null, null, V(1, 0), V(2, 5), V(3, 5), V(4, 5)), 8),
            (R(null, null, V(1, 0), V(2, 0), V(3, 2), V(4, 0)), 8));
        Assert.Equal("1:1/1 2:2/2", Dire(rangs));
    }

    [Fact]
    public void En_equipes_tous_les_humains_du_meme_cote_ne_se_classent_pas()
    {
        var fin = Survie with
        {
            Seats = [new() { Seat = 1, CoreRam = "0x10", Camp = 1 }, new() { Seat = 2, CoreRam = "0x11", Camp = 1 },
                new() { Seat = 3, CoreRam = "0x12", Camp = 2 }, new() { Seat = 4, CoreRam = "0x13", Camp = 2 }],
        };
        Assert.False(fin.Classable([1, 2]));
        Assert.True(fin.Classable([1, 3]));
        Assert.False(Survie.Classable([2]));
        Assert.False((Survie with { MinHumans = 3 }).Classable([1, 2]));
        Assert.True((Survie with { MinHumans = 1 }).Classable([1, 2]));
    }

    private static readonly MultiplayerRankingEnd Course = new()
    {
        Rule = MultiplayerRankingEnd.Arrivee, SettleMs = 500, Over = new() { CoreRam = "0x50", Value = 1 },
        Seats =
        [
            new() { Seat = 1, CoreRam = "0x30", Finished = new() { CoreRam = "0x40", Value = 1 } },
            new() { Seat = 2, CoreRam = "0x31", Finished = new() { CoreRam = "0x41", Value = 1 } },
            new() { Seat = 3, CoreRam = "0x32", Finished = new() { CoreRam = "0x42", Value = 1 } },
            new() { Seat = 4, CoreRam = "0x33", Finished = new() { CoreRam = "0x43", Value = 1 } },
        ],
    };

    [Fact]
    public void Arrivee_les_humains_par_leur_position_d_arrivee()
    {
        // L'ordinateur arrive 1er et 3e ; les humains 2e (P3) et 4e (P1) : P3 devant P1.
        var rangs = Rejouer(new MultiplayerClassementEnCours(Course, [1, 3]),
            (R(null, 0, (1, 4, null, 0), (2, 3, null, 0), (3, 2, null, 0), (4, 1, null, 0)), 2),
            (R(null, 0, (1, 4, null, 0), (2, 3, null, 0), (3, 2, null, 1), (4, 1, null, 1)), 4),
            (R(null, 0, (1, 4, null, 1), (2, 3, null, 1), (3, 2, null, 1), (4, 1, null, 1)), 4));
        Assert.Equal("3:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Arrivee_un_humain_qui_n_a_pas_fini_au_signal_est_derriere()
    {
        // P1 est 1er en course mais n'a pas franchi la ligne quand le signal de fin tombe ; P2 a fini 4e.
        var rangs = Rejouer(new MultiplayerClassementEnCours(Course, [1, 2]),
            (R(null, 0, (1, 2, null, 0), (2, 3, null, 0)), 2),
            (R(null, 0, (1, 1, null, 0), (2, 4, null, 1)), 4),
            (R(null, 1, (1, 1, null, 0), (2, 4, null, 1)), 4));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Arrivee_un_signal_de_fin_deja_la_au_depart_doit_retomber()
    {
        var enCours = new MultiplayerClassementEnCours(Course, [1, 2]);
        var horloge = new Horloge();
        Assert.Null(Rejouer(enCours, horloge, (R(null, 1, (1, 1, null, 0), (2, 2, null, 0)), 10)));
        Assert.Equal("1:1 2:2", Dire(Rejouer(enCours, horloge,
            (R(null, 0, (1, 1, null, 0), (2, 2, null, 0)), 2),
            (R(null, 1, (1, 1, null, 0), (2, 2, null, 0)), 4))));
    }

    private static readonly MultiplayerRankingEnd Rampage = new()
    {
        Rule = MultiplayerRankingEnd.ScoreFige, SettleMs = 500, Over = new() { CoreRam = "0x90", Value = 1 },
        Seats =
        [
            new() { Seat = 1, CoreRam = "0x60", Bytes = 2, Dead = new() { CoreRam = "0x70", Value = 1 } },
            new() { Seat = 2, CoreRam = "0x62", Bytes = 2, Dead = new() { CoreRam = "0x71", Value = 1 } },
            new() { Seat = 3, CoreRam = "0x64", Bytes = 2, Dead = new() { CoreRam = "0x72", Value = 1 } },
        ],
    };

    [Fact]
    public void Score_fige_le_continue_ne_compte_plus()
    {
        // P1 meurt a 1000 puis continue jusqu'a 5000 ; P2, en vie au signal, garde ses 3000 ; P3 meurt a 2000.
        var rangs = Rejouer(new MultiplayerClassementEnCours(Rampage, [1, 2, 3]),
            (R(null, 0, (1, 0, 0, null), (2, 0, 0, null), (3, 0, 0, null)), 2),
            (R(null, 0, (1, 1000, 0, null), (2, 1500, 0, null), (3, 2000, 0, null)), 4),
            (R(null, 0, (1, 1000, 1, null), (2, 2500, 0, null), (3, 2000, 1, null)), 4),
            (R(null, 0, (1, 5000, 0, null), (2, 3000, 0, null), (3, 2000, 1, null)), 4),
            (R(null, 1, (1, 5000, 0, null), (2, 3000, 0, null), (3, 2000, 1, null)), 4));
        Assert.Equal("2:1 3:2 1:3", Dire(rangs));
    }

    [Fact]
    public void Score_fige_finit_quand_chaque_humain_est_mort_une_fois()
    {
        var rangs = Rejouer(new MultiplayerClassementEnCours(Rampage, [1, 2]),
            (R(null, 0, (1, 0, 0, null), (2, 0, 0, null)), 2),
            (R(null, 0, (1, 700, 0, null), (2, 900, 0, null)), 4),
            (R(null, 0, (1, 700, 1, null), (2, 900, 1, null)), 4));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Une_valeur_du_68000_et_un_score_BCD_se_lisent()
    {
        var place = new MultiplayerRankingSeat { Bytes = 4, Order = "68k", Bcd = true };
        // 00 12 34 56 range par FBNeo octets permutes deux a deux : 12 00 56 34.
        Assert.Equal(123456, place.Valeur([0x12, 0x00, 0x56, 0x34]));
        Assert.Null(place.Valeur([0x1A, 0x00, 0x00, 0x00]));
        Assert.Equal(0x0102, new MultiplayerRankingSeat { Bytes = 2, Order = "be" }.Valeur([0x01, 0x02]));
        Assert.Equal(0x0201, new MultiplayerRankingSeat { Bytes = 2 }.Valeur([0x01, 0x02]));
    }

    // ── La lecture dans RetroArch ─────────────────────────────────────────

    /// <summary>Un RetroArch dont la carte du coeur (READ_CORE_MEMORY) est seule lisible, comme mGBA Splitscreen.</summary>
    private sealed class CarteSeule : ICommandesRetroArch
    {
        public readonly Dictionary<long, int> Carte = new();
        public readonly List<string> Recues = new();
        private readonly object _verrou = new();

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            var morceaux = commande.Split(' ');
            lock (_verrou)
            {
                Recues.Add(morceaux[0]);
                if (morceaux[0] != "READ_CORE_MEMORY")
                {
                    // READ_CORE_RAM sous mGBA Splitscreen : des octets fixes, jamais la memoire du jeu (mesure du 29/10).
                    return Task.FromResult<string?>(morceaux[0] + " " + morceaux[1] + " 53");
                }

                var debut = long.Parse(morceaux[1], NumberStyles.HexNumber);
                var n = int.Parse(morceaux[2], CultureInfo.InvariantCulture);
                return Task.FromResult<string?>("READ_CORE_MEMORY " + morceaux[1] + " "
                    + string.Join(' ', Enumerable.Range(0, n).Select(i => Carte.GetValueOrDefault(debut + i).ToString("X2"))));
            }
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;

        public void Poser(long adresse, int valeur)
        {
            lock (_verrou)
            {
                Carte[adresse] = valeur;
            }
        }

        public int Lectures(string commande)
        {
            lock (_verrou)
            {
                return Recues.Count(r => r == commande);
            }
        }
    }

    [Fact]
    public async Task Une_fin_a_plusieurs_se_lit_dans_la_carte_du_coeur()
    {
        var ra = new CarteSeule();
        var jeu = new MultiplayerGame { Id = "gba/sonic-battle", Players = 4, EloType = "combat", RankingEnd = SonicBattle with { SettleMs = 0 } };
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(5));
        var attente = guet.AttendreLIssueAsync(jeu, [1, 2, 3], CancellationToken.None);
        for (var i = 0; i < 200 && ra.Lectures("READ_CORE_MEMORY") < 30; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(attente.IsCompleted);
        ra.Poser(0x03001648, 5);
        ra.Poser(0x03001649, 2);
        var issue = await attente.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, issue!.Gagnante);
        Assert.Equal("2:1 3:2 1:3", Dire(issue.Rangs));
        Assert.Equal(0, ra.Lectures("READ_CORE_RAM"));
    }

    [Fact]
    public async Task Une_fin_en_manches_se_lit_aussi_dans_la_carte_du_coeur()
    {
        var ra = new CarteSeule();
        var fin = new MultiplayerEnd
        {
            FirstTo = 2, Memory = "core_map",
            Seats = [new() { Seat = 1, CoreRam = "0x0300C5E3" }, new() { Seat = 2, CoreRam = "0x1300C5E3" }],
        };
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(5));
        var attente = guet.AttendreLeVainqueurAsync(fin, [1, 2], CancellationToken.None);
        await Task.Delay(60);
        ra.Poser(0x1300C5E3, 2);
        Assert.Equal(2, await attente.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, ra.Lectures("READ_CORE_RAM"));
    }

    [Fact]
    public async Task Une_seule_place_humaine_ne_se_classe_pas()
    {
        var guet = new MultiplayerFinDeRencontre(new CarteSeule(), TimeSpan.FromMilliseconds(5));
        var jeu = new MultiplayerGame { Id = "gba/sonic-battle", Players = 4, EloType = "combat", RankingEnd = SonicBattle };
        Assert.Null(await guet.AttendreLIssueAsync(jeu, [1], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(jeu.ClassePour([1]));
        Assert.True(jeu.ClassePour([1, 2]));
    }

    [Fact]
    public async Task Les_statistiques_se_lisent_dans_la_carte_du_coeur()
    {
        var ra = new CarteSeule();
        var profil = JsonSerializer.Deserialize<MultiplayerStatsProfil>("""
            { "id": "gba/sonic-battle/battle", "version": 1, "players": 2,
              "signals": { "points": { "source": "poll", "memory": "core_map", "seats": { "1": "0x03001647", "2": "0x03001648" }, "bytes": 1, "min": 0, "max": 99 } },
              "counters": { "kos": { "rule": "somme_hausse", "signal": "points" } },
              "metrics": [
                { "category": "execution", "id": "kos", "label": "multiplayer.stat.kos", "formula": "kos", "unit": "count", "better": "higher" },
                { "category": "efficacite", "id": "kos2", "label": "multiplayer.stat.kos", "formula": "kos", "unit": "count", "better": "higher" },
                { "category": "maitrise", "id": "kos3", "label": "multiplayer.stat.kos", "formula": "kos", "unit": "count", "better": "higher" } ] }
            """)!;
        Assert.Null(MultiplayerStatsProfil.Valider(profil));
        using var fin = new CancellationTokenSource();
        var suivi = new MultiplayerStatsMoteur(ra, TimeSpan.FromMilliseconds(5)).Suivre(profil, [1, 2], fin.Token);
        await Task.Delay(60);
        ra.Poser(0x03001647, 3);
        await Task.Delay(120);
        fin.Cancel();
        var resultat = await suivi.Fin.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3L, resultat.Compteurs[1]["kos"]);
        Assert.Equal(0, ra.Lectures("READ_CORE_RAM"));
    }

    // ── Le catalogue ──────────────────────────────────────────────────────

    private static MultiplayerCatalogue Lire(string json)
        => JsonSerializer.Deserialize<MultiplayerCatalogue>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private const string SonicBattleAuCatalogue = """
        { "id": "gba/sonic-battle", "system": "gba", "name": "Sonic Battle", "players": 4, "elo_type": "combat",
          "content": { "md5": ["100579ef01225c620560f88e65ca423a"] }, "min_api": "1.9.127",
          "ranking_end": { "rule": "manches", "memory": "core_map", "first_to": 5, "settle_ms": 1000, "min_humans": 2, "close_after_s": 5,
            "guard": { "core_ram": "0x03002600", "value": 0 },
            "seats": [ { "seat": 1, "core_ram": "0x03001647" }, { "seat": 2, "core_ram": "0x03001648" },
                       { "seat": 3, "core_ram": "0x03001649" }, { "seat": 4, "core_ram": "0x0300164A" } ] } }
        """;

    [Fact]
    public void La_livraison_de_Sonic_Battle_est_un_jeu_classe()
    {
        var catalogue = Lire("{ \"schema_version\": 1, \"games\": [" + SonicBattleAuCatalogue + "] }");
        Assert.Null(MultiplayerCatalogueStore.Valider(catalogue));
        var jeu = catalogue.Games[0];
        Assert.True(jeu.FinLue);
        Assert.True(jeu.EstClasse);
        Assert.True(jeu.RankingEnd!.ParLaCarteDuCoeur);
        Assert.Equal(5, jeu.DelaiDeFermeture);
    }

    [Theory]
    [InlineData("\"rule\": \"relais\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "regle « relais » inconnue")]
    [InlineData("\"rule\": \"manches\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "manches sans seuil")]
    [InlineData("\"rule\": \"manches\", \"first_to\": 3, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" } ]", "pas d'entree pour la place 2")]
    [InlineData("\"rule\": \"score_fige\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "n'a pas ce que la regle")]
    [InlineData("\"rule\": \"arrivee\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "arrivee sans drapeau")]
    [InlineData("\"rule\": \"survie\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\", \"camp\": 1 }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "camps incoherents")]
    [InlineData("\"rule\": \"survie\", \"memory\": \"ram\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "memoire « ram » inconnue")]
    public void Une_fin_a_plusieurs_douteuse_est_refusee(string classement, string raison)
    {
        var json = "{ \"schema_version\": 1, \"games\": [ { \"id\": \"a\", \"system\": \"arcade\", \"set\": \"a\", \"players\": 2, \"ranking_end\": { "
            + classement + " } } ] }";
        Assert.Contains(raison, MultiplayerCatalogueStore.Valider(Lire(json)));
    }

    [Fact]
    public void Une_fin_a_plusieurs_ne_se_porte_pas_avec_une_autre_fin()
    {
        var json = "{ \"schema_version\": 1, \"games\": [ { \"id\": \"a\", \"system\": \"arcade\", \"set\": \"a\", \"players\": 2, "
            + "\"end\": { \"first_to\": 2, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ] }, "
            + "\"ranking_end\": { \"rule\": \"manches\", \"first_to\": 2, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ] } } ] }";
        Assert.Contains("ranking_end avec end ou match_end", MultiplayerCatalogueStore.Valider(Lire(json)));
    }

    [Fact]
    public void Un_jeu_invalide_est_ecarte_seul_et_un_jeu_trop_recent_aussi()
    {
        // Jusqu'a la 1.9.126, le match_end inconnu de « b » faisait refuser tout le catalogue (CDC Classements v1.0 §2). Le
        // champ « bios » de « d » ne compte pas ici : l'outil de diagnostic le lit (decision du user du 29/10).
        var json = """
            { "schema_version": 1, "games": [
              { "id": "a", "system": "arcade", "set": "a", "players": 2 },
              { "id": "b", "system": "arcade", "set": "b", "players": 2, "match_end": { "type": "chrono" } },
              { "id": "c", "system": "arcade", "set": "c", "players": 2, "min_api": "9.0.0", "match_end": { "type": "chrono" } },
              { "id": "a", "system": "arcade", "set": "a2", "players": 2 },
              { "id": "d", "system": "arcade", "set": "d", "players": 2, "bios": ["namcoc75.zip"] } ] }
            """;
        var retenu = MultiplayerCatalogueStore.Retenir(Lire(json), new Version(1, 9, 127), out var erreur, out var ecartes);
        Assert.Null(erreur);
        Assert.Equal(["a", "d"], retenu!.Games.Select(j => j.Id).ToArray());
        Assert.Equal(2, ecartes.Count);
        Assert.Contains(ecartes, e => e.StartsWith("b : fin de rencontre : type « chrono » inconnu", StringComparison.Ordinal));
        Assert.Contains(ecartes, e => e.Contains("en double", StringComparison.Ordinal));

        // Le schema inconnu, lui, refuse toujours le fichier entier.
        Assert.Null(MultiplayerCatalogueStore.Retenir(Lire("{ \"schema_version\": 2, \"games\": [] }"), new Version(1, 9, 127), out var schema, out _));
        Assert.Contains("schema 2 inconnu", schema);
    }

    [Fact]
    public void Le_delai_des_choix_se_compte_en_images_et_reste_borne()
    {
        Assert.Equal(150, new MultiplayerStart { WriteAfterFrames = 9 }.DelaiDesChoix!.Value.TotalMilliseconds, 3);
        Assert.Null(new MultiplayerStart().DelaiDesChoix);
        var jeu = new MultiplayerGame
        {
            Id = "arcade/magdrop3", System = "arcade", Set = "magdrop3", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = new() { State = "states/arcade/magdrop3/valide-2p-fbneo.state", Sha256 = new string('a', 64), WriteAfterFrames = 601 },
        };
        Assert.Contains("delai d'ecriture des choix", MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu with { Start = jeu.Start with { WriteAfterFrames = 9 } }));
    }
}
