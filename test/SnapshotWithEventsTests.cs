using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Demonstrates snapshots and events working together: 10 events, a snapshot, then 10 more events.
/// Rebuilding state loads the snapshot and replays only the events after its version.
/// </summary>
public class SnapshotWithEventsTests : IDisposable
{
    private const string StreamId = "Test-1";
    private const string EntityId = "Test-1";

    private readonly SqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static TestEvent[] EventsInRange(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => new TestEvent(i, $"e{i}")).ToArray();

    private static string Apply(string state, IEvent e) =>
        state.Length == 0 ? ((TestEvent)e).Name : $"{state},{((TestEvent)e).Name}";

    private async Task AppendAsync(IEnumerable<IEvent> events)
    {
        using var store = _fixture.CreateEventStore();
        await store.AppendToStreamAsync(StreamId, events);
        await store.SaveStreamAsync();
    }

    private async Task SnapshotAsync(TestSnapshot snapshot)
    {
        using var store = _fixture.CreateSnapshotStore();
        await store.UpsertAsync(EntityId, snapshot);
        await store.SaveChangesAsync();
    }

    private async Task<(TestSnapshot State, int ReplayedEvents)> RebuildAsync()
    {
        TestSnapshot state = new(0, "");

        using (var snapshots = _fixture.CreateSnapshotStore())
        {
            var snapshot = await snapshots.ByIdAsync<TestSnapshot>(EntityId);
            if (snapshot)
                state = snapshot.Get();
        }

        using var events = _fixture.CreateEventStore();
        var result = await events.ReadStreamFromEventIdAsync(StreamId, state.EntityVersion);

        var replayed = result;
        foreach (var e in replayed)
            state = new TestSnapshot((int)e.EventId, Apply(state.State, e));

        return (state, replayed.Length);
    }

    [Fact]
    public async Task TenEvents_Snapshot_TenMoreEvents_RebuildsFromSnapshotAndReplaysOnlyNewEvents()
    {
        // Phase 1: 10 events, then snapshot the state at version 10.
        await AppendAsync(EventsInRange(1, 10));
        var (atTen, replayedFirst) = await RebuildAsync();

        Assert.Equal(10, replayedFirst);
        Assert.Equal(10, atTen.EntityVersion);
        await SnapshotAsync(atTen);

        // Phase 2: 10 more events after the snapshot.
        await AppendAsync(EventsInRange(11, 20));

        // The full stream holds all 20 events...
        using (var store = _fixture.CreateEventStore())
        {
            var all = await store.ReadStreamAsync(StreamId);
            Assert.Equal(20, all.Length);
        }

        // ...but rebuilding replays only the 10 events newer than the snapshot.
        var (current, replayed) = await RebuildAsync();

        Assert.Equal(10, replayed);
        Assert.Equal(20, current.EntityVersion);
        Assert.Equal(string.Join(",", Enumerable.Range(1, 20).Select(i => $"e{i}")), current.State);
    }

    [Fact]
    public async Task StateFromSnapshotPlusTail_EqualsStateFromFullReplay()
    {
        await AppendAsync(EventsInRange(1, 10));
        await SnapshotAsync(new TestSnapshot(10, string.Join(",", Enumerable.Range(1, 10).Select(i => $"e{i}"))));
        await AppendAsync(EventsInRange(11, 20));

        var (fromSnapshot, _) = await RebuildAsync();

        using var store = _fixture.CreateEventStore();
        var full = await store.ReadStreamAsync(StreamId);
        var fromScratch = full.Aggregate("", Apply);

        Assert.Equal(fromScratch, fromSnapshot.State);
    }

    [Fact]
    public async Task NewerSnapshot_ReplacesOlder_AndReducesReplay()
    {
        await AppendAsync(EventsInRange(1, 10));
        await SnapshotAsync(new TestSnapshot(10, string.Join(",", Enumerable.Range(1, 10).Select(i => $"e{i}"))));
        await AppendAsync(EventsInRange(11, 20));

        var (atTwenty, replayedBefore) = await RebuildAsync();
        Assert.Equal(10, replayedBefore);

        await SnapshotAsync(atTwenty);

        var (again, replayedAfter) = await RebuildAsync();
        Assert.Equal(0, replayedAfter);
        Assert.Equal(atTwenty, again);
    }
}
