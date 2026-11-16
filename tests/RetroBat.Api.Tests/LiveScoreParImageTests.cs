using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// UN TOTAL PAR IMAGE, TOUS SES MOTS APPLIQUES (16/11). Le wrapper signale chaque mot d'un score sur son propre evenement.
/// Giga Wing (4 mots BCD, 10^12, 10^8, 10^4, 1) : le score ne baisse jamais en memoire (replay du record d'Old AF Gamer
/// rp_06GXKGW1 rejoue hors ecran, banc du 16/11, E:\claude_travail\temp\audit_replays_oldaf\banc\trace_gw_2c8417dc.txt), mais le
/// total recompose apres le premier mot d'une retenue melangeait mots neufs et ancien : image 1876, 208 259 050 au lieu de
/// 282 485 120 et plus. Dangun Feveron (2 mots) : la restauration des hiscores de FBNeo ecrit 10 000 puis 12 989 a la meme
/// image. Le total d'une image ne part qu'une fois tous ses mots appliques, avec son image ; sans image (pont MAME), rien ne
/// change.
/// </summary>
public sealed class LiveScoreParImageTests
{
    private const string DescGw = "[16-bit BCD] 1P Score, 4 BCD digits (value * {0})";
    private static readonly long[] PoidsGw = [1_000_000_000_000, 100_000_000, 10_000, 1];

