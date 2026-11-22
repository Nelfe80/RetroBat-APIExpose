using System.Text.Json;
using RetroBat.Api.Deck.Canal;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES PROFILS D'AFFICHAGE DECLARATIFS (1.9.212) : le profil de DaiOuJou, code en dur en 1.9.211, vient maintenant de
/// <c>ram/arcade/dodonpachi-daioujou.VIEW.json</c>. Preuve de non-regression : les messages deck.live sont IDENTIQUES a l'octet.
/// </summary>
public class CanalProfilsTests
{
    private static readonly string Fichier = Path.Combine(AppContext.BaseDirectory, "Profils", "ram", "arcade", "dodonpachi-daioujou.VIEW.json");

    /// <summary>LE PROFIL CODE EN DUR de la 1.9.211 (CanalAffichage.cs, DaiOuJou), recopie tel quel : la reference de la non-regression.</summary>
    private static CanalProfilAffichage ProfilCode()
    {
        var d = new Dictionary<int, int>();
        foreach (var t in new[] { 0x1101, 0x1001, 0x9500, 0x2500, 0x2B00, 0x2900, 0x0501 }) d[t] = 0;
        foreach (var t in new[] { 0x8200, 0x8B01, 0x8A01, 0x8000, 0x8500, 0x8801, 0x8D01, 0x8F01, 0x9401, 0x9701, 0x9201, 0x9601, 0x9301, 0x8401, 0x9001, 0x9101, 0x8600 }) d[t] = 1;
        foreach (var t in new[] { 0x8901, 0x0D81, 0x8C80 }) d[t] = 2;
        foreach (var t in new[] { 0x0500, 0x0700, 0x0800, 0x0801, 0x0900, 0x0B00, 0x0B01, 0x2700, 0x4D00 }) d[t] = 3;
        return new CanalProfilAffichage(
            "daioujou", 224, 448,
            VaisseauX: new(1.0 / 64.0, 0), VaisseauY: new(-1.0 / 64.0, 456),
            ColonneX: "ax", ColonneY: "ay",
            EnnemiX: new(1.0 / 64.0, 0), EnnemiY: new(-1.0 / 64.0, 447),
            MasqueDessine: 0x2000, VieRequise: true, CoordonneesSignees: true, ComboMax: 56,
            CameraEchelle: 1.0 / 64.0, Classes: d, Roms: ["ddpdojblka", "ddpdojblk", "ddpdoj", "ddpdojh"]);
    }

    [Fact]
    public void Le_fichier_donne_le_profil_code_de_la_1_9_211()
    {
        var f = CanalProfilAffichage.Pour("ddpdojblka")!;
        var c = ProfilCode();
        Assert.Equal(c.Id, f.Id);
        Assert.Equal((c.EcranL, c.EcranH), (f.EcranL, f.EcranH));
        Assert.Equal((c.VaisseauX, c.VaisseauY, c.EnnemiX, c.EnnemiY), (f.VaisseauX, f.VaisseauY, f.EnnemiX, f.EnnemiY));
        Assert.Equal((c.ColonneX, c.ColonneY, c.MasqueDessine, c.VieRequise, c.CoordonneesSignees, c.ComboMax, c.CameraEchelle),
            (f.ColonneX, f.ColonneY, f.MasqueDessine, f.VieRequise, f.CoordonneesSignees, f.ComboMax, f.CameraEchelle));
        Assert.Equal(c.Classes.OrderBy(k => k.Key), f.Classes.OrderBy(k => k.Key));
        Assert.Equal("dodonpachi-daioujou", f.Vue);
    }

    [Fact]
    public void Les_messages_deck_live_sont_identiques_a_l_octet_sur_des_blocs_varies()
    {
        var e = CanalScenarioSimule.DaiOuJou(5);
        var d = CanalFormat.LireDescripteur(e.Tampon)!;
        var types = new[] { 0x1101, 0x8200, 0x8901, 0x0D81, 0x0500, 0x4D00, 0x2B00, 0x1234, 0, 0xFFFF };
        var rnd = new Random(1122);
        var code = new CanalAffichageCalcul(ProfilCode(), d, "ddpdojblka");
        var fichier = new CanalAffichageCalcul(CanalProfilAffichage.Pour("ddpdojblka")!, d, "ddpdojblka");
        var avecEnnemis = 0; var avecBoss = 0; var avecVaisseau = 0; var sansVaisseau = 0;
        for (var n = 0; n < 4000; n++)
        {
            var champs = new int[CanalScenarioSimule.Champs.Length];
            for (var i = 0; i < champs.Length; i++) champs[i] = rnd.Next(0, 65536);
            champs[CanalScenarioSimule.Vies] = new[] { 0, 1, 2, 255 }[rnd.Next(4)];
            champs[CanalScenarioSimule.Camera] = rnd.Next(-3000, 3000);
            champs[8] = rnd.Next(3) == 0 ? rnd.Next(1, 30) : 0;   // mort_anim
            if (rnd.Next(10) == 0) champs[CanalScenarioSimule.VaisseauX] = champs[CanalScenarioSimule.VaisseauY] = 0;
            var objets = new List<int[]>();
            var aff = new List<int[]>();
            for (var slot = 0; slot < 48; slot++)
            {
                if (rnd.Next(3) != 0) continue;
                if (rnd.Next(5) != 0) objets.Add([slot, types[rnd.Next(types.Length)], rnd.Next(4) == 0 ? 0 : rnd.Next(0, 3000)]);
                var bx = rnd.Next(4) == 0 ? rnd.Next(60000, 65536) : rnd.Next(0, 16000);
                aff.Add([slot, rnd.Next(2) == 0 ? 0xA200 : rnd.Next(0, 65536), rnd.Next(0, 65536), bx]);
            }

            e.Ecrire((uint)(n + 1), champs, [objets.ToArray(), aff.ToArray(), []]);
            var bloc = new CanalBloc(d);
            Assert.True(CanalFormat.Lire(e.Tampon, d, bloc));
            var m = code.Convertir(bloc);
            Assert.Equal(JsonSerializer.Serialize(m), JsonSerializer.Serialize(fichier.Convertir(bloc)));
            if (m.Enemies.Length > 0) avecEnnemis++;
            if (m.Boss is not null) avecBoss++;
            if (m.Ship is not null) avecVaisseau++; else sansVaisseau++;
        }

        // le test est utile : les blocs tirent bien des ennemis, des boss, des vaisseaux presents et absents
        Assert.True(avecEnnemis > 500 && avecBoss > 20 && avecVaisseau > 500 && sansVaisseau > 500, $"{avecEnnemis} {avecBoss} {avecVaisseau} {sansVaisseau}");
    }

