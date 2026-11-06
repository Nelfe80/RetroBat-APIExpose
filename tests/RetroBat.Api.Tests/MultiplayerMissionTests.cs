using System.Globalization;
using System.Text.Json;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA FIN D'UNE COOP (V24-16, V24-18 ; nuit du 06/11) : le moteur sur des series de releves (mission accomplie par l'etape ou
/// par son signal, GAME OVER de toute l'equipe, depart a voir avant toute fin, garde, valeurs au vol), la lecture dans
/// RetroArch, le catalogue qui l'admet seulement sur une coop, et la raison de fin envoyee a la plateforme.
/// </summary>
public class MultiplayerMissionTests
{
    /// <summary>Metal Slug 3 sous FBNeo : l'octet de la mission (0x10040C ^ 1), 8 entre deux missions.</summary>
    private static readonly MultiplayerMissionEnd Ms3ALaMission = new()
    {
        CloseOn = MultiplayerMissionEnd.FermerALaMission,
        Stage = new()
        {
            CoreRam = "0x40D", Transition = [8],
            Names = [new() { Value = 0, Name = "MISSION 1" }, new() { Value = 1, Name = "MISSION 2" }, new() { Value = 2, Name = "MISSION 3" }],
        },
        Seats = [new() { Seat = 1, Dead = new() { CoreRam = "0xFDB7", Value = 3 } }, new() { Seat = 2, Dead = new() { CoreRam = "0xFDB6", Value = 3 } }],
    };

    private static readonly MultiplayerMissionEnd Ms3AuGameOver = Ms3ALaMission with { CloseOn = null };

    /// <summary>Un releve : l'etape et la sortie de chaque place (null : pas lue).</summary>
    private static MultiplayerReleveDeMission R(long? etape, long? sortie1 = 0, long? sortie2 = 0, long? garde = null, long? accomplie = null, long? over = null)
        => new(garde, accomplie, over, etape, new Dictionary<int, long?> { [1] = sortie1, [2] = sortie2 });

    /// <summary>Rejoue des releves toutes les 250 ms, chacun tenu le nombre de lectures donne ; l'issue a la fin, ou null.</summary>
    private static MultiplayerFinDeMission? Rejouer(MultiplayerMissionEnCours enCours, params (MultiplayerReleveDeMission Releve, int Lectures)[] suite)
    {
        long ms = 0;
        foreach (var (releve, lectures) in suite)
        {
            for (var i = 0; i < lectures; i++)
            {
                if (enCours.Observer(releve, ms) is { } issue)
                {
                    return issue;
                }

                ms += 250;
            }
        }

        return null;
    }

    [Fact]
    public void Une_mission_accomplie_ferme_a_l_ecran_de_transition()
    {
        var issue = Rejouer(new MultiplayerMissionEnCours(Ms3ALaMission, [1, 2]), (R(0), 20), (R(8), 8));
        Assert.NotNull(issue);
        Assert.True(issue.Accomplie);
        Assert.Equal(0, issue.Etape);
        Assert.Equal("MISSION 1", issue.NomDeLEtape);
        Assert.Equal(1, issue.Franchies);
        Assert.Equal("mission_cleared", issue.Raison);
    }

    [Fact]
    public void Le_game_over_de_toute_l_equipe_passe_avant_la_mission()
    {
        // Un joueur tombe : la partie continue ; les deux dehors : GAME OVER, a la mission en cours.
        var enCours = new MultiplayerMissionEnCours(Ms3ALaMission, [1, 2]);
        Assert.Null(Rejouer(enCours, (R(0), 12), (R(0, sortie1: 3), 20)));
        var issue = Rejouer(new MultiplayerMissionEnCours(Ms3ALaMission, [1, 2]), (R(0), 12), (R(0, sortie1: 3), 20), (R(0, 3, 3), 8));
        Assert.NotNull(issue);
        Assert.False(issue.Accomplie);
        Assert.Equal("MISSION 1", issue.NomDeLEtape);
        Assert.Equal(0, issue.Franchies);
        Assert.Equal("game_over", issue.Raison);
    }