    /// <summary>Les mots du score de Giga Wing, image par image (image du rejeu, mot 0 a 3 = adresse 0x70CC + 2 x mot, valeur brute).</summary>
    private static readonly string MotsGigaWing = ""
        + "0,0,0;0,2,0;0,4,117;0,6,12608;3,6,18768;6,4,121;6,6,4432;9,6,10592;18,4,131;18,6,33120;29,4,132;29,6,8304;32,6"
        + ",22912;45,6,39056;46,4,133;46,6,14336;48,6,30480;49,4,134;49,6,5664;50,4,147;50,6,38944;54,4,148;54,6,14128;59"
        + ",6,30272;60,4,258;60,6,38736;65,4,259;65,6,30064;68,4,273;68,6,22384;84,6,38528;88,4,274;88,6,18752;100,4,275;"
        + "100,6,2384;104,6,26976;116,4,276;116,6,14192;120,4,304;120,6,17968;127,4,325;127,6,30768;132,4,354;132,6,36912"
        + ";133,4,355;133,6,30096;136,4,357;136,6,18192;141,4,388;141,6,18704;146,4,389;146,6,17440;149,4,390;149,6,14640"
        + ";151,4,391;151,6,13376;158,4,392;158,6,10576;159,4,393;159,6,9312;162,4,528;162,6,9824;164,4,561;164,6,10336;1"
        + "66,4,594;166,6,12384;168,4,595;168,6,13680;178,4,628;178,6,14192;184,4,629;184,6,17024;189,4,630;189,6,18320;1"
        + "90,4,631;190,6,21248;192,4,632;192,6,22544;194,4,633;194,6,25376;197,4,640;197,6,26672;208,4,641;208,6,29504;2"
        + "15,4,642;215,6,30800;216,4,643;216,6,33632;218,4,644;218,6,34928;229,4,645;229,6,37760;232,4,646;232,6,39056;2"
        + "40,4,648;240,6,1024;264,4,649;264,6,6496;265,4,838;265,6,39264;275,4,840;275,6,5408;277,4,841;277,6,12416;278,"
        + "4,848;278,6,17984;289,4,849;289,6,25088;292,4,850;292,6,30560;305,4,851;305,6,37664;310,4,853;310,6,6528;324,4"
        + ",854;324,6,17984;328,4,855;328,6,29440;332,4,857;332,6,4368;333,4,902;333,6,29456;363,4,904;363,6,4384;384,4,9"
        + "05;384,6,18736;389,4,912;389,6,34624;414,4,914;414,6,9552;416,4,1049;416,6,34640;419,4,1057;419,6,9568;421,4,1"
        + "096;421,6,34656;463,4,1104;463,6,9584;467,4,1105;467,6,25472;473,4,1107;473,6,400;476,4,1108;476,6,16384;480,4"
        + ",1109;480,6,30736;484,4,1318;484,6,8480;496,4,1319;496,6,22832;505,4,1320;505,6,38720;509,4,1328;509,6,13648;5"
        + "14,4,1329;514,6,29536;523,4,1331;523,6,4464;524,4,1376;524,6,29552;532,4,1378;532,6,9088;536,4,1379;536,6,2958"
        + "4;544,4,1381;544,6,9216;546,4,1429;546,6,9728;547,4,1573;547,6,10240;548,4,1670;548,6,33296;551,4,1814;551,6,3"
        + "3808;553,4,1816;553,6,13344;559,4,1817;559,6,33840;564,4,1825;564,6,13376;565,4,1826;565,6,33872;569,4,1874;56"
        + "9,6,34384;572,4,1922;572,6,34896;573,4,2066;573,6,36944;583,4,2117;583,6,16976;584,4,2119;584,6,1296;589,4,216"
        + "9;589,6,22288;599,4,2177;599,6,6512;600,4,2178;600,6,33328;601,4,2325;601,6,13360;603,4,2375;603,6,34352;620,4"
        + ",2377;620,6,18576;621,4,2434;621,6,144;677,4,2435;677,6,36864;688,4,4129;688,6,29184;696,4,4131;696,6,24848;70"
        + "3,4,4196;703,6,8976;724,4,4198;724,6,21872;726,4,4373;726,6,16432;730,4,4449;730,6,37424;736,4,4452;736,6,9360"
        + ";739,4,4624;739,6,30352;745,4,4627;745,6,2384;746,4,4629;746,6,16912;749,4,4631;749,6,36896;752,4,4640;752,6,1"
        + "4384;755,4,4645;755,6,13392;758,4,4724;758,6,38480;761,4,4727;761,6,17504;765,4,4729;765,6,37488;767,4,5123;76"
        + "7,6,38768;769,4,5203;769,6,22896;778,4,5206;778,6,1920;782,4,5381;782,6,27008;787,4,5384;787,6,6032;789,4,5463"
        + ";789,6,31120;795,4,5472;795,6,10240;798,4,5641;798,6,36864;804,4,5652;804,6,34336;805,4,5908;805,6,4128;811,4,"
        + "5910;811,6,22576;816,4,5990;816,6,8240;819,4,6165;819,6,33328;824,4,6168;824,6,12352;825,4,6247;825,6,37440;82"
        + "6,4,6256;826,6,16464;832,4,6258;832,6,34912;835,4,6434;835,6,20576;840,4,6514;840,6,4704;842,4,8225;842,6,2979"
        + "2;912,4,8229;912,6,9760;914,4,8341;914,6,22560;930,4,8345;930,6,18480;932,4,8567;932,6,20528;936,4,8577;936,6,"
        + "24640;939,4,8803;939,6,25152;953,4,8808;953,6,13648;954,4,9058;954,6,38736;959,4,9063;959,6,28768;963,4,9314;9"
        + "63,6,12896;972,4,9319;972,6,20512;975,4,9585;975,6,544;979,4,9590;979,6,6528;981,4,9849;981,6,29056;987,4,9873"
        + ";987,6,0;995,4,9878;995,6,25616;999,4,9986;999,6,21024;1004,4,10563;1004,6,25648;1009,4,12385;1009,6,9776;1016"
        + ",4,12664;1016,6,34864;1017,4,12950;1017,6,20528;1018,4,13332;1018,6,4656;1024,4,13344;1024,6,64;1025,4,13623;1"
        + "025,6,25152;1028,4,13909;1028,6,9280;1033,4,13921;1033,6,4688;1034,4,13927;1034,6,96;1037,4,14212;1037,6,25184"
        + ";1041,4,14594;1041,6,9312;1045,4,14600;1045,6,4720;1048,4,16421;1048,6,29808;1052,4,16433;1052,6,25216;1058,4,"
        + "16713;1058,6,9344;1061,4,16725;1061,6,4752;1066,4,17015;1066,6,25744;1088,4,17028;1088,6,21504;1091,4,17447;10"
        + "91,6,26112;1190,4,17463;1190,6,4448;1191,4,17958;1191,6,9056;1234,4,17974;1234,6,38432;1322,4,17992;1322,6,384"
        + ";1323,4,18009;1323,6,1856;1328,4,18032;1328,6,4864;1331,4,18049;1331,6,6240;1335,4,18066;1335,6,9248;1338,4,18"
        + "179;1338,6,10624;1342,4,18196;1342,6,13632;1346,4,18213;1346,6,16640;1357,4,18230;1357,6,18016;1365,4,18247;13"
        + "65,6,21024;1369,4,18281;1369,6,25408;1370,4,18304;1370,6,26880;1372,4,18321;1372,6,29792;1375,4,18434;1375,6,3"
        + "2800;1377,4,18451;1377,6,34176;1379,4,18468;1379,6,37184;1383,4,18485;1383,6,38656;1384,4,18503;1384,6,608;138"
        + "6,4,18520;1386,6,2080;1389,4,18537;1389,6,4992;1391,4,18560;1391,6,6464;1392,4,18577;1392,6,9472;1398,4,18690;"
        + "1398,6,12384;1400,4,18707;1400,6,13856;1402,4,18741;1402,6,18240;1405,4,21095;1405,6,5440;1410,4,21912;1410,6,"
        + "33600;1419,4,22025;1419,6,35072;1423,4,22048;1423,6,37984;1424,4,22066;1424,6,32;1425,4,22083;1425,6,1408;1426"
        + ",4,22100;1426,6,4416;1430,4,22117;1430,6,5888;1433,4,22134;1433,6,8800;1437,4,22151;1437,6,10272;1442,4,22168;"
        + "1442,6,13184;1444,4,22281;1444,6,14656;1446,4,22321;1446,6,20576;1451,4,22338;1451,6,22048;1455,4,22355;1455,6"
        + ",24960;1458,4,22372;1458,6,26432;1462,4,22406;1462,6,30816;1463,4,22423;1463,6,33824;1466,4,22553;1466,6,38208"
        + ";1470,4,22594;1470,6,1632;1482,4,22628;1482,6,6016;1486,4,22662;1486,6,10496;1507,4,22792;1507,6,16416;1510,4,"
        + "25169;1510,6,4992;1514,4,25186;1514,6,21312;1518,4,26116;1518,6,16704;1697,4,33123;1697,6,37184;1698,4,33142;1"
        + "698,6,14592;1704,4,33160;1704,6,34400;1707,4,33281;1707,6,13344;1712,4,33299;1712,6,33152;1714,2,1;1714,4,1393"
        + ";1714,6,30752;1721,4,8497;1721,6,10272;1725,4,8515;1725,6,30080;1728,4,8534;1728,6,9024;1729,4,8552;1729,6,289"
        + "28;1730,4,8577;1730,6,6240;1733,4,8710;1733,6,4992;1736,4,8728;1736,6,24896;1738,4,8753;1738,6,2304;1740,4,879"
        + "0;1740,6,1056;1742,4,8808;1742,6,20864;1744,4,14400;1744,6,18752;1745,4,14418;1745,6,38656;1747,4,14455;1747,6"
        + ",37408;1751,4,14480;1751,6,14720;1752,4,21577;1752,6,35200;1754,4,21602;1754,6,14144;1757,4,21620;1757,6,34048"
        + ";1761,4,30770;1761,6,33088;1767,4,30789;1767,6,10496;1768,4,30807;1768,6,30304;1770,4,30832;1770,6,9248;1772,4"
        + ",30869;1772,6,6464;1773,4,30983;1773,6,26368;1774,4,37991;1774,6,5888;1775,2,2;1775,4,4134;1775,6,26368;1776,4"
        + ",4153;1776,6,5216;1777,4,4177;1777,6,25120;1783,4,4196;1783,6,2432;1788,4,4214;1788,6,22336;1790,4,4233;1790,6"
        + ",1280;1791,4,4353;1791,6,21088;1792,4,4372;1792,6,32;1794,4,4390;1794,6,18304;1798,4,4408;1798,6,38208;1806,4,"
        + "9880;1806,6,17728;1811,4,10000;1811,6,37632;1812,4,10019;1812,6,16480;1815,4,10037;1815,6,34848;1816,4,10056;1"
        + "816,6,13696;1818,4,10081;1818,6,8512;1823,4,10100;1823,6,1792;1825,4,10118;1825,6,37472;1829,4,17299;1849,4,22"
        + "296;1849,6,21088;1859,4,22321;1859,6,30496;1863,4,33333;1863,6,9824;1872,4,33352;1872,6,20768;1876,2,3;1876,4,"
        + "2085;1876,6,36944;1885,4,2112;1885,6,29200;1891,4,2134;1891,6,12928;1905,4,2163;1905,6,37632;1907,4,2193;1907,"
        + "6,37696;1918,4,2322;1918,6,14096;1920,4,25351;1920,6,29584;1969,4,25385;1969,6,16720;1972,2,4;1972,4,8273;1972"
        + ",6,34704;2016,4,8307;2016,6,38992;2019,4,8342;2019,6,2320;2032,4,18521;2032,6,13328"
        ;

