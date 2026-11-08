using System;
using System.Text.Json;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization
{
    internal sealed class SnapshotSerializer
    {
        public static Snapshot Serialize<T>(string snapshotId, T snapshot) where T : ISnapshot
        {
            return new(snapshotId,
                       snapshot.EntityVersion,
                       DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                       // Runtime type: T may be just ISnapshot (as when EventStore snapshots an entity), which would drop all state.
                       EntityStateSerializer.Handles(snapshot.GetType())
                           ? EntityStateSerializer.Serialize(snapshot)
                           : JsonSerializer.Serialize((object)snapshot, snapshot.GetType()));
        }

        public static T Deserialize<T>(Snapshot snapshot) =>
            EntityStateSerializer.Handles(typeof(T))
                ? EntityStateSerializer.Deserialize<T>(snapshot.Data, snapshot.EntityVersion)
                : JsonSerializer.Deserialize<T>(snapshot.Data)!;
    }
}
