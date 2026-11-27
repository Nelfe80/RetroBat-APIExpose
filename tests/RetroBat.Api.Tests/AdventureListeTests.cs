using System.Text;
using System.Text.Json;
using RetroBat.Api.Adventure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 27/11 : brouillon, activation, liste d'aventures en lignes (nom, date, joueurs), refus propres a Adventure.
/// Le contrat du site : E:\claude_travail\temp\adventure\CONTRAT.md, section 27/11.
/// </summary>
public sealed class AdventureListeTests
{
    private sealed class Bouteille(Func<HttpRequestMessage, (int Statut, string Corps)> reponse) : HttpMessageHandler
    {
        public List<string> Appels { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
        {
            Appels.Add(requete.Method + " " + requete.RequestUri!.AbsolutePath);
            var (statut, corps) = reponse(requete);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)statut) { Content = new StringContent(corps, Encoding.UTF8, "application/json") });
        }
    }

    private static AdventureSiteClient Client(Bouteille b) => new(() => new HttpClient(b) { BaseAddress = new Uri("https://nelfeplay.test") });

    [Fact]
    public async Task Creer_rend_un_brouillon_ou_la_raison_du_refus()
    {
        var b = new Bouteille(r => (200, """{"ok":true,"reused":true,"adventure":{"id":"av1","game":"g","content":"c","places":4,"state":"draft","draft":true}}"""));
        var (aventure, raison) = await Client(b).CreerAvecRaisonAsync("g", "c", 4, AdventureModes.Exclusive, null, null, null, CancellationToken.None);
        Assert.Null(raison);
        Assert.True(aventure!.EstUnBrouillon);

        var refuse = new Bouteille(r => (429, """{"ok":false,"reason":"rate_limited"}"""));
        Assert.Equal((null, "rate_limited"), await Client(refuse).CreerAvecRaisonAsync("g", "c", 4, AdventureModes.Exclusive, null, null, null, CancellationToken.None));
        Assert.Equal((null, "offline"), await new AdventureSiteClient(() => null).CreerAvecRaisonAsync("g", "c", 4, AdventureModes.Exclusive, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task L_activation_appelle_la_route_activate_et_rend_la_raison_du_site()
    {
        var b = new Bouteille(r => r.RequestUri!.AbsolutePath.EndsWith("/av1/activate", StringComparison.Ordinal)
            ? (200, """{"ok":true,"activated":true,"adventure":{"id":"av1"}}""")
            : (409, """{"ok":false,"reason":"not_enough_players"}"""));
        var client = Client(b);
        Assert.Equal((true, (string?)null), await client.ActiverAsync("av1", 2, CancellationToken.None));
        Assert.Equal((false, (string?)"not_enough_players"), await client.ActiverAsync("av2", 1, CancellationToken.None));
        Assert.Equal((false, (string?)"offline"), await new AdventureSiteClient(() => null).ActiverAsync("av1", 2, CancellationToken.None));
        Assert.Equal(["POST /api/v1/agent/adventure/av1/activate", "POST /api/v1/agent/adventure/av2/activate"], b.Appels);
    }

    [Fact]
    public async Task La_liste_du_site_rend_le_nom_la_date_et_les_joueurs_par_place()
    {
        var b = new Bouteille(r => (200, """{"ok":true,"adventures":[{"id":"a1","game":"g","title":"Kirby #2","places":4,"state":"active","members":2,"last_played_at":"2026-11-27T20:30:00Z","participants":["alice","bob"],"session":{"room":"r1"}}],"invitations":[]}"""));
        var mine = await Client(b).MesAventuresAsync(CancellationToken.None);
        var a = Assert.Single(mine!.Aventures);
        Assert.Equal("Kirby #2", a.Name);
        Assert.Equal(["alice", "bob"], a.Pseudos.ToArray());
        Assert.Equal(new DateTime(2026, 11, 27, 20, 30, 0, DateTimeKind.Utc), a.LastPlayedUtc);
        Assert.False(a.EstUnBrouillon);
    }

    [Fact]
    public void Les_lignes_montrent_les_actives_de_ce_jeu_la_derniere_partie_d_abord_sans_brouillon()
    {
        var t = new DateTime(2026, 11, 27, 20, 0, 0, DateTimeKind.Utc);
        AdventureInfo A(string id, string jeu, string? etat, DateTime? date, params string[] pseudos)
            => new() { Id = id, Game = jeu, Name = "Nom " + id, Seats = 4, State = etat, LastPlayedUtc = date, Pseudos = pseudos, Content = "c" };
        var lignes = AdventureLancement.LesLignes(
            [A("vieille", "g", "active", t.AddDays(-3), "alice", "bob"), A("brouillon", "g", "draft", t.AddHours(1)), A("autre", "h", "active", t),
             A("recente", "g", "active", t, "alice", "carl", "dora"), A("ancienne-api", "g", null, null)],
            "g", c => "Europe", "c");
        Assert.Equal(["recente", "vieille", "ancienne-api"], lignes.Select(l => l.Id).ToArray());
        Assert.Equal("Nom recente", lignes[0].Titre);
        Assert.Equal(["alice", "carl", "dora"], lignes[0].Pseudos!.ToArray());
        Assert.Equal(t, lignes[0].Date);
        Assert.Null(lignes[2].Date);
    }

    private static string Texte(string cle, string defaut) => defaut;

    [Fact]
    public void L_onglet_est_une_liste_de_lignes_avec_nom_date_et_joueurs_et_se_choisit()
    {
        var t = new DateTime(2026, 11, 27, 20, 30, 0, DateTimeKind.Utc);
        var local = t.ToLocalTime().ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var (lignes, haut) = MultiplayerPanelService.LesAventures(new AventuresDuJeu("Europe", 4,
            [new AventureLigne("a1", "Europe", 3, t, true, "Kirby #2", ["alice", "bob", "carl"]), new AventureLigne("a0", "USA", 2, null, false)]),
            "Kirby", 4, Texte);
        Assert.Equal("HOLD A : START A NEW ADVENTURE (Europe, 4 seats)", haut);
        Assert.Equal(2, lignes.Count);
        Assert.Equal("Kirby #2", lignes[0].Nom);
        Assert.Equal(local + " · alice, bob, carl · IN PROGRESS", lignes[0].Etat);
        // Sans titre ni pseudos : le jeu et sa version, le nombre de joueurs.
        Assert.Equal("Kirby (USA)", lignes[1].Nom);
        Assert.Equal("2 players", lignes[1].Etat);
        // Des lignes, pas des tuiles : ni place ni case libre.
        Assert.All(lignes, l => { Assert.Null(l.Place); Assert.False(l.Libre); });

        // Sans aventure : une ligne d'information, qui ne se choisit pas.
        Assert.Equal("NO ADVENTURE YET FOR THIS GAME", MultiplayerPanelService.LesAventures(new AventuresDuJeu("Europe", 4, []), "Kirby", 4, Texte).Lignes[0].Nom);
    }

    [Fact]
    public void Les_lignes_de_l_onglet_NEW_ADVENTURE_se_choisissent_seulement_pour_une_aventure()
    {
        var modele = new MultiplayerPanelModel();
        modele.Ouvrir();
        modele.Gauche();   // menu d'ES -> panneau, sur FOR PLAYERS
        Assert.Equal(MultiplayerPanelModel.Onglet.WaitingForPlayers, modele.OngletCourant);

        modele.PoserLesLignes(2);
        Assert.Equal(MultiplayerPanelModel.Effet.Rien, modele.Bas());   // MULTIPLAYER : aucune ligne a choisir
        Assert.Equal(-1, modele.Ligne);

        modele.ListeDAventures = true;
        modele.PoserLesLignes(2);
        Assert.Equal(MultiplayerPanelModel.Effet.Rien, modele.Valider());   // ligne du haut : rien (c'est l'appui long qui crée)
        modele.Bas();
        Assert.Equal(0, modele.Ligne);
        Assert.Equal(MultiplayerPanelModel.Effet.AgirSurLaLigne, modele.Valider());
        modele.Bas();
        modele.Bas();
        Assert.Equal(1, modele.Ligne);   // borné
        modele.Haut();
        modele.Haut();
        Assert.Equal(-1, modele.Ligne);
        modele.PoserLesLignes(0);        // « NO ADVENTURE YET » : rien à choisir
        Assert.Equal(MultiplayerPanelModel.Effet.Rien, modele.Bas());
    }

    [Theory]
    [InlineData("rate_limited", "Too many adventures")]
    [InlineData("bad_players", "at least two players")]
    [InlineData("not_enough_players", "at least two players")]
    [InlineData("offline", "not answering")]
    [InlineData("n_importe_quoi", "Could not start")]
    public void Chaque_refus_du_site_a_son_propre_texte_Adventure_jamais_un_texte_MULTIPLAYER(string raison, string contient)
    {
        var texte = AdventureRefus.Texte(raison, Texte);
        Assert.Contains(contient, texte);
        Assert.DoesNotContain("MULTIPLAYER", texte);
        Assert.StartsWith("adventure.refused.", AdventureRefus.Cle(raison));

        // Sur le panneau : un jeu d'Adventure prend le texte d'Adventure, un jeu MULTIPLAYER garde le sien.
        var panneau = MultiplayerPanelService.TexteDuRefus(raison, true, c => c, Texte);
        Assert.Equal(texte, panneau);
        Assert.Equal("multiplayer.refused.other", MultiplayerPanelService.TexteDuRefus(raison, false, c => c, Texte));
    }

    [Fact]
    public void Le_Data_Pack_porte_un_texte_pour_chaque_refus_et_chaque_ligne_en_anglais_et_en_francais()
    {
        var pack = Environment.GetEnvironmentVariable("NELFEPLAY_DATAPACK");
        if (string.IsNullOrEmpty(pack) || !File.Exists(Path.Combine(pack, "locales", "interface-texts.json")))
        {
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "locales", "interface-texts.json")));
        var cles = AdventureRefus.Connus.Keys.Select(r => "adventure.refused." + r)
            .Concat(["adventure.new.selected", "adventure.help.select"]).ToList();
        foreach (var langue in new[] { "en", "en_gb", "fr" })
        {
            var textes = doc.RootElement.GetProperty(langue);
            foreach (var cle in cles)
            {
                Assert.True(textes.TryGetProperty(cle, out var valeur) && valeur.GetString()!.Length > 0, langue + " : " + cle);
                Assert.DoesNotContain("—", valeur.GetString());
                Assert.DoesNotContain("RAM", valeur.GetString());
            }
        }
    }

    [Fact]
    public void Le_rendu_de_la_liste_passe_au_dessin()
    {
        var t = new DateTime(2026, 11, 27, 20, 30, 0, DateTimeKind.Utc);
        var (lignes, haut) = MultiplayerPanelService.LesAventures(new AventuresDuJeu("Europe", 4,
            [new AventureLigne("a1", "Europe", 3, t, true, "Kirby #2", ["alice", "bob", "carl"]), new AventureLigne("a0", "Europe", 2, t.AddDays(-2), false, "Kirby #1", ["alice", "bob"])]),
            "Kirby", 4, Texte);
        var dossier = Path.Combine("E:" + Path.DirectorySeparatorChar, "claude_travail", "temp", "adventure_panel");
        Directory.CreateDirectory(dossier);
        var style = EsMenuStyle.Lire(null);
        var contenu = new LeaderboardOverlayService.ContenuMultiplayer(["INVITE", "ADVENTURE", "NEW ADVENTURE (HOLD A)"], 2, true, string.Empty, haut, string.Empty, null,
            lignes, 0, string.Empty, string.Empty, [new("dpad_leftright", "TAB")], Logo: AdventureCollection.Logo,
            Disposition: LeaderboardOverlayService.DispositionDesJoueurs.Liste);
        var png = LeaderboardOverlayService.ApercuMultiplayer(style, contenu, "", "", "", Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaderboardOverlayService>.Instance);
        Assert.NotEmpty(png);
        File.WriteAllBytes(Path.Combine(dossier, "4-liste-des-aventures.png"), png);
    }
}
