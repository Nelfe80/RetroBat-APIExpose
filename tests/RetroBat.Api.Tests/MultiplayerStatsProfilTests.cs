using System.Globalization;
using System.Text.Json;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le lot 2 du CDC des statistiques : le profil de SF2CE livre par le Data Pack, la liste fermee des regles, les trois
/// statistiques par categorie et leurs etats, et le format envoye au site.
/// </summary>
public sealed class MultiplayerStatsProfilTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "stats_" + Guid.NewGuid().ToString("N"));

    public MultiplayerStatsProfilTests() => Directory.CreateDirectory(Path.Combine(_dossier, "stats", "arcade"));

    public void Dispose()
    {
        if (Directory.Exists(_dossier))
        {
            Directory.Delete(_dossier, recursive: true);
        }
    }

    private const string ProfilSf2ce = """
        {
          "id": "arcade/sf2ce/duel", "version": 1, "players": 2,
          "signals": {
            "vie": { "source": "poll", "seats": { "1": "0x83E8", "2": "0x86E8" }, "bytes": 2, "full": 144, "ko": 65535 },
            "manches": { "source": "poll", "seats": { "1": "0x864F", "2": "0x894F" }, "bytes": 1, "confirm": false }
          },
          "counters": {
            "coups": { "rule": "baisse_adverse", "signal": "vie" },
            "ko": { "rule": "atteint_adverse", "signal": "vie", "value": "ko" },
            "perfects": { "rule": "manche_gagnee_si_plein", "signal": "vie", "rounds": "manches" },
            "degats": { "rule": "somme_baisse_adverse", "signal": "vie" }
          },
          "metrics": [
            { "category": "execution", "id": "coups", "label": "multiplayer.stat.hits", "formula": "coups" },
            { "category": "efficacite", "id": "degats", "label": "multiplayer.stat.damage", "formula": "degats", "unit": "points" },
            { "category": "maitrise", "id": "perfects", "label": "multiplayer.stat.perfects", "formula": "perfects" }
          ]
        }
        """;

    private MultiplayerStatsProfil Charger(string json, out string? erreur)
    {
        File.WriteAllText(Path.Combine(_dossier, "stats", "arcade", "jeu.json"), json);
        return MultiplayerStatsProfil.Charger("stats/arcade/jeu.json", _dossier, out erreur)!;
    }

    [Fact]
    public async Task Le_profil_de_SF2CE_compte_comme_avant_et_range_ses_trois_statistiques()
    {
        var profil = Charger(ProfilSf2ce, out var erreur);
        Assert.Null(erreur);
        var ra = new FauxRetroArch();
        ra.Mots[0x83E8] = 144;
        ra.Mots[0x86E8] = 144;
        using var fin = new CancellationTokenSource();
        var suivi = new MultiplayerStatsMoteur(ra, TimeSpan.FromMilliseconds(10)).Suivre(profil, [1, 2], fin.Token);
        async Task Pose(long adresse, int valeur) { ra.Mots[adresse] = valeur; await Task.Delay(250); }

        await Task.Delay(250);
        await Pose(0x86E8, 120);      // P1 touche P2
        await Pose(0x86E8, 0xFFFF);   // K.O. de P2
        ra.Octets[0x864F] = 1;        // P1 gagne la manche, sa vie pleine : perfect
        await Task.Delay(250);
        await Pose(0x86E8, 144);
        await Pose(0x86E8, 0xFFFF);
        ra.Octets[0x864F] = 2;
        await Task.Delay(250);
        fin.Cancel();
        var resultat = await suivi.Fin;

        // Les memes chiffres que le suivi d'avant les profils (3 coups, 2 K.O., 2 perfects, 288 de degats).
        Assert.Equal(new StatsDeLaPlace(3, 2, 2, 24 + 120 + 144), resultat.Ancien(1));
        Assert.Equal(
            [("execution", 3L, "measured"), ("efficacite", 288L, "measured"), ("maitrise", 2L, "measured")],
            resultat.MesuresDe(1).Select(v => (v.Mesure.Category, v.Valeur ?? -1, v.Etat)));
        // P2 n'a rien fait : ses statistiques sont mesurees a zero, pas absentes.
        Assert.All(resultat.MesuresDe(2), v => Assert.Equal((0L, "zero"), (v.Valeur ?? -1, v.Etat)));
    }

    /// <summary>Le profil de TGM2 (releve du 27/10) : le jeu ne garde pas les lignes, elles se deduisent du niveau.</summary>
    private const string ProfilPuzzle = """
        {
          "id": "arcade/puzzle/versus", "version": 1, "players": 2,
          "signals": {
            "niveau": { "source": "poll", "seats": { "1": "0x100", "2": "0x200" }, "bytes": 2 },
            "apparitions": { "source": "poll", "seats": { "1": "0x102", "2": "0x202" }, "bytes": 2 },
            "poses": { "source": "poll", "seats": { "1": "0x104", "2": "0x204" }, "bytes": 2 }
          },
          "counters": {
            "pieces": { "rule": "hausse", "signal": "poses" },
            "niveaux": { "rule": "somme_hausse", "signal": "niveau" },
            "apparitions": { "rule": "somme_hausse", "signal": "apparitions" },
            "manches": { "rule": "atteint", "signal": "apparitions", "value": "1" },
            "tetris": { "rule": "hausse_d_au_moins", "signal": "niveau", "value": "4" }
          },
          "metrics": [
            { "category": "execution", "id": "pieces", "label": "multiplayer.stat.pieces", "formula": "pieces" },
            { "category": "efficacite", "id": "lignes", "label": "multiplayer.stat.lines", "formula": "niveaux - apparitions + manches" },
            { "category": "maitrise", "id": "tetris", "label": "multiplayer.stat.tetris", "formula": "tetris" }
          ]
        }
        """;

    [Fact]
    public async Task Les_hausses_de_la_place_comptent_pieces_lignes_et_tetris_d_une_manche_a_l_autre_jusqu_a_la_victoire()
    {
        var profil = Charger(ProfilPuzzle, out var erreur);
        Assert.Null(erreur);
        Assert.False(profil.LitLAdversaire);
        var ra = new FauxRetroArch();
        using var fin = new CancellationTokenSource();
        var suivi = new MultiplayerStatsMoteur(ra, TimeSpan.FromMilliseconds(10)).Suivre(profil, [1, 2], fin.Token);
        async Task Pose(params (long Adresse, int Valeur)[] valeurs)
        {
            foreach (var (adresse, valeur) in valeurs)
            {
                ra.Mots[adresse] = valeur;
            }
            await Task.Delay(250);
        }

        // Comme dans le jeu : une piece apparait (le niveau monte, sauf a la premiere de la manche), elle se pose, et les
        // lignes qu'elle efface montent le niveau d'autant, a l'instant ou elle se pose.
        await Task.Delay(250);
        await Pose((0x102, 1));
        await Pose((0x104, 1));
        await Pose((0x102, 2), (0x100, 1));
        await Pose((0x104, 2), (0x100, 2));    // une simple
        await Pose((0x102, 3), (0x100, 3));
        await Pose((0x104, 3));
        await Pose((0x100, 0), (0x102, 0), (0x104, 0));   // manche suivante : tout repart de zero
        await Pose((0x102, 1));
        await Pose((0x104, 1), (0x100, 4));    // un TETRIS
        await Pose((0x102, 2), (0x100, 5));
        suivi.Figer();                          // le vainqueur est designe : la suite (le gagnant seul) ne compte plus
        await Pose((0x100, 0), (0x102, 0), (0x104, 0));
        await Pose((0x102, 1), (0x104, 1), (0x100, 4));
        fin.Cancel();
        var resultat = await suivi.Fin;

        Assert.Equal(
            [("execution", 4L, "measured"), ("efficacite", 5L, "measured"), ("maitrise", 1L, "measured")],
            resultat.MesuresDe(1).Select(v => (v.Mesure.Category, v.Valeur ?? -1, v.Etat)));
        Assert.All(resultat.MesuresDe(2), v => Assert.Equal((0L, "zero"), (v.Valeur ?? -1, v.Etat)));
        // Sans coups ni degats, le format d'avant les profils ne part pas.
        Assert.Null(resultat.AncienDesPlaces());
    }

    [Theory]
    [InlineData(", \"value\": \"4\"", "", "hausse minimale inconnue")]
    [InlineData("\"niveaux - apparitions + manches\"", "\"niveaux apparitions\"", "statistique « lignes » incoherente")]
    [InlineData("\"niveaux - apparitions + manches\"", "\"niveaux - inconnu\"", "statistique « lignes » incoherente")]
    [InlineData("\"niveaux - apparitions + manches\"", "\"niveaux -\"", "statistique « lignes » incoherente")]
    [InlineData("\"value\": \"1\"", "\"value\": \"x\"", "valeur a atteindre inconnue")]
    public void Un_profil_de_puzzle_incoherent_est_refuse(string avant, string apres, string raison)
    {
        Assert.Contains(avant, ProfilPuzzle);
        Assert.Null(Charger(ProfilPuzzle.Replace(avant, apres), out var erreur));
        Assert.Contains(raison, erreur);
    }

    [Theory]
    [InlineData("le", false, new[] { 0x34, 0x12 }, 0x1234)]
    [InlineData("be", false, new[] { 0x12, 0x34 }, 0x1234)]
    [InlineData("68k", true, new[] { 0x00, 0x00, 0x50, 0x12 }, 1250)]
    [InlineData("68k", true, new[] { 0x01, 0x00, 0x00, 0x99 }, 19900)]
    [InlineData("le", true, new[] { 0x5A }, -1)]
    public void Un_score_se_lit_dans_son_ordre_et_en_BCD(string ordre, bool bcd, int[] octets, int attendu)
        => Assert.Equal(attendu < 0 ? null : attendu, new MultiplayerStatsSignal { Bytes = octets.Length, Order = ordre, Bcd = bcd }.Valeur(octets));

    [Fact]
    public void La_plus_grande_hausse_garde_le_meilleur_coup()
    {
        var profil = new MultiplayerStatsProfil
        {
            Id = "x", Players = 2,
            Signals = new Dictionary<string, MultiplayerStatsSignal> { ["score"] = new() { Seats = new Dictionary<string, string> { ["1"] = "0x10", ["2"] = "0x20" } } },
            Counters = new Dictionary<string, MultiplayerStatsCompteur> { ["meilleur"] = new() { Rule = "plus_grande_hausse", Signal = "score" } },
        };
        var suivi = new MultiplayerStatsMoteur(new RetroArchMuet()).Suivre(profil, [1, 2], new CancellationToken(true));
        var avant = new Dictionary<(string, int), int?>();
        foreach (var valeur in new[] { 0, 30, 30, 130, 150, 0, 60 })
        {
            var nouvelles = new Dictionary<(string, int), int?> { [("score", 1)] = valeur, [("score", 2)] = 0 };
            suivi.Appliquer(nouvelles, avant);
            avant = nouvelles;
        }

        Assert.Equal(100, suivi.Instantane().Compteurs[1]["meilleur"]);
    }

    [Fact]
    public void La_plus_grande_baisse_adverse_garde_le_meilleur_coup_porte()
    {
        var profil = new MultiplayerStatsProfil
        {
            Id = "x", Players = 2,
            Signals = new Dictionary<string, MultiplayerStatsSignal>
            {
                ["vie"] = new() { Seats = new Dictionary<string, string> { ["1"] = "0x10", ["2"] = "0x20" }, Full = 223, Ko = 0 },
            },
            Counters = new Dictionary<string, MultiplayerStatsCompteur> { ["meilleur"] = new() { Rule = "plus_grande_baisse_adverse", Signal = "vie" } },
        };
        var suivi = new MultiplayerStatsMoteur(new RetroArchMuet()).Suivre(profil, [1, 2], new CancellationToken(true));
        var avant = new Dictionary<(string, int), int?>();
        // La vie de P2 : trois coups de P1 (20, 55, 10), puis le K.O. (la vie restante, 30).
        foreach (var vie in new[] { 223, 203, 203, 148, 138, 30, 0 })
        {
            var nouvelles = new Dictionary<(string, int), int?> { [("vie", 1)] = 223, [("vie", 2)] = vie };
            suivi.Appliquer(nouvelles, avant);
            avant = nouvelles;
        }

        Assert.Equal(108, suivi.Instantane().Compteurs[1]["meilleur"]);
        Assert.Equal(0, suivi.Instantane().Compteurs[2]["meilleur"]);
    }

    [Fact]
    public void Le_maximum_garde_la_plus_haute_valeur_vue_d_une_course_a_l_autre()
    {
        var profil = new MultiplayerStatsProfil
        {
            Id = "x", Players = 2,
            Signals = new Dictionary<string, MultiplayerStatsSignal> { ["pieces"] = new() { Seats = new Dictionary<string, string> { ["1"] = "0x10", ["2"] = "0x20" } } },
            Counters = new Dictionary<string, MultiplayerStatsCompteur> { ["record"] = new() { Rule = "maximum", Signal = "pieces" } },
        };
        var suivi = new MultiplayerStatsMoteur(new RetroArchMuet()).Suivre(profil, [1, 2], new CancellationToken(true));
        var avant = new Dictionary<(string, int), int?>();
        foreach (var valeur in new[] { 3, 12, 28, 0, 9, 19 })
        {
            var nouvelles = new Dictionary<(string, int), int?> { [("pieces", 1)] = valeur, [("pieces", 2)] = 0 };
            suivi.Appliquer(nouvelles, avant);
            avant = nouvelles;
        }

        // La premiere lecture (3) ne compte pas : elle peut venir d'avant le depart.
        Assert.Equal(28, suivi.Instantane().Compteurs[1]["record"]);
    }

    [Fact]
    public async Task KOF98_la_vie_sous_zero_est_un_KO_et_la_recharge_entre_deux_manches_ne_compte_pas()
    {
        var profil = Charger("""
            {
              "id": "arcade/kof98/duel", "version": 1, "players": 2, "settle_after_round_ms": 600,
              "signals": {
                "vie": { "source": "poll", "seats": { "1": "0x8238", "2": "0x8438" }, "bytes": 1, "full": 103 },
                "manches": { "source": "poll", "seats": { "1": "0xA85F", "2": "0xA84C" }, "bytes": 1, "confirm": false }
              },
              "counters": {
                "coups": { "rule": "baisse_adverse", "signal": "vie" },
                "degats": { "rule": "somme_baisse_adverse", "signal": "vie" },
                "perfects": { "rule": "manche_gagnee_si_plein", "signal": "vie", "rounds": "manches" }
              },
              "metrics": [
                { "category": "execution", "id": "coups", "label": "multiplayer.stat.hits", "formula": "coups" },
                { "category": "efficacite", "id": "degats", "label": "multiplayer.stat.damage", "formula": "degats" },
                { "category": "maitrise", "id": "perfects", "label": "multiplayer.stat.perfects", "formula": "perfects" }
              ]
            }
            """, out var erreur);
        Assert.Null(erreur);
        var ra = new FauxRetroArch();
        ra.Octets[0x8238] = 103;
        ra.Octets[0x8438] = 103;
        using var fin = new CancellationTokenSource();
        var suivi = new MultiplayerStatsMoteur(ra, TimeSpan.FromMilliseconds(10)).Suivre(profil, [1, 2], fin.Token);
        async Task Pose(long adresse, int valeur) { ra.Octets[adresse] = valeur; await Task.Delay(150); }

        await Task.Delay(150);
        await Pose(0x8438, 93);     // P1 touche P2 : 10
        await Pose(0x8238, 24);     // P2 touche P1 : 79
        await Pose(0x8438, 7);      // P1 touche P2 : 86
        await Pose(0x8438, 253);    // K.O. de P2 : la vie passe sous zero, le coup compte les 7 qui restaient
        await Pose(0xA85F, 1);      // P1 elimine le premier combattant de P2
        await Pose(0x8438, 103);    // le suivant arrive
        await Pose(0x8238, 103);    // la vie de P1 passe a 103 un instant...
        await Pose(0x8238, 34);     // ... puis retombe a 34 : pas un coup
        await Task.Delay(700);
        await Pose(0x8438, 90);     // le calme passe : P1 touche P2 (13)
        fin.Cancel();
        var resultat = await suivi.Fin;

        Assert.Equal(
            [("execution", 4L), ("efficacite", 10L + 86 + 7 + 13), ("maitrise", 0L)],
            resultat.MesuresDe(1).Select(v => (v.Mesure.Category, v.Valeur ?? -1)));
        Assert.Equal([("execution", 1L), ("efficacite", 79L)], resultat.MesuresDe(2).Take(2).Select(v => (v.Mesure.Category, v.Valeur ?? -1)));
    }

    [Fact]
    public async Task Un_RetroArch_muet_ne_donne_que_des_statistiques_non_mesurees()
    {
        var profil = Charger(ProfilSf2ce, out _);
        using var fin = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var resultat = await new MultiplayerStatsMoteur(new RetroArchMuet(), TimeSpan.FromMilliseconds(10)).Suivre(profil, [1, 2], fin.Token).Fin;

        Assert.All(resultat.MesuresDe(1).Concat(resultat.MesuresDe(2)), v => Assert.Equal((null, "unavailable"), (v.Valeur, v.Etat)));
    }

    [Fact]
    public void Le_suivi_se_lit_en_cours_de_route_et_part_au_format_generique()
    {
        var profil = Charger(ProfilSf2ce, out _);
        using var fin = new CancellationTokenSource();
        var suivi = new MultiplayerStatsMoteur(new RetroArchMuet(), TimeSpan.FromMilliseconds(10)).Suivre(profil, [1, 2], fin.Token);

        var json = JsonSerializer.Serialize(suivi.Instantane().PourLeSite());
        fin.Cancel();

        Assert.Equal(
            """{"profile":{"id":"arcade/sf2ce/duel","version":1},"seats":{"1":{"coups":{"value":null,"state":"unavailable"},"degats":{"value":null,"state":"unavailable"},"perfects":{"value":null,"state":"unavailable"}},"2":{"coups":{"value":null,"state":"unavailable"},"degats":{"value":null,"state":"unavailable"},"perfects":{"value":null,"state":"unavailable"}}}}""",
            json);
    }

    [Theory]
    [InlineData("\"rule\": \"baisse_adverse\", \"signal\": \"vie\" }", "\"rule\": \"inventee\", \"signal\": \"vie\" }", "regle inconnue")]
    [InlineData("\"category\": \"maitrise\"", "\"category\": \"execution\"", "une statistique par categorie")]
    [InlineData("\"2\": \"0x86E8\"", "\"3\": \"0x86E8\"", "pas d'adresse pour la place 2")]
    [InlineData("\"formula\": \"perfects\"", "\"formula\": \"inconnu\"", "statistique « perfects » incoherente")]
    [InlineData("\"rounds\": \"manches\"", "\"rounds\": \"absent\"", "manches manquantes")]
    public void Un_profil_incoherent_est_refuse(string avant, string apres, string raison)
    {
        Assert.Contains(avant, ProfilSf2ce);
        Assert.Null(Charger(ProfilSf2ce.Replace(avant, apres), out var erreur));
        Assert.Contains(raison, erreur);
    }

    [Fact]
    public void Un_chemin_hors_de_stats_est_refuse()
    {
        Assert.Null(MultiplayerStatsProfil.Charger("../catalogue.json", _dossier, out var erreur));
        Assert.Contains("chemin de profil refuse", erreur);
        Assert.Null(MultiplayerStatsProfil.Charger("stats/arcade/absent.json", _dossier, out erreur));
        Assert.Contains("illisible", erreur);
    }

    [Fact]
    public void Sans_fichier_le_profil_implicite_reprend_le_bloc_de_vie_et_retombe_dessus_si_le_fichier_manque()
    {
        var jeu = new MultiplayerGame
        {
            Id = "arcade/sf2ce", System = "arcade", Players = 2,
            End = new MultiplayerEnd { FirstTo = 2, Seats = [new() { Seat = 1, CoreRam = "0x864F" }, new() { Seat = 2, CoreRam = "0x894F" }] },
            Stats = new MultiplayerStats
            {
                Profile = "stats/arcade/absent.json",
                Life = new MultiplayerStatLife { Bytes = 2, Full = 144, Ko = 0xFFFF, Seats = [new() { Seat = 1, CoreRam = "0x83E8" }, new() { Seat = 2, CoreRam = "0x86E8" }] },
            },
        };

        var profil = MultiplayerStatsProfil.Pour(jeu, out var erreur, _dossier);

        Assert.Contains("illisible", erreur);
        Assert.Equal((0, "arcade/sf2ce/implicite"), (profil!.Version, profil.Id));
        Assert.Equal(["execution", "efficacite", "maitrise"], profil.Metrics.Select(m => m.Category));
        Assert.Null(MultiplayerStatsProfil.Implicite(jeu with { Stats = null }));
    }

    /// <summary>Un RetroArch de test : vies sur deux octets, manches sur un.</summary>
    private sealed class FauxRetroArch : ICommandesRetroArch
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Mots = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Octets = new();

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            var m = commande.Split(' ');
            var adresse = long.Parse(m[1], NumberStyles.HexNumber);
            if (m[2] == "2")
            {
                var v = Mots.GetValueOrDefault(adresse);
                return Task.FromResult<string?>($"READ_CORE_RAM {m[1]} {v & 0xFF:x2} {(v >> 8) & 0xFF:x2}");
            }

            return Task.FromResult<string?>($"READ_CORE_RAM {m[1]} {Octets.GetValueOrDefault(adresse):x2}");
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RetroArchMuet : ICommandesRetroArch
    {
        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500) => Task.FromResult<string?>(null);

        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
    }
}
