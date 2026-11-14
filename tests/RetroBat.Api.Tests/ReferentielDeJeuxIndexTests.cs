using System.Diagnostics;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, PERF-API-07 : les referentiels de jeux indexes. L'index doit rendre EXACTEMENT ce que rend le balayage,
/// ligne gagnante comprise. Mesure sur les vrais referentiels s'ils sont la (NELFEPLAY_REFERENTIELS, sinon ceux de
/// l'APIExpose voisin, en lecture seule).
/// </summary>
public class ReferentielDeJeuxIndexTests
{
    private readonly ITestOutputHelper _sortie;
    public ReferentielDeJeuxIndexTests(ITestOutputHelper sortie) => _sortie = sortie;

    private static readonly Func<JsonElement, string?>[] Extracteurs =
    {
        GamelistIdentity.PremierSha1, GamelistIdentity.Orientation, GamelistIdentity.Nom, r => GamelistIdentity.Marque(r),
    };

    private static string Ecrire(params string[] lignes)
    {
        var chemin = Path.Combine(Path.GetTempPath(), "nelfe-referentiel-" + Guid.NewGuid().ToString("N") + "_lt.json");
        File.WriteAllText(chemin, string.Join("\n", lignes) + "\n");
        return chemin;
    }

    [Fact]
    public void Meme_resultat_que_le_balayage_sur_un_petit_referentiel()
    {
        var chemin = Ecrire(
            """{"n": "19XX: The War Against Destiny", "ori": "vertical", "id": "19xx", "set": "19xx", "grp": "19xx", "hsh": [{"sha1": "AAA"}], "aka": [{"set": "19xxj"}]}""",
            """{"n": "19XX (Japan)", "id": "19xxj", "set": "19xxj", "grp": "19xx", "hsh": [{"sha1": "BBB"}], "t": "hack"}""",
            "",
            """{"n": "Pokemon", "id": "poke", "set": "poke", "grp": "Pokémon Rouge", "flg": ["bootleg"]}""",
            """{"n": "Sans set", "grp": "Street Fighter II", "ori": "horizontal"}""",
            """{"n": "Doublon", "id": "19XXJ", "set": "autre", "grp": "autre"}""");
        try
        {
            var requetes = new (string Slug, string Set)[]
            {
                ("19xx", ""), ("", "19xx"), ("", "19xxj"), ("19xx", "19xxj"), ("19xx", "inconnu"), ("pok-mon-rouge", ""),
                ("street-fighter-ii", ""), ("street-fighter-ii", "sf2"), ("", "19XXJ"), ("autre", ""), ("absent", ""), ("", "absent"),
            };
            foreach (var (slug, set) in requetes)
            foreach (var extraire in Extracteurs)
            {
                Assert.Equal(GamelistIdentity.ChercherParBalayage(chemin, slug, set, extraire), GamelistIdentity.Chercher(chemin, slug, set, extraire));
            }

            Assert.Equal("bbb", GamelistIdentity.Chercher(chemin, "", "19xxj", GamelistIdentity.PremierSha1));
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Fact]
    public void Ligne_illisible_le_fichier_n_est_pas_indexe_et_le_balayage_decide()
    {
        var chemin = Ecrire(
            """{"n": "A", "set": "a", "grp": "a", "hsh": [{"sha1": "111"}]}""",
            """{"n": "casse", "set": 12, "grp": "b"}""",
            """{"n": "B", "set": "b", "grp": "b", "hsh": [{"sha1": "222"}]}""");
        try
        {
            Assert.Null(ReferentielDeJeuxIndex.Pour(chemin));
            Assert.Equal(GamelistIdentity.ChercherParBalayage(chemin, "", "b", GamelistIdentity.PremierSha1),
                GamelistIdentity.Chercher(chemin, "", "b", GamelistIdentity.PremierSha1));
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Fact]
    public void Fichier_modifie_index_refait()
    {
        var chemin = Ecrire("""{"n": "A", "set": "a", "grp": "a", "hsh": [{"sha1": "111"}]}""");
        try
        {
            Assert.Equal("111", GamelistIdentity.Chercher(chemin, "", "a", GamelistIdentity.PremierSha1));
            File.WriteAllText(chemin, """{"n": "A", "set": "a", "grp": "a", "hsh": [{"sha1": "999"}], "x": "plus long"}""" + "\n");
            File.SetLastWriteTimeUtc(chemin, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("999", GamelistIdentity.Chercher(chemin, "", "a", GamelistIdentity.PremierSha1));
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    private static string? Referentiels()
    {
        var env = Environment.GetEnvironmentVariable("NELFEPLAY_REFERENTIELS");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var voisin = Path.Combine(d.FullName, "RetroBat", "plugins", "APIExpose", "resources", "gamelist", "systems");
            if (Directory.Exists(voisin)) return voisin;
            voisin = Path.Combine(d.FullName, "plugins", "APIExpose", "resources", "gamelist", "systems");
            if (Directory.Exists(voisin)) return voisin;
        }

        return Directory.Exists(@"E:\RetroBat\plugins\APIExpose\resources\gamelist\systems") ? @"E:\RetroBat\plugins\APIExpose\resources\gamelist\systems" : null;
    }

    [Theory]
    [InlineData("fbneo")]
    [InlineData("mame")]
    [InlineData("arcade")]
    [InlineData("snes")]
    public void Meme_resultat_que_le_balayage_sur_le_vrai_referentiel_et_mesure(string systeme)
    {
        var dossier = Referentiels();
        var source = dossier is null ? null : Path.Combine(dossier, systeme + "_lt.json");
        if (source is null || !File.Exists(source)) { _sortie.WriteLine("Referentiel absent : test saute."); return; }

        // Copie : le fichier de la borne n'est que lu, et l'index du test ne partage rien avec un autre.
        var chemin = Path.Combine(Path.GetTempPath(), "nelfe-ref-" + Guid.NewGuid().ToString("N") + "_" + systeme + "_lt.json");
        File.Copy(source, chemin);
        try
        {
            var lignes = File.ReadLines(chemin).Where(l => l.Length > 0).ToList();
            var nombreDeLignes = lignes.Count;
            var aleatoire = new Random(1311);
            var requetes = new List<(string Slug, string Set)>();
            for (var i = 0; i < 15; i++)
            {
                using var doc = JsonDocument.Parse(lignes[aleatoire.Next(lignes.Count)]);
                var r = doc.RootElement;
                var set = r.TryGetProperty("set", out var s) ? s.GetString() ?? "" : "";
                var grp = r.TryGetProperty("grp", out var g) ? GamelistIdentity.Slugifier(g.GetString()) : "";
                requetes.Add(("", set));
                requetes.Add((grp, ""));
                requetes.Add((grp, set));
                requetes.Add((grp, set + "zz"));
            }

            requetes.Add(("jeu-qui-n-existe-pas", ""));
            requetes.Add(("", "inconnu_set"));

            lignes = null!;
            var memoireAvant = GC.GetTotalMemory(forceFullCollection: true);
            var chrono = Stopwatch.StartNew();
            var index = ReferentielDeJeuxIndex.Pour(chemin);
            var construction = chrono.Elapsed.TotalMilliseconds;
            var memoireIndex = (GC.GetTotalMemory(forceFullCollection: true) - memoireAvant) / 1048576.0;
            GC.KeepAlive(index);

            foreach (var (slug, set) in requetes)
            {
                Assert.Equal(GamelistIdentity.ChercherParBalayage(chemin, slug, set, GamelistIdentity.PremierSha1),
                    GamelistIdentity.Chercher(chemin, slug, set, GamelistIdentity.PremierSha1));
                Assert.Equal(GamelistIdentity.ChercherParBalayage(chemin, slug, set, GamelistIdentity.Nom),
                    GamelistIdentity.Chercher(chemin, slug, set, GamelistIdentity.Nom));
            }

            // Mesure : un jeu absent (le pire cas, tout le fichier balaye) et un jeu present, balayage contre index.
            double Mesurer(Func<string?> f)
            {
                var c = Stopwatch.StartNew();
                for (var i = 0; i < 5; i++) f();
                return c.Elapsed.TotalMilliseconds / 5;
            }

            var (presentSlug, presentSet) = requetes[0];
            var absentBalayage = Mesurer(() => GamelistIdentity.ChercherParBalayage(chemin, "jeu-qui-n-existe-pas", "", GamelistIdentity.PremierSha1));
            var absentIndex = Mesurer(() => GamelistIdentity.Chercher(chemin, "jeu-qui-n-existe-pas", "", GamelistIdentity.PremierSha1));
            var presentBalayage = Mesurer(() => GamelistIdentity.ChercherParBalayage(chemin, presentSlug, presentSet, GamelistIdentity.PremierSha1));
            var presentIndex = Mesurer(() => GamelistIdentity.Chercher(chemin, presentSlug, presentSet, GamelistIdentity.PremierSha1));
            _sortie.WriteLine($"{systeme}_lt.json ({new FileInfo(chemin).Length / 1048576.0:F1} Mo, {nombreDeLignes} lignes) : index construit en {construction:F0} ms, environ {memoireIndex:F1} Mo ; "
                              + $"jeu absent {absentBalayage:F1} -> {absentIndex:F2} ms ; jeu present {presentBalayage:F1} -> {presentIndex:F2} ms ; "
                              + $"{requetes.Count * 2} requetes identiques au balayage");
        }
        finally
        {
            File.Delete(chemin);
        }
    }
}
