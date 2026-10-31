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

    /// <summary>Bomber Man World (livraison du 29/10) : compteurs BCD, premier a 3, fin au GAME OVER (0x11328 = 2), sans garde.</summary>
    private static readonly MultiplayerRankingEnd BomberManWorld = new()
    {
        Rule = MultiplayerRankingEnd.Manches, FirstTo = 3, SettleMs = 1000, Keep = MultiplayerRankingEnd.LaPlusHaute,
        Over = new() { CoreRam = "0x11328", Value = 2 },
        Seats =
        [
            new() { Seat = 1, CoreRam = "0x113B5", Bcd = true }, new() { Seat = 2, CoreRam = "0x113C5", Bcd = true },
            new() { Seat = 3, CoreRam = "0x113D5", Bcd = true }, new() { Seat = 4, CoreRam = "0x113E5", Bcd = true },
        ],
    };

    [Fact]
    public void Manches_un_humain_elimine_garde_ses_victoires()
    {
        // Mesure de la livraison (v1.1 §7, n° 29) : P1, elimine avec une victoire, passe au tableau des meilleurs et son
        // compteur retombe a 0 pendant que P2 joue encore. Au GAME OVER, chacun a une victoire : match nul, pas une defaite.
        var rangs = Rejouer(new MultiplayerClassementEnCours(BomberManWorld, [1, 2]),
            (R(null, 0, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(null, 0, V(1, 1), V(2, 0), V(3, 0), V(4, 0)), 6),
            (R(null, 0, V(1, 1), V(2, 1), V(3, 0), V(4, 0)), 6),
            (R(null, 0, V(1, 0), V(2, 1), V(3, 0), V(4, 0)), 6),
            (R(null, 2, V(1, 0), V(2, 1), V(3, 0), V(4, 0)), 6));
        Assert.Equal("1:1 2:1", Dire(rangs));
        Assert.Equal(MultiplayerResultats.MatchNul, MultiplayerIssue.DesRangs(rangs!).Gagnante);
    }

    [Fact]
    public void Manches_une_valeur_lue_au_vol_ne_devient_pas_la_plus_haute()
    {
        var rangs = Rejouer(new MultiplayerClassementEnCours(BomberManWorld, [1, 2]),
            (R(null, 0, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(null, 0, V(1, 2), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(null, 0, V(1, 0), V(2, 1), V(3, 0), V(4, 0)), 6),
            (R(null, 2, V(1, 0), V(2, 1), V(3, 0), V(4, 0)), 6));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Manches_la_plus_haute_valeur_repart_de_zero_hors_de_la_garde()
    {
        // P1 a 4 K.O., puis un joueur ouvre le menu RULE : la rencontre repart, ses 4 K.O. d'avant ne comptent plus.
        var rangs = Rejouer(new MultiplayerClassementEnCours(SonicBattle with { Keep = MultiplayerRankingEnd.LaPlusHaute }, [1, 2, 3]),
            (R(0, null, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(0, null, V(1, 4), V(2, 0), V(3, 0), V(4, 0)), 6),
            (R(1, null, V(1, 4), V(2, 0), V(3, 0), V(4, 0)), 4),
            (R(0, null, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 6),
            (R(0, null, V(1, 0), V(2, 5), V(3, 2), V(4, 0)), 6));
        Assert.Equal("2:1 3:2 1:3", Dire(rangs));
    }

    [Fact]
    public void Manches_sans_keep_un_compteur_qui_baisse_compte_a_sa_derniere_valeur()
    {
        // Doom II (v1.1 §7, n° 6) : P1 a 2 frags puis se suicide (1) ; au signal de fin, P2 (2) passe devant.
        var doom = BomberManWorld with { Keep = null, FirstTo = 10 };
        var rangs = Rejouer(new MultiplayerClassementEnCours(doom, [1, 2]),
            (R(null, 0, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 2),
            (R(null, 0, V(1, 2), V(2, 1), V(3, 0), V(4, 0)), 6),
            (R(null, 0, V(1, 1), V(2, 2), V(3, 0), V(4, 0)), 6),
            (R(null, 2, V(1, 1), V(2, 2), V(3, 0), V(4, 0)), 6));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Une_valeur_signee_passe_sous_zero()
    {
        var place = new MultiplayerRankingSeat { Seat = 1, CoreRam = "0x1", Bytes = 2, Signed = true };
        Assert.Equal(-1L, place.Valeur([0xFF, 0xFF]));
        Assert.Equal(-2L, place.Valeur([0xFE, 0xFF]));
        Assert.Equal(32767L, place.Valeur([0xFF, 0x7F]));
        Assert.Equal(-1L, (place with { Bytes = 1 }).Valeur([0xFF]));
        Assert.Equal(255L, (place with { Bytes = 1, Signed = false }).Valeur([0xFF]));
    }

    [Fact]
    public void Score_fige_le_plus_bas_d_abord()
    {
        // Mario Golf (v1.1 §7, n° 5 et 6) : le score au par, signe ; -2 bat +1. Fin au signal, chacun en jeu.
        var golf = new MultiplayerRankingEnd
        {
            Rule = MultiplayerRankingEnd.ScoreFige, Better = "lower", Over = new() { CoreRam = "0x30", Value = 1 }, SettleMs = 1000,
            Seats = [new() { Seat = 1, CoreRam = "0x10", Signed = true }, new() { Seat = 2, CoreRam = "0x11", Signed = true }],
        };
        var rangs = Rejouer(new MultiplayerClassementEnCours(golf, [1, 2]),
            (R(null, 0, V(1, 0), V(2, 0)), 2),
            (R(null, 0, V(1, 1), V(2, -2)), 6),
            (R(null, 1, V(1, 1), V(2, -2)), 6));
        Assert.Equal("2:1 1:2", Dire(rangs));
    }

    [Fact]
    public void Un_signal_a_plusieurs_valeurs_prend_chacune()
    {
        // F-Zero MV (v1.1 §7, n° 2) : l'arrivee vaut 06 ou 10 selon la course.
        var arrivee = new MultiplayerDuelGuard { CoreRam = "0x1", Values = [6, 16] };
        Assert.True(arrivee.Vaut(6));
        Assert.True(arrivee.Vaut(16));
        Assert.False(arrivee.Vaut(0));
        Assert.True(new MultiplayerDuelGuard { CoreRam = "0x1", Value = 3 }.Vaut(3));

        var course = new MultiplayerRankingEnd
        {
            Rule = MultiplayerRankingEnd.Arrivee, Position = MultiplayerRankingEnd.OrdreDArrivee, SettleMs = 500,
            Seats = [new() { Seat = 1, Finished = arrivee }, new() { Seat = 2, Finished = arrivee }],
        };
        var rangs = Rejouer(new MultiplayerClassementEnCours(course, [1, 2]),
            (R(null, null, (1, null, null, 0), (2, null, null, 0)), 2),
            (R(null, null, (1, null, null, 0), (2, null, null, 16)), 4),
            (R(null, null, (1, null, null, 6), (2, null, null, 16)), 4));
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

    /// <summary>Un combat fini au temps : P3 tombe, puis le temps s'ecoule avec P1 (30) et P2 (50) debout ; P4 est l'ordinateur.</summary>
    private static readonly (MultiplayerReleve, int)[] FiniAuTemps =
    [
        (R(null, 0, V(1, 90), V(2, 90), V(3, 90), V(4, 90)), 2),
        (R(null, 0, V(1, 60), V(2, 70), V(3, 0), V(4, 40)), 8),
        (R(null, 1, V(1, 30), V(2, 50), V(3, 0), V(4, 40)), 8),
    ];

    [Fact]
    public void Survie_au_temps_le_departage_classe_les_survivants_par_leur_vie()
    {
        // Sugoi Hebereke (v1.1 §7, n° 20) : la vie restante departage ceux qui sont encore debout au signal de fin.
        var auTemps = Survie with { Over = new() { CoreRam = "0x20", Value = 1 } };
        Assert.Equal("2:1 1:2 3:3", Dire(Rejouer(new MultiplayerClassementEnCours(auTemps with { TieBreak = new() }, [1, 2, 3]), FiniAuTemps)));
        Assert.Equal("1:1 2:2 3:3", Dire(Rejouer(new MultiplayerClassementEnCours(auTemps with { TieBreak = new() { Better = "lower" } }, [1, 2, 3]), FiniAuTemps)));
        // Sans departage, ils partagent leur rang, comme avant.
        Assert.Equal("1:1 2:1 3:3", Dire(Rejouer(new MultiplayerClassementEnCours(auTemps, [1, 2, 3]), FiniAuTemps)));
    }

    [Fact]
    public void Survie_au_temps_deux_vies_egales_partagent_leur_rang()
    {
        var fin = Survie with { Over = new() { CoreRam = "0x20", Value = 1 }, TieBreak = new() };
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2, 3]),
            (R(null, 0, V(1, 90), V(2, 90), V(3, 90), V(4, 90)), 2),
            (R(null, 1, V(1, 50), V(2, 50), V(3, 20), V(4, 0)), 8));
        Assert.Equal("1:1 2:1 3:3", Dire(rangs));
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
    public void Une_victoire_de_camp_n_est_pas_un_match_nul()
    {
        // Slam Masters a quatre (couloir 3, 30/10) : camp 1 (places 1 et 2) contre camp 2 (3 et 4), le camp 2 gagne.
        IReadOnlyList<MultiplayerRang> rangs = [new(3, 1, 2), new(4, 1, 2), new(1, 2, 1), new(2, 2, 1)];
        var issue = MultiplayerIssue.DesRangs(rangs);
        Assert.Equal(3, issue.Gagnante);
        Assert.Equal(2, MultiplayerIssue.UnitesDe(rangs));

        // Tout le camp vainqueur a gagne, tout le camp perdant a perdu : DEFAITE, pas « 2E PLACE ».
        static string T(string cle) => cle;
        Assert.All(new[] { 3, 4 }, place => Assert.True(MultiplayerPanelService.EstVainqueur(place, rangs, issue.Gagnante)));
        Assert.All(new[] { 1, 2 }, place => Assert.False(MultiplayerPanelService.EstVainqueur(place, rangs, issue.Gagnante)));
        Assert.Equal("multiplayer.result.victory", MultiplayerPanelService.IssueDuRang(rangs[1] with { Camp = null }, rangs, issue.Gagnante, T));
        Assert.Equal("multiplayer.result.defeat", MultiplayerPanelService.IssueDuRang(rangs[3] with { Camp = null }, rangs, issue.Gagnante, T));

        // Les deux camps au rang 1 : un vrai nul.
        IReadOnlyList<MultiplayerRang> nul = [new(1, 1, 1), new(2, 1, 2)];
        Assert.Equal(MultiplayerResultats.MatchNul, MultiplayerIssue.DesRangs(nul).Gagnante);
        Assert.False(MultiplayerPanelService.EstVainqueur(1, nul, MultiplayerResultats.MatchNul));

        // Trois camps ou plus, ou chacun pour soi : le rang reste affiche.
        IReadOnlyList<MultiplayerRang> trois = [new(1, 1), new(2, 2), new(3, 3)];
        Assert.Equal(1, MultiplayerIssue.DesRangs(trois).Gagnante);
        Assert.StartsWith("#", MultiplayerPanelService.IssueDuRang(trois[1], trois, 1, T));
        // Deux humains a egalite sans camp : un nul, comme avant.
        Assert.Equal(MultiplayerResultats.MatchNul, MultiplayerIssue.DesRangs([new(1, 1), new(2, 1), new(3, 3)]).Gagnante);
    }

    [Fact]
    public void Survie_un_camp_tombe_avec_son_premier_membre()
    {
        // WrestleFest en Tag (v1.1 §7, n° 15) : P1 et l'ordinateur (P2) contre P3 et l'ordinateur (P4) ; P2 tombe le premier.
        var tag = Survie with
        {
            Seats = [new() { Seat = 1, CoreRam = "0x10", Camp = 1 }, new() { Seat = 2, CoreRam = "0x11", Camp = 1 },
                new() { Seat = 3, CoreRam = "0x12", Camp = 2 }, new() { Seat = 4, CoreRam = "0x13", Camp = 2 }],
        };
        (MultiplayerReleve, int)[] suite =
        [
            (R(null, null, V(1, 5), V(2, 5), V(3, 5), V(4, 5)), 2),
            (R(null, null, V(1, 5), V(2, 0), V(3, 5), V(4, 5)), 8),
        ];
        Assert.Equal("3:1/2 1:2/1", Dire(Rejouer(new MultiplayerClassementEnCours(tag with { CampOut = "first" }, [1, 3]), suite)));
        // Par defaut, le camp 1 tient tant que P1 est debout.
        Assert.Null(Rejouer(new MultiplayerClassementEnCours(tag, [1, 3]), suite));
    }

    [Fact]
    public void Survie_une_mort_qui_vaut_zero_se_lit()
    {
        // v1.1 §7, n° 17 : l'octet de la mort vaut 1 en jeu et 0 a la chute.
        var fin = new MultiplayerRankingEnd
        {
            Rule = MultiplayerRankingEnd.Survie, SettleMs = 1000,
            Seats = [new() { Seat = 1, Dead = new() { CoreRam = "0x20", Value = 0 } }, new() { Seat = 2, Dead = new() { CoreRam = "0x21", Value = 0 } }],
        };
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2]),
            (R(null, null, (1, null, 1, null), (2, null, 1, null)), 2),
            (R(null, null, (1, null, 1, null), (2, null, 0, null)), 8));
        Assert.Equal("1:1 2:2", Dire(rangs));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeClassement(new MultiplayerGame { Id = "a", System = "arcade", Players = 2, RankingEnd = fin }));
    }

    [Fact]
    public void Une_attente_de_fin_au_dela_de_10_s_passe_par_un_champ_nouveau()
    {
        // CDC Demandes v1.2, D2 (31/10) : le vainqueur de Sugoi Hebereke se lit 11 s apres l'elimination.
        var fin = new MultiplayerRankingEnd
        {
            Rule = MultiplayerRankingEnd.Survie, SettleMs = 10000, SettleExtraMs = 2000,
            Seats = [new() { Seat = 1, Dead = new() { CoreRam = "0x20", Value = 0 } }, new() { Seat = 2, Dead = new() { CoreRam = "0x21", Value = 0 } }],
        };
        Assert.Equal(12000, fin.AttenteMs);
        Assert.Null(MultiplayerCatalogueStore.ValiderLeClassement(new MultiplayerGame { Id = "a", System = "arcade", Players = 2, RankingEnd = fin }));
        // Une chute ne se retient qu'apres 12 s de tenue : a 11 s, personne n'est encore elimine.
        var avant = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2]),
            (R(null, null, (1, null, 1, null), (2, null, 1, null)), 2),
            (R(null, null, (1, null, 1, null), (2, null, 0, null)), 11));
        Assert.Null(avant);
        Assert.NotNull(MultiplayerCatalogueStore.ValiderLeClassement(new MultiplayerGame
            { Id = "a", System = "arcade", Players = 2, RankingEnd = fin with { SettleExtraMs = 25000 } }));
        Assert.NotNull(MultiplayerCatalogueStore.ValiderLeClassement(new MultiplayerGame
            { Id = "a", System = "arcade", Players = 2, RankingEnd = fin with { SettleExtraMs = -1 } }));
        // Une version d'avant ne connait pas le champ : il se lit, et ne change rien quand il manque.
        var lu = System.Text.Json.JsonSerializer.Deserialize<MultiplayerRankingEnd>("{\"rule\":\"survie\",\"settle_ms\":10000,\"settle_extra_ms\":1500}")!;
        Assert.Equal(11500, lu.AttenteMs);
        Assert.Equal(1000, new MultiplayerRankingEnd().AttenteMs);
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
    public void Score_fige_sans_mort_le_score_du_signal_de_fin_classe_les_camps()
    {
        // NBA Jam TE remis dans l'ordre de la plateforme (seat_order [1, 3, 2, 4]) : places 1 et 3 au camp 1, 2 et 4 au camp 2,
        // un score par camp. L'etat garde 3 points de la demo ; FINAL (50) fige le score.
        var fin = new MultiplayerRankingEnd
        {
            Rule = MultiplayerRankingEnd.ScoreFige, Over = new() { CoreRam = "0x1D8DA", Value = 50 }, SettleMs = 1000,
            Seats =
            [
                new() { Seat = 1, CoreRam = "0x1D5FA", Bytes = 2, Camp = 1 }, new() { Seat = 2, CoreRam = "0x1D5FC", Bytes = 2, Camp = 2 },
                new() { Seat = 3, CoreRam = "0x1D5FA", Bytes = 2, Camp = 1 }, new() { Seat = 4, CoreRam = "0x1D5FC", Bytes = 2, Camp = 2 },
            ],
        };
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2]),
            (R(null, 22, V(1, 3), V(2, 0), V(3, 3), V(4, 0)), 2),
            (R(null, 22, V(1, 0), V(2, 0), V(3, 0), V(4, 0)), 4),
            (R(null, 17, V(1, 12), V(2, 20), V(3, 12), V(4, 20)), 8),
            (R(null, 50, V(1, 12), V(2, 20), V(3, 12), V(4, 20)), 8));
        Assert.Equal("2:1/2 1:2/1", Dire(rangs));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeClassement(new MultiplayerGame { Id = "a", System = "arcade", Players = 4, RankingEnd = fin }));
        // Sans signal de fin ni mort, rien ne fige le score : refuse.
        Assert.Contains("n'a pas ce que la regle",
            MultiplayerCatalogueStore.ValiderLeClassement(new MultiplayerGame { Id = "a", System = "arcade", Players = 4, RankingEnd = fin with { Over = null } }));
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

    [Fact]
    public void Une_fin_par_contenu_ne_classe_que_les_contenus_listes()
    {
        // Sonic Battle (v1.1 §7, n° 9) : seules les adresses de l'USA sont mesurees ; l'Europe joue en partie libre.
        const string Usa = "100579ef01225c620560f88e65ca423a", Europe = "7194f25da6be4548f7e0c80f73e3f628";
        var catalogue = Lire("{ \"schema_version\": 1, \"games\": [" + SonicBattleAuCatalogue.Replace(
            "\"content\": { \"md5\": [\"100579ef01225c620560f88e65ca423a\"] }",
            "\"content\": { \"md5\": [\"" + Usa + "\", \"" + Europe + "\"] }") + "] }");
        var sansContenu = catalogue.Games[0];
        var jeu = sansContenu with { RankingEndByContent = new Dictionary<string, MultiplayerRankingEnd> { [Usa.ToUpperInvariant()] = SonicBattle } };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeClassement(jeu));

        Assert.Same(SonicBattle, jeu.PourLeContenu(Usa).RankingEnd);
        Assert.True(jeu.PourLeContenu(Usa).ClassePour([1, 2]));
        var europe = jeu.PourLeContenu(Europe);
        Assert.Null(europe.RankingEnd);
        Assert.False(europe.FinLue);
        Assert.False(europe.ClassePour([1, 2]));
        // Sans fin par contenu, rien ne change.
        Assert.Same(sansContenu, sansContenu.PourLeContenu(Europe));

        // Une cle qui n'est pas un md5 du jeu (un CRC), ou sans ranking_end : refuse.
        Assert.Contains("fins par contenu incoherentes", MultiplayerCatalogueStore.ValiderLeClassement(
            jeu with { RankingEndByContent = new Dictionary<string, MultiplayerRankingEnd> { ["9ec9d86f"] = SonicBattle } }));
        Assert.Contains("fins par contenu incoherentes", MultiplayerCatalogueStore.ValiderLeClassement(jeu with { RankingEnd = null }));
        Assert.Contains("(contenu " + Usa + ")", MultiplayerCatalogueStore.ValiderLeClassement(
            jeu with { RankingEndByContent = new Dictionary<string, MultiplayerRankingEnd> { [Usa] = SonicBattle with { FirstTo = null } } }));
    }

    [Theory]
    [InlineData("\"rule\": \"relais\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "regle « relais » inconnue")]
    [InlineData("\"rule\": \"manches\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "manches sans seuil")]
    [InlineData("\"rule\": \"manches\", \"first_to\": 3, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" } ]", "pas d'entree pour la place 2")]
    [InlineData("\"rule\": \"score_fige\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "n'a pas ce que la regle")]
    [InlineData("\"rule\": \"arrivee\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "arrivee sans drapeau")]
    [InlineData("\"rule\": \"survie\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\", \"camp\": 1 }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "camps incoherents")]
    [InlineData("\"rule\": \"manches\", \"first_to\": 3, \"tie_break\": { \"better\": \"higher\" }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "departage incoherent")]
    [InlineData("\"rule\": \"survie\", \"tie_break\": { \"better\": \"most\" }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "departage incoherent")]
    [InlineData("\"rule\": \"survie\", \"tie_break\": { \"core_ram\": \"0x9\" }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "departage incoherent")]
    [InlineData("\"rule\": \"survie\", \"tie_break\": { }, \"seats\": [ { \"seat\": 1, \"dead\": { \"core_ram\": \"0x1\", \"value\": 1 } }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "departage incoherent")]
    [InlineData("\"rule\": \"survie\", \"keep\": \"highest\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "keep (manches) ou better (score_fige) incoherent")]
    [InlineData("\"rule\": \"manches\", \"first_to\": 3, \"better\": \"lower\", \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "keep (manches) ou better (score_fige) incoherent")]
    [InlineData("\"rule\": \"manches\", \"first_to\": 3, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\", \"bcd\": true, \"signed\": true }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "format de la place 1")]
    [InlineData("\"rule\": \"manches\", \"first_to\": 3, \"over\": { \"core_ram\": \"0x9\", \"values\": [1, 300] }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "signal de fin ou garde incoherent")]
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

    // ── Lot B (v1.1 §7) ───────────────────────────────────────────────────

    [Fact]
    public void Un_score_a_un_chiffre_par_octet_se_lit()
    {
        // Rampage : 7 chiffres, poids fort d'abord ; le quartet bas porte le chiffre (une tuile « 3x » aussi).
        var place = new MultiplayerRankingSeat { Bytes = 7, Digits = true, Order = "be" };
        Assert.Equal(1650, place.Valeur([0, 0, 0, 1, 6, 5, 0]));
        Assert.Equal(1650, place.Valeur([0x30, 0x30, 0x30, 0x31, 0x36, 0x35, 0x30]));
        Assert.Null(place.Valeur([0, 0, 0, 1, 0x0A, 5, 0]));
        var signal = JsonSerializer.Deserialize<MultiplayerStatsSignal>("""{ "seats": { "1": "0x3ACD" }, "bytes": 7, "digits": true, "order": "be" }""")!;
        Assert.Equal(6000, signal.Valeur([0, 0, 0, 6, 0, 0, 0]));
    }

    [Fact]
    public void Un_signal_se_lit_sur_sa_largeur()
    {
        // Mario Kart SC : l'arrivee sur un mot de 16 bits (0x000A en petit-boutiste).
        Assert.Equal(10, new MultiplayerDuelGuard { Bytes = 2, Order = "le" }.Valeur([0x0A, 0x00]));
        Assert.Equal(0x0A00, new MultiplayerDuelGuard { Bytes = 2, Order = "be" }.Valeur([0x0A, 0x00]));
        Assert.Null(new MultiplayerDuelGuard { Bytes = 2 }.Valeur([0x0A]));
    }

    private const string LesLivraisonsDuLotB = """
        { "schema_version": 1, "games": [
          { "id": "arcade/rampage", "system": "arcade", "set": "rampage", "players": 3, "elo_type": "arene", "core": "fbneo_multi32", "min_api": "1.9.128",
            "ranking_end": { "rule": "score_fige", "settle_ms": 1000, "min_humans": 2, "close_after_s": 8,
              "over": { "core_ram": "0x3AF1", "value": 4 }, "guard": { "core_ram": "0x3A9B", "value": 0 },
              "seats": [
                { "seat": 1, "core_ram": "0x3ACD", "bytes": 7, "digits": true, "order": "be", "dead": { "core_ram": "0x391F", "value": 3 } },
                { "seat": 2, "core_ram": "0x3AD4", "bytes": 7, "digits": true, "order": "be", "dead": { "core_ram": "0x396F", "value": 3 } },
                { "seat": 3, "core_ram": "0x3ADB", "bytes": 7, "digits": true, "order": "be", "dead": { "core_ram": "0x39BF", "value": 3 } } ] } },
          { "id": "gba/mksc", "system": "gba", "content": { "md5": ["1"] }, "players": 4, "elo_type": "course", "min_api": "1.9.128",
            "ranking_end": { "rule": "arrivee", "memory": "core_map", "rank_base": 0, "over": { "core_ram": "0x03003A28", "bytes": 2, "value": 15 },
              "seats": [
                { "seat": 1, "core_ram": "0x030023B4", "bytes": 1, "finished": { "core_ram": "0x03003A28", "bytes": 2, "order": "le", "value": 10 } },
                { "seat": 2, "core_ram": "0x130023B4", "bytes": 1, "finished": { "core_ram": "0x13003A28", "bytes": 2, "order": "le", "value": 10 } },
                { "seat": 3, "core_ram": "0x230023B4", "bytes": 1, "finished": { "core_ram": "0x23003A28", "bytes": 2, "order": "le", "value": 10 } },
                { "seat": 4, "core_ram": "0x330023B4", "bytes": 1, "finished": { "core_ram": "0x33003A28", "bytes": 2, "order": "le", "value": 10 } } ] } },
          { "id": "gba/kurukuru", "system": "gba", "content": { "md5": ["2"] }, "players": 4, "elo_type": "course", "min_api": "1.9.128",
            "ranking_end": { "rule": "arrivee", "position": "finish_order", "memory": "core_map", "settle_ms": 500,
              "seats": [
                { "seat": 1, "finished": { "core_ram": "0x030045EC", "value": 10 } }, { "seat": 2, "finished": { "core_ram": "0x130045EC", "value": 10 } },
                { "seat": 3, "finished": { "core_ram": "0x230045EC", "value": 10 } }, { "seat": 4, "finished": { "core_ram": "0x330045EC", "value": 10 } } ] } } ] }
        """;

    [Fact]
    public void La_livraison_de_Bomber_Man_World_est_un_jeu_classe()
    {
        var catalogue = Lire("""
            { "schema_version": 1, "games": [
              { "id": "arcade/bbmanw", "system": "arcade", "set": "bbmanw", "players": 4, "elo_type": "arene", "min_api": "1.9.129",
                "ranking_end": { "rule": "manches", "keep": "highest", "first_to": 3, "over": { "core_ram": "0x11328", "value": 2 },
                  "settle_ms": 1000, "min_humans": 2, "close_after_s": 5,
                  "seats": [
                    { "seat": 1, "core_ram": "0x113B5", "bytes": 1, "bcd": true, "order": "le" },
                    { "seat": 2, "core_ram": "0x113C5", "bytes": 1, "bcd": true, "order": "le" },
                    { "seat": 3, "core_ram": "0x113D5", "bytes": 1, "bcd": true, "order": "le" },
                    { "seat": 4, "core_ram": "0x113E5", "bytes": 1, "bcd": true, "order": "le" } ] } } ] }
            """);
        Assert.Null(MultiplayerCatalogueStore.Valider(catalogue));
        Assert.True(catalogue.Games[0].EstClasse);
        Assert.False(catalogue.Games[0].RankingEnd!.ParLaCarteDuCoeur);
        Assert.True(catalogue.Games[0].RankingEnd!.GardeLaPlusHaute);
    }

    [Fact]
    public void Les_livraisons_du_lot_B_sont_des_jeux_classes()
    {
        var catalogue = Lire(LesLivraisonsDuLotB);
        Assert.Null(MultiplayerCatalogueStore.Valider(catalogue));
        Assert.All(catalogue.Games, jeu => Assert.True(jeu.EstClasse, jeu.Id));
        Assert.True(catalogue.Games[2].RankingEnd!.ParOrdreDArrivee);
        Assert.False(catalogue.Games[1].RankingEnd!.ParOrdreDArrivee);
    }

    [Theory]
    [InlineData("\"position\": \"finish_order\", \"seats\": [ { \"seat\": 1, \"finished\": { \"core_ram\": \"0x1\", \"value\": 1 } }, { \"seat\": 2 } ]", "n'a pas ce que la regle")]
    [InlineData("\"rank_base\": 2, \"over\": { \"core_ram\": \"0x9\", \"value\": 1 }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "base ou mode")]
    [InlineData("\"over\": { \"core_ram\": \"0x9\", \"bytes\": 1, \"value\": 300 }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "signal de fin")]
    [InlineData("\"over\": { \"core_ram\": \"0x9\", \"value\": 1 }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\", \"bytes\": 5 }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "format de la place 1")]
    [InlineData("\"over\": { \"core_ram\": \"0x9\", \"value\": 1 }, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\", \"bytes\": 3, \"digits\": true, \"bcd\": true }, { \"seat\": 2, \"core_ram\": \"0x2\" } ]", "format de la place 1")]
    public void Une_arrivee_douteuse_est_refusee(string classement, string raison)
    {
        var json = "{ \"schema_version\": 1, \"games\": [ { \"id\": \"a\", \"system\": \"arcade\", \"set\": \"a\", \"players\": 2, \"ranking_end\": { \"rule\": \"arrivee\", "
            + classement + " } } ] }";
        Assert.Contains(raison, MultiplayerCatalogueStore.Valider(Lire(json)));
    }

    private const string SlamMastersAuCatalogue = """
        { "schema_version": 1, "games": [ { "id": "arcade/slammast", "system": "arcade", "set": "slammast", "players": 4, "elo_type": "combat",
          "ranking_end": { "players": [3, 4], "rule": "manches", "first_to": 1, "guard": { "core_ram": "0xA095", "value": 1 },
            "seats": [ { "seat": 1, "core_ram": "0x929E", "camp": 1 }, { "seat": 2, "core_ram": "0x949E", "camp": 1 },
                       { "seat": 3, "core_ram": "0x969E", "camp": 2 }, { "seat": 4, "core_ram": "0x989E", "camp": 2 } ] },
          "ranking_ends": [ AUTRES ] } ] }
        """;

    private const string DuelDeSlamMasters = """
        { "players": [2], "rule": "manches", "first_to": 1, "guard": { "core_ram": "0xA095", "value": 1 },
          "seats": [ { "seat": 1, "core_ram": "0x929E" }, { "seat": 2, "core_ram": "0x949E" } ] }
        """;

    [Fact]
    public void La_livraison_de_Slam_Masters_est_un_jeu_classe()
    {
        var catalogue = Lire(SlamMastersAuCatalogue.Replace("AUTRES", DuelDeSlamMasters));
        Assert.Null(MultiplayerCatalogueStore.Valider(catalogue));
        Assert.Equal([2], catalogue.Games[0].ClassementPour(2)!.Players);
        Assert.Equal([3, 4], catalogue.Games[0].ClassementPour(4)!.Players);
    }

    [Theory]
    [InlineData("{ \"rule\": \"manches\", \"first_to\": 1, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ] }", "nombres de joueurs des fins incoherents")]
    [InlineData("{ \"players\": [2, 3], \"rule\": \"manches\", \"first_to\": 1, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" }, { \"seat\": 3, \"core_ram\": \"0x3\" } ] }", "nombres de joueurs des fins incoherents")]
    [InlineData("{ \"players\": [5], \"rule\": \"manches\", \"first_to\": 1, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ] }", "nombres de joueurs des fins incoherents")]
    [InlineData("{ \"players\": [2], \"rule\": \"manches\", \"first_to\": 1, \"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" } ] }", "pas d'entree pour la place 2 (fin a 2 joueurs)")]
    public void Une_fin_par_nombre_de_joueurs_douteuse_est_refusee(string autre, string raison)
        => Assert.Contains(raison, MultiplayerCatalogueStore.Valider(Lire(SlamMastersAuCatalogue.Replace("AUTRES", autre))));

    [Fact]
    public void L_ordre_des_places_et_le_camp_qui_tombe_au_premier_se_valident()
    {
        var jeu = new MultiplayerGame
        {
            Id = "a", System = "arcade", Players = 4, EloType = "combat", SeatOrder = [1, 3, 2, 4],
            RankingEnd = Survie with
            {
                CampOut = "first",
                Seats = [new() { Seat = 1, CoreRam = "0x10", Camp = 1 }, new() { Seat = 2, CoreRam = "0x11", Camp = 1 },
                    new() { Seat = 3, CoreRam = "0x12", Camp = 2 }, new() { Seat = 4, CoreRam = "0x13", Camp = 2 }],
            },
        };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeClassement(jeu) ?? MultiplayerCatalogueStore.ValiderLOrdreDesPlaces(jeu));
        Assert.Contains("ordre des places incoherent", MultiplayerCatalogueStore.ValiderLOrdreDesPlaces(jeu with { SeatOrder = [1, 1, 2, 4] }));
        Assert.Contains("ordre des places incoherent", MultiplayerCatalogueStore.ValiderLOrdreDesPlaces(jeu with { SeatOrder = [1, 3, 2] }));
        Assert.Contains("ordre des places incoherent", MultiplayerCatalogueStore.ValiderLOrdreDesPlaces(jeu with { SeatPorts = [[1], [2], [3], [4]] }));
        Assert.Contains("camp_out incoherent", MultiplayerCatalogueStore.ValiderLeClassement(jeu with { RankingEnd = Survie with { CampOut = "first" } }));
        Assert.Contains("camp_out incoherent", MultiplayerCatalogueStore.ValiderLeClassement(jeu with { RankingEnd = jeu.RankingEnd with { CampOut = "last" } }));
    }

    [Fact]
    public void Une_fin_en_duel_ne_prend_qu_une_valeur_de_signal()
    {
        var json = "{ \"schema_version\": 1, \"games\": [ { \"id\": \"a\", \"system\": \"arcade\", \"set\": \"a\", \"players\": 2, "
            + "\"match_end\": { \"type\": \"score_final\", \"over\": { \"core_ram\": \"0x9\", \"value\": 1, \"values\": [1, 2] }, "
            + "\"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ] } } ] }";
        Assert.Contains("signal de fin incoherent", MultiplayerCatalogueStore.Valider(Lire(json)));
    }

    [Fact]
    public void Une_fin_en_duel_garde_ses_signaux_d_un_octet()
    {
        // Une API d'avant lirait un octet : la largeur n'existe que dans une fin a plusieurs.
        var json = "{ \"schema_version\": 1, \"games\": [ { \"id\": \"a\", \"system\": \"arcade\", \"set\": \"a\", \"players\": 2, "
            + "\"match_end\": { \"type\": \"score_final\", \"over\": { \"core_ram\": \"0x9\", \"bytes\": 2, \"value\": 1 }, "
            + "\"seats\": [ { \"seat\": 1, \"core_ram\": \"0x1\" }, { \"seat\": 2, \"core_ram\": \"0x2\" } ] } } ] }";
        Assert.Contains("signal de fin incoherent", MultiplayerCatalogueStore.Valider(Lire(json)));
    }

    [Fact]
    public void Mario_Kart_SC_classe_par_ses_positions_comptees_depuis_zero()
    {
        var fin = Lire(LesLivraisonsDuLotB).Games[1].RankingEnd! with { SettleMs = 500 };
        // P2 (1er, position 0) et P4 (ordinateur) arrivent, puis P1 3e (position 2) ; P3 (4e) n'a pas fini au signal.
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2, 3]),
            (R(null, 7, (1, 2, null, 7), (2, 0, null, 7), (3, 3, null, 7), (4, 1, null, 7)), 2),
            (R(null, 7, (1, 2, null, 7), (2, 0, null, 10), (3, 3, null, 7), (4, 1, null, 10)), 4),
            (R(null, 7, (1, 2, null, 10), (2, 0, null, 10), (3, 3, null, 7), (4, 1, null, 10)), 4),
            (R(null, 15, (1, 2, null, 10), (2, 0, null, 10), (3, 3, null, 7), (4, 1, null, 10)), 4));
        Assert.Equal("2:1 1:2 3:3", Dire(rangs));
    }

    [Fact]
    public void Kurukuru_classe_par_l_ordre_ou_les_drapeaux_tombent()
    {
        var fin = Lire(LesLivraisonsDuLotB).Games[2].RankingEnd!;
        // P3 arrive le premier, puis P1 et P4 dans la meme fenetre de 500 ms, P2 en dernier.
        var rangs = Rejouer(new MultiplayerClassementEnCours(fin, [1, 2, 3, 4]),
            (R(null, null, (1, null, null, 2), (2, null, null, 2), (3, null, null, 2), (4, null, null, 2)), 2),
            (R(null, null, (1, null, null, 2), (2, null, null, 2), (3, null, null, 10), (4, null, null, 2)), 4),
            (R(null, null, (1, null, null, 10), (2, null, null, 2), (3, null, null, 10), (4, null, null, 2)), 1),
            (R(null, null, (1, null, null, 10), (2, null, null, 2), (3, null, null, 10), (4, null, null, 10)), 4),
            (R(null, null, (1, null, null, 10), (2, null, null, 10), (3, null, null, 10), (4, null, null, 10)), 4));
        Assert.Equal("3:1 1:2 4:2 2:4", Dire(rangs));
    }

    [Fact]
    public async Task Un_signal_de_deux_octets_se_lit_dans_la_carte_du_coeur()
    {
        var ra = new CarteSeule();
        var fin = Lire(LesLivraisonsDuLotB).Games[1].RankingEnd! with { SettleMs = 0 };
        var jeu = new MultiplayerGame { Id = "gba/mksc", Players = 4, EloType = "course", RankingEnd = fin };
        foreach (var console in new long[] { 0x03003A28, 0x13003A28, 0x23003A28, 0x33003A28 })
        {
            ra.Poser(console, 7);
        }

        ra.Poser(0x030023B4, 1);
        ra.Poser(0x130023B4, 0);
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(5));
        var attente = guet.AttendreLIssueAsync(jeu, [1, 2], CancellationToken.None);
        for (var i = 0; i < 200 && ra.Lectures("READ_CORE_MEMORY") < 40; i++)
        {
            await Task.Delay(10);
        }

        ra.Poser(0x03003A28, 10);
        ra.Poser(0x13003A28, 10);
        var issue = await attente.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("2:1 1:2", Dire(issue!.Rangs));
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
