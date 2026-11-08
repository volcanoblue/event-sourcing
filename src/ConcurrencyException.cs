using System;

namespace VolcanoBlue.EventSourcing;

/// <summary>
/// Thrown by <see cref="EventStore.SaveAsync{T}"/> when another writer appended to the stream first.
/// Reload the entity, reapply the command and save again.
/// </summary>
public sealed class ConcurrencyException(string streamId, long expectedVersion, long actualVersion, Exception inner)
    : Exception($"Stream '{streamId}' is at version {actualVersion}, but the entity expected {expectedVersion}.", inner)
{
    public string StreamId { get; } = streamId;

    /// <summary>Version of the stream the entity was loaded from (0 for a new stream).</summary>
    public long ExpectedVersion { get; } = expectedVersion;

    /// <summary>Version of the stream at the moment of the conflict.</summary>
    public long ActualVersion { get; } = actualVersion;
}
