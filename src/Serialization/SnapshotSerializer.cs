using System;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// [INFRASTRUCTURE - SERIALIZATION] Turns an entity into its snapshot row and back.
/// The entity is serialized by its instance fields (see <see cref="EntityStateSerializer"/>).
/// </summary>
internal sealed class SnapshotSerializer
{
    public Snapshot Serialize(string entityId, ISnapshot snapshot, string created)
    {
        if (snapshot is not EventSourcedEntity entity)
            throw new ArgumentException($"{snapshot.GetType().Name} implements ISnapshot but is not an EventSourcedEntity.", nameof(snapshot));

        return new Snapshot(entityId, snapshot.EntityVersion, created, EntityStateSerializer.Serialize(entity));
    }

    public T Deserialize<T>(Snapshot row) where T : EventSourcedEntity =>
        EntityStateSerializer.Deserialize<T>(row.Data, row.EntityVersion);
}
