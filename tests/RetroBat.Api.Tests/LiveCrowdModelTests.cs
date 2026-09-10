using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La foule du HUD doit rendre les MEMES nombres que son jumeau web (`nelfe-crowd.js`).
///
/// Sans ce test, les deux derivent en silence : la meme foule se placerait autrement selon
/// l'ecran, et deux personnes qui commentent la meme partie ne parleraient pas de la meme
/// silhouette. Les valeurs attendues ci-dessous ont ete MESUREES sur le module web avant
/// d'etre recopiees ici, elles ne sont pas devinees.
/// </summary>
public sealed class LiveCrowdModelTests
{
    /// <summary>Des acteurs realistes : des pseudonymes hexadecimaux de 32 caracteres.</summary>
    private static string[] Acteurs(int combien)
    {
        var sortie = new string[combien];
        for (var i = 0; i < combien; i++)
        {
            // Deterministe et bien reparti, sans dependre d'un generateur.
            sortie[i] = (i * 2654435761u).ToString("x8") + new string('0', 24);
        }
        return sortie;
    }

    [Fact]
    public void Le_placement_ne_collisionne_jamais()
    {
        var places = LiveCrowdModel.Places(Acteurs(250));

        Assert.Equal(250, places.Count);
        // Deux personnes sur la meme place, c'est ce qu'un hachage aurait produit une fois sur
        // deux. La liste triee l'interdit par construction.
        var occupees = places.Values.Select(p => p.Rangee * LiveCrowdModel.ParRangee + p.Index).ToArray();
        Assert.Equal(occupees.Length, occupees.Distinct().Count());
    }

    [Fact]
    public void Le_remplissage_part_de_la_rangee_de_devant()
    {
        var places = LiveCrowdModel.Places(Acteurs(250));
        var parRangee = places.Values.GroupBy(p => p.Rangee).ToDictionary(x => x.Key, x => x.Count());

        // Mesure cote web : 100 / 100 / 50.
        Assert.Equal(100, parRangee[0]);
        Assert.Equal(100, parRangee[1]);
        Assert.Equal(50, parRangee[2]);
    }

    [Fact]
    public void La_foule_sature_a_sa_capacite()
    {
        Assert.Equal(300, LiveCrowdModel.Capacite);
        Assert.Equal(300, LiveCrowdModel.Places(Acteurs(500)).Count);
    }

    [Fact]
    public void La_duree_des_etiquettes_decroit_puis_tombe_a_zero()
    {
        // Mesure cote web : 1800, 1625, 1450, 1275, 1100, 925, 750, 575, puis 0.
        var attendues = new[] { 1800, 1625, 1450, 1275, 1100, 925, 750, 575, 0 };
        var obtenues = Enumerable.Range(0, attendues.Length)
            .Select(LiveCrowdModel.DureeEtiquette)
            .ToArray();

        Assert.Equal(attendues, obtenues);

        // Et elle DECROIT strictement tant qu'elle n'est pas nulle : une duree qui remonterait
        // laisserait un nom monopoliser sa place alors que ca reagit de plus en plus.
        for (var i = 1; i < attendues.Length - 1; i++)
        {
            Assert.True(obtenues[i] < obtenues[i - 1]);
        }
    }

    [Fact]
    public void Le_saut_suit_l_intensite_et_non_le_budget()
    {
        // Trois crans, ceux de la jauge de charge. Mesure cote web : 10 / 14 / 18.
        Assert.Equal(10, LiveCrowdModel.HauteurSaut(1));
        Assert.Equal(14, LiveCrowdModel.HauteurSaut(2));
        Assert.Equal(18, LiveCrowdModel.HauteurSaut(3));

        // Borne des deux cotes : une intensite absurde ne doit pas produire un saut absurde.
        Assert.Equal(10, LiveCrowdModel.HauteurSaut(0));
        Assert.Equal(10, LiveCrowdModel.HauteurSaut(-3));
        Assert.Equal(18, LiveCrowdModel.HauteurSaut(99));
    }

