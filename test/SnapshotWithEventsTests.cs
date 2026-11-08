using Microsoft.EntityFrameworkCore;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Snapshots and events working together: 10 events, a snapshot, then 10 more events.
/// Loading restores the snapshot and replays only the events after its version.
/// </summary>
public class SnapshotWithEventsTests : IDisposable
{
    private const string StreamId = "Test-1";

    private readonly DatabaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static string Names(int from, int to) =>
        string.Join(",", Enumerable.Range(from, to - from + 1).Select(i => $"e{i}"));

    private static void Rename(SnapshotEntity entity, int from, int to)
    {
        for (var i = from; i <= to; i++)
            entity.Rename($"e{i}");
    }

    private async Task SaveAsync(SnapshotEntity entity, bool snapshot)
    {
        entity.AutoSnapshot = snapshot;
        using var db = _fixture.CreateContext();
        await _fixture.CreateStore(db).SaveAsync(StreamId, entity);
    }

    private async Task<SnapshotEntity> LoadAsync()
    {
        using var db = _fixture.CreateContext();
        return (await _fixture.CreateStore(db).LoadAsync<SnapshotEntity>(StreamId))!;
    }

    [Fact]
    public async Task TenEvents_Snapshot_TenMoreEvents_LoadsFromSnapshotAndReplaysOnlyNewEvents()
    {
        var entity = new SnapshotEntity();
        Rename(entity, 1, 10);
        await SaveAsync(entity, snapshot: true);

        Rename(entity, 11, 20);
        await SaveAsync(entity, snapshot: false);

        using (var db = _fixture.CreateContext())
            Assert.Equal(20, await db.Events.CountAsync());

        var loaded = await LoadAsync();

        Assert.Equal(10, loaded.ProcessedEvents);
        Assert.Equal(20, loaded.Version);
        Assert.Equal(Names(1, 20), loaded.State);
    }

    [Fact]
    public async Task NewerSnapshot_ReplacesOlder_AndReducesReplay()
    {
        var entity = new SnapshotEntity();
        Rename(entity, 1, 10);
        await SaveAsync(entity, snapshot: true);
        Rename(entity, 11, 20);
        await SaveAsync(entity, snapshot: false);
        Assert.Equal(10, (await LoadAsync()).ProcessedEvents);

        Rename(entity, 21, 21);
        await SaveAsync(entity, snapshot: true);

        var loaded = await LoadAsync();
        Assert.Equal(0, loaded.ProcessedEvents);
        Assert.Equal(21, loaded.Version);
        Assert.Equal(Names(1, 21), loaded.State);
    }
}
