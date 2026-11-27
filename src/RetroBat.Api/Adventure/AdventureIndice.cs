using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Adventure;

/// <summary>
/// L'INDICE DE DEPART D'UNE AVENTURE (27/11, decision du user, Secret of Mana : les joueurs 2 et 3 doivent appuyer sur START quand leur
/// personnage rejoint l'equipe). Rien n'est code pour un jeu : le catalogue porte <c>adventure.start_hint</c>, une CLE de texte du Data Pack
/// (ex. <c>adventure.hint.press_start_to_join</c>), que l'API dit au depart, dans la langue de la borne (Deck d'abord, sinon bandeau).
/// </summary>
public sealed class AdventureIndice
{
    private readonly InterfaceTextService? _textes;
    private readonly IEsSettingsStore? _es;

    public AdventureIndice(InterfaceTextService? textes = null, IEsSettingsStore? es = null)
    {
        _textes = textes;
        _es = es;
    }

    /// <summary>Couture des essais : l'annonce (texte).</summary>
    internal Action<string>? Annonce { get; set; }

    /// <summary>« 2 » ou « 2 and 3 » : les places qui ne sont pas la 1re, pour {players}. Pur.</summary>
    internal static string Places(int joueurs, string et)
    {
        var autres = Enumerable.Range(2, Math.Max(0, joueurs - 1)).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return autres.Count <= 1 ? string.Concat(autres) : string.Join(", ", autres.Take(autres.Count - 1)) + " " + et + " " + autres[^1];
    }

    /// <summary>Le texte final : la cle (variante <c>_one</c> pour une seule place), {players} rempli ; null si la cle est vide. Pur.</summary>
    internal static string? Composer(string? cle, int joueurs, Func<string, string?, string> texte)
    {
        if (string.IsNullOrWhiteSpace(cle) || joueurs < 2) return null;
        var variante = joueurs == 2 ? texte(cle + "_one", string.Empty) : string.Empty;
        var modele = variante.Length > 0 ? variante : texte(cle, string.Empty);
        if (string.IsNullOrWhiteSpace(modele)) return null;
        return modele.Replace("{players}", Places(joueurs, texte("adventure.hint.and", "and")), StringComparison.Ordinal);
    }

    public void Dire(string? cle, int joueurs)
    {
        try
        {
            var langue = Langue();
            string Texte(string k, string? repli)
            {
                var t = _textes?.Text(k, langue);
                return string.IsNullOrWhiteSpace(t) || t == k ? repli ?? string.Empty : t;
            }

            if (Composer(cle, joueurs, Texte) is not { } message) return;
            if (Annonce is { } essai) { essai(message); return; }
            RetroBat.Api.Infrastructure.NotificationsRetroArch.Bandeau?.ShowTop("ADVENTURE", message, null, 9000, false,
                new RetroBat.Api.Deck.DeckNotice("adventure.hint", RetroBat.Api.Deck.DeckSeverity.Info, "PLAYERS 2+", "PRESS START WHEN YOU JOIN", null,
                    RetroBat.Api.Deck.DeckNotices.Long) { Source = "platform" });
        }
        catch (Exception)
        {
            // Un indice ne bloque jamais un depart.
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
            // L'anglais.
        }

        return "en";
    }
}
