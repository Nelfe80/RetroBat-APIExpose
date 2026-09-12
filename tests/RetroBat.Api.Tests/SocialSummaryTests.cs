using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Overlay;
using RetroBat.Api.Replay.Social;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le resume signe des reactions, et le cameo qu'il commande.
///
/// Le vecteur est un resume REEL rendu par la plateforme (replay Sonic, 2026-09-12), cle publique
/// comprise : si la canonicalisation de la borne divergeait de celle de la plateforme (un objet
/// imbrique ecrit comme une liste, un nombre a virgule), la signature ne tiendrait plus ici.
/// </summary>
public sealed class SocialSummaryTests
{
    private static readonly JsonObject Vecteur = (JsonObject) JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "social-summary-vector.json")))!;

    private static (byte[] Spki, string KeyId) Emetteur()
        => SocialEventVerifier.FromPem(Vecteur["issuer_public_key"]!.GetValue<string>());

    [Fact]
    public void Le_resume_reel_de_la_plateforme_est_accepte()
    {
        var (spki, keyId) = Emetteur();
        var (resume, refus) = SocialSummary.Verifier(Vecteur, spki, keyId);
        Assert.Equal(string.Empty, refus);
        Assert.NotNull(resume);
        Assert.Equal("rp_06G67BPZ3V2Y2F0ZZVM3HCN7WW", resume!.TargetId);
        Assert.Equal(96, resume.Chaleur.Count);
        Assert.Equal(60.0, resume.Fps);
        Assert.Equal(20521, resume.ReplayEnd);
        Assert.Equal(3, resume.TotalReactions);
        Assert.Equal(3, resume.Cameos.Count);
        var premier = resume.Cameos[0];
        Assert.Equal("Nelfe80", premier.Nom);
        Assert.Equal(1, premier.Rang);
        Assert.Equal(2, premier.Premieres);
        Assert.Equal(1030, premier.Frame);
        Assert.NotNull(premier.Avatar.Planche);
        Assert.Equal(6666, premier.Avatar.Variation);
    }

    [Fact]
    public void Un_resume_altere_est_refuse()
    {
        var (spki, keyId) = Emetteur();
        var copie = (JsonObject) Vecteur.DeepClone();
        copie["body"]!["totals"]!["reactions"] = 300;
        var (resume, refus) = SocialSummary.Verifier(copie, spki, keyId);
        Assert.Null(resume);
        Assert.Equal("signature_invalid", refus);

        var autreCle = (JsonObject) Vecteur.DeepClone();
        var (r2, refus2) = SocialSummary.Verifier(autreCle, spki, new string('0', 64));
        Assert.Null(r2);
        Assert.Equal("issuer_unknown", refus2);
    }

    // ── Le cameo ─────────────────────────────────────────────────────────────

    private static SocialSummary.Cameo Cameo(long frame) => new(frame, "wow", 2, "Nelfe80", "cd0edd1cac6911f1", 1, 2, 2, 2,
        new SocialSummary.Avatar("Nelfe80", "platformer", 6666, Array.Empty<string>(), "atelier64-v7", new string('a', 64)));

    [Fact]
    public void Le_cameo_entre_une_seconde_avant_et_saute_pile_a_la_frame()
    {
        var m = new ReplayCameoModel();
        m.Charger(new[] { Cameo(6000) }, 60);

        // A 2 s de la frame : rien.
        Assert.Null(m.Relever(6000 - 120, false, 10_000));
        // A 1 s : l'entree commence, depuis le bas.
        var e = m.Relever(6000 - 60, false, 11_000);
        Assert.NotNull(e);
        Assert.Equal(ReplayCameoModel.Phase.Entree, e!.Phase);
        Assert.InRange(e.Montee, 0f, 0.05f);
        Assert.True(e.Bouge);
        // A mi-chemin : a moitie monte, encore de face, pas de saut.
        e = m.Relever(6000 - 30, false, 11_500);
        Assert.Equal(ReplayCameoModel.Phase.Entree, e!.Phase);
        Assert.InRange(e.Montee, 0.45f, 0.55f);
        Assert.Equal(0, e.Hauteur);
        // La frame arrive : le saut PART, meme si l'horloge n'a pas encore fini l'entree.
        e = m.Relever(6000, false, 11_990);
        Assert.Equal(ReplayCameoModel.Phase.Saut, e!.Phase);
        Assert.Equal(1f, e.Montee);
        // Pendant le saut : pirouette (droite, dos, gauche), hauteur, emoji en vol.
        var vues = new List<int>();
        var maxHauteur = 0f;
        for (var t = 20; t < ReplayCameoModel.SautMs; t += 40)
        {
            e = m.Relever(6000 + t / 16, false, 11_990 + t);
            Assert.Equal(ReplayCameoModel.Phase.Saut, e!.Phase);
            vues.Add(e.Vue);
            maxHauteur = Math.Max(maxHauteur, e.Hauteur);
            Assert.InRange(e.Vol, 0f, 1f);
        }
        Assert.Contains(2, vues);   // droite
        Assert.Contains(1, vues);   // dos
        Assert.Contains(3, vues);   // gauche
        Assert.Equal(0, vues[^1]);  // il retombe de face
        Assert.InRange(maxHauteur, ReplayCameoModel.HauteurSaut * 0.9f, ReplayCameoModel.HauteurSaut);
        // Puis il reste debout, l'emoji finit son vol, et il redescend.
        e = m.Relever(6100, false, 11_990 + ReplayCameoModel.SautMs + 100);
        Assert.Equal(ReplayCameoModel.Phase.Repos, e!.Phase);
        Assert.Equal(0, e.Vue);
        e = m.Relever(6150, false, 11_990 + ReplayCameoModel.SautMs + ReplayCameoModel.AttenteMs + 350);
        Assert.Equal(ReplayCameoModel.Phase.Sortie, e!.Phase);
        Assert.InRange(e.Montee, 0.4f, 0.6f);
        Assert.Null(m.Relever(6200, false, 11_990 + ReplayCameoModel.SautMs + ReplayCameoModel.AttenteMs + ReplayCameoModel.SortieMs + 10));
        Assert.False(m.EnScene);
        // Et il ne se rejoue pas tout seul.
        Assert.Null(m.Relever(6300, false, 20_000));
    }

    [Fact]
    public void En_pause_il_attend_debout_et_saute_quand_la_frame_arrive()
    {
        var m = new ReplayCameoModel();
        m.Charger(new[] { Cameo(6000) }, 60);
        Assert.NotNull(m.Relever(5950, false, 1_000));
        // Pause : la frame ne bouge plus, l'horloge si. Il finit de monter, puis attend.
        var e = m.Relever(5970, true, 2_500);
        Assert.Equal(ReplayCameoModel.Phase.Attente, e!.Phase);
        Assert.Equal(1f, e.Montee);
        Assert.False(e.Bouge);
        e = m.Relever(6000, false, 3_000);
        Assert.Equal(ReplayCameoModel.Phase.Saut, e!.Phase);
    }

    [Fact]
    public void Un_saut_dans_la_lecture_ne_le_fait_pas_arriver_en_retard()
    {
        var m = new ReplayCameoModel();
        m.Charger(new[] { Cameo(6000), Cameo(12000) }, 60);
        Assert.NotNull(m.Relever(5950, false, 1_000));
        // La lecture saute loin devant : le premier cameo est abandonne, le second n'est pas encore du.
        Assert.Null(m.Relever(9000, false, 1_200));
        Assert.False(m.EnScene);
        Assert.Null(m.Relever(11000, false, 1_400));
        Assert.NotNull(m.Relever(11950, false, 1_600));
        // Une reprise depuis le debut le remet en jeu.
        Assert.Null(m.Relever(100, false, 30_000));
        Assert.NotNull(m.Relever(5950, false, 31_000));
    }

    [Fact]
    public void Une_lecture_qui_reprend_apres_les_cameos_n_en_montre_aucun()
    {
        var m = new ReplayCameoModel();
        m.Charger(new[] { Cameo(1000), Cameo(2000) }, 60);
        Assert.Null(m.Relever(15000, false, 1_000));
        Assert.Null(m.Relever(15060, false, 2_000));
        Assert.False(m.EnScene);
    }
}
