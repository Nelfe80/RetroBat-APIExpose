using System.Collections.Concurrent;
using System.Diagnostics;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Recording;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA RELANCE N'ATTEND PLUS SES ABONNES (12/11 soir). Banc de 18:17 : la relance d'un defi est entree dans l'ouverture du
/// replay a 18:17:02.814 et n'a arrete l'ancien qu'a 18:17:37.000. Entre les deux, « replay.recording.stopped » publie sur le
/// fil de la relance, chez un abonne qui attendait un verrou. La relance suivante n'a rien pu ouvrir.
/// </summary>
public sealed class RelanceSansAttenteTests
{
    [Fact]
    public async Task Un_abonne_bloque_ne_retarde_plus_la_relance()
    {
        // L'abonne lent : il attend un verrou tenu 3 s ailleurs (le rapporteur au banc : 34 s).
        var verrou = new object();
        var recus = new ConcurrentQueue<string>();
        var bus = new SimpleEventBus();
        bus.Subscribe<EventEnvelope>(e => { lock (verrou) recus.Enqueue(e.Type); });
        var file = new FileDEvenements(bus, null);

        var tenu = new ManualResetEventSlim();
        var occupe = Task.Run(() => { lock (verrou) { tenu.Set(); Thread.Sleep(3000); } });
        tenu.Wait();

        // La relance : arret de l'ancien, annonce, ouverture du suivant, annonce.
        var etapes = new ReplayRecorderService.EtapesDeLaRelance();
        etapes.Noter("tour");
        file.Publier("replay.recording.stopped", new { ReplayId = "rp_ancien" });
        etapes.Noter("annonce");
        file.Publier("replay.recording.started", new { ReplayId = "rp_neuf" });
        etapes.Noter("ouverture");

        Assert.True(etapes.TotalMs < 1000, $"relance retenue {etapes.TotalMs} ms ({etapes.Detail()})");
        Assert.Null(etapes.Lente());

        // L'abonne recoit tout, dans l'ordre, quand il se libere.
        await occupe;
        var chrono = Stopwatch.StartNew();
        while (recus.Count < 2 && chrono.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.Equal(new[] { "replay.recording.stopped", "replay.recording.started" }, recus.ToArray());
    }

    [Fact]
    public async Task Les_annonces_gardent_leur_ordre()
    {
        var recus = new ConcurrentQueue<string>();
        var bus = new SimpleEventBus();
        bus.Subscribe<EventEnvelope>(e => { Thread.Sleep(5); recus.Enqueue(e.Type); });
        var file = new FileDEvenements(bus, null);
        var attendus = Enumerable.Range(0, 30).Select(i => "e" + i).ToArray();
        foreach (var t in attendus) file.Publier(t, new { });
        await file.ViderAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(attendus, recus.ToArray());
    }

    [Fact]
    public void Une_etape_de_plus_d_une_demi_seconde_est_signalee()
    {
        var etapes = new ReplayRecorderService.EtapesDeLaRelance();
        etapes.Noter("tour");
        Thread.Sleep(600);
        etapes.Noter("fichier");
        Assert.Equal("fichier", etapes.Lente()?.Nom);
        Assert.Contains("fichier", etapes.Detail());
    }

    [Fact]
    public void Le_fichier_se_cherche_dans_le_dossier_du_coeur()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfe-relance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var coeur = Directory.CreateDirectory(Path.Combine(racine, "megadrive", "libretro.genesis_plus_gx_ghosts")).FullName;
            var autre = Directory.CreateDirectory(Path.Combine(racine, "nes", "libretro.fceumm")).FullName;
            var debut = DateTime.UtcNow.AddMinutes(-1);
            var ancien = Ecrire(coeur, "Sonic.replay4", debut.AddMinutes(-10));
            var bon = Ecrire(coeur, "Sonic.replay5", debut.AddSeconds(30));
            Ecrire(autre, "Mario.replay1", debut.AddSeconds(50)); // plus recent, mais d'un autre coeur

            Assert.Equal(bon, ReplayRecorderService.LePlusRecent(coeur, debut));
            // tout saves : le plus recent l'emporte, quel que soit le coeur (le repli d'avant)
            Assert.EndsWith("Mario.replay1", ReplayRecorderService.LePlusRecent(racine, debut));
            // rien d'ecrit depuis le debut de l'enregistrement
            Assert.Null(ReplayRecorderService.LePlusRecent(coeur, debut.AddMinutes(5)));
            Assert.Null(ReplayRecorderService.LePlusRecent(Path.Combine(racine, "absent"), debut));
            _ = ancien;
        }
        finally
        {
            try { Directory.Delete(racine, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void L_arret_note_sans_verrou_pose_la_fin_une_seule_fois()
    {
        var enregistrements = new List<(string Id, long Debut, long? Fin)> { ("rp_a", 100, null), ("rp_b", 900, 1200), ("rp_c", 1300, null) };
        var arrets = new ConcurrentQueue<(string Id, long Fin)>();
        arrets.Enqueue(("rp_a", 850));
        arrets.Enqueue(("rp_b", 5000)); // fin deja connue : gardee
        arrets.Enqueue(("rp_a", 9999)); // second arret du meme : la premiere fin reste
        NelfePlayScoringReporter.AppliquerLesArrets(enregistrements, arrets);
        Assert.Equal(new long?[] { 850, 1200, null }, enregistrements.Select(e => e.Fin).ToArray());
        Assert.True(arrets.IsEmpty);
    }

    private static string Ecrire(string dossier, string nom, DateTime quandUtc)
    {
        var chemin = Path.Combine(dossier, nom);
        File.WriteAllBytes(chemin, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(chemin, quandUtc);
        return chemin;
    }
}
