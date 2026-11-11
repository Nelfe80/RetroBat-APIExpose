using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>LES FILMS DES RECORDS, FINALISATION (11/11) : non-capture des fm_ et mp_, refus definitifs du site.</summary>
public sealed class FilmsFinalisationTests
{
    [Theory]
    [InlineData("rp_06GWKHWSC775CYJY7J0T1FE844", true)]
    [InlineData("fm_01JCZ8X9Y2ABCDEFGHJKMNPQRS", true)]
    [InlineData("mp_01JCZ8X9Y2ABCDEFGHJKMNPQRS", true)]
    [InlineData("xx_01JCZ8X9Y2ABCDEFGHJKMNPQRS", false)]
    [InlineData("rp_", false)]
    [InlineData(null, false)]
    public void Les_films_dont_la_non_capture_se_dit(string? id, bool attendu)
        => Assert.Equal(attendu, ReplayVideoPublisher.FilmSignalable(id));

    [Fact]
    public void Le_detail_d_une_rencontre_porte_sa_gameroom()
    {
        Assert.Equal("gameroom gr_42 : filtre ntsc", ReplayVideoPublisher.DetailDeNonCapture("mp_01JCZ8X9Y2ABCDEFGHJKMNPQRS", "filtre ntsc", "gr_42"));
        Assert.Equal("gameroom gr_42", ReplayVideoPublisher.DetailDeNonCapture("mp_01JCZ8X9Y2ABCDEFGHJKMNPQRS", null, "gr_42"));
        Assert.Equal("filtre ntsc", ReplayVideoPublisher.DetailDeNonCapture("fm_01JCZ8X9Y2ABCDEFGHJKMNPQRS", "filtre ntsc", "gr_42"));
        Assert.Equal("", ReplayVideoPublisher.DetailDeNonCapture("rp_06GWKHWSC775CYJY7J0T1FE844", null, null));
        Assert.Null(ReplayVideoPublisher.DetailDeNonCapture("xx_01JCZ8X9Y2ABCDEFGHJKMNPQRS", "x", null));
        Assert.Equal(200, ReplayVideoPublisher.DetailDeNonCapture("mp_01JCZ8X9Y2ABCDEFGHJKMNPQRS", new string('a', 400), "gr")!.Length);
    }

    [Fact]
    public void Pas_un_record_est_definitif_et_n_arrete_pas_la_file()
    {
        Assert.True(ReplayVideoPublisher.RefusDefinitif("pas_un_record"));
        Assert.True(ReplayVideoPublisher.RefusDefinitif("deja_filme"));
        Assert.True(ReplayVideoPublisher.RefusDefinitif("taille"));
        // Le quota et les pannes passent, eux, par l'heure d'attente.
        Assert.False(ReplayVideoPublisher.RefusDefinitif("quota"));
        Assert.False(ReplayVideoPublisher.RefusDefinitif(null));
        // Le site ecarte deja pas_un_record et deja_filme de sa liste : rien a lui dire ; un film trop gros compte un essai.
        Assert.False(ReplayVideoPublisher.RefusACompter("pas_un_record"));
        Assert.False(ReplayVideoPublisher.RefusACompter("deja_filme"));
        Assert.True(ReplayVideoPublisher.RefusACompter("taille"));
    }
}
