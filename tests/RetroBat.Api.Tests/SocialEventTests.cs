using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Social;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le contrat des événements sociaux signés (LOT R9 ; CDC §58, §70).
///
/// Ce qui se joue ici n'est pas de la validation d'entrée : c'est la seule raison pour laquelle
/// une borne peut afficher la réaction de quelqu'un qu'elle ne connaît pas, reçue d'un pair
/// qu'elle n'a aucune raison de croire. Le vecteur est produit par l'implémentation PHP de la
/// plateforme, donc ces tests prouvent aussi que les deux langages canonicalisent pareil. Si un
/// jour ils divergent, c'est ici qu'on l'apprend, et pas sur une borne en salle.
/// </summary>
public sealed class SocialEventTests
{
    private static readonly (string Pem, string KeyId, List<SocialEvent> Events) Vecteur = Charger();

    private static (string, string, List<SocialEvent>) Charger()
    {
        var chemin = Path.Combine(AppContext.BaseDirectory, "social-events-vector.json");
        var doc = JsonNode.Parse(File.ReadAllText(chemin))!.AsObject();
        var events = new List<SocialEvent>();
        foreach (var item in doc["events"]!.AsArray())
        {
            var e = SocialEvent.FromJson(item);
            Assert.NotNull(e);
            events.Add(e!);
        }
        return (doc["issuer_public_key"]!.GetValue<string>(),
                doc["issuer_key_id"]!.GetValue<string>(), events);
    }

    [Fact]
    public void L_empreinte_de_la_cle_se_recalcule_depuis_le_PEM()
    {
        var (spki, keyId) = SocialEventVerifier.FromPem(Vecteur.Pem);
        Assert.NotEmpty(spki);
        // Les deux implémentations doivent tomber sur le MÊME key_id, sinon l'épinglage ne
        // reconnaîtrait jamais la clé que la plateforme annonce.
        Assert.Equal(Vecteur.KeyId, keyId);
    }

    [Fact]
    public void Un_evenement_signe_par_la_plateforme_est_accepte_ici()
    {
        var (spki, keyId) = SocialEventVerifier.FromPem(Vecteur.Pem);
        foreach (var e in Vecteur.Events)
        {
            Assert.Equal(string.Empty, SocialEventVerifier.Check(e, spki, keyId));
        }
    }

    [Fact]
    public void L_ordre_des_cles_au_transport_est_indifferent()
    {
        var (spki, keyId) = SocialEventVerifier.FromPem(Vecteur.Pem);
        var origine = Vecteur.Events[0];

        // Le corps arrive dans l'ordre du producteur, jamais dans l'ordre canonique : on le
        // remet à l'envers pour s'en assurer. Sans cette propriété, tout proxy JSON qui
        // réordonne casserait chaque signature en transit.
        var inverse = new JsonObject();
        foreach (var paire in origine.Body.Reverse()) inverse[paire.Key] = paire.Value?.DeepClone();
        var remonte = SocialEvent.FromJson(new JsonObject
        {
            ["event_id"] = origine.EventId,
            ["body"] = inverse,
            ["signature"] = origine.Signature,
        })!;

        Assert.Equal(string.Empty, SocialEventVerifier.Check(remonte, spki, keyId));
    }

    [Theory]
    [InlineData("frame", 12L)]
    [InlineData("session_seq", 99L)]
    public void Retoucher_le_corps_casse_la_signature(string champ, long valeur)
    {
        var (spki, keyId) = SocialEventVerifier.FromPem(Vecteur.Pem);
        var falsifie = Falsifier(Vecteur.Events[0], champ, JsonValue.Create(valeur));
        Assert.Equal("signature_invalid", SocialEventVerifier.Check(falsifie, spki, keyId));
    }

    [Fact]
    public void Changer_la_reaction_casse_la_signature()
    {
        var (spki, keyId) = SocialEventVerifier.FromPem(Vecteur.Pem);
        var falsifie = Falsifier(Vecteur.Events[0], "reaction", JsonValue.Create("rage"));
        Assert.Equal("signature_invalid", SocialEventVerifier.Check(falsifie, spki, keyId));
    }

    [Fact]
    public void Un_evenement_signe_par_une_autre_cle_est_refuse()
    {
        // LE point de sécurité de la distribution : n'importe qui peut fabriquer une paire de
        // clés et signer ce qu'il veut. Ce qui distingue un événement authentique n'est pas
        // d'être signé, c'est d'être signé par CETTE clé-là.
        var (spki, _) = SocialEventVerifier.FromPem(Vecteur.Pem);
        var autreKeyId = new string('f', 64);
        Assert.Equal("issuer_unexpected",
            SocialEventVerifier.Check(Vecteur.Events[0], spki, autreKeyId));
    }

