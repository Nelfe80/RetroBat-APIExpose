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
