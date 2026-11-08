using Microsoft.EntityFrameworkCore;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>Concurrency and atomicity against a real PostgreSQL server (several writers, each with its own context).</summary>
public sealed class ConcurrencyTests : IDisposable
{
    private readonly DatabaseFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    private static string NewStream() => $"Pg-{Guid.NewGuid():N}";

    private async Task<SnapshotEntity> SeedAsync(string streamId)
    {
        await using var db = fixture.CreateContext();
        var entity = new SnapshotEntity();
        entity.Rename("a");
        await fixture.CreateStore(db).SaveAsync(streamId, entity);
        return entity;
    }

    private async Task<SnapshotEntity> LoadAsync(string streamId)
    {
        await using var db = fixture.CreateContext();
        return (await fixture.CreateStore(db).LoadAsync<SnapshotEntity>(streamId))!;
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_WithSnapshot()
    {
        var streamId = NewStream();
        await using (var db = fixture.CreateContext())
        {
            var entity = new SnapshotEntity { AutoSnapshot = true };
            entity.Rename("a");
            entity.Rename("b");
            await fixture.CreateStore(db).SaveAsync(streamId, entity);
        }

        var loaded = await LoadAsync(streamId);

        Assert.Equal("a,b", loaded.State);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(0, loaded.ProcessedEvents); // came from the snapshot, no replay
    }

    [Fact]
    public async Task SaveAsync_FromAStaleVersion_ThrowsConcurrencyException()
    {
        var streamId = NewStream();
        await SeedAsync(streamId);

        var stale = new SnapshotEntity();
        stale.Rename("conflict");
        await using var db = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => fixture.CreateStore(db).SaveAsync(streamId, stale));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
        Assert.Equal("a", (await LoadAsync(streamId)).State);
    }

    [Fact]
    public async Task SaveAsync_TwoWritersRacing_ExactlyOneWins()
    {
        var streamId = NewStream();
        await SeedAsync(streamId);

        const int writers = 8;
        using var start = new Barrier(writers);

        var results = await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            var entity = await LoadAsync(streamId);
            entity.Rename($"w{i}");

            await using var db = fixture.CreateContext();
            var store = fixture.CreateStore(db);
            start.SignalAndWait();
            try
            {
                await store.SaveAsync(streamId, entity);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        })));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.IsType<ConcurrencyException>(r));

        var loaded = await LoadAsync(streamId);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(2, loaded.State.Split(',').Length);
    }

    [Fact]
    public async Task SaveAsync_WhenTheSnapshotCannotBeSerialized_StoresNothing()
    {
        var streamId = NewStream();
        var entity = new UnserializableSnapshotEntity();
        entity.Rename("x");

        await using var db = fixture.CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.CreateStore(db).SaveAsync(streamId, entity));

        await using var check = fixture.CreateContext();
        Assert.False(await check.Events.AnyAsync(e => e.StreamId == streamId));
        Assert.False(await check.Snapshots.AnyAsync(s => s.EntityId == streamId));
        Assert.Single(entity.UncommittedEvents);
    }

    [Fact]
    public async Task SaveAsync_AConflictWritesNeitherEventsNorSnapshot()
    {
        var streamId = NewStream();
        await SeedAsync(streamId);

        var stale = new SnapshotEntity { AutoSnapshot = true };
        stale.Rename("conflict");
        await using (var db = fixture.CreateContext())
            await Assert.ThrowsAsync<ConcurrencyException>(() => fixture.CreateStore(db).SaveAsync(streamId, stale));

        await using var check = fixture.CreateContext();
        Assert.Equal(1, await check.Events.CountAsync(e => e.StreamId == streamId));
        Assert.False(await check.Snapshots.AnyAsync(s => s.EntityId == streamId));
    }
}
