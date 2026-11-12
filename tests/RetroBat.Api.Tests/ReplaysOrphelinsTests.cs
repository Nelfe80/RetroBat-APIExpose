using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Recording;
using RetroBat.Api.Replay.Runtime;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES TEMPS PARTIS SANS REPLAY (12/11). Green Hill 1, run de 0:32.56 (1951 images, arrivee a l'image 11130 du wrapper) :
/// RetroArch a ouvert le replay de la relance 0,9 s apres RECORD_REPLAY, le controle unique a 300 ms l'a dit non confirme,
/// l'enregistreur ne l'a jamais suivi (fichier .replay5 orphelin, 2353 images), et le temps est parti sans replay
/// (« aucun des 13 enregistrement(s) »).
/// </summary>
public sealed class ReplaysOrphelinsTests
{
    private static readonly RaActiveReplay Enregistre = new(1788241628, 8, 37);

    [Fact]
    public async Task La_confirmation_attend_un_enregistrement_ouvert_en_retard()
    {
        // 0,9 s sur la borne chargee : rien aux six premiers sondages, puis l'enregistrement.
        var lectures = 0;
        Task<RaActiveReplay?> Lire(CancellationToken _) => Task.FromResult<RaActiveReplay?>(++lectures < 7 ? RaActiveReplay.Idle : Enregistre);

        var lu = await ReplayRecorderService.AttendreLaConfirmationAsync(Lire, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.True(lu is { Recording: true });
        Assert.Equal(7, lectures);
    }

    [Fact]
    public async Task La_confirmation_rend_la_main_au_bout_de_l_attente()
    {
        var lectures = 0;
        Task<RaActiveReplay?> Lire(CancellationToken _) { lectures++; return Task.FromResult<RaActiveReplay?>(null); }

        var lu = await ReplayRecorderService.AttendreLaConfirmationAsync(Lire, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(60), CancellationToken.None);

        Assert.Null(lu);
        Assert.InRange(lectures, 2, 20);
    }

    [Fact]
    public void L_enregistrement_demande_puis_ouvert_en_retard_est_adopte()
    {
        var demande = new DateTime(2026, 11, 12, 8, 23, 4, DateTimeKind.Utc);
        // Le tour suivant de la boucle (1,5 s apres l'echec de la confirmation) le voit enregistrer : il est le notre.
        Assert.True(ReplayRecorderService.EnregistrementAAdopter(Enregistre, demande, demande.AddSeconds(4.5), ReplayRecorderService.FenetreDAdoption));
    }

    [Fact]
    public void Un_enregistrement_qu_on_n_a_pas_demande_n_est_pas_adopte()
    {
        var demande = new DateTime(2026, 11, 12, 8, 23, 4, DateTimeKind.Utc);
        // Aucune demande en cours (touche du joueur, autre outil) : on ne s'en empare pas.
        Assert.False(ReplayRecorderService.EnregistrementAAdopter(Enregistre, null, demande, ReplayRecorderService.FenetreDAdoption));
        // Une demande trop ancienne.
        Assert.False(ReplayRecorderService.EnregistrementAAdopter(Enregistre, demande, demande.AddMinutes(2), ReplayRecorderService.FenetreDAdoption));
        // RetroArch n'enregistre pas (ou relit un replay).
        Assert.False(ReplayRecorderService.EnregistrementAAdopter(RaActiveReplay.Idle, demande, demande.AddSeconds(2), ReplayRecorderService.FenetreDAdoption));
        Assert.False(ReplayRecorderService.EnregistrementAAdopter(new RaActiveReplay(5, 4, 100), demande, demande.AddSeconds(2), ReplayRecorderService.FenetreDAdoption));
    }

    [Fact]
    public void Le_debut_d_un_enregistrement_retire_les_images_deja_enregistrees()
    {
        // Adopte 1,5 s apres son ouverture : l'annonce arrive a l'image 9300 du wrapper, 90 images deja enregistrees.
        Assert.Equal(9210, NelfePlayScoringReporter.DebutDeLEnregistrement(9300, 90));
        // Annonce sans le compte (enregistreur d'avant) : rien ne change.
        Assert.Equal(9300, NelfePlayScoringReporter.DebutDeLEnregistrement(9300, null));
        Assert.Equal(0, NelfePlayScoringReporter.DebutDeLEnregistrement(20, 90));
    }

    [Fact]
    public void Le_replay_d_une_epreuve_contient_le_depart_et_l_arrivee()
    {
        // Relance a 09:22:19, depart pris a l'image 6554, replay ouvert seulement 1,2 s apres (le scellement du precedent
        // tenait la boucle) : il contient l'arrivee, pas le depart. Un replay plus ancien qui contient les deux l'emporte.
        var enregistrements = new List<(string Id, long Debut, long? Fin)>
        {
            ("rp_complet", 6300, 9000),
            ("rp_tardif", 6626, 9000),
        };
        Assert.Equal("rp_complet", NelfePlayScoringReporter.ReplayDuDefi(enregistrements, 6554, 8640));
    }

    [Fact]
    public void A_defaut_le_replay_d_une_epreuve_est_celui_qui_contient_l_arrivee()
    {
        var enregistrements = new List<(string Id, long Debut, long? Fin)> { ("rp_tardif", 6626, 9000) };
        Assert.Equal("rp_tardif", NelfePlayScoringReporter.ReplayDuDefi(enregistrements, 6554, 8640));
    }

    [Fact]
    public void Sans_enregistrement_qui_contient_l_arrivee_le_temps_part_sans_replay()
    {
        // Le 12/11 : EFPS7 arrete a la relance (9179), REGR ouvert apres l'arrivee (11130). Aucun ne la contient.
        var enregistrements = new List<(string Id, long Debut, long? Fin)>
        {
            ("rp_EFPS7", 6626, 9150),
            ("rp_REGR", 11260, 11400),
        };
        Assert.Null(NelfePlayScoringReporter.ReplayDuDefi(enregistrements, 9179, 11130));
        // Le meme run, l'enregistrement orphelin repris par l'enregistreur (debut recale, fin a l'arret) : il est retenu.
        enregistrements.Insert(1, ("rp_adopte", NelfePlayScoringReporter.DebutDeLEnregistrement(9150, 60), 11220));
        Assert.Equal("rp_adopte", NelfePlayScoringReporter.ReplayDuDefi(enregistrements, 9179, 11130));
    }
}
