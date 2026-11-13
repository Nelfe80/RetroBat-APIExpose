// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NelfePlay.VocalRoom.Protocol;

/// <summary>Trame refusee : trop grande, vide, tronquee ou qui n'est pas un objet JSON.</summary>
public sealed class FramingException(string message) : Exception(message);

/// <summary>
/// Trames du protocole : 4 octets de longueur (entier non signe, petit-boutiste) puis la charge, un objet JSON en UTF-8.
/// Une trame plus grande que <see cref="ProtocolInfo.MaxFrameBytes"/> est refusee AVANT d'etre lue : le lecteur ferme
/// alors la connexion (la suite du flux n'est plus fiable).
/// </summary>
public static class Framing
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Lit une trame. Rend null si le flux se termine proprement entre deux trames.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken ct, int maxBytes = ProtocolInfo.MaxFrameBytes)
    {
        var header = new byte[4];
        int got = await ReadAtMostAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new FramingException("en-tete de trame tronque");

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0) throw new FramingException("trame vide");
        if (length > (uint)maxBytes) throw new FramingException($"trame trop grande ({length} octets, maximum {maxBytes})");

        var body = new byte[length];
        got = await ReadAtMostAsync(stream, body, ct).ConfigureAwait(false);
        if (got < body.Length) throw new FramingException("trame tronquee");

        return Parse(body);
    }

    /// <summary>Ecrit une trame (en-tete et charge en une seule ecriture, puis vidage).</summary>
    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct, int maxBytes = ProtocolInfo.MaxFrameBytes)
    {
        byte[] frame = Encode(message, maxBytes);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>En-tete + charge, pret a ecrire.</summary>
    public static byte[] Encode(JsonObject message, int maxBytes = ProtocolInfo.MaxFrameBytes)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString(Compact));
        if (body.Length > maxBytes) throw new FramingException($"message trop grand ({body.Length} octets, maximum {maxBytes})");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Charge d'une trame → objet JSON (refuse tout ce qui n'est pas un objet).</summary>
    public static JsonObject Parse(ReadOnlySpan<byte> body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw new FramingException("charge JSON invalide");
        }
        return node as JsonObject ?? throw new FramingException("la charge n'est pas un objet JSON");
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
