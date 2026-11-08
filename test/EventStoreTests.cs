using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing;
using VolcanoBlue.EventSourcing.Extensions;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing.Tests;

public class EventStoreTests : IDisposable
{
    private const string StreamId = "Snap-1";

    private readonly SqliteFixture _fixture = new();
    private readonly EventDbContext _events;
    private readonly SnapshotDbContext _snapshots;
    private readonly EventStore _store;

    public EventStoreTests()
    {
        _events = _fixture.CreateEventStore();
        _snapshots = _fixture.CreateSnapshotStore();
        _store = new EventStore(_events, _snapshots);
    }

    public void Dispose()
    {
        _events.Dispose();
        _snapshots.Dispose();
        _fixture.Dispose();
    }

    private static string Names(int from, int to) =>
        string.Join(",", Enumerable.Range(from, to - from + 1).Select(i => $"e{i}"));

    private static SnapshotEntity Renamed(int from, int to, SnapshotEntity? entity = null)
    {
        entity ??= new SnapshotEntity();
        for (var i = from; i <= to; i++)
            entity.Rename($"e{i}");
        return entity;
    }

    [Fact]
    public async Task LoadAsync_UnknownStream_ReturnsNone()
    {
        var result = await _store.LoadAsync<SnapshotEntity>("Missing-1");

        Assert.False(result);
    }

    [Fact]
    public async Task SaveAsync_PersistsUncommittedEventsAndCommitsThem()
    {
        var entity = Renamed(1, 2);

        await _store.SaveAsync(StreamId, entity);

        Assert.Empty(entity.UncommittedEvents);
        using var reader = _fixture.CreateEventStore();
        var stored = await reader.ReadStreamAsync(StreamId);
        Assert.Equal(["e1", "e2"], stored.Cast<TestEvent>().Select(e => e.Name));
    }

