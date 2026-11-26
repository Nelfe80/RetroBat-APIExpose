using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 26/11, Rainbow Islands : `rbisland` (romof cchip) ne se lance pas sous FBNeo sans cchip.zip, et le joueur n'avait aucune
/// explication. Le controle lit le DAT, cherche le BIOS la ou FBNeo le cherche, et previent sans jamais bloquer.
/// </summary>
public class ControleBiosFbneoTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "bios_fbneo_" + Guid.NewGuid().ToString("N"));
    private readonly string _roms, _fbneo, _bios;

    public ControleBiosFbneoTests()
    {
        _roms = Path.Combine(_racine, "roms", "fbneo");
        _fbneo = Path.Combine(_racine, "bios", "fbneo");
        _bios = Path.Combine(_racine, "bios");
        Directory.CreateDirectory(_roms);
        Directory.CreateDirectory(_fbneo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, true); } catch (IOException) { }
    }

    private static string Zip(string chemin, params (string Nom, string Contenu)[] fichiers)
    {
        using var z = ZipFile.Open(chemin, ZipArchiveMode.Create);
        foreach (var (nom, contenu) in fichiers)
        {
            var e = z.CreateEntry(nom);
            using var s = e.Open();
            s.Write(System.Text.Encoding.ASCII.GetBytes(contenu));
        }

        return chemin;
    }

    private static string Crc(string chemin, string nom)
    {
        using var z = ZipFile.OpenRead(chemin);
        return z.GetEntry(nom)!.Crc32.ToString("x8");
    }

    /// <summary>Un DAT de poche : rbisland (parent cchip), neogeo a deux variantes, mslug3, un set sans BIOS.</summary>
    private string Dat(string crcCchip, string crcMvs, string crcAes)
    {
        var dossier = Path.Combine(_racine, "dat");
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, ControleBiosFbneo.DatArcade), $$"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>FinalBurn Neo</name><version>test</version></header>
              <game name="rbisland" romof="cchip"><description>Rainbow Islands</description><rom name="b22-10-1.19" size="4" crc="e34a50ca"/></game>
              <game name="jumping" cloneof="rbisland" romof="rbisland"><description>Jumping</description></game>
              <game isbios="yes" name="cchip"><description>C-Chip</description><rom name="cchip_upd78c11.bin" size="4" crc="{{crcCchip}}"/></game>
              <game isbios="yes" name="neogeo"><description>Neo Geo</description><rom name="sp-s2.sp1" size="4" crc="{{crcMvs}}"/><rom name="sp-e.sp1" size="4" crc="{{crcAes}}"/></game>
              <game name="mslug3" romof="neogeo"><description>Metal Slug 3</description><rom name="256-p1.p1" size="4" crc="11111111"/></game>
              <game name="galaga"><description>Galaga</description><rom name="g.bin" size="4" crc="22222222"/></game>
            </datafile>
            """);
        return dossier;
    }

    private (ControleBiosFbneo Controle, List<(DeckNotice Plaque, string Texte)> Annonces) Controle(string dossierDat, bool worldScoring = true)
    {
        var annonces = new List<(DeckNotice, string)>();
        var c = new ControleBiosFbneo
        {
            DossierDuDat = dossierDat,
            Verdict = _ => worldScoring ? "collection" : null,
            Annonce = (p, t) => annonces.Add((p, t)),
        };
        return (c, annonces);
    }

    [Fact]
    public void Rainbow_Islands_sans_cchip_zip_previent_et_nomme_le_fichier()
    {
        var rom = Zip(Path.Combine(_roms, "rbisland.zip"), ("b22-10-1.19", "rom"));
        var cchip = Zip(Path.Combine(_racine, "ref_cchip.zip"), ("cchip_upd78c11.bin", "c-chip"));
        var dat = Dat(Crc(cchip, "cchip_upd78c11.bin"), "aaaaaaaa", "bbbbbbbb");
        var (c, annonces) = Controle(dat);
        c.DossiersDeBios = [_roms, _fbneo, _bios];

        var absent = c.Verifier(rom);

        Assert.NotNull(absent);
        Assert.Equal("cchip.zip", absent!.Fichier);
        Assert.Equal("cchip_upd78c11.bin", absent.Contenu);
        var (plaque, texte) = Assert.Single(annonces);
        Assert.Equal("BIOS MISSING", plaque.Title);
        Assert.Equal(DeckSeverity.Alert, plaque.Severity);
        Assert.Contains("CCHIP.ZIP", plaque.Text);
        Assert.Contains("cchip.zip", texte);
        Assert.Contains(Path.Combine("bios", "fbneo"), texte);
    }

    [Theory]
    [InlineData("fbneo")]
    [InlineData("bios")]
    [InlineData("roms")]
    public void Rainbow_Islands_avec_cchip_zip_a_l_un_des_trois_endroits_ne_dit_rien(string ou)
    {
        var rom = Zip(Path.Combine(_roms, "rbisland.zip"), ("b22-10-1.19", "rom"));
        var cchip = Zip(Path.Combine(_racine, "ref_cchip.zip"), ("cchip_upd78c11.bin", "c-chip"));
        var dossier = ou switch { "fbneo" => _fbneo, "bios" => _bios, _ => _roms };
        File.Copy(cchip, Path.Combine(dossier, "cchip.zip"));
        var (c, annonces) = Controle(Dat(Crc(cchip, "cchip_upd78c11.bin"), "aaaaaaaa", "bbbbbbbb"));
        c.DossiersDeBios = ControleBiosFbneo.Dossiers(rom, _racine);

        Assert.Null(c.Verifier(rom));
        Assert.Empty(annonces);
    }

    [Fact]
    public void Un_cchip_zip_qui_ne_porte_pas_le_bon_fichier_est_signale()
    {
        var rom = Zip(Path.Combine(_racine, "rbisland.zip"), ("b22-10-1.19", "rom"));
        var cchip = Zip(Path.Combine(_racine, "ref_cchip.zip"), ("cchip_upd78c11.bin", "c-chip"));
        Zip(Path.Combine(_fbneo, "cchip.zip"), ("autre.bin", "pas le bon"));
        var (c, _) = Controle(Dat(Crc(cchip, "cchip_upd78c11.bin"), "aaaaaaaa", "bbbbbbbb"));
        c.DossiersDeBios = [_fbneo];

        Assert.NotNull(c.Verifier(rom));
    }

    [Fact]
    public void Le_clone_d_un_set_a_bios_est_controle_par_la_chaine_romof()
    {
        var rom = Zip(Path.Combine(_roms, "jumping.zip"), ("x.bin", "x"));
        var (c, _) = Controle(Dat("43021521", "aaaaaaaa", "bbbbbbbb"));
        c.DossiersDeBios = [_roms, _fbneo, _bios];

        // jumping -> rbisland (pas un BIOS) -> cchip : le BIOS du grand-parent manque aussi.
        Assert.Equal("cchip.zip", c.Verifier(rom)!.Fichier);
    }

    [Fact]
    public void Neo_Geo_une_seule_variante_du_bios_suffit_et_un_jeu_a_bios_ouvert_n_est_pas_casse()
    {
        var rom = Zip(Path.Combine(_roms, "mslug3.zip"), ("256-p1.p1", "p1"));
        var neo = Zip(Path.Combine(_racine, "ref_neogeo.zip"), ("sp-s2.sp1", "mvs"));
        Zip(Path.Combine(_fbneo, "neogeo.zip"), ("sp-s2.sp1", "mvs"));
        var (c, annonces) = Controle(Dat("43021521", Crc(neo, "sp-s2.sp1"), "bbbbbbbb"));
        c.DossiersDeBios = [_roms, _fbneo, _bios];

        Assert.Null(c.Verifier(rom));
        Assert.Empty(annonces);

        File.Delete(Path.Combine(_fbneo, "neogeo.zip"));
        Assert.Equal("neogeo.zip", c.Verifier(rom)!.Fichier);
    }

    [Fact]
    public void Un_set_sans_bios_un_set_inconnu_et_une_partie_du_joueur_ne_disent_rien()
    {
        var galaga = Zip(Path.Combine(_roms, "galaga.zip"), ("g.bin", "g"));
        var inconnu = Zip(Path.Combine(_roms, "inconnu.zip"), ("z.bin", "z"));
        var rb = Zip(Path.Combine(_roms, "rbisland.zip"), ("b22-10-1.19", "rom"));
        var dat = Dat("43021521", "aaaaaaaa", "bbbbbbbb");
        var (c, annonces) = Controle(dat);
        c.DossiersDeBios = [_fbneo];
        Assert.Null(c.Verifier(galaga));
        Assert.Null(c.Verifier(inconnu));
        Assert.Empty(annonces);

        var (joueur, annoncesJoueur) = Controle(dat, worldScoring: false);
        joueur.DossiersDeBios = [_fbneo];
        Assert.Null(joueur.Verifier(rb));
        Assert.Empty(annoncesJoueur);
    }

    [Fact]
    public void Sans_dat_ou_avec_un_dat_illisible_rien_ne_se_dit_et_rien_ne_plante()
    {
        var rom = Zip(Path.Combine(_roms, "rbisland.zip"), ("b22-10-1.19", "rom"));
        var vide = Path.Combine(_racine, "pas_de_dat");
        Directory.CreateDirectory(vide);
        var (c, annonces) = Controle(vide);
        Assert.Null(c.Verifier(rom));

        File.WriteAllText(Path.Combine(vide, ControleBiosFbneo.DatArcade), "<datafile><game name=");
        Assert.Null(c.Verifier(rom));
        Assert.Empty(annonces);
    }

    [Fact]
    public void Le_message_s_annonce_une_fois_par_set_et_par_moment()
    {
        var rom = Zip(Path.Combine(_roms, "rbisland.zip"), ("b22-10-1.19", "rom"));
        var (c, annonces) = Controle(Dat("43021521", "aaaaaaaa", "bbbbbbbb"));
        c.DossiersDeBios = [_fbneo];
        Assert.NotNull(c.Verifier(rom));
        Assert.NotNull(c.Verifier(rom));
        Assert.Single(annonces);
    }

    [Fact]
    public void Le_texte_vient_du_dictionnaire_de_la_langue_sinon_de_l_anglais_par_defaut()
    {
        var a = new ControleBiosFbneo.BiosAbsent("rbisland", "cchip", "cchip.zip", "cchip_upd78c11.bin", @"bios\fbneo");
        Assert.Equal("This game needs the BIOS file cchip.zip (cchip_upd78c11.bin), which was not found. Copy it into bios\\fbneo "
            + "and start the game again. NelfePlay does not provide BIOS files.", ControleBiosFbneo.Message(a, null, "fr"));

        var dico = Path.Combine(_racine, "textes.json");
        File.WriteAllText(dico, "{\"fr\":{\"" + ControleBiosFbneo.Cle + "\":\"Il manque {file} : copie-le dans {folder}.\"}}");
        var textes = new InterfaceTextService(dico);
        Assert.Equal(@"Il manque cchip.zip : copie-le dans bios\fbneo.", ControleBiosFbneo.Message(a, textes, "fr"));
        Assert.StartsWith("This game needs", ControleBiosFbneo.Message(a, textes, "de"));
    }

    [Fact]
    public void Le_dat_reel_de_la_borne_dit_que_rbisland_exige_cchip_si_le_dat_est_la()
    {
        var reel = Environment.GetEnvironmentVariable("NELFEPLAY_BIOS_FBNEO") ?? @"E:\RetroBat\bios\fbneo";
        var dat = ControleBiosFbneo.LireLeDat(reel);
        if (dat is null) return;
        Assert.True(dat["cchip"].EstBios);
        Assert.Equal("cchip", dat["rbisland"].RomOf);
        Assert.Contains(dat["cchip"].Roms, r => r.Crc == "43021521");
        Assert.True(dat["neogeo"].EstBios);
    }

    [Fact]
    public void Les_dossiers_cherches_sont_ceux_de_FBNeo()
    {
        var d = ControleBiosFbneo.Dossiers(Path.Combine(_roms, "rbisland.zip"), _racine);
        Assert.Equal([_roms, _fbneo, _bios], d.ToArray());
    }
}
