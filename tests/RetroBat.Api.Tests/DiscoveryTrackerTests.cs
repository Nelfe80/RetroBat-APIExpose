using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le suivi temporel du §7.2.6 : ce qui prouve, c'est plusieurs valeurs differentes lues au
/// meme endroit, pas une reconnaissance isolee.
/// </summary>
public class DiscoveryTrackerTests
{
    private const string Version = "0.2.0";

    private static VerificationOutcome Good(double confidence = 1.0, double stability = 1.0, int candidates = 1)
        => new(true, confidence, stability, candidates, Version);

    private static VerificationOutcome Miss() => new(false, 0, 0, 0, Version);

    private static DiscoveryTracker Tracker(ScoringDiscoveryOptions? options = null)
        => new(options ?? new ScoringDiscoveryOptions());

    [Fact]
    public void Une_partie_sans_tentative_ne_prouve_rien()
    {
        var evidence = Tracker().Summarise();

        Assert.Equal(0, evidence.Attempts);
        Assert.False(evidence.IsCredible);
        Assert.Equal("aucune tentative", evidence.Verdict);
    }

    [Fact]
    public void Une_seule_valeur_reconnue_ne_prouve_rien()
    {
        var tracker = Tracker();
        tracker.Record(1200, Good());

        var evidence = tracker.Summarise();
        Assert.Equal(1, evidence.Matches);
        Assert.Equal(1, evidence.DistinctValuesMatched);
        Assert.False(evidence.IsCredible);
    }

    [Fact]
    public void La_meme_valeur_reconnue_dix_fois_ne_prouve_toujours_rien()
    {
        // Un ecran fige reconnu en boucle n'apporte aucune information nouvelle.
        var tracker = Tracker();
        for (var i = 0; i < 10; i++)
        {
            tracker.Record(1200, Good());
        }

        var evidence = tracker.Summarise();
        Assert.Equal(10, evidence.Matches);
        Assert.Equal(1, evidence.DistinctValuesMatched);
        Assert.False(evidence.IsCredible);
    }

    [Fact]
    public void Deux_valeurs_distinctes_ne_suffisent_pas_non_plus()
    {
        var tracker = Tracker();
        tracker.Record(0, Good());
        tracker.Record(100, Good());

        Assert.False(tracker.Summarise().IsCredible);
    }

    [Fact]
    public void Trois_valeurs_distinctes_au_meme_endroit_font_une_preuve()
    {
        var tracker = Tracker();
        tracker.Record(1200, Good());
        tracker.Record(4500, Good());
        tracker.Record(40000, Good());

        var evidence = tracker.Summarise();
        Assert.True(evidence.IsCredible);
        Assert.Equal(3, evidence.DistinctValuesMatched);
        Assert.Contains("meme endroit", evidence.Verdict);
    }

    [Fact]
    public void Une_reconnaissance_sous_le_seuil_de_confiance_ne_compte_pas()
    {
        // Une reconnaissance faible n'est pas une demi-preuve.
        var tracker = Tracker();
        tracker.Record(1200, Good(confidence: 0.90));
        tracker.Record(4500, Good(confidence: 0.90));
        tracker.Record(40000, Good(confidence: 0.90));

        var evidence = tracker.Summarise();
        Assert.Equal(3, evidence.Matches);
        Assert.Equal(0, evidence.DistinctValuesMatched);
        Assert.False(evidence.IsCredible);
    }

    [Fact]
    public void Une_region_qui_bouge_ne_compte_pas_non_plus()
    {
        // Trois nombres lus a trois endroits differents ne designent aucun champ.
        var tracker = Tracker();
        tracker.Record(1200, Good(stability: 0.30));
        tracker.Record(4500, Good(stability: 0.20));
        tracker.Record(40000, Good(stability: 0.10));

        Assert.False(tracker.Summarise().IsCredible);
    }

    [Fact]
    public void La_premiere_reconnaissance_ne_peut_pas_valoir_comme_preuve_de_champ()
    {
        // La stabilite est nulle par construction a la premiere image : il n'y a pas encore
        // de « fois precedente » a comparer.
        var tracker = Tracker();
        tracker.Record(1200, Good(stability: 0));
        tracker.Record(4500, Good());
        tracker.Record(40000, Good());
        tracker.Record(52000, Good());

        var evidence = tracker.Summarise();
        Assert.Equal(4, evidence.Matches);
        Assert.Equal(3, evidence.DistinctValuesMatched);
        Assert.True(evidence.IsCredible);
    }

    [Fact]
    public void Les_echecs_restent_comptes_car_le_taux_d_echec_est_une_mesure()
    {
        var tracker = Tracker();
        tracker.Record(1200, Miss());
        tracker.Record(1200, Miss());
        tracker.Record(4500, Good());

        var evidence = tracker.Summarise();
        Assert.Equal(3, evidence.Attempts);
        Assert.Equal(1, evidence.Matches);
    }

    [Fact]
    public void Le_resume_porte_la_confiance_moyenne_et_la_plus_basse()
    {
        var tracker = Tracker();
        tracker.Record(1200, Good(confidence: 1.0));
        tracker.Record(4500, Good(confidence: 0.98));
        tracker.Record(40000, Good(confidence: 0.99));
        tracker.Record(52000, Miss());   // un echec ne tire pas la moyenne des reconnaissances

        var evidence = tracker.Summarise();
        Assert.Equal(0.99, evidence.MeanConfidence, 4);
        Assert.Equal(0.98, evidence.MinConfidence, 4);
        Assert.Equal(1.0, evidence.MeanRegionStability, 4);
    }

    [Fact]
    public void Le_resume_retient_le_plus_grand_nombre_de_candidats_vu()
    {
        // Plusieurs candidats pour une meme valeur signalent un ecran ambigu : c'est une
        // information de qualification, pas un detail.
        var tracker = Tracker();
        tracker.Record(500, Good(candidates: 1));
        tracker.Record(700, Good(candidates: 3));
        tracker.Record(900, Good(candidates: 2));

        Assert.Equal(3, tracker.Summarise().MaxCandidateCount);
    }

    [Fact]
    public void Le_resume_nomme_la_version_du_verificateur()
    {
        var tracker = Tracker();
        tracker.Record(500, Good());

        Assert.Equal(Version, tracker.Summarise().VerifierVersion);
    }

    [Fact]
    public void Le_verdict_dit_ce_qui_manque_quand_ce_n_est_pas_credible()
    {
        var tracker = Tracker();
        tracker.Record(500, Good());

        Assert.Contains("il en faut 3", tracker.Summarise().Verdict);
    }

    [Fact]
    public void Une_nouvelle_partie_ne_herite_pas_des_preuves_de_la_precedente()
    {
        var tracker = Tracker();
        tracker.Record(1200, Good());
        tracker.Record(4500, Good());
        tracker.Record(40000, Good());
        Assert.True(tracker.Summarise().IsCredible);

        tracker.StartSession();

        var evidence = tracker.Summarise();
        Assert.Equal(0, evidence.Attempts);
        Assert.False(evidence.IsCredible);
    }

    [Fact]
    public void Le_resume_ne_porte_aucune_image_et_passe_le_schema_d_enveloppe()
    {
        var tracker = Tracker();
        tracker.Record(1200, Good());
        tracker.Record(4500, Good());
        tracker.Record(40000, Good());

        var json = System.Text.Json.JsonSerializer.Serialize(tracker.Summarise());

        Assert.True(DiscoveryEnvelopeSchema.IsAcceptable(json, out var reason), reason);
    }
}
