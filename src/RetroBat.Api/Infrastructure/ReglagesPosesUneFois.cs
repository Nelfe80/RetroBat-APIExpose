using System.Xml.Linq;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES REGLAGES ALLUMES UNE SEULE FOIS sur les bornes existantes (decision du user, 18/11 nuit). Un nouveau defaut en C# ne
/// change rien chez un joueur dont l'appsettings.json porte deja la valeur (le synchroniseur y ecrit meme la valeur d'usine
/// d'un reglage qu'il a seulement parcouru) : la migration pose la valeur voulue, UNE fois, dans es_settings.cfg et dans
/// appsettings.json, puis marque la chose faite (<c>global.apiexpose.migrations.*</c>, jamais effacee) : le joueur qui recoupe
/// ensuite le reglage garde son choix. Seules des cles <c>global.apiexpose.*</c> : jamais une option native d'ES.
/// </summary>
public static class ReglagesPosesUneFois
{
    /// <summary>Une migration : la marque qui dit qu'elle a eu lieu, la cle posee et sa valeur d'ES (« 1 » ou « 0 »).</summary>
    public sealed record Migration(string Marqueur, string Cle, string Valeur);

    private const string Prefixe = "global.apiexpose.";

    /// <summary>Les migrations connues, dans l'ordre. Ajouter une ligne ne rejoue jamais les anciennes.</summary>
    public static readonly IReadOnlyList<Migration> Liste =
    [
        // Panel.state sur /ws : le panneau de MarqueeManager en depend (support du 18/11 : sans, il retombe sur l'aspect simple)
        new("global.apiexpose.migrations.cpo_websocket_push_allume_1118",
            "global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled", "1"),
    ];

    /// <summary>Celles qui n'ont pas encore eu lieu d'apres les reglages d'ES. Pur.</summary>
    public static IReadOnlyList<Migration> EnAttente(IReadOnlyDictionary<string, string> reglagesES)
        => [.. Liste.Where(m => !reglagesES.ContainsKey(m.Marqueur))];

    /// <summary>
    /// Pose les migrations en attente dans le document d'ES (la cle, puis la marque) ; rend celles qui ont eu lieu. Ne touche
    /// que des cles <c>global.apiexpose.*</c>. Pur sur le document.
    /// </summary>
    public static IReadOnlyList<Migration> Appliquer(XElement racine)
    {
        var faites = new List<Migration>();
        foreach (var m in Liste)
        {
            if (!m.Cle.StartsWith(Prefixe, StringComparison.OrdinalIgnoreCase) || !m.Marqueur.StartsWith(Prefixe, StringComparison.OrdinalIgnoreCase)) continue;
            if (Existe(racine, m.Marqueur)) continue;
            Poser(racine, m.Cle, m.Valeur);
            Poser(racine, m.Marqueur, "1");
            faites.Add(m);
        }

        return faites;
    }

    private static bool Existe(XElement racine, string cle)
        => racine.Elements().Any(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase));

    private static void Poser(XElement racine, string cle, string valeur)
    {
        var existant = racine.Elements().FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase));
        if (existant is not null)
        {
            existant.SetAttributeValue("value", valeur);
            return;
        }

        racine.Add(new XText(Environment.NewLine + "  "));
        racine.Add(new XElement("string", new XAttribute("name", cle), new XAttribute("value", valeur)));
    }
}
