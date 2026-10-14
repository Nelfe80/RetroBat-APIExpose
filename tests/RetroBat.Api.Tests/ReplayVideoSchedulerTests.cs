using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le rendu des films au repos (CDC video V2, lot 1) : quand la borne filme, et quand elle repasse.</summary>
public sealed class ReplayVideoSchedulerTests
{
    private static readonly DateTime Maintenant = new(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Repos = TimeSpan.FromMinutes(15);

    [Fact]
    public void Une_borne_inactive_depuis_le_delai_de_repos_filme()
    {
        Assert.True(ReplayVideoScheduler.AuRepos(Maintenant, Maintenant.AddMinutes(-15), Repos, false, false, false));
        Assert.True(ReplayVideoScheduler.AuRepos(Maintenant, Maintenant.AddHours(-3), Repos, false, false, false));
    }

    [Fact]
    public void Une_activite_recente_une_partie_un_emulateur_ou_une_lecture_empechent_le_rendu()
    {
        var ancienne = Maintenant.AddHours(-1);
        Assert.False(ReplayVideoScheduler.AuRepos(Maintenant, Maintenant.AddMinutes(-14), Repos, false, false, false));
        Assert.False(ReplayVideoScheduler.AuRepos(Maintenant, ancienne, Repos, true, false, false));
        Assert.False(ReplayVideoScheduler.AuRepos(Maintenant, ancienne, Repos, false, true, false));
        Assert.False(ReplayVideoScheduler.AuRepos(Maintenant, ancienne, Repos, false, false, true));
    }

    [Fact]
    public void Une_station_filme_meme_quand_on_se_sert_du_pc_mais_jamais_pendant_une_partie()
    {
        const string station = ReplayVideoPublisher.RoleStation;
        var aLInstant = Maintenant.AddSeconds(-5);
        Assert.True(ReplayVideoScheduler.PeutFilmer(station, Maintenant, aLInstant, Repos, false, false, false));
        Assert.False(ReplayVideoScheduler.PeutFilmer(station, Maintenant, aLInstant, Repos, true, false, false));
        Assert.False(ReplayVideoScheduler.PeutFilmer(station, Maintenant, aLInstant, Repos, false, true, false));
        Assert.False(ReplayVideoScheduler.PeutFilmer(station, Maintenant, aLInstant, Repos, false, false, true));
    }

    [Fact]
    public void Une_borne_de_joueur_attend_le_repos()
    {
        const string joueur = ReplayVideoPublisher.RoleJoueur;
        Assert.False(ReplayVideoScheduler.PeutFilmer(joueur, Maintenant, Maintenant.AddSeconds(-5), Repos, false, false, false));
        Assert.True(ReplayVideoScheduler.PeutFilmer(joueur, Maintenant, Maintenant.AddMinutes(-20), Repos, false, false, false));
    }

    [Fact]
    public void Le_clavier_ou_la_souris_comptent_quand_ils_sont_plus_recents_que_la_borne()
    {
        var borne = Maintenant.AddHours(-2);
        // Quelqu'un a tape il y a 3 minutes : c'est la derniere activite.
        Assert.Equal(Maintenant.AddMinutes(-3),
            ReplayVideoScheduler.DerniereActivite(Maintenant, borne, TimeSpan.FromMinutes(3)));
        // Le clavier dort depuis plus longtemps que le panel : le panel fait foi.
        Assert.Equal(borne, ReplayVideoScheduler.DerniereActivite(Maintenant, borne, TimeSpan.FromHours(5)));
        // Windows ne repond pas : la borne seule.
        Assert.Equal(borne, ReplayVideoScheduler.DerniereActivite(Maintenant, borne, null));
    }

    [Fact]
    public void Rien_de_filmable_espace_les_passages()
    {
        var intervalle = TimeSpan.FromMinutes(5);
        var rien = TimeSpan.FromMinutes(60);
        var vide = new ReplayVideoPublisher.Rapport(0, 0, 0, Array.Empty<string>());
        var absents = new ReplayVideoPublisher.Rapport(3, 0, 0, new[] { "a : replay absent de cette borne" });
        var filme = new ReplayVideoPublisher.Rapport(4, 1, 1, new[] { "b : envoyee (x)" });
        var rate = new ReplayVideoPublisher.Rapport(4, 1, 0, new[] { "c : rendu plafond" });
        var refuse = new ReplayVideoPublisher.Rapport(4, 1, 0, new[] { "d : envoi refuse" }, EnvoiRefuse: true);
        Assert.Equal(rien, ReplayVideoScheduler.ProchainPassage(vide, intervalle, rien));
        Assert.Equal(rien, ReplayVideoScheduler.ProchainPassage(absents, intervalle, rien));
        Assert.Equal(intervalle, ReplayVideoScheduler.ProchainPassage(filme, intervalle, rien));
        Assert.Equal(intervalle, ReplayVideoScheduler.ProchainPassage(rate, intervalle, rien));
        Assert.Equal(rien, ReplayVideoScheduler.ProchainPassage(refuse, intervalle, rien));
        // Passage interrompu ou en erreur : le rythme normal.
        Assert.Equal(intervalle, ReplayVideoScheduler.ProchainPassage(null, intervalle, rien));
    }
}

/// <summary>Ce que le site demande a la borne : son role et la file des films.</summary>
public sealed class ReplayVideoListeTests
{
    [Fact]
    public void La_station_recoit_son_role_et_sa_file()
    {
        var liste = ReplayVideoPublisher.LireLaListe(
            """{"ok":true,"role":"station","targets":[{"replay_id":"rp_A","run_start_frame":120,"run_end_frame":9000},{"replay_id":"rp_B","run_start_frame":null,"run_end_frame":null}]}""");
        Assert.Equal(ReplayVideoPublisher.RoleStation, liste.Role);
        Assert.Equal(2, liste.Cibles.Count);
        Assert.Equal(new ReplayVideoPublisher.Cible("rp_A", 120, 9000), liste.Cibles[0]);
        Assert.Equal(new ReplayVideoPublisher.Cible("rp_B", null, null), liste.Cibles[1]);
    }

    [Fact]
    public void Une_borne_sans_role_ne_filme_rien_et_un_site_d_avant_les_roles_fait_filmer_ses_records()
    {
        var aucun = ReplayVideoPublisher.LireLaListe("""{"ok":true,"role":"none","targets":[]}""");
        Assert.Equal(ReplayVideoPublisher.RoleAucun, aucun.Role);
        Assert.Empty(aucun.Cibles);
        var avant = ReplayVideoPublisher.LireLaListe("""{"ok":true,"targets":[{"replay_id":"rp_C"}]}""");
        Assert.Equal(ReplayVideoPublisher.RoleJoueur, avant.Role);
        Assert.Single(avant.Cibles);
        // Un role inconnu reste prudent : celui d'une borne de joueur.
        Assert.Equal(ReplayVideoPublisher.RoleJoueur, ReplayVideoPublisher.LireLaListe("""{"role":"autre"}""").Role);
    }
}
