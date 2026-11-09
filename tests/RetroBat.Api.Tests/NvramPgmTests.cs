using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les reglages du menu de test des jeux PGM (Ketsui, DaiOuJou), gardes dans une NVRAM de 128 Ko (2026-11-09).
/// FBNeo sauve la RAM 68000 entiere (ket.fs) ; les reglages y vivent en 0x1000-0x1009. Lue jusqu'a 64 Ko seulement,
/// l'epingle partait sans octet. Ces tests disent aussi ce qui NE change PAS : une NVRAM non epinglee de plus de 64 Ko
/// reste hors du passeport, une NVRAM de 64 Ko au plus part comme avant, un profil sans champ « nvram » signe la meme
/// difficulte qu'avant.
/// </summary>
public class NvramPgmTests
{
    private static readonly byte[] UsineKetsui = [0x01, 0x00, 0x02, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01];

    private static byte[] RamPgm(params (int Offset, byte Valeur)[] changes)
    {
        var ram = new byte[131072];
        UsineKetsui.CopyTo(ram, 0x1000);
        ram[0x100F] = 0x4F;   // un compteur, hors des reglages
        foreach (var (o, v) in changes) ram[o] = v;
        return ram;
    }

    private static string Dossier()
    {
        var d = Path.Combine(Path.GetTempPath(), "nelfe-nvram-pgm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Poser(string saves, string relatif, byte[] octets)
    {
        var chemin = Path.Combine(saves, relatif.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllBytes(chemin, octets);
        return chemin;
    }

    [Fact]
    public void Une_NVRAM_epinglee_de_128_Ko_part_entiere_avec_son_vrai_depart()
    {
        var saves = Dossier();
        try
        {
            var depart = RamPgm((0x1005, 0x00));   // DIFFICULTY changee dans une session precedente
            var chemin = Poser(saves, "mame/fbneo/ket.fs", depart);
            var lancement = new List<(string, string, byte[])> { ("saves/mame/fbneo/ket.fs", chemin, depart) };

            var entrees = NvramSnapshotService.EntreesDuPasseport(lancement, ["fbneo/ket.fs"], "mame", saves);

            var e = Assert.Single(entrees)!.AsObject();
            Assert.Equal("saves/mame/fbneo/ket.fs", (string?)e["file"]);
            Assert.Equal(131072, (int)e["size"]!);
            Assert.Equal(depart, Convert.FromBase64String((string)e["start"]!));
            Assert.Equal(depart, Convert.FromBase64String((string)e["end"]!));
        }
        finally { Directory.Delete(saves, true); }
    }

    [Fact]
    public void Une_NVRAM_non_epinglee_de_plus_de_64_Ko_reste_hors_du_passeport_comme_avant()
    {
        var saves = Dossier();
        try
        {
            var gros = new byte[131072];
            var chemin = Poser(saves, "mame/fbneo/ket.fs", gros);
            var lancement = new List<(string, string, byte[])> { ("saves/mame/fbneo/ket.fs", chemin, gros) };

            Assert.Empty(NvramSnapshotService.EntreesDuPasseport(lancement, [], "mame", saves));
            // L'epingle d'un autre fichier part seule (depart vide, fin absente) ; ket.fs, non epingle, reste dehors.
            var e = Assert.Single(NvramSnapshotService.EntreesDuPasseport(lancement, ["fbneo/autre.fs"], "mame", saves))!;
            Assert.Equal("saves/mame/fbneo/autre.fs", (string?)e["file"]);
        }
        finally { Directory.Delete(saves, true); }
    }

    [Fact]
    public void Une_NVRAM_de_64_Ko_au_plus_part_comme_avant_epinglee_ou_non()
    {
        var saves = Dossier();
        try
        {
            // Blazing Star (blazstar.fs, 64 Ko tout juste) et 19XX (EEPROM de 128 octets) : rien ne change.
            var neo = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
            var eeprom = Enumerable.Range(0, 128).Select(i => (byte)(255 - i)).ToArray();
            var c1 = Poser(saves, "fbneo/fbneo/blazstar.fs", neo);
            var c2 = Poser(saves, "fbneo/fbneo/19xx.nv", eeprom);
            var lancement = new List<(string, string, byte[])>
            {
                ("saves/fbneo/fbneo/blazstar.fs", c1, neo),
                ("saves/fbneo/fbneo/19xx.nv", c2, eeprom),
            };

            var entrees = NvramSnapshotService.EntreesDuPasseport(lancement, ["fbneo/19xx.nv"], "fbneo", saves);

            Assert.Equal(2, entrees.Count);
            Assert.Equal(neo, Convert.FromBase64String((string)entrees[0]!["end"]!));
            Assert.Equal(65536, (int)entrees[0]!["size"]!);
            Assert.Equal(eeprom, Convert.FromBase64String((string)entrees[1]!["start"]!));
            Assert.Equal(eeprom, Convert.FromBase64String((string)entrees[1]!["end"]!));
        }
        finally { Directory.Delete(saves, true); }
    }

    [Fact]
    public void Une_NVRAM_non_epinglee_qui_grossit_au_dela_de_64_Ko_finit_vide_comme_avant()
    {
        var saves = Dossier();
        try
        {
            var petit = new byte[1024];
            var chemin = Poser(saves, "fbneo/fbneo/jeu.nv", new byte[70000]);
            var lancement = new List<(string, string, byte[])> { ("saves/fbneo/fbneo/jeu.nv", chemin, petit) };

            var e = Assert.Single(NvramSnapshotService.EntreesDuPasseport(lancement, [], "fbneo", saves))!;
            Assert.Equal("", (string?)e["end"]);
        }
        finally { Directory.Delete(saves, true); }
    }

    [Fact]
    public void Une_NVRAM_epinglee_effacee_a_la_selection_part_avec_un_depart_vide_et_sa_fin_de_128_Ko()
    {
        var saves = Dossier();
        try
        {
            var fin = RamPgm();
            Poser(saves, "mame/fbneo/ket.fs", fin);   // ecrite par FBNeo a la fermeture

            var e = Assert.Single(NvramSnapshotService.EntreesDuPasseport([], ["fbneo/ket.fs"], "mame", saves))!;
            Assert.Equal("saves/mame/fbneo/ket.fs", (string?)e["file"]);
            Assert.Equal("", (string?)e["start"]);
            Assert.Equal(fin, Convert.FromBase64String((string)e["end"]!));
        }
        finally { Directory.Delete(saves, true); }
    }

    [Fact]
    public void Au_dela_de_256_Ko_rien_n_est_joint_et_le_serveur_refusera()
    {
        var saves = Dossier();
        try
        {
            Poser(saves, "mame/fbneo/ket.fs", new byte[NvramSnapshotService.TailleEpingleeMax + 1]);

            var e = Assert.Single(NvramSnapshotService.EntreesDuPasseport([], ["fbneo/ket.fs"], "mame", saves))!;
            Assert.Equal("", (string?)e["end"]);
            Assert.Null(NvramSnapshotService.Lire(Path.Combine(saves, "mame", "fbneo", "ket.fs"), NvramSnapshotService.TailleEpingleeMax));
        }
        finally { Directory.Delete(saves, true); }
    }

    // ---- Les reglages du menu de test (ReglagesDuMenu) ----

    private const string ProfilKetsui = """
        {"difficulty":{"fields":[
          {"address":"0x1005","initial":1,"accept":[1],"label":{"en":"Difficulty"},"values":{"1":{"en":"Normal"}},
           "name":"DIFFICULTY","nvram":{"file":"fbneo/ket.fs","offset":4101}},
          {"address":"0x1004","initial":0,"accept":[0],"values":{"0":{}},"name":"EXTEND","nvram":{"file":"fbneo/ket.fs","offset":4100}},
          {"address":"0x1007","initial":0,"accept":[0],"values":{"0":{}},"name":"SHIP STOCK","nvram":{"file":"fbneo/ket.fs","offset":4103}}
        ]}}
        """;

    private static JsonArray Nvram(byte[]? debut, byte[]? fin, string fichier = "saves/mame/fbneo/ket.fs") => new()
    {
        new JsonObject
        {
            ["file"] = fichier,
            ["size"] = debut?.Length ?? 0,
            ["start"] = debut is null ? "" : Convert.ToBase64String(debut),
            ["end"] = fin is null ? "" : Convert.ToBase64String(fin),
        },
    };

    [Fact]
    public void Un_depart_vierge_et_une_fin_d_usine_ne_disent_rien()
    {
        using var p = JsonDocument.Parse(ProfilKetsui);
        Assert.Empty(ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, Nvram(null, RamPgm())));
        var tout_ff = Enumerable.Repeat((byte)0xFF, 131072).ToArray();
        Assert.Empty(ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, Nvram(tout_ff, RamPgm())));
    }

    [Fact]
    public void Un_depart_hors_usine_est_vu_meme_si_la_fin_est_revenue_a_l_usine()
    {
        using var p = JsonDocument.Parse(ProfilKetsui);
        var r = Assert.Single(ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, Nvram(RamPgm((0x1005, 0x00)), RamPgm())));
        Assert.Equal(new ReglagesDuMenu.Reglage("0x1005", "DIFFICULTY", 0), r);
    }

