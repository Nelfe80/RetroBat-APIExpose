using System.Runtime.Versioning;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE SON D'ES PENDANT UNE PARTIE MULTIPLAYER (26/10) : coupe au lancement, rendu au premier appui apres la partie, jamais
/// une session que le joueur avait deja coupee ; une marque sur le disque le rend apres un redemarrage de l'API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SonDEmulationStationTests : IDisposable
{
    private readonly string _marque = Path.Combine(Path.GetTempPath(), "nelfeplay-son-" + Guid.NewGuid().ToString("N"), "es-son-coupe.json");
    private readonly SimpleEventBus _bus = new();

    public void Dispose()
    {
        var dossier = Path.GetDirectoryName(_marque)!;
        if (Directory.Exists(dossier))
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Une_partie_coupe_la_musique_et_les_videos_d_ES_mais_pas_ce_que_le_joueur_avait_coupe()
    {
        var melangeur = new FauxMelangeur(("musique", false), ("deja-coupee", true));
        using var son = new SonDEmulationStation(melangeur, _marque, TimeSpan.FromMilliseconds(10), _bus);

        son.Couper();

        Assert.True(melangeur.Muette("musique"));
        Assert.True(son.Coupe);
        Assert.True(File.Exists(_marque));
    }

    [Fact]
    public async Task Le_son_revient_au_premier_appui_apres_la_partie_jamais_pendant()
    {
        var melangeur = new FauxMelangeur(("musique", false), ("deja-coupee", true));
        using var son = new SonDEmulationStation(melangeur, _marque, TimeSpan.FromMilliseconds(10), _bus);
        son.Couper();

        // Pendant la partie, les appuis du joueur ne rendent rien.
        await Appuyer();
        await Task.Delay(100);
        Assert.True(melangeur.Muette("musique"));

        son.RendreAuProchainAppui();
        Assert.True(melangeur.Muette("musique"));
        await Appuyer();
        await Attendre(() => !melangeur.Muette("musique"));

        Assert.False(son.Coupe);
        Assert.True(melangeur.Muette("deja-coupee"));
        Assert.False(File.Exists(_marque));
    }

    [Fact]
    public async Task Une_revanche_avant_le_premier_appui_garde_ES_muet()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = new SonDEmulationStation(melangeur, _marque, TimeSpan.FromMilliseconds(10), _bus);
        son.Couper();
        son.RendreAuProchainAppui();
        son.Couper();

        await Appuyer();
        await Task.Delay(100);

        Assert.True(melangeur.Muette("musique"));
        Assert.True(son.Coupe);
    }

    [Fact]
    public void Une_partie_qui_ne_part_pas_rend_le_son_tout_de_suite()
    {
        var melangeur = new FauxMelangeur(("musique", false));
        using var son = new SonDEmulationStation(melangeur, _marque, TimeSpan.FromMilliseconds(10), _bus);
        son.Couper();

        son.Rendre();

        Assert.False(melangeur.Muette("musique"));
        Assert.False(File.Exists(_marque));
    }

    [Fact]
    public async Task Apres_un_redemarrage_en_pleine_partie_ES_retrouve_le_son()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_marque)!);
        File.WriteAllText(_marque, "{}");
        var melangeur = new FauxMelangeur(("musique", true));

        using var son = new SonDEmulationStation(melangeur, _marque, TimeSpan.FromMilliseconds(10), _bus);
        await Attendre(() => !melangeur.Muette("musique"));

        Assert.False(son.Coupe);
        Assert.False(File.Exists(_marque));
    }

    /// <summary>Le vrai melangeur, en lecture seule : outil de mise au point, hors des passes ordinaires.</summary>
    [Fact]
    public void Le_melangeur_de_Windows_lit_les_sessions_d_ES()
    {
        if (Environment.GetEnvironmentVariable("NELFEPLAY_MELANGEUR") is not { Length: > 0 } sortie)
        {
            return;
        }

        var sessions = new MelangeurDeWindows().De(SonDEmulationStation.Processus);
        File.WriteAllLines(sortie, sessions.Select(s => $"{s.Muette} {s.Id}"));
    }

    private Task Appuyer() => _bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed" });

    private static async Task Attendre(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class FauxMelangeur(params (string Id, bool Muette)[] sessions) : ISessionsDuMelangeur
    {
        private readonly Dictionary<string, bool> _sessions = sessions.ToDictionary(s => s.Id, s => s.Muette);

        public bool Muette(string id)
        {
            lock (_sessions)
            {
                return _sessions[id];
            }
        }

        public IReadOnlyList<SessionDuMelangeur> De(string processus)
        {
            lock (_sessions)
            {
                return _sessions.Select(s => new SessionDuMelangeur(s.Key, s.Value)).ToList();
            }
        }

        public void Muette(string processus, string id, bool muette)
        {
            lock (_sessions)
            {
                _sessions[id] = muette;
            }
        }
    }
}
