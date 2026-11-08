namespace VolcanoBlue.EventSourcing
{
    /// <summary>
    /// [INFRASTRUCTURE - PERSISTENCE MODEL] Record for storing entity snapshots.
    /// Architectural Role: Represents the physical storage structure of snapshots in database.
    /// Snapshots are optimization technique to avoid replaying all events for long-lived aggregates.
    /// EntityId: Identifies which aggregate this snapshot belongs to.
    /// EntityVersion: Event version number at which this snapshot was taken.
    /// Created: Timestamp when snapshot was created (UTC).
    /// Data: JSON-serialized snapshot state.
    /// Public so consumers can map it onto an existing schema via the model configuration callback of SnapshotDbContext.
    /// </summary>
    public record Snapshot(string EntityId, long EntityVersion, string Created, string Data);
}
