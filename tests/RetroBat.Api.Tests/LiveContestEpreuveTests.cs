using System.Net;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// UN CONCOURS SUR UNE EPREUVE (LiveContest, 15/11). Un faux LiveContest sert <c>/play/contest</c> avec
/// <c>certified { challenge, game, system, ruleset }</c> : l'epreuve de l'index des defis de la borne, ou son refus (phase
/// <c>certified-*</c>). Sans <c>challenge</c> (null, absent : LiveContest d'avant), le concours sur score de la 1.9.187 et le
/// concours declare partent comme avant.
/// </summary>
public sealed class LiveContestEpreuveTests
{
    private const string JeuMm2 = "mega-man-2";
    private const string EpreuveAirMan = "mega-man-2.air-man";
    private const string RegleAirMan = "ch-mm2-air-man-v1";
    private const string RomMm2 = @"E:\RetroBat\roms\nes\Mega Man 2 (USA).nes";

    // ── le faux LiveContest ─────────────────────────────────────────────────

    private static string Concours(string? certifie, string id = "k3x9p2")
        => "{\"contestId\":" + (id.Length == 0 ? "null" : "\"" + id + "\"") + ",\"title\":\"Soiree Mega Man\",\"gameSlug\":\"mega-man-2\","
           + "\"status\":\"open\",\"channel\":\"une-chaine\",\"objective\":{\"metric\":{\"kind\":\"score\"}},\"myStatus\":\"Confirmed\","
           + "\"login\":\"player\",\"system\":\"nes\",\"brief\":null,\"startAt\":null,\"certified\":" + (certifie ?? "null") + ","
           + "\"serverNow\":1763164800000}";

    private static string Certifie(string? challenge, string game = JeuMm2, string system = "nes", string ruleset = RegleAirMan)
        => "{\"game\":\"" + game + "\",\"system\":\"" + system + "\",\"ruleset\":\"" + ruleset + "\""
           + (challenge is null ? "" : ",\"challenge\":\"" + challenge + "\"") + "}";

