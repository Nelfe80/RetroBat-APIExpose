using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.VocalRoom;

/// <summary>Ce que le geste du vocal a change (Aucune : pas dans le salon).</summary>
public enum IssueDuGeste { Aucune, MicroOuvert, MicroCoupe, SonCoupe, SonRendu }

/// <summary>Le geste armé puis relâché : couper le micro ou le son du vocal.</summary>
public enum GesteDuVocal { Rien, Micro, Son }

/// <summary>
/// LES CALCULS PURS DU MICRO EN JEU (27/11, décision du user : « Y est une touche de jeu, ça mutait en permanence »).
/// EN JEU, Y seul ne touche jamais le vocal : il faut la HOTKEY (le Select de RetroBat) tenue au moment où Y est appuyée.
/// Court = micro, maintenu = son du vocal (casque). Hors jeu, le panneau MULTIPLAYER garde Y seul (inchangé).
/// </summary>
public static class MicroEnJeu
{
    /// <summary>L'appui de Y arme le geste seulement en jeu ET avec la HOTKEY tenue. Pur.</summary>
    public static bool Arme(bool enJeu, bool hotkeyTenue) => enJeu && hotkeyTenue;

    /// <summary>Le relâchement de Y : le geste armé, selon la durée d'appui (seuil = l'appui long du panneau). Pur.</summary>
    public static GesteDuVocal Relache(bool arme, double dureeMs, double seuilMs)
        => !arme ? GesteDuVocal.Rien : dureeMs >= seuilMs ? GesteDuVocal.Son : GesteDuVocal.Micro;

    /// <summary>La clé du texte (Data Pack, locales\interface-texts.json), son texte anglais de repli et la plaque du Deck. Pur.</summary>
    public static (string Cle, string Defaut, string Plaque)? Annonce(IssueDuGeste issue) => issue switch
    {
        IssueDuGeste.MicroOuvert => ("multiplayer.voice.mic_on", "MIC ON", "MIC ON"),
        IssueDuGeste.MicroCoupe => ("multiplayer.voice.mic_off", "MIC OFF", "MIC OFF"),
        IssueDuGeste.SonCoupe => ("multiplayer.voice.sound_off", "VOICE SOUND OFF", "SOUND OFF"),
        IssueDuGeste.SonRendu => ("multiplayer.voice.sound_on", "VOICE SOUND ON", "SOUND ON"),
        _ => null,
    };

    /// <summary>La plaque du Deck : rouge-orangé quand on coupe, vert quand on rouvre.</summary>
    public static DeckNotice Plaque(IssueDuGeste issue, string plaque)
        => new("voice." + issue.ToString().ToLowerInvariant(),
            issue is IssueDuGeste.MicroCoupe or IssueDuGeste.SonCoupe ? DeckSeverity.Warn : DeckSeverity.Good,
            plaque, "", null, DeckNotices.Court)
        { Source = "platform" };
}

/// <summary>
/// HOTKEY + Y EN JEU. Lit les mêmes boutons que le speedrun (<c>panel.input.*</c>, joueur 1, identités « select » = la
/// HOTKEY de RetroBat et « y »), sait si une partie est lancée par <c>ui.game.started</c> / <c>ui.game.ended</c>, commande le
/// vocal par <see cref="VocalRoomCoordinator.GesteEnJeuAsync"/> et annonce le résultat (Deck d'abord, sinon bandeau).
/// </summary>
public sealed class MicroEnJeuService : IHostedService, IDisposable
{
    private const string Hotkey = "select";
    private const string Touche = "y";

    private readonly IEventBus _bus;
    private readonly Func<bool, Task<IssueDuGeste>> _geste;
    private readonly InterfaceTextService? _textes;
    private readonly IEsSettingsStore? _es;
    private readonly ILogger<MicroEnJeuService>? _journal;
    private readonly Func<int> _seuilMs;
    private readonly object _verrou = new();
    private readonly HashSet<string> _tenus = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _abonnement;
    private bool _enJeu;
    private bool _arme;
    private DateTime _yDepuis;

    public MicroEnJeuService(IEventBus bus, VocalRoomCoordinator vocal, IOptionsMonitor<ApiExposeOptions> options,
        InterfaceTextService? textes = null, IEsSettingsStore? es = null, ILogger<MicroEnJeuService>? journal = null)
        : this(bus, vocal.GesteEnJeuAsync, () => Math.Max(200, options.CurrentValue.Leaderboard.LongPressMs), textes, es, journal)
    {
    }

    internal MicroEnJeuService(IEventBus bus, Func<bool, Task<IssueDuGeste>> geste, Func<int> seuilMs,
        InterfaceTextService? textes = null, IEsSettingsStore? es = null, ILogger<MicroEnJeuService>? journal = null)
    {
        _bus = bus;
        _geste = geste;
        _seuilMs = seuilMs;
        _textes = textes;
        _es = es;
        _journal = journal;
    }

    /// <summary>Couture des essais : l'annonce (issue, texte) ; sinon le bandeau de l'API.</summary>
    internal Action<IssueDuGeste, string>? Annonce { get; set; }

    public bool EnJeu { get { lock (_verrou) return _enJeu; } }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    internal void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    lock (_verrou) { _enJeu = true; _arme = false; _tenus.Clear(); }
                    break;
                case "ui.game.ended":
                    lock (_verrou) { _enJeu = false; _arme = false; _tenus.Clear(); }
                    break;
                case "panel.input.pressed":
                case "panel.input.released":
                    SurBouton(e.Type == "panel.input.pressed", e.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Micro en jeu : evenement ignore.");
        }
    }

    private void SurBouton(bool appui, object? payload)
    {
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (racine.TryGetProperty("Player", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() != 1) return;
        var identite = racine.TryGetProperty("Identity", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(identite)) return;

        double duree;
        bool arme;
        lock (_verrou)
        {
            if (appui) _tenus.Add(identite); else _tenus.Remove(identite);
            if (!identite.Equals(Touche, StringComparison.OrdinalIgnoreCase)) return;

            if (appui)
            {
                _arme = MicroEnJeu.Arme(_enJeu, _tenus.Contains(Hotkey));
                _yDepuis = DateTime.UtcNow;
                return;
            }

            duree = (DateTime.UtcNow - _yDepuis).TotalMilliseconds;
            arme = _arme;
            _arme = false;
        }

        var geste = MicroEnJeu.Relache(arme, duree, _seuilMs());
        if (geste != GesteDuVocal.Rien) _ = AgirAsync(geste == GesteDuVocal.Son);
    }

    internal async Task AgirAsync(bool maintenu)
    {
        try
        {
            var issue = await _geste(maintenu).ConfigureAwait(false);
            if (MicroEnJeu.Annonce(issue) is not { } a) return;
            var texte = _textes?.Text(a.Cle, Langue());
            if (string.IsNullOrWhiteSpace(texte) || texte == a.Cle) texte = a.Defaut;
            if (Annonce is { } essai) { essai(issue, texte); return; }
            NotificationsRetroArch.Bandeau?.ShowTop("VOCAL", texte, null, 2500, issue is IssueDuGeste.MicroCoupe or IssueDuGeste.SonCoupe,
                MicroEnJeu.Plaque(issue, a.Plaque));
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Micro en jeu : geste ignore.");
        }
    }

    private string Langue()
    {
        try
        {
            if (_es is not null && _es.ReadAllSettings().TryGetValue("Language", out var l) && !string.IsNullOrWhiteSpace(l)) return l;
        }
        catch (Exception)
        {
            // Des reglages illisibles : l'anglais.
        }

        return "en";
    }
}
