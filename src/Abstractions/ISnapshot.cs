namespace VolcanoBlue.EventSourcing.Abstractions
{
    /// <summary>
    /// [INFRASTRUCTURE - SNAPSHOT CONTRACT] Interface for entity snapshots in Event Sourcing.
    /// Architectural Role: Defines contract for snapshot optimization pattern.
    /// Snapshots store materialized state at a point in time to avoid replaying all events.
    /// EntityVersion indicates which event version the snapshot represents.
    /// Used to improve performance when reconstructing aggregates with long event histories.
    /// </summary>
    /// <remarks>
    /// An entity can implement it to be its own snapshot. <see cref="ShouldTakeSnapshot"/> then tells the event store
    /// when to persist one after saving; the default is never, so plain data snapshots need not implement it.
    /// </remarks>
    public interface ISnapshot
    {
        int EntityVersion { get; }

        bool ShouldTakeSnapshot() => false;
    }
}
