using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE SONIC THE HEDGEHOG 2 (Mega Drive, (World) crc32 24ab4c3a, prepares le 06/11, NON ouverts) : 19 niveaux en
/// depart direct sur l'etat du carton (Sonic seul), et l'Any%. Les traces viennent du banc Genesis Plus GX sans fenetre du 06/11
/// (joueur automatique ; Sonic deplace en memoire pres de l'arrivee pour la mesure seulement, combat du boss abrege) : chaque
/// trace porte, image par image depuis le chargement de l'etat, les adresses du .MEM (.user) qui ont change.
/// <para>Differences avec Sonic 1 : le drapeau du chrono 0xFE1F est deja a 1 pendant le carton et une mort ne le coupe pas ;
/// le depart cale est la main au joueur (0xF601 de 0x8C a 0x0C), le chrono du jeu court des l'image suivante ; les actes a
/// capsule n'ont pas de fin d'acte 0xF7CD.</para>
/// </summary>
public class SpeedrunSonic2Tests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"sonic-the-hedgehog-2.{slug}.json")));

    private static byte[] Mem => File.ReadAllBytes(Fichier("sonic-the-hedgehog-2.MEM"));

    public static readonly TheoryData<string, int, int> Niveaux = new()
    {
        { "emerald-hill-1", 0x00, 0 }, { "emerald-hill-2", 0x00, 1 }, { "chemical-plant-1", 0x0D, 0 }, { "chemical-plant-2", 0x0D, 1 },
        { "aquatic-ruin-1", 0x0F, 0 }, { "aquatic-ruin-2", 0x0F, 1 }, { "casino-night-1", 0x0C, 0 }, { "casino-night-2", 0x0C, 1 },
        { "hill-top-1", 0x07, 0 }, { "hill-top-2", 0x07, 1 }, { "mystic-cave-1", 0x0B, 0 }, { "mystic-cave-2", 0x0B, 1 },
        { "oil-ocean-1", 0x0A, 0 }, { "oil-ocean-2", 0x0A, 1 }, { "metropolis-1", 0x04, 0 }, { "metropolis-2", 0x04, 1 },
        { "metropolis-3", 0x05, 0 }, { "death-egg", 0x0E, 0 },
    };

    private static List<PassageMesure> Jouer(string trace, params string[] slugs)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace + ".trace"))), LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)),
            slugs.Select(s => (s, Lire(s))).ToList());

    [Theory]
    [MemberData(nameof(Niveaux))]
    public void Chaque_niveau_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int zone, int acte)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal($"megadrive/sonic-the-hedgehog-2/{slug}-panneau.state", r.Etat!.Fichier);
        Assert.Equal(["genesis_plus_gx"], r.Coeurs);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFE11 && c.Egal == zone);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFE10 && c.Egal == acte);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFF70 && c.Egal == 1);   // Sonic seul
    }

    [Fact]
    public void L_Any_cite_seulement_des_lignes_du_MEM()
    {
        var r = Lire("any");
        Assert.Empty(r.VerifierContre(Mem));
        Assert.Equal("normal_boot", r.StartPolicy);
    }

    [Fact]
    public void Les_dix_neuf_epreuves_ont_chacune_leur_classement_dans_l_ordre_du_jeu()
    {
        // Wing Fortress retiree le 07/11 (arrivee non vue au banc) : 18 niveaux et l'Any%.
        var reglements = Niveaux.Select(n => Lire((string)n[0])).Append(Lire("any")).ToList();
        Assert.Equal(19, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 19), reglements.Select(r => r.Ordre).Order());
        Assert.Equal(18, reglements.Where(r => r.Etat is not null).Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    [Fact]
    public void L_Any_exige_Sonic_seul_choisi_dans_les_options()
    {
        // Banc du 07/11, depuis l'allumage : OPTIONS, PLAYER SELECT = SONIC ALONE (Player_option), Start lance la partie ;
        // Player_mode (index brut 0xFF70, ligne CHARACTER_SELECTED du .MEM) vaut 1 au depart (image 1154). Sans passer par
        // les options (Sonic et Tails, 0xFF70 = 0), aucun depart.
        Assert.Contains(Lire("any").Depart, c => c.Adresse == 0xFF70 && c.Egal == 1);
        var seul = Jouer("sonic2-any-seul", "any");
        var p = Assert.Single(seul);
        Assert.Equal(1154, p.Depart);
        Assert.Empty(Jouer("sonic2-any-tails", "any"));
    }

    [Fact]
    public void Emerald_Hill_1_de_la_main_au_joueur_au_panneau_egal_au_temps_du_jeu()
    {
        // Le panneau touche coupe le chrono du jeu a 0:13.50 (830 images) ; Sonic retombe (0xF7CD = 1) et l'acte change ensuite.
        // La trace continue sur l'acte 2 (un second passage, abandonne a la fin de la trace) : le premier est celui de l'acte 1.
        var p = Jouer("sonic2-emerald-hill-1", "emerald-hill-1", "emerald-hill-2")[0];
        Assert.Equal("emerald-hill-1", p.Defi);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(DEPART_EHZ, p.Depart);
        Assert.Equal(13 * 60 + 50, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Emerald_Hill_2_finit_a_la_capsule_sans_fin_d_acte()
    {
        // Le bouton de la capsule coupe le chrono a 0:34.15 (2055 images) ; la zone passe ensuite a Chemical Plant.
        var p = Jouer("sonic2-emerald-hill-2", "emerald-hill-2", "emerald-hill-1")[0];
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(DEPART_EHZ, p.Depart);
        Assert.Equal(34 * 60 + 15, p.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Une_mort_fait_echouer_le_niveau()
    {
        // Apres la mort, le niveau repart par le carton : un second passage commence (abandonne a la fin de la trace).
        var p = Jouer("sonic2-emerald-hill-1-mort", "emerald-hill-1")[0];
        Assert.Equal(189, p.Resultat.FrameDeFin);
        Assert.Equal(EtatTentative.Ratee, p.Resultat.Etat);
        Assert.Equal("LOSE_LIFE", p.Resultat.Cause);
    }

    [Fact]
    public void Une_pause_annule_la_tentative()
    {
        var p = Jouer("sonic2-emerald-hill-1-pause", "emerald-hill-1")[0];
        Assert.Equal(EtatTentative.Annulee, p.Resultat.Etat);
        Assert.Equal("PAUSE_ON", p.Resultat.Cause);
    }

    // Image de la trace ou le mode passe de 0x8C a 0x0C (banc du 06/11, etat pris 60 images avant).
    private const long DEPART_EHZ = 59;
}
