using System;
using System.Text.Json;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.EventStore.Serialization
{
    /// <summary>
    /// [INFRASTRUCTURE - SERIALIZATION] Serializes and deserializes domain events for persistence.
    /// Architectural Role: Converts between domain event objects and storage format.
    /// </summary>
    internal static class EventSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new PrivateConstructorConverter() }
        };

        // Separate from Options: PrivateConstructorConverter.Write re-enters the serializer with the same options.
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            IncludeFields = true
        };

        public static Event Serialize(string streamId, IEvent @event) =>
            Serialize(streamId, @event, Timestamp());

        public static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        public static Event Serialize(string streamId, IEvent @event, string occurredAt)
        {
            var type = @event.GetType();

            Event serialized = new(@event.EventId,
                                   streamId,
                                   occurredAt,
                                   type.FullName!,
                                   JsonSerializer.Serialize(@event, type, WriteOptions));

            return serialized;
        }

        public static IEvent Deserialize(Event @event)
        {
            var type = EventTypesRegistry.GetType(@event.EventType);
            var deserialized = (IEvent)JsonSerializer.Deserialize(@event.Data, type, Options)!;

            return deserialized;
        }
    }
}
