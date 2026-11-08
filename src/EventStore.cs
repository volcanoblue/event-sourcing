using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing;

/// <summary>
/// [INFRASTRUCTURE - REPOSITORY] Loads and saves event-sourced entities.
/// Entities need a parameterless constructor, which may be non-public. An entity that implements
/// <see cref="ISnapshot"/> is loaded from its last snapshot plus the later events; any other entity replays its stream.
/// All reflection runs once per type: factories and accessors are cached, so the hot path is plain delegate calls.
/// </summary>
public sealed class EventStore(EventStoreDbContext db, EventSourcingOptions options)
{
    private readonly SerializationRuntime _runtime = options.Runtime;

    /// <summary>
    /// Builds a <typeparamref name="T"/> from its stream; null when the stream is empty.
    /// When <typeparamref name="T"/> implements <see cref="ISnapshot"/> the last snapshot is the starting point and
    /// only the later events are replayed. For any other type the snapshot table is not even queried. A snapshot that
    /// cannot be read back (<see cref="JsonException"/>, e.g. after refactoring the entity) is ignored and the whole
    /// stream is replayed: the events are the source of truth.
    /// </summary>
    public async Task<T?> LoadAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(
        string streamId, CancellationToken ct = default)
        where T : EventSourcedEntity
    {
        T? entity = null;
        if (SnapshotSupport<T>.Enabled && await db.LastSnapshotAsync(streamId, ct) is { } row)
        {
            try
            {
                entity = _runtime.Snapshots.Deserialize<T>(row);
            }
            catch (JsonException)
            {
            }
        }

        // Events are applied as they are read: the stream is never materialized.
        await foreach (var stored in db.ReadStream(streamId, entity?.Version ?? 0).WithCancellation(ct))
        {
            entity ??= EntityFactory<T>.Create();
            entity.Replay(_runtime.Events.Deserialize(stored));
        }

        return entity;
    }

    /// <summary>
    /// Appends the uncommitted events to <paramref name="streamId"/>, saves them and marks them as committed.
    /// When the entity implements <see cref="ISnapshot"/> and <c>ShouldTakeSnapshot()</c> returns true, the entity
    /// itself is persisted as the snapshot under <paramref name="streamId"/>. Events and snapshot are written in a
    /// single transaction: both are stored or neither is. Everything is serialized before anything is written.
    /// When another writer got to the stream first, <see cref="ConcurrencyException"/> is thrown.
    /// The snapshot's <c>EntityVersion</c> must equal the entity's last applied event (<c>Version</c>),
    /// otherwise <see cref="InvalidOperationException"/> is thrown before anything is saved. The same happens when the
    /// uncommitted events do not have consecutive <c>EventId</c>s.
    /// </summary>
    public async Task SaveAsync<T>(string streamId, T entity, CancellationToken ct = default)
        where T : EventSourcedEntity
    {
        var uncommitted = entity.UncommittedEvents;
        if (uncommitted.Count == 0)
            return;

        var expectedVersion = entity.Version - uncommitted.Count;

        // Pending events must be contiguous (expectedVersion + 1, + 2, ...); RaiseEvent does not check it.
        for (var i = 0; i < uncommitted.Count; i++)
        {
            if (uncommitted[i].EventId != expectedVersion + 1 + i)
            {
                throw new InvalidOperationException(
                    $"Event {uncommitted[i].GetType().Name} of '{streamId}' has EventId {uncommitted[i].EventId}; " +
                    $"expected {expectedVersion + 1 + i}. Nothing was saved.");
            }
        }

        var snapshot = entity is ISnapshot candidate && candidate.ShouldTakeSnapshot() ? candidate : null;

        // EntityVersion must be the last applied event (Version). Loading replays events with an id
        // greater than it, so an off-by-one would silently skip or duplicate an event. Checked before saving.
        if (snapshot is not null && snapshot.EntityVersion != entity.Version)
        {
            throw new InvalidOperationException(
                $"Snapshot of '{streamId}' has EntityVersion {snapshot.EntityVersion}, but the last applied event is {entity.Version}. " +
                "Nothing was saved.");
        }

        try
        {
            var now = _runtime.Timestamp();
            for (var i = 0; i < uncommitted.Count; i++)
                db.Events.Add(_runtime.Events.Serialize(streamId, uncommitted[i], now));

            if (snapshot is not null)
                await db.StageSnapshotAsync(_runtime.Snapshots.Serialize(streamId, snapshot, now), ct);

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Tells a lost race apart from any other write failure: the stream moved past what we loaded.
            db.ChangeTracker.Clear();
            var actualVersion = await db.CurrentVersionAsync(streamId, ct);
            if (actualVersion > expectedVersion)
                throw new ConcurrencyException(streamId, expectedVersion, actualVersion, ex);

            throw;
        }
        finally
        {
            // Also on failure: otherwise a retry would re-send the rejected rows.
            db.ChangeTracker.Clear();
        }

        entity.CommitEvents();
    }

    private static class SnapshotSupport<T>
    {
        public static readonly bool Enabled = typeof(ISnapshot).IsAssignableFrom(typeof(T));
    }
}