    [Fact]
    public async Task GigaWing_rejoue_le_total_ne_baisse_jamais_quel_que_soit_l_ordre_des_mots()
    {
        foreach (var basFirst in new[] { true, false })
        {
            var (bus, provider, totaux) = await Monter();
            var memoire = new long[4];
            var attendus = new List<long>();
            foreach (var image in ParImage())
            {
                var mots = basFirst ? image.Mots.OrderByDescending(m => m.Mot) : image.Mots.OrderBy(m => m.Mot);
                foreach (var (mot, valeur) in mots)
                {
                    memoire[mot] = valeur;
                    await bus.PublishAsync(MotGigaWing(mot, valeur, image.Frame + 1000));
                }
                var total = Enumerable.Range(0, 4).Sum(i => Bcd(memoire[i]) * PoidsGw[i]);
                if (attendus.Count == 0 || attendus[^1] != total) attendus.Add(total);
            }
            provider.ViderLesImagesEnAttente();

            var publies = totaux.Select(t => t.Score).ToList();
            Assert.DoesNotContain(208_259_050L, publies);
            for (var i = 1; i < publies.Count; i++)
            {
                Assert.True(publies[i] >= publies[i - 1], $"baisse {publies[i - 1]} -> {publies[i]} (ordre bas d'abord : {basFirst})");
            }
            // Exactement les totaux de la memoire, image par image, et chacun avec son image.
            Assert.Equal(attendus.Where(t => t > 0), publies.Where(t => t > 0));
            Assert.All(totaux, t => Assert.True(t.Frame >= 1000));
        }
    }

