using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// COMBIEN D'OBJETS FENETRE L'API TIENT, DANS LE JOURNAL (2026-09-29).
///
/// Windows accorde 10 000 objets fenetre (USER) et 10 000 objets graphiques (GDI) a un processus.
/// Chez un joueur, l'API epuisait le premier en 5 a 20 minutes et tombait en ouvrant un bandeau.
/// Au repos, sur la borne de developpement, elle en tient 48. Pour savoir QUI fuit, il faut voir
/// quand le compte monte : un releve toutes les 30 s, ecrit quand il a bouge de 50 ou plus, et au
/// moins toutes les 10 minutes. Le journal de la session, joint aux rapports de support, montre
/// alors la pente et ce qui se passait au meme moment.
/// </summary>
public sealed class GuiResourceMonitorService : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Battement = TimeSpan.FromMinutes(10);
    private const int Pas = 50;
    private const int Alerte = 5000;

    private readonly ILogger<GuiResourceMonitorService> _logger;

    public GuiResourceMonitorService(ILogger<GuiResourceMonitorService> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (dernierUser, dernierGdi) = (-1, -1);
        var dernierEcrit = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var (user, gdi) = GuiResources.Lire();
            if (user >= 0)
            {
                var bouge = dernierUser < 0
                    || Math.Abs(user - dernierUser) >= Pas
                    || Math.Abs(gdi - dernierGdi) >= Pas;
                if (bouge || DateTime.UtcNow - dernierEcrit >= Battement)
                {
                    var delta = dernierUser < 0 ? "" : $" ({user - dernierUser:+#;-#;0} / {gdi - dernierGdi:+#;-#;0} depuis le releve precedent)";
                    if (user >= Alerte || gdi >= Alerte)
                    {
                        _logger.LogWarning("Ressources fenetre : USER={User} GDI={Gdi}{Delta}, limite Windows 10 000 : une fuite est en cours", user, gdi, delta);
                    }
                    else
                    {
                        _logger.LogInformation("Ressources fenetre : USER={User} GDI={Gdi}{Delta}", user, gdi, delta);
                    }

                    (dernierUser, dernierGdi) = (user, gdi);
                    dernierEcrit = DateTime.UtcNow;
                }
            }

            try
            {
                await Task.Delay(Periode, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
