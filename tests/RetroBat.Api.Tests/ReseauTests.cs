using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Reseau;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les fondations du reseau, cote borne (CDC infra §15, 2026-10-08) : l'enveloppe signee, la carte, le verdict
/// signe, l'enveloppe scellee et le relais. Les vecteurs du central (PHP) disent que les deux cotes parlent
/// exactement la meme langue.
/// </summary>
public class ReseauTests
{
    private static readonly JsonObject Vecteurs = (JsonObject)JsonNode.Parse(VecteursDuCentral.Json)!;

    private static ClePublique Publique(ECDsa cle) => ClePublique.DepuisPem(cle.ExportSubjectPublicKeyInfoPem())!;

    private static ClePublique Publique(ECDiffieHellman cle) => ClePublique.DepuisPem(cle.ExportSubjectPublicKeyInfoPem())!;

    // ── L'enveloppe scellee ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_central_ouvre_ce_que_la_borne_scelle_et_la_borne_ouvre_sa_reponse()
    {
        using var central = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var requete = new JsonObject { ["path"] = "scores/ticket", ["device"] = "secret-de-la-borne" };

        var scellee = Scellement.Sceller(requete, Publique(central), garder: false);

        Assert.DoesNotContain("secret-de-la-borne", scellee.Enveloppe.ToJsonString());
        var ouverte = Scellement.Ouvrir(scellee.Enveloppe, central);
        Assert.NotNull(ouverte);
        Assert.Equal("secret-de-la-borne", (string?)ouverte.Value.Requete["device"]);
        Assert.Equal(scellee.CleReponse, ouverte.Value.CleReponse);

        var reponse = Scellement.ScellerReponse(scellee.Id, ouverte.Value.CleReponse, new JsonObject { ["status"] = 200, ["body"] = "{}" });
        Assert.Equal(200, (int?)Scellement.OuvrirReponse(reponse, scellee.Id, scellee.CleReponse)?["status"]);
        Assert.Null(Scellement.OuvrirReponse(reponse, "autre", scellee.CleReponse));
    }

    [Fact]
    public void La_borne_ouvre_ce_que_le_central_PHP_a_scelle()
    {
        using var central = ECDiffieHellman.Create();
        central.ImportFromPem((string)Vecteurs["seal_private"]!);
        var enveloppe = (JsonObject)Vecteurs["sealed_request"]!;

        var ouverte = Scellement.Ouvrir(enveloppe, central);

        Assert.NotNull(ouverte);
        Assert.True(JsonNode.DeepEquals(Vecteurs["request"], ouverte.Value.Requete));
        Assert.Equal((string)Vecteurs["reply_key_hex"]!, Convert.ToHexString(ouverte.Value.CleReponse).ToLowerInvariant());
        Assert.Equal("1200", (string?)ouverte.Value.Resume?["score"]);
        var reponse = Scellement.OuvrirReponse((JsonObject)Vecteurs["sealed_reply"]!, (string)enveloppe["id"]!, ouverte.Value.CleReponse);
        Assert.True(JsonNode.DeepEquals(Vecteurs["reply"], reponse));
    }

    /// <summary>
    /// Le vecteur dans l'autre sens : une requete scellee par la borne pour la cle de scellement des vecteurs du
    /// central. Ecrit si NELFE_RESEAU_VECTEUR donne un fichier ; tests/reseau.php du site l'ouvre.
    /// </summary>
    [Fact]
    public void Vecteur_pour_le_central()
    {
        var requete = new JsonObject
        {
            ["method"] = "POST", ["path"] = "scores/submissions", ["query"] = "", ["device"] = "cred-borne",
            ["label"] = "", ["body"] = "{\"session_id\":\"s-2\",\"score\":\"é\"}", ["sent_at"] = "2026-10-08T21:30:00Z",
        };
        var scellee = Scellement.Sceller(requete, ClePublique.DepuisPem((string)Vecteurs["seal_public"]!)!, garder: true,
            new JsonObject { ["session_id"] = "s-2" });
        var fichier = Environment.GetEnvironmentVariable("NELFE_RESEAU_VECTEUR");
        if (!string.IsNullOrEmpty(fichier))
        {
            File.WriteAllText(fichier, new JsonObject
            {
                ["sealed_request"] = scellee.Enveloppe.DeepClone(),
                ["request"] = requete.DeepClone(),
                ["reply_key_hex"] = Convert.ToHexString(scellee.CleReponse).ToLowerInvariant(),
            }.ToJsonString());
        }
        using var central = ECDiffieHellman.Create();
        central.ImportFromPem((string)Vecteurs["seal_private"]!);
        Assert.NotNull(Scellement.Ouvrir(scellee.Enveloppe, central));
    }

