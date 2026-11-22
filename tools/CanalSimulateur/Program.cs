// Simulateur du canal d'affichage du Player Deck (essais sans jeu). Ecrit un niveau simule de DaiOuJou a 60 images par seconde
// dans Local\NelfePlay.Canal.<pid> (pid = celui de ce processus, ou --pid <n> pour imiter un RetroArch).
//   CanalSimulateur [--pid <n>] [--secondes <n>] [--rythme <images par s>] [--netplay-apres <s>]
// Puis lancer l'API avec NELFEPLAY_CANAL_PID=<pid affiche> (et NELFEPLAY_CANAL_ROM=ddpdojblka) : elle publie deck.live au Deck.
using System.Diagnostics;
using RetroBat.Api.Deck.Canal;

var pid = Environment.ProcessId;
double secondes = 0, rythme = 60, netplay = 0;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pid") pid = int.Parse(args[i + 1]);
    else if (args[i] == "--secondes") secondes = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--rythme") rythme = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--netplay-apres") netplay = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
}

using var e = CanalScenarioSimule.DaiOuJou((uint)pid);
e.Creer(pid);
Console.WriteLine($"canal simule ouvert : Local\\NelfePlay.Canal.{pid} ({e.Tampon.Length} octets, {rythme} images par seconde). NELFEPLAY_CANAL_PID={pid}");
var sw = Stopwatch.StartNew();
var image = 0;
var coupe = false;
while (secondes <= 0 || sw.Elapsed.TotalSeconds < secondes)
{
    var voulu = (int)(sw.Elapsed.TotalSeconds * rythme);
    while (image < voulu)
    {
        CanalScenarioSimule.Ecrire(e, image++);
    }

    if (netplay > 0 && !coupe && sw.Elapsed.TotalSeconds > netplay) { e.Couper(CanalFormat.EtatCoupeNetplay); coupe = true; Console.WriteLine("canal coupe (netplay)"); }
    Thread.Sleep(2);
}

e.Couper(CanalFormat.EtatFerme);
Console.WriteLine("canal ferme");
