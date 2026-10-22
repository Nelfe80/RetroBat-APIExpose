using System.Text.Json;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE BILAN DE FIN DE PARTIE DU WRAPPER (<c>scoring.listener.session</c>) et ce qu'il dit d'une rencontre MULTIPLAYER
/// (decision du user du 22/10 : retour arriere et avance rapide interdits, rencontre annulee sur triche).
///
/// Le wrapper compte a part les chargements et les retours arriere du netplay lui-meme (contexte ROLLBACK_NETPLAY de
/// RetroArch : synchronisation de l'invite, corrections) ; le rattrapage du netplay ne passe pas par l'avance rapide.
/// Hors triche, un bilan MULTIPLAYER montre donc zero chargement chez l'invite, et chez l'hote un seul, celui du depart
/// pret quand le jeu en a un. Un chargement fait chez l'invite se propage a l'hote sans y etre compte : chaque borne juge
/// donc son propre bilan.
/// </summary>
public sealed class MultiplayerBilanDuWrapper : IDisposable
{
    private readonly IDisposable? _abonnement;
    private readonly object _verrou = new();
    private (DateTime Recu, JsonElement Bilan)? _dernier;

    public MultiplayerBilanDuWrapper(IEventBus? bus)
    {
        _abonnement = bus?.Subscribe<EventEnvelope>(Recevoir);
    }

    private void Recevoir(EventEnvelope e)
    {
        if (!string.Equals(e.Type, "scoring.listener.session", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var enveloppe = JsonSerializer.SerializeToElement(e.Payload);
            if (enveloppe.TryGetProperty("Session", out var session) && session.GetString() is { Length: > 0 } json)
            {
                using var document = JsonDocument.Parse(json);
                lock (_verrou)
                {
                    _dernier = (DateTime.UtcNow, document.RootElement.Clone());
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Le bilan recu depuis <paramref name="depuis"/>, attendu jusqu'a <paramref name="delai"/> ; null sans bilan.</summary>
    public async Task<JsonElement?> AttendreAsync(DateTime depuis, TimeSpan delai)
    {
        var limite = DateTime.UtcNow + delai;
        while (true)
        {
            lock (_verrou)
            {
                if (_dernier is { } d && d.Recu >= depuis)
                {
                    return d.Bilan;
                }
            }

            if (DateTime.UtcNow >= limite)
            {
                return null;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Le jugement d'un bilan : annulee, et pourquoi, si le joueur a triche, rembobine, accelere, couru en avance, ou
    /// charge plus d'etats que <paramref name="chargementsPermis"/> (le depart pret de l'hote).
    /// </summary>
    public static (bool Annulee, string? Motif) Juger(JsonElement bilan, int chargementsPermis)
    {
        int Compte(string nom) => bilan.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        if (Compte("cheats") > 0)
        {
            return (true, "cheats");
        }

        if (Compte("rewind") > 0)
        {
            return (true, "rewind");
        }

        if (Compte("fast_forward") > 0)
        {
            return (true, "fast_forward");
        }

        if (Compte("runahead") > 0)
        {
            return (true, "runahead");
        }

        return Compte("save_state_loads") > chargementsPermis ? (true, "save_state") : (false, null);
    }

    /// <summary>Les compteurs du bilan qui comptent, pour le journal.</summary>
    public static string Resume(JsonElement bilan)
    {
        string Valeur(string nom) => bilan.TryGetProperty(nom, out var v) ? v.ToString() : "?";
        return $"chargements {Valeur("save_state_loads")}, retour arriere {Valeur("rewind")}, avance rapide {Valeur("fast_forward")}, "
            + $"execution anticipee {Valeur("runahead")}, triche {Valeur("cheats")}, netplay {Valeur("netplay_rollbacks")} corrections";
    }

    public void Dispose() => _abonnement?.Dispose();
}
