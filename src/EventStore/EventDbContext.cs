using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moonad;
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

        public async Task<ImmutableArray<IEvent>> ReadStreamAsync(string streamId, 
                                                                                  CancellationToken ct = default)
        {
            var rows = await Events.AsNoTracking()
                .Where(e => e.StreamId == streamId)
                .OrderBy(e => e.Id)
                .ToListAsync(ct);

            return Deserialize(rows);
        }

        public async Task<ImmutableArray<IEvent>> ReadStreamFromEventIdAsync(string streamId, long eventId, 
                                                                                             CancellationToken ct = default)
        {
            var rows = await Events.AsNoTracking()
                .Where(e => e.StreamId == streamId && e.Id > eventId)
                .OrderBy(e => e.Id)
                .ToListAsync(ct);

            return Deserialize(rows);
        }

        public async Task AppendToStreamAsync(string streamId, IEnumerable<IEvent> events, CancellationToken ct = default)
        {
            var eventsToAppend = events.Select(e => EventSerializer.Serialize(streamId, e));
            await Events.AddRangeAsync(eventsToAppend, ct);
        }

        public async Task<int> SaveStreamAsync(CancellationToken ct = default)
        {
            var saved = await SaveChangesAsync(ct);
            ChangeTracker.Clear(); // appended rows are never read back through this context
            return saved;
        }

        private static ImmutableArray<IEvent> Deserialize(List<Event> rows)
        {
            if (rows.Count == 0)
                return ImmutableArray<IEvent>.Empty;

            var builder = ImmutableArray.CreateBuilder<IEvent>(rows.Count);
            foreach (var row in rows)
                builder.Add(EventSerializer.Deserialize(row));
            return builder.ToImmutable();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Event>().HasKey(e => new { e.Id, e.StreamId });
            // Reads filter by stream first and order by id; the primary key alone can't serve that.
            modelBuilder.Entity<Event>().HasIndex(e => new { e.StreamId, e.Id });
            base.OnModelCreating(modelBuilder);

            _modelConfigAction?.Invoke(modelBuilder);
        }
    }
}
