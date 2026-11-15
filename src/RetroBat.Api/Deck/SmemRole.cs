namespace RetroBat.Api.Deck;

/// <summary>
/// LE ROLE NORMALISE D'UN SIGNAL (.SMEM), demande des plugins le 15/11 : les identifiants ne sont pas homogenes
/// (<c>players.1.bombs</c>, <c>players.1.resources.bombs</c>, <c>p1.bombs</c>, <c>bombs</c>...). Le role se deduit de la fin
/// de l'identifiant, sans toucher aux .SMEM : <c>lives</c>, <c>bombs</c>, <c>ammo</c>, <c>health</c>, <c>power</c>,
/// <c>boss_hp</c>, <c>stage</c>. Un identifiant sans role connu (compteur propre au jeu, maximum, nombre utilise...) n'en a
/// pas : <c>null</c>. Additif : l'identifiant d'origine reste celui du .SMEM.
/// </summary>
public static class SmemRole
{
    public static string? Depuis(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var parts = id.Trim().ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var dernier = parts[^1];
        var avant = parts.Length >= 2 ? parts[^2] : null;

        // Un signal propre a un jeu (players.N.game.<jeu>.<nom>) n'a jamais de role commun.
        if (parts.Contains("game")) return null;
        // La vie et la phase d'un boss.
        if (avant == "boss" && dernier == "hp") return "boss_hp";
        if (parts[0] == "boss" || parts[0] == "enemy") return null;

        return dernier switch
        {
            "lives" or "life" or "stock" => "lives",
            "bombs" or "bomb" => "bombs",
            "ammo" or "bullets" or "rounds" => "ammo",
            "health" or "energy" or "hp" => "health",
            "level" when avant == "power" => "power",
            "power" => "power",
            "stage" or "level" when parts.Length == 1 => "stage",
            _ => null,
        };
    }
}