    [Fact]
    public void Un_identifiant_qui_ne_tombe_pas_sur_le_corps_est_refuse()
    {
        var (spki, keyId) = SocialEventVerifier.FromPem(Vecteur.Pem);
        var origine = Vecteur.Events[0];
        var maquille = SocialEvent.FromJson(new JsonObject
        {
            ["event_id"] = new string('c', 64),
            ["body"] = origine.Body.DeepClone(),
            ["signature"] = origine.Signature,
        })!;
        Assert.Equal("event_id_mismatch", SocialEventVerifier.Check(maquille, spki, keyId));
    }

    [Fact]
    public void La_derniere_seance_supplante_la_precedente_et_le_retrait_ne_compte_plus()
    {
        // Le vecteur porte : un acteur avec deux séances (frames 100 puis 900), un deuxième avec
        // une seule (frame 500), un troisième (frame 700) retiré par un événement de modération.
        var converge = ReplaySocialStore.Converge("rp_test_r9", Vecteur.Events);

        Assert.Equal(2, converge.Count);
        Assert.Equal(new long[] { 500, 900 }, converge.Select(r => r.Frame).ToArray());

        // La séance ancienne du premier acteur a disparu de l'affichage sans être effacée du
        // journal : c'est ce qui plafonne son total à cinq quel que soit le nombre de visionnages.
        Assert.DoesNotContain(converge, r => r.Frame == 100);
        // Le retrait ne réécrit rien non plus, il cesse simplement d'être compté.
        Assert.DoesNotContain(converge, r => r.Frame == 700);
    }

    [Fact]
    public void L_ordre_d_arrivee_ne_change_rien()
    {
        // La convergence est ce qui permet à deux bornes d'afficher la même chose sans se parler.
        // Elle ne vaut que si l'ordre de réception est indifférent.
        var melange = Vecteur.Events.AsEnumerable().Reverse().ToList();
        Assert.Equal(
            ReplaySocialStore.Converge("rp_test_r9", Vecteur.Events).Select(r => r.Frame),
            ReplaySocialStore.Converge("rp_test_r9", melange).Select(r => r.Frame));
    }

    [Fact]
    public void Nos_propres_reactions_ne_sont_pas_comptees_deux_fois()
    {
        // Une réaction faite ici remonte, se fait signer, et nous revient dans le flux. Sans
        // dédoublonnage elle apparaîtrait en double, et la carte de chaleur mentirait sur un
        // record.
        var distantes = ReplaySocialStore.Converge("rp_test_r9", Vecteur.Events);
        var doublon = distantes.First(r => r.Frame == 900);
        var locales = new List<ReplayReaction>
        {
            new("rp_test_r9", doublon.Reaction, doublon.Level, doublon.Frame, 1_000, "fr", false, "Vincent"),
        };

        var affiche = ReplaySocialStore.Fusionner(locales, distantes);

        Assert.Equal(2, affiche.Count);
        // Sur collision, c'est la version LOCALE qui reste : elle seule porte un nom à afficher.
        Assert.Equal("Vincent", affiche.Single(r => r.Frame == 900).Author);
    }

    [Fact]
    public void Une_forme_invalide_est_refusee_avant_toute_crypto()
    {
        var corps = (JsonObject)Vecteur.Events[0].Body.DeepClone();
        corps["actor"] = "vincent";      // un pseudonyme est 32 caractères hexadécimaux
        Assert.Equal("actor_invalid", SocialEventVerifier.CheckBody(corps));

        corps = (JsonObject)Vecteur.Events[0].Body.DeepClone();
        corps["kind"] = "applause";
        Assert.Equal("kind_unknown", SocialEventVerifier.CheckBody(corps));

        corps = (JsonObject)Vecteur.Events[0].Body.DeepClone();
        corps["schema"] = "nelfe.social.event.v2";
        Assert.Equal("schema_unknown", SocialEventVerifier.CheckBody(corps));
    }

    private static SocialEvent Falsifier(SocialEvent origine, string champ, JsonNode valeur)
    {
        // On retouche le corps ET on recalcule l'identifiant : sinon le refus viendrait du simple
        // désaccord entre les deux et ne prouverait rien de la signature. Un faussaire prendrait
        // évidemment la peine de refaire le hash.
        var corps = (JsonObject)origine.Body.DeepClone();
        corps[champ] = valeur;
        var canonique = RetroBat.Api.Scoring.Jcs.Canonical(corps);
        var id = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonique)))
            .ToLowerInvariant();
        return SocialEvent.FromJson(new JsonObject
        {
            ["event_id"] = id,
            ["body"] = corps,
            ["signature"] = origine.Signature,
        })!;
    }
}
