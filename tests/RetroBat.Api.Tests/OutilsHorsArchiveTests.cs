using System;
using System.IO;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'outil de diagnostic faisait 62 Mo des 62 Mo de chaque update.7z (1.9.14, 1.9.15) : il voyage
/// desormais a part, et outils.json dit a la borne ou le prendre (decision user 2026-10-01). Un
/// outils.json ne doit pouvoir ecrire que les outils prevus, depuis les releases du depot.
/// </summary>
public class OutilsHorsArchiveTests
{
    private const string Depot = "Nelfe80/RetroBat-APIExpose";
    private const string Sha = "628f18c2e0e7aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Outil(string fichier = "RetroBat.Api.Diagnostic.exe", string sha = Sha,
        string url = "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.16/RetroBat.Api.Diagnostic.exe",
        long taille = 65958697)
        => JsonSerializer.Serialize(new { fichier, version = "1.2.0+20260930.1bf15ce", sha256 = sha, taille, url });

    [Fact]
    public void Un_outil_prevu_depuis_les_releases_du_depot_est_retenu()
    {
        var outils = SelfUpdateService.LireOutils($"{{\"outils\":[{Outil()}]}}", Depot);

        var outil = Assert.Single(outils);
        Assert.Equal("RetroBat.Api.Diagnostic.exe", outil.Fichier);
        Assert.Equal(Sha, outil.Sha256);
        Assert.Equal(65958697, outil.Taille);
    }

    [Theory]
    [InlineData("RetroBat.Api.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("..\\RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", "abc", "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/autre/depot/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "http://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 10)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 0)]
    [InlineData("RetroBat.Api.Diagnostic.exe", Sha, "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.exe", 600L * 1024 * 1024)]
    public void Le_reste_est_ignore(string fichier, string sha, string url, long taille)
    {
        Assert.Empty(SelfUpdateService.LireOutils($"{{\"outils\":[{Outil(fichier, sha, url, taille)}]}}", Depot));
    }

    [Fact]
    public void L_exe_de_retroarch_nelfeplay_est_retenu_avec_son_chemin_et_repris_sur_son_empreinte()
    {
        // 1.9.216 : le SEUL exe du lot 1 voyage par outils.json, sans version de fichier ; les sources (GPL) sont dans l'entree.
        var outil = Assert.Single(SelfUpdateService.LireOutils($"{{\"outils\":[{Outil(RetroArchNelfePlay.CheminDeLExe, RetroArchNelfePlay.ExeNelfePlay,
            "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.216/retroarch-1.22.2-nelfeplay.1.exe", 18000000)}]}}", Depot));
        Assert.Equal("tools/retroarch-nelfeplay/retroarch-1.22.2-nelfeplay.1.exe", outil.Fichier);
        // Un exe sans version se compare par empreinte, pas par version : EstUnExe l'exclut.
        Assert.False(SelfUpdateService.EstUnExe(outil.Fichier));
        Assert.True(SelfUpdateService.EstUnExe("NelfePlay.LiveStreaming.exe"));
        Assert.StartsWith("https://github.com/Nelfe80/RetroArch/tree/v1.22.2-nelfeplay.1", RetroArchNelfePlay.Sources);
        // Un autre chemin sous tools n'est pas prevu : il est ignore.
        Assert.Empty(SelfUpdateService.LireOutils($"{{\"outils\":[{Outil("tools/autre/x.7z", Sha,
            "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x.7z", 10)}]}}", Depot));
    }

    private static EtatNelfePlay Etat(bool auto = true, bool refus = false, bool nocturne = false, bool marque = false,
        string ici = "", string dansLaMarque = "", bool pret = true, bool installeur = true, bool desinstalleur = true, bool jeu = false)
        => new(auto, refus, nocturne, marque, ici, dansLaMarque, pret, installeur, desinstalleur, jeu);

    private static readonly string Notre = RetroArchNelfePlay.ExeNelfePlay;
    private static readonly string Stable = RetroArchNelfePlay.Stable;
    private static readonly string Autre = new('c', 64);

    [Fact]
    public void RetroArch_nelfeplay_se_pose_sur_la_stable_connue_et_sur_elle_seule()
    {
        Assert.Equal(ActionNelfePlay.Installer, RetroArchNelfePlayService.Decider(Etat(ici: Stable)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: Autre)));           // une autre RetroArch : jamais de retrogradation
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: "")));              // exe illisible
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: Stable, pret: false)));        // notre exe pas encore arrive ou altere
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: Stable, installeur: false)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: Stable, refus: true)));        // le joueur l'a desinstallee
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: Stable, auto: false)));        // option eteinte
    }

    [Fact]
    public void RetroArch_nelfeplay_ne_touche_jamais_une_partie()
    {
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(ici: Stable, jeu: true)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Autre, dansLaMarque: Notre, jeu: true)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(nocturne: true, jeu: true)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Notre, dansLaMarque: Notre, auto: false, jeu: true)));
    }

    [Fact]
    public void RetroArch_nelfeplay_en_place_reste_ou_se_retire_selon_l_option()
    {
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Notre, dansLaMarque: Notre)));
        Assert.Equal(ActionNelfePlay.Retirer, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Notre, dansLaMarque: Notre, auto: false)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Notre, dansLaMarque: Notre, auto: false, desinstalleur: false)));
    }

    [Fact]
    public void Une_mise_a_jour_de_RetroBat_ne_restaure_rien_et_repose_seulement_sur_la_stable()
    {
        // La marque reste, l'exe n'est plus le notre : la stable revient (meme 1.22.2) -> l'installeur ecarte la sauvegarde perimee et repose.
        Assert.Equal(ActionNelfePlay.Installer, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Stable, dansLaMarque: Notre)));
        // Une autre version : on se retire (sans restaurer, le script le sait) et on ne repose pas.
        Assert.Equal(ActionNelfePlay.Retirer, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Autre, dansLaMarque: Notre)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Autre, dansLaMarque: Notre, desinstalleur: false)));
        // Option eteinte, la stable revenue : on ne repose pas, on retire la marque.
        Assert.Equal(ActionNelfePlay.Retirer, RetroArchNelfePlayService.Decider(Etat(marque: true, ici: Stable, dansLaMarque: Notre, auto: false)));
    }

    [Fact]
    public void La_nocturne_sdl3_de_la_1_9_60_est_toujours_retiree()
    {
        Assert.Equal(ActionNelfePlay.RetirerLaNocturne, RetroArchNelfePlayService.Decider(Etat(nocturne: true, ici: Autre)));
        Assert.Equal(ActionNelfePlay.RetirerLaNocturne, RetroArchNelfePlayService.Decider(Etat(nocturne: true, auto: false)));
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(Etat(nocturne: true, desinstalleur: false)));
    }

    [Theory]
    [InlineData("retroarch", true)]
    [InlineData("retroarch.patched.RETROBAT", true)]   // l'hote netplay d'emulatorLauncher
    [InlineData("RetroArch", true)]
    [InlineData("retroarch-spectateur", true)]
    [InlineData("emulatorLauncher", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Les_processus_de_retroarch_se_reconnaissent_par_leur_prefixe(string? nom, bool attendu)
    {
        Assert.Equal(attendu, RetroArchProcessus.EstUnRetroArch(nom));
    }

    [Theory]
    [InlineData("tools/playerdeck/NelfePlay.PlayerDeck.exe")]
    [InlineData("tools/playerdeck/PlayerDeck.ini")]
    [InlineData("tools/playerdeck/themes/19xx-hangar.json")]
    [InlineData("tools/playerdeck/themes/sf2ce-world-warrior.json")]
    [InlineData("tools/playerdeck/fonts/PressStart2P-Regular.ttf")]
    [InlineData("tools/playerdeck/fonts/OFL-pressstart2p.txt")]
    public void Les_fichiers_du_PlayerDeck_sont_retenus_par_leur_dossier(string fichier)
    {
        // 1.9.163 : le Deck voyage comme l'exe du direct, sous tools/playerdeck (exe, configuration, Decks, polices).
        var outil = Assert.Single(SelfUpdateService.LireOutils($"{{\"outils\":[{Outil(fichier, Sha,
            "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.163/x", 1000)}]}}", Depot));
        Assert.Equal(fichier, outil.Fichier);
        Assert.True(SelfUpdateService.EstDuPlayerDeck(outil.Fichier));
    }

    [Theory]
    [InlineData("tools/playerdeck/NelfePlay.PlayerDeck.pdb")]          // jamais le .pdb
    [InlineData("tools/playerdeck/themes/../../../RetroBat.Api.exe")]  // jamais hors du dossier
    [InlineData("tools/playerdeck/themes/sous/x.json")]
    [InlineData("tools/playerdeck/decks/x.json")]                      // les Decks des joueurs ne viennent pas de la release
    [InlineData("tools/playerdeck/fonts/x.dll")]
    [InlineData("tools/playerdeck/autre.exe")]
    [InlineData("tools/playerdeck/themes/.json")]
    [InlineData("TOOLS/PLAYERDECK/themes/x.json")]
    public void Un_autre_fichier_sous_tools_playerdeck_est_ignore(string fichier)
    {
        Assert.Empty(SelfUpdateService.LireOutils($"{{\"outils\":[{Outil(fichier, Sha,
            "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1.9.163/x", 1000)}]}}", Depot));
    }

    [Theory]
    [InlineData(false, false, SelfUpdateService.DecisionDeck.Poser)]
    [InlineData(false, true, SelfUpdateService.DecisionDeck.Poser)]              // le Deck ne tourne pas : rien n'est tenu
    [InlineData(true, false, SelfUpdateService.DecisionDeck.ArreterPuisPoser)]   // hors partie : arrete, il repart au lancement suivant
    [InlineData(true, true, SelfUpdateService.DecisionDeck.Remettre)]            // il habille la partie : a la fin du jeu
    public void Le_PlayerDeck_ouvert_s_arrete_hors_partie_et_attend_la_fin_d_une_partie(bool deckTourne, bool partie, SelfUpdateService.DecisionDeck attendue)
    {
        Assert.Equal(attendue, SelfUpdateService.DecisionDuDeck(deckTourne, partie));
    }

    [Fact]
    public void Sans_liste_rien_n_est_retenu()
    {
        Assert.Empty(SelfUpdateService.LireOutils("{}", Depot));
        Assert.Empty(SelfUpdateService.LireOutils("{\"outils\":{}}", Depot));
    }

    [Fact]
    public void L_adresse_d_outils_json_se_lit_dans_les_actifs_de_la_release()
    {
        using var avec = JsonDocument.Parse("{\"tag_name\":\"v1.9.16\",\"assets\":[{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://x/SHA256SUMS.txt\"},{\"name\":\"outils.json\",\"browser_download_url\":\"https://x/outils.json\"}]}");
        using var sans = JsonDocument.Parse("{\"tag_name\":\"v1.9.15\",\"assets\":[{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://x/SHA256SUMS.txt\"}]}");

        Assert.Equal("https://x/outils.json", SelfUpdateService.AdresseDesOutils(avec.RootElement));
        Assert.Null(SelfUpdateService.AdresseDesOutils(sans.RootElement));
    }

    [Theory]
    [InlineData(null, "1.2.0+20260930.1bf15ce", true)]                       // la borne ne l'a pas
    [InlineData("", "1.2.0+20260930.1bf15ce", true)]                         // illisible
    [InlineData("1.1.0+20260925.abc", "1.2.0+20260930.1bf15ce", true)]       // plus ancien
    [InlineData("1.2.0+20260930.1bf15ce", "1.2.0+20260930.1bf15ce", false)]  // le meme
    [InlineData("1.3.0+20261002.def", "1.2.0+20260930.1bf15ce", false)]      // borne de developpement : jamais redescendre
    [InlineData("1.1.0", "illisible", false)]                                // version publiee illisible : on ne touche a rien
    // Quatrieme nombre (1.9.202, PlayerDeck 0.1.16.2) : il compte, et un nombre absent vaut 0
    [InlineData("0.1.16+a876afa", "0.1.16.2+9ed652380c9facc0d5ac13040c7b917c5165377c", true)]
    [InlineData("0.1.16.2+9ed652380c9facc0d5ac13040c7b917c5165377c", "0.1.16+a876afa", false)]
    [InlineData("0.1.16.2+9ed6523", "0.1.16.2+9ed6523", false)]
    [InlineData("0.1.16.2+9ed6523", "0.1.17+abc", true)]
    [InlineData("0.7.3.0", "0.7.3+a0e1989", false)]                          // 0.7.3.0 = 0.7.3
    [InlineData("0.3.0+20261112.4825568", "0.3.0+20261112.4825568", false)]  // la date apres le + ne compte pas
    public void On_ne_prend_l_outil_publie_que_s_il_manque_ou_s_il_est_plus_recent(string? locale, string publiee, bool attendu)
    {
        Assert.Equal(attendu, SelfUpdateService.AMettreAJour(locale, publiee));
    }

    [Fact]
    public void Le_nouvel_outil_remplace_l_ancien()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "outils-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var cible = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe");
            var nouveau = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe.part");
            File.WriteAllText(cible, "ancien");
            File.WriteAllText(nouveau, "nouveau");

            SelfUpdateService.Poser(nouveau, cible);

            Assert.Equal("nouveau", File.ReadAllText(cible));
            Assert.False(File.Exists(nouveau));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Un_outil_ouvert_part_en_old_et_le_nouveau_prend_sa_place()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "outils-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var cible = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe");
            var nouveau = Path.Combine(dossier, "RetroBat.Api.Diagnostic.exe.part");
            File.WriteAllText(cible, "ancien");
            File.WriteAllText(nouveau, "nouveau");

            // Comme un exe en cours d'execution : ni ecrasable ni supprimable, mais renommable.
            using (new FileStream(cible, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                SelfUpdateService.Poser(nouveau, cible);
            }

            Assert.Equal("nouveau", File.ReadAllText(cible));
            Assert.Equal("ancien", File.ReadAllText(cible + ".old"));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }
}
