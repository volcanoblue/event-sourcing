using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace VolcanoBlue.EventSourcing;

/// <summary>
/// Callback invoked at the end of <see cref="EventStoreDbContext"/> model creation.
/// Register it in DI to map <see cref="Event"/> and <see cref="Snapshot"/> onto an existing schema.
/// </summary>
public delegate void EventStoreModelConfiguration(ModelBuilder modelBuilder);

/// <summary>
/// [INFRASTRUCTURE - PERSISTENCE] The single DbContext of the library: the append-only <see cref="Events"/> table and
/// the <see cref="Snapshots"/> table live in the same context, so <see cref="EventStore.SaveAsync{T}"/> writes the
/// events and the snapshot in one transaction. It only stores rows; serialization belongs to <see cref="EventStore"/>.
/// </summary>
public sealed class EventStoreDbContext : DbContext
{
    private readonly EventStoreModelConfiguration? _modelConfiguration;

    // EF Core itself is not trimming/AOT compatible (its own DbContext constructor says so); the library only passes that on.
    [RequiresUnreferencedCode("EF Core isn't fully compatible with trimming, and running the application may generate unexpected runtime failures.")]
    [RequiresDynamicCode("EF Core isn't fully compatible with NativeAOT, and running the application may generate unexpected runtime failures.")]
    public EventStoreDbContext(DbContextOptions<EventStoreDbContext> options,
                               EventStoreModelConfiguration? modelConfiguration = null) : base(options)
    {
        _modelConfiguration = modelConfiguration;

        // Rows are only added, replaced or removed explicitly, so scanning tracked entities for changes is wasted work.
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Snapshot> Snapshots => Set<Snapshot>();

    // Not EF.CompileAsyncQuery: compiled queries are bound to a single model, and the model here
    // can differ per configuration callback (database-first mappings).
    internal IAsyncEnumerable<Event> ReadStream(string streamId, long afterEventId) =>
        Events.AsNoTracking()
            .Where(e => e.StreamId == streamId && e.Id > afterEventId)
            .OrderBy(e => e.Id)
            .AsAsyncEnumerable();

    internal async Task<long> CurrentVersionAsync(string streamId, CancellationToken ct) =>
        await Events.AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .MaxAsync(e => (long?)e.Id, ct) ?? 0;

    internal Task<Snapshot?> LastSnapshotAsync(string entityId, CancellationToken ct) =>
        Snapshots.AsNoTracking()
            .Where(s => s.EntityId == entityId)
            .OrderByDescending(s => s.EntityVersion)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Stages <paramref name="replacement"/> as the only snapshot of its entity: a stored row with the same version is
    /// overwritten in place and every other row is removed. Nothing is written until the next save.
    /// </summary>
    internal async Task StageSnapshotAsync(Snapshot replacement, CancellationToken ct)
    {
        var stored = await Snapshots.AsNoTracking()
            .Where(s => s.EntityId == replacement.EntityId)
            .ToListAsync(ct);

        var replaced = false;
        foreach (var row in stored)
        {
            if (row.EntityVersion == replacement.EntityVersion)
            {
                Update(replacement); // same key: overwrite instead of delete + insert
                replaced = true;
            }
            else
            {
                Remove(row);
            }
        }

        if (!replaced)
            Snapshots.Add(replacement);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Event>().HasKey(e => new { e.Id, e.StreamId });
        modelBuilder.Entity<Event>().HasIndex(e => new { e.StreamId, e.Id });
        modelBuilder.Entity<Snapshot>().HasKey(e => new { e.EntityId, e.EntityVersion });
        base.OnModelCreating(modelBuilder);

        _modelConfiguration?.Invoke(modelBuilder);
    }
}
