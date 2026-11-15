using System.Net;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// UN CONCOURS SUR SCORE CERTIFIE (LiveContest, 15/11). Un faux LiveContest sert <c>/play/contest</c> dans ses deux
/// formats : <c>certified {game, system, ruleset}</c> pour un concours certifie, <c>null</c> (ou absent, client d'avant)
/// pour un concours declare. Le premier lance le jeu du classement que la collection World Scoring a retenu, ou refuse ;
/// le second part comme avant, par son <c>gameSlug</c>.
/// </summary>
public sealed class LiveContestCertifieTests : IDisposable
{
    private const string Jeu19xx = "19xx-the-war-against-destiny";
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "livecontest-certifie-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    // ── le faux LiveContest ─────────────────────────────────────────────────

    /// <summary>Ce que LiveContest (P3Endpoints) serialise pour <c>/play/contest</c>, en camelCase.</summary>
    private static string Concours(string? certifie, bool avecLeChamp = true, string id = "k3x9p2", string system = "fbneo", string slug = "19xx")
        => "{\"contestId\":\"" + id + "\",\"title\":\"Soiree shmup\",\"gameSlug\":\"" + slug + "\",\"status\":\"open\","
           + "\"channel\":\"une-chaine\",\"objective\":{\"metric\":{\"kind\":\"score\"}},\"myStatus\":\"Confirmed\","
           + "\"login\":\"player\",\"system\":\"" + system + "\",\"brief\":null,\"startAt\":null,"
           + (avecLeChamp ? "\"certified\":" + (certifie ?? "null") + "," : "")
           + "\"serverNow\":1763164800000}";

    private static string Certifie(string game, string system, string ruleset)
        => "{\"game\":\"" + game + "\",\"system\":\"" + system + "\",\"ruleset\":\"" + ruleset + "\"}";

