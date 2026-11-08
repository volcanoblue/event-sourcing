using System.Threading;
using System.Threading.Tasks;
using Moonad;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.EventStore
{
    /// <summary>
    /// [INFRASTRUCTURE - REPOSITORY] Loads and saves event-sourced entities.
    /// Entities loaded from events need a parameterless constructor. When loading from a snapshot, the object
    /// deserialized by <see cref="SnapshotDbContext"/> is used directly if it already is a <c>T</c>; otherwise
    /// <c>T</c> needs a constructor taking the snapshot (<c>T(TSnapshot)</c>) that sets <c>Version</c> to the snapshot version.
    /// Constructors may be non-public. All reflection runs once per type: factories are compiled and cached,
    /// so the hot path is plain delegate calls.
    /// </summary>
    public sealed class EntityRepository(EventDbContext events, SnapshotDbContext snapshots)
    {
        /// <summary>Replays the whole stream into a new <typeparamref name="T"/>; None when the stream is empty.</summary>
        public async Task<Option<T>> LoadAsync<T>(string streamId, CancellationToken ct = default)
            where T : EventSourcedEntity
        {
            var stream = await events.ReadStreamAsync(streamId, ct);
            if (stream.IsEmpty)
                return Option.None<T>();

            var entity = EntityFactory<T>.Create();
            entity.Rehydrate(stream);
            return entity.ToOption();
        }

        /// <summary>
        /// Builds a <typeparamref name="T"/> from the last snapshot of <paramref name="streamId"/> and replays only the
        /// events after it. Without a snapshot the whole stream is replayed.
        /// <typeparamref name="T"/> must implement <see cref="ISnapshot"/>.
        /// </summary>
        public async Task<Option<T>> LoadFromSnapshotAsync<T>(string streamId,
            CancellationToken ct = default)
            where T : EventSourcedEntity, ISnapshot
        {
            var exists = await snapshots.ByIdAsync<T>(streamId, ct);
            if (!exists)
                return await LoadAsync<T>(streamId, ct);
            
            var entity = exists.Get();
            var stream = await events.ReadStreamFromEventIdAsync(streamId, entity.EntityVersion, ct);
            entity.RehydrateFromSnapshot(entity.EntityVersion, stream);
            return entity.ToOption();
        }

        /// <summary>Appends the uncommitted events to <paramref name="streamId"/>, saves them and marks them as committed.</summary>
        public async Task SaveAsync<T>(string streamId, T entity, CancellationToken ct = default)
            where T : EventSourcedEntity
        {
            if (entity.UncommittedEvents.Count == 0)
                return;

            await events.AppendToStreamAsync(streamId, entity.UncommittedEvents, ct);
            await events.SaveStreamAsync(ct);
            entity.CommitEvents();
        }
    }
}