    [Fact]
    public void La_teinte_est_la_meme_arithmetique_que_le_jumeau_web()
    {
        // Valeurs obtenues en executant la fonction du module web sur ces memes chaines.
        Assert.Equal(0, LiveCrowdModel.Teinte(""));
        Assert.Equal(97, LiveCrowdModel.Teinte("a"));
        Assert.Equal(225, LiveCrowdModel.Teinte("ab"));
        Assert.Equal(132, LiveCrowdModel.Teinte("nelfe"));
        Assert.Equal(24, LiveCrowdModel.Teinte(new string('f', 32)));
    }

    [Fact]
    public void Une_reaction_sans_place_ne_dessine_rien()
    {
        var foule = new LiveCrowdModel();
        foule.Poser(Acteurs(3), 3);

        // Un acteur absent du releve de presence : on ne dessine pas n'importe ou.
        foule.Reagir("inconnu", "hype", 2, "Quelqu'un", 1000);
        var vide = foule.Relever(1000);
        Assert.Empty(vide.Vols);
        Assert.Empty(vide.Etiquettes);
        Assert.Empty(vide.Sauts);

        // Un acteur present : saut, vol et etiquette.
        var present = Acteurs(3)[0];
        foule.Reagir(present, "hype", 2, "Nelfe80", 1000);
        var plein = foule.Relever(1000);
        Assert.Single(plein.Vols);
        Assert.Single(plein.Etiquettes);
        Assert.Single(plein.Sauts);
        Assert.Equal(14, plein.Sauts[present].Hauteur);
    }

    [Fact]
    public void Ce_qui_est_fini_disparait()
    {
        var foule = new LiveCrowdModel();
        var acteurs = Acteurs(2);
        foule.Poser(acteurs, 2);
        foule.Reagir(acteurs[0], "wow", 1, "Vero", 1000);

        Assert.True(foule.Anime);

        // TROIS DUREES DIFFERENTES, et c'est voulu. Le saut est bref (420 ms), l'emoji vole
        // plus longtemps (1400 ms), et le nom reste le plus longtemps (1800 ms au premier
        // cran) : on doit pouvoir lire qui a reagi apres que son emoji a disparu.
        //
        // Mon premier essai attendait que tout disparaisse ensemble a la fin du vol. Le test a
        // eu raison contre moi : l'etiquette etait encore la, et elle DOIT l'etre.
        var pendant = foule.Relever(1000 + LiveCrowdModel.DureeVol + 1);
        Assert.Empty(pendant.Vols);
        Assert.Empty(pendant.Sauts);
        Assert.Single(pendant.Etiquettes);
        Assert.True(foule.Anime);

        // Passe la duree de l'etiquette, plus rien ne doit rester : une foule qui garde ses
        // sprites maintiendrait la fenetre a vingt-cinq images par seconde pour rien.
        var apres = foule.Relever(1000 + LiveCrowdModel.DureeEtiquette(0) + 1);
        Assert.Empty(apres.Vols);
        Assert.Empty(apres.Sauts);
        Assert.Empty(apres.Etiquettes);
        Assert.False(foule.Anime);
    }

    [Fact]
    public void Le_plafond_retire_la_plus_ancienne_etiquette()
    {
        var foule = new LiveCrowdModel();
        var acteurs = Acteurs(20);
        foule.Poser(acteurs, 20);

        // Neuf reactions d'affilee : la huitieme rend une duree nulle (donc pas d'etiquette),
        // et le plafond ne doit jamais etre depasse.
        for (var i = 0; i < 9; i++)
        {
            foule.Reagir(acteurs[i], "laugh", 1, "nom" + i, 1000);
        }

        var etat = foule.Relever(1000);
        Assert.True(etat.Etiquettes.Count <= 8);
        Assert.Equal(9, etat.Vols.Count);   // les vols, eux, ne sont pas plafonnes si bas
    }
}
