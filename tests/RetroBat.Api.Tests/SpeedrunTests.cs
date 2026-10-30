using System.Text;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE SPEEDRUN, PROTOTYPE SONIC (CDC-NelfePlay-Speedruns-Sonic-Prototype-v1.0, lot S1). Le reglement
/// nelfe.challenge.v2 et l'evaluateur d'une tentative, sans borne. Les frames viennent des mesures du
/// 20/10 sur Green Hill acte 1 : timer du jeu demarre a 909, main au joueur a 934, panneau a 3754 (timer
/// fige a 0:47), fin d'acte a 3878, acte 2 a 4407. Les lignes du .MEM sont celles du Data Pack.
/// </summary>
public class SpeedrunTests
{
    /// <summary>Extrait de ram/megadrive/sonic-the-hedgehog.MEM (Data Pack 8c60b311), lignes recopiees telles quelles.</summary>
    private const string MemSonic = """
        { address=0XF008, type="u8", condition="eq", value=0X8A, action="TITLE_SCREEN", desc="Title screen" },
        { address=0XF601, type="u8", condition="eq", value=0X04, action="TITLE_SCREEN", desc="Title screen" },
        { address=0XF601, type="u8", condition="eq", value=0X08, action="DEMO_MODE", desc="Demo level" },
        { address=0XF601, type="u8", condition="eq", value=0X0C, action="GAME_PLAYING", desc="Normal level" },
        { address=0XF601, type="u8", condition="eq", value=0X10, action="GAME_PLAYING", desc="Special stage" },
        { address=0XF601, type="u8", condition="eq", value=0X18, action="GAME_OVER", desc="Ending sequence" },
        { address=0XF601, type="u8", condition="eq", value=0X88, action="DEMO_MODE", desc="Pre-level (demo)" },
        { address=0XF601, type="u8", condition="eq", value=0X8C, action="GAME_PLAYING", desc="Pre-level" },
        { address=0XF63A, type="u8", condition="eq", value=0X00, action="PAUSE_OFF", desc="No" },
        { address=0XF63A, type="u8", condition="eq", value=0X01, action="PAUSE_ON", desc="Yes" },
        { address=0XFE11, type="u8", condition="eq", value=0X00, action="LEVEL_GREEN_HILL_ZONE_FIRST_ZONE", desc="Green Hill Zone (first zone)" },
        { address=0XFE11, type="u8", condition="change", action="PROGRESSION_ZONE", desc="Current Zone" },
        { address=0XFE10, type="u8", condition="change", action="PROGRESSION_STAGE", desc="Act within Zone" },
        { address=0XFFE1, type="u8", condition="change", action="NEW_LEVEL", desc="Cheat - Level Select" },
        { address=0XFE19, type="u8", condition="change", action="CONTINUES", desc="Continues restants" },
        { address=0XFE13, type="u8", condition="decrease", action="LOSE_LIFE", desc="Lives decreased" },
        { address=0XF008, type="u8", condition="eq", value=0X8C, action="BOSS_HIT", desc="Boss" },
        { address=0XF7A6, type="u8", condition="eq", value=0X01, action="BOSS_DEFEATED", desc="Defeated" },
        { address=0XF7A6, type="u8", condition="eq", value=0X02, action="OBJECT_DESTROYED", desc="Capsule destroyed" },
        { address=0XF00A, type="u8", condition="eq", value=0XA0, action="JUMPING", no_log=true, no_survey=true, desc="Jumping" },
        { address=0XF00A, type="u8", condition="eq", value=0XC3, action="OBJECT_INTERACTION", desc="Big Ring" },
        { address=0XF7CD, type="u8", condition="eq", value=0X01, action="LEVEL_CLEAR", desc="Fin d acte : commandes bloquees pour le decompte des bonus (mesure 2026-10-20)" },
        { address=0XFE20, type="u16le", condition="increase", action="COIN_GAIN", desc="Rings possessed increased" },
        { address=0XFE56, type="u8", condition="eq", value=0X06, action="TREASURE", desc="All" },
        { address=0XFE1F, type="u8", condition="change", action="GENERAL_TIMER", timer_kind="game", desc="Time active" },
        { address=0XFE22, type="u8", condition="change", action="LEVEL_TIMER", timer_kind="game", timer_unit="minute", desc="Timer minutes" },
        { address=0XFE25, type="u8", condition="change", action="LEVEL_TIMER", timer_kind="game", timer_unit="second", desc="Timer - seconds" },
        """;

