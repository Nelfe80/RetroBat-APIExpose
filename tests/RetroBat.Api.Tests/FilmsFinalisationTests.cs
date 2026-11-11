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
    /// <summary>
    /// LE SCORE MEGA DRIVE COMPOSE PAR LE LECTEUR (11/11) : Streets of Rage 2, trois octets BCD lus par READ_CORE_RAM a
    /// 0xEF96/97/99 (repere de Genesis Plus GX, sans ^1). Sur la borne, en fin du replay rp_06GVYGMP (recalage) : 39, 00, 60.
    /// </summary>
    [Fact]
    public void Le_score_de_Streets_of_Rage_2_se_compose_depuis_ses_octets_bcd()
    {
        const string mem = """
return {
  events = {
    scoring = {
      points = {
        { address=0XEF96, type="u8", condition="change", action="SCORE_STATE", player=1, score_kind="game", score_mask="00XX00", score_encoding="bcd", desc="[8-bit BCD] Player 1 Score: 00XX00" },
        { address=0XEF97, type="u8", condition="change", action="SCORE_STATE", player=1, score_kind="game", score_mask="XX0000", score_encoding="bcd", desc="[8-bit BCD] Player 1 Score: XX0000" },
        { address=0XEF99, type="u8", condition="change", action="SCORE_STATE", player=1, score_kind="game", score_mask="0000XX", score_encoding="bcd", desc="[8-bit BCD] Player 1 Score: 0000XX" },
        { address=0XF096, type="u8", condition="change", action="SCORE_STATE", player=2, score_kind="game", score_mask="00XX00", score_encoding="bcd", desc="[8-bit BCD] Player 2 Score: 00XX00" },
      },
    },
  },
}
""";
        using var lecteur = LecteurDuScore.Depuis("megadrive", "streets-of-rage-2", "streets-of-rage-2.MEM", mem);
        Assert.NotNull(lecteur);
        Assert.Equal([0xEF96L, 0xEF97L, 0xEF99L], lecteur!.Lignes.Select(l => l.Adresse));
        var octets = new Dictionary<long, int> { [0xEF96] = 0x39, [0xEF97] = 0x00, [0xEF99] = 0x60 };
        Assert.Equal(3960, lecteur.Composer(lecteur.Lignes.Select(l => (long?)octets[l.Adresse]).ToList()));
        // Le score de neogeoff (rp_06GW30HW, rejoue sans recalage jusqu'a la fin) : 10 75 80.
        var neogeoff = new Dictionary<long, int> { [0xEF96] = 0x75, [0xEF97] = 0x10, [0xEF99] = 0x80 };
        Assert.Equal(107580, lecteur.Composer(lecteur.Lignes.Select(l => (long?)neogeoff[l.Adresse]).ToList()));
    }

    [Fact]
    public void La_liste_du_site_porte_les_epreuves_a_controler()
    {
        var liste = ReplayVideoPublisher.LireLaListe("""
{"ok":true,"role":"player","targets":[{"replay_id":"rp_1","run_start_frame":0,"run_end_frame":1603,"challenge":true,
"checks":[{"challenge_id":"super-mario-bros.1-1","version":1,"frames":1423,"end_frame":1603}]},
{"replay_id":"rp_2","run_start_frame":10,"run_end_frame":900,"checks":[{"end_frame":900,"score":12345}]}]}
""");
        var epreuve = Assert.Single(liste.Cibles[0].Epreuves!);
        Assert.Equal(new EpreuveCertifiee("super-mario-bros.1-1", 1, 1423, 1603), epreuve);
        Assert.Null(liste.Cibles[0].Controles);
        Assert.Null(liste.Cibles[1].Epreuves);
        Assert.Equal(new PointDuScore(900, 12345), Assert.Single(liste.Cibles[1].Controles!));
    }
}