    [Fact]
    public async Task GigaWing_sans_image_mot_par_mot_montre_la_fausse_baisse_d_avant()
    {
        // La cause, rejouee : les mots du bas d'abord, chacun publie aussitot (ce que faisait l'agregateur pour toute image).
        var (bus, _, totaux) = await Monter();
        foreach (var image in ParImage().Where(i => i.Frame <= 1876))
        {
            foreach (var (mot, valeur) in image.Mots.OrderByDescending(m => m.Mot))
            {
                await bus.PublishAsync(MotGigaWingSansImage(mot, valeur));
            }
        }
        Assert.Contains(208_259_050L, totaux.Select(t => t.Score));
    }

    [Fact]
    public async Task GigaWing_l_image_1876_part_entiere()
    {
        var (bus, provider, totaux) = await Monter();
        foreach (var image in ParImage().Where(i => i.Frame <= 1876))
        {
            foreach (var (mot, valeur) in image.Mots.OrderByDescending(m => m.Mot))
            {
                await bus.PublishAsync(MotGigaWing(mot, valeur, image.Frame + 1000));
            }
        }
        provider.ViderLesImagesEnAttente();
        var derniere = totaux[^1];
        Assert.Equal(2876, derniere.Frame);
        Assert.True(derniere.Score >= 282_485_120, $"image 1876 : {derniere.Score}");
    }

