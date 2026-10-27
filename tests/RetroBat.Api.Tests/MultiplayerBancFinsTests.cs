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
    /// Yu Yu Hakusho (BATTLE ROYALE a deux) : la vie de P2 ramenee a 1 (jamais quand elle est deja a 0 : le jeu le
    /// ressusciterait avant le K.O.), P1 avance et frappe ; au K.O., la fin du combat (0x8300) passe a 1 et la vie restante
    /// designe P1.
    /// </summary>
    [Fact]
    public async Task Yu_Yu_Hakusho_finit_au_KO_lu_par_la_vie_de_chaque_joueur()
    {
        const string Id = "megadrive/yu-yu-hakusho-makyou-toitsusen";
        if (Port is null || Fin != Id)
        {
            return;   // banc non monte
        }

        var ra = new CommandesRetroArchUdp(int.Parse(Port, CultureInfo.InvariantCulture));
        var jeu = DuCatalogue(Id);
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var guet = new MultiplayerFinDeRencontre(ra).AttendreLeScoreFinalAsync(jeu.MatchEnd!, [1, 2], delai.Token);
        using var manette = new MultiplayerBancTests.ManetteReseau(1);
        while (!guet.IsCompleted && !delai.IsCancellationRequested)
        {
            if (await LireAsync(ra, 0x8300) == 0 && await LireAsync(ra, 0x9078) is > 1)
            {
                await ra.EnvoyerAsync("WRITE_CORE_RAM 9078 01", CancellationToken.None);
            }

            await manette.TenirAsync(7, TimeSpan.FromMilliseconds(700));   // droite
            foreach (var bouton in new[] { 8, 0, 1 })                       // A, B, Y
            {
                await Task.Delay(400);
                await manette.TenirAsync(bouton, TimeSpan.FromMilliseconds(150));
            }

            await Task.Delay(400);
        }

        var vainqueur = await guet;
        Assert.True(vainqueur == 1, $"vainqueur {vainqueur} ; fin {await LireAsync(ra, 0x8300)}, vies {await LireAsync(ra, 0x8478)} et {await LireAsync(ra, 0x9078)}");
    }

    /// <summary>
    /// KOF '98 (equipes de trois) : depuis le choix des personnages, ou les compteurs comptent les combattants choisis
    /// (la garde 0x0500 = 2 les ignore), jusqu'aux trois K.O. de l'equipe de P2 (vie a 1, puis un coup de P1).
    /// </summary>
    [Fact]
    public async Task KOF_98_finit_quand_une_equipe_est_eliminee()
    {
        const string Id = "arcade/kof98";
        if (Port is null || Fin != Id)
        {
            return;   // banc non monte
        }

        var ra = new CommandesRetroArchUdp(int.Parse(Port, CultureInfo.InvariantCulture));
        var jeu = DuCatalogue(Id);
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(200));
        var guet = new MultiplayerFinDeRencontre(ra).AttendreLeVainqueurAsync(jeu.End!, [1, 2], delai.Token);
        using var manette = new MultiplayerBancTests.ManetteReseau(1);
        while (!guet.IsCompleted && !delai.IsCancellationRequested)
        {
            if (await LireAsync(ra, 0x500) == 8 && await LireAsync(ra, 0x8438) is > 1 and < 0x80)
            {
                await ra.EnvoyerAsync("WRITE_CORE_RAM 8438 01", CancellationToken.None);
            }

            await manette.TenirAsync(7, TimeSpan.FromMilliseconds(600));   // droite
            foreach (var bouton in new[] { 8, 0, 1, 9 })                    // A, B, Y, X
            {
                await Task.Delay(300);
                await manette.TenirAsync(bouton, TimeSpan.FromMilliseconds(150));
            }

            await Task.Delay(300);
        }

        var vainqueur = await guet;
        Assert.True(vainqueur == 1, $"vainqueur {vainqueur} ; garde {await LireAsync(ra, 0x500)}, manches {await LireAsync(ra, 0xA85F)} et {await LireAsync(ra, 0xA84C)}");
    }

    private static async Task<int?> LireAsync(CommandesRetroArchUdp ra, long adresse)
    {
        var reponse = await ra.DemanderAsync($"READ_CORE_RAM {adresse:x} 1", CancellationToken.None);
        var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return morceaux.Length >= 3 && int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
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
