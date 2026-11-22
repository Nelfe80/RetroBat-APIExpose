using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>DEUX JEUX D'ETATS SELON LE COEUR (22/11) : le bon etat pour le coeur installe, jamais un etat d'une autre taille.</summary>
public class EtatsParCoeurTests
{
    private static readonly string Ancien = new('a', 64);
    private static readonly string Nouveau = new('b', 64);
    private static readonly string Inconnu = new('c', 64);

    private static string Modele => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "metal-slug-3.mission-1-knife.json"));

    private static JsonObject Etat(string nom, string sha, params string[] coeurs)
    {
        var e = new JsonObject { ["name"] = nom, ["sha256"] = sha, ["file"] = $"arcade/metal-slug-3/{nom}.state", ["size"] = 414187 };
        if (coeurs.Length > 0) e["core_sha256"] = new JsonArray(coeurs.Select(c => (JsonNode)c).ToArray());
        return e;
    }

    private static Reglement Deux(bool avecStateAncien = true)
    {
        var doc = JsonNode.Parse(Modele)!.AsObject();
        doc["start_policy"] = "canonical_state";
        doc["state"] = Etat("m1-ancien", new string('1', 64));
        if (!avecStateAncien) doc.Remove("state");
        doc["states"] = new JsonArray(Etat("m1-ancien", new string('1', 64), Ancien[..16]), Etat("m1-c261102", new string('2', 64), Nouveau));
        return Reglement.Lire(doc.ToJsonString());
    }

    [Fact]
    public void Le_coeur_installe_choisit_son_etat()
    {
        var r = Deux();
        Assert.Equal("m1-ancien", r.EtatPour(Ancien)!.Nom);
        Assert.Equal("m1-c261102", r.EtatPour(Nouveau.ToUpperInvariant())!.Nom);
    }

    [Fact]
    public void Un_coeur_inconnu_ou_illisible_ne_recoit_aucun_etat()
    {
        var r = Deux();
        Assert.Null(r.EtatPour(Inconnu));
        Assert.Null(r.EtatPour(null));
    }

    [Fact]
    public void Un_ancien_reglement_sans_states_vaut_pour_tout_coeur()
    {
        var doc = JsonNode.Parse(Modele)!.AsObject();
        doc["start_policy"] = "canonical_state";
        doc["state"] = Etat("m1", new string('1', 64));
        var r = Reglement.Lire(doc.ToJsonString());
        Assert.Empty(r.Etats);
        Assert.Same(r.Etat, r.EtatPour(Inconnu));
        Assert.Same(r.Etat, r.EtatPour(null));
    }

    [Fact]
    public void Le_state_reste_l_etat_de_l_ancien_coeur_pour_une_api_plus_ancienne()
    {
        var r = Deux();
        Assert.Equal("m1-ancien", r.Etat!.Nom);
        Assert.Equal(414187, r.Etat.Taille);
        // sans « state » : le premier des « states » fait office d'etat par defaut
        Assert.Equal("m1-ancien", Deux(avecStateAncien: false).Etat!.Nom);
    }

    [Fact]
    public void Un_state_qui_nomme_un_coeur_est_strict_meme_seul()
    {
        var doc = JsonNode.Parse(Modele)!.AsObject();
        doc["start_policy"] = "canonical_state";
        doc["state"] = Etat("m1", new string('1', 64), Ancien);
        var r = Reglement.Lire(doc.ToJsonString());
        Assert.NotNull(r.EtatPour(Ancien));
        Assert.Null(r.EtatPour(Nouveau));
    }

    [Fact]
    public void Un_states_mal_forme_est_refuse()
    {
        var doc = JsonNode.Parse(Modele)!.AsObject();
        doc["start_policy"] = "canonical_state";
        doc["state"] = Etat("m1", new string('1', 64));
        doc["states"] = new JsonArray(Etat("a", new string('1', 64)));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(doc.ToJsonString()));
        doc["states"] = new JsonArray(Etat("a", new string('1', 64), Ancien), Etat("b", new string('2', 64), Ancien));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(doc.ToJsonString()));
        doc["states"] = new JsonArray(Etat("a", new string('1', 64), "xyz"));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(doc.ToJsonString()));
    }

    [Fact]
    public void La_taille_annoncee_est_verifiee_a_la_lecture()
    {
        var dossier = Directory.CreateTempSubdirectory().FullName;
        try
        {
            byte[] octets = [1, 2, 3];
            File.WriteAllBytes(Path.Combine(dossier, "e.state"), octets);
            var sha = Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();
            Assert.NotNull(EtatsDesDefis.Lire(new EtatDeDepart("e", sha, "e.state", null, 3), dossier).Octets);
            Assert.Equal("state_mismatch", EtatsDesDefis.Lire(new EtatDeDepart("e", sha, "e.state", null, 4), dossier).Refus);
        }
        finally { Directory.Delete(dossier, true); }
    }

    [Fact]
    public void Le_coeur_installe_se_lit_dans_cores_ou_dans_cores_real_si_cores_porte_le_wrapper()
    {
        var racine = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(racine, "cores"));
            Directory.CreateDirectory(Path.Combine(racine, "cores_real"));
            var wrapper = Path.Combine(racine, "wrapper.dll");
            File.WriteAllBytes(wrapper, [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(racine, "cores", "fbneo_libretro.dll"), [1, 1, 1]);
            string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
            Assert.Equal(Sha([1, 1, 1]), EtatsParCoeur.EmpreinteDuCoeurInstalle("fbneo", racine, wrapper));
            // ancien montage : cores\ porte le wrapper, le vrai coeur est dans cores_real\
            File.WriteAllBytes(Path.Combine(racine, "cores", "fbneo_libretro.dll"), [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(racine, "cores_real", "fbneo_libretro.dll"), [2, 2, 2]);
            Assert.Equal(Sha([2, 2, 2]), EtatsParCoeur.EmpreinteDuCoeurInstalle("fbneo", racine, wrapper));
            Assert.Null(EtatsParCoeur.EmpreinteDuCoeurInstalle("absent", racine, wrapper));
        }
        finally { Directory.Delete(racine, true); }
    }

    private static MultiplayerStart Depart(string motif, params string[] coeurs)
        => new() { State = $"states/arcade/sf2ce/{motif}.state", Sha256 = new string(motif[0], 64), CoreSha256 = coeurs.Length == 0 ? null : coeurs };

    [Fact]
    public void Multiplayer_choisit_le_depart_du_coeur_et_n_en_charge_aucun_d_une_autre_taille()
    {
        var jeu = new MultiplayerGame
        {
            Id = "arcade/sf2ce", System = "arcade", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = Depart("1ancien", Ancien), Starts = [Depart("2nouveau", Nouveau)],
        };
        Assert.True(jeu.ADesEtatsParCoeur);
        Assert.Equal("1ancien", Path.GetFileNameWithoutExtension(jeu.DepartPour(2, null, Ancien)!.State));
        Assert.Equal("2nouveau", Path.GetFileNameWithoutExtension(jeu.DepartPour(2, null, Nouveau)!.State));
        Assert.Null(jeu.DepartPour(2, null, Inconnu));
        Assert.Null(jeu.DepartPour(2));
        Assert.False(jeu.ADesDepartsPour(null, Inconnu));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu));
    }

    [Fact]
    public void Multiplayer_un_catalogue_sans_core_sha256_ne_change_pas()
    {
        var jeu = new MultiplayerGame { Id = "arcade/sf2ce", System = "arcade", Players = 2, Level = MultiplayerLevels.NelfePlay, Start = Depart("1x") };
        Assert.False(jeu.ADesEtatsParCoeur);
        Assert.Same(jeu.Start, jeu.DepartPour(2, null, Inconnu));
        Assert.Same(jeu.Start, jeu.DepartPour(2));
    }

    [Fact]
    public void Multiplayer_refuse_un_core_sha256_mal_forme_ou_en_double()
    {
        var mal = new MultiplayerGame { Id = "arcade/sf2ce", System = "arcade", Players = 2, Level = MultiplayerLevels.NelfePlay, Start = Depart("1x", "zz") };
        Assert.Contains("core_sha256", MultiplayerCatalogueStore.ValiderLeDepart(mal));
        var doublon = new MultiplayerGame
        {
            Id = "arcade/sf2ce", System = "arcade", Players = 2, Level = MultiplayerLevels.NelfePlay,
            Start = Depart("1ancien", Ancien), Starts = [Depart("2autre", Ancien)],
        };
        Assert.NotNull(MultiplayerCatalogueStore.ValiderLeDepart(doublon));
    }
}
