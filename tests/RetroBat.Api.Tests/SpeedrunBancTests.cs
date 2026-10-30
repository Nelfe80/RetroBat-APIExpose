using System.Globalization;
using System.Text;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot S2 du prototype Sonic : le simulateur du wrapper (regles de wrapper.cpp) et le banc sur les replays.
/// Le banc lui-meme ne tourne que si NELFE_BANC_SPEEDRUN donne le dossier des traces de la sonde libretro
/// (LibretroProbe --replay) et le .MEM certifie : sans eux, il ne fait rien.
/// </summary>
public class SpeedrunBancTests
{
    private const string Mem = """
        return {
          events = {
            flow = {
              lifecycle = {
                { address=0XF601, type="u8", condition="eq", value=0X0C, action="GAME_PLAYING", desc="Normal level" },
                { address=0XF601, type="u8", condition="eq", value=0X8C, action="GAME_PLAYING", desc="Pre-level" },
              },
            },
            resources = {
              lives = {
                { address=0XFE13, type="u8", condition="decrease", action="LOSE_LIFE", desc="Lives decreased" },
                { address=0XD003, type="u8", condition="bit_true", mask=0X80, action="LOSE_LIFE", desc="Sonic dead" },
              },
            },
            scoring = {
              collectibles = {
                { address=0XFE20, type="u16le", condition="increase", action="COIN_GAIN", desc="Rings" },
              },
            },
            system = {
              timer = {
                { address=0XFE1F, type="u8", condition="change", action="GENERAL_TIMER", desc="Time active" },
                { address=0XFE25, type="u8", condition="change", action="LEVEL_TIMER", desc="Timer - seconds" },
                { address=0XF00A, type="u8", condition="eq", value=0XA0, action="JUMPING", no_log=true, desc="Jumping" },
              },
            },
          },
        }
        """;

    private static WrapperSimule Wrapper() => new(LignesDuMem.Lire(Mem));

    [Fact]
    public void Le_lecteur_du_mem_prend_le_type_le_masque_et_la_section_scoring()
    {
        var lignes = LignesDuMem.Lire(Mem).Lignes;
        var anneaux = lignes.Single(l => l.Adresse == 0xFE20);
        Assert.Equal("u16le", anneaux.Type);
        Assert.True(anneaux.Score);
        var mort = lignes.Single(l => l.Adresse == 0xD003);
        Assert.Equal(0x80, mort.Masque);
        Assert.False(mort.Score);
        Assert.False(lignes.Single(l => l.Adresse == 0xFE1F).Score);
    }

    [Fact]
    public void La_premiere_lecture_initialise_sans_rien_emettre()
    {
        var w = Wrapper();
        Assert.Empty(w.Lire(0, 0xF601, "u8", 0x0C));
        Assert.Empty(w.Lire(0, 0xFE1F, "u8", 1));
        Assert.Equal(0x0C, w.Valeur(0xF601));
    }

    [Fact]
    public void Eq_emet_a_l_entree_dans_la_valeur_et_change_a_chaque_changement()
    {
        var w = Wrapper();
        w.Lire(0, 0xF601, "u8", 0x04);
        w.Lire(0, 0xFE1F, "u8", 0);
        var preLevel = Assert.Single(w.Lire(10, 0xF601, "u8", 0x8C));
        Assert.Equal(("GAME_PLAYING", 0x8C, 10L), (preLevel.Action, preLevel.Valeur, preLevel.Frame));
        Assert.Empty(w.Lire(11, 0xF601, "u8", 0x8C));   // la valeur ne change pas : rien
        Assert.Empty(w.Lire(12, 0xF601, "u8", 0x14));   // aucune ligne pour 0x14
        var timer = Assert.Single(w.Lire(20, 0xFE1F, "u8", 1));
        Assert.Equal(("GENERAL_TIMER", 1), (timer.Action, timer.Valeur));
    }

    [Fact]
    public void Decrease_et_bit_true_suivent_le_sens_et_le_masque()
    {
        var w = Wrapper();
        w.Lire(0, 0xFE13, "u8", 3);
        w.Lire(0, 0xD003, "u8", 0x04);
        Assert.Empty(w.Lire(5, 0xFE13, "u8", 4));
        Assert.Single(w.Lire(6, 0xFE13, "u8", 3));
        Assert.Empty(w.Lire(7, 0xD003, "u8", 0x05));   // bit 0x80 toujours a zero
        var mort = Assert.Single(w.Lire(8, 0xD003, "u8", 0x85));
        Assert.Equal(0x80, mort.Valeur);               // la valeur emise est masquee
        Assert.Empty(w.Lire(9, 0xD003, "u8", 0x81));   // deja vrai : pas de nouvelle entree
    }

