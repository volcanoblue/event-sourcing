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
                       // Runtime type: T may be just ISnapshot (e.g. from a covariant ISnapshotable), which would drop all state.
                       JsonSerializer.Serialize((object)snapshot, snapshot.GetType()));
        }

        public static T Deserialize<T>(Snapshot snapshot) =>
            JsonSerializer.Deserialize<T>(snapshot.Data)!;
    }
}
