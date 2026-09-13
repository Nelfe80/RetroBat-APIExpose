using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'enchainement complet d'une partie, y compris les cas ou il ne doit rien se passer,
/// verifie sans lancer un emulateur.
/// </summary>
public class ScoringDiscoveryCoordinatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);

    private sealed class FakeEmulator : IEmulatorCaptureChannel
    {
        public bool IsAvailable { get; set; } = true;
        public bool ArmSucceeds { get; set; } = true;
        public List<(string Token, int Frames)> Arms { get; } = new();
        public int Disarms { get; private set; }

        public Task<bool> ArmAsync(string token, int frames, CancellationToken cancellationToken)
        {
            if (ArmSucceeds) { Arms.Add((token, frames)); }
            return Task.FromResult(ArmSucceeds);
        }

        public Task DisarmAsync(CancellationToken cancellationToken)
        {
            Disarms++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVerifier : IScoreVerifierChannel
    {
        public bool IsAvailable { get; set; } = true;
        public bool ExpectSucceeds { get; set; } = true;
        public List<(string Token, long Value, FrameOrientationDegrees Orientation)> Expectations { get; } = new();
        public int Forgets { get; private set; }

        public event Action<string, VerificationOutcome>? ResultReceived;

        public Task<bool> ExpectAsync(string token, long value, FrameOrientationDegrees orientation, CancellationToken cancellationToken)
        {
            if (ExpectSucceeds) { Expectations.Add((token, value, orientation)); }
            return Task.FromResult(ExpectSucceeds);
        }

        public Task ForgetAsync(CancellationToken cancellationToken)
        {
            Forgets++;
            return Task.CompletedTask;
        }

        public void Reply(string token, bool matched = true, double confidence = 1.0, double stability = 1.0, int candidates = 1)
            => ResultReceived?.Invoke(token, new VerificationOutcome(matched, confidence, stability, candidates, "0.2.0"));
    }

    private static ScoringConditions Candidate() => new(
        PlayerConsent: true, DevicePaired: true, VerifierAvailable: true, CaptureComponentAvailable: true,
        Enrollment: DiscoveryEnrollment.Shadow, ProfileIsOpen: false, FingerprintInvalidated: false);

    private static (ScoringDiscoveryCoordinator Coordinator, FakeEmulator Emulator, FakeVerifier Verifier) Build(
        ScoringDiscoveryOptions? options = null)
    {
        var emulator = new FakeEmulator();
        var verifier = new FakeVerifier();
        return (new ScoringDiscoveryCoordinator(options ?? new ScoringDiscoveryOptions(), emulator, verifier), emulator, verifier);
    }

    private static async Task<(ScoringDiscoveryCoordinator Coordinator, FakeEmulator Emulator, FakeVerifier Verifier)> Started(
        ScoringConditions? conditions = null, ScoringDiscoveryOptions? options = null)
    {
        var built = Build(options);
        await built.Coordinator.StartSessionAsync(conditions ?? Candidate(), FrameOrientationDegrees.CounterClockwise90, "tok1", default);
        return built;
    }

    [Fact]
    public async Task Une_partie_en_decouverte_annonce_la_valeur_puis_arme_la_capture()
    {
        var (coordinator, emulator, verifier) = await Started();

        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddMilliseconds(300), default);

        // L'ordre compte : une image qui arriverait sans attente posee serait jetee.
        Assert.Single(verifier.Expectations);
        Assert.Equal(("tok1", 1200L, FrameOrientationDegrees.CounterClockwise90), verifier.Expectations[0]);
        Assert.Single(emulator.Arms);
        Assert.Equal(("tok1", 3), emulator.Arms[0]);
        Assert.Equal(1, coordinator.ArmedCount);
    }

    [Fact]
    public async Task Hors_decouverte_rien_n_est_jamais_arme()
    {
        var (coordinator, emulator, verifier) = await Started(Candidate() with { PlayerConsent = false });

        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddSeconds(10), default);

        Assert.Empty(emulator.Arms);
        Assert.Empty(verifier.Expectations);
        Assert.False(coordinator.Observing);
        Assert.Equal(ScoringMode.Disabled, coordinator.Mode.Mode);
    }

    [Fact]
    public async Task Un_jeu_deja_ouvert_ne_declenche_aucune_capture()
    {
        var (coordinator, emulator, _) = await Started(Candidate() with { ProfileIsOpen = true });

        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddSeconds(1), default);

        Assert.Equal(ScoringMode.OfficialScoring, coordinator.Mode.Mode);
        Assert.Empty(emulator.Arms);
    }

    [Fact]
    public async Task Le_debut_de_partie_fait_oublier_la_police_de_la_precedente()
    {
        var (_, _, verifier) = await Started();

        Assert.Equal(1, verifier.Forgets);
    }

    [Fact]
    public async Task L_oubli_est_demande_meme_quand_la_partie_n_observe_pas()
    {
        // Le verificateur a pu servir la partie precedente : sa police ne vaut rien ici.
        var (_, _, verifier) = await Started(Candidate() with { PlayerConsent = false });

        Assert.Equal(1, verifier.Forgets);
    }

    [Fact]
    public async Task Les_resultats_nourrissent_les_preuves_de_la_partie()
    {
        var (coordinator, _, verifier) = await Started();

        foreach (var (value, at) in new[] { (1200L, 0), (4500L, 1000), (40000L, 2000) })
        {
            coordinator.OnScoreChanged(value, T0.AddMilliseconds(at));
            await coordinator.PollAsync(T0.AddMilliseconds(at + 300), default);
            verifier.Reply("tok1");
        }

        var evidence = await coordinator.EndSessionAsync(default);

        Assert.NotNull(evidence);
        Assert.Equal(3, evidence!.Attempts);
        Assert.Equal(3, evidence.DistinctValuesMatched);
        Assert.True(evidence.IsCredible);
    }

    [Fact]
    public async Task Un_resultat_en_retard_d_une_autre_partie_ne_compte_pas()
    {
        var (coordinator, _, verifier) = await Started();
        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddMilliseconds(300), default);

        verifier.Reply("une-autre-partie");

        var evidence = await coordinator.EndSessionAsync(default);
        Assert.Equal(0, evidence!.Attempts);
    }

    [Fact]
    public async Task Un_resultat_arrive_apres_la_fin_de_partie_ne_compte_pas_non_plus()
    {
        var (coordinator, _, verifier) = await Started();
        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddMilliseconds(300), default);
        await coordinator.EndSessionAsync(default);

        verifier.Reply("tok1");

        // Rien ne doit s'ajouter apres coup : la partie est close.
        Assert.False(coordinator.Observing);
    }

    [Fact]
    public async Task Un_verificateur_injoignable_arrete_la_partie_sans_armer_l_emulateur()
    {
        var (coordinator, emulator, verifier) = await Started();
        verifier.ExpectSucceeds = false;

        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddMilliseconds(300), default);

        Assert.Empty(emulator.Arms);
        Assert.False(coordinator.Observing);
    }

    [Fact]
    public async Task Un_emulateur_qui_ne_peut_pas_s_armer_arrete_la_partie()
    {
        var (coordinator, emulator, _) = await Started();
        emulator.ArmSucceeds = false;

        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddMilliseconds(300), default);

        Assert.False(coordinator.Observing);
        Assert.Equal(0, coordinator.ArmedCount);
    }

    [Fact]
    public async Task Une_derive_de_duree_d_image_desarme_l_emulateur_et_la_partie()
    {
        var (coordinator, emulator, _) = await Started();

        await coordinator.ReportFrameTimeDriftAsync(9.0, default);

        Assert.False(coordinator.Observing);
        Assert.Equal(1, emulator.Disarms);

        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddSeconds(5), default);
        Assert.Empty(emulator.Arms);
    }

    [Fact]
    public async Task Une_valeur_jamais_vue_en_chiffres_fait_cesser_la_partie()
    {
        var options = new ScoringDiscoveryOptions { StableDelayMs = 0, MinIntervalMs = 0, NoCandidateAttemptsBeforeGivingUp = 3 };
        var (coordinator, emulator, verifier) = await Started(options: options);

        for (var i = 1; i <= 3; i++)
        {
            coordinator.OnScoreChanged(i, T0.AddSeconds(i));
            await coordinator.PollAsync(T0.AddSeconds(i), default);
            verifier.Reply("tok1", matched: false, confidence: 0, stability: 0, candidates: 0);
        }

        await coordinator.PollAsync(T0.AddSeconds(4), default);

        Assert.False(coordinator.Observing);
        Assert.Equal(1, emulator.Disarms);
        var evidence = await coordinator.EndSessionAsync(default);
        Assert.True(evidence!.LikelyNotShownAsDigits);
    }

    [Fact]
    public async Task Une_partie_qui_n_observait_pas_ne_rend_aucune_preuve()
    {
        var (coordinator, _, _) = await Started(Candidate() with { DevicePaired = false });

        Assert.Null(await coordinator.EndSessionAsync(default));
    }

    [Fact]
    public async Task La_fin_de_partie_desarme_l_emulateur_dans_tous_les_cas()
    {
        var (coordinator, emulator, _) = await Started(Candidate() with { PlayerConsent = false });

        await coordinator.EndSessionAsync(default);

        Assert.Equal(1, emulator.Disarms);
    }

    [Fact]
    public async Task Une_nouvelle_partie_repart_de_zero()
    {
        var (coordinator, _, verifier) = await Started();
        coordinator.OnScoreChanged(1200, T0);
        await coordinator.PollAsync(T0.AddMilliseconds(300), default);
        verifier.Reply("tok1");
        await coordinator.EndSessionAsync(default);

        await coordinator.StartSessionAsync(Candidate(), FrameOrientationDegrees.Upright, "tok2", default);

        Assert.True(coordinator.Observing);
        Assert.Equal(0, coordinator.ArmedCount);
        var evidence = await coordinator.EndSessionAsync(default);
        Assert.Equal(0, evidence!.Attempts);
    }

    [Fact]
    public async Task Une_partie_en_decouverte_qui_n_a_rien_vu_rend_quand_meme_son_constat()
    {
        // « Il ne s'est rien passe » est une information : c'est ce qui distingue une borne
        // qui n'a pas observe d'un jeu qui n'a rien donne.
        var (coordinator, _, _) = await Started();

        var evidence = await coordinator.EndSessionAsync(default);

        Assert.NotNull(evidence);
        Assert.Equal(0, evidence!.Attempts);
        Assert.False(evidence.IsCredible);
        Assert.Equal("aucune tentative", evidence.Verdict);
    }
}