    [Fact]
    public void Huit_emissions_rapprochees_coupent_la_ligne_pour_la_session()
    {
        var w = Wrapper();
        w.Lire(0, 0xFE25, "u8", 0);
        var emises = 0;
        for (var i = 1; i <= 12; i++) emises += w.Lire(100 + i * 10, 0xFE25, "u8", i).Count;
        // La premiere emission vient apres 110 images (ecart non regulier), les sept suivantes comptent, la
        // huitieme reguliere coupe : 8 emissions, puis le silence.
        Assert.Equal(8, emises);
        var coupure = Assert.Single(w.Coupures);
        Assert.Equal((0xFE25L, "LEVEL_TIMER"), (coupure.Adresse, coupure.Action));
        Assert.Empty(w.Lire(1000, 0xFE25, "u8", 50));
    }

    [Fact]
    public void Une_ligne_de_score_n_est_jamais_coupee_et_une_ligne_no_log_jamais_emise()
    {
        var w = Wrapper();
        w.Lire(0, 0xFE20, "u16le", 0);
        var emises = 0;
        for (var i = 1; i <= 30; i++) emises += w.Lire(i * 2, 0xFE20, "u16le", i).Count;
        Assert.Equal(30, emises);
        Assert.Empty(w.Coupures);
        w.Lire(0, 0xF00A, "u8", 0);
        Assert.Empty(w.Lire(1, 0xF00A, "u8", 0xA0));
    }

    [Fact]
    public void La_trace_de_la_sonde_se_lit()
    {
        var t = TraceDeReplay.Lire(new[]
        {
            "# fps_du_coeur 59.92274340431231 etat_du_coeur 1036288",
            "0 0x00F601 u8 4",
            "909 0x00FE1F u8 1",
            "# point image 300 octets 11 lus 0",
            "# point image 600 octets 1815 lus 3 0xF601:0C>8C | 0xAC82:40>80",
            "# images 1068",
        });
        Assert.Equal(2, t.Lectures.Count);
        Assert.Equal(new TraceDeReplay.Lecture(909, 0xFE1F, "u8", 1), t.Lectures[1]);
        Assert.Equal(new[] { (300L, 0), (600L, 3) }, t.Points);
        Assert.Equal(1068, t.Images);
        Assert.Equal(59.92274340431231, t.CadenceDuCoeur);
    }

    [Fact]
    public void Le_banc_mesure_green_hill_1_sur_une_trace_construite()
    {
        // Les frames du 20/10 : timer a 909, main au joueur a 934, timer fige a 3754, fin d'acte a 3878.
        var lignes = new[]
        {
            "0 0x00F601 u8 4", "0 0x00FE10 u8 0", "0 0x00FE11 u8 0", "0 0x00FE13 u8 3", "0 0x00FE1F u8 0",
            "0 0x00FE22 u8 0", "0 0x00FE25 u8 0", "0 0x00F7CD u8 0", "0 0x00F63A u8 0", "0 0x00FFE1 u8 0",
            "900 0x00F601 u8 140", "909 0x00FE1F u8 1", "934 0x00F601 u8 12",
            "3754 0x00FE1F u8 0", "3754 0x00FE25 u8 47", "3878 0x00F7CD u8 1",
            "# point image 3000 octets 0 lus 0", "# images 4000",
        };
        var mem = File.ReadAllText(MemCertifie() ?? throw new InvalidOperationException("Data Pack absent"));
        var defis = ReglementsDesNiveaux().Where(d => d.Nom == "GHZ1").ToList();
        var p = Assert.Single(BancDesReplays.Mesurer(TraceDeReplay.Lire(lignes), LignesDuMem.Lire(mem), defis));
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(2845, p.Resultat.Cale?.Frames);
        Assert.Equal(2944, p.Resultat.Independant?.Frames);
        Assert.Equal("fidele", p.Fidelite);
    }