    [Theory]
    [InlineData("keep", "false")]
    [InlineData("pub", "\"eyJzY29yZSI6Ijk5OTk5OSJ9\"")]
    [InlineData("id", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void Une_enveloppe_scellee_retouchee_ne_s_ouvre_plus(string champ, string valeur)
    {
        using var central = ECDiffieHellman.Create();
        central.ImportFromPem((string)Vecteurs["seal_private"]!);
        var enveloppe = (JsonObject)Vecteurs["sealed_request"]!.DeepClone();
        enveloppe[champ] = JsonNode.Parse(valeur);

        Assert.Null(Scellement.Ouvrir(enveloppe, central));
    }

    // ── Le verdict signe ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_verdict_signe_par_le_central_se_verifie_et_fait_foi()
    {
        var cles = new[] { ClePublique.DepuisPem((string)Vecteurs["issuer_public"]!)! };
        var passeport = Encoding.UTF8.GetBytes((string)Vecteurs["passport"]!);
        var corps = (string)Vecteurs["passport_response"]!;

        var lecture = VerdictSigne.Verifier(corps, cles, "s-1", "d_9", passeport);

        Assert.True(lecture.Valide, lecture.Raison);
        Assert.Equal("published", (string?)lecture.Contenu!["status"]);
        var revu = JsonNode.Parse(VerdictSigne.AvecLesChampsSignes(corps.Replace("\"rank\":3", "\"rank\":1"), lecture.Contenu))!;
        Assert.Equal(3, (int?)revu["rank"]);
    }

    [Theory]
    [InlineData("s-2", "d_9", false, "autre_session")]
    [InlineData("s-1", "d_8", false, "autre_appareil")]
    [InlineData("s-1", "d_9", true, "autre_passeport")]
    public void Un_verdict_d_une_autre_partie_ne_vaut_rien(string session, string appareil, bool autreCorps, string raison)
    {
        var cles = new[] { ClePublique.DepuisPem((string)Vecteurs["issuer_public"]!)! };
        var passeport = Encoding.UTF8.GetBytes((string)Vecteurs["passport"]! + (autreCorps ? " " : ""));

        var lecture = VerdictSigne.Verifier((string)Vecteurs["passport_response"]!, cles, session, appareil, passeport);

        Assert.False(lecture.Valide);
        Assert.Equal(raison, lecture.Raison);
    }

    [Fact]
    public void Un_verdict_sans_signature_ou_d_une_cle_inconnue_ne_vaut_rien()
    {
        var passeport = Encoding.UTF8.GetBytes((string)Vecteurs["passport"]!);
        var corps = (string)Vecteurs["passport_response"]!;

        Assert.Equal("verdict_absent", VerdictSigne.Verifier("{\"ok\":true,\"status\":\"published\"}",
            CarteDuReseau.ParDefaut.ClesDeVerdict, "s-1", "d_9", passeport).Raison);
        Assert.StartsWith("cle_inconnue", VerdictSigne.Verifier(corps, CarteDuReseau.ParDefaut.ClesDeVerdict, "s-1", "d_9", passeport).Raison);
        var retouche = JsonNode.Parse(corps)!;
        var charge = Encoding.UTF8.GetString(Crypto.FromB64Url((string)retouche["verdict"]!["payload"]!)).Replace("\"rank\":3", "\"rank\":1");
        retouche["verdict"]!["payload"] = Crypto.B64Url(Encoding.UTF8.GetBytes(charge));
        Assert.Equal("signature_invalide", VerdictSigne.Verifier(retouche.ToJsonString(),
            [ClePublique.DepuisPem((string)Vecteurs["issuer_public"]!)!], "s-1", "d_9", passeport).Raison);
    }

    // ── La carte ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void La_carte_signee_par_le_central_s_ouvre_avec_sa_cle_seulement()
    {
        var cleDuSite = ClePublique.DepuisPem((string)Vecteurs["site_public"]!)!;

        var carte = CarteDuReseau.Ouvrir((string)Vecteurs["map"]!, cleDuSite);

        Assert.NotNull(carte);
        Assert.Equal(1791331200, carte.Version);
        Assert.Equal("https://nelfeplay.com", carte.UrlDuCentral);
        Assert.Equal(ClePublique.DepuisPem((string)Vecteurs["seal_public"]!)!.KeyId, carte.CleDeScellement?.KeyId);
        Assert.Equal(new[] { "miroir", "eu" }, carte.Noeuds.Select(n => n.Nom));
        Assert.Equal(new[] { "front" }, carte.Noeuds[0].Roles);
        Assert.Equal(new[] { "front", "relay", "replay" }, carte.Noeuds[1].Roles);
        Assert.NotNull(carte.Noeuds[1].Cle);
        Assert.Equal(3, carte.Noeuds[1].Poids);
        Assert.Null(CarteDuReseau.Ouvrir((string)Vecteurs["map"]!, CarteDuReseau.CleDuSiteStatique));
    }

    [Fact]
    public void La_carte_par_defaut_porte_les_cles_du_central()
    {
        Assert.Equal("bcd5ab3c8104cbda0a8367002a6afcd8a6cb1f1826fa5f8dedac67238151a184", CarteDuReseau.ParDefaut.ClesDeVerdict.Single().KeyId);
        Assert.StartsWith("ac42342a", CarteDuReseau.CleDuSiteStatique.KeyId);
        Assert.Equal(0, CarteDuReseau.ParDefaut.Version);
        Assert.Empty(CarteDuReseau.ParDefaut.Noeuds);
    }

    [Fact]
    public void La_borne_n_adopte_qu_une_carte_signee_et_plus_recente_et_la_garde()
    {
        var dossier = Directory.CreateTempSubdirectory("carte-");
        try
        {
            var fichier = Path.Combine(dossier.FullName, "carte.json");
            var cleDuSite = ClePublique.DepuisPem((string)Vecteurs["site_public"]!)!;
            var service = new ServiceDeCarte(new FauxReseau(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), null, fichier, cleDuSite, () => "https://nelfeplay.com");

            Assert.True(service.Proposer((string)Vecteurs["map"]!, "essai"));
            Assert.False(service.Proposer((string)Vecteurs["map"]!, "essai"));
            using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var contrefaite = EnveloppeSignee.Signer(Crypto.FromB64Url((string)JsonNode.Parse((string)Vecteurs["map"]!)!["payload"]!), autre);
            Assert.False(service.Proposer(contrefaite.ToJsonString(), "essai"));

            var relue = new ServiceDeCarte(new FauxReseau(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), null, fichier, cleDuSite, () => "https://nelfeplay.com");
            Assert.Equal(1791331200, relue.Actuelle.Version);
            Assert.Equal(relue.Actuelle.ClesDeVerdict.Single().KeyId, ClePublique.DepuisPem((string)Vecteurs["issuer_public"]!)!.KeyId);
        }
        finally
        {
            dossier.Delete(true);
        }
    }

    [Fact]
    public async Task La_borne_lit_la_carte_chez_le_central_puis_chez_une_copie()
    {
        var dossier = Directory.CreateTempSubdirectory("carte-");
        try
        {
            var cleDuSite = ClePublique.DepuisPem((string)Vecteurs["site_public"]!)!;
            var demandes = new List<string>();
            var reseau = new FauxReseau(requete =>
            {
                demandes.Add(requete.RequestUri!.ToString());
                return requete.RequestUri!.Host == "miroir.nelfeplay.com"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent((string)Vecteurs["map"]!) }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
            var service = new ServiceDeCarte(reseau, null, Path.Combine(dossier.FullName, "carte.json"), cleDuSite, () => "https://nelfeplay.com");

            Assert.True(await service.RafraichirAsync(CancellationToken.None));
            Assert.Equal("https://nelfeplay.com/.well-known/nelfeplay-carte.json", demandes[0]);
            Assert.Equal(1791331200, service.Actuelle.Version);
        }
        finally
        {
            dossier.Delete(true);
        }
    }

    // ── Le relais ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Une carte d'essai : un central (sa cle de scellement) et des noeuds « relay » dont on tient les cles.</summary>
    private sealed class Essai : IDisposable
    {
        public readonly ECDsa Site = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly ECDiffieHellman Central = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public readonly ECDsa Emetteur = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly Dictionary<string, ECDsa> Noeuds = new();
        public readonly DirectoryInfo Dossier = Directory.CreateTempSubdirectory("relais-");

        public ServiceDeCarte Carte(IHttpClientFactory reseau, params (string Nom, string Hote)[] relais)
        {
            var noeuds = new JsonArray();
            var id = 10;
            foreach (var (nom, hote) in relais)
            {
                var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Noeuds[nom] = cle;
                noeuds.Add(new JsonObject
                {
                    ["id"] = id++, ["name"] = nom, ["kind"] = "node", ["url"] = $"https://{nom}.exemple",
                    ["key_id"] = Crypto.KeyId(cle.ExportSubjectPublicKeyInfo()), ["public_key"] = cle.ExportSubjectPublicKeyInfoPem(),
                    ["roles"] = new JsonArray("front", "relay"), ["region"] = "", ["country"] = "", ["host"] = hote, ["weight"] = 1,
                });
            }
            var carte = new JsonObject
            {
                ["format"] = 1, ["kind"] = "nelfeplay-map", ["version"] = 5, ["issued_at"] = "2026-10-08T00:00:00Z",
                ["central"] = new JsonObject
                {
                    ["url"] = "https://nelfeplay.com", ["seal_key"] = Central.ExportSubjectPublicKeyInfoPem(),
                    ["seal_key_id"] = Crypto.KeyId(Central.ExportSubjectPublicKeyInfo()),
                    ["verdict_keys"] = new JsonArray(Emetteur.ExportSubjectPublicKeyInfoPem()),
                },
                ["replay"] = new JsonObject { ["copies"] = 2 },
                ["nodes"] = noeuds,
            };
            var service = new ServiceDeCarte(reseau, null, Path.Combine(Dossier.FullName, "carte.json"), Publique(Site), () => "https://nelfeplay.com");
            Assert.True(service.Proposer(EnveloppeSignee.Signer(Encoding.UTF8.GetBytes(carte.ToJsonString()), Site).ToJsonString(), "essai"));
            return service;
        }

        public void Dispose()
        {
            Site.Dispose();
            Central.Dispose();
            Emetteur.Dispose();
            foreach (var cle in Noeuds.Values) cle.Dispose();
            Dossier.Delete(true);
        }
    }

    [Fact]
    public async Task Le_relais_rend_la_reponse_du_central_sans_rien_lire()
    {
        using var essai = new Essai();
        string? vuParLeNoeud = null;
        var reseau = new FauxReseau(requete =>
        {
            // Le noeud fait suivre ; le central ouvre, repond, scelle la reponse.
            vuParLeNoeud = requete.Content!.ReadAsStringAsync().Result;
            var ouverte = Scellement.Ouvrir((JsonObject)JsonNode.Parse(vuParLeNoeud)!, essai.Central)!.Value;
            var corps = "{\"ticket\":{\"device_id\":\"d_1\"},\"vu\":\"" + (string?)ouverte.Requete["device"] + "\"}";
            var scellee = Scellement.ScellerReponse((string)JsonNode.Parse(vuParLeNoeud)!["id"]!, ouverte.CleReponse,
                new JsonObject { ["status"] = 200, ["retry_after"] = null, ["body"] = corps });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["ok"] = true, ["status"] = 200, ["reply"] = scellee }.ToJsonString()),
            };
        });
        var client = new ClientDeRelais(reseau, essai.Carte(reseau, ("r1", "a")));

        var issue = await client.EnvoyerAsync("POST", "scores/ticket", null, null, "code-de-la-borne", null, false, null, CancellationToken.None);

        Assert.NotNull(issue.Reponse);
        Assert.Equal(200, issue.Reponse.Statut);
        Assert.Contains("\"vu\":\"code-de-la-borne\"", issue.Reponse.Corps);
        Assert.DoesNotContain("code-de-la-borne", vuParLeNoeud);
    }