    [Fact]
    public void Rien_ne_finit_avant_d_avoir_vu_la_partie_en_cours()
    {
        // L'invite tourne sa demo, ou l'ecran de GAME OVER de la partie d'avant : les deux places sont deja dehors, l'etape
        // est en transition. Rien ne finit tant que la partie n'a pas ete vue.
        var enCours = new MultiplayerMissionEnCours(Ms3ALaMission, [1, 2]);
        Assert.Null(Rejouer(enCours, (R(8, 3, 3), 20), (R(5, 3, 3), 20)));
        // Puis la partie arrive (l'etat de l'hote) : elle se suit normalement.
        var issue = Rejouer(new MultiplayerMissionEnCours(Ms3ALaMission, [1, 2]), (R(8, 3, 3), 20), (R(1), 12), (R(8), 8));
        Assert.Equal("MISSION 2", issue?.NomDeLEtape);
        Assert.True(issue?.Accomplie);
    }

    [Fact]
    public void Metal_Slug_3_mesure_au_banc_la_demo_ne_compte_jamais()
    {
        // Banc du 06/11 (coeur FBNeo sans RetroArch, etat du catalogue, aucun appui) : chaque place vaut 1 en jeu, 2 au
        // CONTINUE?, 3 au GAME OVER (image 48 912, les deux ensemble), 0 dans la demo qui suit, ou la mission vaut 6, 5, 7.
        // Avec 0 et 3 pour sortie, une borne qui tourne encore sa demo (l'invite avant l'etat de l'hote) ne voit aucun depart.
        var fin = Ms3AuGameOver with
        {
            Seats =
            [
                new() { Seat = 1, Dead = new() { CoreRam = "0xFDB7", Values = [0, 3] } },
                new() { Seat = 2, Dead = new() { CoreRam = "0xFDB6", Values = [0, 3] } },
            ],
        };
        Assert.Null(MultiplayerCatalogueStore.ValiderLeJeu(Coop(fin)));
        Assert.Null(Rejouer(new MultiplayerMissionEnCours(fin, [1, 2]), (R(6, 0, 0), 40), (R(5, 0, 0), 40), (R(7, 0, 0), 40)));
        var issue = Rejouer(new MultiplayerMissionEnCours(fin, [1, 2]),
            (R(6, 0, 0), 40), (R(0, 1, 1), 40), (R(0, 2, 2), 20), (R(0, 2, 1), 20), (R(0, 3, 3), 8), (R(6, 0, 0), 20));
        Assert.Equal(new MultiplayerFinDeMission(false, 0, "MISSION 1", 0), issue);
    }

    [Fact]
    public void Une_valeur_lue_au_vol_ne_termine_rien()
    {
        // L'octet de l'etape passe un instant a 8 (moins d'une seconde) : pas de mission accomplie.
        var enCours = new MultiplayerMissionEnCours(Ms3ALaMission, [1, 2]);
        Assert.Null(Rejouer(enCours, (R(0), 12), (R(8), 3), (R(0), 12), (R(0, 3, 3), 3), (R(0), 12)));
    }

    [Fact]
    public void Jusqu_au_game_over_l_etape_atteinte_et_les_etapes_franchies()
    {
        var issue = Rejouer(new MultiplayerMissionEnCours(Ms3AuGameOver, [1, 2]),
            (R(0), 20), (R(8), 8), (R(1), 20), (R(8), 8), (R(2), 20), (R(2, 3, 0), 8), (R(2, 3, 3), 8));
        Assert.NotNull(issue);
        Assert.False(issue.Accomplie);
        Assert.Equal(2, issue.Etape);
        Assert.Equal("MISSION 3", issue.NomDeLEtape);
        Assert.Equal(2, issue.Franchies);
    }

    [Fact]
    public void Le_signal_de_mission_accomplie_ferme_la_partie_entiere()
    {
        var fin = Ms3AuGameOver with { Cleared = new() { CoreRam = "0x100", Value = 1 } };
        var issue = Rejouer(new MultiplayerMissionEnCours(fin, [1, 2]), (R(0, accomplie: 0), 12), (R(1, accomplie: 0), 12), (R(1, accomplie: 1), 8));
        Assert.True(issue?.Accomplie);
        Assert.Equal("MISSION 2", issue?.NomDeLEtape);
        Assert.Equal(2, issue?.Franchies);
    }

    [Fact]
    public void Hors_de_la_garde_rien_ne_se_lit()
    {
        var fin = Ms3ALaMission with { Guard = new() { CoreRam = "0x10", Value = 1 } };
        // Sans la garde (la demo), une transition ne compte pas ; sous la garde, oui.
        Assert.Null(Rejouer(new MultiplayerMissionEnCours(fin, [1, 2]), (R(0, garde: 1), 12), (R(8, garde: 0), 20)));
        Assert.True(Rejouer(new MultiplayerMissionEnCours(fin, [1, 2]), (R(0, garde: 1), 12), (R(8, garde: 1), 8))?.Accomplie);
    }

