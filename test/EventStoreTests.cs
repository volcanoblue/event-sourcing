using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.Extensions;

namespace VolcanoBlue.EventSourcing.Tests;

public class EventStoreTests : IDisposable
{
    private const string StreamId = "Snap-1";

    private readonly DatabaseFixture _fixture = new();
    private readonly EventStoreDbContext _db;
    private readonly EventStore _store;

    public EventStoreTests()
    {
        _db = _fixture.CreateContext();
        _store = _fixture.CreateStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
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

    private async Task<IEvent[]> StoredEventsAsync(string streamId)
    {
        using var reader = _fixture.CreateContext();
        var rows = await reader.Events.AsNoTracking().Where(e => e.StreamId == streamId).OrderBy(e => e.Id).ToListAsync();
        return rows.Select(DatabaseFixture.Options.Runtime.Events.Deserialize).ToArray();
    }

    private async Task<List<Snapshot>> SnapshotRowsAsync(string entityId = StreamId)
    {
        using var reader = _fixture.CreateContext();
        return await reader.Snapshots.AsNoTracking().Where(s => s.EntityId == entityId).ToListAsync();
    }

    // Stores a snapshot of the entity as it is now, without appending events.
    private async Task SnapshotAsync(SnapshotEntity entity)
    {
        var row = DatabaseFixture.Options.Runtime.Snapshots.Serialize(StreamId, entity, "2026-01-01 00:00:00");
        await _db.StageSnapshotAsync(row, default);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task LoadAsync_UnknownStream_ReturnsNone()
    {
        Assert.Null(await _store.LoadAsync<SnapshotEntity>("Missing-1"));
        Assert.Null(await _store.LoadAsync<PrivateCtorEntity>("Missing-1"));
    }

    [Fact]
    public async Task SaveAsync_PersistsUncommittedEventsAndCommitsThem()
    {
        var entity = Renamed(1, 2);

        await _store.SaveAsync(StreamId, entity);

        Assert.Empty(entity.UncommittedEvents);
        var stored = await StoredEventsAsync(StreamId);
        Assert.Equal(["e1", "e2"], stored.Cast<TestEvent>().Select(e => e.Name));
    }

    [Fact]
    public async Task SaveAsync_WithoutUncommittedEvents_DoesNothing()
    {
        await _store.SaveAsync(StreamId, new SnapshotEntity());

        Assert.Empty(await StoredEventsAsync(StreamId));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsStateAndVersion_AndAllowsFurtherChanges()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(3, loaded.Version);

        loaded.Rename("e4");
        await _store.SaveAsync(StreamId, loaded);

        var reloaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        Assert.Equal(Names(1, 4), reloaded.State);
        Assert.Equal(4, reloaded.Version);
    }

    [Fact]
    public async Task LoadAsync_SupportsNonPublicParameterlessConstructor()
    {
        var entity = PrivateCtorEntity.Create();
        entity.Rename("a");
        await _store.SaveAsync("Private-1", entity);

        var loaded = (await _store.LoadAsync<PrivateCtorEntity>("Private-1"))!;

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
    public async Task LoadAsync_SnapshotEntityWithoutSnapshot_ReplaysTheWholeStream()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;

        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(3, loaded.Version);
        Assert.Equal(3, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadAsync_RestoresSnapshotAndReplaysOnlyLaterEvents()
    {
        var entity = Renamed(1, 10);
        await _store.SaveAsync(StreamId, entity);
        await SnapshotAsync(entity);

        Renamed(11, 20, entity);
        await _store.SaveAsync(StreamId, entity);

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;

        Assert.Equal(Names(1, 20), loaded.State);
        Assert.Equal(20, loaded.Version);
        Assert.Equal(10, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadAsync_SnapshotWithoutLaterEvents_KeepsSnapshotVersion()
    {
        var entity = Renamed(1, 5);
        await _store.SaveAsync(StreamId, entity);
        await SnapshotAsync(entity);

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;

        Assert.Equal(Names(1, 5), loaded.State);
        Assert.Equal(5, loaded.Version);
        Assert.Equal(0, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadAsync_UsesTheHighestSnapshotVersion()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 9));
        const string Insert = "INSERT INTO \"Snapshots\" (\"EntityId\", \"EntityVersion\", \"Created\", \"Data\") VALUES ({0}, {1}, '2026-01-01 00:00:00', {2})";
        await _db.Database.ExecuteSqlRawAsync(Insert, StreamId, 5, "{\"State\":\"old\"}");
        await _db.Database.ExecuteSqlRawAsync(Insert, StreamId, 9, "{\"State\":\"new\"}");

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;

        Assert.Equal("new", loaded.State);
        Assert.Equal(9, loaded.Version);
        Assert.Equal(0, loaded.ProcessedEvents);
    }

    [Fact]
    public async Task LoadAsync_WithoutISnapshot_NeverQueriesTheSnapshotTable()
    {
        var entity = PrivateCtorEntity.Create();
        entity.Rename("a");
        await _store.SaveAsync("Private-2", entity);
        await _db.Database.ExecuteSqlRawAsync("DROP TABLE \"Snapshots\"");

        var loaded = (await _store.LoadAsync<PrivateCtorEntity>("Private-2"))!;

        Assert.Equal("a", loaded.State);
        await Assert.ThrowsAnyAsync<Exception>(() => _store.LoadAsync<SnapshotEntity>(StreamId));
    }

    [Fact]
    public void EventRows_RoundTripPublicFields()
    {
        var events = DatabaseFixture.Options.Runtime.Events;

        var row = events.Serialize("Field-1", new FieldEvent { EventId = 1, Note = "kept" }, "2026-01-01 00:00:00");

        Assert.Equal("kept", ((FieldEvent)events.Deserialize(row)).Note);
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
    public async Task SaveAsync_TakesSnapshotWhenShouldTakeSnapshotIsTrue()
    {
        var entity = Renamed(1, 10);
        entity.AutoSnapshot = true;
        await _store.SaveAsync(StreamId, entity);

        Renamed(11, 12, entity);
        entity.AutoSnapshot = false;
        await _store.SaveAsync(StreamId, entity);

        var row = Assert.Single(await SnapshotRowsAsync());
        Assert.Equal(10, row.EntityVersion);

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        Assert.Equal(Names(1, 12), loaded.State);
        Assert.Equal(2, loaded.ProcessedEvents);
        Assert.Equal(12, loaded.Version);
    }

    [Fact]
    public async Task SaveAsync_DoesNotSnapshotWhenShouldTakeSnapshotIsFalse()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));

        Assert.Empty(await SnapshotRowsAsync());
    }

    [Fact]
    public async Task SaveAsync_KeepsOnlyTheLatestSnapshot()
    {
        var entity = Renamed(1, 3);
        entity.AutoSnapshot = true;
        await _store.SaveAsync(StreamId, entity);
        await _store.SaveAsync(StreamId, Renamed(4, 6, entity));

        Assert.Equal(6, Assert.Single(await SnapshotRowsAsync()).EntityVersion);
    }

    [Fact]
    public async Task SaveAsync_SnapshotVersionIsTheLastAppliedEvent()
    {
        var entity = Renamed(1, 4);
        entity.AutoSnapshot = true;
        Assert.Equal(4, entity.Version);

        await _store.SaveAsync(StreamId, entity);

        Assert.Equal(4, Assert.Single(await SnapshotRowsAsync()).EntityVersion);
    }

    [Fact]
    public async Task LoadAsync_FromSnapshot_ThenRaiseAndSave_ContinuesTheSequenceWithoutConflicts()
    {
        var entity = Renamed(1, 5);
        entity.AutoSnapshot = true;
        await _store.SaveAsync(StreamId, entity);

        // Snapshot at 5, no tail: the next event must be 6.
        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        Assert.Equal(5, loaded.Version);
        loaded.Rename("e6");
        Assert.Equal(6, loaded.UncommittedEvents.Single().EventId);
        await _store.SaveAsync(StreamId, loaded);

        // AutoSnapshot isn't serialized, so the snapshot stays at 5 and the tail grows to e6..e8.
        Renamed(7, 8, loaded);
        await _store.SaveAsync(StreamId, loaded);

        var reloaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        Assert.Equal(Names(1, 8), reloaded.State);
        Assert.Equal(8, reloaded.Version);
        Assert.Equal(3, reloaded.ProcessedEvents);

        reloaded.Rename("e9");
        Assert.Equal(9, reloaded.UncommittedEvents.Single().EventId);
        await _store.SaveAsync(StreamId, reloaded);

        Assert.Equal(9, (await StoredEventsAsync(StreamId)).Length);
    }

    [Fact]
    public async Task LoadAsync_RestoresEncapsulatedEntity_WithoutPublicConstructorOrSetters()
    {
        var entity = EncapsulatedEntity.Create();
        entity.Rename("a");
        entity.Rename("b");
        await _store.SaveAsync(StreamId, entity);

        var loaded = (await _store.LoadAsync<EncapsulatedEntity>(StreamId))!;

        Assert.Equal(["a", "b"], loaded.Names);
        Assert.Equal(2, loaded.Renames);
        Assert.Equal("b", loaded.Last);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(7, loaded.Skipped); // [JsonIgnore] state is not persisted

        loaded.Rename("c");
        await _store.SaveAsync(StreamId, loaded);

        var again = (await _store.LoadAsync<EncapsulatedEntity>(StreamId))!;
        Assert.Equal(["a", "b", "c"], again.Names);
        Assert.Equal(3, again.Version);
    }

    [Fact]
    public async Task SaveAsync_AfterAConcurrencyConflict_DoesNotResendTheRejectedEvents()
    {
        var first = new SnapshotEntity();
        first.Rename("a");
        await _store.SaveAsync(StreamId, first);

        var stale = new SnapshotEntity();
        stale.Rename("conflict");
        await Assert.ThrowsAsync<ConcurrencyException>(() => _store.SaveAsync(StreamId, stale));

        var current = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        current.Rename("b");
        await _store.SaveAsync(StreamId, current);

        Assert.Equal(2, (await StoredEventsAsync(StreamId)).Length);
    }

    [Fact]
    public async Task SaveAsync_WithTheSameVersionAsTheStoredSnapshot_ReplacesIt()
    {
        await SnapshotAsync(Renamed(1, 3));

        var sameVersion = new SnapshotEntity();
        for (var i = 0; i < 3; i++)
            sameVersion.Rename("x");
        await SnapshotAsync(sameVersion);

        Assert.Single(await SnapshotRowsAsync());
        Assert.Equal("x,x,x", (await _store.LoadAsync<SnapshotEntity>(StreamId))!.State);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    public async Task SaveAsync_WithNonConsecutiveEventIds_ThrowsAndSavesNothing(long eventId)
    {
        var entity = new EventIdEntity();
        entity.RaiseWith(1);
        entity.RaiseWith(eventId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(StreamId, entity));

        Assert.Contains("expected", ex.Message);
        Assert.Empty(await StoredEventsAsync(StreamId));
        Assert.Equal(2, entity.UncommittedEvents.Count);
    }

    [Fact]
    public async Task SaveAsync_WhenTheStreamMovedOn_ThrowsConcurrencyExceptionWithVersions()
    {
        var first = new SnapshotEntity();
        first.Rename("a");
        await _store.SaveAsync(StreamId, first);

        var stale = new SnapshotEntity();
        stale.Rename("conflict");
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => _store.SaveAsync(StreamId, stale));

        Assert.Equal(StreamId, ex.StreamId);
        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
        Assert.IsType<DbUpdateException>(ex.InnerException);
        Assert.Equal(1, await _db.CurrentVersionAsync(StreamId, default));
        Assert.Equal(0, await _db.CurrentVersionAsync("Nobody", default));
    }

    [Fact]
    public async Task SaveAsync_WhenTheConflictedWriterAlsoSnapshots_NeitherEventsNorSnapshotAreWritten()
    {
        var first = new SnapshotEntity { AutoSnapshot = true };
        first.Rename("a");
        await _store.SaveAsync(StreamId, first);

        var stale = new SnapshotEntity { AutoSnapshot = true };
        stale.Rename("conflict");
        await Assert.ThrowsAsync<ConcurrencyException>(() => _store.SaveAsync(StreamId, stale));

        // Same snapshot version (1): had the snapshot been written on its own, it would have replaced "a".
        Assert.Equal("a", (await _store.LoadAsync<SnapshotEntity>(StreamId))!.State);
        Assert.Single(await StoredEventsAsync(StreamId));
    }

    [Fact]
    public async Task SaveAsync_WhenTheSnapshotCannotBeSerialized_SavesNothing()
    {
        var entity = new UnserializableSnapshotEntity();
        entity.Rename("a");

        await Assert.ThrowsAnyAsync<Exception>(() => _store.SaveAsync("Bad-1", entity));

        Assert.Empty(await StoredEventsAsync("Bad-1"));
        Assert.Single(entity.UncommittedEvents);
    }

    [Fact]
    public async Task SaveAsync_WithAnUnregisteredEventType_ThrowsAndSavesNothing()
    {
        var options = new EventSourcingOptions().AddEvent<NamedEvent>();
        using var db = _fixture.CreateContext();
        var store = _fixture.CreateStore(db, options);
        var entity = new SnapshotEntity();
        entity.Rename("a");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(StreamId, entity));

        Assert.Contains(nameof(TestEvent), ex.Message);
        Assert.Empty(await StoredEventsAsync(StreamId));
    }

    [Fact]
    public async Task LoadAsync_WithAnUnreadableSnapshot_FallsBackToFullReplay()
    {
        await _store.SaveAsync(StreamId, Renamed(1, 3));
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Snapshots\" (\"EntityId\", \"EntityVersion\", \"Created\", \"Data\") VALUES ({0}, 3, '2026-01-01 00:00:00', {1})",
            StreamId, "not json");

        var loaded = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;

        Assert.Equal(Names(1, 3), loaded.State);
        Assert.Equal(3, loaded.Version);
        Assert.Equal(3, loaded.ProcessedEvents);
    }

    [Fact]
    public void EventName_IsStoredInsteadOfTheFullName_AndFullNameRowsStillRead()
    {
        var events = DatabaseFixture.Options.Runtime.Events;

        var row = events.Serialize("Named-1", new NamedEvent(1, "x"), "2026-01-01 00:00:00");
        Assert.Equal("tests.named-event", row.EventType);

        var legacy = row with { EventType = typeof(NamedEvent).FullName! };
        Assert.Equal("x", ((NamedEvent)events.Deserialize(legacy)).Name);
    }

    [Fact]
    public async Task SaveAsync_SnapshotWithWrongEntityVersion_ThrowsBeforeSavingAnything()
    {
        var entity = new WrongVersionSnapshotEntity();
        entity.Rename("a");
        entity.Rename("b");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync("Wrong-1", entity));

        Assert.Equal(2, entity.UncommittedEvents.Count);
        Assert.Empty(await StoredEventsAsync("Wrong-1"));
        Assert.Empty(await SnapshotRowsAsync("Wrong-1"));
    }

