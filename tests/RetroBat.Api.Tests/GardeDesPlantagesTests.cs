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
}
