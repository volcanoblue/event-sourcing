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
            // Snapshots saved earlier by this same context instance are still tracked; detach them so the
            // database rows read below can be attached without an identity conflict.
            foreach (var entry in ChangeTracker.Entries<Snapshot>().Where(e => e.Entity.EntityId == entityId).ToList())
                entry.State = EntityState.Detached;

            var replacement = SnapshotSerializer.Serialize(entityId, snapshot);
            var stored = await Snapshots.AsNoTracking()
                                        .Where(s => s.EntityId == entityId)
                                        .ToListAsync(ct);

            var replaced = false;
            foreach (var row in stored)
            {
                if (row.EntityVersion == replacement.EntityVersion)
                {
                    Update(replacement); // same key: overwrite in place instead of delete + insert
                    replaced = true;
                }
                else
                {
                    Remove(row);
                }
            }

            if (!replaced)
                await Snapshots.AddAsync(replacement, ct);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Snapshot>().HasKey(e => new { e.EntityId, e.EntityVersion });
            base.OnModelCreating(modelBuilder);

            modelConfigAction?.Invoke(modelBuilder);
        }
    }
}