    [Fact]
    public void Un_profil_ne_vaut_que_pour_les_roms_prouvees()
    {
        // 1.9.212 : seule ddpdojblka est prouvee ; ddpdojblk (autre revision, adresses differentes), ddpdoj et ddpdojh n'ont pas de canal
        Assert.NotNull(CanalProfilAffichage.Pour("ddpdojblka"));
        foreach (var rom in new[] { "ddpdojblk", "ddpdoj", "ddpdojh" }) Assert.Null(CanalProfilAffichage.Pour(rom));
        Assert.False(CanalDuDeck.Demander("ddpdojblk"));
        CanalDuDeck.Retirer();
    }

    [Fact]
    public void Un_profil_invalide_est_refuse_avec_son_motif()
    {
        var bon = File.ReadAllText(Fichier).Replace("\r\n", "\n");
        Assert.NotNull(CanalProfils.DepuisJson(bon, out _));
        void Refuse(string json, string motif)
        {
            Assert.Null(CanalProfils.DepuisJson(json, out var erreur));
            Assert.Contains(motif, erreur);
        }

        Refuse(bon.Replace("view-profile/1", "view-profile/2"), "schema");
        Refuse(bon.Replace("\"id\"", "\"idee\""), "cle inconnue");
        Refuse(bon.Replace("\"roms\": [\"ddpdojblka\"]", "\"roms\": []"), "roms");
        Refuse(bon.Replace("\"ddpdojblka\"", "\"ddp doj\""), "ROM invalide");
        Refuse(bon.Replace("\"w\": 224", "\"w\": 0"), "screen");
        Refuse(bon.Replace("\"x\": { \"scale\": \"1/64\", \"offset\": 0 },\n    \"y\"", "\"x\": { \"scale\": 0, \"offset\": 0 },\n    \"y\""), "scale");
        Refuse(bon.Replace("\"0x0500\"", "\"0x1101\""), "deux classes");
        Refuse(bon.Replace("\"light\"", "\"leger\""), "classe inconnue");
        Refuse(bon.Replace("\"drawn_mask\": \"0x2000\"", "\"drawn_mask\": \"0x20000\""), "drawn_mask");
        Refuse("{ pas du json", "JSON");
        Refuse("[]", "objet");
    }

    [Fact]
    public void Le_profil_se_lit_dans_un_dossier_ram_avec_la_couche_user_prioritaire_et_ignore_les_fichiers_faux()
    {
        var racine = Path.Combine(Path.GetTempPath(), "profils_" + Guid.NewGuid().ToString("N"));
        var avant = CanalProfils.DossierRam;
        try
        {
            Directory.CreateDirectory(Path.Combine(racine, "arcade"));
            Directory.CreateDirectory(Path.Combine(racine, ".user", "arcade"));
            Directory.CreateDirectory(Path.Combine(racine, ".defis", "arcade"));
            var bon = File.ReadAllText(Fichier);
            File.WriteAllText(Path.Combine(racine, "arcade", "dodonpachi-daioujou.VIEW.json"), bon);
            File.WriteAllText(Path.Combine(racine, "arcade", "casse.VIEW.json"), "{ nope");
            // l'essai du labo (.user) a la priorite ; une couche .defis n'en porte pas
            File.WriteAllText(Path.Combine(racine, ".user", "arcade", "dodonpachi-daioujou.VIEW.json"), bon.Replace("\"combo_max\": 56", "\"combo_max\": 99"));
            File.WriteAllText(Path.Combine(racine, ".defis", "arcade", "x.VIEW.json"), bon.Replace("ddpdojblka", "autrerom"));
            CanalProfils.DossierRam = racine;
            Assert.Equal(99, CanalProfilAffichage.Pour("ddpdojblka")!.ComboMax);
            Assert.Null(CanalProfilAffichage.Pour("autrerom"));
            Assert.Contains(CanalProfils.Erreurs, x => x.Contains("casse.VIEW.json"));
        }
        finally
        {
            CanalProfils.DossierRam = avant;
            try { Directory.Delete(racine, true); } catch { }
        }
    }

    [Fact]
    public void Les_noms_de_fichier_du_profil_passent_le_data_pack_et_le_wrapper_ne_les_voit_pas()
    {
        var dossiers = new HashSet<string>(["ram"], StringComparer.OrdinalIgnoreCase);
        Assert.True(DataPackPaths.Autorise("ram/arcade/dodonpachi-daioujou.VIEW.json", dossiers));
        Assert.False(DataPackPaths.Autorise("../ram/arcade/x.VIEW.json", dossiers));
        // le wrapper ouvre « <jeu>.VIEW » (nom exact, docs\VIEW.md section 3) : « .VIEW.json » est un autre nom, jamais lu par lui
        Assert.Equal(".json", Path.GetExtension("dodonpachi-daioujou.VIEW.json"));
    }
}
