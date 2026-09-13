using System.Linq;
using System.Reflection;
using System.Text.Json;
using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les garde-fous de la découverte silencieuse, livrés avant toute capture.
///
/// Ils ne décrivent pas une intention, ils ferment des portes : ce qui remonte ne peut pas
/// être une image, la découverte ne peut pas appeler la route qui en reçoit, et son code ne
/// peut pas nommer celui qui en produit.
/// </summary>
public class ScoringDiscoveryGuardrailTests
{
    // ── Ce qui remonte ne peut pas être une image ─────────────────────────────

    [Fact]
    public void Une_enveloppe_faite_de_nombres_et_d_empreintes_passe()
    {
        var envelope = JsonSerializer.Serialize(new
        {
            system_id = "fbneo",
            rom_group = "1942",
            content_sha256 = new string('a', 64),
            metric = new { value = 40000, kind = "score" },
            ocr = new { matched = true, confidence = 0.98, region_stability = 0.99, candidates = 1 },
            trajectory = new[] { 0, 1200, 4500, 40000 }
        });

        Assert.True(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason), reason);
    }

    [Fact]
    public void Un_champ_qui_annonce_une_image_est_refuse_par_son_nom()
    {
        var envelope = JsonSerializer.Serialize(new { rom_group = "1942", screenshot_path = "E:/shots/1942.png" });

        Assert.False(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason));
        Assert.Contains("screenshot_path", reason);
    }

    [Fact]
    public void Un_champ_nomme_pour_une_image_mais_vide_reste_lisible()
    {
        // Un compteur de captures est un nombre : il dit combien, jamais quoi.
        var envelope = JsonSerializer.Serialize(new { frames_seen = 3, screenshot = (string?)null });

        Assert.True(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason), reason);
    }

    [Fact]
    public void Un_tampon_glisse_dans_un_tableau_de_nombres_est_refuse()
    {
        var envelope = JsonSerializer.Serialize(new { rom_group = "1942", samples = Enumerable.Range(0, 5000).ToArray() });

        Assert.False(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason));
        Assert.Contains("tampon", reason);
    }

    [Fact]
    public void Une_trajectoire_de_score_de_taille_raisonnable_passe()
    {
        var envelope = JsonSerializer.Serialize(new { trajectory = Enumerable.Range(0, 500).ToArray() });

        Assert.True(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason), reason);
    }

    [Fact]
    public void Une_image_encodee_en_base64_est_refusee_meme_sous_un_nom_anodin()
    {
        var envelope = JsonSerializer.Serialize(new { note = new string('Q', 4000) + "==" });

        Assert.False(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason));
        Assert.Contains("base64", reason);
    }

    [Fact]
    public void Une_empreinte_et_une_signature_ne_sont_pas_prises_pour_des_images()
    {
        // Un SHA-256 en base64 fait 44 caractères, une signature ECDSA P-256 en fait 96.
        var envelope = JsonSerializer.Serialize(new
        {
            digest = "n4bQgYhMfWWaL+qgxVrQFaO/TxsrC4Is0V1sFbDwCgg=",
            signature = new string('B', 96) + "=="
        });

        Assert.True(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason), reason);
    }

    [Theory]
    [InlineData("E:/RetroBat/shots/1942.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    public void Un_chemin_ou_une_URL_d_image_est_refuse(string value)
    {
        var envelope = JsonSerializer.Serialize(new { detail = value });

        Assert.False(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out _));
    }

    [Fact]
    public void Un_champ_image_cache_au_fond_de_l_enveloppe_est_trouve()
    {
        var envelope = JsonSerializer.Serialize(new
        {
            session = new { runs = new[] { new { attempt = 1, evidence = new { pixels = "AAAA" } } } }
        });

        Assert.False(DiscoveryEnvelopeSchema.IsAcceptable(envelope, out var reason));
        Assert.Contains("pixels", reason);
    }

    [Fact]
    public void Un_JSON_invalide_est_refuse_plutot_qu_envoye()
    {
        Assert.False(DiscoveryEnvelopeSchema.IsAcceptable("{ pas du json", out var reason));
        Assert.Contains("JSON invalide", reason);
    }

    // ── La découverte ne peut pas appeler la route qui reçoit des images ──────

    [Theory]
    [InlineData(DiscoveryRoutes.Ticket)]
    [InlineData(DiscoveryRoutes.Sessions)]
    [InlineData("https://nelfeplay.com/api/v1/agent/scoring-discovery/sessions")]
    public void Les_deux_routes_de_la_decouverte_sont_autorisees(string url)
    {
        Assert.True(DiscoveryRoutes.IsAllowed(url, out var reason), reason);
    }

    [Theory]
    [InlineData("/api/v1/agent/scores/shot")]
    [InlineData("https://nelfeplay.com/api/v1/agent/scores/shot")]
    [InlineData("/api/v1/agent/scores/shot/")]
    [InlineData("/api/v1/agent/scores/shot?session=12")]
    [InlineData("/api/v1/agent/screenshot")]
    [InlineData("/api/v1/agent/images/upload")]
    public void Une_route_qui_recoit_des_images_est_refusee(string url)
    {
        Assert.False(DiscoveryRoutes.IsAllowed(url, out var reason));
        Assert.NotEqual(string.Empty, reason);
    }

    [Theory]
    [InlineData("/api/v1/agent/play")]
    [InlineData("/api/v1/agent/scores/submit")]
    [InlineData("")]
    public void Toute_autre_route_est_refusee_aussi(string url)
    {
        // La liste est fermée : une route inconnue ne passe pas parce qu'elle est inoffensive.
        Assert.False(DiscoveryRoutes.IsAllowed(url, out _));
    }

    // ── Le code de la découverte ne nomme pas celui des captures ──────────────

    [Fact]
    public void Le_namespace_de_la_decouverte_ignore_le_service_de_captures()
    {
        // Test d'architecture : la séparation doit survivre à quelqu'un qui, dans six mois,
        // voudrait « réutiliser » deux lignes du service de captures.
        var assembly = typeof(DiscoveryRoutes).Assembly;
        var discoveryTypes = assembly.GetTypes()
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith("RetroBat.Api.Scoring.Discovery", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(discoveryTypes);

        var offenders = discoveryTypes
            .SelectMany(NamedTypes)
            .Where(name => name.Contains("ScoreShot", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> NamedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(all))
        {
            yield return field.FieldType.FullName ?? string.Empty;
        }

        foreach (var property in type.GetProperties(all))
        {
            yield return property.PropertyType.FullName ?? string.Empty;
        }

        foreach (var method in type.GetMethods(all))
        {
            yield return method.ReturnType.FullName ?? string.Empty;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType.FullName ?? string.Empty;
            }
        }

        foreach (var constructor in type.GetConstructors(all))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType.FullName ?? string.Empty;
            }
        }
    }

    // ── L'interrupteur du joueur ──────────────────────────────────────────────

    [Fact]
    public void L_interrupteur_est_allume_par_defaut_et_les_seuils_suivent_le_CDC()
    {
        var options = new ScoringDiscoveryOptions();

        Assert.True(options.Enabled);
        Assert.Equal(300, options.StableDelayMs);
        Assert.Equal(3, options.MaxFramesPerTrigger);
        Assert.Equal(500, options.MinIntervalMs);
        Assert.Equal(60, options.MaxCapturesPerMinute);
        Assert.Equal(5, options.FrameTimeDriftMaxPercent);
    }

    [Fact]
    public void Le_plugin_Lua_n_ecoute_pas_sur_le_port_du_pont_RAM()
    {
        // 12347 est au pont RAM : deux plugins sur le même port, et l'un lirait les
        // commandes de l'autre.
        Assert.NotEqual(12347, new ScoringDiscoveryOptions().MameControlPort);
    }
}
