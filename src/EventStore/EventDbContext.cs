using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.EventStore.Serialization;

namespace VolcanoBlue.EventSourcing.EventStore
{
    /// <summary>
    /// [INFRASTRUCTURE - EVENT STORE] Entity Framework DbContext for event persistence.
    /// Architectural Role: Implements append-only event store using Entity Framework Core.
    /// Provides operations to read event streams and append new events to streams.
    /// Each stream is identified by a streamId (typically aggregate ID prefixed with type).
    /// Supports reading full streams or from a specific event version for incremental updates.
    /// Returns immutable arrays to prevent accidental modifications to event history.
    /// </summary>
    public sealed class EventDbContext : DbContext
    {
        private readonly EventModelConfiguration? _modelConfigAction;

        public EventDbContext(DbContextOptions<EventDbContext> options,
                              EventModelConfiguration? modelConfigAction = null) : base(options)
        {
            _modelConfigAction = modelConfigAction;

            // Events are only ever added, so scanning tracked entities for changes is wasted work.
            ChangeTracker.AutoDetectChangesEnabled = false;
        }

        private DbSet<Event> Events { get; set; }

        // Not EF.CompileAsyncQuery: compiled queries are bound to a single model, and the model here
        // can differ per configuration callback (database-first mappings).
        public Task<ImmutableArray<IEvent>> ReadStreamAsync(string streamId,
            CancellationToken ct = default) =>
            DeserializeAsync(Events.AsNoTracking()
                .Where(e => e.StreamId == streamId)
                .OrderBy(e => e.Id)
                .AsAsyncEnumerable(), ct);

        public Task<ImmutableArray<IEvent>> ReadStreamFromEventIdAsync(string streamId, long eventId,
            CancellationToken ct = default) =>
            DeserializeAsync(Events.AsNoTracking()
                .Where(e => e.StreamId == streamId && e.Id > eventId)
                .OrderBy(e => e.Id)
                .AsAsyncEnumerable(), ct);

        public Task AppendToStreamAsync(string streamId, IEnumerable<IEvent> events, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Nothing here is truly asynchronous (no async value generators), so add synchronously
            // with a single timestamp for the whole batch.
            var occurredAt = EventSerializer.Timestamp();
            foreach (var @event in events)
                Events.Add(EventSerializer.Serialize(streamId, @event, occurredAt));

            return Task.CompletedTask;
        }

        public async Task<int> SaveStreamAsync(CancellationToken ct = default)
        {
            var saved = await SaveChangesAsync(ct);
            ChangeTracker.Clear();
            return saved;
        }

        // Streams rows straight into the result, without an intermediate list of rows.
        private static async Task<ImmutableArray<IEvent>> DeserializeAsync(IAsyncEnumerable<Event> rows,
            CancellationToken ct)
        {
            var builder = ImmutableArray.CreateBuilder<IEvent>();
            await foreach (var row in rows.WithCancellation(ct))
                builder.Add(EventSerializer.Deserialize(row));

            return builder.DrainToImmutable();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Event>().HasKey(e => new { e.Id, e.StreamId });
            modelBuilder.Entity<Event>().HasIndex(e => new { e.StreamId, e.Id });
            base.OnModelCreating(modelBuilder);

            _modelConfigAction?.Invoke(modelBuilder);
        }
    }
}