    [Fact]
    public async Task Sans_image_le_pont_MAME_publie_mot_par_mot_comme_avant()
    {
        var (bus, _, totaux) = await Monter();
        await bus.PublishAsync(MotDangun("0X3FAE", 0x0001, null));
        await bus.PublishAsync(MotDangun("0X3FB0", 0x2989, null));
        Assert.Equal([10_000L, 12_989L], totaux.Select(t => t.Score));
        Assert.All(totaux, t => Assert.Null(t.Frame));
    }

    [Fact]
    public async Task Dangun_la_restauration_des_deux_mots_fait_un_seul_total()
    {
        var (bus, provider, totaux) = await Monter();
        await bus.PublishAsync(MotDangun("0X3FAE", 0x0001, 988));
        await bus.PublishAsync(MotDangun("0X3FB0", 0x2989, 988));
        Assert.Empty(totaux);   // l'image 988 attend
        provider.ViderLesImagesEnAttente();
        var seul = Assert.Single(totaux);
        Assert.Equal(12_989L, seul.Score);
        Assert.Equal(988L, seul.Frame);
    }

    [Fact]
    public async Task La_copie_sans_image_du_meme_mot_ne_devance_pas_l_image()
    {
        // Le wrapper publie chaque mot deux fois : retroarch.memory.changed (avec son image) puis retroarch.score (sans).
        var (bus, provider, totaux) = await Monter();
        await bus.PublishAsync(MotDangun("0X3FAE", 0x0001, 988));
        await bus.PublishAsync(MotDangun("0X3FAE", 0x0001, null, "retroarch.score"));
        await bus.PublishAsync(MotDangun("0X3FB0", 0x2989, 988));
        await bus.PublishAsync(MotDangun("0X3FB0", 0x2989, null, "retroarch.score"));
        provider.ViderLesImagesEnAttente();
        Assert.Equal([12_989L], totaux.Select(t => t.Score));
    }

    [Fact]
    public async Task Un_jeu_a_un_seul_mot_garde_ses_totaux_et_ne_republie_pas_l_inchange()
    {
        var (bus, provider, totaux) = await Monter();
        var lectures = new (long Frame, long Valeur)[] { (100, 0x0010), (101, 0x0020), (102, 0x0020), (140, 0x0150), (141, 0x0150) };
        foreach (var (frame, valeur) in lectures)
        {
            await bus.PublishAsync(MotSeul(valeur, frame));
        }
        provider.ViderLesImagesEnAttente();
        Assert.Equal([10L, 20L, 150L], totaux.Select(t => t.Score));
        Assert.Equal([100L, 101L, 140L], totaux.Select(t => t.Frame ?? 0));
    }

    [Fact]
    public async Task Le_dernier_total_part_seul_apres_le_delai()
    {
        var (bus, provider, totaux) = await Monter();
        provider.DelaiDeLImage = TimeSpan.FromMilliseconds(20);
        await bus.PublishAsync(MotDangun("0X3FAE", 0x0001, 988));
        await bus.PublishAsync(MotDangun("0X3FB0", 0x2989, 988));
        for (var i = 0; i < 100 && totaux.Count == 0; i++) await Task.Delay(20);
        Assert.Equal([12_989L], totaux.Select(t => t.Score));
    }

    [Fact]
    public async Task La_fin_de_partie_vide_l_image_en_attente()
    {
        var (bus, _, totaux) = await Monter();
        await bus.PublishAsync(MotDangun("0X3FAE", 0x0001, 988));
        await bus.PublishAsync(MotDangun("0X3FB0", 0x2989, 988));
        await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
        Assert.Equal([12_989L], totaux.Select(t => t.Score));
    }

    private static async Task<(SimpleEventBus Bus, LiveScoreAggregatorProvider Provider, List<ScoreLiveEvent> Totaux)> Monter()
    {
        var bus = new SimpleEventBus();
        var totaux = new List<ScoreLiveEvent>();
        bus.Subscribe<EventEnvelope>(e =>
        {
            if (string.Equals(e.Type, "score.live.changed", StringComparison.OrdinalIgnoreCase) && e.Payload is ScoreLiveEvent s)
            {
                lock (totaux) totaux.Add(s);
            }
        });
        var provider = new LiveScoreAggregatorProvider(bus, NullLogger<LiveScoreAggregatorProvider>.Instance)
        {
            // Les tests vident a la main : le delai ne doit pas publier dans leur dos.
            DelaiDeLImage = TimeSpan.FromMinutes(5),
        };
        await provider.StartAsync();
        return (bus, provider, totaux);
    }