    [Fact]
    public async Task Central_tombe_deux_hebergeurs_gardent_la_partie_avec_un_recu_signe()
    {
        using var essai = new Essai();
        var reseau = new FauxReseau(requete =>
        {
            var nom = requete.RequestUri!.Host.Split('.')[0];
            var octets = requete.Content!.ReadAsByteArrayAsync().Result;
            var enveloppe = JsonNode.Parse(octets)!;
            Assert.True((bool)enveloppe["keep"]!);
            var recu = new JsonObject
            {
                ["format"] = 1, ["kind"] = ClientDeRelais.GenreDuRecu, ["id"] = (string)enveloppe["id"]!,
                ["sha256"] = Crypto.Sha256Hex(octets), ["node"] = nom, ["received_at"] = "2026-10-08T22:00:00Z",
            };
            var signe = EnveloppeSignee.Signer(Encoding.UTF8.GetBytes(recu.ToJsonString()), essai.Noeuds[nom]);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(new JsonObject { ["ok"] = true, ["kept"] = true, ["receipt"] = signe }.ToJsonString()),
            };
        });
        var client = new ClientDeRelais(reseau, essai.Carte(reseau, ("r1", "a"), ("r2", "a"), ("r3", "b")));

        var issue = await client.EnvoyerAsync("POST", "scores/submissions", null, "{\"session_id\":\"s\"}", "code", null, true,
            new JsonObject { ["session_id"] = "s" }, CancellationToken.None);