    /// <summary>
    /// LE BANC SUR LES REPLAYS. NELFE_BANC_SPEEDRUN = dossier des traces (*.txt) ; le rapport s'ecrit a cote
    /// (rapport-s2.md). Sans la variable, rien ne tourne.
    /// </summary>
    [Fact]
    public void Banc_des_replays_de_sonic()
    {
        var dossier = Environment.GetEnvironmentVariable("NELFE_BANC_SPEEDRUN");
        if (string.IsNullOrEmpty(dossier) || !Directory.Exists(dossier) || MemCertifie() is not { } chemin) return;

        var mem = LignesDuMem.Lire(File.ReadAllText(chemin));
        var defis = ReglementsDesNiveaux();
        var rapport = new StringBuilder();
        var tous = new List<(string Replay, PassageMesure P, double Cadence)>();
        rapport.AppendLine("# Banc S2 : Sonic the Hedgehog, replays rejoues hors ecran").AppendLine();
        foreach (var fichier in Directory.GetFiles(dossier, "rp_*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            var trace = TraceDeReplay.Lire(File.ReadLines(fichier));
            var cadence = trace.CadenceDuCoeur ?? 59.922743;
            var passages = BancDesReplays.Mesurer(trace, mem, defis);
            var nom = Path.GetFileNameWithoutExtension(fichier);
            rapport.AppendLine($"## {nom}").AppendLine();
            rapport.AppendLine($"{trace.Images} images, {trace.Points.Count} points de controle, {trace.Points.Count(x => x.Lus > 0)} en ecart sur une adresse du .MEM.").AppendLine();
            if (passages.Count == 0) { rapport.AppendLine("Aucun depart de niveau.").AppendLine(); continue; }
            rapport.AppendLine("| Niveau | Depart | Etat | Cause | Cale | Independant | Ecart | Temps du jeu | Controle | Fidelite |");
            rapport.AppendLine("|---|---:|---|---|---:|---:|---:|---:|---|---|");
            foreach (var p in passages)
            {
                var r = p.Resultat;
                string T(MesureChrono? m) => m?.Frames is { } f ? $"{Chronometrage.Afficher(f, cadence)} ({f})" : "";
                var ecart = r.Cale?.Frames is { } c && r.Independant?.Frames is { } i ? (i - c).ToString(CultureInfo.InvariantCulture) : "";
                var jeu = r.TempsDuJeuSecondes is { } s ? $"{s / 60}:{s % 60:00}" : "";
                rapport.AppendLine($"| {p.Defi} | {p.Depart} | {r.Etat} | {r.Cause} | {T(r.Cale)} | {T(r.Independant)} | {ecart} | {jeu} | {r.ControleTempsDuJeu} | {p.Fidelite} |");
                tous.Add((nom, p, cadence));
            }

            rapport.AppendLine();
        }

        rapport.AppendLine("## Synthese par niveau").AppendLine();
        rapport.AppendLine("| Niveau | Passages | Finis | Rates | Annules | Meilleur cale | Ecart independant - cale (min..max) | Controle ok |");
        rapport.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|");
        foreach (var d in defis)
        {
            var ps = tous.Where(x => x.P.Defi == d.Nom).ToList();
            if (ps.Count == 0) { rapport.AppendLine($"| {d.Nom} | 0 | | | | | | |"); continue; }
            var finis = ps.Where(x => x.P.Resultat.Etat == EtatTentative.Finie).ToList();
            var ecarts = finis.Where(x => x.P.Resultat.Cale?.Frames is not null && x.P.Resultat.Independant?.Frames is not null)
                .Select(x => x.P.Resultat.Independant!.Frames!.Value - x.P.Resultat.Cale!.Frames!.Value).ToList();
            var meilleur = finis.Where(x => x.P.Resultat.Cale?.Frames is not null).OrderBy(x => x.P.Resultat.Cale!.Frames).FirstOrDefault();
            rapport.AppendLine($"| {d.Nom} | {ps.Count} | {finis.Count} | {ps.Count(x => x.P.Resultat.Etat == EtatTentative.Ratee)} | {ps.Count(x => x.P.Resultat.Etat is EtatTentative.Annulee or EtatTentative.Abandonnee)} | "
                + $"{(meilleur.P is { } m ? Chronometrage.Afficher(m.Resultat.Cale!.Frames!.Value, meilleur.Cadence) : "")} | "
                + $"{(ecarts.Count > 0 ? $"{ecarts.Min()}..{ecarts.Max()}" : "")} | {finis.Count(x => x.P.Resultat.ControleTempsDuJeu == "ok")}/{finis.Count} |");
        }

        File.WriteAllText(Path.Combine(dossier, "rapport-s2.md"), rapport.ToString());
    }

    /// <summary>Le .MEM certifie de Sonic dans le Data Pack voisin, s'il est la.</summary>
    private static string? MemCertifie()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "RetroBat-DataPack", "ram", "megadrive", "sonic-the-hedgehog.MEM");
            if (File.Exists(c)) return c;
        }

        return null;
    }

    /// <summary>
    /// Les 19 niveaux de Sonic (§3 du document du prototype) : zone (0xFE11) et acte (0xFE10), sur le modele
    /// du reglement de Green Hill 1. Scrap Brain 3 est l'acte 3 du Labyrinth ; la Final Zone, l'acte 2 du
    /// Scrap Brain.
    /// </summary>
    internal static List<(string Nom, Reglement Reglement)> ReglementsDesNiveaux()
    {
        var niveaux = new (string Nom, int Zone, int Acte)[]
        {
            ("GHZ1", 0, 0), ("GHZ2", 0, 1), ("GHZ3", 0, 2), ("MZ1", 2, 0), ("MZ2", 2, 1), ("MZ3", 2, 2),
            ("SYZ1", 4, 0), ("SYZ2", 4, 1), ("SYZ3", 4, 2), ("LZ1", 1, 0), ("LZ2", 1, 1), ("LZ3", 1, 2),
            ("SLZ1", 3, 0), ("SLZ2", 3, 1), ("SLZ3", 3, 2), ("SBZ1", 5, 0), ("SBZ2", 5, 1), ("SBZ3", 1, 3), ("FZ", 5, 2),
        };
        return niveaux.Select(n => (n.Nom, Reglement.Lire(ReglementDeNiveau(n.Nom, n.Zone, n.Acte)))).ToList();
    }

    private static string ReglementDeNiveau(string nom, int zone, int acte) => $$"""
        {
          "schema": "nelfe.challenge.v2",
          "challenge_id": "sonic-the-hedgehog.{{nom.ToLowerInvariant()}}",
          "version": 1,
          "kind": "speedrun_level",
          "game": { "system_id": "megadrive", "rom_group": "sonic-the-hedgehog" },
          "start_policy": "natural_split",
          "start": [
            { "value": { "address": "0xFE11", "equals": "0x{{zone:X2}}" } },
            { "value": { "address": "0xFE10", "equals": "0x{{acte:X2}}" } }
          ],
          "timing": {
            "official": "game_timer_aligned",
            "game_timer_aligned": {
              "start": { "event": { "action": "GENERAL_TIMER", "address": "0xFE1F", "to": "0x01" },
                         "when": [ { "value": { "address": "0xF601", "equals": "0x8C" } },
                                   { "value": { "address": "0xFE22", "equals": "0x00" } },
                                   { "value": { "address": "0xFE25", "equals": "0x00" } } ] },
              "finish": { "event": { "action": "GENERAL_TIMER", "address": "0xFE1F", "to": "0x00" } }
            },
            "independent": {
              "start": { "event": { "action": "GAME_PLAYING", "address": "0xF601", "value": "0x0C", "leaves": "0x8C" } },
              "finish": { "any_of": [
                { "event": { "action": "LEVEL_CLEAR", "address": "0xF7CD", "value": "0x01" } },
                { "event": { "action": "GAME_PLAYING", "address": "0xF601", "value": "0x10" } } ] }
            },
            "game_time": [ { "address": "0xFE22", "unit": "minute" }, { "address": "0xFE25", "unit": "second" } ],
            "game_timer_period_frames": 60
          },
          "finish_confirm": {
            "any_of": [
              { "event": { "action": "LEVEL_CLEAR", "address": "0xF7CD", "value": "0x01" } },
              { "event": { "action": "GAME_PLAYING", "address": "0xF601", "value": "0x10" } },
              { "event": { "address": "0xFE10", "leaves": "0x{{acte:X2}}" } },
              { "event": { "address": "0xFE11", "leaves": "0x{{zone:X2}}" } } ],
            "cleared_by": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } } ]
          },
          "fail": [ { "event": { "action": "LOSE_LIFE", "address": "0xFE13" } } ],
          "void": ["save_state_load", "rewind", "runahead", "fast_forward", "cheats", "netplay",
                   { "event": { "action": "PAUSE_ON", "address": "0xF63A", "value": "0x01" } },
                   { "event": { "action": "NEW_LEVEL", "address": "0xFFE1" } },
                   { "event": { "action": "TITLE_SCREEN", "address": "0xF601", "value": "0x04" } },
                   { "event": { "action": "CORPORATE_SCREEN", "address": "0xF601", "value": "0x00" } },
                   { "event": { "address": "0xFE10", "leaves": "0x{{acte:X2}}" } },
                   { "event": { "address": "0xFE11", "leaves": "0x{{zone:X2}}" } }],
          "ranking": { "metric": "frames", "direction": "lower_better" },
          "cores": ["genesis_plus_gx"],
          "runtime": { "frame_rate": 59.922743 }
        }
        """;
}
