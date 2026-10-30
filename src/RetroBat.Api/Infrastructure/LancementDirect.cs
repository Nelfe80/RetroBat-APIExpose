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
    /// donc le jeu nous-memes, on annonce son debut, et sa fin quand le lanceur rend la main : il vit tant que
    /// l'emulateur tourne.
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
            try { await lanceur.WaitForExitAsync().ConfigureAwait(false); } catch { /* le lanceur a deja disparu */ }
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
                    Payload = new { Source = "commands.launch", SystemId = systeme, GamePath = romPath, GameName = jeu.GameName, Direct = true },
                }).ConfigureAwait(false);
            }
            catch { /* rien a faire de plus */ }
        });
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();
        return lanceur;
    }
}
