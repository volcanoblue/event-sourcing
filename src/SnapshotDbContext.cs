using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moonad;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing
{
    public sealed class SnapshotDbContext(DbContextOptions<SnapshotDbContext> options,
                                          SnapshotModelConfiguration? modelConfigAction = null) : DbContext(options)
    {
        private DbSet<Snapshot> Snapshots { get; set; }

        public async Task<Option<TResult>> ByIdAsync<TResult>(string entityId, 
            CancellationToken cancellationToken = default)
            where TResult : notnull, ISnapshot
        {
            var snapshot = (await Snapshots.AsNoTracking()
                    .Where(e => e.EntityId == entityId)
                    .OrderByDescending(e => e.EntityVersion)
                    .FirstOrDefaultAsync(cancellationToken))
                    .ToOption();
            if (snapshot)
                return SnapshotSerializer.Deserialize<TResult>(snapshot.Get());

            return Option.None<TResult>();
        }

        public async Task UpsertAsync<T>(string entityId, T snapshot, CancellationToken ct = default) 
            where T : ISnapshot
        {
            // Snapshots saved earlier by this same context instance are still tracked: reuse those entries
            // instead of attaching a second instance with the same key.
            var tracked = ChangeTracker.Entries<Snapshot>()
                                       .Where(e => e.Entity.EntityId == entityId)
                                       .ToList();
            foreach (var entry in tracked)
                entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Deleted;

            var stored = await Snapshots.AsNoTracking()
                                        .Where(s => s.EntityId == entityId)
                                        .ToListAsync(ct);
            foreach (var row in stored)
            {
                if (!tracked.Exists(e => e.Entity.EntityVersion == row.EntityVersion))
                    Remove(row);
            }

            await Snapshots.AddAsync(SnapshotSerializer.Serialize(entityId, snapshot), ct);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Snapshot>().HasKey(e => new { e.EntityId, e.EntityVersion });
            base.OnModelCreating(modelBuilder);

            modelConfigAction?.Invoke(modelBuilder);
        }
    }
}
