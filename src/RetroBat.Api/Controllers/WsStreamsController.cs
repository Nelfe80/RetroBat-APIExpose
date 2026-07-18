using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Hubs;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Discovery of the real-time WebSocket surface: which streams exist, which
/// event-type prefixes each one delivers, and which snapshots are replayed on
/// connect.
/// </summary>
[ApiController]
[Tags("Real-time (WebSocket)")]
[Route("api/v1/ws")]
public sealed class WsStreamsController : ControllerBase
{
    /// <summary>
    /// Lists the WebSocket streams: connect to ws://127.0.0.1:12345/ws for the
    /// unfiltered firehose or ws://127.0.0.1:12345/ws/{stream} for a filtered
    /// feed. Retained event types are replayed as soon as a client connects.
    /// </summary>
    [HttpGet("streams")]
    [ProducesResponseType(typeof(WsStreamsResponse), StatusCodes.Status200OK)]
    public ActionResult<WsStreamsResponse> GetStreams()
    {
        var streams = WebSocketConnectionManager.StreamPrefixes
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new WsStreamDescriptor
            {
                Name = pair.Key,
                Url = $"ws://127.0.0.1:12345/ws/{pair.Key}",
                EventTypePrefixes = pair.Value,
                Aliases = WebSocketConnectionManager.StreamAliases.TryGetValue(pair.Key, out var aliases)
                    ? aliases
                    : [],
                RetainedEventTypes = WebSocketConnectionManager.RetainedTypesByStream.TryGetValue(pair.Key, out var retained)
                    ? retained
                    : []
            })
            .ToArray();

        return Ok(new WsStreamsResponse
        {
            FirehoseUrl = "ws://127.0.0.1:12345/ws",
            FirehoseNote = "Delivers every event except esevent.* (raw EmulationStation hook traffic).",
            Streams = streams
        });
    }
}

public sealed class WsStreamsResponse
{
    /// <example>ws://127.0.0.1:12345/ws</example>
    public string FirehoseUrl { get; set; } = string.Empty;
    public string FirehoseNote { get; set; } = string.Empty;
    public IReadOnlyList<WsStreamDescriptor> Streams { get; set; } = [];
}

public sealed class WsStreamDescriptor
{
    /// <example>frontend</example>
    public string Name { get; set; } = string.Empty;
    /// <example>ws://127.0.0.1:12345/ws/frontend</example>
    public string Url { get; set; } = string.Empty;
    /// <summary>Only event types starting with one of these prefixes are delivered.</summary>
    public IReadOnlyList<string> EventTypePrefixes { get; set; } = [];
    public IReadOnlyList<string> Aliases { get; set; } = [];
    /// <summary>Last snapshot of these types is replayed on connect.</summary>
    public IReadOnlyList<string> RetainedEventTypes { get; set; } = [];
}
