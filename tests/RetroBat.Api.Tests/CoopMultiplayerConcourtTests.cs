using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Une coop de MULTIPLAYER concourt de base (decision user du 2026-10-31) : 1CC MULTI et 1LC MULTI de chaque place, depuis
/// le depart pret que l'hote charge et que le passeport nomme.
/// </summary>
[Collection("MultiplayerScoring")]
public class CoopMultiplayerConcourtTests
{
    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? Regle(JsonElement? profil) => profil?.GetProperty("ruleset").GetString();

    private static PartieMultiplayerCertifiee Partie(string? etat = "c68b590e", int chargements = 1) =>
        new("0123456789abcdef0123456789abcdef", 2, 2, 2, @"E:\roms\fbneo\ddragon.zip", etat,
            [new ChoixDuDepart(1, "machine", 2), new ChoixDuDepart(2, "machine", 0)], chargements);

    [Fact]
    public void Le_1LC_MULTI_n_est_jamais_le_1LC_d_une_partie_seule()
    {
        Assert.True(ModesDeJeu.Est1LCMulti("1lc-multi"));
        Assert.True(ModesDeJeu.Est1LCMulti("1lc-multi-super"));
        Assert.False(ModesDeJeu.Est1LCMulti("1lc"));
        Assert.False(ModesDeJeu.Est1LC("1lc-multi"));
        Assert.True(ModesDeJeu.Est1LC("1lc-super"));

        var unLC = Profil("""{"ruleset":"1lc"}""");
        var unLCMulti = Profil("""{"ruleset":"1lc-multi"}""");
        var unCC = Profil("""{"ruleset":"1cc"}""");
        var unCCMulti = Profil("""{"ruleset":"1cc-multi"}""");
        // L'ordre ne compte plus : chacun prend le sien.
        Assert.Equal("1lc", Regle(ModesDeJeu.ChoisirProfil1LC([unLCMulti, unLC, unCC], null)));
        Assert.Equal("1lc-multi", Regle(ModesDeJeu.ChoisirProfil1LCMulti([unLC, unCC, unCCMulti, unLCMulti], null)));
        Assert.Equal("1cc-multi", Regle(ModesDeJeu.ChoisirProfilMulti([unLCMulti, unCCMulti], null)));
        Assert.Null(ModesDeJeu.ChoisirProfil1LCMulti([unLC, unCC, unCCMulti], null));
        Assert.Equal(["1cc"], ModesDeJeu.DuSolo([unLC, unLCMulti, unCC, unCCMulti]).Select(p => Regle(p)));
        Assert.Equal(["1lc", "1cc"], ModesDeJeu.SansLeMulti([unLC, unLCMulti, unCC, unCCMulti]).Select(p => Regle(p)));
    }

    [Fact]
    public void Une_partie_posee_se_prend_une_fois_et_se_perd_avec_le_temps()
    {
        var maintenant = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        MultiplayerScoring.Poser(Partie(), maintenant);
        Assert.NotNull(MultiplayerScoring.Prendre(maintenant.AddSeconds(20)));
        Assert.Null(MultiplayerScoring.Prendre(maintenant.AddSeconds(21)));

        MultiplayerScoring.Poser(Partie(), maintenant);
        Assert.Null(MultiplayerScoring.Prendre(maintenant + MultiplayerScoring.Validite + TimeSpan.FromSeconds(1)));

        MultiplayerScoring.Poser(Partie(), maintenant);
        MultiplayerScoring.Oublier();
        Assert.Null(MultiplayerScoring.Prendre(maintenant));
    }

    [Fact]
    public void Seules_les_coop_sorties_au_World_Scoring_concourent()
    {
        var sortie = new MultiplayerScoringDuJeu { RomGroup = "double-dragon" };
        Assert.True(MultiplayerScoring.Concourt(new MultiplayerGame { Id = "arcade/ddragon", Family = "coop", Scoring = sortie }));
        // Profils ouverts mais depart pas encore admis (19xx, 31/10) : il ne concourt pas, aucun refus apres chaque partie.
        Assert.False(MultiplayerScoring.Concourt(new MultiplayerGame { Id = "arcade/19xx", Family = "coop" }));
        Assert.False(MultiplayerScoring.Concourt(new MultiplayerGame { Id = "arcade/sf2ce", Family = "duel", Scoring = sortie }));
    }

    [Fact]
    public void Le_catalogue_dit_la_coop_qui_concourt()
    {
        var jeu = JsonSerializer.Deserialize<MultiplayerGame>("{\"id\":\"arcade/ddragon\",\"family\":\"coop\",\"scoring\":{\"rom_group\":\"double-dragon\"}}")!;
        Assert.Equal("double-dragon", jeu.Scoring?.RomGroup);
        Assert.True(MultiplayerScoring.Concourt(jeu));
    }

    [Fact]
    public void Les_choix_ecrits_sont_ceux_que_le_catalogue_sait_ecrire()
    {
        var jeu = new MultiplayerGame
        {
            Id = "arcade/19xx", Family = "coop",
            Choices = [new MultiplayerChoice { Key = "machine" }],
        };
        var choix = MultiplayerScoring.ChoixEcrits(jeu, new Dictionary<int, IReadOnlyDictionary<string, int>?>
        {
            [2] = new Dictionary<string, int> { ["machine"] = 0, ["decor"] = 4 },
            [1] = new Dictionary<string, int> { ["machine"] = 2 },
            [3] = null,
        });

        Assert.Equal([new ChoixDuDepart(1, "machine", 2), new ChoixDuDepart(2, "machine", 0)], choix);
    }

    [Fact]
    public void Le_depart_du_brouillon_nomme_l_etat_ses_choix_et_ses_chargements()
    {
        var depart = NelfePlayScoringReporter.DepartDuBrouillon(Partie())!;

        Assert.Equal("c68b590e", (string?)depart["etat"]);
        Assert.Equal(1, (int?)depart["chargements_permis"]);
        Assert.Equal("""[{"seat":1,"key":"machine","value":2},{"seat":2,"key":"machine","value":0}]""", depart["choix"]!.ToJsonString());
        Assert.Null(NelfePlayScoringReporter.DepartDuBrouillon(null));
    }

    [Fact]
    public void Le_1LC_MULTI_ne_compte_que_les_morts_de_son_joueur()
    {
        var vies = new List<EvenementDeVie>
        {
            new("0x3ea", true, 1, 500, 1),
            new("0x448", true, 1, 700, 2),
            new("0x448", false, 2, 800, 2),
            new("0x3c1", true, null, 900, 0),
        };

        Assert.Equal([700L], NelfePlayScoringReporter.PertesDuJoueur(vies, 2).Select(v => v.Frame));
        // Une ligne sans joueur vaut pour le joueur 1.
        Assert.Equal([500L, 900L], NelfePlayScoringReporter.PertesDuJoueur(vies, 1).Select(v => v.Frame));

        // Le run du joueur 2 se coupe a SA mort, pas a celle du joueur 1.
        var run = new List<(long frame, long total)> { (100, 0), (600, 1000), (650, 1500), (750, 2500) };
        var pertes = NelfePlayScoringReporter.PertesDuJoueur(vies, 2).Select(v => (v.Frame, 1)).ToList();
        var (run1lc, mort) = PremiereVie.Couper(run, pertes);
        Assert.Equal(700, mort);
        Assert.Equal(1500, run1lc[^1].total);
    }
}

[CollectionDefinition("MultiplayerScoring", DisableParallelization = true)]
public class MultiplayerScoringCollection;