    /// <summary>Le reglement de Green Hill 1, celui du §9.2 du document du prototype (cadence reelle, D30).</summary>
    private const string GreenHill1 = """
        {
          "schema": "nelfe.challenge.v2",
          "challenge_id": "sonic-the-hedgehog.green-hill-1",
          "version": 1,
          "kind": "speedrun_level",
          "game": { "system_id": "megadrive", "rom_group": "sonic-the-hedgehog" },
          "start_policy": "normal_boot",
          "start": [
            { "value": { "address": "0xFE11", "equals": "0x00" } },
            { "value": { "address": "0xFE10", "equals": "0x00" } }
          ],
          "timing": {
            "official": "game_timer_aligned",
            "game_timer_aligned": {
              "start": { "event": { "action": "GENERAL_TIMER", "address": "0xFE1F", "to": "0x01" },
                         "when": [ { "value": { "address": "0xF601", "equals": "0x8C" } } ] },
              "finish": { "event": { "action": "GENERAL_TIMER", "address": "0xFE1F", "to": "0x00" } }
            },
            "independent": {
              "start": { "event": { "action": "GAME_PLAYING", "address": "0xF601", "value": "0x0C" } },
              "finish": { "event": { "action": "LEVEL_CLEAR", "address": "0xF7CD", "value": "0x01" } }
            },
            "game_time": [ { "address": "0xFE22", "unit": "minute" }, { "address": "0xFE25", "unit": "second" } ],
            "game_timer_period_frames": 60
          },
          "finish_confirm": {
            "event": { "action": "LEVEL_CLEAR", "address": "0xF7CD", "value": "0x01" },
            "cleared_by": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } } ]
          },
          "fail": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } }, { "event": { "action": "GAME_OVER" } } ],
          "void": ["save_state_load", "rewind", "runahead", "fast_forward", "cheats", "netplay",
                   { "event": { "action": "PAUSE_ON", "address": "0xF63A", "value": "0x01" } },
                   { "event": { "action": "NEW_LEVEL", "address": "0xFFE1" } }],
          "ranking": { "metric": "frames", "direction": "lower_better" },
          "cores": ["genesis_plus_gx"],
          "runtime": { "frame_rate": 59.922743 }
        }
        """;

    /// <summary>Green Hill en monde, hors Special Stages (D25) : trois actes dans l'ordre, la capsule confirmee par la fin d'acte.</summary>
    private const string GreenHillHorsSpecial = """
        {
          "schema": "nelfe.challenge.v2",
          "challenge_id": "sonic-the-hedgehog.green-hill-world-no-special",
          "version": 1,
          "kind": "speedrun_world",
          "start_policy": "natural_split",
          "start": [ { "value": { "address": "0xFE11", "equals": "0x00" } },
                     { "value": { "address": "0xFE10", "equals": "0x00" } } ],
          "milestones": [ { "address": "0xFE10", "sequence": ["0x00", "0x01", "0x02"] } ],
          "finish": [ { "event": { "action": "OBJECT_DESTROYED", "address": "0xF7A6", "value": "0x02" } } ],
          "finish_confirm": { "event": { "action": "LEVEL_CLEAR", "address": "0xF7CD", "value": "0x01" },
                              "cleared_by": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } } ] },
          "timing": {
            "official": "game_timer_aligned",
            "game_timer_aligned": { "start": { "event": { "address": "0xFE1F", "to": "0x01" } }, "finish": "finish" },
            "independent": { "start": { "event": { "action": "GAME_PLAYING", "address": "0xF601", "value": "0x0C" } },
                             "finish": "finish_confirm" },
            "exclude": [ { "from": { "event": { "action": "OBJECT_INTERACTION", "address": "0xF00A", "value": "0xC3" } },
                           "to": { "event": { "address": "0xF601", "leaves": "0x10" } } } ]
          },
          "fail": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } } ],
          "void": ["save_state_load", { "event": { "action": "PAUSE_ON", "address": "0xF63A", "value": "0x01" } }],
          "ranking": { "metric": "frames", "direction": "lower_better" }
        }
        """;

