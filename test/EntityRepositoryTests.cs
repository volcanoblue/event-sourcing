using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.EventStore;
using VolcanoBlue.EventSourcing.Extensions;
using VolcanoBlue.EventSourcing.EventStore.Serialization;

namespace VolcanoBlue.EventSourcing.Tests;

public class EntityRepositoryTests : IDisposable
{
    private const string StreamId = "Snap-1";

    private readonly SqliteFixture _fixture = new();
    private readonly EventDbContext _events;
    private readonly SnapshotDbContext _snapshots;
    private readonly EntityRepository _repository;

    public EntityRepositoryTests()
    {
        _events = _fixture.CreateEventStore();
        _snapshots = _fixture.CreateSnapshotStore();
        _repository = new EntityRepository(_events, _snapshots);
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
        var result = await _repository.LoadAsync<SnapshotEntity>("Missing-1");

        Assert.False(result);
    }

    [Fact]
    public async Task SaveAsync_PersistsUncommittedEventsAndCommitsThem()
    {
        var entity = Renamed(1, 2);

        await _repository.SaveAsync(StreamId, entity);

        Assert.Empty(entity.UncommittedEvents);
        using var reader = _fixture.CreateEventStore();
        var stored = await reader.ReadStreamAsync(StreamId);
        Assert.Equal(["e1", "e2"], stored.Cast<TestEvent>().Select(e => e.Name));
    }

    [Fact]
    public async Task SaveAsync_WithoutUncommittedEvents_DoesNothing()
    {
        await _repository.SaveAsync(StreamId, new SnapshotEntity());

        using var reader = _fixture.CreateEventStore();
        Assert.Empty(await reader.ReadStreamAsync(StreamId));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsStateAndVersion_AndAllowsFurtherChanges()
    {
        await _repository.SaveAsync(StreamId, Renamed(1, 3));

        var loaded = (await _repository.LoadAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(4, loaded.Version);

        loaded.Rename("e4");
        await _repository.SaveAsync(StreamId, loaded);

        var reloaded = (await _repository.LoadAsync<SnapshotEntity>(StreamId)).Get();
        Assert.Equal(Names(1, 4), reloaded.State);
        Assert.Equal(5, reloaded.Version);
    }

    [Fact]
    public async Task LoadAsync_SupportsNonPublicParameterlessConstructor()
    {
        var entity = PrivateCtorEntity.Create();
        entity.Rename("a");
        await _repository.SaveAsync("Private-1", entity);

        var loaded = (await _repository.LoadAsync<PrivateCtorEntity>("Private-1")).Get();

        Assert.Equal("a", loaded.State);
    }

    [Fact]
    public async Task LoadAsync_WithoutParameterlessConstructor_Throws()
    {
        var entity = new TestEntity("1");
        entity.Rename("a");
        await _repository.SaveAsync(entity.StreamId, entity);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.LoadAsync<TestEntity>(entity.StreamId));
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_NoSnapshot_FallsBackToFullReplay()
    {
        await _repository.SaveAsync(StreamId, Renamed(1, 3));

        var loaded = (await _repository.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();

        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(4, loaded.Version);
        Assert.Equal(3, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_NoSnapshotAndNoEvents_ReturnsNone()
    {
        var result = await _repository.LoadFromSnapshotAsync<SnapshotEntity>("Missing-1");

        Assert.False(result);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_RestoresSnapshotAndReplaysOnlyLaterEvents()
    {
        var entity = Renamed(1, 10);
        await _repository.SaveAsync(StreamId, entity);

        await _snapshots.UpsertAsync(StreamId, entity.MarkSnapshot());
        await _snapshots.SaveChangesAsync();

        Renamed(11, 20, entity);
        await _repository.SaveAsync(StreamId, entity);

        var loaded = (await _repository.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();

        Assert.Equal(Names(1, 20), loaded.State);
        Assert.Equal(21, loaded.Version);
        Assert.Equal(10, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadFromSnapshotAsync_SnapshotWithoutLaterEvents_SetsNextVersion()
    {
        var entity = Renamed(1, 5);
        await _repository.SaveAsync(StreamId, entity);
        await _snapshots.UpsertAsync(StreamId, entity.MarkSnapshot());
        await _snapshots.SaveChangesAsync();

        var loaded = (await _repository.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();

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
    public async Task LoadFromSnapshotAsync_MatchesFullReplay()
    {
        var entity = Renamed(1, 5);
        await _repository.SaveAsync(StreamId, entity);
        await _snapshots.UpsertAsync(StreamId, entity.MarkSnapshot());
        await _snapshots.SaveChangesAsync();
        await _repository.SaveAsync(StreamId, Renamed(6, 8, entity));

        var fromSnapshot = (await _repository.LoadFromSnapshotAsync<SnapshotEntity>(StreamId)).Get();
        var fromEvents = (await _repository.LoadAsync<SnapshotEntity>(StreamId)).Get();

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
    public void AddEventSourcing_RegistersEntityRepository()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EventDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddDbContext<SnapshotDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddEventSourcing();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EntityRepository>());
    }
}
