using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La règle de routage des messages de la plateforme.
///
/// C'est elle qui décide si une nouvelle paraît maintenant, plus tard, ou par-dessus un jeu.
/// Une erreur ici éjecte un joueur de sa partie (une notification ES ramène ES au premier
/// plan) ou couvre son écran pendant qu'il joue. D'où un test sur la décision seule, sans
/// machine ni fenêtre.
/// </summary>
public class NelfePlayMessageRouterTests
{
    // ── Hors jeu : tout passe par EmulationStation ────────────────────────────

    [Theory]
    [InlineData("critical")]
    [InlineData("score")]
    [InlineData("social")]
    public void Sans_jeu_tout_passe_par_EmulationStation(string niveau)
    {
        // Le seuil ne concerne QUE l'affichage en jeu : même à `Off`, le menu reçoit.
        Assert.Equal(
            NelfePlayMessageRouter.Canal.EmulationStation,
            NelfePlayMessageRouter.Choisir(niveau, jeuEnCours: false, gameplayActif: null, InGameMessageLevel.Off));
    }

    // ── En jeu, partie engagée : rien, jamais ─────────────────────────────────

    [Theory]
    [InlineData(InGameMessageLevel.Critical)]
    [InlineData(InGameMessageLevel.Score)]
    [InlineData(InGameMessageLevel.All)]
    public void Pendant_une_partie_engagee_rien_ne_parait(InGameMessageLevel seuil)
    {
        // Même un message critique attend : il paraîtra au premier temps mort.
        Assert.Equal(
            NelfePlayMessageRouter.Canal.Attendre,
            NelfePlayMessageRouter.Choisir("critical", jeuEnCours: true, gameplayActif: true, seuil));
    }

    // ── En jeu, temps mort : la surimpression, si le niveau le permet ─────────

    [Fact]
    public void Dans_un_temps_mort_la_surimpression_prend_le_relais()
    {
        Assert.Equal(
            NelfePlayMessageRouter.Canal.Surimpression,
            NelfePlayMessageRouter.Choisir("score", jeuEnCours: true, gameplayActif: false, InGameMessageLevel.Score));
    }

    [Fact]
    public void Jamais_la_notification_ES_pendant_qu_un_jeu_tourne()
    {
        // Elle ramène ES au premier plan et éjecte le joueur (vécu le 2026-09-21).
        foreach (var actif in new bool?[] { true, false, null })
        {
            foreach (var seuil in Enum.GetValues<InGameMessageLevel>())
            {
                Assert.NotEqual(
                    NelfePlayMessageRouter.Canal.EmulationStation,
                    NelfePlayMessageRouter.Choisir("critical", jeuEnCours: true, gameplayActif: actif, seuil));
            }
        }
    }

    // ── Ne pas savoir se tranche du côté du silence ───────────────────────────

    [Fact]
    public void Un_etat_de_jeu_inconnu_impose_le_silence()
    {
        // Définition sans marqueur de cycle de vie, ou état trop vieux : on se tait plutôt que
        // de risquer d'écrire par-dessus une partie en cours.
        Assert.Equal(
            NelfePlayMessageRouter.Canal.Attendre,
            NelfePlayMessageRouter.Choisir("critical", jeuEnCours: true, gameplayActif: null, InGameMessageLevel.All));
    }

    // ── Le seuil ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(InGameMessageLevel.Off, "critical", false)]
    [InlineData(InGameMessageLevel.Off, "score", false)]
    [InlineData(InGameMessageLevel.Critical, "critical", true)]
    [InlineData(InGameMessageLevel.Critical, "score", false)]
    [InlineData(InGameMessageLevel.Critical, "social", false)]
    [InlineData(InGameMessageLevel.Score, "critical", true)]
    [InlineData(InGameMessageLevel.Score, "score", true)]
    [InlineData(InGameMessageLevel.Score, "social", false)]
    [InlineData(InGameMessageLevel.All, "social", true)]
    public void Le_seuil_decide_de_ce_qui_parait_en_jeu(InGameMessageLevel seuil, string niveau, bool attendu)
    {
        Assert.Equal(attendu, NelfePlayMessageRouter.AuDessusDuSeuil(niveau, seuil));
    }

    [Fact]
    public void Un_niveau_inconnu_est_traite_comme_le_plus_bas()
    {
        // Un message dont le niveau ne se lit pas ne doit pas forcer le passage : il attend.
        Assert.False(NelfePlayMessageRouter.AuDessusDuSeuil("", InGameMessageLevel.Score));
        Assert.False(NelfePlayMessageRouter.AuDessusDuSeuil("bavardage", InGameMessageLevel.Score));
    }

    // ── La file ──────────────────────────────────────────────────────────────

    [Fact]
    public void Un_message_deja_recu_n_entre_pas_deux_fois()
    {
        // Tant que la borne n'acquitte pas, le serveur le représente à chaque relevé.
        var routeur = Routeur();
        var m = new NelfePlayMessageRouter.Message("abc", "scoring.released", "score", "Ton score est classé.", 3600);

        routeur.Recevoir([m]);
        routeur.Recevoir([m]);

        Assert.Equal(1, routeur.EnAttente);
    }

    [Fact]
    public void Un_message_sans_texte_n_entre_pas()
    {
        var routeur = Routeur();
        routeur.Recevoir([
            new NelfePlayMessageRouter.Message("a", "k", "score", "   ", 3600),
            new NelfePlayMessageRouter.Message("", "k", "score", "du texte", 3600),
        ]);

        Assert.Equal(0, routeur.EnAttente);
    }

    [Fact]
    public void Un_message_perime_ne_se_remet_plus()
    {
        var routeur = Routeur();
        routeur.Recevoir([
            new NelfePlayMessageRouter.Message("vieux", "k", "score", "trop tard", 60)
            {
                RecuUtc = DateTime.UtcNow.AddMinutes(-5),
            },
        ]);

        Assert.Empty(routeur.Remettre());
        Assert.Equal(0, routeur.EnAttente);
    }

    [Fact]
    public void Sans_canal_disponible_le_message_reste_en_attente()
    {
        // Ni notification ES ni surimpression : rien n'est acquitté, tout revient plus tard.
        var routeur = Routeur();
        routeur.Recevoir([new NelfePlayMessageRouter.Message("a", "k", "score", "à dire", 3600)]);

        Assert.Empty(routeur.Remettre());
        Assert.Equal(1, routeur.EnAttente);
    }

    private static NelfePlayMessageRouter Routeur() => new(
        new FauxOptionsMonitor(new ApiExposeOptions()),
        es: null,
        overlay: null,
        jeu: null,
        logger: null,
        emulateurTourne: () => false);

    private sealed class FauxOptionsMonitor(ApiExposeOptions valeur)
        : Microsoft.Extensions.Options.IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = valeur;

        public ApiExposeOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ApiExposeOptions, string?> listener) => new Rien();

        private sealed class Rien : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
