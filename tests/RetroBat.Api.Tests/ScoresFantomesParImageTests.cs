using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SCORES FANTOMES DE DANGUN FEVERON (15/11). FBNeo restaure au demarrage la plage de hiscore.dat du jeu (dfeveron :
/// 0x103EB4, 0x16D octets), sauvee a la sortie de la partie precedente ; elle contient les mots BCD du score. neogeoff tenait
/// START sur l'ecran d'avertissement et quittait pendant la selection : la borne lisait 10 000 puis 12 989 A LA MEME IMAGE et
/// certifiait le score de la partie d'avant (c7227a06 et 02a8f731 : 12 989 ; 35feeb72 et 3b6a9800 : 12 381). Les hausses se
/// comptent desormais par image (<see cref="ProgressionDuScore.ParImage"/>). Les lectures viennent des passeports publies et
/// des replays publics rejoues hors ecran (393e2873, le fantome ; a29427fe, la vraie partie Uotaro de neogeoff, 437 lectures,
/// score final 12 989).
/// </summary>
public class ScoresFantomesParImageTests
{
    /// <summary>Le fantome c7227a06 tel que son passeport le signe : deux lectures, toutes deux a l'image 988.</summary>
    private static readonly (long frame, long total)[] FantomeC7227a06 = [(988, 10000), (988, 12989)];

    /// <summary>Le fantome 35feeb72 : trois lectures a l'image 854 (23 s de session).</summary>
    private static readonly (long frame, long total)[] Fantome35feeb72 = [(854, 10000), (854, 12000), (854, 12381)];

    /// <summary>La vraie partie Uotaro de neogeoff (replay a29427fe rejoue, mots du score des 5 stages additionnes par lecture).</summary>
    private static readonly string LecturesUotaro = ""
        + "301,7794;354,0;534,1;536,2;560,3;564,4;626,5;654,6;670,7;672,8;678,9;684,11;712,36;744,43;748,51;754,70;816,80"
        + ";830,113;844,125;874,137;878,149;896,224;902,241;906,258;918,358;922,380;924,402;958,424;960,493;966,542;968,5"
        + "68;970,594;972,620;982,647;986,674;1002,676;1008,678;1010,680;1020,682;1038,684;1040,687;1048,694;1050,699;105"
        + "6,705;1070,712;1072,719;1080,726;1086,734;1124,742;1126,751;1130,761;1134,772;1136,783;1150,794;1158,805;1160,"
        + "817;1182,829;1184,841;1196,854;1198,868;1204,899;1208,916;1226,933;1240,950;1246,968;1248,987;1266,1006;1272,1"
        + "025;1276,1044;1280,1063;1310,1082;1314,1102;1316,1122;1334,1142;1340,1162;1344,1183;1364,1205;1372,1227;1384,1"
        + "249;1396,1272;1408,1321;1416,1347;1418,1374;1436,1402;1440,1459;1472,1488;1502,1517;1508,1677;1512,1711;1520,1"
        + "745;1546,1779;1560,1847;1572,2032;1574,2072;1606,2112;1616,2152;1620,2192;1626,2232;1630,2272;1640,2353;1658,2"
        + "394;1660,2436;1664,2478;1676,2520;1686,2563;1696,2606;1706,2649;1708,2692;1710,2735;1714,2778;1718,2821;1740,2"
        + "865;1742,2910;1744,3001;1746,3048;1772,3096;1774,3243;1776,3344;1780,3447;1794,3500;1818,3554;1820,3609;1824,3"
        + "664;1828,3719;1852,3774;1856,3829;1864,3884;1868,3939;1870,3994;1876,4104;1882,4159;1894,4269;1900,4324;1904,4"
        + "379;1996,4669;1998,4984;2002,5050;2004,5117;2008,5184;2010,5251;2012,5385;2018,5452;2022,5519;2046,5869;2050,6"
        + "244;2058,6321;2064,6398;2066,6553;2070,6631;2090,6709;2100,6787;2110,6865;2118,6943;2120,7021;2138,7099;2142,7"
        + "177;2146,7255;2148,7333;2166,7411;2168,7567;2188,8141;2200,8227;2202,8313;2208,8400;2220,8487;2222,8574;2226,8"
        + "661;2242,8748;2244,9198;2250,9290;2264,9383;2274,9476;2286,9478;2292,9481;2300,9484;2302,9488;2308,9493;2318,9"
        + "498;2328,9503;2330,9508;2336,9513;2340,9518;2346,9524;2358,9531;2360,9538;2366,9552;2378,9559;2380,9566;2394,9"
        + "573;2400,9580;2406,9588;2420,9596;2432,9605;2456,9623;2498,9662;2502,9673;2510,9684;2512,9695;2520,9696;2538,9"
        + "697;2558,9698;2564,9699;2568,9700;2572,9701;2574,9702;2578,9703;2594,9704;2608,9705;2610,9706;2624,9707;2632,9"
        + "708;2634,9709;2638,9711;2642,9712;2648,9713;2654,9714;2684,9716;2706,9719;2710,9722;2712,9728;2714,9732;2720,9"
        + "740;2724,9744;2734,9756;2738,9760;2740,9764;2742,9768;2744,9773;2746,9778;2748,9783;2766,9788;2768,9789;2788,9"
        + "791;2836,9803;2838,9809;2852,9810;2864,9815;2946,9824;2948,9830;2950,9837;2978,9845;2990,9853;3064,9861;3222,9"
        + "862;3266,9863;3270,9864;3274,9865;3278,9866;3288,9867;3296,9869;3298,9881;3302,9892;3310,9898;3316,9904;3326,9"
        + "910;3329,9922;3333,9928;3425,9937;3427,9963;3429,9972;3431,9990;3433,9998;3435,19998;3435,10007;3437,10017;344"
        + "3,10027;3445,10047;3451,10057;3455,10067;3459,10077;3467,10087;3471,10097;3473,10107;3483,10127;3485,10137;348"
        + "7,10147;3489,10157;3494,10187;3496,10197;3526,10198;3582,10207;3584,10225;3586,10275;3588,10333;3592,10350;359"
        + "4,10368;3598,10386;3602,10404;3606,10422;3610,10458;3644,10476;3676,10495;3690,10534;3704,10555;3708,10577;371"
        + "2,10600;3714,10649;3716,10675;3722,10701;3734,10727;3742,10754;3758,10781;3794,10784;3804,10786;3818,10788;385"
        + "0,10797;3880,10802;3888,10808;3892,10814;3908,10821;3942,10828;3946,10836;3954,10844;3978,10852;3986,10860;401"
        + "2,10879;4024,10889;4100,10900;4104,10925;4108,11005;4114,11024;4116,11043;4120,11062;4132,11172;4134,11307;415"
        + "2,11336;4156,11365;4182,11395;4184,11426;4208,11596;4214,11632;4236,11668;4246,11705;4258,11743;4290,11781;431"
        + "6,11819;4348,11857;4356,11895;4360,11933;4378,12010;4400,12049;4402,12088;4416,12127;4428,12166;4446,12205;445"
        + "8,12245;4482,12286;4484,12327;4500,12369;4502,12411;4524,12453;4530,12496;4532,12539;4546,12583;4560,12627;460"
        + "4,12671;4648,12672;4662,12674;4668,12677;4678,12680;4684,12683;4688,12686;4694,12690;4700,12694;4708,12698;471"
        + "8,12702;4724,12706;4726,12710;4750,12714;4780,12726;4782,12731;4798,12736;4802,12741;4806,12747;4824,12765;482"
        + "6,12771;4834,12778;4836,12785;4848,12787;4854,12789;4862,12791;4864,12795;4866,12797;4870,12799;4872,12801;488"
        + "0,12803;4886,12805;4888,12807;4894,12808;4908,12809;4916,12812;4918,12814;4920,12817;4926,12821;4954,12822;495"
        + "8,12824;4960,12826;4962,12828;4968,12833;4988,12837;4996,12841;5028,12852;5032,12853;5034,12854;5038,12855;504"
        + "4,12858;5046,12859;5050,12860;5054,12861;5058,12862;5060,12864;5064,12867;5120,12870;5122,12873;5126,12876;514"
        + "4,12885;5172,12888;5188,12889;5198,12890;5216,12891;5236,12892;5250,12893;5252,12896;5266,12899;5268,12903;527"
        + "4,12907;5280,12911;5298,12912;5300,12913;5302,12922;5334,12927;5336,12933;5358,12940;5372,12948;5376,12967;543"
        + "0,12978;5676,12989"
        ;

    private static List<(long frame, long total)> Lire(string brut)
        => brut.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(','))
            .Select(p => (long.Parse(p[0]), long.Parse(p[1])))
            .ToList();

    [Fact]
    public void Le_fantome_restaure_d_un_coup_n_est_pas_une_montee()
    {
        Assert.False(NelfePlayScoringReporter.ScoreAMonte(FantomeC7227a06));
        Assert.False(NelfePlayScoringReporter.ScoreAMonte(Fantome35feeb72));
    }

    [Fact]
    public void Le_fantome_rejoue_hors_ecran_n_est_pas_une_montee()
    {
        // Replay 393e2873 rejoue : la restauration tombe a l'image 301 du rejeu (point de controle), les deux mots ensemble.
        Assert.False(NelfePlayScoringReporter.ScoreAMonte([(301, 10000), (301, 12989)]));
    }

    [Fact]
    public void Le_fantome_fait_une_seule_hausse_et_la_partie_courte_le_refuse()
    {
        Assert.Equal(1, ProgressionDuScore.Hausses(FantomeC7227a06));
        Assert.Equal(1, ProgressionDuScore.Hausses(Fantome35feeb72));
        // 35feeb72 : 1 333 images de session. Compte lecture par lecture, il passait (3 hausses) ; par image, il est trop court.
        Assert.NotNull(DepartReel.PartieTropCourte(1333, ProgressionDuScore.Hausses(Fantome35feeb72)));
    }

    [Fact]
    public void La_vraie_partie_de_neogeoff_monte_toujours()
    {
        var run = Lire(LecturesUotaro);
        Assert.Equal(437, run.Count);
        Assert.Equal(12989, run[^1].total);
        Assert.True(NelfePlayScoringReporter.ScoreAMonte(run));
        Assert.Equal(435, ProgressionDuScore.Hausses(run));
        // Une seule image porte deux lectures : le passage de 9 998 a 10 007, ou le mot haut s'ecrit avant le bas (19 998 un
        // instant). Par image, il reste 10 007, et rien ne disparait de la montee.
        var parImage = ProgressionDuScore.ParImage(run);
        Assert.Equal(436, parImage.Count);
        Assert.Contains((3435L, 10007L), parImage);
        Assert.DoesNotContain((3435L, 19998L), parImage);
    }

    [Fact]
    public void Une_montee_d_une_image_a_la_suivante_compte()
    {
        Assert.True(NelfePlayScoringReporter.ScoreAMonte([(988, 10000), (989, 12989)]));
        Assert.Equal(2, ProgressionDuScore.Hausses([(988, 10000), (989, 12989)]));
    }

    [Fact]
    public void Une_image_inconnue_ne_regroupe_rien()
    {
        // Pont MAME, lectures non datees : chaque lecture compte, comme avant.
        Assert.True(NelfePlayScoringReporter.ScoreAMonte([(0, 63), (0, 100)]));
        Assert.Equal(2, ProgressionDuScore.Hausses([(0, 63), (0, 100)]));
    }

    [Fact]
    public void Seules_les_lectures_qui_se_suivent_a_la_meme_image_se_regroupent()
    {
        var p = ProgressionDuScore.ParImage([(10, 1), (10, 2), (11, 3), (10, 4)]);
        Assert.Equal([(10L, 2L), (11L, 3L), (10L, 4L)], p);
    }

    [Fact]
    public void Un_score_precedent_sous_10000_etait_deja_arrete()
    {
        // Un seul mot change : une lecture, jamais une montee (avant comme apres).
        Assert.False(NelfePlayScoringReporter.ScoreAMonte([(988, 7794)]));
    }
}