    [Fact]
    public void Une_fin_hors_usine_est_vue_apres_un_depart_vierge()
    {
        using var p = JsonDocument.Parse(ProfilKetsui);
        var r = ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, Nvram(null, RamPgm((0x1004, 0x02), (0x1007, 0x03))));
        Assert.Equal(new[] { "EXTEND", "SHIP STOCK" }, r.Select(x => x.Nom).ToArray());
    }

    [Fact]
    public void La_valeur_lue_dans_la_NVRAM_devient_la_difficulte_signee_et_le_serveur_la_refuse()
    {
        using var p = JsonDocument.Parse(ProfilKetsui);
        var horsUsine = ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, Nvram(RamPgm((0x1005, 0x03)), RamPgm((0x1005, 0x03))));

        // Sans signal du wrapper, la difficulte signee etait la valeur « initial » : 1 (NORMAL), admise.
        var avant = ModesDeJeu.DifficultePourLePasseport(p.RootElement, ContexteDeJeu.Vide)!;
        Assert.Equal(1, (int)avant["0x1005"]!);

        var apres = ModesDeJeu.DifficultePourLePasseport(p.RootElement, ReglagesDuMenu.Appliquer(ContexteDeJeu.Vide, horsUsine))!;
        Assert.Equal(3, (int)apres["0x1005"]!);
        Assert.Equal(0, (int)apres["0x1004"]!);
        Assert.Equal(new[] { "DIFFICULTY" }, ReglagesDuMenu.NomsRefuses(p.RootElement, apres).ToArray());
    }

    [Fact]
    public void Une_valeur_hors_usine_vue_en_jeu_par_le_MEM_est_nommee_aussi()
    {
        using var p = JsonDocument.Parse(ProfilKetsui);
        var ctx = ContexteDeJeu.Vide.AvecDifficulte("0X1004", 1);
        var signee = ModesDeJeu.DifficultePourLePasseport(p.RootElement, ctx);
        Assert.Equal(new[] { "EXTEND" }, ReglagesDuMenu.NomsRefuses(p.RootElement, signee).ToArray());
        Assert.Equal("test menu settings changed: EXTEND", ReglagesDuMenu.Motif(["EXTEND"], "en"));
        Assert.Equal("réglages du menu de test changés : DIFFICULTY, EXTEND", ReglagesDuMenu.Motif(["DIFFICULTY", "EXTEND"], "fr"));
    }

    [Fact]
    public void Un_profil_sans_champ_nvram_signe_la_meme_difficulte_qu_avant()
    {
        // Streets of Rage 2 et Blazing Star : des champs imposes, sans « nvram » ni « name ».
        using var p = JsonDocument.Parse("""
            {"difficulty":{"fields":[{"address":"0xFD04","initial":4,"accept":[4],"label":{"en":"Difficulty"}},
                                     {"address":"0xFD06","initial":2,"accept":[2]}]}}
            """);
        var nvram = Nvram(RamPgm((0x1005, 0x00)), RamPgm((0x1005, 0x00)));

        Assert.Empty(ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, nvram));
        var ctx = ContexteDeJeu.Vide.AvecDifficulte("0xFD04", 6);
        var signee = ModesDeJeu.DifficultePourLePasseport(p.RootElement, ReglagesDuMenu.Appliquer(ctx, []))!;
        Assert.Equal(6, (int)signee["0xFD04"]!);
        Assert.Empty(ReglagesDuMenu.NomsRefuses(p.RootElement, signee));
    }

    [Fact]
    public void L_entree_jugee_est_celle_que_le_coeur_lit_comme_au_serveur()
    {
        using var p = JsonDocument.Parse(ProfilKetsui);
        // Une copie egaree (saves/mame/ket.fs... jointe d'abord) ne cache pas la vraie, hors usine.
        var nvram = new JsonArray
        {
            new JsonObject { ["file"] = "saves/mame/x/fbneo/ket.fs", ["size"] = 0, ["start"] = "", ["end"] = Convert.ToBase64String(RamPgm()) },
            new JsonObject { ["file"] = "saves/mame/fbneo/ket.fs", ["size"] = 0, ["start"] = "", ["end"] = Convert.ToBase64String(RamPgm((0x1005, 2))) },
        };
        Assert.Equal("DIFFICULTY", Assert.Single(ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, nvram)).Nom);
        // Un fichier dont le nom finit pareil sans etre le meme (« xket.fs ») n'est pas pris.
        Assert.Empty(ReglagesDuMenu.HorsUsineDansLaNvram(p.RootElement, Nvram(null, RamPgm((0x1005, 2)), "saves/mame/fbneo/xket.fs")));
    }
}
