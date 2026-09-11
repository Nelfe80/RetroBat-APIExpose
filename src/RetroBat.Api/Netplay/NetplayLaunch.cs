using System.Diagnostics;
using System.Text;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Lancer une partie netplay EN PASSANT PAR EMULATIONSTATION.
///
/// Quand ES lance un jeu, sa boucle principale reste BLOQUEE dans l'attente du lanceur : il ne
/// dessine plus rien tant que la partie dure. Quand c'est l'API qui lance `emulatorLauncher`, ES ne
/// sait pas qu'une partie tourne et continue de dessiner son interface derriere, video du jeu
/// selectionne et son compris. Mesure sur borne pendant un direct : onze pour cent du GPU et un
/// tiers de coeur pour ES, sur une machine dont le GPU est deja le goulot.
///
/// L'API HTTP d'ES ne prend qu'un chemin de ROM, aucune option. Mais le lanceur importe
/// `global.&lt;cle&gt;` de `es_settings.cfg` dans la meme table que ses arguments de ligne de commande
/// (Program.cs : LoadEmulationStationSettings puis LoadAll("global")), et le generateur libretro lit
/// `netplaymode`, `netplayip`, `netplayport`, `netplaysession`, `netplaypass` dans cette table. On
/// pose donc ces cles, on demande a ES de lancer, et on les EFFACE des que l'emulateur parait :
/// laissees la, chaque lancement suivant serait une partie netplay.
///
/// Si ES ne repond pas, on lance directement comme avant : mieux vaut une partie avec ES qui
/// dessine derriere qu'aucune partie.
/// </summary>
public static class NetplayLaunch
{
    private static readonly Uri EmulationStation = new("http://127.0.0.1:1234");

    /// <summary>Au-dela, le lancement a echoue autrement, et les cles ne doivent pas rester.</summary>
    private static readonly TimeSpan PatienceApparition = TimeSpan.FromSeconds(60);

    public sealed record Resultat(bool Ok, bool ParEs);

    /// <summary>
    /// Lance `rom` : par ES si possible, directement sinon. `argumentsDirects` sont les arguments
    /// complets du lanceur pour le repli, netplay compris.
    /// </summary>
    public static async Task<Resultat> LancerAsync(
        string rom, string argumentsDirects, IHttpClientFactory httpFactory, ILogger logger, CancellationToken ct)
    {
        if (await LancerParEsAsync(rom, httpFactory, logger, ct).ConfigureAwait(false))
        {
            // Les cles s'effacent quand l'emulateur parait, c'est-a-dire une fois que le lanceur
            // les a lues. Sans attendre : la reponse HTTP n'a pas a patienter.
            _ = Task.Run(EffacerQuandLeLanceurALuAsync, CancellationToken.None);
            return new Resultat(true, true);
        }

        // Repli : les cles ne servent a rien au lancement direct, qui porte tout en arguments.
        NetplaySettings.EffacerLancement(logger);
        return new Resultat(LancerDirectement(argumentsDirects, logger), false);
    }

    private static async Task<bool> LancerParEsAsync(
        string rom, IHttpClientFactory httpFactory, ILogger logger, CancellationToken ct)
    {
        try
        {
            // ES doit avoir le premier plan AVANT le lancement, sinon il demarre l'emulateur
            // derriere le navigateur, qui a le focus au moment ou l'on arrive ici.
            EmulatorForeground.FocusEmulationStation();

            using var contenu = new StringContent(rom, Encoding.UTF8, "text/plain");
            var client = httpFactory.CreateClient();
            client.BaseAddress = EmulationStation;
            client.Timeout = TimeSpan.FromSeconds(5);
            using var reponse = await client.PostAsync("/launch", contenu, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                logger.LogInformation("Netplay : ES refuse le lancement (HTTP {Code}), lancement direct.", (int) reponse.StatusCode);
                return false;
            }
            logger.LogInformation("Netplay : partie lancee par EmulationStation.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Netplay : EmulationStation injoignable, lancement direct.");
            return false;
        }
    }

    private static bool LancerDirectement(string arguments, ILogger logger)
    {
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", "emulatorLauncher.exe");
        if (!File.Exists(exe))
        {
            logger.LogWarning("Netplay : emulatorLauncher introuvable ({Chemin}).", exe);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Netplay : lancement refuse.");
            return false;
        }
    }

    /// <summary>
    /// Efface les cles de lancement une fois l'emulateur apparu : a ce moment, le lanceur les a lues.
    /// Au bout d'une minute sans emulateur, on efface quand meme : quelque chose a echoue, et des
    /// cles qui trainent feraient de la prochaine partie ordinaire une partie netplay.
    /// </summary>
    private static async Task EffacerQuandLeLanceurALuAsync()
    {
        try
        {
            var limite = DateTime.UtcNow + PatienceApparition;
            while (DateTime.UtcNow < limite && !EmulatorForeground.EmulateurTourne())
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            // Deux secondes de plus : l'emulateur parait avant que le lanceur ait fini d'ecrire la
            // configuration, et le lanceur relit es_settings au tout debut, donc c'est deja large.
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // On efface quoi qu'il arrive.
        }
        NetplaySettings.EffacerLancement(null);
    }
}
