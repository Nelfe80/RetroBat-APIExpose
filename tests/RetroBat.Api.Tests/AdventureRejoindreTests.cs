using System.Text;
using RetroBat.Api.Adventure;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 28/11 : rejoindre une aventure a deux bornes. L'ami invite dans la salle devient participant de l'aventure (sinon le site refuse
/// l'activation), la liste dit le createur et les joueurs par pseudo, une version qu'on n'a pas est grisee avec sa raison, X dit pourquoi
/// il ne regle pas.
/// </summary>
public sealed class AdventureRejoindreTests
{
    private sealed class Bouteille(Func<HttpRequestMessage, (int Statut, string Corps)> reponse) : HttpMessageHandler
    {
        public List<string> Appels { get; } = [];
        public List<string> Corps { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
        {
            Appels.Add(requete.Method + " " + requete.RequestUri!.AbsolutePath.Replace("/api/v1/agent/adventure/", "", StringComparison.Ordinal));
            Corps.Add(requete.Content is null ? string.Empty : await requete.Content.ReadAsStringAsync(ct));
            var (statut, corps) = reponse(requete);
            return new HttpResponseMessage((System.Net.HttpStatusCode)statut) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
        }
    }

    private static AdventureSiteClient Client(Bouteille b) => new(() => new HttpClient(b) { BaseAddress = new Uri("https://nelfeplay.test") });

    private static string Texte(string cle, string defaut) => defaut;

    private const string Liste = """
        {"ok":true,"adventures":[{"id":"a1","game":"gba/kirby","title":"Kirby #1","places":4,"state":"active","creator":"4fe5aa048e007ad0","creator_pseudo":"RetroLife",
         "participants":["4fe5aa048e007ad0","a1b2c3d4e5f60718"],"participant_pseudos":["RetroLife","Nelfe80"],"last_played_at":"2026-11-27T16:43:00Z"}],"invitations":[]}
        """;

    [Fact]
    public async Task La_liste_du_site_garde_le_pseudo_du_createur_et_des_joueurs_a_cote_des_poignees()
    {
        var mine = await Client(new Bouteille(_ => (200, Liste))).MesAventuresAsync(CancellationToken.None);
        var a = Assert.Single(mine!.Aventures);
        Assert.Equal("4fe5aa048e007ad0", a.Creator);
        Assert.Equal("RetroLife", a.CreatorPseudo);
        Assert.Equal(["RetroLife", "Nelfe80"], a.NomsDesJoueurs.ToArray());
        // Les poignees restent lues telles quelles (clefs), jamais montrees quand un pseudo existe.
        Assert.Equal(["4fe5aa048e007ad0", "a1b2c3d4e5f60718"], a.Pseudos.ToArray());
    }

    [Fact]
    public async Task Le_detail_donne_le_pseudo_du_createur_et_les_pseudos_des_membres()
    {
        var detail = """{"ok":true,"adventure":{"id":"a1","game":"gba/kirby","content":"md5usa","members":[{"handle":"4fe5aa048e007ad0","pseudo":"RetroLife","role":"creator"},{"handle":"zz","pseudo":"Nelfe80","role":"member"}]}}""";
        var a = await Client(new Bouteille(_ => (200, detail))).DetailAsync("a1", CancellationToken.None);
        Assert.Equal("md5usa", a!.Content);
        Assert.Equal("RetroLife", a.CreatorPseudo);
        Assert.Equal(["RetroLife", "Nelfe80"], a.NomsDesJoueurs.ToArray());
    }

    [Fact]
    public void Le_detail_complete_la_liste_sans_changer_ce_que_la_liste_dit_deja()
    {
        var liste = new AdventureInfo { Id = "a1", Game = "g", Creator = "4fe5", Pseudos = ["4fe5"], Name = "Kirby #1" };
        var detail = new AdventureInfo { Id = "a1", Game = "g", Content = "md5usa", CreatorPseudo = "RetroLife", NomsDesJoueurs = ["RetroLife"], Name = "autre" };
        var c = AdventureLancement.Completer(liste, detail);
        Assert.Equal("md5usa", c.Content);
        Assert.Equal("RetroLife", c.CreatorPseudo);
        Assert.Equal("Kirby #1", c.Name);
        var dejaDit = liste with { Content = "md5eu", CreatorPseudo = "Moi" };
        Assert.Equal("md5eu", AdventureLancement.Completer(dejaDit, detail).Content);
        Assert.Equal("Moi", AdventureLancement.Completer(dejaDit, detail).CreatorPseudo);
    }

    [Fact]
    public async Task Une_liste_sans_contenu_ni_pseudos_se_complete_par_le_detail_une_fois_puis_se_garde()
    {
        var b = new Bouteille(r => r.RequestUri!.AbsolutePath.EndsWith("/mine", StringComparison.Ordinal)
            ? (200, """{"ok":true,"adventures":[{"id":"a1","game":"gba/kirby","title":"Kirby #1","places":4,"state":"active","creator":"4fe5aa048e007ad0","participants":["4fe5aa048e007ad0"]}],"invitations":[]}""")
            : (200, """{"ok":true,"adventure":{"id":"a1","game":"gba/kirby","content":"md5usa","members":[{"handle":"4fe5aa048e007ad0","pseudo":"RetroLife","role":"creator"}]}}"""));
        var lancement = new AdventureLancement(Client(b));
        var site = Client(b);
        var liste = (await site.MesAventuresAsync(CancellationToken.None))!.Aventures;
        var avant = await lancement.EnrichirAsync(liste, CancellationToken.None);
        Assert.Equal("md5usa", avant[0].Content);
        Assert.Equal("RetroLife", avant[0].CreatorPseudo);
        var details = b.Appels.Count(a => a.EndsWith("a1", StringComparison.Ordinal) && a.StartsWith("GET", StringComparison.Ordinal));
        Assert.Equal(1, details);
        await lancement.EnrichirAsync(liste, CancellationToken.None);
        Assert.Equal(1, b.Appels.Count(a => a.EndsWith("a1", StringComparison.Ordinal) && a.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Fact]
    public void Une_aventure_dont_la_borne_n_a_pas_le_contenu_est_grisee_avec_sa_raison()
    {
        AdventureInfo A(string id, string contenu) => new() { Id = id, Game = "g", Content = contenu, Seats = 4, State = "active", CreatorPseudo = "RetroLife", NomsDesJoueurs = ["RetroLife", "Nelfe80"] };
        var possede = (string jeu, string contenu) => contenu == "md5eu";
        var lignes = AdventureLancement.LesLignes([A("usa", "md5usa"), A("eu", "md5eu"), A("inconnue", "")], "g", c => c == "md5usa" ? "USA" : "Europe", "md5eu", possede);
        Assert.False(lignes.Single(l => l.Id == "usa").Jouable);
        Assert.Equal("content_missing", lignes.Single(l => l.Id == "usa").Raison);
        Assert.True(lignes.Single(l => l.Id == "eu").Jouable);
        // Un contenu que le site ne dit pas ne grise rien : on ne refuse pas sur une supposition.
        Assert.True(lignes.Single(l => l.Id == "inconnue").Jouable);
        Assert.Equal("RetroLife", lignes[0].Createur);
        Assert.Equal(["RetroLife", "Nelfe80"], lignes[0].Pseudos!.ToArray());
    }

    [Fact]
    public void La_ligne_montre_le_createur_et_les_joueurs_par_pseudo_et_la_grisee_dit_pourquoi_en_dessous()
    {
        var t = new DateTime(2026, 11, 27, 16, 43, 0, DateTimeKind.Utc);
        var local = t.ToLocalTime().ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var (lignes, _) = MultiplayerPanelService.LesAventures(new AventuresDuJeu("Europe", 4,
            [new AventureLigne("a1", "Europe", 2, t, true, "Kirby #1", ["RetroLife", "Nelfe80"], "RetroLife"),
             new AventureLigne("a2", "USA", 2, t, false, "Kirby #2", ["RetroLife"], "RetroLife", false, "content_missing")]), "Kirby", 4, Texte);
        Assert.Equal(local + " · by RetroLife · RetroLife, Nelfe80 · IN PROGRESS", lignes[0].Etat);
        Assert.True(lignes[0].Present);
        Assert.Equal("Kirby #2", lignes[1].Nom);
        Assert.False(lignes[1].Present);
        Assert.Equal("The creator's version of the game is not on this cabinet", lignes[1].Etat);
    }

    [Fact]
    public void Une_invitation_vers_une_version_qu_on_n_a_pas_est_grisee_et_une_aventure_en_cours_jouable_passe_avant()
    {
        var invitation = new MultiplayerInvitation { Invitation = "i1", Game = "gba/kirby", Content = "md5usa", Adventure = "a1", From = new MultiplayerJoueur { Pseudo = "RetroLife" } };
        var offre = MultiplayerPanelService.OffreDEntreeAventure([invitation], "gba/kirby", null, false, Texte, (jeu, contenu) => contenu == "md5eu");
        Assert.NotNull(offre);
        Assert.False(offre.Jouable);
        Assert.Equal("content_missing", offre.Raison);
        Assert.Equal("Invitation from RetroLife: A to join", offre.Texte);

        var jouable = MultiplayerPanelService.OffreDEntreeAventure([invitation with { Content = "md5eu" }], "gba/kirby", null, false, Texte, (jeu, contenu) => contenu == "md5eu");
        Assert.True(jouable!.Jouable);

        var aventures = new AventuresDuJeu("Europe", 4,
        [
            new AventureLigne("usa", "USA", 2, null, true, "U", ["RetroLife"], "RetroLife", false, "content_missing"),
            new AventureLigne("eu", "Europe", 2, null, true, "E", ["Nelfe80"], "Nelfe80"),
        ]);
        var attend = MultiplayerPanelService.OffreDEntreeAventure([], "gba/kirby", aventures, false, Texte);
        Assert.Equal("eu", attend!.Aventure);
        Assert.True(attend.Jouable);
        // Seule une aventure non jouable attend : l'offre existe, grisee.
        var seule = MultiplayerPanelService.OffreDEntreeAventure([], "gba/kirby", aventures with { Lignes = [aventures.Lignes![0]] }, false, Texte);
        Assert.False(seule!.Jouable);
    }

    [Fact]
    public async Task L_ami_invite_dans_la_salle_est_invite_a_l_aventure_puis_la_rejoint_en_acceptant()
    {
        var b = new Bouteille(r => r.RequestUri!.AbsolutePath.EndsWith("/invite", StringComparison.Ordinal)
            ? (200, """{"ok":true,"invitation":"inv1","duplicate":false}""")
            : r.RequestUri.AbsolutePath.EndsWith("/mine", StringComparison.Ordinal)
                ? (200, """{"ok":true,"adventures":[],"invitations":[{"invitation":"inv1","adventure":"a1","game":"gba/kirby","from":{"handle":"h1","pseudo":"Nelfe80"}}]}""")
                : (200, """{"ok":true,"adventure":"a1","accepted":true}"""));
        var lancement = new AdventureLancement(Client(b));
        Assert.Null(await lancement.InviterALAventureAsync("a1", "retrolife", CancellationToken.None));
        Assert.True(await lancement.DevenirParticipantAsync("a1", CancellationToken.None));
        Assert.Equal(["POST a1/invite", "GET mine", "POST invitation"], b.Appels);
        Assert.Contains("\"handle\":\"retrolife\"", b.Corps[0], StringComparison.Ordinal);
        Assert.Contains("\"invitation\":\"inv1\"", b.Corps[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_ami_deja_participant_ou_sans_invitation_ne_bloque_rien()
    {
        var deja = new Bouteille(_ => (409, """{"ok":false,"reason":"already_member"}"""));
        Assert.Null(await new AdventureLancement(Client(deja)).InviterALAventureAsync("a1", "x", CancellationToken.None));
        var refuse = new Bouteille(_ => (404, """{"ok":false,"reason":"not_found"}"""));
        Assert.Equal("not_found", await new AdventureLancement(Client(refuse)).InviterALAventureAsync("a1", "x", CancellationToken.None));

        var sans = new Bouteille(_ => (200, """{"ok":true,"adventures":[],"invitations":[]}"""));
        Assert.False(await new AdventureLancement(Client(sans)).DevenirParticipantAsync("a1", CancellationToken.None));
        var membre = new Bouteille(_ => (200, """{"ok":true,"adventures":[{"id":"a1","game":"g","state":"active"}],"invitations":[]}"""));
        Assert.True(await new AdventureLancement(Client(membre)).DevenirParticipantAsync("a1", CancellationToken.None));
        Assert.Equal(["GET mine"], membre.Appels);
    }

    [Fact]
    public async Task X_passe_par_session_size_et_rend_la_raison_du_site()
    {
        var b = new Bouteille(r => r.RequestUri!.AbsolutePath.Contains("/ok/", StringComparison.Ordinal)
            ? (200, """{"ok":true,"players":3}""") : (403, """{"ok":false,"reason":"not_host"}"""));
        var client = Client(b);
        Assert.Equal((true, (string?)null), await client.ReglerLaSeanceAsync("ok", 3, CancellationToken.None));
        Assert.Equal((false, (string?)"not_host"), await client.ReglerLaSeanceAsync("ko", 3, CancellationToken.None));
        Assert.Equal(["POST ok/session/size", "POST ko/session/size"], b.Appels);
        Assert.Contains("\"players\":3", b.Corps[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Les_refus_du_reglage_ont_leur_texte_adventure()
    {
        foreach (var raison in new[] { "not_owner", "not_host", "no_session", "too_late", "locked", "size_refused", "content_missing" })
        {
            Assert.Equal(raison, AdventureRefus.Raison(raison));
            Assert.NotEqual(AdventureRefus.Texte("other", Texte), AdventureRefus.Texte(raison, Texte));
        }
    }

    [Fact]
    public async Task L_activation_refusee_pour_un_hote_non_participant_accepte_son_invitation_puis_recommence()
    {
        var appels = 0;
        var b = new Bouteille(r =>
        {
            var chemin = r.RequestUri!.AbsolutePath;
            if (chemin.EndsWith("/activate", StringComparison.Ordinal))
            {
                return ++appels == 1 ? (403, """{"ok":false,"reason":"not_member"}""") : (200, """{"ok":true,"activated":true}""");
            }

            return chemin.EndsWith("/mine", StringComparison.Ordinal)
                ? (200, """{"ok":true,"adventures":[],"invitations":[{"invitation":"inv9","adventure":"a1"}]}""")
                : (200, """{"ok":true,"accepted":true}""");
        });
        var dossier = Path.Combine(Path.GetTempPath(), "adv-rejoindre-" + Guid.NewGuid().ToString("N"));
        var stockage = new AdventureStockage(dossier);
        var service = new AdventureSeanceService(Client(b), stockage, new AdventureFileDEnvoi(stockage));
        Assert.True(await service.ActiverAsync("a1", 2));
        Assert.Equal(["POST a1/activate", "GET mine", "POST invitation", "POST a1/activate"], b.Appels);
    }

    [Fact]
    public async Task Une_nouvelle_aventure_ferme_l_ancienne_seance_du_brouillon_et_part_des_places_du_jeu()
    {
        var b = new Bouteille(r =>
        {
            var chemin = r.RequestUri!.AbsolutePath;
            return chemin.EndsWith("/create", StringComparison.Ordinal)
                ? (200, """{"ok":true,"reused":true,"adventure":{"id":"d1","game":"gba/kirby","content":"c","places":4,"state":"draft","session":{"room":"vieille"}}}""")
                : chemin.EndsWith("/session/open", StringComparison.Ordinal)
                    ? (200, """{"ok":true,"attached":false,"room":"neuve","session":{"id":"s2","room":"neuve"}}""")
                    : (200, """{"ok":true}""");
        });
        var jeu = new MultiplayerInstalledGame(new MultiplayerGame { Id = "gba/kirby", Players = 4 }, "k.gba", null, "c");
        Assert.Null(await new AdventureLancement(Client(b)).NouvelleAsync(jeu, CancellationToken.None));
        Assert.Equal(["POST create", "POST d1/session/close", "POST d1/session/open", "POST d1/session/size"], b.Appels);
        Assert.Contains("\"players\":4", b.Corps[2], StringComparison.Ordinal);
        Assert.Contains("\"places\":4", b.Corps[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Les_champs_du_site_creator_name_et_participants_names_sont_lus_en_premier()
    {
        var corps = """{"ok":true,"adventures":[{"id":"a1","game":"g","creator":"4fe5","creator_name":"RetroLife","participants":["4fe5","zz"],"participants_names":["RetroLife","Nelfe80"]}],"invitations":[]}""";
        var a = Assert.Single((await Client(new Bouteille(_ => (200, corps))).MesAventuresAsync(CancellationToken.None))!.Aventures);
        Assert.Equal("RetroLife", a.CreatorPseudo);
        Assert.Equal(["RetroLife", "Nelfe80"], a.NomsDesJoueurs.ToArray());
    }
}