    [Fact]
    public void Un_humain_seul_finit_seul_et_une_place_vide_ne_compte_pas()
    {
        // Une partie a trois sur un jeu a quatre : la place 4, que personne ne tient, n'est pas lue.
        var fin = new MultiplayerMissionEnd
        {
            Seats = Enumerable.Range(1, 4).Select(p => new MultiplayerMissionSeat { Seat = p, Dead = new() { CoreRam = "0x" + (0x200 + p).ToString("X", CultureInfo.InvariantCulture), Value = 1 } }).ToList(),
        };
        var trois = new MultiplayerReleveDeMission(null, null, null, null, new Dictionary<int, long?> { [1] = 0, [2] = 0, [3] = 0 });
        var dehors = new MultiplayerReleveDeMission(null, null, null, null, new Dictionary<int, long?> { [1] = 1, [2] = 1, [3] = 1 });
        var issue = Rejouer(new MultiplayerMissionEnCours(fin, [1, 2, 3]), (trois, 12), (dehors, 8));
        Assert.False(issue?.Accomplie);
        Assert.Null(issue?.NomDeLEtape);
    }

    [Fact]
    public void La_raison_de_fin_de_l_hote_dit_l_issue_aux_autres_bornes()
    {
        Assert.True(MultiplayerFinDeMission.DeLaRaison("mission_cleared")?.Accomplie);
        Assert.False(MultiplayerFinDeMission.DeLaRaison("game_over")?.Accomplie);
        Assert.Null(MultiplayerFinDeMission.DeLaRaison("game_closed"));
        Assert.Null(MultiplayerFinDeMission.DeLaRaison(null));
        // La plateforme garde la raison telle quelle si elle tient en a-z et _ sur 32 caracteres (GameroomRepository::terminer).
        Assert.Matches("^[a-z_]{1,32}$", MultiplayerFinDeMission.RaisonAccomplie);
        Assert.Matches("^[a-z_]{1,32}$", MultiplayerFinDeMission.RaisonGameOver);
    }

    [Fact]
    public void Une_etape_sans_nom_se_dit_par_son_rang()
    {
        var etape = new MultiplayerMissionStage { CoreRam = "0x40D", Names = [new() { Value = 0, Name = "MISSION 1" }] };
        Assert.Equal("MISSION 1", etape.NomDe(0));
        Assert.Equal("4", etape.NomDe(3));
    }

    // ── L'ecran de fin ───────────────────────────────────────────────────

    private static string Defaut(string cle, string parDefaut) => parDefaut;

    [Fact]
    public void L_ecran_de_fin_montre_l_etape_a_la_place_du_vainqueur()
    {
        var accomplie = MultiplayerPanelService.AvecLEtape(new MultiplayerFinDeMission(true, 0, "MISSION 1", 1), null, Defaut);
        Assert.Equal(("Stage cleared", "MISSION 1", ""), (accomplie?.Libelle, accomplie?.Valeur, accomplie?.Ruban));
        var tombee = MultiplayerPanelService.AvecLEtape(new MultiplayerFinDeMission(false, 2, "MISSION 3", 2), null, Defaut);
        Assert.Equal(("Stage reached", "MISSION 3", "STAGES CLEARED: 2"), (tombee?.Libelle, tombee?.Valeur, tombee?.Ruban));
        // Sans etape nommee (une raison de fin venue du site), rien.
        Assert.Null(MultiplayerPanelService.AvecLEtape(MultiplayerFinDeMission.DeLaRaison("game_over"), null, Defaut));
        Assert.Null(MultiplayerPanelService.AvecLEtape(null, null, Defaut));
    }

    [Fact]
    public void Une_coop_qui_concourt_garde_son_score_et_met_l_etape_dessous()
    {
        var score = new RetroBat.Api.Leaderboard.LeaderboardOverlayService.ScoreDeFin("Your score", "120 000", "1CC MULTI · #2", "", false);
        var avec = MultiplayerPanelService.AvecLEtape(new MultiplayerFinDeMission(false, 3, "ROUND 4", 3), score, Defaut);
        Assert.Equal("120 000", avec?.Valeur);
        Assert.Equal("STAGE REACHED  ROUND 4", avec?.Second);
        // Le 1LC MULTI tient deja la ligne du dessous : il la garde.
        var uneVie = score with { Second = "1LC MULTI  45 600" };
        Assert.Equal("1LC MULTI  45 600", MultiplayerPanelService.AvecLEtape(new MultiplayerFinDeMission(false, 3, "ROUND 4", 3), uneVie, Defaut)?.Second);
    }