        Assert.Null(issue.Reponse);
        Assert.Equal(2, issue.Recus.Count);
        Assert.Equal(2, issue.Recus.Select(r => r.Hote).Distinct().Count());
    }

    [Fact]
    public void Un_recu_d_une_autre_cle_ou_d_autres_octets_ne_vaut_rien()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var noeud = new NoeudDuReseau(1, "r1", "node", "https://r1.exemple", Publique(cle), ["relay"], "", "", "a", 1);
        var recu = new JsonObject
        {
            ["format"] = 1, ["kind"] = ClientDeRelais.GenreDuRecu, ["id"] = "abc", ["sha256"] = "00", ["node"] = "r1", ["received_at"] = "x",
        };
        var octets = Encoding.UTF8.GetBytes(recu.ToJsonString());

        Assert.NotNull(ClientDeRelais.LireLeRecu(EnveloppeSignee.Signer(octets, cle), noeud, "abc", "00"));
        Assert.Null(ClientDeRelais.LireLeRecu(EnveloppeSignee.Signer(octets, autre), noeud, "abc", "00"));
        Assert.Null(ClientDeRelais.LireLeRecu(EnveloppeSignee.Signer(octets, cle), noeud, "abc", "01"));
    }

    [Fact]
    public async Task Un_relais_en_echec_est_ecarte_et_passe_en_dernier()
    {
        using var essai = new Essai();
        var essais = new List<string>();
        var reseau = new FauxReseau(requete =>
        {
            essais.Add(requete.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var maintenant = new DateTime(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc);
        var cartes = essai.Carte(reseau, ("r1", "a"), ("r2", "b"));
        var client = new ClientDeRelais(reseau, cartes, null, () => maintenant);
        var relais = cartes.Actuelle.AvecLeRole("relay").ToList();
        var premier = client.Ordre(relais)[0];
        reseau.Repondre = requete =>
        {
            essais.Add(requete.RequestUri!.Host);
            // Le premier essaye repond 503 (ecarte) ; le second ne repond pas du tout.
            return requete.RequestUri!.Host.StartsWith(premier.Nom, StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : throw new HttpRequestException("injoignable");
        };

        var issue = await client.EnvoyerAsync("GET", "scores/profile", "rom_group=dino", null, "code", null, false, null, CancellationToken.None);

        Assert.Null(issue.Reponse);
        Assert.Equal(2, essais.Count);
        // Les deux sont ecartes cinq minutes : ils passent apres un noeud sain, et reviennent ensuite.
        Assert.Equal(2, client.Ordre(relais).Count);
        maintenant = maintenant.AddMinutes(6);
        Assert.Equal(2, client.Ordre(relais).Count);
    }

    // ── Ce que la borne en fait ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(502, "", true)]
    [InlineData(504, "{\"ok\":false}", true)]
    [InlineData(500, "<html>erreur</html>", true)]
    [InlineData(503, "{\"ok\":false,\"error\":\"temporarily_unavailable\"}", false)]
    [InlineData(200, "<html>portail</html>", false)]
    [InlineData(404, "", false)]
    public void Le_central_muet_se_reconnait(int statut, string corps, bool muet)
        => Assert.Equal(muet, NelfePlayScoringReporter.CentralMuet(statut, corps));

    [Fact]
    public void Le_ticket_du_lancement_ne_vaut_que_s_il_expire_apres_la_partie()
    {
        var ticket = JsonNode.Parse("{\"expires_at\":\"2026-10-08T22:00:00Z\"}");
        var fin = new DateTime(2026, 10, 8, 21, 30, 0, DateTimeKind.Utc);

        Assert.True(NelfePlayScoringReporter.TicketValablePour(ticket, fin));
        Assert.False(NelfePlayScoringReporter.TicketValablePour(ticket, fin.AddHours(1)));
        Assert.False(NelfePlayScoringReporter.TicketValablePour(null, fin));
    }

    [Fact]
    public void Les_recus_d_une_partie_vivent_a_cote_de_son_brouillon_et_partent_avec_lui()
    {
        var dossier = Directory.CreateTempSubdirectory("brouillons-");
        try
        {
            var file = new FileDesBrouillons(dossier.FullName);
            var brouillon = new JsonObject { ["schema"] = BrouillonDeScore.Schema, ["id"] = "p1", ["fin_le"] = "2026-10-08T21:00:00.000Z" };
            Assert.True(file.Poser(brouillon));
            Directory.CreateDirectory(Path.GetDirectoryName(file.FichierDesRecus("p1"))!);
            File.WriteAllText(file.FichierDesRecus("p1"), "[]");

            Assert.Single(file.EnAttente());
            file.Retirer("p1");

            Assert.False(File.Exists(file.FichierDesRecus("p1")));
            Assert.Empty(file.EnAttente());
        }
        finally
        {
            dossier.Delete(true);
        }
    }

    /// <summary>Un reseau d'essai : chaque requete recoit la reponse que la fonction donne.</summary>
    private sealed class FauxReseau(Func<HttpRequestMessage, HttpResponseMessage> repondre) : IHttpClientFactory
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Repondre { get; set; } = repondre;

        public HttpClient CreateClient(string name) => new(new Gestionnaire(this));

        private sealed class Gestionnaire(FauxReseau reseau) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(reseau.Repondre(request));
        }
    }
}
