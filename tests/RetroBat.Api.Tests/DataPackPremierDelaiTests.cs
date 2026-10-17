using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La premiere synchronisation du Data Pack (2026-10-18) : une borne en retard se synchronise vite apres le demarrage,
/// une borne a jour attend son echeance. Une API relancee souvent ne doit plus rester des jours sans synchronisation.
/// </summary>
public sealed class DataPackPremierDelaiTests
{
    private static readonly DateTime Maintenant = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Jour = TimeSpan.FromHours(24);
    private static readonly TimeSpan Demarrage = TimeSpan.FromSeconds(120);

    [Fact]
    public void Jamais_synchronisee_trente_secondes()
        => Assert.Equal(TimeSpan.FromSeconds(30), DataPackSyncService.PremierDelai(default, Maintenant, Jour, Demarrage));

    [Fact]
    public void Quatre_jours_de_retard_trente_secondes()
        => Assert.Equal(TimeSpan.FromSeconds(30), DataPackSyncService.PremierDelai(Maintenant.AddDays(-4), Maintenant, Jour, Demarrage));

    [Fact]
    public void A_jour_on_attend_l_echeance()
        => Assert.Equal(TimeSpan.FromHours(20), DataPackSyncService.PremierDelai(Maintenant.AddHours(-4), Maintenant, Jour, Demarrage));

    [Fact]
    public void Jamais_avant_le_delai_de_demarrage()
        => Assert.Equal(Demarrage, DataPackSyncService.PremierDelai(Maintenant.AddHours(-24).AddSeconds(10), Maintenant, Jour, Demarrage));

    [Fact]
    public void Un_delai_de_demarrage_court_reste_court()
        => Assert.Equal(TimeSpan.FromSeconds(5), DataPackSyncService.PremierDelai(default, Maintenant, Jour, TimeSpan.FromSeconds(5)));

    [Fact]
    public void Une_horloge_qui_recule_compte_comme_un_retard()
        => Assert.Equal(TimeSpan.FromSeconds(30), DataPackSyncService.PremierDelai(Maintenant.AddHours(3), Maintenant, Jour, Demarrage));
}