    [Fact]
    public void L_issue_de_l_hote_l_emporte_et_son_etape_aussi()
    {
        var lue = new MultiplayerFinDeMission(true, 1, "MISSION 2", 2);
        // La plateforme rend l'issue et l'etape de l'hote (site de la branche nuit0611-coop-etape).
        var fin = JsonSerializer.Deserialize<MultiplayerFin>("""{ "reason": "game_over", "stage": { "name": "MISSION 3", "cleared": 2 } }""")!;
        Assert.Equal(new MultiplayerFinDeMission(false, 1, "MISSION 3", 2), MultiplayerPanelService.Combiner(lue, fin));
        // Un site d'avant : la raison seule ; l'etape lue ici reste.
        var raisonSeule = new MultiplayerFin { Reason = "game_over" };
        Assert.Equal(new MultiplayerFinDeMission(false, 1, "MISSION 2", 2), MultiplayerPanelService.Combiner(lue, raisonSeule));
        Assert.Equal(new MultiplayerFinDeMission(false, null, null, 0), MultiplayerPanelService.Combiner(null, raisonSeule));
        // Rien de lu, une autre raison : pas d'issue de coop.
        Assert.Null(MultiplayerPanelService.Combiner(null, new MultiplayerFin { Reason = "game_closed" }));
        Assert.Equal(lue, MultiplayerPanelService.Combiner(lue, null));
    }

    [Fact]
    public void Le_meilleur_stage_d_un_joueur_se_lit_dans_la_gameroom()
    {
        var joueur = JsonSerializer.Deserialize<MultiplayerJoueur>("""{ "handle": "nelfe", "best_stage": { "name": "ROUND 3", "cleared": 2 } }""")!;
        Assert.Equal("ROUND 3", joueur.BestStage?.Name);
        Assert.Equal(2, joueur.BestStage?.Cleared);
        Assert.Null(JsonSerializer.Deserialize<MultiplayerJoueur>("""{ "handle": "nelfe" }""")!.BestStage);
    }

    // ── Le catalogue ─────────────────────────────────────────────────────

    private static MultiplayerGame Coop(MultiplayerMissionEnd? fin, string famille = "coop") => new()
    {
        Id = "arcade/mslug3", System = "arcade", Set = "mslug3", Name = "Metal Slug 3", Family = famille, Players = 2,
        Level = MultiplayerLevels.Classique, MissionEnd = fin,
    };