    private static IEnumerable<(long Frame, List<(int Mot, long Valeur)> Mots)> ParImage()
        => MotsGigaWing.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(','))
            .Select(p => (Frame: long.Parse(p[0]), Mot: Convert.ToInt32(p[1], 16) / 2, Valeur: long.Parse(p[2])))
            .GroupBy(x => x.Frame)
            .Select(g => (g.Key, g.Select(x => (x.Mot, x.Valeur)).ToList()));

    private static long Bcd(long brut)
    {
        long s = 0, m = 1;
        while (brut > 0)
        {
            s += (brut & 0xF) * m;
            brut >>= 4;
            m *= 10;
        }
        return s;
    }

    private static EventEnvelope MotGigaWing(int mot, long valeur, long frame) => new()
    {
        Type = "retroarch.memory.changed",
        Payload = new
        {
            SystemId = "arcade",
            Rom = "giga-wing",
            Source = "retroarch.wrapper.pipe",
            Signal = new
            {
                Name = "SCORE_STATE",
                Channel = "SCORE",
                Address = "0X" + (0x70CC + 2 * mot).ToString("X4"),
                Value = valeur,
                RawValueHex = "0x" + valeur.ToString("X4"),
                SourceDescription = string.Format(DescGw, PoidsGw[mot]),
                Frame = (long?) frame,
            },
        },
    };

    private static EventEnvelope MotGigaWingSansImage(int mot, long valeur) => new()
    {
        Type = "ingame.memory.changed",
        Payload = new
        {
            SystemId = "arcade",
            Rom = "giga-wing",
            Source = "mame.lua",
            Signal = new
            {
                Name = "SCORE_STATE",
                Channel = "SCORE",
                Address = "0X" + (0x70CC + 2 * mot).ToString("X4"),
                Value = valeur,
                RawValueHex = "0x" + valeur.ToString("X4"),
                SourceDescription = string.Format(DescGw, PoidsGw[mot]),
            },
        },
    };

    private static EventEnvelope MotDangun(string adresse, long valeur, long? frame, string type = "retroarch.memory.changed")
    {
        var desc = adresse == "0X3FAE"
            ? "Player 1 score of stage 1, BCD digits 5 to 8 (value * 10000)"
            : "Player 1 score of stage 1, BCD digits 1 to 4 (value * 1)";
        if (type == "retroarch.score")
        {
            // La copie projetee par le wrapper : ni signal, ni image.
            return new EventEnvelope
            {
                Type = type,
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe",
                    SystemId = "arcade",
                    Rom = "dangun-feveron",
                    actionType = "SCORE_STATE",
                    sourceCategory = desc,
                    Value = valeur,
                    Address = adresse,
                    RawValueHex = "0x" + valeur.ToString("X4"),
                },
            };
        }
        return new EventEnvelope
        {
            Type = type,
            Payload = new
            {
                SystemId = "arcade",
                Rom = "dangun-feveron",
                Source = "retroarch.wrapper.pipe",
                Signal = new
                {
                    Name = "SCORE_STATE",
                    Channel = "SCORE",
                    Address = adresse,
                    Value = valeur,
                    RawValueHex = "0x" + valeur.ToString("X4"),
                    SourceDescription = desc,
                    Frame = frame,
                },
            },
        };
    }

    private static EventEnvelope MotSeul(long valeur, long frame) => new()
    {
        Type = "retroarch.memory.changed",
        Payload = new
        {
            SystemId = "arcade",
            Rom = "un-mot",
            Source = "retroarch.wrapper.pipe",
            Signal = new
            {
                Name = "SCORE_STATE",
                Channel = "SCORE",
                Address = "0X1000",
                Value = valeur,
                RawValueHex = "0x" + valeur.ToString("X4"),
                SourceDescription = "Player 1 score (BCD)",
                Frame = (long?) frame,
            },
        },
    };
}
