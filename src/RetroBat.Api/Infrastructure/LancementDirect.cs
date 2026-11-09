using System.Diagnostics;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UN JEU LANCE PAR emulatorLauncher, SANS EMULATIONSTATION : emulateur et coeur imposes, RetroAchievements
/// coupe pour la seance si on le demande. Sert a commands/launch (le labo, le HubManager) et au lancement
/// type d'un defi de speedrun (prototype Sonic, §10).
/// </summary>
public static class LancementDirect
{
    /// <summary>
    /// Demarre le lanceur et pose le jeu en cours. Rend null si le lanceur ne demarre pas.
    ///
    /// SANS EMULATIONSTATION, PERSONNE NE DIT QUEL JEU TOURNE. Le contexte « jeu en cours » vient d'ordinaire du
    /// hook game-start d'ES ; sans lui, le wrapper resout sa definition sur le jeu SELECTIONNE dans le menu, ou
    /// sur rien, et aucun score n'est lu (labo du 2026-09-22 : 0 evenement sur FBNeo lance en direct). On pose
    /// donc le jeu nous-memes, on annonce son debut, et sa fin des que le RetroArch du lanceur disparait (processus ou
    /// fenetre, <see cref="GuetDeLaFinDuJeu"/>) ; a defaut, quand le lanceur rend la main.
    /// </summary>
    public static Process? Lancer(string arguments, string systeme, string romPath, ApiContext contexte, IEventBus bus, ILogger logger)
    {
        var lanceur = RetroBat.Api.Netplay.NetplayLaunch.DemarrerDirectement(arguments, logger);
        if (lanceur is null) return null;

        var jeu = new GameReference
        {
            SystemId = systeme,
            GamePath = romPath,
            GameName = Path.GetFileNameWithoutExtension(romPath),
        };
        contexte.Ui.Running = jeu;
        contexte.Ui.State = "playing";
        _ = bus.PublishAsync(new EventEnvelope
        {
            Type = "ui.game.started",
            Payload = new { Source = "commands.launch", SystemId = systeme, GamePath = romPath, GameName = jeu.GameName, Direct = true },
        });
        _ = Task.Run(async () =>
        {
            // LA FIN TOMBE AVEC RETROARCH (09/11), PAS AVEC LE LANCEUR : 48 s d'ecart vus apres une erreur de FBNeo.
            var sortieDuLanceur = AttendreLaSortie(lanceur);
            var depart = DateTime.UtcNow;
            string raison;
            try
            {
                raison = await GuetterLaFinAsync(SondeDuLanceur(lanceur), sortieDuLanceur, GuetDeLaFinDuJeu.Pas, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Lancement direct : guet de RetroArch interrompu, fin a la sortie du lanceur.");
                await sortieDuLanceur.ConfigureAwait(false);
                raison = "lanceur";
            }

            if (raison != "lanceur")
            {
                logger.LogInformation("Lancement direct : RetroArch ferme ({Raison}), fin de partie annoncee sans attendre le lanceur ({Duree:0} s de jeu).",
                    raison == "fenetre" ? "fenetre disparue" : "processus termine", (DateTime.UtcNow - depart).TotalSeconds);
                var finDuJeu = DateTime.UtcNow;
                _ = sortieDuLanceur.ContinueWith(_ =>
                {
                    var ecart = (DateTime.UtcNow - finDuJeu).TotalSeconds;
                    if (ecart >= 5) logger.LogInformation("Lancement direct : le lanceur a rendu la main {Ecart:0} s apres RetroArch.", ecart);
                }, TaskScheduler.Default);
            }

            if (ReferenceEquals(contexte.Ui.Running, jeu))
            {
                contexte.Ui.Running = null;
                contexte.Ui.State = "browsing";
            }
            try
            {
                await bus.PublishAsync(new EventEnvelope
                {
                    Type = "ui.game.ended",
                    Payload = new { Source = "commands.launch", SystemId = systeme, GamePath = romPath, GameName = jeu.GameName, Direct = true, EndedBy = raison },
                }).ConfigureAwait(false);
            }
            catch { /* rien a faire de plus */ }
        });
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();
        return lanceur;
    }

    private static async Task AttendreLaSortie(Process lanceur)
    {
        try { await lanceur.WaitForExitAsync().ConfigureAwait(false); } catch { /* le lanceur a deja disparu */ }
    }

    /// <summary>
    /// LE GUET : un relevé par <paramref name="pas"/> jusqu'a la fin du RetroArch du lanceur, ou jusqu'a la sortie du lanceur
    /// (filet : RetroArch jamais vu, autre emulateur). Rend « processus », « fenetre » ou « lanceur ». Un relevé null (Windows
    /// refuse l'instantane) est saute.
    /// </summary>
    internal static async Task<string> GuetterLaFinAsync(Func<(bool Processus, bool Fenetre)?> relever, Task sortieDuLanceur, TimeSpan pas, CancellationToken annulation)
    {
        var guet = new GuetDeLaFinDuJeu();
        while (!sortieDuLanceur.IsCompleted)
        {
            if (relever() is { } releve && guet.Relever(releve.Processus, releve.Fenetre) is { } raison) return raison;
            await Task.WhenAny(sortieDuLanceur, Task.Delay(pas, annulation)).ConfigureAwait(false);
            annulation.ThrowIfCancellationRequested();
        }

        return "lanceur";
    }

    /// <summary>
    /// Le relevé reel : les RetroArch qui descendent du lanceur, nes apres lui (un numero de parent repris par Windows ne
    /// rattache pas un RetroArch plus ancien), et leur fenetre visible.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Func<(bool Processus, bool Fenetre)?> SondeDuLanceur(Process lanceur)
    {
        var racine = lanceur.Id;
        DateTime departDuLanceur;
        try { departDuLanceur = lanceur.StartTime; }
        catch (Exception) { departDuLanceur = DateTime.Now; }
        var admis = new Dictionary<int, bool>();
        return () =>
        {
            var instantane = ArbreDesProcessus.Instantane();
            if (instantane is null) return null;
            var retroarch = ArbreDesProcessus.RetroArchSous(instantane, racine);
            retroarch.RemoveAll(pid => !Admis(pid));
            return (retroarch.Count > 0, ArbreDesProcessus.FenetreVisible(retroarch));
        };

        bool Admis(int pid)
        {
            if (admis.TryGetValue(pid, out var oui)) return oui;
            try
            {
                using var p = Process.GetProcessById(pid);
                oui = p.StartTime >= departDuLanceur.AddSeconds(-1);
            }
            catch (Exception)
            {
                // Parti entre l'instantane et cette lecture : il ne compte pas, et n'est pas retenu (on relira).
                return false;
            }

            admis[pid] = oui;
            return oui;
        }
    }
}
