using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA PARTIE DU JOUEUR PASSE AVANT LA LECTURE D'UN REPLAY (11/11, support de neogeoff). Sequence vecue : une lecture demandee
/// au panneau de classement telecharge son replay (09:44:06) ; le joueur lance Ketsui depuis ES (09:44:10) ; le wrapper
/// atteste (09:44:12) ; la lecture lance un second RetroArch (09:44:11.6) qui meurt a 09:44:14.7. Le scoring, qui ignorait
/// tout pendant la lecture, a perdu le lancement et l'attestation : record refuse (profile.content_mismatch).
/// </summary>
public sealed class LecturePendantUnePartieTests
{
    private static ReplayPlaybackService Lecteur(SimpleEventBus bus)
        => new(null!, null!, null!, null!, null!, null!, bus, null!, null!, null!, null!, null!, null!,
            NullLogger<ReplayPlaybackService>.Instance);

    private static Task Publier(SimpleEventBus bus, string type)
        => bus.PublishAsync(new EventEnvelope { Type = type, Payload = new { } });

    [Fact]
    public async Task Une_partie_qui_commence_annule_la_lecture_qui_telecharge()
    {
        var bus = new SimpleEventBus();
        var lecteur = Lecteur(bus);
        lecteur.PoserEtatPourEssai(ReplayPlaybackState.Replicating, "rp_06GW2C8P8JJHZMNGD3091TGBG0");
        Assert.True(lecteur.IsBusy);

        await Publier(bus, "ui.game.started");

        Assert.False(lecteur.IsBusy);
        Assert.True(lecteur.PartieEnCours);
        var etat = lecteur.GetState();
        Assert.Equal("error", etat.State);
        Assert.Equal(nameof(ReplayErrorCode.GameAlreadyRunning), etat.Error);
    }

    [Fact]
    public async Task Aucune_lecture_ne_part_pendant_une_partie()
    {
        var bus = new SimpleEventBus();
        var lecteur = Lecteur(bus);
        await Publier(bus, "ui.game.started");

        var r = await lecteur.PlayAsync("rp_06GW2C8P8JJHZMNGD3091TGBG0", CancellationToken.None);

        Assert.False(r.Accepted);
        Assert.Equal(ReplayErrorCode.GameAlreadyRunning, r.Error);
        Assert.False(lecteur.IsBusy);

        await Publier(bus, "ui.game.ended");
        Assert.False(lecteur.PartieEnCours);
    }

    [Theory]
    [InlineData(ReplayPlaybackState.Resolving, "annuler")]
    [InlineData(ReplayPlaybackState.Replicating, "annuler")]
    [InlineData(ReplayPlaybackState.Verifying, "annuler")]
    [InlineData(ReplayPlaybackState.Launching, "fermer")]
    [InlineData(ReplayPlaybackState.Playing, "fermer")]
    [InlineData(ReplayPlaybackState.Paused, "fermer")]
    [InlineData(ReplayPlaybackState.Idle, null)]
    [InlineData(ReplayPlaybackState.Error, null)]
    public void Ce_qu_une_partie_fait_de_la_lecture(ReplayPlaybackState etat, string? attendu)
        => Assert.Equal(attendu, ReplayPlaybackService.DecisionALaPartie(etat));

    [Fact]
    public void Le_scoring_garde_le_lancement_et_l_attestation_du_joueur()
    {
        // 09:44:10, la lecture telecharge encore : le lancement du jeu passe toujours.
        Assert.False(NelfePlayScoringReporter.IgnorePendantUneLecture(true, false, "ui.game.started"));
        Assert.False(NelfePlayScoringReporter.IgnorePendantUneLecture(true, false, "ui.game.ended"));
        // 09:44:12, la partie du joueur a commence : son attestation et ses scores sont a elle.
        Assert.False(NelfePlayScoringReporter.IgnorePendantUneLecture(true, true, "scoring.listener.attestation"));
        Assert.False(NelfePlayScoringReporter.IgnorePendantUneLecture(true, true, "retroarch.score"));
        // Une vraie lecture (un replay de defi relu par le wrapper), sans partie : son attestation reste ignoree.
        Assert.True(NelfePlayScoringReporter.IgnorePendantUneLecture(true, false, "scoring.listener.attestation"));
        Assert.True(NelfePlayScoringReporter.IgnorePendantUneLecture(true, false, "retroarch.score"));
        // Sans lecture, rien n'est ignore.
        Assert.False(NelfePlayScoringReporter.IgnorePendantUneLecture(false, false, "scoring.listener.attestation"));
    }
}
