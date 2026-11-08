using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Recording;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FILMS DES EPREUVES (08/11) : du depart a l'arrivee du run, jamais la fin seule. Releve du 08/11 : 1-1 de SMB en 4 s
/// pour 29,77 s de run (bornes envoyees : frames 0 a 2082), Green Hill 1 en 15 s pour 39,78 s.
/// </summary>
public sealed class FilmsDesEpreuvesTests
{
    private const double Nes = 60.0988;

    [Fact]
    public void La_coupe_au_score_ne_garde_que_la_fin_d_une_epreuve()
    {
        // Le cas du releve : run 1-1 de 1789 images, film commence a la frame 23 du replay, bornes 0 a 2082. Le score ne
        // monte qu'a l'arrivee (drapeau puis decompte du temps) : la coupe des scores ne garde que quelques secondes.
        var finRunS = (2082 - 23) / Nes;
        var courbe = new List<double[]> { new[] { 2.0, 0 }, new[] { 12.0, 200 }, new[] { 31.0, 5_200 }, new[] { 34.0, 21_200 } };
        var (debut, fin) = ReplayCaptureService.SequenceDuRecord(courbe, finRunS);
        Assert.True(fin - debut < 6, $"la coupe au score garde {fin - debut} s");
    }

    [Fact]
    public void Le_film_d_une_epreuve_va_du_depart_a_l_arrivee()
    {
        // Memes bornes : tout le run, des 0 s du film jusqu'a la fin du run et 2 s apres.
        var sequence = ReplayCaptureService.SequenceDEpreuve(23, 0, 2082, Nes, 40.0);
        Assert.NotNull(sequence);
        Assert.Equal(0, sequence.Value.DebutS);
        Assert.Equal(Math.Round((2082 - 23) / Nes + 2, 2), sequence.Value.FinS);
        Assert.True(sequence.Value.FinS - sequence.Value.DebutS > 1789 / Nes);
    }

    [Fact]
    public void Le_depart_d_une_epreuve_se_ramene_au_debut_du_film()
    {
        // Bornes 326 a 3407 (une 1-3 du 07/11), film commence a la frame 20 : 5,09 s de film avant le depart.
        var sequence = ReplayCaptureService.SequenceDEpreuve(20, 326, 3407, Nes, null);
        Assert.Equal(Math.Round(306 / Nes, 2), sequence!.Value.DebutS);
        Assert.Equal(Math.Round(3387 / Nes + 2, 2), sequence.Value.FinS);
    }

    [Fact]
    public void La_fin_d_une_epreuve_ne_depasse_pas_le_film()
    {
        var sequence = ReplayCaptureService.SequenceDEpreuve(0, 0, 2400, 60, 39.5);
        Assert.Equal(39.5, sequence!.Value.FinS);
    }

    [Fact]
    public void Sans_fin_connue_une_epreuve_part_entiere()
    {
        Assert.Null(ReplayCaptureService.SequenceDEpreuve(23, null, null, Nes, 30));
        Assert.Null(ReplayCaptureService.SequenceDEpreuve(23, 0, 10, Nes, 30));
        Assert.Null(ReplayCaptureService.SequenceDEpreuve(23, 0, 2082, 0, 30));
    }

    [Fact]
    public void Une_station_rend_toute_une_epreuve_meme_de_plus_de_trois_minutes()
    {
        // Metal Slug 3, 3:41.44 a 59,1854 i/s (13106 images) : la coupe des scores en garderait 3 minutes.
        const double Arcade = 59.1854;
        var (debutScore, _) = ReplayVideoPublisher.SequenceDuReplay(0, 13_466, 14_000, Arcade);
        Assert.True(debutScore > 0);
        var (debut, fin) = ReplayVideoPublisher.SequenceDEpreuveDuReplay(0, 13_466, 14_000, Arcade);
        Assert.Equal(0L, debut);
        Assert.Equal(13_466L + 118, fin);
        // Sans bornes : tout le replay ; jamais au-dela de sa fin.
        Assert.Equal((0L, 5_000L), ReplayVideoPublisher.SequenceDEpreuveDuReplay(null, null, 5_000, 60));
        Assert.Equal((300L, 5_000L), ReplayVideoPublisher.SequenceDEpreuveDuReplay(300, 4_990, 5_000, 60));
    }

    [Fact]
    public void Le_site_dit_quand_le_run_est_une_epreuve()
    {
        Assert.True(ReplayVideoPublisher.EstUneEpreuve(JsonNode.Parse("""{"ruleset":"x","challenge":true,"start_frame":0,"end_frame":2082}""")!.AsObject()));
        Assert.False(ReplayVideoPublisher.EstUneEpreuve(JsonNode.Parse("""{"ruleset":"ch-smb-1-1-v1","challenge":false}""")!.AsObject()));
        // Un site d'avant le 08/11 ne le dit pas : le reglement d'une epreuve commence par « ch- ».
        Assert.True(ReplayVideoPublisher.EstUneEpreuve(JsonNode.Parse("""{"ruleset":"ch-smb-1-1-v1","start_frame":0,"end_frame":2082}""")!.AsObject()));
        Assert.False(ReplayVideoPublisher.EstUneEpreuve(JsonNode.Parse("""{"ruleset":"1cc","start_frame":0,"end_frame":2082}""")!.AsObject()));
        Assert.False(ReplayVideoPublisher.EstUneEpreuve(null));
    }

    [Fact]
    public void Une_relance_annoncee_couvre_l_etat_charge_qui_la_suit()
    {
        // 07/11 : replay ouvert a 16:01:05.296 et arrete a 16:01:05.377 par l'evenement de la meme relance. L'annonce, posee
        // avant la tache, couvre l'evenement meme si le replay neuf n'est pas encore ouvert.
        var t = new DateTime(2026, 11, 7, 15, 1, 5, DateTimeKind.Utc);
        Assert.True(ReplayRecorderService.RelanceCouvreLEtatCharge(t.AddMilliseconds(80), t, DateTime.MinValue));
        Assert.True(ReplayRecorderService.RelanceCouvreLEtatCharge(t.AddSeconds(3), DateTime.MinValue, t));
        // Un etat recharge sans relance de la session (ou longtemps apres) se traite comme avant.
        Assert.False(ReplayRecorderService.RelanceCouvreLEtatCharge(t, DateTime.MinValue, DateTime.MinValue));
        Assert.False(ReplayRecorderService.RelanceCouvreLEtatCharge(t.AddSeconds(11), t, t));
    }
}