    private sealed class FauxLiveContest : HttpMessageHandler
    {
        public string Corps { get; set; } = "{}";
        public HttpStatusCode Statut { get; set; } = HttpStatusCode.OK;
        public List<HttpRequestMessage> Demandes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Demandes.Add(request);
            return Task.FromResult(new HttpResponseMessage(Statut) { Content = new StringContent(Corps, Encoding.UTF8, "application/json") });
        }
    }

    private static readonly LiveContestClientService.Enrollment Inscription =
        new("jeton-de-jeu", "https://livecontest.example", DateTimeOffset.UtcNow);

    private static async Task<JsonDocument?> LireAsync(FauxLiveContest faux)
    {
        using var http = new HttpClient(faux);
        var (_, doc) = await LiveContestClientService.LireLeConcoursAsync(http, Inscription, CancellationToken.None);
        return doc;
    }

    // ── la borne : index des jeux ouverts et collection ────────────────────

    private static OpenGame Ouvert(string systeme, string jeu, params string[] regles)
        => new() { SystemId = systeme, RomGroup = jeu, Rulesets = regles.Select(r => new OpenRuleset { Id = r, ProfileVersion = 1 }).ToList() };

    private string Rom(string dossier, string nom)
    {
        var chemin = Path.Combine(_racine, "roms", dossier, nom);
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, "rom");
        return chemin;
    }

    private static bool Existe(string chemin) => File.Exists(chemin);

    // ── les deux formats de /play/contest ───────────────────────────────────

    [Fact]
    public async Task Un_concours_declare_part_comme_avant_par_son_game_slug()
    {
        foreach (var corps in new[] { Concours(null), Concours(null, avecLeChamp: false) })
        {
            var faux = new FauxLiveContest { Corps = corps };
            using var doc = await LireAsync(faux);
            var resolu = false;

            var plan = LiveContestClientService.Planifier(doc!.RootElement, "RGPC-1", _ => { resolu = true; throw new InvalidOperationException(); });

            Assert.False(plan.EstCertifie);
            Assert.Equal("fbneo", plan.System);
            Assert.Equal("19xx", plan.Slug);
            Assert.Equal("k3x9p2", plan.ContestId);
            Assert.False(resolu);
            Assert.Equal("Bearer", faux.Demandes.Single().Headers.Authorization!.Scheme);
            Assert.EndsWith("/play/contest", faux.Demandes.Single().RequestUri!.AbsolutePath);
        }
    }

    [Fact]
    public async Task Un_concours_certifie_lance_le_fichier_retenu_par_la_collection_sur_la_machine_du_classement()
    {
        var retenu = Rom("fbneo", "19xx.zip");
        var faux = new FauxLiveContest { Corps = Concours(Certifie(Jeu19xx, "fbneo", "1lc")) };
        using var doc = await LireAsync(faux);
        var chemins = new Dictionary<string, string> { ["arcade/" + Jeu19xx] = retenu };

        var plan = LiveContestClientService.Planifier(doc!.RootElement, "RGPC-1", demande => LiveContestCertifie.Resoudre(
            demande, [Ouvert("arcade", Jeu19xx, "1cc", "1lc")], chemins, null, Existe));

        Assert.True(plan.EstCertifie);
        Assert.True(plan.Certifie!.Prete);
        Assert.Equal(retenu, plan.Certifie.Rom);
        Assert.Equal("arcade", plan.Certifie.Systeme); // fbneo est ramene au systeme de scoring
        Assert.Equal(Jeu19xx, plan.Certifie.Jeu);
        Assert.Equal("1lc", plan.Certifie.Regle);
        Assert.Equal("k3x9p2", plan.ContestId);
        Assert.Equal("une-chaine", plan.Channel);
    }

    [Fact]
    public async Task Un_concours_certifie_ne_retombe_jamais_sur_le_game_slug()
    {
        // le gameSlug « 19xx » existe sur la machine, mais le classement demande n'est pas ouvert
        Rom("fbneo", "19xx.zip");
        using var doc = await LireAsync(new FauxLiveContest { Corps = Concours(Certifie(Jeu19xx, "fbneo", "1cc-multi-super")) });

        var plan = LiveContestClientService.Planifier(doc!.RootElement, "RGPC-1", demande => LiveContestCertifie.Resoudre(
            demande, [Ouvert("arcade", Jeu19xx, "1cc", "1lc")], new Dictionary<string, string>(), null, Existe));

        Assert.True(plan.EstCertifie);
        Assert.False(plan.Certifie!.Prete);
        Assert.Null(plan.Certifie.Rom);
        Assert.Equal(LiveContestCertifie.RefusModeInconnu, plan.Certifie.Refus);
        Assert.Contains("1cc, 1lc", plan.Certifie.Detail);
    }

    [Fact]
    public async Task Un_concours_certifie_exige_un_compte_lie()
    {
        using var doc = await LireAsync(new FauxLiveContest { Corps = Concours(Certifie(Jeu19xx, "fbneo", "1cc")) });
        var resolu = false;

        var plan = LiveContestClientService.Planifier(doc!.RootElement, null, _ => { resolu = true; throw new InvalidOperationException(); });

        Assert.Equal(LiveContestCertifie.RefusSansCompte, plan.Certifie!.Refus);
        Assert.False(resolu);
    }

    [Fact]
    public async Task Un_jeton_expire_rend_401_sans_concours()
    {
        var faux = new FauxLiveContest { Statut = HttpStatusCode.Unauthorized, Corps = "" };
        using var http = new HttpClient(faux);

        var (statut, doc) = await LiveContestClientService.LireLeConcoursAsync(http, Inscription, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, statut);
        Assert.Null(doc);
    }

    [Fact]
    public void Un_objet_certified_sans_jeu_ou_sans_regle_est_refuse()
    {
        using var doc = JsonDocument.Parse(Concours("{\"game\":\"\",\"system\":\"fbneo\",\"ruleset\":\"1cc\"}"));
        var demande = LiveContestCertifie.Lire(doc.RootElement)!;

        var preparation = LiveContestCertifie.Resoudre(demande, [Ouvert("arcade", Jeu19xx, "1cc")], null, null, Existe);

        Assert.Equal(LiveContestCertifie.RefusIncomplet, preparation.Refus);
    }

    // ── la resolution ───────────────────────────────────────────────────────

    [Fact]
    public void Sans_machine_le_jeu_part_sur_celle_que_la_collection_a_retenue()
    {
        var snes = Rom("snes", "Jeu (USA).sfc");
        var demande = new LiveContestCertifie.Demande("jeu", "", "1cc");
        var ouverts = new[] { Ouvert("megadrive", "jeu", "1cc"), Ouvert("snes", "jeu", "1cc") };

        var preparation = LiveContestCertifie.Resoudre(demande, ouverts, new Dictionary<string, string> { ["snes/jeu"] = snes }, null, Existe);

        Assert.True(preparation.Prete);
        Assert.Equal("snes", preparation.Systeme);
        Assert.Equal(snes, preparation.Rom);
    }

    [Fact]
    public void Un_jeu_ouvert_sur_une_autre_machine_est_refuse()
    {
        var preparation = LiveContestCertifie.Resoudre(new LiveContestCertifie.Demande("jeu", "snes", "1cc"),
            [Ouvert("megadrive", "jeu", "1cc")], new Dictionary<string, string>(), null, Existe);

        Assert.Equal(LiveContestCertifie.RefusJeuFerme, preparation.Refus);
        Assert.Contains("snes", preparation.Detail);
    }

    [Fact]
    public void Une_partie_a_plusieurs_ou_un_speedrun_ne_part_pas_d_un_concours()
    {
        foreach (var regle in new[] { "1cc-multi", "1lc-multi", "speedrun-green-hill-1" })
        {
            var preparation = LiveContestCertifie.Resoudre(new LiveContestCertifie.Demande("jeu", "arcade", regle),
                [Ouvert("arcade", "jeu", "1cc", regle)], new Dictionary<string, string>(), null, Existe);

            Assert.Equal(LiveContestCertifie.RefusModeNonJouable, preparation.Refus);
        }
    }

    [Fact]
    public void Un_jeu_absent_de_la_collection_dit_pourquoi_et_ne_lance_rien()
    {
        // une ROM du jeu existe ailleurs, mais la collection ne l'a pas retenue : jamais le repli apres une passe
        var ailleurs = Rom("mame", "19xx.zip");
        var preparation = LiveContestCertifie.Resoudre(new LiveContestCertifie.Demande(Jeu19xx, "arcade", "1cc"),
            [Ouvert("arcade", Jeu19xx, "1cc")], new Dictionary<string, string>(), (_, _) => ailleurs, Existe,
            [Jeu19xx + " : ROM non reconnue (ce n'est pas la version classée)"]);

        Assert.Equal(LiveContestCertifie.RefusRomAbsente, preparation.Refus);
        Assert.Null(preparation.Rom);
        Assert.Contains("ROM non reconnue", preparation.Detail);
    }

    [Fact]
    public void Avant_la_premiere_passe_la_recherche_du_site_sert_de_repli()
    {
        var trouvee = Rom("fbneo", "19xx.zip");
        string? demande = null;

        var preparation = LiveContestCertifie.Resoudre(new LiveContestCertifie.Demande(Jeu19xx, "fbneo", "1cc"),
            [Ouvert("arcade", Jeu19xx, "1cc")], null, (systeme, jeu) => { demande = systeme + "/" + jeu; return trouvee; }, Existe);

        Assert.True(preparation.Prete);
        Assert.Equal(trouvee, preparation.Rom);
        Assert.Equal("arcade/" + Jeu19xx, demande);
    }

    [Fact]
    public void Sans_index_des_jeux_ouverts_rien_ne_part()
    {
        var preparation = LiveContestCertifie.Resoudre(new LiveContestCertifie.Demande(Jeu19xx, "arcade", "1cc"), null, null, null, Existe);

        Assert.Equal(LiveContestCertifie.RefusSansIndex, preparation.Refus);
    }

    // ── le mode a choisir dans le jeu ───────────────────────────────────────

    private const string ProfilsTgm2 =
        "{\"open\":true,\"profiles\":["
        + "{\"ruleset\":\"1cc-normal\",\"mode\":{\"value\":10,\"default\":true,\"label\":{\"fr\":\"Normal\",\"en\":\"Normal mode\"}}},"
        + "{\"ruleset\":\"1cc\",\"mode\":{\"value\":2,\"label\":{\"fr\":\"Master\",\"en\":\"Master\"}}}]}";

    [Fact]
    public void Un_mode_du_jeu_se_nomme_par_le_libelle_de_son_profil()
    {
        Assert.Equal("Normal", LiveContestCertifie.ModeAChoisir(ProfilsTgm2, "1cc-normal", francais: true));
        Assert.Equal("Normal mode", LiveContestCertifie.ModeAChoisir(ProfilsTgm2, "1cc-normal", francais: false));
        // « 1cc » de TGM2 est le classement Master : un mode a choisir, lui aussi
        Assert.Equal("Master", LiveContestCertifie.ModeAChoisir(ProfilsTgm2, "1cc", francais: true));
    }

    [Fact]
    public void Sans_profil_garde_le_mode_vient_du_nom_de_la_regle_et_un_jeu_sans_modes_n_en_a_pas()
    {
        Assert.Equal("NORMAL", LiveContestCertifie.ModeAChoisir(null, "1cc-normal", francais: true));
        Assert.Equal("POWER UP", LiveContestCertifie.ModeAChoisir(null, "1lc-power-up", francais: true));
        Assert.Null(LiveContestCertifie.ModeAChoisir(null, "1lc", francais: true));
        Assert.Null(LiveContestCertifie.ModeAChoisir("{\"open\":true,\"profiles\":[{\"ruleset\":\"1cc\"}]}", "1cc", francais: true));
    }

    // ── la metrique et la seance ────────────────────────────────────────────

    [Fact]
    public void Un_concours_certifie_ne_compte_pas_la_metrique()
    {
        Assert.False(LiveContestClientService.CompteLaMetrique(certifie: true, demarre: true));
        Assert.True(LiveContestClientService.CompteLaMetrique(certifie: false, demarre: true));
        Assert.False(LiveContestClientService.CompteLaMetrique(certifie: false, demarre: false));
    }

    [Fact]
    public void La_seance_tient_jusqu_au_verdict_du_classement_du_concours()
    {
        var session = new NelfePlayScoringSessionService();
        var seance = new SeanceDuConcoursCertifie(session);
        Assert.False(seance.Armer(null, "une-chaine", "k3x9p2"));
        Assert.Null(session.Get());

        Assert.True(seance.Armer("RGPC-1", "une-chaine", "k3x9p2"));
        var armee = session.Get()!;
        Assert.Equal(("RGPC-1", "stream", "une-chaine", "k3x9p2"), (armee.PlayerCode, armee.World, armee.Channel, armee.ContestId));

        seance.DesarmerApresLaPartie("k3x9p2", Jeu19xx, "1lc");
        // le 1CC de la meme partie arrive avant son 1LC : la seance doit encore etre la pour le brouillon du 1LC
        Assert.False(seance.SurEvenement("scoring.verdict", Jeu19xx, "1cc"));
        Assert.NotNull(session.Get());
        Assert.True(seance.SurEvenement("scoring.verdict", Jeu19xx, "1lc"));
        Assert.Null(session.Get());
        Assert.False(seance.EnAttente);
    }

    [Fact]
    public void La_seance_se_retire_au_lancement_suivant_ou_a_l_echeance()
    {
        var maintenant = new DateTime(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);
        var session = new NelfePlayScoringSessionService();
        var seance = new SeanceDuConcoursCertifie(session, () => maintenant);

        seance.Armer("RGPC-1", "une-chaine", "k3x9p2");
        seance.DesarmerApresLaPartie("k3x9p2", Jeu19xx, "1cc");
        Assert.True(seance.SurEvenement("ui.game.started", null, null));
        Assert.Null(session.Get());

        seance.Armer("RGPC-1", "une-chaine", "k3x9p2");
        seance.DesarmerApresLaPartie("k3x9p2", Jeu19xx, "1cc");
        maintenant += SeanceDuConcoursCertifie.DelaiMaximal - TimeSpan.FromSeconds(1);
        Assert.False(seance.Echeance());
        maintenant += TimeSpan.FromSeconds(1);
        Assert.True(seance.Echeance());
        Assert.Null(session.Get());
    }

    [Fact]
    public void La_seance_ne_retire_jamais_celle_d_une_salle_ni_d_un_autre_concours()
    {
        var session = new NelfePlayScoringSessionService();
        var seance = new SeanceDuConcoursCertifie(session);
        seance.Armer("RGPC-1", "une-chaine", "k3x9p2");
        seance.DesarmerApresLaPartie("k3x9p2", Jeu19xx, "1cc");

        // entre-temps, un check-in en salle
        session.Set("RGPC-2", "Salle", "Ville");
        Assert.True(seance.SurEvenement("scoring.verdict", Jeu19xx, "1cc"));
        Assert.Equal("station", session.Get()!.World);

        seance.Armer("RGPC-1", "une-chaine", "k3x9p2");
        seance.DesarmerApresLaPartie("k3x9p2", Jeu19xx, "1cc");
        session.Set("RGPC-1", null, null, "stream", "une-chaine", "autre");
        Assert.True(seance.SurEvenement("ui.game.started", null, null));
        Assert.Equal("autre", session.Get()!.ContestId);
    }
}
