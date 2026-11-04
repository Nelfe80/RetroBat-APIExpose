using System.Runtime.Versioning;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE SON D'ES PENDANT UNE PARTIE MULTIPLAYER (26/10) : coupe au lancement, rendu au premier appui apres la partie. Hors d'une
/// partie MULTIPLAYER, ES n'est jamais laisse muet (31/10) : au demarrage, au retour d'un jeu, a chaque activite dans ES.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SonDEmulationStationTests
{
    private readonly SimpleEventBus _bus = new();
    private static readonly TimeSpan Vite = TimeSpan.FromMilliseconds(20);
    private volatile bool _jeu = true;

    private SonDEmulationStation Son(FauxMelangeur melangeur, TimeSpan? auPlusTard = null, TimeSpan? sansJeuAuDepart = null)
        => new(melangeur, TimeSpan.FromMilliseconds(10), _bus, journal: null, periode: Vite, auPlusTard: auPlusTard ?? TimeSpan.FromMinutes(5),
            jeuEnCours: () => _jeu, absenceDuJeu: TimeSpan.FromMilliseconds(100), sansJeuAuDepart: sansJeuAuDepart ?? TimeSpan.FromMinutes(5));

    [Fact]
    public void Une_partie_coupe_la_musique_et_les_videos_d_ES()
    {
        var melangeur = new FauxMelangeur(("musique", false), ("video", false));
        using var son = Son(melangeur);

        son.Couper();

        Assert.True(melangeur.Muette("musique"));
        Assert.True(melangeur.Muette("video"));
        Assert.True(son.Coupe);
    }

    [Fact]
    public async Task Le_son_revient_au_premier_appui_apres_la_partie_jamais_pendant()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur);
        son.Couper();

        // Pendant la partie, ni les appuis du joueur ni l'activite d'ES ne rendent rien.
        await Appuyer();
        await _bus.PublishAsync(new EventEnvelope { Type = "ui.game.selected.raw" });
        await Task.Delay(100);
        Assert.True(melangeur.Muette("musique"));

        son.RendreAuProchainAppui();
        Assert.True(melangeur.Muette("musique"));
        await Appuyer();
        await Attendre(() => !melangeur.Muette("musique"));

        Assert.False(son.Coupe);
    }

    [Fact]
    public async Task Sans_appui_le_son_revient_quand_meme_apres_la_partie()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur, TimeSpan.FromMilliseconds(100));
        son.Couper();

        son.RendreAuProchainAppui();

        await Attendre(() => !melangeur.Muette("musique"));
        Assert.False(son.Coupe);
    }

    [Fact]
    public async Task Une_revanche_avant_le_premier_appui_garde_ES_muet()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur);
        son.Couper();
        son.RendreAuProchainAppui();
        son.Couper();

        await Appuyer();
        await Task.Delay(100);

        Assert.True(melangeur.Muette("musique"));
        Assert.True(son.Coupe);
    }

    [Fact]
    public void Une_partie_qui_ne_part_pas_rend_le_son_tout_de_suite()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur);
        son.Couper();

        son.Rendre();

        Assert.False(melangeur.Muette("musique"));
    }

    [Fact]
    public void Au_demarrage_ES_retrouve_le_son_que_Windows_avait_garde_coupe()
    {
        var melangeur = new FauxMelangeur(("musique", true));

        using var son = Son(melangeur);

        Assert.False(melangeur.Muette("musique"));
        Assert.False(son.Coupe);
    }

    [Fact]
    public async Task Au_demarrage_le_son_revient_des_que_la_session_d_ES_arrive()
    {
        var melangeur = new FauxMelangeur();
        using var son = Son(melangeur);

        melangeur.Ajouter("musique", true);

        await Attendre(() => !melangeur.Muette("musique"));
    }

    [Fact]
    public async Task Une_activite_dans_ES_rend_le_son()
    {
        foreach (var activite in new[] { "panel.input.pressed", "ui.system.selected.raw", "ui.game.selected.raw" })
        {
            var melangeur = new FauxMelangeur(("musique", false));
            using var son = Son(melangeur);
            melangeur.Muette(SonDEmulationStation.Processus, "musique", true);

            await _bus.PublishAsync(new EventEnvelope { Type = activite });

            await Attendre(() => !melangeur.Muette("musique"));
        }
    }

    [Fact]
    public async Task Au_retour_d_un_jeu_lance_par_ES_le_son_revient_des_que_sa_session_rouvre()
    {
        var melangeur = new FauxMelangeur();
        using var son = Son(melangeur);
        await Task.Delay(60);
        melangeur.Ajouter("autre", false);
        await Task.Delay(60);

        await _bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended.raw" });
        melangeur.Ajouter("musique", true);

        await Attendre(() => !melangeur.Muette("musique"));
    }

    [Fact]
    public async Task Un_jeu_lance_par_l_API_garde_ES_muet_tant_qu_il_tourne()
    {
        // Le panneau des defis (01/11) rendait le son des le lancement : la musique d'ES revenait au premier appui en jeu.
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur);
        son.Couper();

        await Appuyer();
        await Task.Delay(400);
        Assert.True(melangeur.Muette("musique"));

        // Le jeu est ferme : le son revient au premier appui.
        _jeu = false;
        await Task.Delay(400);
        Assert.True(melangeur.Muette("musique"));
        await Appuyer();
        await Attendre(() => !melangeur.Muette("musique"));
        Assert.False(son.Coupe);
    }

    [Fact]
    public async Task Un_lancement_sans_jeu_ne_laisse_pas_ES_muet()
    {
        _jeu = false;
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur, auPlusTard: TimeSpan.FromMilliseconds(100), sansJeuAuDepart: TimeSpan.FromMilliseconds(200));
        son.Couper();
        Assert.True(melangeur.Muette("musique"));

        await Attendre(() => !melangeur.Muette("musique"));
        Assert.False(son.Coupe);
    }

    [Fact]
    public async Task Une_relance_du_jeu_garde_ES_muet()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = Son(melangeur);
        son.Couper();
        await Task.Delay(60);

        // Le lanceur d'une course se ferme, celui de la suivante part dans la foulee.
        _jeu = false;
        await Task.Delay(40);
        _jeu = true;
        await Task.Delay(300);
        await Appuyer();
        await Task.Delay(100);

        Assert.True(melangeur.Muette("musique"));
        Assert.True(son.Coupe);
    }

    /// <summary>Le vrai melangeur, en lecture seule : outil de mise au point, hors des passes ordinaires.</summary>
    [Fact]
    public void Le_melangeur_de_Windows_lit_les_sessions_d_ES()
    {
        if (Environment.GetEnvironmentVariable("NELFEPLAY_MELANGEUR") is not { Length: > 0 } sortie)
        {
            return;
        }

        var sessions = new MelangeurDeWindows().De(SonDEmulationStation.Processus);
        File.WriteAllLines(sortie, sessions.Select(s => $"{s.Muette} {s.Id}"));
    }

    private Task Appuyer() => _bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed" });

    private static async Task Attendre(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class FauxMelangeur(params (string Id, bool Muette)[] sessions) : ISessionsDuMelangeur
    {
        private readonly Dictionary<string, bool> _sessions = sessions.ToDictionary(s => s.Id, s => s.Muette);

        public void Ajouter(string id, bool muette)
        {
            lock (_sessions)
            {
                _sessions[id] = muette;
            }
        }

        public bool Muette(string id)
        {
            lock (_sessions)
            {
                return _sessions[id];
            }
        }

        public IReadOnlyList<SessionDuMelangeur> De(string processus)
        {
            lock (_sessions)
            {
                return _sessions.Select(s => new SessionDuMelangeur(s.Key, s.Value)).ToList();
            }
        }

        public void Muette(string processus, string id, bool muette)
        {
            lock (_sessions)
            {
                _sessions[id] = muette;
            }
        }
    }
}
