using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>La sante de l'emission d'un direct (14/11) : lue aux lignes de l'exe, la borne rend l'antenne si elle patine.</summary>
public sealed class SanteDeLEmissionTests
{
    private const string Saut = "21:04:10 regulation : 6.4 s de retard, saut jusqu'a l'image cle suivante";
    private const string Baisse = "21:04:10 regulation : debit 2000 kbit/s (l'envoi n'ecoule pas, retard 2.1 s, file 3.2 s)";
    private const string Remontee = "21:05:10 regulation : debit 3000 kbit/s (une minute sans retard, retard 0.0 s, file 0.2 s)";

    // Une ligne de point de l'exe 0.7.2, telle qu'il l'ecrit toutes les 10 s.
    private static string Point(double s, double mo, int kbps)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"21:04:{(int)s % 60:00} {s:F0} s, images lues 600, encodees 600, envoyees 600, sautees 0, blocs de son 470, {mo:F1} Mo envoyes, "
            + $"processeur 0.80 coeur(s), fabrique 1.40x le temps reel, retard max 0.20 s ; par image : decodage 2.0 ms, "
            + $"rotation 0.0 ms, encodage 4.0 ms ; debit {kbps} kbit/s, 60 images/s");

    [Fact]
    public void Une_emission_saine_ne_coupe_jamais()
    {
        var sante = new SanteDeLEmission();
        for (var i = 1; i <= 30; i++) Assert.Null(sante.Observer(i * 10, Point(i * 10, i * 3.7, 3000)));
        Assert.Null(sante.RaisonDeCouper);
    }

    [Fact]
    public void Trois_sauts_en_90_secondes_rendent_l_antenne()
    {
        var sante = new SanteDeLEmission();
        Assert.Null(sante.Observer(10, Saut));
        Assert.Null(sante.Observer(25, Saut));
        Assert.StartsWith("3 sauts", sante.Observer(40, Saut));
        // La raison reste, et ne se redit pas.
        Assert.Null(sante.Observer(50, Saut));
        Assert.NotNull(sante.RaisonDeCouper);
    }

    [Fact]
    public void Des_sauts_espaces_ne_coupent_pas()
    {
        // Un a-coup de temps en temps (le reseau se fige 8 s) : le regulateur rattrape, le direct continue.
        var sante = new SanteDeLEmission();
        Assert.Null(sante.Observer(10, Saut));
        Assert.Null(sante.Observer(110, Saut));
        Assert.Null(sante.Observer(210, Saut));
        Assert.Null(sante.RaisonDeCouper);
    }

    [Fact]
    public void Un_saut_au_plus_bas_debit_rend_l_antenne()
    {
        var sante = new SanteDeLEmission();
        Assert.Null(sante.Observer(10, Baisse));
        Assert.Null(sante.Observer(20, Baisse));
        Assert.Equal("saut d'images au plus bas debit", sante.Observer(30, Saut));
    }

    [Fact]
    public void Un_debit_remonte_redonne_de_la_marge()
    {
        var sante = new SanteDeLEmission();
        Assert.Null(sante.Observer(10, Baisse));
        Assert.Null(sante.Observer(20, Baisse));
        Assert.Null(sante.Observer(80, Remontee));
        Assert.Null(sante.Observer(85, Saut));
    }

    [Fact]
    public void Un_debit_reel_bas_et_engorge_pendant_30_s_rend_l_antenne()
    {
        // 3000 kbit/s vises, 0,5 Mo envoyes en 10 s = 400 kbit/s, et l'exe dit que l'envoi n'ecoule pas.
        var sante = new SanteDeLEmission();
        Assert.Null(sante.Observer(10, Point(10, 3.0, 3000)));
        Assert.Null(sante.Observer(12, Baisse));
        Assert.Null(sante.Observer(20, Point(20, 3.5, 3000)));
        Assert.Null(sante.Observer(30, Point(30, 4.0, 3000)));
        Assert.StartsWith("debit envoye 400 kbit/s pour 3000", sante.Observer(40, Point(40, 4.5, 3000)));
    }

    [Fact]
    public void Un_debit_bas_sans_engorgement_est_une_image_fixe()
    {
        // Un ecran fixe s'encode en peu d'octets : sans signe d'engorgement, rien ne coupe.
        var sante = new SanteDeLEmission();
        for (var i = 1; i <= 10; i++) Assert.Null(sante.Observer(i * 10, Point(i * 10, 3.0 + i * 0.1, 3000)));
        Assert.Null(sante.RaisonDeCouper);
    }
}
