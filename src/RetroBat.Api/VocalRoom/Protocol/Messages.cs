// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
using System.Text.Json.Nodes;

namespace NelfePlay.VocalRoom.Protocol;

/// <summary>
/// Les trois formes de message du protocole v1.
/// <code>
/// commande  {"v":1,"request_id":"c-0042","command":"session.attach","payload":{...}}
/// reponse   {"v":1,"request_id":"c-0042","ok":true,"result":{...}}
///           {"v":1,"request_id":"c-0042","ok":false,"error":{"code":"stale_generation","message":"..."}}
/// evenement {"v":1,"event":"voice.state","seq":7,"payload":{...}}
/// </code>
/// </summary>
public static class Messages
{
    public const int MaxRequestIdLength = 64;

    public static JsonObject Command(string requestId, string command, JsonObject? payload = null) => new()
    {
        ["v"] = ProtocolInfo.Version,
        ["request_id"] = requestId,
        ["command"] = command,
        ["payload"] = payload ?? new JsonObject(),
    };

    public static JsonObject Ok(string? requestId, JsonObject? result = null) => new()
    {
        ["v"] = ProtocolInfo.Version,
        ["request_id"] = requestId,
        ["ok"] = true,
        ["result"] = result ?? new JsonObject(),
    };

    public static JsonObject Error(string? requestId, string code, string message) => new()
    {
        ["v"] = ProtocolInfo.Version,
        ["request_id"] = requestId,
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public static JsonObject Event(string name, long seq, JsonObject? payload = null) => new()
    {
        ["v"] = ProtocolInfo.Version,
        ["event"] = name,
        ["seq"] = seq,
        ["payload"] = payload ?? new JsonObject(),
    };

    public static bool IsResponse(JsonObject m) => m.ContainsKey("ok");
    public static bool IsEvent(JsonObject m) => m.ContainsKey("event");
    public static bool IsCommand(JsonObject m) => m.ContainsKey("command");

    public static string? Str(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static long? Long(JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= long.MinValue and <= long.MaxValue) return (long)d;
        return null;
    }

    public static bool? Bool(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public static JsonObject? Obj(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) ? n as JsonObject : null;

    /// <summary>Un <c>request_id</c> acceptable : 1 a 64 caracteres imprimables ASCII.</summary>
    public static bool IsValidRequestId(string? id) =>
        id is { Length: > 0 and <= MaxRequestIdLength } && id.All(c => c is > ' ' and < (char)127);

    /// <summary><c>room_id</c> NelfePlay : 32 caracteres hexadecimaux (comme les gamerooms du site).</summary>
    public static bool IsValidRoomId(string? roomId) => roomId is { Length: 32 } && roomId.All(Uri.IsHexDigit);

    /// <summary><c>lobby_id</c> Discord : un snowflake, entier non signe 64 bits non nul ecrit en decimal (chaine).</summary>
    public static bool TryParseLobbyId(string? lobbyId, out ulong value)
    {
        value = 0;
        return lobbyId is { Length: > 0 and <= 20 } && lobbyId.All(char.IsAsciiDigit) && ulong.TryParse(lobbyId, out value) && value != 0;
    }
}
