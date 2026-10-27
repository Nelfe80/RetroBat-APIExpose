using System.Globalization;
using System.Text.Json;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FINS DU CATALOGUE CONTRE UN VRAI RETROARCH (27/10, banc d'essai, jamais une partie de la borne) : la definition du
/// Data Pack, lue telle quelle, et le guet de l'API. Le banc lance le jeu, charge un etat en plein combat et donne
/// NELFEPLAY_BANC_RA_PORT et NELFEPLAY_BANC_FIN (l'id du jeu) ; sinon chaque test passe sans rien faire.
/// </summary>
public class MultiplayerBancFinsTests
{
    private static readonly string? Port = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RA_PORT");
    private static readonly string? Fin = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_FIN");

    private static MultiplayerGame DuCatalogue(string id)
    {
        var chemin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "RetroBat-DataPack", "multiplayer", "catalogue.json"));
        var catalogue = JsonSerializer.Deserialize<MultiplayerCatalogue>(File.ReadAllText(chemin),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return catalogue.Games.Single(j => j.Id == id);
    }

    /// <summary>
    /// Twinkle Star Sprites (versus) : la vie de P1 tenue pleine, celle de P2 basse ; au match perdu par P2, ses victoires
    /// de match restent a 0 et celles de P1 passent a 1.
    /// </summary>
    [Fact]
    public async Task Twinkle_Star_Sprites_finit_au_premier_match_gagne()
    {
        const string Id = "arcade/twinspri";
        if (Port is null || Fin != Id)
        {
            return;   // banc non monte
        }

        var ra = new CommandesRetroArchUdp(int.Parse(Port, CultureInfo.InvariantCulture));
        var jeu = DuCatalogue(Id);
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var guet = new MultiplayerFinDeRencontre(ra).AttendreLeVainqueurAsync(jeu.End!, [1, 2], delai.Token);
        while (!guet.IsCompleted && !delai.IsCancellationRequested)
        {
            await ra.EnvoyerAsync("WRITE_CORE_RAM c015 28", CancellationToken.None);
            await ra.EnvoyerAsync("WRITE_CORE_RAM d015 04", CancellationToken.None);
            await Task.Delay(1000);
        }

        Assert.Equal(1, await guet);
    }
}