    [Fact]
    public void Le_catalogue_admet_une_fin_de_mission_sur_une_coop_seulement()
    {
        Assert.Null(MultiplayerCatalogueStore.ValiderLeJeu(Coop(Ms3ALaMission)));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeJeu(Coop(Ms3AuGameOver)));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeJeu(Coop(null)));
        Assert.Contains("hors d'une coop", MultiplayerCatalogueStore.ValiderLeJeu(Coop(Ms3ALaMission, "versus")));
    }

    [Theory]
    [InlineData("une place sans sortie")]
    [InlineData("rien ne la termine")]
    [InlineData("a la mission sans etape")]
    [InlineData("close_on inconnu")]
    [InlineData("memoire inconnue")]
    [InlineData("signal trop large")]
    [InlineData("noms en double")]
    public void Une_fin_de_mission_incoherente_ecarte_le_jeu(string cas)
    {
        var fin = cas switch
        {
            "une place sans sortie" => Ms3ALaMission with { Seats = [Ms3ALaMission.Seats![0]] },
            "rien ne la termine" => new MultiplayerMissionEnd { Stage = Ms3ALaMission.Stage },
            "a la mission sans etape" => new MultiplayerMissionEnd { CloseOn = "mission", Over = new() { CoreRam = "0x10", Value = 1 } },
            "close_on inconnu" => Ms3ALaMission with { CloseOn = "stage" },
            "memoire inconnue" => Ms3ALaMission with { Memory = "system" },
            "signal trop large" => Ms3ALaMission with { Over = new() { CoreRam = "0x10", Value = 300 } },
            _ => Ms3ALaMission with { Stage = Ms3ALaMission.Stage! with { Names = [new() { Value = 0, Name = "A" }, new() { Value = 0, Name = "B" }] } },
        };
        Assert.StartsWith("mission :", MultiplayerCatalogueStore.ValiderLeJeu(Coop(fin)));
    }

    [Fact]
    public void La_cle_se_lit_dans_le_catalogue_et_une_coop_sans_elle_ne_change_pas()
    {
        const string json = """
            { "id": "arcade/mslug3", "system": "arcade", "set": "mslug3", "name": "Metal Slug 3", "family": "coop", "players": 2,
              "level": "classique",
              "mission_end": { "close_on": "mission", "close_after_s": 4,
                "stage": { "core_ram": "0x40D", "transition": [8], "names": [ { "value": 0, "name": "MISSION 1" } ] },
                "seats": [ { "seat": 1, "dead": { "core_ram": "0xFDB7", "value": 3 } }, { "seat": 2, "dead": { "core_ram": "0xFDB6", "value": 3 } } ] } }
            """;
        var jeu = JsonSerializer.Deserialize<MultiplayerGame>(json)!;
        Assert.Equal(0x40D, jeu.MissionEnd?.Stage?.Decalage);
        Assert.True(jeu.MissionEnd?.ALaMission);
        Assert.Equal(4, jeu.MissionEnd?.CloseAfterS);
        Assert.Equal(0xFDB6, jeu.MissionEnd?.SortieDe(2)?.Decalage);
        Assert.Null(MultiplayerCatalogueStore.ValiderLeJeu(jeu));
        // Une coop sans la cle reste une partie libre, jamais classee ni fermee par la plateforme.
        var sans = JsonSerializer.Deserialize<MultiplayerGame>(json.Replace("\"mission_end\"", "\"autre\""))!;
        Assert.Null(sans.MissionEnd);
        Assert.False(sans.EstClasse);
    }

    [Fact]
    public void L_ordre_du_netplay_remet_les_sorties_aux_places_de_la_plateforme()
    {
        var jeu = MultiplayerOrdreNetplay.Permuter(Coop(Ms3ALaMission), new Dictionary<int, int> { [1] = 2, [2] = 1 });
        Assert.Equal(0xFDB7, jeu.MissionEnd?.SortieDe(2)?.Decalage);
        Assert.Equal(0xFDB6, jeu.MissionEnd?.SortieDe(1)?.Decalage);
    }

    // ── La lecture dans RetroArch ────────────────────────────────────────

    /// <summary>Un RetroArch de test : READ_CORE_RAM sur une memoire qu'on change pendant le guet.</summary>
    private sealed class Memoire : ICommandesRetroArch
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Octets = new();

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            var morceaux = commande.Split(' ');
            if (morceaux[0] != "READ_CORE_RAM")
            {
                return Task.FromResult<string?>(null);
            }

            var debut = long.Parse(morceaux[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var n = int.Parse(morceaux[2], CultureInfo.InvariantCulture);
            return Task.FromResult<string?>($"READ_CORE_RAM {morceaux[1]} "
                + string.Join(' ', Enumerable.Range(0, n).Select(i => Octets.GetValueOrDefault(debut + i).ToString("x2", CultureInfo.InvariantCulture))));
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task L_hote_lit_la_mission_dans_retroarch()
    {
        var ra = new Memoire();
        var fin = Ms3ALaMission with { SettleMs = 60 };
        var guet = new MultiplayerFinDeRencontre(ra, TimeSpan.FromMilliseconds(15));
        var attente = guet.AttendreLaMissionAsync(fin, [1, 2], CancellationToken.None);
        await Task.Delay(250);
        Assert.False(attente.IsCompleted);
        ra.Octets[0x40D] = 8;   // fin de la mission 1
        var issue = await attente.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(issue?.Accomplie);
        Assert.Equal("MISSION 1", issue?.NomDeLEtape);
    }

    [Fact]
    public async Task Une_partie_fermee_avant_la_fin_ne_rend_rien()
    {
        using var arret = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var guet = new MultiplayerFinDeRencontre(new Memoire(), TimeSpan.FromMilliseconds(15));
        Assert.Null(await guet.AttendreLaMissionAsync(Ms3ALaMission with { SettleMs = 60 }, [1, 2], arret.Token));
    }
}
