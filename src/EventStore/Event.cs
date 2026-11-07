namespace VolcanoBlue.EventSourcing.EventStore
{
    /// <summary>
    /// [INFRASTRUCTURE - PERSISTENCE MODEL] Record for storing events in database.
    /// Architectural Role: Represents the physical storage structure of events in the event store.
    /// Maps domain events to database rows with serialized data.
    /// Id: Auto-incrementing sequence number for ordering.
    /// StreamId: Identifies which aggregate the event belongs to.
    /// OccurredAt: Timestamp when event was persisted (UTC).
    /// EventType: Fully qualified type name for deserialization.
    /// Data: JSON-serialized event data.
    /// Public so consumers can map it onto an existing schema via the model configuration callback of EventDbContext.
    /// </summary>
    public record Event(long Id, string StreamId, string OccurredAt, string EventType, string Data);
}