    [Fact]
    public async Task LoadAsync_FromSnapshotPlusTail_MatchesAFullReplay()
    {
        var entity = Renamed(1, 5);
        await _store.SaveAsync(StreamId, entity);
        await SnapshotAsync(entity);
        await _store.SaveAsync(StreamId, Renamed(6, 8, entity));

        var fromSnapshot = (await _store.LoadAsync<SnapshotEntity>(StreamId))!;
        var fromEvents = new SnapshotEntity();
        fromEvents.Rehydrate([.. await StoredEventsAsync(StreamId)]);

        Assert.Equal(3, fromSnapshot.ProcessedEvents);
        Assert.Equal(fromEvents.State, fromSnapshot.State);
        Assert.Equal(fromEvents.Version, fromSnapshot.Version);
    }

    [Fact]
    public void Rehydrate_AppliesEventsInOrderAndKeepsSnapshotVersion()
    {
        ImmutableArray<IEvent> stream = [new TestEvent(1, "a"), new TestEvent(2, "b")];

        var entity = new SnapshotEntity();
        entity.Rehydrate(stream);

        Assert.Equal("a,b", entity.State);
        Assert.Equal(2, entity.Version);
        Assert.Empty(entity.UncommittedEvents);
    }

    [Fact]
    public async Task SaveAsync_StampsEventsAndSnapshotWithSubSecondUtcTime()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234567));
        var options = new EventSourcingOptions { TimeProvider = clock }.AddEvent<TestEvent>();
        using var db = _fixture.CreateContext();
        var entity = Renamed(1, 2);
        entity.AutoSnapshot = true;

        await _fixture.CreateStore(db, options).SaveAsync(StreamId, entity);

        using var reader = _fixture.CreateContext();
        Assert.All(await reader.Events.AsNoTracking().ToListAsync(), e => Assert.Equal("2026-03-04 05:06:07.1234567", e.OccurredAt));
        Assert.Equal("2026-03-04 05:06:07.1234567", (await reader.Snapshots.AsNoTracking().SingleAsync()).Created);
    }

    [Fact]
    public void Options_AddEventTwice_IsIdempotent_AndRejectsNonEvents()
    {
        var options = new EventSourcingOptions().AddEvent<TestEvent>().AddEvent<TestEvent>();

        Assert.Equal(nameof(TestEvent), options.Runtime.Events.Serialize("s", new TestEvent(1, "a"), "t").EventType.Split('.').Last());
        Assert.Throws<ArgumentException>(() => new EventSourcingOptions().AddEvent(typeof(string)));
    }

    [Fact]
    public void Options_TwoEventsWithTheSameName_Throw()
    {
        var options = new EventSourcingOptions().AddEvent<DupEvent<int>>().AddEvent<DupEvent<string>>();

        var ex = Assert.Throws<InvalidOperationException>(() => options.Runtime);

        Assert.Contains("tests.dup", ex.Message);
    }

    [Fact]
    public void Options_AfterAnEventStoreUsedThem_AreFrozen()
    {
        var options = new EventSourcingOptions().AddEvent<TestEvent>();
        _ = _fixture.CreateStore(_db, options);

        Assert.Throws<InvalidOperationException>(() => options.AddEvent<NamedEvent>());
        Assert.Throws<InvalidOperationException>(() => options.TimeProvider = TimeProvider.System);
    }

    [Fact]
    public void Options_ScanningSkipsOpenGenericEvents()
    {
        var options = new EventSourcingOptions().AddEventsFrom(typeof(DatabaseFixture).Assembly);

        Assert.Equal("tests.named-event", options.Runtime.Events.Serialize("s", new NamedEvent(1, "a"), "t").EventType);
    }

    [Fact]
    public void AddEventSourcing_RegistersEventStoreAndOptions()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EventStoreDbContext>(o => o.UseNpgsql("Host=localhost"));
        services.AddEventSourcing(o => o.AddEvent<TestEvent>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EventStore>());
        Assert.Same(provider.GetRequiredService<EventSourcingOptions>(), provider.GetRequiredService<EventSourcingOptions>());
    }

    [Fact]
    public void AddEventSourcing_WithAssemblies_ScansForEvents()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EventStoreDbContext>(o => o.UseNpgsql("Host=localhost"));
        services.AddEventSourcing(typeof(DatabaseFixture).Assembly);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var options = provider.GetRequiredService<EventSourcingOptions>();
        Assert.Equal("tests.named-event", options.Runtime.Events.Serialize("s", new NamedEvent(1, "a"), "t").EventType);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EventStore>());
    }
}