    [Fact]
    public async Task SaveAsync_WithoutUncommittedEvents_DoesNothing()
    {
        await _store.SaveAsync(StreamId, new SnapshotEntity());

        using var reader = _fixture.CreateEventStore();
        Assert.Empty(await reader.ReadStreamAsync(StreamId));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsStateAndVersion_AndAllowsFurtherChanges()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(4, loaded.Version);

        loaded.Rename("e4");
        await _store.SaveAsync(StreamId, loaded);

        var reloaded = (await _store.LoadAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(Names(1, 4), reloaded.State);
        Assert.Equal(5, reloaded.Version);
    }

    [Fact]
    public async Task LoadAsync_SupportsNonPublicParameterlessConstructor()
    {
        var entity = PrivateCtorEntity.Create();
        entity.Rename("a");
        await _store.SaveAsync("Private-1", entity);

        var loaded = (await _store.LoadAsync<PrivateCtorEntity>("Private-1")).Get();

        Assert.Equal("a", loaded.State);
    }

    [Fact]
    public async Task LoadAsync_WithoutParameterlessConstructor_Throws()
    {
        var entity = new TestEntity("1");
        entity.Rename("a");
        await _store.SaveAsync(entity.StreamId, entity);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.LoadAsync<TestEntity>(entity.StreamId));
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_NoSnapshot_FallsBackToFullReplay()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));

        var loaded = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();

        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(4, loaded.Version);
        Assert.Equal(3, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_NoSnapshotAndNoEvents_ReturnsNone()
    {
        var result = await _store.LoadFromSnapshotAsync<SnapshotEntity>("Missing-1");

        Assert.False(result);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_RestoresSnapshotAndReplaysOnlyLaterEvents()
    {
        var entity = Renamed(1, 10);
        await _store.SaveAsync(StreamId, entity);

        await _snapshots.UpsertAsync(StreamId, entity);
        await _snapshots.SaveChangesAsync();

        Renamed(11, 20, entity);
        await _store.SaveAsync(StreamId, entity);

        var loaded = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();

        Assert.Equal(Names(1, 20), loaded.State);
        Assert.Equal(21, loaded.Version);
        Assert.Equal(10, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_SnapshotWithoutLaterEvents_SetsNextVersion()
    {
        var entity = Renamed(1, 5);
        await _store.SaveAsync(StreamId, entity);
        await _snapshots.UpsertAsync(StreamId, entity);
        await _snapshots.SaveChangesAsync();

        var loaded = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();

        Assert.Equal(Names(1, 5), loaded.State);
        Assert.Equal(6, loaded.Version);
        Assert.Equal(0, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task ByIdAsync_ReturnsTheHighestVersion()
    {
        const string Insert = "INSERT INTO Snapshots (EntityId, EntityVersion, Created, Data) VALUES ('S-1', {0}, '2026-01-01 00:00:00', {1})";
        await _snapshots.Database.ExecuteSqlRawAsync(Insert, 5, "{\"EntityVersion\":5,\"State\":\"old\"}");
        await _snapshots.Database.ExecuteSqlRawAsync(Insert, 9, "{\"EntityVersion\":9,\"State\":\"new\"}");

        var snapshot = (await _snapshots.ByIdAsync<TestSnapshot>("S-1")).Get();

        Assert.Equal(new TestSnapshot(9, "new"), snapshot);
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsPublicFields()
    {
        await _events.AppendToStreamAsync("Field-1", [new FieldEvent { EventId = 1, Note = "kept" }]);
        await _events.SaveStreamAsync();

        using var reader = _fixture.CreateEventStore();
        var stored = await reader.ReadStreamAsync("Field-1");

        Assert.Equal("kept", ((FieldEvent)stored[0]).Note);
    }

    [Fact]
    public void UncommittedEvents_IsAStableLiveView()
    {
        var entity = new SnapshotEntity();
        var view = entity.UncommittedEvents;

        entity.Rename("a");

        Assert.Same(view, entity.UncommittedEvents);
        Assert.Single(view);
    }

    [Fact]
    public void EventTypesRegistry_InitializeIsRepeatable()
    {
        EventTypesRegistry.Initialize(typeof(SqliteFixture).Assembly);
        EventTypesRegistry.Initialize(typeof(EventDbContext).Assembly, typeof(SqliteFixture).Assembly);

        Assert.Equal(typeof(TestEvent), EventTypesRegistry.GetType(typeof(TestEvent).FullName!));
    }

    [Fact]
    public async Task SaveAsync_TakesSnapshotWhenShouldTakeSnapshotIsTrue()
    {
        var entity = Renamed(1, 10);
        entity.AutoSnapshot = true;
        await _store.SaveAsync(StreamId, entity);

        Renamed(11, 12, entity);
        entity.AutoSnapshot = false;
        await _store.SaveAsync(StreamId, entity);

        using var snapshots = _fixture.CreateSnapshotStore();
        var snapshot = (await snapshots.ByIdAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(10, snapshot.EntityVersion);
        Assert.Equal(Names(1, 10), snapshot.State);

        var loaded = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(Names(1, 12), loaded.State);
        Assert.Equal(2, loaded.ProcessedEvents);
        Assert.Equal(13, loaded.Version);
    }

    [Fact]
    public async Task SaveAsync_DoesNotSnapshotWhenShouldTakeSnapshotIsFalse()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));

        using var snapshots = _fixture.CreateSnapshotStore();
        Assert.False(await snapshots.ByIdAsync<SnapshotEntity>(StreamId));
    }

    [Fact]
    public async Task SaveAsync_KeepsOnlyTheLatestSnapshot()
    {
        var entity = Renamed(1, 3);
        entity.AutoSnapshot = true;
        await _store.SaveAsync(StreamId, entity);
        await _store.SaveAsync(StreamId, Renamed(4, 6, entity));

        using var snapshots = _fixture.CreateSnapshotStore();
        Assert.Equal(6, (await snapshots.ByIdAsync<SnapshotEntity>(StreamId)).Get().EntityVersion);
    }

    [Fact]
    public async Task SaveAsync_SnapshotVersionIsTheLastAppliedEvent_AndVersionIsNextEventId()
    {
        var entity = Renamed(1, 4);
        entity.AutoSnapshot = true;
        Assert.Equal(5, entity.Version);

        await _store.SaveAsync(StreamId, entity);

        using var snapshots = _fixture.CreateSnapshotStore();
        Assert.Equal(4, (await snapshots.ByIdAsync<SnapshotEntity>(StreamId)).Get().EntityVersion);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_ThenRaiseAndSave_ContinuesTheSequenceWithoutConflicts()
    {
        var entity = Renamed(1, 5);
        entity.AutoSnapshot = true;
        await _store.SaveAsync(StreamId, entity);

        // Snapshot at 5, no tail: the next event must be 6.
        var loaded = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(6, loaded.Version);
        loaded.Rename("e6");
        Assert.Equal(6, loaded.UncommittedEvents.Single().EventId);
        await _store.SaveAsync(StreamId, loaded);

        // AutoSnapshot isn't serialized, so the snapshot stays at 5 and the tail grows to e6..e8.
        Renamed(7, 8, loaded);
        await _store.SaveAsync(StreamId, loaded);

        var reloaded = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(Names(1, 8), reloaded.State);
        Assert.Equal(9, reloaded.Version);
        Assert.Equal(3, reloaded.ProcessedEvents);

        reloaded.Rename("e9");
        Assert.Equal(9, reloaded.UncommittedEvents.Single().EventId);
        await _store.SaveAsync(StreamId, reloaded);

        using var reader = _fixture.CreateEventStore();
        Assert.Equal(9, (await reader.ReadStreamAsync(StreamId)).Length);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_RestoresEncapsulatedEntity_WithoutPublicConstructorOrSetters()
    {
        var entity = EncapsulatedEntity.Create();
        entity.Rename("a");
        entity.Rename("b");
        await _store.SaveAsync(StreamId, entity);

        var loaded = (await _store.LoadFromSnapshotAsync<EncapsulatedEntity>(StreamId)).Get();

        Assert.Equal(["a", "b"], loaded.Names);
        Assert.Equal(2, loaded.Renames);
        Assert.Equal("b", loaded.Last);
        Assert.Equal(3, loaded.Version);
        Assert.Equal(7, loaded.Skipped); // [JsonIgnore] state is not persisted

        loaded.Rename("c");
        await _store.SaveAsync(StreamId, loaded);

        var again = (await _store.LoadFromSnapshotAsync<EncapsulatedEntity>(StreamId)).Get();
        Assert.Equal(["a", "b", "c"], again.Names);
        Assert.Equal(4, again.Version);
    }

    [Fact]
    public void RaiseEvent_WhenProcessEventThrows_KeepsVersionAndQueueUntouched()
    {
        var entity = new RejectingEntity();
        entity.Rename("ok");

        Assert.Throws<InvalidOperationException>(() => entity.Rename("bad"));

        Assert.Equal(2, entity.Version);
        Assert.Single(entity.UncommittedEvents);
    }

    [Fact]
    public async Task SaveAsync_AfterAConcurrencyConflict_DoesNotResendTheRejectedEvents()
    {
        var first = new SnapshotEntity();
        first.Rename("a");
        await _store.SaveAsync(StreamId, first);

        var stale = new SnapshotEntity();
        stale.Rename("conflict");
        await Assert.ThrowsAsync<DbUpdateException>(() => _store.SaveAsync(StreamId, stale));

        var current = (await _store.LoadAsync<SnapshotEntity>(StreamId)).Get();
        current.Rename("b");
        await _store.SaveAsync(StreamId, current);

        Assert.Equal(2, (await _events.ReadStreamAsync(StreamId)).Length);
    }

    [Fact]
    public async Task UpsertAsync_WithTheSameVersionAsTheStoredSnapshot_ReplacesIt()
    {
        var entity = Renamed(1, 3);
        await _snapshots.UpsertAsync(StreamId, entity);
        await _snapshots.SaveChangesAsync();

        using var other = _fixture.CreateSnapshotStore();
        var sameVersion = new SnapshotEntity();
        for (var i = 0; i < 3; i++)
            sameVersion.Rename("x");
        await other.UpsertAsync(StreamId, sameVersion);
        await other.SaveChangesAsync();

        using var reader = _fixture.CreateSnapshotStore();
        Assert.Equal(1, await reader.Set<Snapshot>().CountAsync(s => s.EntityId == StreamId));
        Assert.Equal("x,x,x", (await reader.ByIdAsync<SnapshotEntity>(StreamId)).Get().State);
    }

    [Fact]
    public async Task SaveAsync_SnapshotWithWrongEntityVersion_ThrowsBeforeSavingAnything()
    {
        var entity = new WrongVersionSnapshotEntity();
        entity.Rename("a");
        entity.Rename("b");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync("Wrong-1", entity));

        Assert.Equal(2, entity.UncommittedEvents.Count);
        using var reader = _fixture.CreateEventStore();
        Assert.Empty(await reader.ReadStreamAsync("Wrong-1"));
        using var snapshots = _fixture.CreateSnapshotStore();
        Assert.False(await snapshots.ByIdAsync<WrongVersionSnapshotEntity>("Wrong-1"));
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_MatchesFullReplay()
    {
        var entity = Renamed(1, 5);
        await _store.SaveAsync(StreamId, entity);
        await _snapshots.UpsertAsync(StreamId, entity);
        await _snapshots.SaveChangesAsync();
        await _store.SaveAsync(StreamId, Renamed(6, 8, entity));

        var fromSnapshot = (await _store.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();
        var fromEvents = (await _store.LoadAsync<SnapshotEntity>(StreamId)).Get();

        Assert.Equal(fromEvents.State, fromSnapshot.State);
        Assert.Equal(fromEvents.Version, fromSnapshot.Version);
    }

    [Fact]
    public void Rehydrate_AppliesEventsInOrderAndSetsNextVersion()
    {
        ImmutableArray<IEvent> stream = [new TestEvent(1, "a"), new TestEvent(2, "b")];

        var entity = new SnapshotEntity();
        entity.Rehydrate(stream);

        Assert.Equal("a,b", entity.State);
        Assert.Equal(3, entity.Version);
        Assert.Empty(entity.UncommittedEvents);
    }

    [Fact]
    public void AddEventSourcing_RegistersEventStore()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EventDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddDbContext<SnapshotDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddEventSourcing();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EventStore>());
    }
}
