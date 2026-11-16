using System.Text.Json;
using RetroBat.Api.Replay.Recording;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le garde-fou des replays (2026-10-03) : un jeu dont l'enregistrement a fait mourir RetroArch ne
/// s'enregistre plus sur la borne, pour ce coeur et ce RetroArch ; une partie finie proprement ne
/// change rien.
/// </summary>
public sealed class GardeDesPlantagesTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-garde-" + Guid.NewGuid().ToString("N"));
    private DateTime _maintenant = new(2026, 10, 3, 0, 17, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { }
    }

    private string Fichier => Path.Combine(_dossier, "sans-enregistrement.json");

    private GardeDesPlantages Garde() => new(Fichier, () => _maintenant);

    private static string Cle(string coeur = "c0ffee0123456789") =>
        GardeDesPlantages.Cle("fb_alpha", "1942", "a0b1c2d3", coeur, "1.22.2+123+20250101000000");

    /// <summary>Un jeu NelfePlay qui s'enregistre ; <paramref name="propre"/> : sa fin de partie arrive.</summary>
    private void Jouer(GardeDesPlantages garde, bool propre, bool enregistre = true, bool ecoute = true)
    {
        garde.JeuLance();
        if (ecoute) garde.EcouteVue("c0ffee0123456789");
        if (enregistre) garde.EnregistrementLance(Cle(), "1942", "1.22.2+123+20250101000000");
        _maintenant = _maintenant.AddSeconds(5);
        if (propre) garde.FinDePartieRecue();
        garde.JeuTermine();
    }

    [Fact]
    public void Une_partie_finie_proprement_ne_change_rien()
    {
        var garde = Garde();
        Jouer(garde, propre: true);
        _maintenant = _maintenant.AddSeconds(6);
        Assert.Null(garde.Juger());
        Assert.Null(garde.RenoncementPour(Cle()));
    }

    [Fact]
    public void RetroArch_mort_pendant_l_enregistrement_fait_renoncer_a_ce_jeu()
    {
        var garde = Garde();
        Jouer(garde, propre: false);

        // Pas avant le delai : la fin de partie peut encore arriver.
        Assert.Null(garde.Juger());
        _maintenant = _maintenant.AddSeconds(6);
        var renoncement = garde.Juger();

        Assert.NotNull(renoncement);
        Assert.Equal("1942", renoncement!.Jeu);
        Assert.NotNull(garde.RenoncementPour(Cle()));
        // Garde sur disque : un redemarrage de l'API s'en souvient.
        Assert.NotNull(Garde().RenoncementPour(Cle()));
    }

    [Fact]
    public void Une_fin_de_partie_qui_arrive_apres_la_fin_du_jeu_compte_encore()
    {
        var garde = Garde();
        Jouer(garde, propre: false);
        garde.FinDePartieRecue();
        _maintenant = _maintenant.AddSeconds(6);
        Assert.Null(garde.Juger());
    }

    [Fact]
    public void Sans_enregistrement_ou_sans_ecoute_rien_n_est_juge()
    {
        var garde = Garde();
        Jouer(garde, propre: false, enregistre: false);
        _maintenant = _maintenant.AddSeconds(6);
        Assert.Null(garde.Juger());

        Jouer(garde, propre: false, ecoute: false);
        _maintenant = _maintenant.AddSeconds(6);
        Assert.Null(garde.Juger());
        Assert.False(File.Exists(Fichier));
    }

    [Fact]
    public void Un_autre_coeur_ou_un_autre_RetroArch_fait_reessayer()
    {
        var garde = Garde();
        Jouer(garde, propre: false);
        _maintenant = _maintenant.AddSeconds(6);
        Assert.NotNull(garde.Juger());

        Assert.Null(garde.RenoncementPour(Cle("feedface01234567")));
        Assert.Null(garde.RenoncementPour(GardeDesPlantages.Cle("fb_alpha", "1942", "a0b1c2d3", "c0ffee0123456789", "1.22.2+999+20261101000000")));
    }

    [Fact]
    public void Le_coeur_se_lit_dans_l_attestation()
    {
        var wrapper = JsonSerializer.SerializeToElement(new { CoreSha256 = "C0FFEE0123456789ABCDEF", CoreName = "FinalBurn Neo", CoreVersion = "v1.0.0.03" });
        var sansEmpreinte = JsonSerializer.SerializeToElement(new { CoreSha256 = (string?)null, CoreName = "MAME", CoreVersion = "0.289" });
        var vide = JsonSerializer.SerializeToElement(new { Rom = "1942" });

        Assert.Equal("c0ffee0123456789", GardeDesPlantages.CoeurDeLAttestation(wrapper));
        Assert.Equal("MAME 0.289", GardeDesPlantages.CoeurDeLAttestation(sansEmpreinte));
        Assert.Equal("?", GardeDesPlantages.CoeurDeLAttestation(vide));
    }

    // ── L'inscription expire (16/11) ─────────────────────────────────────────

    /// <summary>Une partie ou RetroArch meurt pendant l'enregistrement, jugee : rend l'inscription.</summary>
    private GardeDesPlantages.Renoncement Planter(GardeDesPlantages garde)
    {
        Jouer(garde, propre: false);
        _maintenant = _maintenant.AddSeconds(6);
        return garde.Juger()!;
    }

    [Fact]
    public void L_inscription_expire_apres_sept_jours_et_le_jeu_est_retente()
    {
        var garde = Garde();
        var r = Planter(garde);
        Assert.Equal(TimeSpan.FromDays(7), garde.DelaiDeBase);
        Assert.Equal(r.Le.AddDays(7), r.Jusqua);
        Assert.Single(r.Plantages!);

        _maintenant = r.Le.AddDays(7).AddSeconds(-1);
        Assert.NotNull(garde.RenoncementPour(Cle()));
        _maintenant = r.Le.AddDays(7);
        Assert.Null(garde.RenoncementPour(Cle()));
        // L'historique reste, sur disque aussi.
        Assert.NotNull(garde.InscriptionPour(Cle()));
        var relue = new GardeDesPlantages(Fichier, () => _maintenant);
        Assert.Null(relue.RenoncementPour(Cle()));
        Assert.Equal(1, relue.InscriptionPour(Cle())!.NombreDePlantages);
    }

    [Fact]
    public void Une_recidive_double_le_delai_et_garde_l_historique()
    {
        var garde = Garde();
        var premier = Planter(garde);

        _maintenant = premier.Jusqua!.Value.AddHours(1);
        Assert.Null(garde.RenoncementPour(Cle()));
        var second = Planter(garde);
        Assert.Equal(second.Le.AddDays(14), second.Jusqua);
        Assert.Equal(new[] { premier.Le, second.Le }, second.Plantages);

        _maintenant = second.Jusqua!.Value.AddHours(1);
        var troisieme = Planter(garde);
        Assert.Equal(troisieme.Le.AddDays(28), troisieme.Jusqua);
        Assert.Equal(3, troisieme.NombreDePlantages);
        Assert.Equal(3, new GardeDesPlantages(Fichier, () => _maintenant).InscriptionPour(Cle())!.Plantages!.Count);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 14)]
    [InlineData(3, 28)]
    [InlineData(4, 56)]
    [InlineData(6, 224)]
    [InlineData(7, 365)]
    [InlineData(40, 365)]
    public void Le_delai_double_jusqu_au_plafond(int plantages, int jours)
        => Assert.Equal(TimeSpan.FromDays(jours), GardeDesPlantages.Delai(TimeSpan.FromDays(7), plantages));

    [Fact]
    public void Le_delai_de_base_se_regle()
    {
        var garde = new GardeDesPlantages(Fichier, () => _maintenant, TimeSpan.FromDays(3));
        var r = Planter(garde);
        Assert.Equal(r.Le.AddDays(3), r.Jusqua);
        Assert.Equal(TimeSpan.FromDays(7), new GardeDesPlantages(Fichier, () => _maintenant, TimeSpan.Zero).DelaiDeBase);
    }

    [Fact]
    public void Une_inscription_v1_sans_date_de_fin_expire_sept_jours_apres_son_plantage()
    {
        // Le fichier d'Old AF Gamer : 19XX inscrit le 15/11 par la 1.9.192, sans date de fin.
        Directory.CreateDirectory(_dossier);
        var le = new DateTime(2026, 11, 15, 21, 4, 0, DateTimeKind.Utc);
        File.WriteAllText(Fichier, JsonSerializer.Serialize(new
        {
            schema = "nelfe.replay.sans-enregistrement.v1",
            jeux = new[] { new { cle = Cle(), jeu = "19xx", coeur = "c0ffee0123456789", retroArch = "1.22.2+123+20250101000000", le } },
        }));

        _maintenant = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var garde = Garde();
        var r = garde.RenoncementPour(Cle());
        Assert.NotNull(r);
        Assert.Equal(le.AddDays(7), garde.ProchainEssai(r!));
        _maintenant = le.AddDays(7);
        Assert.Null(garde.RenoncementPour(Cle()));

        // Il replante au nouvel essai : c'est sa deuxieme fois, 14 jours.
        var second = Planter(garde);
        Assert.Equal(2, second.NombreDePlantages);
        Assert.Equal(second.Le.AddDays(14), second.Jusqua);
    }

    [Fact]
    public void Lever_fait_reessayer_tout_de_suite_et_un_nouveau_plantage_compte_comme_recidive()
    {
        var garde = Garde();
        var r = Planter(garde);
        _maintenant = _maintenant.AddHours(2);

        Assert.Empty(garde.Lever("cle inconnue"));
        var levees = garde.Lever(Cle());
        Assert.Single(levees);
        Assert.Equal(_maintenant, levees[0].LeveeLe);
        Assert.Null(garde.RenoncementPour(Cle()));
        Assert.Empty(garde.Lever());
        Assert.Equal(_maintenant, new GardeDesPlantages(Fichier, () => _maintenant).InscriptionPour(Cle())!.LeveeLe);

        var second = Planter(garde);
        Assert.Equal(2, second.NombreDePlantages);
        Assert.Equal(second.Le.AddDays(14), second.Jusqua);
        Assert.Null(second.LeveeLe);
        Assert.Equal(r.Le, second.Plantages![0]);
    }
}