    private sealed class FauxLiveContest(string corps) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corps, Encoding.UTF8, "application/json") });
    }

    private static async Task<JsonDocument> LireAsync(string corps)
    {
        using var http = new HttpClient(new FauxLiveContest(corps));
        var (_, doc) = await LiveContestClientService.LireLeConcoursAsync(http,
            new LiveContestClientService.Enrollment("jeton", "https://livecontest.example", DateTimeOffset.UtcNow), CancellationToken.None);
        return doc!;
    }

    // ── l'index des defis de la borne ───────────────────────────────────────

    /// <summary>Une epreuve minimale, a la forme de celles du Data Pack (depart normal).</summary>
    private static Reglement Epreuve(string id = EpreuveAirMan, string regle = RegleAirMan, string kind = "speedrun_level",
        string jeu = JeuMm2, string systeme = "nes")
        => Reglement.Lire($$"""
        {
          "schema": "nelfe.challenge.v2", "challenge_id": "{{id}}", "version": 1, "ruleset": "{{regle}}",
          "kind": "{{kind}}",
          "title": { "fr": "Air Man", "en": "Air Man" },
          "game": { "system_id": "{{systeme}}", "rom_group": "{{jeu}}" },
          "mem_sha256": "{{new string('b', 64)}}",
          "start_policy": "normal_boot",
          "timing": { "official": "independent", "independent": {
            "start": { "event": { "action": "PROGRESSION_STAGE", "address": "0x2A", "value": "0x01" } },
            "finish": { "event": { "action": "PROGRESSION_STAGE", "address": "0x2A", "value": "0x02" } } } },
          "void": [],
          "ranking": { "direction": "lower_better", "metric": "frames" },
          "cores": [ "fceumm" ],
          "runtime": { "frame_rate": 60.098814 }
        }
        """);

    private static Func<LiveContestCertifie.Demande, LiveContestEpreuve.Preparation> Index(params Reglement[] ouvertes)
        => demande => LiveContestEpreuve.Resoudre(demande, ouvertes, _ => RomMm2);

    // ── les formats de /play/contest ────────────────────────────────────────

    [Fact]
    public async Task Un_concours_sur_epreuve_lance_l_epreuve_de_l_index_avec_son_jeu_et_sa_regle()
    {
        using var doc = await LireAsync(Concours(Certifie(EpreuveAirMan)));
        var scoreResolu = false;

        var plan = LiveContestClientService.Planifier(doc.RootElement, "RGPC-1",
            _ => { scoreResolu = true; throw new InvalidOperationException(); }, Index(Epreuve()));

        Assert.False(scoreResolu);
        Assert.True(plan.EstCertifie);
        Assert.True(plan.EstUneEpreuve);
        Assert.Null(plan.Certifie);
        Assert.True(plan.Epreuve!.Prete);
        Assert.Equal(EpreuveAirMan, plan.Epreuve.Epreuve);
        Assert.Equal(RegleAirMan, plan.Epreuve.Regle);
        Assert.Equal(JeuMm2, plan.Epreuve.Jeu);
        Assert.Equal("nes", plan.Epreuve.Systeme);
        Assert.Equal(RomMm2, plan.Epreuve.Rom);
        Assert.Equal("k3x9p2", plan.ContestId);
        Assert.Equal("une-chaine", plan.Channel);
    }

    [Fact]
    public async Task Sans_challenge_le_concours_sur_score_et_le_concours_declare_partent_comme_avant()
    {
        // concours sur score de la 1.9.187 : `challenge` absent ou null
        foreach (var certifie in new[] { Certifie(null, ruleset: "1cc"), "{\"game\":\"mega-man-2\",\"system\":\"nes\",\"ruleset\":\"1cc\",\"challenge\":null}" })
        {
            using var doc = await LireAsync(Concours(certifie));
            var epreuveResolue = false;
            var plan = LiveContestClientService.Planifier(doc.RootElement, "RGPC-1",
                d => new LiveContestCertifie.Preparation("rom", "nes", d.Jeu, d.Regle, null, null),
                _ => { epreuveResolue = true; throw new InvalidOperationException(); });

            Assert.False(epreuveResolue);
            Assert.False(plan.EstUneEpreuve);
            Assert.True(plan.EstCertifie);
            Assert.Equal("1cc", plan.Certifie!.Regle);
        }

        // concours declare
        using var declare = await LireAsync(Concours(null));
        var planDeclare = LiveContestClientService.Planifier(declare.RootElement, "RGPC-1",
            _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        Assert.False(planDeclare.EstCertifie);
        Assert.Equal("mega-man-2", planDeclare.Slug);
    }

    [Fact]
    public async Task Une_epreuve_exige_un_compte_lie_et_un_concours_identifie()
    {
        using var doc = await LireAsync(Concours(Certifie(EpreuveAirMan)));
        var resolue = false;
        var sansCompte = LiveContestClientService.Planifier(doc.RootElement, null, _ => throw new InvalidOperationException(),
            _ => { resolue = true; throw new InvalidOperationException(); });
        Assert.False(resolue);
        Assert.False(sansCompte.Epreuve!.Prete);
        Assert.Equal(LiveContestCertifie.RefusSansCompte, sansCompte.Epreuve.Refus);

        using var sansId = await LireAsync(Concours(Certifie(EpreuveAirMan), id: ""));
        var plan = LiveContestClientService.Planifier(sansId.RootElement, "RGPC-1", _ => throw new InvalidOperationException(), Index(Epreuve()));
        Assert.Equal(LiveContestCertifie.RefusIncomplet, plan.Epreuve!.Refus);
    }

    // ── la resolution sur la borne ──────────────────────────────────────────

    [Fact]
    public void L_epreuve_se_trouve_par_son_identifiant_ou_par_son_ruleset()
    {
        var parRuleset = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, RegleAirMan),
            [Epreuve("mega-man-2.heat-man", "ch-mm2-heat-man-v1"), Epreuve()], _ => RomMm2);
        Assert.True(parRuleset.Prete);
        Assert.Equal(EpreuveAirMan, parRuleset.Epreuve);

        // jeu et machine du concours facultatifs ; fbneo et arcade sont la meme machine de scoring
        var seule = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande("", "", "", EpreuveAirMan), [Epreuve()], _ => RomMm2);
        Assert.True(seule.Prete);
        var arcade = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande("metal-slug-3", "fbneo", "ch-ms3-m1-v1", "metal-slug-3.m1"),
            [Epreuve("metal-slug-3.m1", "ch-ms3-m1-v1", "challenge", "metal-slug-3", "arcade")], _ => "mslug3.zip");
        Assert.True(arcade.Prete);
        Assert.Equal("arcade", arcade.Systeme);
    }

    [Fact]
    public void Une_epreuve_absente_de_l_index_de_la_borne_est_refusee()
    {
        var demande = new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, EpreuveAirMan);

        var autre = LiveContestEpreuve.Resoudre(demande, [Epreuve("mega-man-2.heat-man", "ch-mm2-heat-man-v1")], _ => RomMm2);
        Assert.False(autre.Prete);
        Assert.Equal(LiveContestEpreuve.RefusEpreuveFermee, autre.Refus);
        Assert.Contains(EpreuveAirMan, autre.Detail);

        var vide = LiveContestEpreuve.Resoudre(demande, [], _ => RomMm2);
        Assert.Equal(LiveContestEpreuve.RefusEpreuveFermee, vide.Refus);
        Assert.Contains("index des defis", vide.Detail);

        var incomplet = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan), [Epreuve()], _ => RomMm2);
        Assert.Equal(LiveContestCertifie.RefusIncomplet, incomplet.Refus);
    }

    [Fact]
    public void Une_autre_version_ouverte_sur_la_borne_est_refusee_son_temps_n_entrerait_pas_au_classement_du_concours()
    {
        var r = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", "ch-mm2-air-man-v1", EpreuveAirMan),
            [Epreuve(regle: "ch-mm2-air-man-v2")], _ => RomMm2);

        Assert.False(r.Prete);
        Assert.Equal(LiveContestEpreuve.RefusEpreuveFermee, r.Refus);
        Assert.Contains("ch-mm2-air-man-v2", r.Detail);
    }

    [Fact]
    public void Un_entrainement_n_est_jamais_classe_et_se_refuse()
    {
        var r = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, EpreuveAirMan),
            [Epreuve(kind: Reglement.KindEntrainement)], _ => RomMm2);

        Assert.False(r.Prete);
        Assert.Equal(LiveContestEpreuve.RefusEntrainement, r.Refus);
        Assert.Null(r.Rom);
    }

    [Fact]
    public void Un_jeu_ou_une_machine_qui_ne_sont_pas_ceux_de_l_epreuve_se_refusent()
    {
        var jeu = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande("mega-man-3", "nes", RegleAirMan, EpreuveAirMan), [Epreuve()], _ => RomMm2);
        Assert.Equal(LiveContestEpreuve.RefusAutreJeu, jeu.Refus);

        var machine = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "snes", RegleAirMan, EpreuveAirMan), [Epreuve()], _ => RomMm2);
        Assert.Equal(LiveContestEpreuve.RefusAutreJeu, machine.Refus);
    }

    [Fact]
    public void Sans_rom_admise_pour_ses_epreuves_rien_ne_part()
    {
        var r = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, EpreuveAirMan), [Epreuve()], _ => null);

        Assert.False(r.Prete);
        Assert.Equal(LiveContestCertifie.RefusRomAbsente, r.Refus);
        Assert.Contains("collection World Scoring", r.Detail);
    }

    [Theory]
    [InlineData("game_running", LiveContestEpreuve.RefusJeuEnCours)]
    [InlineData("not_installed", LiveContestCertifie.RefusRomAbsente)]
    [InlineData("unknown_challenge", LiveContestEpreuve.RefusEpreuveFermee)]
    [InlineData("mem_mismatch", LiveContestEpreuve.RefusPasPrete)]
    [InlineData("state_missing", LiveContestEpreuve.RefusPasPrete)]
    [InlineData("launcher_failed", LiveContestEpreuve.RefusPasPrete)]
    [InlineData(null, LiveContestEpreuve.RefusPasPrete)]
    public void Un_refus_du_lancement_type_devient_une_phase_certified(string? raison, string phase)
        => Assert.Equal(phase, LiveContestEpreuve.PhaseDuLancement(raison));

    [Fact]
    public void Toutes_les_phases_de_refus_sont_des_phases_certified()
    {
        foreach (var phase in new[] { LiveContestEpreuve.RefusEpreuveFermee, LiveContestEpreuve.RefusEntrainement,
                     LiveContestEpreuve.RefusAutreJeu, LiveContestEpreuve.RefusPasPrete, LiveContestEpreuve.RefusJeuEnCours })
        {
            Assert.StartsWith("certified-", phase);
        }
    }

    // ── le statut et la seance ──────────────────────────────────────────────

    [Fact]
    public void Le_statut_dit_l_epreuve_et_null_pour_un_concours_sur_score()
    {
        var epreuve = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, EpreuveAirMan), [Epreuve()], _ => RomMm2);
        var json = JsonSerializer.Serialize(LiveContestClientService.CertifiePourLeStatut(null, epreuve));
        Assert.Equal("{\"game\":\"mega-man-2\",\"system\":\"nes\",\"ruleset\":\"ch-mm2-air-man-v1\",\"challenge\":\"mega-man-2.air-man\"}", json);

        var score = new LiveContestCertifie.Preparation("rom", "arcade", "19xx-the-war-against-destiny", "1cc", null, null);
        Assert.Equal("{\"game\":\"19xx-the-war-against-destiny\",\"system\":\"arcade\",\"ruleset\":\"1cc\",\"challenge\":null}",
            JsonSerializer.Serialize(LiveContestClientService.CertifiePourLeStatut(score, null)));
        Assert.Null(LiveContestClientService.CertifiePourLeStatut(null, null));
    }

    [Fact]
    public void La_seance_d_une_epreuve_tient_jusqu_au_verdict_de_son_ruleset()
    {
        var epreuve = LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, EpreuveAirMan), [Epreuve()], _ => RomMm2);
        var classement = LiveContestClientService.ClassementDuConcours(null, epreuve);
        Assert.Equal((JeuMm2, RegleAirMan), classement);
        Assert.Null(LiveContestClientService.ClassementDuConcours(null,
            LiveContestEpreuve.Resoudre(new LiveContestCertifie.Demande(JeuMm2, "nes", RegleAirMan, EpreuveAirMan), [Epreuve()], _ => null)));

        var session = new NelfePlayScoringSessionService();
        var seance = new SeanceDuConcoursCertifie(session);
        Assert.True(seance.Armer("RGPC-1", "une-chaine", "k3x9p2"));
        seance.DesarmerApresLaPartie("k3x9p2", classement!.Value.Jeu, classement.Value.Regle);

        // un score du meme jeu (une autre partie) n'est pas le verdict de l'epreuve
        Assert.False(seance.SurEvenement("scoring.verdict", JeuMm2, "1cc"));
        Assert.NotNull(session.Get());
        Assert.True(seance.SurEvenement("scoring.verdict", JeuMm2, RegleAirMan));
        Assert.Null(session.Get());
    }

    [Fact]
    public void Le_client_ne_compte_jamais_la_metrique_d_une_epreuve()
        => Assert.False(LiveContestClientService.CompteLaMetrique(certifie: true, demarre: true));
}
