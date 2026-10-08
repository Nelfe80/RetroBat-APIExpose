using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Social;
using RetroBat.Api.Replay.Storage;
using RetroBat.Api.Reseau;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les replays sur les noeuds, cote borne (CDC infra §15.7) : le rendez-vous (meme vecteur que NelfeNode), ou
/// chercher, quoi deposer ; et les cles d'emetteur apprises par la carte (§15.10).
/// </summary>
public class ReplayNodesTests : IDisposable
{
    private readonly DirectoryInfo _dossier = Directory.CreateTempSubdirectory("noeuds-replays-");
    private readonly ECDsa _site = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _emetteur = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _ancienEmetteur = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _site.Dispose();
        _emetteur.Dispose();
        _ancienEmetteur.Dispose();
        try { _dossier.Delete(true); } catch (IOException) { }
    }

    private static NoeudDuReseau Noeud(string nom, char cle, int poids, params string[] roles)
        => new(1, nom, "node", $"https://{nom}.exemple", new ClePublique(new string(cle, 64), [], ""), roles, "", "", "h-" + nom, poids);

    /// <summary>
    /// Le vecteur commun avec NelfeNode (ReplaysTests.Le_rendez_vous_est_le_vecteur_commun) : memes entrees, meme
    /// ordre. Si l'un change, l'autre doit changer avec lui.
    /// </summary>
    [Fact]
    public void Le_rendez_vous_est_celui_des_noeuds()
    {
        var sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        NoeudDuReseau[] noeuds =
        [
            Noeud("a", 'a', 1, "front", "replay"), Noeud("b", 'b', 1, "replay"), Noeud("c", 'c', 3, "replay"), Noeud("d", 'd', 1, "front"),
        ];

        Assert.Equal(new[] { "c", "b" }, Rendezvous.Proprietaires(sha, noeuds, 2).Select(n => n.Nom));
        Assert.DoesNotContain("d", Rendezvous.Proprietaires(sha, noeuds, 4).Select(n => n.Nom));
    }

    /// <summary>Une carte d'essai signee, avec ces noeuds « replay » et l'historique des cles d'emetteur.</summary>
    private ServiceDeCarte Carte(IHttpClientFactory reseau, params string[] noeuds)
    {
        var liste = new JsonArray();
        var id = 1;
        foreach (var nom in noeuds)
        {
            using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            liste.Add(new JsonObject
            {
                ["id"] = id++, ["name"] = nom, ["kind"] = "node", ["url"] = $"https://{nom}.exemple",
                ["key_id"] = Crypto.KeyId(cle.ExportSubjectPublicKeyInfo()), ["public_key"] = cle.ExportSubjectPublicKeyInfoPem(),
                ["roles"] = new JsonArray("front", "replay"), ["region"] = "", ["country"] = "", ["host"] = "h-" + nom, ["weight"] = 1,
            });
        }
        using var scellement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var carte = new JsonObject
        {
            ["format"] = 1, ["kind"] = "nelfeplay-map", ["version"] = 9, ["issued_at"] = "2026-10-08T00:00:00Z",
            ["central"] = new JsonObject
            {
                ["url"] = "https://nelfeplay.com", ["seal_key"] = scellement.ExportSubjectPublicKeyInfoPem(),
                ["seal_key_id"] = Crypto.KeyId(scellement.ExportSubjectPublicKeyInfo()),
                ["verdict_keys"] = new JsonArray(_emetteur.ExportSubjectPublicKeyInfoPem()),
                ["social_keys"] = new JsonArray(_emetteur.ExportSubjectPublicKeyInfoPem(), _ancienEmetteur.ExportSubjectPublicKeyInfoPem()),
            },
            ["replay"] = new JsonObject { ["copies"] = 2 },
            ["nodes"] = liste,
        };
        var service = new ServiceDeCarte(reseau, null, Path.Combine(_dossier.FullName, "carte-" + Guid.NewGuid().ToString("N") + ".json"),
            ClePublique.DepuisPem(_site.ExportSubjectPublicKeyInfoPem())!, () => "https://nelfeplay.com");
        Assert.True(service.Proposer(EnveloppeSignee.Signer(Encoding.UTF8.GetBytes(carte.ToJsonString()), _site).ToJsonString(), "essai"));
        return service;
    }

    private static IConfiguration Reglages(params (string Cle, string? Valeur)[] valeurs) =>
        new ConfigurationBuilder().AddInMemoryCollection(valeurs.Select(v => new KeyValuePair<string, string?>(v.Cle, v.Valeur))).Build();

    [Fact]
    public async Task Les_noeuds_replay_de_la_carte_sont_des_sources_et_seuls_les_proprietaires_sont_demandes()
    {
        var reseau = new FauxReseau(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = new NodePeerSource(Carte(reseau, "r1", "r2", "r3"), Reglages());
        var sha = Crypto.Sha256Hex("un replay");

        var pairs = await source.DiscoverAsync(CancellationToken.None);

        Assert.Equal(3, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(NodePeerSource.SourceTag, p.Source));
        Assert.Equal("https://r1.exemple/replays/v1/objets/{sha}.replay", pairs[0].UrlTemplate);
        var voisin = new ReplayPeer("voisin", "http://192.168.1.20:12345", null, "lan");
        var demandes = source.Filtrer([voisin, .. pairs], sha);
        Assert.Contains(voisin, demandes);
        Assert.Equal(2, demandes.Count(p => p.Source == NodePeerSource.SourceTag));
        Assert.Equal(source.Proprietaires(sha).Select(n => n.Url).Order(), demandes.Where(p => p.Source == NodePeerSource.SourceTag).Select(p => p.BaseUrl).Order());
        Assert.Empty(await new NodePeerSource(Carte(reseau, "r1"), Reglages(("Replay:Share:NodesEnabled", "false"))).DiscoverAsync(CancellationToken.None));
    }

    private async Task<string> Enregistrer(ReplayStore magasin, string id, string contenu, string visibilite)
    {
        var fichier = Path.Combine(_dossier.FullName, id + ".replay");
        await File.WriteAllTextAsync(fichier, contenu);
        var objet = await magasin.ImportObjectAsync(fichier, CancellationToken.None);
        magasin.SaveManifest(new ReplayManifest(ReplayManifest.SchemaId, id, "s-" + id, new ReplayGame("dino", "arcade", "dino", "1cc", null),
            DateTime.UtcNow, "local", new ReplayRuntime("retroarch", "1.22.2", null, null, null, null, "bsv2"), objet,
            new ReplayFrames(0, null, null, 100, 60), null, new ReplayRecovery(false)));
        magasin.SaveMeta(ReplayLocalMetadata.Fresh(id) with { Visibility = visibilite });
        return objet.Sha256;
    }

    [Fact]
    public async Task Une_borne_depose_ses_replays_publics_listes_et_rien_d_autre()
    {
        var magasin = new ReplayStore(NullLogger<ReplayStore>.Instance, Path.Combine(_dossier.FullName, "magasin"));
        var publicListe = await Enregistrer(magasin, "rp_public", "replay public d'un score publie", "public");
        var publicNonListe = await Enregistrer(magasin, "rp_attente", "replay public, score pas encore publie", "public");
        var prive = await Enregistrer(magasin, "rp_prive", "replay prive", "private");
        var deposes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var enJeu = false;
        var reseau = new FauxReseau(requete =>
        {
            var chemin = requete.RequestUri!.AbsolutePath;
            if (chemin == "/api/v1/nodes/replays")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"format\":1,\"objects\":[{\"sha256\":\"" + publicListe + "\",\"size\":null,\"seeded\":false,\"urls\":[]}]}"),
                };
            var cle = requete.RequestUri.Host + chemin;
            if (requete.Method == HttpMethod.Head)
                return new HttpResponseMessage(deposes.ContainsKey(cle.Replace("/objets/", "/depot/")) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (requete.Method == HttpMethod.Put)
            {
                deposes[cle] = requete.Content!.ReadAsByteArrayAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new ReplayNodeDepositService(new NodePeerSource(Carte(reseau, "r1", "r2"), Reglages()), magasin, magasin, magasin,
            reseau, Reglages(), null, null, () => enJeu);

        enJeu = true;
        Assert.Equal(new ReplayNodeDepositService.Bilan(0, 0, 0, 0), await service.PasserAsync(CancellationToken.None));
        enJeu = false;
        var premier = await service.PasserAsync(CancellationToken.None);

        // Deux noeuds, deux copies : le replay public liste part chez les deux ; les deux autres ne partent pas.
        Assert.Equal(new ReplayNodeDepositService.Bilan(2, 0, 0, 2), premier);
        Assert.Equal(2, deposes.Count);
        Assert.All(deposes.Keys, k => Assert.EndsWith("/replays/v1/depot/" + publicListe + ".replay.gz", k));
        using var gz = new GZipStream(new MemoryStream(deposes.Values.First()), CompressionMode.Decompress);
        using var brut = new MemoryStream();
        gz.CopyTo(brut);
        Assert.Equal(publicListe, Crypto.Sha256Hex(brut.ToArray()));
        Assert.DoesNotContain(deposes.Keys, k => k.Contains(publicNonListe) || k.Contains(prive));

        // Au passage suivant, ils y sont deja.
        Assert.Equal(new ReplayNodeDepositService.Bilan(0, 2, 0, 2), await service.PasserAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sans_la_liste_du_central_rien_ne_part()
    {
        var magasin = new ReplayStore(NullLogger<ReplayStore>.Instance, Path.Combine(_dossier.FullName, "magasin2"));
        await Enregistrer(magasin, "rp_public", "replay public", "public");
        var envois = 0;
        var reseau = new FauxReseau(requete =>
        {
            if (requete.Method == HttpMethod.Put) envois++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var service = new ReplayNodeDepositService(new NodePeerSource(Carte(reseau, "r1"), Reglages()), magasin, magasin, magasin,
            reseau, Reglages(), null, null, () => false);

        Assert.Equal(new ReplayNodeDepositService.Bilan(0, 0, 0, 1), await service.PasserAsync(CancellationToken.None));
        Assert.Equal(0, envois);
    }

    // ── Les cles d'emetteur par la carte (changement de cle sans rien effacer) ──────────────────────

    [Fact]
    public void La_carte_publie_les_cles_qui_relisent_les_evenements_sociaux()
    {
        var reseau = new FauxReseau(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var carte = Carte(reseau, "r1").Actuelle;

        Assert.Equal(Crypto.KeyId(_emetteur.ExportSubjectPublicKeyInfo()), carte.ClesDeVerdict.Single().KeyId);
        Assert.Equal(2, carte.ClesSociales.Count);
        Assert.Contains(carte.ClesSociales, c => c.KeyId == Crypto.KeyId(_ancienEmetteur.ExportSubjectPublicKeyInfo()));
        // Une carte sans historique (celle livree, ou d'avant) : les cles sociales sont celles des verdicts.
        Assert.Equal(CarteDuReseau.ParDefaut.ClesDeVerdict.Single().KeyId, CarteDuReseau.ParDefaut.ClesSociales.Single().KeyId);
    }

    [Fact]
    public void Un_evenement_social_se_verifie_avec_la_cle_qu_il_nomme()
    {
        var reseau = new FauxReseau(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var magasin = new ReplayStore(NullLogger<ReplayStore>.Instance, Path.Combine(_dossier.FullName, "magasin3"));
        var epingle = new SocialIssuerPin(magasin, reseau, Reglages(), NullLogger<SocialIssuerPin>.Instance, Carte(reseau, "r1"));
        var enService = Crypto.KeyId(_emetteur.ExportSubjectPublicKeyInfo());
        var ancienne = Crypto.KeyId(_ancienEmetteur.ExportSubjectPublicKeyInfo());

        // Rien d'epingle : la cle en service de la carte en tient lieu.
        Assert.Equal(enService, epingle.Current?.KeyId);
        Assert.Equal(enService, epingle.Pour(enService)?.KeyId);
        // Un evenement signe par l'ancienne cle se relit avec elle.
        Assert.Equal(ancienne, epingle.Pour(ancienne)?.KeyId);
        // Une cle que ni l'epinglage ni la carte ne connaissent : la verification echouera sur la cle en service.
        Assert.Equal(enService, epingle.Pour(new string('0', 64))?.KeyId);
    }

    private sealed class FauxReseau(Func<HttpRequestMessage, HttpResponseMessage> repondre) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Gestionnaire(repondre));

        private sealed class Gestionnaire(Func<HttpRequestMessage, HttpResponseMessage> repondre) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(repondre(request));
        }
    }
}
