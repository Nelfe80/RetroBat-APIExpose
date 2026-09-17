using System.Text.Json.Nodes;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Quel joueur est quelle manette. Le panel de la borne est TOUJOURS le joueur 1 (et le second
/// panel le joueur 2) : une manette du commerce branchee apres coup se range derriere, jamais
/// devant. Numeroter par l'ordre d'enumeration de SDL faisait passer le panel en joueur 2 des
/// qu'une manette Xbox arrivait (XInput vient en tete), et la marquee eclairait alors les
/// boutons d'un joueur 2 qui n'existe pas.
///
/// Deux sources, dans l'ordre :
///   1. les appareils EPINGLES dans appsettings (ApiExpose:PanelRemapExport:CabinetDevicesByPlayer,
///      « joueur » -> GUID SDL, compare sur ses 24 premiers caracteres, constructeur et produit) ;
///   2. sinon, les panels (tout ce qui n'est pas une manette du commerce) avant les manettes,
///      chaque groupe dans l'ordre de SDL.
/// Une borne sans panel garde sa manette en joueur 1 : on ne range derriere que ce qui a
/// quelque chose devant soi.
/// </summary>
public static class PlayerNumbering
{
    public sealed record Device(string Guid, string Name);

    /// <summary>Constructeurs de manettes du commerce (identifiant USB) : Microsoft, Sony, Nintendo, 8BitDo, Valve.</summary>
    private static readonly HashSet<string> CommercialVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "045e", "054c", "057e", "2dc8", "28de",
    };

    /// <summary>
    /// Une manette du commerce, a son GUID SDL : ouverte par XInput (« x » en 15e octet, ou
    /// « xinput » en hexadecimal), en Bluetooth (bus 05), ou d'un constructeur de manettes.
    /// Un encodeur d'arcade n'est rien de tout cela.
    /// </summary>
    public static bool LooksLikeGamepad(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return false;
        var g = guid.ToLowerInvariant();
        if (g.StartsWith("78696e707574", StringComparison.Ordinal)) return true;
        if (g.Length >= 30 && g.Substring(28, 2) == "78") return true;
        if (g.StartsWith("0500", StringComparison.Ordinal)) return true;
        return CommercialVendors.Contains(VendorOf(g));
    }

    /// <summary>Le constructeur USB d'un GUID SDL (mot de 16 bits, octets inverses) : « 5e04 » vaut 045e.</summary>
    public static string VendorOf(string guid)
        => guid.Length >= 12 ? guid.Substring(10, 2) + guid.Substring(8, 2) : "";

    /// <summary>Le numero de joueur de chaque appareil, dans l'ordre de la liste.</summary>
    public static int[] Assign(IReadOnlyList<Device> devices, IReadOnlyDictionary<int, string> pins)
    {
        var joueurs = new int[devices.Count];
        var pris = new HashSet<int>();

        // 1. Les epingles : un appareil dont le GUID commence comme celui epingle pour un joueur.
        foreach (var (joueur, guid) in pins.OrderBy(p => p.Key))
        {
            if (guid.Length < 24) continue;
            for (var i = 0; i < devices.Count; i++)
            {
                if (joueurs[i] != 0 || devices[i].Guid.Length < 24) continue;
                if (string.Equals(devices[i].Guid[..24], guid[..24], StringComparison.OrdinalIgnoreCase))
                {
                    joueurs[i] = joueur;
                    pris.Add(joueur);
                    break;
                }
            }
        }

        // 2. Les panels, puis les manettes, chaque groupe dans l'ordre de SDL.
        var prochain = 1;
        int Libre() { while (pris.Contains(prochain)) prochain++; pris.Add(prochain); return prochain; }
        for (var i = 0; i < devices.Count; i++)
        {
            if (joueurs[i] == 0 && !LooksLikeGamepad(devices[i].Guid)) joueurs[i] = Libre();
        }
        for (var i = 0; i < devices.Count; i++)
        {
            if (joueurs[i] == 0) joueurs[i] = Libre();
        }
        return joueurs;
    }

    /// <summary>Les epingles d'appsettings : « joueur » -> GUID (ou son prefixe de 24 caracteres).</summary>
    public static IReadOnlyDictionary<int, string> ReadPins()
    {
        var pins = new Dictionary<int, string>();
        try
        {
            var chemin = Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");
            if (!File.Exists(chemin)) return pins;
            var node = JsonNode.Parse(File.ReadAllText(chemin))?["ApiExpose"]?["PanelRemapExport"]?["CabinetDevicesByPlayer"] as JsonObject;
            if (node is null) return pins;
            foreach (var (joueur, valeur) in node)
            {
                if (!int.TryParse(joueur, out var n) || n < 1) continue;
                var guid = valeur switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject o when o["Guid"] is JsonValue g && g.TryGetValue<string>(out var s2) => s2,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(guid)) pins[n] = guid.Trim();
            }
        }
        catch
        {
            // reglages illisibles : pas d'epingle, la regle panels-devant s'applique seule
        }
        return pins;
    }

    /// <summary>Une ligne pour le journal : « 1 = Generic USB Joystick (panel), 2 = Xbox 360 Controller (manette) ».</summary>
    public static string Describe(IReadOnlyList<Device> devices, int[] joueurs)
        => string.Join(", ", devices.Select((d, i) => $"{joueurs[i]} = {d.Name} ({(LooksLikeGamepad(d.Guid) ? "manette" : "panel")})"));
}
