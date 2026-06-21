using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/ingest/[controller]")]
public class EsController : ControllerBase
{
    private readonly IEventBus _eventBus;

    public EsController(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] EsEventPayload payload)
    {
        // Map EmulationStation event to internal event
        var @event = new EventEnvelope
        {
            Type = payload.EventName == "game-selected" ? "ui.game.selected" :
                   payload.EventName == "game-start" ? "ui.game.started" : 
                   payload.EventName == "game-end" ? "ui.game.ended" : "ui.event",
            Payload = payload
        };

        await _eventBus.PublishAsync(@event);
        return Accepted();
    }
}

public class EsEventPayload
{
    public string EventName { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
}
