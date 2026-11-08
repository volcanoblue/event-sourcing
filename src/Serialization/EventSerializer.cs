using System.Text.Json;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// [INFRASTRUCTURE - SERIALIZATION] Converts between domain event objects and their stored rows.
/// </summary>
internal sealed class EventSerializer(EventTypeRegistry registry, JsonSerializerOptions readOptions, JsonSerializerOptions writeOptions)
{
    public Event Serialize(string streamId, IEvent @event, string occurredAt)
    {
        var type = @event.GetType();
        return new Event(@event.EventId,
                         streamId,
                         occurredAt,
                         registry.NameOf(type),
                         JsonSerializer.Serialize(@event, type, writeOptions));
    }

    public IEvent Deserialize(Event row)
    {
        var type = registry.GetType(row.EventType);
        return (IEvent)JsonSerializer.Deserialize(row.Data, type, readOptions)!;
    }
}
