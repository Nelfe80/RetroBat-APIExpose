using RetroBat.Api.Leaderboard;
using Xunit;
using Partie = RetroBat.Api.Leaderboard.LocalPlaysIndex.Partie;
using ReplayLocal = RetroBat.Api.Leaderboard.LocalPlaysIndex.ReplayLocal;

namespace RetroBat.Api.Tests;

/// <summary>
/// MES RECORDS montre les parties de CETTE borne (demande user 2026-10-03) : toutes celles du
/// joueur courant que la plateforme a retenues, la meilleure en haut, avec la date de la partie
/// et son replay local.
/// </summary>
public sealed class LocalPlaysIndexTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 19, 45, 0, DateTimeKind.Utc);

    private static Partie P(long score, int minutes, string verdict = "published", string regle = "1cc",
        string jeu = "metal-slug-3", string joueur = "", bool labo = false, int duree = 5)
        => new("s" + score + "-" + minutes, jeu, regle, score, false, T0.AddMinutes(minutes), T0.AddMinutes(minutes + duree),
            verdict, "home", joueur, labo);

    private static string Date(DateTime d) => d.ToString("yyyy-MM-dd HH:mm");

    [Fact]
    public void Un_passeport_se_resume()
    {
        const string json = """
            {"session_id":"abc","verdict":"published","submitted_at":"2026-10-02T19:46:38Z",
             "passport":{"game":{"rom_group":"bubble-bobble","ruleset":"1cc"},
               "context":{"world":"home","lab":null},"identity":{"session_player_id":null},
               "timing":{"started_at":"2026-10-02T19:45:04Z","ended_at":"2026-10-02T19:46:38Z"},
               "metric":{"value":"10210","ranking_direction":"higher_better"}}}
            """;
        var p = LocalPlaysIndex.Lire(json);
        Assert.NotNull(p);
        Assert.Equal("bubble-bobble", p!.RomGroup);
        Assert.Equal(10210, p.Score);
        Assert.Equal(new DateTime(2026, 10, 2, 19, 45, 4, DateTimeKind.Utc), p.DebutUtc);
        Assert.False(p.Labo);
        Assert.Equal(77, LocalPlaysIndex.Lire(json.Replace("\"10210\"", "77"))!.Score);   // un nombre JSON aussi
        Assert.Null(LocalPlaysIndex.Lire("{\"verdict\":\"published\"}"));
        Assert.Null(LocalPlaysIndex.Lire("pas du json"));
    }

    [Fact]
    public void Seules_les_parties_retenues_du_joueur_courant_comptent()
    {
        var parties = new[]
        {
            P(4500, 0),
            P(9000, 10, verdict: "refused"),            // un refus n'est pas un record
            P(8000, 20, labo: true),                    // une partie de labo non plus
            P(7000, 30, regle: "1cc-multi"),            // une autre regle a son onglet
            P(6000, 40, jeu: "bubble-bobble"),          // un autre jeu
            P(5000, 50, joueur: "QX42"),                // le joueur d'une session en salle
            P(3000, 60, verdict: "quarantined"),        // en attente : elle compte
        };
        var lignes = LocalPlaysIndex.MesParties(parties, "metal-slug-3", "1cc", "", Array.Empty<ReplayLocal>(), Date);
        Assert.Equal(new long[] { 4500, 3000 }, lignes.Select(l => l.Valeur));
        Assert.Equal(new[] { 1, 2 }, lignes.Select(l => l.Rang));
        Assert.All(lignes, l => Assert.True(l.CestMoi));
        Assert.Equal(Date(T0), lignes[0].Joueur);   // le nom laisse la place a la date

        var enSalle = LocalPlaysIndex.MesParties(parties, "metal-slug-3", "1cc", "qx42", Array.Empty<ReplayLocal>(), Date);
        Assert.Equal(new long[] { 5000 }, enSalle.Select(l => l.Valeur));
    }

    [Fact]
    public void La_meilleure_en_haut_la_plus_recente_d_abord_a_egalite()
    {
        var lignes = LocalPlaysIndex.MesParties(new[] { P(100, 0), P(300, 10), P(300, 20), P(200, 30) },
            "metal-slug-3", "1cc", "", Array.Empty<ReplayLocal>(), Date);
        Assert.Equal(new long[] { 300, 300, 200, 100 }, lignes.Select(l => l.Valeur));
        Assert.Equal(Date(T0.AddMinutes(20)), lignes[0].Joueur);
    }

    [Fact]
    public void Le_replay_de_la_partie_est_celui_qui_s_y_est_arrete()
    {
        var partie = P(4500, 0, duree: 10);
        var replays = new[]
        {
            new ReplayLocal("avant", T0.AddMinutes(-1), TimeSpan.FromMinutes(3), null),              // fini avant la partie
            new ReplayLocal("court", T0.AddMinutes(4), TimeSpan.FromMinutes(1), null),
            new ReplayLocal("long", T0.AddMinutes(10).AddSeconds(20), TimeSpan.FromMinutes(6), null), // finalise juste apres
            new ReplayLocal("apres", T0.AddMinutes(30), TimeSpan.FromMinutes(5), null),               // la partie suivante
        };
        Assert.Equal("long", LocalPlaysIndex.ReplayDeLaPartie(partie, replays));

        // Le replay qui porte le score de la partie gagne.
        var avecScore = replays.Append(new ReplayLocal("score", T0.AddMinutes(5), TimeSpan.FromMinutes(2), 4500)).ToList();
        Assert.Equal("score", LocalPlaysIndex.ReplayDeLaPartie(partie, avecScore));

        Assert.Null(LocalPlaysIndex.ReplayDeLaPartie(P(1, 100), replays));
    }
}
