using System;
using System.Threading;
using System.Threading.Tasks;
using Moonad;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing
{
    /// <summary>
    /// [INFRASTRUCTURE - REPOSITORY] Loads and saves event-sourced entities.
    /// Entities need a parameterless constructor, which may be non-public. When loading from a snapshot, the entity is
    /// the object restored by <see cref="SnapshotDbContext"/>, with <c>Version</c> set from the snapshot.
    /// All reflection runs once per type: factories and accessors are compiled and cached,
    /// so the hot path is plain delegate calls.
    /// </summary>
    public sealed class EventStore(EventDbContext events, SnapshotDbContext snapshots)
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
            var snapshotVersion = entity.Version - 1;
            var stream = await events.ReadStreamFromEventIdAsync(streamId, snapshotVersion, ct);
            entity.RehydrateFromSnapshot(snapshotVersion, stream);
            return entity.ToOption();
        }

        /// <summary>
        /// Appends the uncommitted events to <paramref name="streamId"/>, saves them and marks them as committed.
        /// When the entity implements <see cref="ISnapshot"/> and <c>ShouldTakeSnapshot()</c> returns true, the entity
        /// itself is persisted as the snapshot under <paramref name="streamId"/> after the events are saved. The two writes are
        /// separate: if the snapshot fails the events are already stored, and the next load simply replays from the previous
        /// snapshot. The snapshot's <c>EntityVersion</c> must equal the entity's last applied event (<c>Version - 1</c>),
        /// otherwise <see cref="InvalidOperationException"/> is thrown before anything is saved.
        /// </summary>
        public async Task SaveAsync<T>(string streamId, T entity, CancellationToken ct = default)
            where T : EventSourcedEntity
        {
            if (entity.UncommittedEvents.Count == 0)
                return;

            var snapshot = entity is ISnapshot candidate && candidate.ShouldTakeSnapshot() ? candidate : null;

            // EntityVersion must be the last applied event (Version - 1). Loading replays events with an id
            // greater than it, so an off-by-one would silently skip or duplicate an event. Checked before saving.
            if (snapshot is not null && snapshot.EntityVersion != entity.Version - 1)
            {
                throw new InvalidOperationException(
                    $"Snapshot of '{streamId}' has EntityVersion {snapshot.EntityVersion}, but the last applied event is {entity.Version - 1}. " +
                    "Nothing was saved.");
            }

            await events.AppendToStreamAsync(streamId, entity.UncommittedEvents, ct);
            await events.SaveStreamAsync(ct);
            entity.CommitEvents();

            if (snapshot is not null)
            {
                await snapshots.UpsertAsync(streamId, snapshot, ct);
                await snapshots.SaveChangesAsync(ct);
            }
        }
    }
}
