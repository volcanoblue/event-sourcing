using System.Collections.Immutable;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

public class TestEntityTests : IDisposable
{
    private readonly DatabaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<TestEntity> LoadAsync(string id)
    {
        using var db = _fixture.CreateContext();
        var stream = ImmutableArray.CreateBuilder<IEvent>();
        await foreach (var row in db.ReadStream($"Test-{id}", 0))
            stream.Add(DatabaseFixture.Options.Runtime.Events.Deserialize(row));

        return TestEntity.Rehydrate(id, stream.ToImmutable());
    }

    private async Task SaveAsync(TestEntity entity)
    {
        using var db = _fixture.CreateContext();
        await _fixture.CreateStore(db).SaveAsync(entity.StreamId, entity);
    }

    [Fact]
    public void Rename_RaisesEventsWithSequentialIds()
    {
        var entity = new TestEntity("1");

        entity.Rename("a");
        entity.Rename("b");

        Assert.Equal([1L, 2L], entity.UncommittedEvents.Select(e => e.EventId));
        Assert.Equal(2, entity.Version);
        Assert.Equal("b", entity.CurrentName);
    }

    [Fact]
    public void Rename_SameName_RaisesNothing()
    {
        var entity = new TestEntity("1");
        entity.Rename("a");

        entity.Rename("a");

        Assert.Single(entity.UncommittedEvents);
    }

    [Fact]
    public async Task SaveAndLoad_RebuildsStateAndVersion()
    {
        var entity = new TestEntity("1");
        entity.Rename("first");
        entity.Rename("second");
        await SaveAsync(entity);

        var loaded = await LoadAsync("1");

        Assert.Equal("second", loaded.CurrentName);
        Assert.Equal(["first", "second"], loaded.History);
        Assert.Equal(entity.Version, loaded.Version);
        Assert.Empty(loaded.UncommittedEvents);
    }

    [Fact]
    public async Task Load_UnknownStream_ReturnsFreshEntity()
    {
        var loaded = await LoadAsync("missing");

        Assert.Null(loaded.CurrentName);
        Assert.Equal(0, loaded.Version);
    }

    [Fact]
    public async Task LoadModifySave_AppendsOnlyNewEvents()
    {
        var entity = new TestEntity("1");
        entity.Rename("a");
        await SaveAsync(entity);

        var loaded = await LoadAsync("1");
        loaded.Rename("b");
        Assert.Equal([2L], loaded.UncommittedEvents.Select(e => e.EventId));
        await SaveAsync(loaded);

        var final = await LoadAsync("1");
        Assert.Equal(["a", "b"], final.History);
        Assert.Equal(2, final.Version);
    }

    [Fact]
    public async Task ConcurrentWriters_SecondSaveFails()
    {
        var seed = new TestEntity("1");
        seed.Rename("a");
        await SaveAsync(seed);

        var first = await LoadAsync("1");
        var second = await LoadAsync("1");
        first.Rename("from-first");
        second.Rename("from-second");
        await SaveAsync(first);

        await Assert.ThrowsAsync<ConcurrencyException>(() => SaveAsync(second));
        Assert.Equal(["a", "from-first"], (await LoadAsync("1")).History);
    }
}
