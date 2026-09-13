using System.Text.Json;

namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Le garde-fou qui décide si une enveloppe de découverte peut partir.
///
/// Ce que la découverte a le droit de remonter, ce sont des nombres, des identités et des
/// empreintes : jamais une image, jamais un morceau d'image, jamais un chemin vers une image.
/// La règle ne peut pas tenir sur la seule discipline du code, parce qu'un champ ajouté dans
/// six mois à un objet anonyme ne réveillerait personne. Elle tient donc ici, sur la forme
/// sérialisée, juste avant l'envoi : une valeur qui ressemble à des octets fait échouer la
/// validation, et l'enveloppe ne part pas.
///
/// « Ressembler à des octets » se reconnaît à trois choses : un tableau de nombres entiers
/// assez long pour n'être pas une liste de scores, une chaîne qui est du base64 de taille
/// déraisonnable, et un nom de champ qui annonce une image. Les trois sont grossiers pris
/// séparément ; ensemble ils attrapent ce qui arrive en pratique, c'est-à-dire une capture
/// glissée dans un champ par commodité.
/// </summary>
public static class DiscoveryEnvelopeSchema
{
    /// <summary>Au-delà, un tableau de nombres n'est plus une trajectoire de score.</summary>
    public const int MaxNumericArrayLength = 4096;

    /// <summary>Au-delà, une chaîne base64 ne transporte plus une empreinte.</summary>
    public const int MaxBase64Length = 512;

    private static readonly string[] ForbiddenNameParts =
    {
        "pixel", "image", "bitmap", "screenshot", "frame", "thumbnail", "capture",
        "png", "jpg", "jpeg", "bmp", "crop", "shot"
    };

    /// <summary>
    /// Vérifie l'enveloppe déjà sérialisée. <paramref name="reason"/> nomme le premier champ
    /// fautif, pour que le refus soit compréhensible sans relire le code.
    /// </summary>
    public static bool IsAcceptable(string json, out string reason)
    {
        reason = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return Inspect(document.RootElement, "$", ref reason);
        }
        catch (JsonException ex)
        {
            reason = "JSON invalide : " + ex.Message;
            return false;
        }
    }

    private static bool Inspect(JsonElement element, string path, ref string reason)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path + "." + property.Name;
                    if (NameAnnouncesAnImage(property.Name) && !IsHarmlessValue(property.Value))
                    {
                        reason = $"{childPath} : un champ nommé ainsi ne peut pas porter de valeur";
                        return false;
                    }

                    if (!Inspect(property.Value, childPath, ref reason))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var index = 0;
                var numbers = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number)
                    {
                        numbers++;
                    }

                    if (!Inspect(item, $"{path}[{index}]", ref reason))
                    {
                        return false;
                    }

                    index++;
                }

                if (numbers > MaxNumericArrayLength)
                {
                    reason = $"{path} : {numbers} nombres, c'est un tampon, pas une mesure";
                    return false;
                }

                return true;

            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (text.Length > MaxBase64Length && LooksLikeBase64(text))
                {
                    reason = $"{path} : {text.Length} caractères de base64, c'est un fichier";
                    return false;
                }

                if (LooksLikeAnImagePath(text))
                {
                    reason = $"{path} : un chemin d'image ne se remonte pas non plus";
                    return false;
                }

                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Un champ dont le nom annonce une image peut exister s'il ne porte rien : <c>null</c>,
    /// <c>false</c> ou un compteur restent lisibles et ne transportent aucune image.
    /// </summary>
    private static bool IsHarmlessValue(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number;

    private static bool NameAnnouncesAnImage(string name)
    {
        var lowered = name.ToLowerInvariant();
        foreach (var part in ForbiddenNameParts)
        {
            if (lowered.Contains(part, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeBase64(string text)
    {
        var padding = 0;
        foreach (var c in text)
        {
            if (c == '=')
            {
                padding++;
                continue;
            }

            if (padding > 0)
            {
                return false;
            }

            var isBase64Character = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                                    || (c >= '0' && c <= '9') || c == '+' || c == '/';
            if (!isBase64Character)
            {
                return false;
            }
        }

        return padding <= 2;
    }

    private static bool LooksLikeAnImagePath(string text)
    {
        var lowered = text.ToLowerInvariant().TrimEnd();
        return lowered.EndsWith(".png", StringComparison.Ordinal)
               || lowered.EndsWith(".jpg", StringComparison.Ordinal)
               || lowered.EndsWith(".jpeg", StringComparison.Ordinal)
               || lowered.EndsWith(".bmp", StringComparison.Ordinal)
               || lowered.StartsWith("data:image/", StringComparison.Ordinal);
    }
}