    /// <summary>Robotnik de Green Hill : pas de timer du jeu a l'entree de l'arene, le chrono independant fait foi.</summary>
    private const string BossGreenHill = """
        {
          "schema": "nelfe.challenge.v2",
          "challenge_id": "sonic-the-hedgehog.boss-green-hill",
          "version": 1,
          "kind": "speedrun_boss",
          "start_policy": "canonical_state",
          "start": [ { "value": { "address": "0xFE11", "equals": "0x00" } } ],
          "timing": {
            "official": "independent",
            "independent": { "start": { "event": { "action": "BOSS_HIT", "address": "0xF008", "value": "0x8C" } },
                             "finish": "finish" }
          },
          "finish": [ { "event": { "action": "BOSS_DEFEATED", "address": "0xF7A6", "value": "0x01" } } ],
          "fail": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } } ],
          "void": ["save_state_load"]
        }
        """;

    private static readonly byte[] Mem = Encoding.UTF8.GetBytes(MemSonic);

    private static Signal S(long frame, string action, string adresse, int valeur, long? horloge = null)
        => Signal.Depuis(frame, action, adresse, valeur, horloge);

    /// <summary>La partie neuve jusqu'a la main au joueur : titre, zone 0 (ligne eq emise a la premiere lecture), carton du titre, timer.</summary>
    private static Evaluateur Partie(string reglement = GreenHill1, bool releverActe = true)
    {
        var e = new Evaluateur(Reglement.Lire(reglement));
        e.Recevoir(S(1, "LEVEL_GREEN_HILL_ZONE_FIRST_ZONE", "0x00FE11", 0x00));
        e.Recevoir(S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        if (releverActe) e.Relever(0xFE10, 0);
        e.Recevoir(S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        e.Recevoir(S(909, "GENERAL_TIMER", "0x00FE1F", 1));
        e.Recevoir(S(934, "GAME_PLAYING", "0x00F601", 0x0C));
        return e;
    }

    /// <summary>
    /// De la main au joueur a la fin d'acte : le timer tourne, se fige au panneau, puis la fin d'acte confirme.
    /// Les minutes (0xFE22, ligne « change ») restent a 0 sur l'acte : jamais emises, elles se relevent a
    /// l'arrivee provisoire, timer fige.
    /// </summary>
    private static void FinirGreenHill1(Evaluateur e)
    {
        for (var s = 1; s <= 47; s++) e.Recevoir(S(909 + 60 * s, "LEVEL_TIMER", "0x00FE25", s));
        e.Recevoir(S(3754, "GENERAL_TIMER", "0x00FE1F", 0));
        if (e.AdressesInconnues.Contains(0xFE22)) e.Relever(0xFE22, 0);
        e.Recevoir(S(3878, "LEVEL_CLEAR", "0x00F7CD", 1));
    }

    [Fact]
    public void A_l_arrivee_provisoire_les_minutes_jamais_emises_sont_a_relever()
    {
        var e = Partie();
        for (var s = 1; s <= 47; s++) e.Recevoir(S(909 + 60 * s, "LEVEL_TIMER", "0x00FE25", s));
        e.Recevoir(S(3754, "GENERAL_TIMER", "0x00FE1F", 0));
        Assert.Equal(EtatTentative.ArriveeProvisoire, e.Etat);
        Assert.Equal(new[] { 0xFE22L }, e.AdressesInconnues);
    }

    [Fact]
    public void Le_reglement_de_Green_Hill_1_ne_cite_que_des_lignes_du_MEM()
    {
        Assert.Empty(Reglement.Lire(GreenHill1).VerifierContre(Mem));
        Assert.Empty(Reglement.Lire(GreenHillHorsSpecial).VerifierContre(Mem));
        Assert.Empty(Reglement.Lire(BossGreenHill).VerifierContre(Mem));
    }

    [Fact]
    public void Une_ligne_absente_du_MEM_rend_le_reglement_invalide()
    {
        var json = GreenHill1.Replace("\"0xF7CD\"", "\"0xF7CE\"");
        var ecarts = Reglement.Lire(json).VerifierContre(Mem);
        Assert.Contains(ecarts, x => x.Contains("0xF7CE") && x.Contains("aucune ligne"));
    }

    [Fact]
    public void Une_ligne_no_survey_ne_parle_jamais_et_ne_peut_pas_etre_citee()
    {
        var json = GreenHill1.Replace(
            "{ \"event\": { \"action\": \"NEW_LEVEL\", \"address\": \"0xFFE1\" } }",
            "{ \"event\": { \"action\": \"JUMPING\", \"address\": \"0xF00A\", \"value\": \"0xA0\" } }");
        var ecarts = Reglement.Lire(json).VerifierContre(Mem);
        Assert.Contains(ecarts, x => x.Contains("no_survey"));
    }

    [Fact]
    public void Une_valeur_qu_aucune_ligne_eq_n_emet_est_refusee()
    {
        // 0xF601 n'a que des lignes « eq » : 0x0D n'est jamais emis.
        var json = GreenHill1.Replace("\"action\": \"GAME_PLAYING\", \"address\": \"0xF601\", \"value\": \"0x0C\"",
            "\"action\": \"GAME_PLAYING\", \"address\": \"0xF601\", \"value\": \"0x0D\"");
        Assert.Contains(Reglement.Lire(json).VerifierContre(Mem), x => x.Contains("0xD"));
    }

    [Fact]
    public void L_empreinte_du_MEM_fige_fait_partie_du_reglement()
    {
        var json = GreenHill1.Replace("\"start_policy\"", "\"mem_sha256\": \"00bf82c7b8783299fde03272c77541049f18e8cc4dd0a54ae5d6fe7e5dcdebe6\",\n  \"start_policy\"");
        Assert.Contains(Reglement.Lire(json).VerifierContre(Mem), x => x.StartsWith("empreinte du .MEM"));
    }

    [Fact]
    public void Green_Hill_1_mesure_les_deux_chronos_du_20_octobre()
    {
        var e = Partie();
        Assert.Equal(EtatTentative.EnCourse, e.Etat);
        FinirGreenHill1(e);

        var r = e.Resultat();
        Assert.Equal(EtatTentative.Finie, r.Etat);
        Assert.Equal(new MesureChrono(909, 3754, 2845, 0), r.Cale);
        Assert.Equal(new MesureChrono(934, 3878, 2944, 0), r.Independant);
        Assert.Equal(2845, r.FramesOfficielles);
        Assert.Equal(47, r.TempsDuJeuSecondes);
        Assert.Equal("ok", r.ControleTempsDuJeu);
    }

    [Fact]
    public void Le_temps_s_affiche_a_la_cadence_reelle_de_la_Mega_Drive()
    {
        Assert.Equal("0:47.48", Chronometrage.Afficher(2845, 59.922743));
        Assert.Equal("0:49.13", Chronometrage.Afficher(2944, 59.922743));
        Assert.Equal("1:00.00", Chronometrage.Afficher(3600, 60));
    }

    [Fact]
    public void Sans_releve_de_l_acte_la_tentative_n_arme_pas()
    {
        // 0xFE10 n'a qu'une ligne « change » : rien a la premiere lecture, l'acte reste inconnu, jamais 0.
        var e = Partie(releverActe: false);
        Assert.Equal(EtatTentative.Preparee, e.Etat);
        Assert.Equal(new[] { 0xFE10L }, e.AdressesInconnues);
    }

    [Fact]
    public void La_demo_ne_demarre_aucune_tentative()
    {
        var e = new Evaluateur(Reglement.Lire(GreenHill1));
        e.Recevoir(S(1, "LEVEL_GREEN_HILL_ZONE_FIRST_ZONE", "0x00FE11", 0x00));
        e.Relever(0xFE10, 0);
        e.Recevoir(S(700, "DEMO_MODE", "0x00F601", 0x88));
        e.Recevoir(S(705, "GENERAL_TIMER", "0x00FE1F", 1));
        e.Recevoir(S(730, "DEMO_MODE", "0x00F601", 0x08));
        Assert.Equal(EtatTentative.Armee, e.Etat);
        e.Recevoir(S(1200, "LEVEL_CLEAR", "0x00F7CD", 1));
        Assert.NotEqual(EtatTentative.Finie, e.Etat);
        Assert.Null(e.Resultat().Cale!.Depart);
    }

    [Fact]
    public void Une_mort_entre_le_panneau_et_la_fin_d_acte_rate_la_tentative()
    {
        var e = Partie();
        e.Recevoir(S(3754, "GENERAL_TIMER", "0x00FE1F", 0));
        Assert.Equal(EtatTentative.ArriveeProvisoire, e.Etat);
        e.Recevoir(S(3800, "LOSE_LIFE", "0x00FE13", 2));
        e.Recevoir(S(3878, "LEVEL_CLEAR", "0x00F7CD", 1));
        var r = e.Resultat();
        Assert.Equal(EtatTentative.Ratee, r.Etat);
        Assert.Equal("LOSE_LIFE", r.Cause);
        Assert.Null(r.FramesOfficielles);
    }

    [Fact]
    public void Une_fin_d_acte_vue_avant_le_depart_ne_compte_pas()
    {
        var e = new Evaluateur(Reglement.Lire(GreenHill1));
        e.Recevoir(S(1, "LEVEL_GREEN_HILL_ZONE_FIRST_ZONE", "0x00FE11", 0x00));
        e.Relever(0xFE10, 0);
        e.Recevoir(S(300, "LEVEL_CLEAR", "0x00F7CD", 1));
        Assert.Equal(EtatTentative.Armee, e.Etat);
        e.Recevoir(S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        e.Recevoir(S(909, "GENERAL_TIMER", "0x00FE1F", 1));
        e.Recevoir(S(934, "GAME_PLAYING", "0x00F601", 0x0C));
        FinirGreenHill1(e);
        Assert.Equal(2845, e.Resultat().FramesOfficielles);
    }

    [Fact]
    public void La_pause_du_jeu_annule_la_tentative()
    {
        var e = Partie();
        e.Recevoir(S(2000, "PAUSE_ON", "0x00F63A", 1));
        Assert.Equal(EtatTentative.Annulee, e.Etat);
        Assert.Equal("PAUSE_ON", e.Cause);
    }

    [Fact]
    public void Une_pause_de_RetroArch_se_lit_dans_l_ecart_entre_horloge_et_frames()
    {
        var e = Partie();
        // 60 frames en une seconde : normal.
        e.Recevoir(S(1000, "LEVEL_TIMER", "0x00FE25", 1, horloge: 10_000));
        e.Recevoir(S(1060, "LEVEL_TIMER", "0x00FE25", 2, horloge: 11_001));
        Assert.Equal(EtatTentative.EnCourse, e.Etat);
        // 60 frames en six secondes : le menu de RetroArch est reste ouvert.
        e.Recevoir(S(1120, "LEVEL_TIMER", "0x00FE25", 3, horloge: 17_000));
        Assert.Equal(EtatTentative.Annulee, e.Etat);
        Assert.Equal("pause", e.Cause);
    }

    [Fact]
    public void Un_chargement_d_etat_annule_meme_avant_le_depart()
    {
        var e = new Evaluateur(Reglement.Lire(GreenHill1));
        e.Recevoir(S(1, "LEVEL_GREEN_HILL_ZONE_FIRST_ZONE", "0x00FE11", 0x00));
        e.Controle("save_state_load");
        Assert.Equal(EtatTentative.Annulee, e.Etat);
        // Plus rien ne la ranime.
        e.Relever(0xFE10, 0);
        e.Recevoir(S(909, "GENERAL_TIMER", "0x00FE1F", 1));
        Assert.Equal(EtatTentative.Annulee, e.Etat);
    }

    [Fact]
    public void Le_level_select_annule_la_tentative()
    {
        var e = Partie();
        e.Recevoir(S(1500, "NEW_LEVEL", "0x00FFE1", 1));
        Assert.Equal(EtatTentative.Annulee, e.Etat);
    }

    [Fact]
    public void Une_ligne_citee_coupee_par_le_wrapper_annule_la_tentative()
    {
        var e = Partie();
        e.CapteurCoupe(0xF00A); // pas citee par Green Hill 1
        Assert.Equal(EtatTentative.EnCourse, e.Etat);
        e.CapteurCoupe(0xFE1F);
        Assert.Equal(EtatTentative.Annulee, e.Etat);
        Assert.Equal("sensor_muted", e.Cause);
    }

    [Fact]
    public void Fermer_le_jeu_ou_abandonner_clot_sans_temps()
    {
        var e = Partie();
        e.Fermer();
        Assert.Equal(EtatTentative.Abandonnee, e.Etat);
        Assert.Equal("game_closed", e.Cause);
        Assert.Null(e.Resultat().FramesOfficielles);
    }

    /// <summary>Les trois actes de Green Hill, un Special Stage pris entre l'acte 1 et l'acte 2 (frames 4000 a 6000).</summary>
    private static Evaluateur MondeGreenHill(string reglement, bool sauterLActe2 = false)
    {
        var e = Partie(reglement);
        e.Recevoir(S(3754, "GENERAL_TIMER", "0x00FE1F", 0));
        e.Recevoir(S(3878, "LEVEL_CLEAR", "0x00F7CD", 1));
        e.Recevoir(S(4000, "OBJECT_INTERACTION", "0x00F00A", 0xC3));
        e.Recevoir(S(4100, "GAME_PLAYING", "0x00F601", 0x10));
        e.Recevoir(S(6000, "GAME_PLAYING", "0x00F601", 0x8C));
        if (!sauterLActe2)
        {
            e.Recevoir(S(6010, "PROGRESSION_STAGE", "0x00FE10", 1));
            e.Recevoir(S(6020, "GENERAL_TIMER", "0x00FE1F", 1));
            e.Recevoir(S(8000, "GENERAL_TIMER", "0x00FE1F", 0));
            e.Recevoir(S(8124, "LEVEL_CLEAR", "0x00F7CD", 1));
        }

        e.Recevoir(S(8600, "PROGRESSION_STAGE", "0x00FE10", 2));
        e.Recevoir(S(8610, "GENERAL_TIMER", "0x00FE1F", 1));
        e.Recevoir(S(11000, "OBJECT_DESTROYED", "0x00F7A6", 2));
        e.Recevoir(S(11040, "GENERAL_TIMER", "0x00FE1F", 0));
        e.Recevoir(S(11300, "LEVEL_CLEAR", "0x00F7CD", 1));
        return e;
    }

    [Fact]
    public void Le_monde_hors_Special_Stages_retire_la_plage_du_Big_Ring_au_retour_au_niveau()
    {
        var hors = MondeGreenHill(GreenHillHorsSpecial).Resultat();
        Assert.Equal(EtatTentative.Finie, hors.Etat);
        Assert.Equal(new[] { (4000L, 6000L) }, hors.PlagesExclues);
        Assert.Equal(new MesureChrono(909, 11000, 11000 - 909 - 2000, 2000), hors.Cale);
        Assert.Equal(new MesureChrono(934, 11300, 11300 - 934 - 2000, 2000), hors.Independant);

        // Special Stages compris : le meme reglement sans « exclude ».
        var compris = GreenHillHorsSpecial.Replace("\"exclude\"", "\"exclude_inactive\"");
        Assert.Equal(11000 - 909, MondeGreenHill(compris).Resultat().FramesOfficielles);
    }

    [Fact]
    public void Un_acte_saute_rate_le_monde()
    {
        var r = MondeGreenHill(GreenHillHorsSpecial, sauterLActe2: true).Resultat();
        Assert.Equal(EtatTentative.Ratee, r.Etat);
        Assert.Equal("milestone_out_of_order", r.Cause);
    }

    [Fact]
    public void Le_boss_se_chronometre_au_chrono_independant()
    {
        var e = new Evaluateur(Reglement.Lire(BossGreenHill));
        e.Recevoir(S(1, "LEVEL_GREEN_HILL_ZONE_FIRST_ZONE", "0x00FE11", 0x00));
        e.Recevoir(S(400, "BOSS_HIT", "0x00F008", 0x8C));
        e.Recevoir(S(1650, "BOSS_DEFEATED", "0x00F7A6", 1));
        var r = e.Resultat();
        Assert.Equal(EtatTentative.Finie, r.Etat);
        Assert.Equal(Reglement.ChronoIndependant, r.ChronoOfficiel);
        Assert.Null(r.Cale);
        Assert.Equal(1250, r.FramesOfficielles);
    }

    [Fact]
    public void Un_compteur_au_dela_de_son_maximum_rate_la_tentative()
    {
        // « Sans anneau » : le premier anneau ramasse rate la tentative.
        var json = GreenHill1.Replace("\"ranking\"",
            "\"counters\": [ { \"id\": \"rings\", \"event\": { \"action\": \"COIN_GAIN\", \"address\": \"0xFE20\" }, \"max\": 0 } ],\n  \"ranking\"");
        Assert.Empty(Reglement.Lire(json).VerifierContre(Mem));
        var e = Partie(json);
        e.Recevoir(S(1100, "COIN_GAIN", "0x00FE20", 1));
        Assert.Equal(EtatTentative.Ratee, e.Etat);
        Assert.Equal("rings", e.Cause);
    }

    [Fact]
    public void Une_valeur_exigee_a_l_arrivee_doit_etre_connue_et_juste()
    {
        // « Bonne fin » reduite a l'acte : six emeraudes exigees a l'arrivee.
        var json = GreenHill1.Replace("\"ranking\"",
            "\"finish_values\": [ { \"id\": \"emeralds\", \"address\": \"0xFE56\", \"equals\": 6 } ],\n  \"ranking\"");

        var inconnue = Partie(json);
        FinirGreenHill1(inconnue);
        Assert.Equal(EtatTentative.Annulee, inconnue.Etat);
        Assert.Equal("emeralds_unknown", inconnue.Cause);

        var juste = Partie(json);
        juste.Relever(0xFE56, 6);
        FinirGreenHill1(juste);
        Assert.Equal(EtatTentative.Finie, juste.Etat);

        var fausse = Partie(json);
        fausse.Relever(0xFE56, 5);
        FinirGreenHill1(fausse);
        Assert.Equal(EtatTentative.Ratee, fausse.Etat);
    }

    [Fact]
    public void Le_rang_suit_le_sens_et_les_criteres_du_reglement()
    {
        var json = GreenHill1.Replace("\"ranking\": { \"metric\": \"frames\", \"direction\": \"lower_better\" }",
            "\"counters\": [ { \"id\": \"rings\", \"event\": { \"action\": \"COIN_GAIN\", \"address\": \"0xFE20\" } } ],\n"
            + "  \"ranking\": { \"metric\": \"rings\", \"direction\": \"lower_better\", \"then\": [ { \"metric\": \"frames\" } ] }");
        var reglement = Reglement.Lire(json);
        var e = Partie(json);
        e.Recevoir(S(1100, "COIN_GAIN", "0x00FE20", 1));
        e.Recevoir(S(1200, "COIN_GAIN", "0x00FE20", 2));
        FinirGreenHill1(e);
        Assert.Equal(new (long?, bool)[] { (2, true), (2845, true) }, e.Resultat().ValeursDeRang(reglement));
    }

    [Fact]
    public void Un_reglement_mal_forme_est_refuse_a_la_lecture()
    {
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire("{ \"schema\": \"autre\" }"));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(GreenHill1.Replace("\"official\": \"game_timer_aligned\"", "\"official\": \"rta\"")));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(BossGreenHill.Replace("\"official\": \"independent\"", "\"official\": \"game_timer_aligned\"")));
    }
}
