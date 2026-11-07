using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moonad;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.EventStore.Serialization;

namespace VolcanoBlue.EventSourcing.EventStore
{
    public sealed class SnapshotDbContext(DbContextOptions<SnapshotDbContext> options,
                                          SnapshotModelConfiguration? modelConfigAction = null) : DbContext(options)
    {
        private DbSet<Snapshot> Snapshots { get; set; }

        public async Task<Option<TResult>> ByIdAsync<TResult>(string snapshotId, 
            CancellationToken cancellationToken = default)
            where TResult : notnull, ISnapshot
        {
            var snapshot = (await Snapshots.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.EntityId == snapshotId, cancellationToken))
                    .ToOption();
            if (snapshot)
                return SnapshotSerializer.Deserialize<TResult>(snapshot.Get());

            return Option.None<TResult>();
        }

        public async Task UpsertAsync<T>(string entityId, T snapshot, CancellationToken ct = default) 
            where T : ISnapshot
        {
            var storedSnapshot = (await Snapshots.AsNoTracking()
                                                 .FirstOrDefaultAsync(s => s.EntityId == entityId, ct))
                                                 .ToOption();
            if (storedSnapshot)
                Remove(storedSnapshot.Get());

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
